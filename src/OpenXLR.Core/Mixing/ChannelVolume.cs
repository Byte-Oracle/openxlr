namespace OpenXLR.Core.Mixing;

/// <summary>
/// A channel's own volume: one level for an application or capture channel
/// that scales every send it makes, the way a console's channel fader sits
/// before its auxiliary sends. It adds no node to the graph. Each send is
/// already one internal stream of the channel's combine sink with a volume of
/// its own, so the channel volume is folded into that number: the stream
/// carries the send level times the channel volume, times the mix master
/// where the mix has no desktop master of its own. Audio takes the same path
/// it always did, with no added latency.
///
/// The hardware inputs (XLR 1, XLR 2, Aux In) have none: their level is the
/// interface's own gain, which the window already draws in that place. The
/// channel's sink is never used for this either. It is held at full desktop
/// volume, and the sweep puts it back when anything else moves it.
/// </summary>
public sealed partial class Mixer
{
    // channel id -> volume; an absent channel is at unity
    private readonly Dictionary<string, double> _channelVolume = [];

    /// <summary>
    /// The volume a send's internal stream is given: the send level, the
    /// channel volume, and the mix master (unity for a monitor mix, whose
    /// master is its sink's own volume). The stream volume is on the desktop's
    /// cubic scale, where multiplying two values multiplies their gains, so
    /// halving the channel volume takes the same number of decibels off every
    /// send whatever its level.
    /// </summary>
    internal static double SendGain(double level, double channelVolume, double mixVolume)
        => Math.Clamp(level, 0, 1) * Math.Clamp(channelVolume, 0, 1) * Math.Clamp(mixVolume, 0, 1);

    /// <summary>Whether a channel takes a channel volume: an application or capture channel.</summary>
    private bool HasChannelVolumeLocked(string channelId)
        => _config.Channels.Any(c => c.Id == channelId && c.InputPair is null);

    private double ChannelVolumeLocked(string channelId) => _channelVolume.GetValueOrDefault(channelId, 1.0);

    /// <summary>The channel volume the state reports: null for a hardware input, which has none.</summary>
    private double? ChannelVolumeStatusLocked(ChannelDefinition channel)
        => channel.InputPair is null ? ChannelVolumeLocked(channel.Id) : null;

    /// <summary>
    /// The volume of an application or capture channel (0..1), applied to
    /// each of its sends at once. Mute stays per send. A hardware input or an
    /// unknown channel is left alone.
    /// </summary>
    public void SetChannelVolume(string channelId, double volume)
    {
        lock (_gate)
        {
            if (!HasChannelVolumeLocked(channelId)) return;
            _channelVolume[channelId] = Math.Clamp(volume, 0, 1);
            foreach (MixDefinition mix in _config.Mixes) ApplyCellLocked(channelId, mix.Id);
        }
    }

    /// <summary>Every application and capture channel's volume, for the settings file and a profile.</summary>
    private Dictionary<string, double> ExportChannelVolumesLocked()
        => _config.Channels.Where(c => c.InputPair is null).ToDictionary(c => c.Id, c => ChannelVolumeLocked(c.Id));

    /// <summary>
    /// Take the channel volumes a settings file or a profile carries. See
    /// <see cref="RecallChannelVolumes"/>. The caller pushes the sends.
    /// </summary>
    private void RecallChannelVolumesLocked(IReadOnlyDictionary<string, double>? saved, IEnumerable<string> describedCells)
        => RecallChannelVolumes(_channelVolume, _config.Channels.Where(c => c.InputPair is null).Select(c => c.Id),
            saved, describedCells);

    /// <summary>
    /// Set each channel's volume from saved data. A channel the data names
    /// takes its value, clamped to 0..1. A channel it does not name but whose
    /// sends it describes was saved before channel volumes existed, when every
    /// channel played at full volume, so it goes back to unity. A channel the
    /// data says nothing about, one added after it was written, keeps its
    /// volume. Entries for channels that do not exist, or that are hardware
    /// inputs, are ignored.
    /// </summary>
    internal static void RecallChannelVolumes(Dictionary<string, double> volumes, IEnumerable<string> channels,
        IReadOnlyDictionary<string, double>? saved, IEnumerable<string> describedCells)
    {
        var described = new HashSet<string>(StringComparer.Ordinal);
        foreach (string cell in describedCells)
        {
            int bar = cell.IndexOf('|', StringComparison.Ordinal);
            if (bar > 0) described.Add(cell[..bar]);
        }
        foreach (string channel in channels)
        {
            if (saved is not null && saved.TryGetValue(channel, out double value) && double.IsFinite(value))
                volumes[channel] = Math.Clamp(value, 0, 1);
            else if (described.Contains(channel))
                volumes.Remove(channel);
        }
    }
}
