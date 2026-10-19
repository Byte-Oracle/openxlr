using System.Text.Json.Serialization;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// One port of a capture source, by exact node name, feeding a hardware
/// strip. A strip takes a single mono port, fed to both sides, so
/// <see cref="Pair"/> is 0 and <see cref="MonoChannel"/> is the port.
/// </summary>
public sealed record SourceBinding(string Source, int Pair = 0, int? MonoChannel = 0)
{
    /// <summary>A name the capture rules accept, pair 0 and a mono port from 0 to 63.</summary>
    [JsonIgnore]
    public bool IsValid => Pair == 0 && MonoChannel is not null && CaptureBinding.IsValid(Source, Pair, MonoChannel);

    /// <summary>The zero-based source port the strip takes.</summary>
    [JsonIgnore]
    public int Port => MonoChannel ?? 0;
}

/// <summary>
/// XLR 1's source binding in the shape the earlier <c>primaryInput</c> field
/// had, for mixer.json files written before <c>inputBindings</c> and for the
/// clients that still read <c>mixer.primaryInput</c>.
/// </summary>
public sealed record PrimaryInputBinding(string Source, int Channel = 0)
{
    [JsonIgnore]
    public bool IsValid => CaptureBinding.IsValid(Source, 0, Channel);
}

/// <summary>
/// What feeds a channel, in the state: <c>unit</c> (a hardware strip on the
/// driven interface's capture pair), <c>source</c> (an exact capture node,
/// on a hardware strip or a capture channel) or <c>apps</c>.
/// </summary>
public sealed record ChannelBinding(string Kind, string? Source = null, int? Pair = null, int? MonoChannel = null);

/// <summary>Which input stage of which driven unit a channel carries.</summary>
/// <param name="Unit">The unit's instance id, as <c>waveInterfaces</c> spells it.</param>
/// <param name="Input">1 or 2: the unit's first or second microphone input.</param>
/// <param name="Primary">True for the unit in the header, the one <c>state</c> and <c>capabilities</c> describe.</param>
public sealed record UnitInputStatus(string Unit, int Input, bool Primary);

public sealed partial class Mixer
{
    // Strip id -> the source it is bound to. Absent means the strip takes
    // its pair of the driven interface. Machine wiring: kept across builds
    // and layout changes, saved in mixer.json, never part of a scene.
    private readonly Dictionary<string, SourceBinding> _bindings = [];
    // The bindings each strip was last wired with, the source node each
    // strip is fed from now, and why a bound strip is silent.
    private readonly Dictionary<string, SourceBinding> _wiredBindings = [];
    private readonly Dictionary<string, string> _stripSources = [];
    private readonly Dictionary<string, string> _stripWarnings = [];
    private readonly Dictionary<string, string> _captureWarnings = [];

    private string? _drivenUnit;   // the primary unit's instance id while one is driven
    private bool _attached;        // a supported unit is attached, driven or not

    /// <summary>The hardware strips that can take a source: XLR 1 and XLR 2. Aux In stays on the interface.</summary>
    public static bool IsBindableStrip(string? channel) => channel is "xlr1" or "xlr2";

    public bool HasBindableStrip(string id) => IsBindableStrip(id) && HasChannel(id);

    /// <summary>The source a strip is bound to, or null when it takes the interface's pair.</summary>
    public SourceBinding? InputBinding(string channel) { lock (_gate) return _bindings.GetValueOrDefault(channel); }

    /// <summary>Every strip bound to a source.</summary>
    public IReadOnlyDictionary<string, SourceBinding> InputBindings { get { lock (_gate) return new Dictionary<string, SourceBinding>(_bindings); } }

    /// <summary>XLR 1's binding in the earlier <c>primaryInput</c> shape, or null when XLR 1 takes the interface.</summary>
    public PrimaryInputBinding? PrimaryInput { get { lock (_gate) return PrimaryInputLocked(); } }

    private PrimaryInputBinding? PrimaryInputLocked()
        => _bindings.TryGetValue("xlr1", out SourceBinding? b) ? new(b.Source, b.Port) : null;

    // While XLR 1 is bound to a source the daemon releases the Elgato units
    // (generic microphone mode): their jacks and the Aux return are set
    // aside and the Pro's direct microphone path is off.
    private bool GenericModeLocked => _bindings.ContainsKey("xlr1");

    /// <summary>
    /// Bind every strip as <paramref name="bindings"/> says, the strips it
    /// leaves out to the interface. Used at start and by saved settings.
    /// </summary>
    public void SetInputBindings(IReadOnlyDictionary<string, SourceBinding>? bindings)
    {
        lock (_gate)
        {
            foreach (string strip in (string[])["xlr1", "xlr2"])
                SetInputBinding(strip, bindings?.GetValueOrDefault(strip));
        }
    }

