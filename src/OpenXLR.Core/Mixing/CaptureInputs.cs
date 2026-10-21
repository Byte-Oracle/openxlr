namespace OpenXLR.Core.Mixing;

/// <summary>Capture bindings use stable node names, never transient registry ids.</summary>
public static class CaptureBinding
{
    /// <summary>
    /// A source takes either a stereo pair, 0 to 31, or with pair 0 one
    /// mono port, 0 to 63, that feeds both sides of the channel.
    /// </summary>
    public static bool IsValid(string? source, int pair, int? monoChannel = null)
        => source is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(source)
            && !source.Any(char.IsControl) && !source.StartsWith("OpenXLR", StringComparison.Ordinal)
            && !source.EndsWith(".monitor", StringComparison.Ordinal) && pair is >= 0 and < 32
            && (monoChannel is null || pair == 0 && monoChannel is >= 0 and < 64);

    internal static ChannelDefinition MigrateMono(ChannelDefinition channel, IReadOnlyList<AudioNode> sources,
        IReadOnlyList<InsertDefinition> inserts)
        => channel.CaptureSource is not null && channel.CapturePair == 0 && channel.CaptureMonoChannel is null
            && inserts.Count == 0 && sources.Count(s => s.Kind == AudioNodeKind.Source && !s.IsOwn
                && s.Name == channel.CaptureSource) == 1
            && sources.Any(s => s.Kind == AudioNodeKind.Source && !s.IsOwn && s.Name == channel.CaptureSource && s.Channels == 1)
                ? channel with { CaptureMonoChannel = 0 } : channel;
}

public sealed partial class Mixer
{
    private readonly Dictionary<string, PortLink> _captureFeeds = [];

    /// <summary>
    /// Adopt a mono port only after saved chains have been restored, and
    /// remember an attached unit while the exact source is present. The
    /// caller saves the changed layout; existing links need no change.
    /// </summary>
    public bool MigrateCaptureInputs(Func<string, string?>? unitForSource = null)
    {
        lock (_gate)
        {
            if (!_built || !_config.Channels.Any(c => c.CaptureSource is not null
                && (c.CaptureUnit is null && unitForSource is not null
                    || c.CapturePair == 0 && c.CaptureMonoChannel is null && InsertsFor(c.Id).Count == 0))) return false;
            IReadOnlyList<AudioNode> sources = _pw.ListDevices();
            bool changed = false;
            _config = _config with { Channels = [.. _config.Channels.Select(ch =>
            {
                ChannelDefinition next = CaptureBinding.MigrateMono(ch, sources, InsertsFor(ch.Id));
                if (!ReferenceEquals(ch, next))
                    _pw.Note($"Capture input {ch.Id} now takes mono port 1 of {ch.CaptureSource}.");
                if (ch.CaptureSource is { } source && ch.CaptureUnit is null
                    && sources.Count(s => s.Kind == AudioNodeKind.Source && !s.IsOwn && s.Name == source) == 1
                    && unitForSource?.Invoke(source) is { } unit && SourceBinding.IsUnitId(unit))
                {
                    next = next with { CaptureUnit = unit };
                    _pw.Note($"Capture input {ch.Id} now follows interface {unit}.");
                }
                changed |= !ReferenceEquals(ch, next);
                return next;
            })] };
            return changed;
        }
    }

    private bool CaptureRouteLocked(ChannelDefinition channel)
        => channel.CaptureSource is not null && (InputRouteLocked(channel, null).ActiveInserts
            || _soundCheck is not null && _soundCheckChannel == channel.Id);

    // The exact name wins, as before, without asking for its ports on every
    // sweep. Only while it is missing does a capture input on a supported
    // unit follow the unit's card, through the bound strips' rule.
    private string? ResolveCaptureSourceLocked(ChannelDefinition channel, IReadOnlyList<AudioNode> sources)
    {
        List<AudioNode> candidates = [.. sources.Where(s => s.Kind == AudioNodeKind.Source && !s.IsOwn)];
        int matches = candidates.Count(s => s.Name == channel.CaptureSource);
        string? source;
        string? warning;
        if (matches == 0 && channel.CaptureUnit is not null)
            source = BoundSourceLocked(new SourceBinding(channel.CaptureSource!, channel.CapturePair, channel.CaptureMonoChannel)
                { Unit = channel.CaptureUnit }, candidates, out warning);
        else
        {
            warning = matches switch
            {
                0 => $"{channel.CaptureSource} is not connected. The channel stays silent until it returns.",
                1 => null,
                _ => $"More than one source is named {channel.CaptureSource}. The channel stays silent until the name is unique.",
            };
            source = warning is null ? channel.CaptureSource : null;
        }
        NoteCaptureLocked(channel.Id, warning);
        return source;
    }

    /// <summary>Transfer a capture channel back to its direct feed, before stopping its chain.</summary>
    private string? RestoreCaptureFeedLocked(ChannelDefinition channel)
    {
        string? source = ResolveCaptureSourceLocked(channel, _pw.ListDevices());
        PortLink? next = _captureFeeds.GetValueOrDefault(channel.Id);
        if (next is null || source is null || !next.Pairs.All(p => p.From.StartsWith(source + ":", StringComparison.Ordinal))
            || _pw.EnsureLinks(next) == LinkHealth.Broken)
        {
            next = source is null ? null : RouteInputLocked(source, channel.SinkName, channel);
            if (next is { Pairs.Count: 0 }) next = null;
            if (source is not null && next is null && _chains.TryGetValue(channel.Id, out FilterHandle? running) && running.IsAlive)
                throw new InvalidOperationException($"{source} could not be linked directly; the previous chain is still running.");
            if (_captureFeeds.Remove(channel.Id, out PortLink? old)) UnlinkDiscardedInputFeed(old, next);
            if (next is not null) _captureFeeds[channel.Id] = next;
            else if (source is not null)
                NoteCaptureLocked(channel.Id, $"{source} could not be linked to the channel; retrying.");
        }
        RemoveCaptureRouteLocked(channel.Id, next);
        if (next is not null) NoteCaptureLocked(channel.Id, null);
        return next is not null ? source : null;
    }

