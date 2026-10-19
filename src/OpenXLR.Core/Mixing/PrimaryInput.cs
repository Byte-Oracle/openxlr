namespace OpenXLR.Core.Mixing;

/// <summary>A stable capture node and one zero-based mono port feeding XLR 1.</summary>
public sealed record PrimaryInputBinding(string Source, int Channel = 0)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => CaptureBinding.IsValid(Source, 0, Channel);
}

public sealed partial class Mixer
{
    private PrimaryInputBinding? _primaryInput, _wiredPrimaryInput;

    /// <summary>Null uses the supported interface; an explicit binding enables generic microphone mode.</summary>
    public PrimaryInputBinding? PrimaryInput { get { lock (_gate) return _primaryInput; } }

    /// <summary>
    /// Replace the primary microphone without rebuilding channels or mixes.
    /// A missing source stays selected and silent. Switching sources mutes
    /// microphone monitoring, so plugging in a hot microphone cannot feed back.
    /// </summary>
    public void SetPrimaryInput(PrimaryInputBinding? input, Func<MixerSettings, string?>? save = null)
    {
        if (input is not null && !input.IsValid) throw new ArgumentException("Select an external capture source and a mono port from 0 to 63.");
        lock (_gate)
        {
            if (_primaryInput == input)
            {
                if (save is not null) PersistLocked(save);
                return;
            }
            PrimaryInputBinding? previous = _primaryInput;
            var previousMutes = new HashSet<string>(_muted);
            bool previousHardwareMonitor = _hardwareMicMonitor;
            if (!_built) { _primaryInput = input; return; }
            List<string> previousOutputs = [.. _monitorOutputs], previousChoice = [.. _monitorChoice];
            (string? previousFallback, string? previousWarning) = (_monitorFallback, _monitorWarning);
            bool modeChanged = (previous is null) != (input is null);
            try
            {
                StopSoundCheckLocked(restore: false);
                _primaryInput = input;
                SetHardwareMicMonitor(false);
                MuteInputMonitorsLocked();
                WireInputFeedsLocked();
                WireAuxRouteLocked();
                if (modeChanged) ApplyModeOutputsLocked();
                if (save is not null) PersistLocked(save);
            }
            catch (Exception change)
            {
                _primaryInput = previous;
                _muted.Clear();
                _muted.UnionWith(previousMutes);
                try
                {
                    SetHardwareMicMonitor(previousHardwareMonitor);
                    ReapplyMixesLocked(_config.Mixes);
                    WireInputFeedsLocked();
                    WireAuxRouteLocked();
                    if (modeChanged)
                    {
                        _monitorChoice.Clear();
                        SetMonitorOutputsLocked(previousOutputs);
                        _monitorChoice.AddRange(previousChoice);
                        (_monitorFallback, _monitorWarning) = (previousFallback, previousWarning);
                    }
                }
                catch (Exception rollback)
                {
                    throw new AggregateException("The primary microphone could not be changed or restored.", change, rollback);
                }
                throw;
            }
        }
    }

    // The Wave XLR Pro's jacks ("...#hp1", "...#hp2", "...#lineout") play
    // through the unit OpenXLR drives, and generic mode drives none: its card
    // goes back to the profile it had, which can rename the raw sink, and
    // nothing asserts the output selectors. While generic mode lasts the
    // jacks are set aside here, never forgotten: the saved settings and
    // profiles keep them, and automatic mode selects them again.
    // The selection, in its order, that last named a jack in generic mode;
    // empty while no jack is set aside. The order matters: the first output
    // is the one the follow mode of the default output resolves to.
    private readonly List<string> _monitorChoice = [];
    private string? _monitorFallback;    // the output picked in their place, until the user picks one
    private string? _monitorWarning;

    /// <summary>
    /// A sink that belongs to an attached supported interface. The daemon
    /// sets it; generic mode releases those units, so their sinks are never
    /// picked as the replacement monitor output.
    /// </summary>
    public Func<string, bool>? IsSupportedUnitNode { get; set; }

    private static bool IsJack(string output)
        => output.Contains('#') && !output.EndsWith("#usbaux", StringComparison.Ordinal);

    /// <summary>A jack set aside by generic mode, whose feed is kept for its return.</summary>
    private bool IsSetAsideLocked(string output) => IsJack(output) && _monitorChoice.Contains(output);

