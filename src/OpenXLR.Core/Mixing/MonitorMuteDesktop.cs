namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    private bool _desktopMuteConsumed;
    private bool? _desktopMonitorIntent;
    private readonly Dictionary<string, (string? Identity, bool Muted)> _outputMutes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (bool Muted, int Failures, long RetryAt)> _desktopMuteResets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Failures, long RetryAt)> _desktopMuteRetries = new(StringComparer.Ordinal);

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

    /// <summary>Application sinks stay open to recording; the default sink's native mute is monitoring intent.</summary>
    private bool ReadDesktopMonitorMuteLocked(ref IReadOnlyList<OwnSinkLevel> levels)
    {
        _desktopMuteConsumed = false;
        var applications = _config.Channels.Where(c => c.IsApplication).Select(c => c.SinkName).ToHashSet(StringComparer.Ordinal);
        // Reading the default costs a helper call, so only a muted or retried application sink pays it.
        string? defaultSink = levels.Any(s => applications.Contains(s.Name) && (s.Muted || _desktopMuteRetries.ContainsKey(s.Name)))
            ? _pw.GetDefaultSink() : null;
        var present = levels.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (string missing in _desktopMuteRetries.Keys.Where(n => n.StartsWith("OpenXLR_", StringComparison.Ordinal)
                     && (!present.Contains(n) || n != defaultSink)).ToArray()) _desktopMuteRetries.Remove(missing);
        bool pulse = false;
        bool skipRepair = false;
        foreach (OwnSinkLevel sink in levels.Where(s => s.Name == defaultSink && applications.Contains(s.Name)
                     && (s.Muted || _desktopMuteRetries.ContainsKey(s.Name))))
        {
            try
            {
                if (!_pw.GetSinkMuted(sink.Name)) { _desktopMuteRetries.Remove(sink.Name); skipRepair = true; continue; }
                pulse |= ConsumeDesktopMuteLocked(sink.Name);
                skipRepair = pulse;
            }
            catch (InvalidOperationException) { /* Retry a source that disappeared during its read. */ }
        }
        bool changed = pulse && ToggleDesktopMonitorIntentLocked();
        _desktopMuteConsumed |= pulse;
        if (skipRepair)
            levels = [.. levels.Select(s =>
            {
                // Do not overwrite another key arriving after the consume.
                if (s.Name == defaultSink) return s with { Muted = false };
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

    /// <summary>Remember new outputs before a key can arrive after selection returns.</summary>
    private void CaptureMonitorMuteBaselinesLocked()
    {
        HashSet<string> selected = PhysicalMonitorOutputsLocked();
        ForgetUnselectedDesktopOutputsLocked(selected);
        if (selected.All(_outputMutes.ContainsKey)) return;
        IReadOnlyDictionary<string, (string Identity, bool Muted)>? nodes = null;
        try { nodes = _pw.ObservedSinkMutes(selected); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { /* Native commands may already reach the selected sink. */ }
        foreach (string name in selected)
        {
            if (_outputMutes.ContainsKey(name)) continue;
            string? identity = nodes is not null && nodes.TryGetValue(name, out var node) ? node.Identity : null;
            try { _outputMutes[name] = (identity, _pw.GetSinkMuted(name)); }
            catch (InvalidOperationException) { /* An absent output is discovered by the next sweep. */ }
        }
    }

    /// <summary>External output mute changes control OpenXLR listening, never its recording mixes.</summary>
    private bool ReadOutputMonitorMuteLocked()
    {
        bool changed = ApplyDesktopMonitorIntentLocked();
        HashSet<string> selected = PhysicalMonitorOutputsLocked();
        ForgetUnselectedDesktopOutputsLocked(selected);
        IReadOnlyDictionary<string, (string Identity, bool Muted)> nodes;
        try { nodes = _pw.ObservedSinkMutes(selected); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { return changed; }
        // Reconnection restores the original flag; it is not a native key.
        foreach ((string name, (string? identity, bool muted)) in _outputMutes.ToArray())
        {
            if (!nodes.TryGetValue(name, out var node))
            {
                if (identity is not null) _desktopMuteResets.TryAdd(name, (muted, 0, 0));
            }
            else if (identity is null) _outputMutes[name] = (node.Identity, muted);
            else if (identity != node.Identity)
            {
                _outputMutes[name] = (node.Identity, muted);
                _desktopMuteResets[name] = (muted, 0, 0);
                _desktopMuteRetries.Remove(name);
            }
        }
        var observed = nodes.ToDictionary(e => e.Key, e => e.Value.Muted, StringComparer.Ordinal);
        foreach (string name in observed.Keys.ToArray())
        {
            bool known = _outputMutes.TryGetValue(name, out var previous);
            if (!known || previous.Muted != observed[name] || _desktopMuteConsumed || _desktopMuteRetries.ContainsKey(name))
            {
                try { observed[name] = _pw.GetSinkMuted(name); }
                catch (InvalidOperationException) { observed.Remove(name); continue; }
            }
            if (!known) _outputMutes[name] = (nodes[name].Identity, observed[name]);
        }
        bool pulse = false;
        foreach ((string name, bool muted) in observed)
        {
            if (_desktopMuteResets.ContainsKey(name)) continue;
            if (_outputMutes[name].Muted == muted) { _desktopMuteRetries.Remove(name); continue; }
            if (!muted)
            {
                _outputMutes[name] = (nodes[name].Identity, false);
                _desktopMuteRetries.Remove(name);
                continue;
            }
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
    }
}
