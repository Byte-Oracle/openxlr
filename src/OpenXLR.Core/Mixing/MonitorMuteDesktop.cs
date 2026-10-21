using System.Text.Json;

namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    private bool _desktopMuteConsumed;
    private bool? _desktopMonitorIntent;
    private sealed record DesktopOutputMute(string? Identity, string? RegistryIdentity, bool Muted);
    private readonly Dictionary<string, DesktopOutputMute> _outputMutes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (bool Muted, int Failures, long RetryAt)> _desktopMuteResets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Failures, long RetryAt)> _desktopMuteRetries = new(StringComparer.Ordinal);
    private (int Failures, long RetryAt) _desktopIdentityRetry;

    /// <summary>
    /// Consume a native key with an atomic toggle. Unlike setting a fixed
    /// flag, this leaves a second press during acknowledgement pending.
    /// A refused consume changes no monitor intent; two unconsumed presses
    /// cancel each other even while a helper is unavailable.
    /// </summary>
    private bool ConsumeDesktopMuteLocked(string name)
    {
        (int failures, long retryAt) = _desktopMuteRetries.GetValueOrDefault(name);
        if (Clock() < retryAt) return false;
        try
        {
            _pw.ToggleSinkMuted(name);
            _desktopMuteRetries.Remove(name);
            return true;
        }
        catch (InvalidOperationException)
        {
            int count = Math.Min(failures + 1, 8);
            _desktopMuteRetries[name] = (count, Clock() + DesktopMuteRetryDelay(count));
            return false;
        }
    }

    private static long DesktopMuteRetryDelay(int count)
        => count < 3 ? 0 : Math.Min(30_000, 1_000L << (count - 3));

    private bool ToggleDesktopMonitorIntentLocked()
    {
        _desktopMonitorIntent = !(_desktopMonitorIntent ?? _monitorMuted);
        return ApplyDesktopMonitorIntentLocked();
    }

    private bool ApplyDesktopMonitorIntentLocked()
    {
        if (_desktopMonitorIntent is not bool wanted) return false;
        bool previous = _monitorMuted;
        try { SetMonitorMuted(wanted); }
        catch (InvalidOperationException) { _desktopMonitorIntent = wanted; }
        return previous != _monitorMuted;
    }

    /// <summary>Application sinks stay open to recording; their native mute is monitoring intent.</summary>
    private bool ReadDesktopMonitorMuteLocked(ref IReadOnlyList<OwnSinkLevel> levels)
    {
        _desktopMuteConsumed = false;
        var applications = _config.Channels.Where(c => c.IsApplication).Select(c => c.SinkName).ToHashSet(StringComparer.Ordinal);
        var present = levels.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (string missing in _desktopMuteRetries.Keys.Where(n => n.StartsWith("OpenXLR_", StringComparison.Ordinal)
                     && !present.Contains(n)).ToArray()) _desktopMuteRetries.Remove(missing);
        bool pulse = false;
        foreach (OwnSinkLevel sink in levels.Where(s => applications.Contains(s.Name) && (s.Muted || _desktopMuteRetries.ContainsKey(s.Name))))
        {
            try
            {
                if (!_pw.GetSinkMuted(sink.Name)) { _desktopMuteRetries.Remove(sink.Name); continue; }
                pulse |= ConsumeDesktopMuteLocked(sink.Name);
            }
            catch (InvalidOperationException) { /* Retry a source that disappeared during its read. */ }
        }
        bool changed = pulse && ToggleDesktopMonitorIntentLocked();
        _desktopMuteConsumed |= pulse;
        if (pulse || levels.Any(s => s.Muted && applications.Contains(s.Name)))
            levels = [.. levels.Select(s =>
            {
                // Do not overwrite another key arriving after the consume.
                if (applications.Contains(s.Name)) return s with { Muted = false };
                MixDefinition? mix = pulse ? MonitorMixForSinkLocked(s.Name) : null;
                if (mix is null) return s;
                bool wanted = _mixMuted.Contains(mix.Id);
                if (s.Muted != wanted)
                {
                    try { _pw.SetSinkMuted(s.Name, wanted); }
                    catch (InvalidOperationException) { /* Retry when the sink returns. */ }
                }
                return s with { Muted = wanted };
            })];
        return changed;
    }

    private MixDefinition? MonitorMixForSinkLocked(string name)
        => _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.Monitor && m.SinkName == name);

    private HashSet<string> PhysicalMonitorOutputsLocked()
        => _monitorOutputs.Select(StripMarker)
            .Where(n => !n.StartsWith("OpenXLR_", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);

    private void ForgetUnselectedDesktopOutputsLocked(IReadOnlySet<string> selected)
    {
        foreach (string removed in _outputMutes.Keys.Where(n => !selected.Contains(n)).ToArray())
        {
            _outputMutes.Remove(removed);
            _desktopMuteResets.Remove(removed);
            _desktopMuteRetries.Remove(removed);
        }
    }

    private IReadOnlyDictionary<string, (string Identity, bool Muted)>? ReadNativeMuteStatesLocked(IReadOnlySet<string> names, bool immediate = false)
    {
        if (!immediate && Clock() < _desktopIdentityRetry.RetryAt) return null;
        try
        {
            var states = _pw.NativeSinkMutes(names);
            _desktopIdentityRetry = default;
            return states;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException)
        {
            int failures = Math.Min(_desktopIdentityRetry.Failures + 1, 8);
            _desktopIdentityRetry = (failures, Clock() + DesktopMuteRetryDelay(failures));
            return null;
        }
    }

    /// <summary>Remember new outputs before a key can arrive after selection returns.</summary>
    private void CaptureMonitorMuteBaselinesLocked()
    {
        HashSet<string> selected = PhysicalMonitorOutputsLocked();
        ForgetUnselectedDesktopOutputsLocked(selected);
        HashSet<string> added = selected.Where(n => !_outputMutes.ContainsKey(n)).ToHashSet(StringComparer.Ordinal);
        if (added.Count == 0) return;
        IReadOnlyDictionary<string, (string Identity, bool Muted)>? nodes = null;
        try { nodes = _pw.ObservedSinkMutes(selected); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { /* Native commands may already reach the selected sink. */ }
        var native = ReadNativeMuteStatesLocked(added, immediate: true);
        foreach (string name in added)
        {
            if (native is not null && native.TryGetValue(name, out var state))
            {
                _outputMutes[name] = new(state.Identity, nodes is not null && nodes.TryGetValue(name, out var node) ? node.Identity : null, state.Muted);
            }
            else
            {
                try { _outputMutes[name] = new(null, null, _pw.GetSinkMuted(name)); }
                catch (InvalidOperationException) { /* An absent output is discovered by the next sweep. */ }
            }
        }
    }

    /// <summary>External output mute changes control OpenXLR listening, never its recording mixes.</summary>
    private bool ReadOutputMonitorMuteLocked()
    {
        bool changed = ApplyDesktopMonitorIntentLocked();
        HashSet<string> selected = PhysicalMonitorOutputsLocked();
        ForgetUnselectedDesktopOutputsLocked(selected);
        var nodes = _pw.ObservedSinkMutes(selected);
        // Native and registry connections can describe different generations
        // of the same name. Verify only a changed or first registry identity,
        // in one batch, against the native identity sampled with its mute.
        var verify = selected.Where(name =>
        {
            bool present = nodes.TryGetValue(name, out var node);
            return _outputMutes.TryGetValue(name, out var previous)
                ? present && (previous.Identity is null || previous.RegistryIdentity != node.Identity)
                    || !present && previous.RegistryIdentity is not null
                : present;
        }).ToHashSet(StringComparer.Ordinal);
        var native = verify.Count > 0 ? ReadNativeMuteStatesLocked(verify) : null;
        var observed = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (string name in selected)
        {
            if (verify.Contains(name))
            {
                if (native is null) continue;
                if (!native.TryGetValue(name, out var state))
                {
                    if (_outputMutes.TryGetValue(name, out var missing))
                    {
                        if (missing.Identity is not null) _desktopMuteResets.TryAdd(name, (missing.Muted, 0, 0));
                        _outputMutes[name] = missing with { RegistryIdentity = nodes.TryGetValue(name, out var absentRegistry) ? absentRegistry.Identity : null };
                    }
                    continue;
                }
                bool known = _outputMutes.TryGetValue(name, out var previous);
                if (known && previous!.Identity is not null && previous.Identity != state.Identity)
                {
                    _desktopMuteResets[name] = (previous.Muted, 0, 0);
                    _desktopMuteRetries.Remove(name);
                }
                _outputMutes[name] = new(state.Identity, nodes.TryGetValue(name, out var registry) ? registry.Identity : null,
                    known ? previous!.Muted : state.Muted);
                observed[name] = state.Muted;
                continue;
            }
            if (nodes.TryGetValue(name, out var node)) observed[name] = node.Muted;
        }
        foreach (string name in observed.Keys.ToArray())
        {
            var previous = _outputMutes[name];
            if (!verify.Contains(name) && (previous.Muted != observed[name] || _desktopMuteConsumed || _desktopMuteRetries.ContainsKey(name)))
            {
                try { observed[name] = _pw.GetSinkMuted(name); }
                catch (InvalidOperationException) { observed.Remove(name); }
            }
        }
        bool pulse = false;
        foreach ((string name, bool muted) in observed)
        {
            if (_desktopMuteResets.ContainsKey(name)) continue;
            if (_outputMutes[name].Muted == muted) { _desktopMuteRetries.Remove(name); continue; }
            pulse |= ConsumeDesktopMuteLocked(name);
        }
        if (pulse && !_desktopMuteConsumed) changed |= ToggleDesktopMonitorIntentLocked();
        foreach ((string name, (bool muted, int failures, long retryAt)) in _desktopMuteResets.ToArray())
        {
            if (!observed.TryGetValue(name, out bool current))
            {
                _desktopMuteResets[name] = (muted, 0, 0);
                continue;
            }
            if (current == muted)
            {
                try { current = _pw.GetSinkMuted(name); }
                catch (InvalidOperationException) { continue; }
                if (current == muted) { _desktopMuteResets.Remove(name); continue; }
            }
            if (Clock() < retryAt) continue;
            try
            {
                _pw.SetSinkMuted(name, muted);
                _desktopMuteResets.Remove(name);
            }
            catch (InvalidOperationException)
            {
                int count = Math.Min(failures + 1, 8);
                _desktopMuteResets[name] = (muted, count, Clock() + DesktopMuteRetryDelay(count));
            }
        }
        return changed;
    }

    private void ResetDesktopMonitorMuteLocked()
    {
        _desktopMuteConsumed = false;
        _desktopMonitorIntent = null;
        _outputMutes.Clear();
        _desktopMuteResets.Clear();
        _desktopMuteRetries.Clear();
        _desktopIdentityRetry = default;
    }
}