    /// <summary>
    /// The monitor outputs as the user chose them, in order: the set-aside
    /// jacks where they were, the outputs still selected, then the outputs
    /// added in generic mode, without the one picked in the jacks' place.
    /// </summary>
    private List<string> SavedMonitorOutputsLocked()
        => _monitorChoice.Count == 0 ? [.. _monitorOutputs]
            : [.. _monitorChoice.Where(o => IsJack(o) || _monitorOutputs.Contains(o)),
               .. _monitorOutputs.Where(o => o != _monitorFallback && !_monitorChoice.Contains(o))];

    /// <summary>
    /// Saved settings and profiles carry the whole choice, jacks included, so
    /// they replace what generic mode set aside instead of adding to it.
    /// </summary>
    private void ReplaceMonitorChoiceLocked(IReadOnlyList<string> outputs)
    {
        _monitorChoice.Clear();
        (_monitorFallback, _monitorWarning) = (null, null);
        SetMonitorOutputsLocked(outputs);
    }

    /// <summary>Set the jacks aside on entering generic mode, or select them again on leaving it.</summary>
    private void ApplyModeOutputsLocked()
    {
        if (_primaryInput is not null)
        {
            SetMonitorOutputsLocked([.. _monitorOutputs]);
            return;
        }
        List<string> restored = SavedMonitorOutputsLocked();
        bool parked = _monitorChoice.Count > 0;
        _monitorChoice.Clear();
        _monitorFallback = null;
        _monitorWarning = null;
        if (parked) SetMonitorOutputsLocked(restored);
    }

    /// <summary>
    /// The outputs to route in generic mode: a request naming a jack sets it
    /// aside, and when nothing else is left the system default output takes
    /// its place, or the first hardware output when the default is one of
    /// OpenXLR's own sinks or a supported interface's. Outside generic mode,
    /// or with no jack in the request, the request is returned unchanged.
    /// </summary>
    private IReadOnlyList<string> DivertJacksLocked(IReadOnlyList<string> requested)
    {
        if (_primaryInput is null) return requested;
        List<string> jacks = [.. requested.Where(IsJack).Distinct()];
        if (jacks.Count == 0) return requested;
        _monitorChoice.Clear();
        _monitorChoice.AddRange(requested.Distinct());
        List<string> plain = [.. requested.Where(o => !o.Contains('#')).Distinct()];
        _monitorFallback = null;
        if (plain.Count > 0)
        {
            _monitorWarning = "The Wave XLR Pro outputs are unavailable in generic microphone mode, so the monitor plays on the other selected outputs until automatic mode returns.";
            return plain;
        }
        if (FallbackOutputLocked(jacks) is { } fallback)
        {
            _monitorFallback = fallback.Name;
            _monitorWarning = $"The Wave XLR Pro outputs are unavailable in generic microphone mode, so the monitor plays on {fallback.Description} until automatic mode returns.";
            return [fallback.Name];
        }
        _monitorWarning = "The Wave XLR Pro outputs are unavailable in generic microphone mode and no other output was found, so the monitor is silent. Pick an output under MONITOR.";
        return [];
    }

    private AudioNode? FallbackOutputLocked(IReadOnlyList<string> jacks)
    {
        var raw = jacks.Select(StripMarker).ToHashSet(StringComparer.Ordinal);
        List<AudioNode> sinks = [.. _pw.ListDevices().Where(d => d.Kind == AudioNodeKind.Sink && !d.IsOwn
            && !d.Name.Contains('#') && !raw.Contains(d.Name) && IsSupportedUnitNode?.Invoke(d.Name) is not true)];
        string? current;
        try { current = _pw.GetDefaultSink(); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { current = null; }
        return sinks.FirstOrDefault(d => d.Name == current) ?? sinks.FirstOrDefault(d => d.IsPhysical);
    }

    private void MuteInputMonitorsLocked()
    {
        foreach (MixDefinition mix in _config.Mixes.Where(m => m.Kind == MixKind.Monitor))
            foreach (ChannelDefinition channel in _config.Channels.Where(c => c.InputPair is not null))
            {
                _muted.Add(Cell(channel.Id, mix.Id));
                ApplyCellLocked(channel.Id, mix.Id);
            }
    }

    private PortLink RouteInputLocked(string source, string target, ChannelDefinition channel, bool processed = false)
    {
        PortLink route = _primaryInput is { } input && !processed
            ? _pw.LinkNodes(source, "", target, "playback", fromChannel: input.Channel)
            : _pw.RouteInputToChannel(source, target, processed ? 0 : channel.InputPair!.Value);
        if (_primaryInput is not null && (!route.Complete || route.Pairs.Count < (target == channel.SinkName ? 2 : 1)))
        {
            // A half-connected microphone must retry instead of being kept
            // as a healthy route that carries audio on one side only.
            _pw.Unlink(route);
            return new PortLink([]);
        }
        return route;
    }
}
