namespace OpenXLR.Core.Mixing;

/// <summary>
/// The microphone EQ on XLR 1 and XLR 2. Its stage sits at the head of the
/// input's route, between the source (or Sound Check's loop) and the software
/// low cut, ClipGuard and inserts:
///
///   source -> OpenXLR_eq_&lt;id&gt;_&lt;gen&gt; -> OpenXLR_lc_&lt;id&gt;_&lt;gen&gt; (when a chain runs) -> OpenXLR_ch_&lt;id&gt;
///
/// The stage is spliced in and out by relinking the head, so the chain
/// behind it keeps running and no plugin restarts. Every value change, the
/// EQ's own switch included, goes to the running stage live; turning the EQ
/// off bypasses inside the stage and keeps it running until the route is
/// next built, so on and off compare at once. A microphone whose EQ is off
/// when its route is built gets no stage.
/// </summary>
public sealed partial class Mixer
{
    /// <summary>What an EQ that cannot run says when this build has no native helper.</summary>
    public const string EqNeedsHost =
        "The microphone EQ needs the OpenXLR native plugin host, which this installation does not include. Install a package that ships it, or build with -p:EnableNativeLv2Host=true.";

    /// <summary>What an EQ the restart policy has given up on says.</summary>
    internal const string EqGivenUp = "the EQ kept failing and is off; switch it off and on to try again";

    /// <summary>The channels that carry a microphone EQ: XLR 1 and XLR 2, until capture microphones take one.</summary>
    public static bool IsMicrophoneId(string? id) => id is "xlr1" or "xlr2";

    /// <summary>A microphone input of this layout.</summary>
    public bool IsMicrophoneChannel(string id) { lock (_gate) return IsMicrophoneId(id) && _config.Channels.Any(c => c.Id == id); }

    private static string EqKey(string channel) => "eq:" + channel;

    // Saved values by channel. A microphone absent here has the defaults with
    // the EQ off; an entry for any other channel, read from a file, is kept
    // for the save.
    private readonly Dictionary<string, MicEq> _eq = new(StringComparer.Ordinal);
    // The running stages, the link from each stage's upstream (the source or
    // the loop helper) into it and that upstream's node, and why a stage
    // could not start.
    private readonly Dictionary<string, SoftwareEqBackend> _eqStages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PortLink> _eqInputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _eqUpstreams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _eqErrors = new(StringComparer.Ordinal);
    // The node each strip's feed comes from: its EQ stage, the loop helper or
    // the source. A feed is reused only from the same node.
    private readonly Dictionary<string, string> _inputHeads = new(StringComparer.Ordinal);
    private long _eqGeneration;

    /// <summary>A channel's EQ: what was set or recalled, else the defaults with the EQ off.</summary>
    public MicEq EqFor(string channel) { lock (_gate) return EqLocked(channel); }

    private MicEq EqLocked(string channel) => _eq.GetValueOrDefault(channel) ?? MicEq.Default;

    /// <summary>
    /// The backend that runs a channel's EQ. Software for every input today:
    /// no unit reports <see cref="Devices.DeviceCapabilities.HardwareEq"/>,
    /// and a bound strip has no hardware stage in front of it whatever unit
    /// is driven.
    /// </summary>
    private static string EqBackendKind(string channel) => "software";

    /// <summary>Replace a microphone's whole EQ: its switch and four bands.</summary>
    public void SetEq(string channel, MicEq eq)
    {
        if (MicEq.Check(eq) is string problem) throw new ArgumentException($"setEq: {problem}");
        lock (_gate)
        {
            RequireMicrophoneLocked(channel);
            bool switchedOn = eq.Enabled && !EqLocked(channel).Enabled;
            _eq[channel] = MicEq.Snap(eq);
            if (switchedOn) _restarts.Forget(EqKey(channel));
            ApplyEqLocked(channel);
        }
    }

    /// <summary>The EQ's own switch.</summary>
    public void SetEqEnabled(string channel, bool on)
    {
        lock (_gate)
        {
            RequireMicrophoneLocked(channel);
            MicEq eq = EqLocked(channel);
            if (on) _restarts.Forget(EqKey(channel));
            _eq[channel] = eq with { Enabled = on };
            ApplyEqLocked(channel);
        }
    }

    /// <summary>Part of one band, 1 to 4: the path a drag takes.</summary>
    public void SetEqBand(string channel, int band, EqBandChange change)
    {
        if (band is < 1 or > MicEq.BandCount) throw new ArgumentException($"setEqBand: band must be 1 to {MicEq.BandCount}");
        if (change.IsEmpty) throw new ArgumentException("setEqBand: need at least one of 'enabled', 'type', 'frequency', 'gain' and 'q'");
        lock (_gate)
        {
            RequireMicrophoneLocked(channel);
            MicEq eq = EqLocked(channel);
            EqBand next = change.ApplyTo(eq.Bands[band - 1]);
            if (MicEq.Check(next) is string problem) throw new ArgumentException($"setEqBand: {problem}");
            _eq[channel] = eq.WithBand(band, MicEq.Snap(next));
            ApplyEqLocked(channel);
        }
    }