    /// <summary>
    /// Bind one hardware strip to a source port, or back to the interface
    /// with null, without rebuilding channels or mixes. A missing source
    /// stays selected and silent. The strip's monitor sends start muted
    /// after a change, so plugging in a hot microphone cannot feed back;
    /// the other strips keep their feeds, chains and mutes.
    /// </summary>
    public void SetInputBinding(string channel, SourceBinding? binding, Func<MixerSettings, string?>? save = null)
    {
        if (!IsBindableStrip(channel)) throw new ArgumentException("Only XLR 1 and XLR 2 take an input binding.");
        if (binding is not null && !binding.IsValid) throw new ArgumentException("Select an external capture source and a mono port from 0 to 63.");
        lock (_gate)
        {
            SourceBinding? previous = _bindings.GetValueOrDefault(channel);
            if (previous == binding)
            {
                if (save is not null) PersistLocked(save);
                return;
            }
            if (!_built) { AssignBindingLocked(channel, binding); return; }
            var previousMutes = new HashSet<string>(_muted);
            bool previousHardwareMonitor = _hardwareMicMonitor;
            List<string> previousOutputs = [.. _monitorOutputs], previousChoice = [.. _monitorChoice];
            (string? previousFallback, string? previousWarning) = (_monitorFallback, _monitorWarning);
            bool modeChanged = channel == "xlr1" && (previous is null) != (binding is null);
            try
            {
                if (_soundCheck is not null && _soundCheckChannel == channel) StopSoundCheckLocked(restore: false);
                AssignBindingLocked(channel, binding);
                if (channel == "xlr1") SetHardwareMicMonitor(false);
                MuteInputMonitorsLocked(channel);
                WireInputFeedsLocked([channel]);
                if (modeChanged)
                {
                    WireAuxRouteLocked();
                    ApplyModeOutputsLocked();
                }
                if (save is not null) PersistLocked(save);
            }
            catch (Exception change)
            {
                AssignBindingLocked(channel, previous);
                _muted.Clear();
                _muted.UnionWith(previousMutes);
                try
                {
                    SetHardwareMicMonitor(previousHardwareMonitor);
                    ReapplyMixesLocked(_config.Mixes);
                    WireInputFeedsLocked([channel]);
                    if (modeChanged)
                    {
                        WireAuxRouteLocked();
                        _monitorChoice.Clear();
                        SetMonitorOutputsLocked(previousOutputs);
                        _monitorChoice.AddRange(previousChoice);
                        (_monitorFallback, _monitorWarning) = (previousFallback, previousWarning);
                    }
                }
                catch (Exception rollback)
                {
                    throw new AggregateException("The input binding could not be changed or restored.", change, rollback);
                }
                throw;
            }
        }
    }

    private void AssignBindingLocked(string channel, SourceBinding? binding)
    {
        if (binding is null) _bindings.Remove(channel);
        else _bindings[channel] = binding;
    }

    /// <summary>
    /// The driven primary unit's instance id, or null while none is driven.
    /// It names the unit in <c>unitInput</c>. True when it changed.
    /// </summary>
    public bool SetDrivenUnit(string? instanceId)
    {
        lock (_gate)
        {
            if (_drivenUnit == instanceId) return false;
            _drivenUnit = instanceId;
            return true;
        }
    }

    /// <summary>
    /// Whether a supported unit is attached, driven or not. Before any unit
    /// has connected in this run, XLR 2 and Aux In are present only while
    /// one is. True when it changed.
    /// </summary>
    public bool SetAttached(bool attached)
    {
        lock (_gate)
        {
            if (_attached == attached) return false;
            _attached = attached;
            return true;
        }
    }

    /// <summary>
    /// Whether a client should draw this channel. A channel that needs no
    /// jack is always present, and so is a strip bound to a source. A strip
    /// on the interface is present when the connected unit has its jack;
    /// with no unit connected it keeps the last answer of this run, and
    /// before any unit has connected XLR 1 is present and XLR 2 and Aux In
    /// are present only while a supported unit is attached.
    /// </summary>
    private bool ChannelPresentLocked(ChannelDefinition channel)
    {
        if (channel.InputPair is null || _bindings.ContainsKey(channel.Id)) return true;
        int? jacks = _deviceXlrInputs ?? _lastXlrInputs;
        bool? aux = _deviceXlrInputs is null ? _lastAuxInput : _deviceAuxInput;
        if (jacks is null) return channel.NeedsXlrInputs is not > 1 && !channel.NeedsAuxInput || _attached;
        if (channel.NeedsXlrInputs is int needed && jacks < needed) return false;
        return !channel.NeedsAuxInput || aux is not false;
    }

    /// <summary>
    /// The channel that carries the driven unit's microphone input 1 or 2,
    /// or null. The hardware strip on that input's pair wins while it takes
    /// the interface; otherwise the first capture channel, in layout order,
    /// on the unit's own node at that input's port (the first for input 1,
    /// the third for input 2) or on its pair.
    /// </summary>
    private string? UnitInputChannelLocked(int input)
        => _drivenUnit is null ? null : UnitInputChannel(_config.Channels, _bindings, _inputDevice, _deviceXlrInputs ?? 1, input);

