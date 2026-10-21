using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The microphone EQ on a capture input, on a private PipeWire server: a mono
/// capture input playing a steady level, an EQ whose only band is a +6 dB low
/// shelf, which lifts a steady level by exactly 6 dB. The stage joins the
/// head of the input's route, ahead of its inserts, every edit is live, off
/// bypasses in place, a profile and a source change keep it, deleting the
/// input takes it away, and Sound Check records the raw microphone and loops
/// it through the EQ.
/// </summary>
public sealed partial class SoundCheckIntegrationTests
{
    /// <summary>A +6 dB lift, 10^(6/20).</summary>
    private const double Boost = 1.9952623149688795;

    /// <summary>On, band 1 a +6 dB low shelf at 1 kHz and every other band off.</summary>
    private static readonly MicEq BoostEq = MicEq.Default with
    {
        Enabled = true,
        Bands = [
            MicEq.Default.Bands[0] with { Type = MicEq.LowShelf, Frequency = 1000, Gain = 6 },
            MicEq.Default.Bands[1] with { Enabled = false },
            MicEq.Default.Bands[2] with { Enabled = false },
            MicEq.Default.Bands[3] with { Enabled = false },
        ],
    };

    [MonitorPipeWireFact]
    public async Task ACaptureInputsEqRunsAtTheHeadOfItsRouteAndEveryEditIsLive()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = BuildCapture(pw, out _);
        using var stop = new CancellationTokenSource();
        Task<ProcessResult> play = PlayCapture(.2, stop.Token);
        try
        {
            AssertCapture(.2);
            MicEqState state = CaptureEq(mixer);
            Assert.False(state.Enabled);
            Assert.Equal(MicEqProcessing.Software, state.Processing);
            Assert.Null(state.Device);
            Assert.Null(state.Backend);
            Assert.Empty(EqNodes(pw));

            // On: the stage takes the microphone's port and feeds the channel.
            mixer.SetEq("capture", BoostEq);
            string stage = WaitForEqStage(pw);
            Assert.Equal("software", CaptureEq(mixer).Backend);
            Assert.True(CaptureEq(mixer).Available);
            string[] links = CaptureLinks();
            Assert.Contains($"{CaptureSource} -> {stage}", links);
            Assert.Contains($"{stage} -> OpenXLR_ch_capture", links);
            Assert.DoesNotContain($"{CaptureSource} -> OpenXLR_ch_capture", links);
            AssertCapture(.2 * Boost);

            // An insert goes behind it, and the running stage stays.
            mixer.SetInserts("capture", [CaptureChainFixture.Gain(1, .5)]);
            string[] chain = CaptureChainFixture.WaitForChain(pw, "capture");
            Assert.Equal([stage], EqNodes(pw));
            links = CaptureLinks();
            Assert.Contains($"{CaptureSource} -> {stage}", links);
            Assert.Contains(links, l => l.StartsWith(stage + " -> OpenXLR_lc_capture_", StringComparison.Ordinal));
            Assert.DoesNotContain(links, l => l.StartsWith(CaptureSource + " -> OpenXLR_lc_capture_", StringComparison.Ordinal));
            AssertCapture(.1 * Boost);

            // Off bypasses inside the stage, which stays through sweeps; on again is the same stage.
            mixer.SetEqEnabled("capture", false);
            AssertCapture(.1);
            for (int i = 0; i < 3; i++) { mixer.EnsureInputFeeds(); mixer.EnsureFilterRoutes(); }
            Assert.Equal([stage], EqNodes(pw));
            Assert.Equal("software", CaptureEq(mixer).Backend);
            AssertCapture(.1);
            mixer.SetEqEnabled("capture", true);
            AssertCapture(.1 * Boost);
            mixer.SetEqBand("capture", 1, new(Gain: 0));
            AssertCapture(.1);
            mixer.SetEqBand("capture", 1, new(Gain: 6));
            AssertCapture(.1 * Boost);
            Assert.Equal([stage], EqNodes(pw));
            Assert.Equal(chain, CaptureChainFixture.Nodes(pw, "capture"));

            // A profile names it and brings it back; the settings carry it.
            MixerScene scene = mixer.ExportScene();
            Assert.Equal(MicEq.Snap(BoostEq), scene.Eq!["capture"]);
            mixer.SetEqEnabled("capture", false);
            AssertCapture(.1);
            mixer.ApplyScene(scene);
            AssertCapture(.1 * Boost);
            Assert.Equal(MicEq.Snap(BoostEq), mixer.ExportSettings().Eq["capture"]);

            // Another port of the same source keeps the EQ: port 0 carries .1.
            mixer.SetCaptureSource("capture", CaptureSource, 0, _ => null, monoChannel: 0);
            AssertCapture(.05 * Boost);
            Assert.Equal(MicEq.Snap(BoostEq), mixer.EqFor("capture"));
            Assert.Single(EqNodes(pw));

            // Deleting the input takes its stage and its EQ with it; a delete
            // whose save is refused keeps both.
            Assert.Throws<IOException>(() => mixer.DeleteApplicationChannel("capture", _ => "disk full"));
            Assert.Equal(MicEq.Snap(BoostEq), mixer.EqFor("capture"));
            Assert.Single(EqNodes(pw));
            MixerSettings? saved = null;
            mixer.DeleteApplicationChannel("capture", s => { saved = s; return null; });
            CaptureChainFixture.Wait(() => EqNodes(pw).Length == 0 && pw.FindNodeId("OpenXLR_ch_capture") is null);
            Assert.False(saved!.Eq.ContainsKey("capture"));
            Assert.False(mixer.ExportSettings().Eq.ContainsKey("capture"));
        }
        finally
        {
            stop.Cancel();
            await play;
        }
    }

    /// <summary>
    /// Sound Check on a capture input with its EQ on: the take is the raw
    /// microphone, so the loop is lifted once, not twice, and the loop goes
    /// through the EQ and follows every edit. Live and stop go back to the
    /// microphone through the same stage.
    /// </summary>
    [MonitorPipeWireFact]
    public async Task ACaptureTakeIsRawAndLoopsThroughTheEq()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = BuildCapture(pw, out _);
        mixer.SetEq("capture", BoostEq);
        using var firstStop = new CancellationTokenSource();
        using var secondStop = new CancellationTokenSource();
        Task<ProcessResult> first = PlayCapture(.2, firstStop.Token);
        Task<ProcessResult>? second = null;
        try
        {
            string stage = WaitForEqStage(pw);
            AssertCapture(.2 * Boost);
            mixer.SoundCheck("capture", "record");
            CaptureChainFixture.Wait(() =>
            {
                mixer.EnsureFilterRoutes(); mixer.EnsureCellLevels();
                return mixer.Snapshot().SoundCheck.Seconds >= .5;
            });
            Assert.Contains($"{CaptureSource} -> OpenXLR_soundcheck_capture", CaptureLinks());
            mixer.SoundCheck("capture", "loop");
            CaptureChainFixture.Wait(() => mixer.Snapshot().SoundCheck.Mode == "looping");
            CaptureChainFixture.Wait(() => CaptureLinks().Contains($"OpenXLR_soundcheck_capture -> {stage}"),
                () => string.Join("\n", CaptureLinks()));
            Assert.DoesNotContain($"{CaptureSource} -> {stage}", CaptureLinks());
            Assert.Equal([stage], EqNodes(pw));
            firstStop.Cancel(); await first;
            second = PlayCapture(.3, secondStop.Token);

            // Lifted once: a take recorded after the EQ would come back at .2 x Boost x Boost.
            AssertCapture(.2 * Boost);
            for (int i = 0; i < 3; i++) { mixer.EnsureInputFeeds(); mixer.EnsureFilterRoutes(); }
            AssertCapture(.2 * Boost);
            mixer.SetEqBand("capture", 1, new(Gain: 0));
            AssertCapture(.2);
            mixer.SetEqEnabled("capture", false);
            mixer.SetEqBand("capture", 1, new(Gain: 6));
            AssertCapture(.2);
            mixer.SetEqEnabled("capture", true);
            AssertCapture(.2 * Boost);

            mixer.SoundCheck("capture", "live");
            AssertCapture(.3 * Boost);
            mixer.SoundCheck("capture", "loop");
            AssertCapture(.2 * Boost);
            mixer.SoundCheck("capture", "stop");
            AssertCapture(.3 * Boost);
            CaptureChainFixture.Wait(() => pw.FindNodeId("OpenXLR_soundcheck_capture") is null);
            Assert.Equal([stage], EqNodes(pw));
            Assert.Contains($"{CaptureSource} -> {stage}", CaptureLinks());
            Assert.DoesNotContain($"{CaptureSource} -> OpenXLR_ch_capture", CaptureLinks());
        }
        finally
        {
            firstStop.Cancel(); secondStop.Cancel();
            await first;
            if (second is not null) await second;
        }
    }

    private static MicEqState CaptureEq(Mixer mixer) => mixer.Snapshot().Channels.Single(c => c.Id == "capture").Eq!;

    private static string[] EqNodes(PipeWireAdapter pw)
        => [.. pw.DumpNodes().Select(n => n.Name).Where(n => n.StartsWith("OpenXLR_eq_capture_", StringComparison.Ordinal)).Order()];

    private static string WaitForEqStage(PipeWireAdapter pw)
    {
        CaptureChainFixture.Wait(() => EqNodes(pw).Length == 1, () => string.Join("\n", CaptureLinks()));
        return EqNodes(pw)[0];
    }
}