    private void RequireMicrophoneLocked(string channel)
    {
        if (!IsMicrophoneId(channel) || _config.Channels.All(c => c.Id != channel))
            throw new ArgumentException($"'{channel}' is not a microphone input");
    }

    /// <summary>
    /// Bring a microphone's running EQ to its saved values: written live to a
    /// running stage, which builds nothing; spliced in when the EQ is on and
    /// no stage runs. A stage that refuses a value is replaced.
    /// </summary>
    private void ApplyEqLocked(string channel)
    {
        if (!_built) return;
        MicEq eq = EqLocked(channel);
        if (_eqStages.TryGetValue(channel, out SoftwareEqBackend? stage))
        {
            if (stage.IsAlive)
            {
                try
                {
                    stage.Apply(eq);
                    return;
                }
                catch (InvalidOperationException ex)
                {
                    _pw.Note($"The EQ on {channel} did not take a change and is started again: {ex.Message}");
                }
            }
            SpliceEqLocked(channel, replace: true);
            return;
        }
        if (eq.Enabled) SpliceEqLocked(channel, replace: false);
    }

    /// <summary>Every microphone's running EQ brought to its saved values, after a recall.</summary>
    private void SyncEqStagesLocked()
    {
        foreach (ChannelDefinition ch in _config.Channels.Where(c => IsMicrophoneId(c.Id)).ToList())
            ApplyEqLocked(ch.Id);
    }

    /// <summary>Store recalled EQs, snapped, for the channels the recall names.</summary>
    private void RecallEqLocked(IReadOnlyDictionary<string, MicEq> recalled)
    {
        foreach ((string channel, MicEq eq) in RecallEq(_eq, recalled))
        {
            if (eq.Enabled && !EqLocked(channel).Enabled) _restarts.Forget(EqKey(channel));
            _eq[channel] = eq;
        }
    }

    /// <summary>
    /// The EQs after a recall: each channel the recalled data names takes its
    /// EQ, snapped; every other channel keeps what it has, so a recall
    /// without EQs (null) changes nothing. A key for a channel that is not a
    /// microphone is kept as it is, and an entry that cannot be read was
    /// refused or dropped by the loader before it got here.
    /// </summary>
    internal static Dictionary<string, MicEq> RecallEq(IReadOnlyDictionary<string, MicEq> current,
        IReadOnlyDictionary<string, MicEq>? recalled)
    {
        var next = new Dictionary<string, MicEq>(current, StringComparer.Ordinal);
        foreach ((string channel, MicEq eq) in recalled ?? new Dictionary<string, MicEq>())
            if (MicEq.Check(eq) is null) next[channel] = MicEq.Snap(eq);
        return next;
    }

