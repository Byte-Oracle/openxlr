namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    private bool _desktopMuteConsumed;
    private bool? _desktopMonitorIntent;
    private readonly Dictionary<string, (string Identity, bool Muted)> _outputMutes = new(StringComparer.Ordinal);
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

    /// <summary>External output mute changes control OpenXLR listening, never its recording mixes.</summary>
    private bool ReadOutputMonitorMuteLocked()
    {
        bool changed = ApplyDesktopMonitorIntentLocked();
        var selected = _monitorOutputs.Select(StripMarker)
            .Where(n => !n.StartsWith("OpenXLR_", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        foreach (string removed in _outputMutes.Keys.Where(n => !selected.Contains(n)).ToArray())
        {
            _outputMutes.Remove(removed);
            _desktopMuteResets.Remove(removed);
            _desktopMuteRetries.Remove(removed);
        }
        var nodes = _pw.ObservedSinkMutes(selected);
        // Reconnection restores the original flag; it is not a native key.
        foreach ((string name, (string identity, bool muted)) in _outputMutes.ToArray())
        {
            if (!nodes.TryGetValue(name, out var node)) _desktopMuteResets.TryAdd(name, (muted, 0, 0));
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
