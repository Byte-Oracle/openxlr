namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    private readonly HashSet<string> _desktopMutePulses = new(StringComparer.Ordinal);
    private bool _desktopMuteConsumed;
    private readonly Dictionary<string, (string Identity, bool Muted)> _outputMutes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (bool Muted, int Failures, long RetryAt)> _desktopMuteResets = new(StringComparer.Ordinal);

    /// <summary>
    /// An application sink stays open to Stream and Chat. Its desktop mute
    /// is a monitor-key pulse instead, consumed once before repairing it.
    /// A desktop that mutes several sinks at once must not replace the
    /// individual monitor mix mutes with that temporary blanket setting.
    /// </summary>
    private bool ReadDesktopMonitorMuteLocked(ref IReadOnlyList<OwnSinkLevel> levels)
    {
        var muted = levels.Where(s => s.Muted && _config.Channels.Any(c =>
            c.InputPair is null && c.CaptureSource is null && c.SinkName == s.Name))
            .Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (string name in muted.Where(n => !_desktopMutePulses.Contains(n)).ToArray())
        {
            try { if (!_pw.GetSinkMuted(name)) muted.Remove(name); }
            catch (InvalidOperationException) { muted.Remove(name); }
        }
        _desktopMutePulses.IntersectWith(muted);
        bool pulse = muted.Any(s => !_desktopMutePulses.Contains(s));
        if (pulse)
        {
            _desktopMutePulses.UnionWith(muted);
            SetMonitorMuted(!MonitorMuted);
            _desktopMuteConsumed = true;
            levels = [.. levels.Select(s =>
            {
                MixDefinition? mix = MonitorMixForSinkLocked(s.Name);
                if (mix is null) return s;
                bool wanted = _mixMuted.Contains(mix.Id);
                if (s.Muted != wanted)
                {
                    try { _pw.SetSinkMuted(s.Name, wanted); }
                    catch (InvalidOperationException) { /* Retry when the sink returns. */ }
                }
                return s with { Muted = wanted };
            })];
        }
        return pulse;
    }

    private MixDefinition? MonitorMixForSinkLocked(string name)
        => _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.Monitor && m.SinkName == name);

    /// <summary>External output mute changes control listening, never the mixes being recorded.</summary>
    private bool ReadOutputMonitorMuteLocked()
    {
        var selected = _monitorOutputs.Select(StripMarker)
            .Where(n => !n.StartsWith("OpenXLR_", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        foreach (string removed in _outputMutes.Keys.Where(n => !selected.Contains(n)).ToList()) _outputMutes.Remove(removed);
        foreach (string removed in _desktopMuteResets.Keys.Where(n => !selected.Contains(n)).ToList()) _desktopMuteResets.Remove(removed);
        var nodes = _pw.ObservedSinkMutes(selected);
        // Reconnection is not a key press. Restore the last known native
        // flag even if a same-name node replaced it between two sweeps.
        foreach ((string name, (string identity, bool muted)) in _outputMutes.ToArray())
        {
            if (!nodes.TryGetValue(name, out var node)) _desktopMuteResets.TryAdd(name, (muted, 0, 0));
            else if (identity != node.Identity)
            {
                _outputMutes[name] = (node.Identity, muted);
                _desktopMuteResets[name] = (muted, 0, 0);
            }
        }
        var observed = nodes.ToDictionary(e => e.Key, e => e.Value.Muted, StringComparer.Ordinal);
        // The first registry observation may still precede an earlier
        // native mute write. Establish each baseline from the actual sink.
        foreach (string name in observed.Keys.Where(n => !_outputMutes.ContainsKey(n)).ToArray())
        {
            try { observed[name] = _pw.GetSinkMuted(name); }
            catch (InvalidOperationException) { observed.Remove(name); }
        }
        // A successful native write can precede its registry echo. Confirm a
        // candidate pulse directly, rather than treating that echo as a key.
        // An internal pulse also needs every selected output confirmed: its
        // native flag may have changed before that same event's echo arrives.
        foreach (string name in observed.Where(e => _outputMutes.TryGetValue(e.Key, out var previous)
                     && (previous.Muted != e.Value || _desktopMuteConsumed)
                     && !_desktopMuteResets.ContainsKey(e.Key)).Select(e => e.Key).ToArray())
        {
            try { observed[name] = _pw.GetSinkMuted(name); }
            catch (InvalidOperationException) { observed.Remove(name); }
        }
        bool pulse = observed.Any(e => _outputMutes.TryGetValue(e.Key, out var previous)
            && previous.Muted != e.Value && !_desktopMuteResets.ContainsKey(e.Key));
        bool changed = pulse && !_desktopMuteConsumed;
        if (changed) SetMonitorMuted(!MonitorMuted);
        foreach ((string name, bool muted) in observed)
        {
            if ((pulse || _desktopMuteConsumed) && _outputMutes.TryGetValue(name, out var previous) && previous.Muted != muted)
                _desktopMuteResets[name] = (previous.Muted, 0, 0);
            else if (!_desktopMuteResets.ContainsKey(name)) _outputMutes[name] = (nodes[name].Identity, muted);
        }
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
                long delay = count < 3 ? 0 : Math.Min(30_000, 1_000L << (count - 3));
                _desktopMuteResets[name] = (muted, count, Clock() + delay);
            }
        }
        _desktopMuteConsumed = false;
        return changed;
    }

    private void ResetDesktopMonitorMuteLocked()
    {
        _desktopMutePulses.Clear();
        _desktopMuteConsumed = false;
        _outputMutes.Clear();
        _desktopMuteResets.Clear();
    }
}
