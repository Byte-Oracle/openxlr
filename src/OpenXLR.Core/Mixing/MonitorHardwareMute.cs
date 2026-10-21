namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    // The native flags held before OpenXLR muted an output. This is machine
    // wiring, saved with selected outputs rather than with profile scenes.
    private readonly Dictionary<string, bool> _monitorOutputMutes = new(StringComparer.Ordinal);
    private bool _readingDesktopMutes;

    /// <summary>Whether one selected output has an open mix in its assigned feed.</summary>
    public bool IsMonitorOutputAudible(string output)
    {
        lock (_gate) return !_monitorMuted && _monitorOutputs.Contains(output)
            && MixesForOutputLocked(output).Any(m => !_mixMuted.Contains(m.Id));
    }

    private void SyncMonitorHardwareMutesLocked(string? unmutedMix = null, bool explicitChange = false)
    {
        if (!_built || _readingDesktopMutes) return;
        foreach (string name in PhysicalMonitorOutputsLocked())
        {
            if (!_outputMutes.TryGetValue(name, out var baseline)) continue;
            if (!explicitChange && _desktopMuteRetries.ContainsKey(name) && !_desktopMuteOffsets.Contains(name)) continue;
            var outputs = _monitorOutputs.Where(o => StripMarker(o) == name).ToArray();
            bool audible = !_monitorMuted && outputs.Any(o => MixesForOutputLocked(o).Any(m => !_mixMuted.Contains(m.Id)));
            // The Pro's Aux pair and monitor bus live in one PipeWire sink.
            // Its jack selectors stop monitoring independently of the Aux port.
            bool independentAux = _auxPortEnabled && _auxTargetSink == name
                && _config.Mixes.Any(m => m.Kind == MixKind.AuxPort && !_mixMuted.Contains(m.Id));
            bool closed = !audible && !independentAux;
            if (closed && baseline.Released && !explicitChange) continue;
            bool explicitOpen = unmutedMix is not null && outputs.Any(o => MixesForOutputLocked(o).Any(m => m.Id == unmutedMix));
            bool wanted;
            if (closed)
            {
                _monitorOutputMutes.TryAdd(name, baseline.Muted);
                wanted = true;
            }
            else if (explicitOpen)
            {
                _monitorOutputMutes.Remove(name);
                wanted = false;
            }
            else if (_monitorOutputMutes.Remove(name, out bool previous)) wanted = previous;
            else continue;
            if (baseline.Muted == wanted) continue;
            _outputMutes[name] = baseline with { Muted = wanted, UnmuteObserved = false, Released = false };
            bool credited = _desktopMuteOffsets.Remove(name);
            _desktopMutePending.Remove(name);
            _desktopMuteRetries.Remove(name);
            try
            {
                // Change the expected flag by a toggle too. A native key that
                // raced the command stays visible as a difference next sweep.
                if (!credited) _pw.ToggleSinkMuted(name);
                _desktopMuteResets.Remove(name);
            }
            catch (InvalidOperationException)
            {
                // Software links stay closed while the usual bounded native
                // restoration path retries the current intent after failure.
                _desktopMuteResets[name] = (wanted, 0, 0);
            }
        }
    }

    private void RestoreUnselectedMonitorHardwareMuteLocked(string name)
    {
        if (!_monitorOutputMutes.Remove(name, out bool previous) || !_outputMutes.TryGetValue(name, out var baseline)
            || baseline.Muted == previous) return;
        try
        {
            var current = _pw.NativeSinkMutes(new HashSet<string>([name], StringComparer.Ordinal));
            if (current.TryGetValue(name, out var state) && state.Identity == baseline.Identity && state.Muted == baseline.Muted)
                _pw.ToggleSinkMuted(name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException)
        { /* A missing or replaced device is no longer owned by the selection. */ }
    }
}
