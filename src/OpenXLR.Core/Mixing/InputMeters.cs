using System.Collections.Concurrent;
using System.Diagnostics;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The level of a microphone before its chain, for Mic check. A client asks
/// for one and keeps asking while it shows it; the meter is a native level
/// helper linked from the channel's own capture port beside the channel's
/// route, which it leaves untouched, and it goes away a lease after the last
/// request. The sweep follows the channel's source and drops a meter whose
/// helper died, so the next request starts a fresh one.
/// </summary>
public sealed partial class Mixer
{
    /// <summary>How long a meter runs after the last request for it.</summary>
    internal static readonly TimeSpan InputMeterLease = TimeSpan.FromSeconds(15);

    private sealed class InputMeter(FilterHandle stage)
    {
        public FilterHandle Stage { get; } = stage;
        public PortLink? Feed { get; set; }
        public string? Device { get; set; }
        public long Until { get; set; }
    }

    private readonly Dictionary<string, InputMeter> _inputMeters = new(StringComparer.Ordinal);
    // The same helpers for the meter push, which reads them without the lock,
    // the clip count each one showed at the last push and when it last rose.
    private readonly ConcurrentDictionary<string, NativePluginHost> _inputMeterHosts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _inputMeterClips = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _inputMeterClipped = new(StringComparer.Ordinal);

    /// <summary>
    /// Run the meter before this microphone's chain for another
    /// <see cref="InputMeterLease"/>, starting it when none runs. Its level
    /// arrives with the other meters as <c>in:</c> and the channel. Never
    /// saved; nothing in the channel's route changes.
    /// </summary>
    public void WatchInputMeter(string channel)
    {
        lock (_gate)
        {
            if (!IsSoundCheckChannel(channel)) throw new ArgumentException("The input meter needs a microphone channel.");
            if (!_built) throw new InvalidOperationException("The input meter needs the software mixer to be running.");
            long until = Stopwatch.GetTimestamp() + (long)(InputMeterLease.TotalSeconds * Stopwatch.Frequency);
            if (_inputMeters.TryGetValue(channel, out InputMeter? running))
            {
                running.Until = until;
                return;
            }
            FilterHandle stage = _pw.CreateInputMeter(channel);
            var meter = new InputMeter(stage) { Until = until };
            _inputMeters[channel] = meter;
            _inputMeterHosts[channel] = stage.NativeHost!;
            _inputMeterClips[channel] = 0;
            LinkInputMeterLocked(channel, meter);
        }
    }

    /// <summary>
    /// Feed a meter from the channel's source as it is now. A healthy link
    /// to the same source is kept; a source that is missing leaves the meter
    /// silent until the sweep finds it again.
    /// </summary>
    private void LinkInputMeterLocked(string channel, InputMeter meter)
    {
        ChannelDefinition input = _config.Channels.Single(c => c.Id == channel);
        string? device = MicrophoneSourceLocked(input);
        if (meter.Feed is not null && meter.Device == device && _pw.EnsureLinks(meter.Feed) != LinkHealth.Broken) return;
        if (meter.Feed is not null) _pw.Unlink(meter.Feed);
        meter.Feed = null;
        meter.Device = device;
        if (device is null) return;
        PortLink feed = RouteInputLocked(device, meter.Stage.SinkName, input);
        if (feed.Pairs.Count == 1) meter.Feed = feed;
        else _pw.Unlink(feed);
    }

    /// <summary>The sweep's part: retire meters past their lease or with a dead helper, and follow each channel's source.</summary>
    private void EnsureInputMetersLocked()
    {
        long now = Stopwatch.GetTimestamp();
        foreach ((string channel, InputMeter meter) in _inputMeters.ToList())
        {
            if (now >= meter.Until || !meter.Stage.IsAlive || !_config.Channels.Any(c => c.Id == channel && c.Microphone))
                RemoveInputMeterLocked(channel);
            else LinkInputMeterLocked(channel, meter);
        }
    }

    private void RemoveInputMeterLocked(string channel)
    {
        if (!_inputMeters.Remove(channel, out InputMeter? meter)) return;
        _inputMeterHosts.TryRemove(channel, out _);
        _inputMeterClips.TryRemove(channel, out _);
        _inputMeterClipped.TryRemove(channel, out _);
        if (meter.Feed is not null) _pw.Unlink(meter.Feed);
        _pw.StopFilter(meter.Stage);
    }

    private void RemoveInputMetersLocked()
    {
        foreach (string channel in _inputMeters.Keys.ToList()) RemoveInputMeterLocked(channel);
    }

    /// <summary>The channels whose meter before the chain runs now.</summary>
    public IReadOnlyCollection<string> InputMeterChannels { get { lock (_gate) return [.. _inputMeters.Keys]; } }

    /// <summary>How long a clip stays in the pushed meters, so a client that skips a frame still sees it.</summary>
    internal static readonly TimeSpan InputClipHold = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Each running meter as <c>[rms, peak, clip]</c>: the first two on the
    /// same 0 to 1 scale as the channel meters (-60 to 0 dBFS), the third 1
    /// for <see cref="InputClipHold"/> after a sample reached the clip level,
    /// else 0.
    /// </summary>
    private void AddInputLevels(Dictionary<string, double[]> meters)
    {
        long now = Stopwatch.GetTimestamp();
        foreach ((string channel, NativePluginHost host) in _inputMeterHosts)
        {
            if (!host.IsHealthy) continue;
            IReadOnlyDictionary<string, double> values = host.Meters;
            double clips = values.GetValueOrDefault("clips");
            if (clips > _inputMeterClips.GetValueOrDefault(channel))
            {
                _inputMeterClips[channel] = clips;
                _inputMeterClipped[channel] = now;
            }
            bool clipped = _inputMeterClipped.TryGetValue(channel, out long at) && Stopwatch.GetElapsedTime(at, now) < InputClipHold;
            meters[$"in:{channel}"] = [MeterScale(values.GetValueOrDefault("rms", -120)),
                MeterScale(values.GetValueOrDefault("peak", -120)), clipped ? 1 : 0];
        }
    }

    /// <summary>dBFS on the meters' scale: 0 at -60 dBFS and below, 1 at 0 dBFS.</summary>
    internal static double MeterScale(double dbfs)
        => double.IsFinite(dbfs) ? Math.Round(Math.Clamp((dbfs + 60) / 60, 0, 1), 3) : 0;
}
