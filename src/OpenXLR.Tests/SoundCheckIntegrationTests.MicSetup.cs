using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class SoundCheckIntegrationTests
{
    /// <summary>
    /// What Mic setup adds to a session, on a real graph: the meter before
    /// the chain beside the untouched route, the take's waveform once, the
    /// raw path past the chain on the same take without a chain rebuild, and
    /// Hear myself opening a muted monitor send for the session only.
    /// </summary>
    [MonitorPipeWireFact]
    public async Task MicSetupMetersTheInputSwitchesRawAndProcessedAndHearsItselfForTheSessionOnly()
    {
        const string channel = "xlr1";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("soundcheck_signal", "Synthetic mic signal");
        pw.CreateVirtualMic("Wave_XLR_soundcheck_input", "soundcheck_signal.monitor", "Synthetic microphone");
        pw.CreateNullSink("soundcheck_output", "Sound Check test output");
        mixer.SetInputDeviceHint("Wave_XLR_soundcheck_input");
        mixer.Build(new MixerConfig
        {
            Mixes = [new("monitor", "Monitor", MixKind.Monitor)],
            Channels = [new(channel, "Microphone") { InputPair = 0, Levels = new Dictionary<string, double> { ["monitor"] = 1 } },
                new("xlr2", "Second microphone") { InputPair = 1 }],
        });
        mixer.SetMonitorOutputs(["soundcheck_output"]);
        using var stop = new CancellationTokenSource();
        Task<ProcessResult> generator = Play(.25, stop.Token);
        try
        {
            AssertSound(.25);

            // The meter before the chain: a parallel tap of the same port.
            mixer.WatchInputMeter(channel);
            Assert.Equal([channel], mixer.InputMeterChannels);
            double expected = Mixer.MeterScale(20 * Math.Log10(.25));
            Assert.True(SpinWait.SpinUntil(() => mixer.ReadMeters().TryGetValue("in:" + channel, out double[]? level)
                && Math.Abs(level[1] - expected) < .02 && Math.Abs(level[0] - expected) < .03 && level[2] == 0,
                TimeSpan.FromSeconds(5)), string.Join(" ", mixer.ReadMeters().Select(m => $"{m.Key}={string.Join(",", m.Value)}")));
            AssertSound(.25);
            mixer.WatchInputMeter(channel);   // a renewal keeps the same helper
            Assert.NotNull(pw.FindNodeId("OpenXLR_level_" + channel));

            mixer.SoundCheck(channel, "record");
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureFilterRoutes(); mixer.EnsureCellLevels();
                return mixer.Snapshot().SoundCheck.Seconds >= .5;
            }, TimeSpan.FromSeconds(5)));
            Assert.Null(mixer.CurrentSoundCheckTake());
            mixer.SoundCheck(channel, "loop");
            Assert.True(SpinWait.SpinUntil(() => mixer.CurrentSoundCheckTake() is not null, TimeSpan.FromSeconds(3)));
            SoundCheckTake take = mixer.CurrentSoundCheckTake()!;
            Assert.Equal(channel, take.Channel);
            Assert.Equal(1, take.Take);
            Assert.Equal(PipeWireAdapter.TakePeakCount, take.Peaks.Count);
            Assert.True(take.Peaks.Count(p => Math.Abs(p - .25) < .02) > take.Peaks.Count * 9 / 10, string.Join(",", take.Peaks));
            Assert.Equal(1, mixer.Snapshot().SoundCheck.Take);
            Assert.Same(take, mixer.NewSoundCheckTake());
            Assert.Null(mixer.NewSoundCheckTake());
            Assert.True(SpinWait.SpinUntil(() => mixer.ReadMeters().TryGetValue("take:" + channel, out double[]? at) && at[0] is >= 0 and <= 1,
                TimeSpan.FromSeconds(3)));

            // The live microphone changes; the loop still plays the take.
            stop.Cancel(); await generator;
            using var second = new CancellationTokenSource();
            generator = Play(.75, second.Token);
            AssertSound(.25);

            // A low cut takes the steady take to silence; raw plays it past
            // the chain, and processed puts the chain back, all at once.
            mixer.SetLowCutHz(120);
            AssertSound(0);
            object chain = Chains(mixer)[channel]!;
            mixer.SetSoundCheckRaw(channel, true);
            Assert.True(mixer.Snapshot().SoundCheck.Raw);
            AssertSound(.25);
            mixer.SetSoundCheckRaw(channel, false);
            AssertSound(0);
            Assert.Same(chain, Chains(mixer)[channel]);   // the switch rebuilt nothing
            mixer.SetLowCutHz(0);
            AssertSound(.25);

            // Hear myself opens the muted monitor send, at unity when the send
            // was pulled down, and closes it again.
            mixer.SetChannelMuted(channel, "monitor", true);
            AssertSound(0);
            Assert.False(mixer.Snapshot().SoundCheck.Heard);
            mixer.SetSoundCheckHearMyself(channel, true);
            AssertSound(.25);
            Assert.True(mixer.Snapshot().SoundCheck.HearMyself);
            Assert.True(mixer.Snapshot().SoundCheck.Heard);
            mixer.SetLevel(channel, "monitor", 0);
            AssertSound(.25);
            mixer.SetLevel(channel, "monitor", 1);
            mixer.SetSoundCheckHearMyself(channel, false);
            AssertSound(0);
            mixer.SetSoundCheckHearMyself(channel, true);
            AssertSound(.25);

            // The end of the session ends both, and the mutes are the user's again.
            mixer.SoundCheck(channel, "stop");
            AssertSound(0);
            Assert.False(mixer.Snapshot().SoundCheck.HearMyself);
            Assert.False(mixer.Snapshot().SoundCheck.Raw);
            Assert.Null(mixer.CurrentSoundCheckTake());
            mixer.SetChannelMuted(channel, "monitor", false);
            AssertSound(.75);

            // Raw on a channel with no session starts one on the live microphone.
            mixer.SetSoundCheckRaw(channel, true);
            Assert.Equal("live", mixer.Snapshot().SoundCheck.Mode);
            Assert.Equal(channel, mixer.SoundCheckChannel);
            AssertSound(.75);
            mixer.SoundCheck(channel, "stop");
            AssertSound(.75);

            // The meter outlives none of it: past its lease the sweep retires it.
            Assert.Equal([channel], mixer.InputMeterChannels);
            mixer.Build(mixer.Config);
            Assert.Empty(mixer.InputMeterChannels);
            Assert.True(SpinWait.SpinUntil(() => pw.FindNodeId("OpenXLR_level_" + channel) is null, TimeSpan.FromSeconds(3)));
            second.Cancel();
        }
        finally
        {
            stop.Cancel();
            await generator;
        }

        void AssertSound(double expected)
        {
            mixer.EnsureCellLevels();
            var result = CaptureSound(expected);
            Assert.True(result.Ok, result.StdoutText + result.Stderr);
        }

        static Task<ProcessResult> Play(double value, CancellationToken cancel) => ProcessRunner.RunAsync("python3", ["-c", """
            import subprocess, struct, sys
            args = ['--format=f32', '--rate=48000', '--channels=2']
            if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
            p = subprocess.Popen(['pw-cat', '--playback', *args, '--properties={ node.link-group = openxlr-test-playback }', '--target', 'soundcheck_signal', '-'], stdin=subprocess.PIPE)
            try: p.communicate(struct.pack('<ff', *([float(sys.argv[1])] * 2)) * (48000 * 45), timeout=50)
            finally:
                if p.poll() is None: p.kill()
                p.wait()
            """, value.ToString(System.Globalization.CultureInfo.InvariantCulture)], TimeSpan.FromSeconds(55), cancel: cancel);
    }

    /// <summary>The meter's lease runs out in the sweep, and the helper goes with it.</summary>
    [MonitorPipeWireFact]
    public void AnInputMeterNobodyRenewsIsRetiredByTheSweep()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("lease_signal", "Synthetic mic signal");
        pw.CreateVirtualMic("Wave_XLR_lease_input", "lease_signal.monitor", "Synthetic microphone");
        mixer.SetInputDeviceHint("Wave_XLR_lease_input");
        mixer.Build(new MixerConfig
        {
            Mixes = [new("monitor", "Monitor", MixKind.Monitor)],
            Channels = [new("xlr1", "Microphone") { InputPair = 0 }],
        });
        mixer.WatchInputMeter("xlr1");
        Assert.NotNull(pw.FindNodeId("OpenXLR_level_xlr1"));
        mixer.EnsureFilterRoutes();
        Assert.Equal(["xlr1"], mixer.InputMeterChannels);
        ExpireInputMeters(mixer);
        mixer.EnsureFilterRoutes();
        Assert.Empty(mixer.InputMeterChannels);
        Assert.DoesNotContain("in:xlr1", mixer.ReadMeters().Keys);
        Assert.True(SpinWait.SpinUntil(() => pw.FindNodeId("OpenXLR_level_xlr1") is null, TimeSpan.FromSeconds(3)));
    }

    private static System.Collections.IDictionary Chains(Mixer mixer) => (System.Collections.IDictionary)typeof(Mixer)
        .GetField("_chains", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(mixer)!;

    private static void ExpireInputMeters(Mixer mixer)
    {
        var meters = (System.Collections.IDictionary)typeof(Mixer)
            .GetField("_inputMeters", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(mixer)!;
        foreach (object meter in meters.Values)
            meter.GetType().GetProperty("Until")!.SetValue(meter, System.Diagnostics.Stopwatch.GetTimestamp() - 1);
    }
}
