using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public Task CaptureSourceRebindMovesAudioAndMutesMonitorsWithoutReplacingTheChannel()
        => RebindCaptureSource(withInsert: false);

    [MonitorPipeWireFact]
    public Task CaptureSourceRebindKeepsItsChainAndEndsSoundCheck()
        => RebindCaptureSource(withInsert: true);

    private static async Task RebindCaptureSource(bool withInsert)
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("primary_signal", "Capture rebinding signal");
        pw.CreateVirtualMic("rebind_first", "primary_signal.monitor", "First microphone");
        pw.CreateVirtualMic("rebind_second", "primary_signal.monitor", "Second microphone");
        pw.CreateNullSink("soundcheck_output", "Capture rebinding monitor");
        mixer.Build(new MixerConfig
        {
            Channels = [new("system", "System"), new("capture", "Guest microphone")
                { CaptureSource = "rebind_first", CaptureMonoChannel = 0 },
                new("steady", "Unchanged microphone") { CaptureSource = "rebind_first", CaptureMonoChannel = 1 }],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("monitor2", "Second monitor", MixKind.Monitor),
                new("stream", "Stream", MixKind.VirtualMic)],
        });
        mixer.SetMonitorOutputs(["soundcheck_output"]);
        CaptureChainFixture.Wait(() => { mixer.EnsureInputFeeds(); mixer.EnsureCellLevels(); return Strip(mixer, "capture").BindingConnected; });
        foreach (string mix in new[] { "monitor", "monitor2", "stream" }) mixer.SetChannelMuted("steady", mix, true);
        mixer.SetExclusiveGroup(null, "Microphones", ["capture", "steady"], _ => null);
        // Sends are on the desktop's cubic scale. The captured output hears
        // Monitor A at unity, so the levels that must survive are set where
        // they keep the measured audio well clear of silence.
        mixer.SetLevel("capture", "monitor", 1);
        mixer.SetLevel("capture", "monitor2", .5);
        mixer.SetLevel("capture", "stream", .3);
        foreach (string mix in new[] { "monitor", "monitor2", "stream" }) mixer.SetChannelMuted("capture", mix, false);
        if (withInsert) mixer.SetInserts("capture", [CaptureChainFixture.Gain(1, .5)]);
        double gain = withInsert ? .5 : 1;
        MixerSettings before = mixer.ExportSettings();
        int? combine = pw.FindNodeId("OpenXLR_ch_capture");
        int? stream = pw.FindNodeId("OpenXLR_mix_stream");
        var otherLinks = GraphLinksInto("OpenXLR_ch_steady");
        using var stop = new CancellationTokenSource();
        Task<ProcessResult> signal = PrimarySignal(stop.Token);
        try
        {
            PrimarySound(.1 * gain);
            mixer.SoundCheck("capture", "record");
            CaptureChainFixture.Wait(() => mixer.Snapshot().SoundCheck.Seconds >= .5);
            mixer.SoundCheck("capture", "loop");
            CaptureChainFixture.Wait(() => mixer.Snapshot().SoundCheck.Mode == "looping");
            Assert.Throws<IOException>(() => mixer.SetCaptureSource("capture", "rebind_second", 0, _ => "disk full", 1));
            Assert.Equal("capture", mixer.SoundCheckChannel);
            Assert.Equal("rebind_first", Strip(mixer, "capture").CaptureSource);
            Assert.DoesNotContain("monitor", Strip(mixer, "capture").MutedIn);
            PrimarySound(.1 * gain);
            if (withInsert)
            {
                Assert.Throws<InvalidOperationException>(() => mixer.SetCaptureSource("capture", "rebind_second", 0, _ => null));
                Assert.Equal("capture", mixer.SoundCheckChannel);
            }

            MixerSettings? written = null;
            mixer.SetCaptureSource("capture", "rebind_second", 0, settings => { written = settings; return null; }, 1);
            Assert.Null(mixer.SoundCheckChannel);
            Assert.Equal("idle", mixer.Snapshot().SoundCheck.Mode);
            Assert.Equal("capture", mixer.Snapshot().SoundCheck.Channel);
            Assert.Contains("source changed", mixer.Snapshot().SoundCheck.Error);
            CaptureChainFixture.Wait(() => pw.FindNodeId("OpenXLR_soundcheck_capture") is null);
            ChannelStatus channel = Strip(mixer, "capture");
            Assert.Equal(new[] { "monitor", "monitor2" }, channel.MutedIn.Order());
            Assert.Equal(before.Levels, written!.Levels);
            Assert.Equal(before.ChannelVolumes, written.ChannelVolumes);
            Assert.Equal(JsonSerializer.Serialize(before.ExclusiveGroups), JsonSerializer.Serialize(written.ExclusiveGroups));
            Assert.Equal(JsonSerializer.Serialize(before.Inserts), JsonSerializer.Serialize(written.Inserts));
            Assert.Equal(combine, pw.FindNodeId("OpenXLR_ch_capture"));
            Assert.Equal(stream, pw.FindNodeId("OpenXLR_mix_stream"));
            Assert.Equal(otherLinks, GraphLinksInto("OpenXLR_ch_steady"));
            PrimarySound(0);
            mixer.SetChannelMuted("capture", "monitor", false);
            PrimarySound(.4 * gain);
            if (withInsert)
            {
                Assert.Null(Assert.Single(mixer.Snapshot().Inserts["capture"]).Error);
                Assert.NotEmpty(CaptureChainFixture.Nodes(pw, "capture"));
            }

            // A different port of the same node must replace a healthy direct feed too.
            mixer.SetCaptureSource("capture", "rebind_second", 0, _ => null, 0);
            Assert.Contains("monitor", Strip(mixer, "capture").MutedIn);
            mixer.SetChannelMuted("capture", "monitor", false);
            PrimarySound(.1 * gain);
            for (int i = 0; i < 3; i++) { mixer.EnsureInputFeeds(); mixer.EnsureFilterRoutes(); }
            PrimarySound(.1 * gain);

            // An unavailable port must not leave the previous microphone feeding the chain.
            mixer.SetCaptureSource("capture", "rebind_second", 0, _ => null, 63);
            Assert.False(Strip(mixer, "capture").BindingConnected);
            CaptureChainFixture.Wait(() => withInsert ? ChainFeed("rebind_second", "capture").Length == 0
                : GraphLinksInto("OpenXLR_ch_capture").Length == 0);
            mixer.SetChannelMuted("capture", "monitor", false);
            PrimarySound(0);

            // An offline selection disconnects the old source, and heals only the selected one.
            mixer.SetCaptureSource("capture", "rebind_missing", 0, _ => null, 1);
            Assert.False(Strip(mixer, "capture").BindingConnected);
            mixer.SetChannelMuted("capture", "monitor", false);
            PrimarySound(0);
            pw.CreateVirtualMic("rebind_missing", "primary_signal.monitor", "Replacement returned");
            CaptureChainFixture.Wait(() => { mixer.EnsureInputFeeds(); return Strip(mixer, "capture").BindingConnected; });
            PrimarySound(.4 * gain);
        }
        finally { stop.Cancel(); await signal; }
    }
}