    private void RemoveCaptureRouteLocked(string id, PortLink? retained = null)
    {
        if (_inputFeeds.Remove(id, out PortLink? feed)) UnlinkDiscardedInputFeed(feed, retained);
        if (_chainOuts.Remove(id, out PortLink? onward)) _pw.Unlink(onward);
        if (_chains.Remove(id, out FilterHandle? chain)) _pw.StopFilter(chain);
        _stripSources.Remove(id);
        _inputHeads.Remove(id);
        _chainStructures.Remove(id);
    }

    private void RemoveCaptureFeedLocked(string id)
    {
        if (_captureFeeds.Remove(id, out PortLink? feed)) _pw.Unlink(feed);
    }

    /// <summary>
    /// Heal only missing capture routes. An absent device or pair stays silent;
    /// no other microphone takes its place. The ordinary sweep retries when
    /// the selected node or its ports return after a hotplug or profile change.
    /// </summary>
    private bool EnsureCaptureFeedsLocked()
    {
        bool changed = false;
        var channels = _config.Channels.Where(c => c.CaptureSource is not null).ToArray();
        if (channels.Length == 0)
        {
            bool cleared = _captureWarnings.Count > 0;
            _captureWarnings.Clear();
            return cleared;
        }
        var sources = _pw.ListDevices();
        foreach (ChannelDefinition channel in channels)
        {
            if (CaptureRouteLocked(channel))
            {
                string? before = _captureWarnings.GetValueOrDefault(channel.Id);
                string? source = ResolveCaptureSourceLocked(channel, sources);
                bool fed = _inputFeeds.TryGetValue(channel.Id, out PortLink? routedFeed);
                bool dead = source is not null && _chains.TryGetValue(channel.Id, out FilterHandle? chain) && !chain.IsAlive;
                if (dead) ChainDiedLocked(channel.Id, _chains[channel.Id]);
                if (dead || (source is not null) != fed || fed && (_stripSources.GetValueOrDefault(channel.Id) != source
                    || _pw.EnsureLinks(routedFeed!) == LinkHealth.Broken))
                {
                    WireInputFeedsLocked([channel.Id]);
                    changed = true;
                }
                changed |= before != _captureWarnings.GetValueOrDefault(channel.Id);
                continue;
            }
            if (_inputFeeds.ContainsKey(channel.Id) || _chains.ContainsKey(channel.Id))
            {
                try { RestoreCaptureFeedLocked(channel); }
                catch (Exception ex)
                {
                    changed |= NoteCaptureLocked(channel.Id, ex.Message);
                    continue;
                }
                // The failed hand-over that left the chain running is over.
                _insertErrors.Remove(channel.Id);
                changed = true;
            }
            string? warning = _captureWarnings.GetValueOrDefault(channel.Id);
            string? captureSource = ResolveCaptureSourceLocked(channel, sources);
            changed |= warning != _captureWarnings.GetValueOrDefault(channel.Id);
            if (_captureFeeds.TryGetValue(channel.Id, out PortLink? existing))
            {
                LinkHealth health = captureSource is not null && existing.Pairs.All(p => p.From.StartsWith(captureSource + ":", StringComparison.Ordinal))
                    ? _pw.EnsureLinks(existing) : LinkHealth.Broken;
                if (health != LinkHealth.Broken) { changed |= health == LinkHealth.Relinked; continue; }
                RemoveCaptureFeedLocked(channel.Id);
                changed = true;
            }
            if (captureSource is null) continue;
            // External sources need not use the hardware driver's capture_
            // prefix. Direction and the exact node name identify their ports.
            PortLink feed = _pw.LinkNodes(captureSource, "", channel.SinkName, "playback",
                fromPairOffset: channel.CapturePair, fromChannel: channel.CaptureMonoChannel);
            // Capture channels have stereo sinks; mono sources also need a
            // link to each side. Do not retain a partial route as healthy.
            if (!feed.Complete || feed.Pairs.Count < 2)
            {
                _pw.Unlink(feed);
                changed |= NoteCaptureLocked(channel.Id, $"{channel.CaptureSource} could not be linked to the channel; retrying.");
                continue;
            }
            _captureFeeds[channel.Id] = feed;
            NoteCaptureLocked(channel.Id, null);
            changed = true;
        }
        foreach (string gone in _captureWarnings.Keys.Where(id => !channels.Any(c => c.Id == id)).ToList())
            changed |= NoteCaptureLocked(gone, null);
        return changed;
    }

    /// <summary>Set or clear why a capture channel is silent. True when it changed.</summary>
    private bool NoteCaptureLocked(string id, string? warning)
    {
        if (_captureWarnings.GetValueOrDefault(id) == warning) return false;
        if (warning is null) _captureWarnings.Remove(id);
        else _captureWarnings[id] = warning;
        return true;
    }
}