    /// <summary>
    /// Start an EQ stage for a strip and link its upstream into it. Null
    /// when it cannot run, with the reason kept for the state; nothing is
    /// left behind then.
    /// </summary>
    private SoftwareEqBackend? StartEqStageLocked(ChannelDefinition ch, string upstream, bool upstreamProcessed, out PortLink? into)
    {
        into = null;
        string key = EqKey(ch.Id);
        if (_restarts.Blocked(key)) return null;
        MicEq eq = EqLocked(ch.Id);
        FilterHandle? filter = null;
        try
        {
            filter = _pw.CreateMicEq($"{ch.Id}_{++_eqGeneration}", SoftwareEqBackend.Controls(eq));
            var stage = new SoftwareEqBackend(_pw, filter, eq);
            into = RouteInputLocked(upstream, stage.Node, ch, upstreamProcessed);
            if (into.Pairs.Count == 0) throw new InvalidOperationException("The microphone could not be linked into its EQ.");
            _eqErrors.Remove(ch.Id);
            return stage;
        }
        catch (InvalidOperationException ex)
        {
            if (into is not null) _pw.Unlink(into);
            into = null;
            if (filter is not null) _pw.StopFilter(filter);
            _eqErrors[ch.Id] = ex.Message;
            // A missing helper is not a failure to count: nothing retries it.
            if (_pw.EqHostInstalled) _restarts.Failed(key, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Put a strip's head right without touching its chain: start a stage
    /// when the EQ is on and none runs (or <paramref name="replace"/> asks
    /// for a fresh one), link the stage's output to whatever the strip feeds
    /// (its chain, or its channel), then remove the old feed. Make before
    /// break, so the microphone is never silent. Without a stage the source
    /// feeds the chain or channel directly again. False when nothing changed
    /// or the strip has no route to splice into; the next build of the strip
    /// picks the EQ up then.
    /// </summary>
    private bool SpliceEqLocked(string channel, bool replace)
    {
        ChannelDefinition? ch = _config.Channels.FirstOrDefault(c => c.Id == channel && c.InputPair is not null);
        if (ch is null || !_inputFeeds.TryGetValue(channel, out PortLink? feed) || !_stripSources.TryGetValue(channel, out string? input))
            return false;
        InputRoute route = InputRouteLocked(ch, input);
        string upstream = route.Source!;
        SoftwareEqBackend? old = _eqStages.GetValueOrDefault(channel);
        if (old is not null && old.IsAlive && !replace) return false;
        PortLink? into = null;
        SoftwareEqBackend? stage = route.Eq ? StartEqStageLocked(ch, upstream, route.Looped, out into) : null;
        if (stage is null && old is null) return false;
        string target = _chains.TryGetValue(channel, out FilterHandle? chain) ? chain.SinkName : ch.SinkName;
        string head = stage?.Node ?? upstream;
        PortLink next = RouteInputLocked(head, target, ch, processed: stage is not null || route.Looped);
        if (next.Pairs.Count == 0)
        {
            if (stage is not null)
            {
                _pw.Unlink(into!);
                stage.Release();
                _eqErrors[channel] = "The EQ could not be linked to the microphone's channel.";
            }
            return false;
        }
        _pw.Unlink(new PortLink([.. feed.Pairs.Except(next.Pairs)]));
        if (_eqInputs.Remove(channel, out PortLink? oldInto)) _pw.Unlink(oldInto);
        _eqUpstreams.Remove(channel);
        if (_eqStages.Remove(channel)) old!.Release();
        _inputFeeds[channel] = next;
        _inputHeads[channel] = head;
        if (stage is not null)
        {
            _eqStages[channel] = stage;
            _eqInputs[channel] = into!;
            _eqUpstreams[channel] = upstream;
            // A direct link from the microphone that this daemon does not
            // track would carry it past the EQ onto the equalised signal.
            _pw.UnlinkNodes(input, target);
        }
        return true;
    }

    /// <summary>
    /// The sweep's part: a stage whose helper died counts against the
    /// restart policy keyed <c>eq:&lt;channel&gt;</c> and is started again;
    /// one that cannot start leaves the microphone on its path without the
    /// EQ, never silent. An EQ that is on but has no stage, because its
    /// start failed, is tried again on the same budget. True when a route
    /// changed.
    /// </summary>
    private bool HealEqStagesLocked()
    {
        if (!_built) return false;
        bool changed = false;
        foreach ((string channel, SoftwareEqBackend stage) in _eqStages.ToList())
        {
            if (stage.IsAlive) continue;
            string? words = stage.Stage.LastWords;
            _restarts.Failed(EqKey(channel), words);
            _pw.Note($"The EQ on {channel} stopped{(words is null ? "." : $". The plugin host's last message: {words}")}");
            if (SpliceEqLocked(channel, replace: true)) { changed = true; continue; }
            // Nothing could be linked: drop the dead stage and leave the
            // strip's broken feed to the input sweep, which builds it again.
            if (_eqInputs.Remove(channel, out PortLink? into)) _pw.Unlink(into);
            _eqUpstreams.Remove(channel);
            _eqStages.Remove(channel);
            stage.Release();
            changed = true;
        }
        if (!_pw.EqHostInstalled) return changed;
        foreach (ChannelDefinition ch in _config.Channels.Where(c => IsMicrophoneId(c.Id) && c.InputPair is not null).ToList())
            if (!_eqStages.ContainsKey(ch.Id) && EqLocked(ch.Id).Enabled && !_restarts.Blocked(EqKey(ch.Id))
                && _eqErrors.ContainsKey(ch.Id) && _inputFeeds.ContainsKey(ch.Id))
                changed |= SpliceEqLocked(ch.Id, replace: false);
        return changed;
    }

    /// <summary>Stop every EQ stage and forget the heads, with the rest of the input routes.</summary>
    private void RemoveEqStagesLocked()
    {
        foreach (PortLink into in _eqInputs.Values) _pw.Unlink(into);
        foreach (SoftwareEqBackend stage in _eqStages.Values) stage.Release();
        _eqInputs.Clear();
        _eqStages.Clear();
        _eqUpstreams.Clear();
        _inputHeads.Clear();
    }

    /// <summary>A microphone's EQ for the state.</summary>
    private MicEqState EqStateLocked(string channel)
    {
        MicEq eq = EqLocked(channel);
        bool installed = _pw.EqHostInstalled;
        bool blocked = _restarts.Blocked(EqKey(channel));
        string? error = !installed ? EqNeedsHost
            : blocked ? _restarts.Message(EqKey(channel), EqGivenUp)
            : eq.Enabled ? _eqErrors.GetValueOrDefault(channel) : null;
        string? backend = _eqStages.ContainsKey(channel) ? EqBackendKind(channel) : null;
        return new MicEqState(eq.Enabled, eq.Bands, backend, installed && !blocked, error);
    }

    /// <summary>Every microphone's EQ, for a profile: a recall then names every microphone.</summary>
    private Dictionary<string, MicEq> SceneEqLocked()
        => _config.Channels.Where(c => IsMicrophoneId(c.Id)).ToDictionary(c => c.Id, c => EqLocked(c.Id), StringComparer.Ordinal);
}