    /// <inheritdoc cref="UnitInputChannelLocked"/>
    internal static string? UnitInputChannel(IReadOnlyList<ChannelDefinition> channels,
        IReadOnlyDictionary<string, SourceBinding> bindings, string? unitNode, int xlrInputs, int input)
    {
        if (input is not (1 or 2) || input > xlrInputs) return null;
        ChannelDefinition? strip = channels.FirstOrDefault(c => c.InputPair == input - 1);
        if (strip is not null && !bindings.ContainsKey(strip.Id)) return strip.Id;
        if (unitNode is null) return null;
        int port = input == 1 ? 0 : 2;
        return channels.FirstOrDefault(c => c.CaptureSource == unitNode
            && (c.CaptureMonoChannel == port || c.CaptureMonoChannel is null && c.CapturePair == input - 1))?.Id;
    }

    private UnitInputStatus? UnitInputLocked(string channel)
    {
        for (int input = 1; input <= 2; input++)
            if (UnitInputChannelLocked(input) == channel) return new(_drivenUnit!, input, Primary: true);
        return null;
    }

    private ChannelBinding BindingStateLocked(ChannelDefinition channel)
    {
        if (channel.InputPair is int pair)
            return _bindings.TryGetValue(channel.Id, out SourceBinding? b)
                ? new("source", b.Source, b.Pair, b.MonoChannel)
                : new("unit", Pair: pair);
        return channel.CaptureSource is { } source
            ? new("source", source, channel.CapturePair, channel.CaptureMonoChannel)
            : new("apps");
    }

    private bool BindingConnectedLocked(ChannelDefinition channel)
        => channel.InputPair is not null ? _stripSources.ContainsKey(channel.Id) && _inputFeeds.ContainsKey(channel.Id)
            : channel.CaptureSource is null || _captureFeeds.ContainsKey(channel.Id);

    private string? BindingWarningLocked(ChannelDefinition channel)
        => (channel.InputPair is not null ? _stripWarnings : _captureWarnings).GetValueOrDefault(channel.Id);

    /// <summary>
    /// The unit-level sentence, one per silent bound strip with the strip's
    /// name in front, then the monitor's, for clients that show one line.
    /// </summary>
    private string? InputWarningLocked()
    {
        List<string> parts = [];
        if (_inputWarning is not null) parts.Add(_inputWarning);
        foreach (ChannelDefinition channel in _config.Channels)
            if (_stripWarnings.TryGetValue(channel.Id, out string? warning)) parts.Add($"{channel.Name}: {warning}");
        if (_monitorWarning is not null) parts.Add(_monitorWarning);
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>
    /// The source a bound strip is fed from, or null with the reason noted
    /// for the strip. The exact name must identify one source that has the
    /// port; no other microphone ever takes its place.
    /// </summary>
    private string? ResolveBoundSourceLocked(string strip, SourceBinding binding, IReadOnlyList<AudioNode> sources)
    {
        int matches = sources.Count(s => s.Name == binding.Source);
        string? warning = matches switch
        {
            0 => $"{binding.Source} is not connected. The strip stays silent until it returns.",
            1 => _pw.HasCaptureChannel(binding.Source, binding.Port) ? null
                : $"{binding.Source} has no port {binding.Port + 1}. The strip stays silent until it returns.",
            _ => $"More than one source is named {binding.Source}. The strip stays silent until the name is unique.",
        };
        if (warning is null)
        {
            _stripWarnings.Remove(strip);
            return binding.Source;
        }
        _stripWarnings[strip] = warning;
        return null;
    }

    /// <summary>Console rule: a strip whose source changed comes up muted in the monitor mixes.</summary>
    private void MuteInputMonitorsLocked(string channel)
    {
        foreach (MixDefinition mix in _config.Mixes.Where(m => m.Kind == MixKind.Monitor))
        {
            if (!_cells.Contains(Cell(channel, mix.Id))) continue;
            _muted.Add(Cell(channel, mix.Id));
            ApplyCellLocked(channel, mix.Id);
        }
    }

    private PortLink RouteInputLocked(string source, string target, ChannelDefinition channel, bool processed = false)
    {
        SourceBinding? binding = _bindings.GetValueOrDefault(channel.Id);
        PortLink route = binding is not null && !processed
            ? _pw.LinkNodes(source, "", target, "playback", fromChannel: binding.Port)
            : _pw.RouteInputToChannel(source, target, processed ? 0 : channel.InputPair!.Value);
        if (binding is not null && (!route.Complete || route.Pairs.Count < (target == channel.SinkName ? 2 : 1)))
        {
            // A half-connected microphone must retry instead of being kept
            // as a healthy route that carries audio on one side only.
            _pw.Unlink(route);
            return new PortLink([]);
        }
        return route;
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
        if (GenericModeLocked)
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
        if (!GenericModeLocked) return requested;
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
}
