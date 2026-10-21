using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void DesktopMuteOnAnApplicationSinkPausesOnlyMonitoringAndTheSecondPressRestoresIt()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(MonitorConfig());
        pw.CreateNullSink("mute_output", "Monitor mute test");
        mixer.SetMonitorOutputs(["mute_output"]);
        mixer.SetMixMuted("monitor2", true);
        mixer.SetLevel("test", "monitor", 1);
        mixer.SetLevel("test", "chat", 1);
        pw.SetDefaultSink("OpenXLR_ch_test");
        mixer.SyncOwnSinkLevels(out _);
        mixer.SyncDeviceVolumes();
        MixerSettings before = mixer.ExportSettings();

        Press();
        CaptureLevel(0, "OpenXLR_ch_test", "mute_output");
        CaptureLevel(0.1, "OpenXLR_ch_test", "OpenXLR_post_chat");
        Press();
        CaptureLevel(0.1, "OpenXLR_ch_test", "mute_output");
        Assert.Equal(before.MixMuted.Order(), mixer.ExportSettings().MixMuted.Order());
        Assert.Equal(before.ChannelMuted.Order(), mixer.ExportSettings().ChannelMuted.Order());
        Assert.Equal(before.Levels, mixer.ExportSettings().Levels);

        void Press()
        {
            ProcessResult result = ProcessRunner.Run("pactl", ["set-sink-mute", "@DEFAULT_SINK@", "toggle"]);
            Assert.True(result.Ok, result.Stderr);
            Assert.True(SpinWait.SpinUntil(() => pw.GetSinkMuted("OpenXLR_ch_test"), TimeSpan.FromSeconds(3)));
            Assert.True(mixer.SyncOwnSinkLevels(out _));
            Assert.False(pw.GetSinkMuted("OpenXLR_ch_test"));
            mixer.SyncDeviceVolumes();
            for (int sweep = 0; sweep < 3; sweep++)
            {
                Assert.False(mixer.SyncOwnSinkLevels(out _));
                mixer.EnsureMonitorRoutes();
            }
        }
    }

    [MonitorPipeWireFact]
    public void ASelectedOutputMuteKeyKeepsAnAlreadyMutedFeedClosed()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(MonitorConfig());
        pw.CreateNullSink("mute_sum", "Monitor sum mute test");
        mixer.SetMonitorOutputs(["mute_sum"]);
        Assert.Null(mixer.SetMonitorFeed("mute_sum", "monitor+monitor2"));
        mixer.SetMixMuted("monitor2", true);

        mixer.ToggleOutputMute("mute_sum");
        mixer.ToggleOutputMute("mute_sum");

        Assert.True(mixer.Snapshot().Mixes.Single(m => m.Id == "monitor2").Muted);
        Assert.False(mixer.Snapshot().Mixes.Single(m => m.Id == "monitor").Muted);
    }

    [MonitorPipeWireFact]
    public void NativePhysicalMuteTogglesTheWholeMonitorWithoutChangingRecordingOrPriorMutes()
        => CheckNativePhysicalMute(firstWasMuted: false);

    [MonitorPipeWireFact]
    public void NativeMuteOnAnAlreadyMutedOutputKeepsThatOutputMutedWhenMonitoringResumes()
        => CheckNativePhysicalMute(firstWasMuted: true);

    private static void CheckNativePhysicalMute(bool firstWasMuted)
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(MonitorConfig());
        pw.CreateNullSink("mute_first", "Monitor first");
        pw.CreateNullSink("mute_closed", "Monitor already muted");
        pw.SetSinkMuted("mute_first", firstWasMuted);
        pw.SetSinkMuted("mute_closed", true);
        mixer.SetMonitorOutputs(["mute_first", "mute_closed"]);
        mixer.SetMixMuted("monitor2", true);
        mixer.SetLevel("test", "monitor", 1);
        mixer.SetLevel("test", "chat", 1);
        pw.SetDefaultSink("mute_first");
        mixer.SyncDeviceVolumes();

        foreach (bool paused in new[] { true, false, true, false })
        {
            Assert.True(ProcessRunner.Run("pactl", ["set-sink-mute", "@DEFAULT_SINK@", "toggle"]).Ok);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.SyncOwnSinkLevels(out _);
                mixer.SyncDeviceVolumes();
                return mixer.MonitorMuted == paused;
            }, TimeSpan.FromSeconds(3)));
            Assert.Equal(firstWasMuted, pw.GetSinkMuted("mute_first"));
            Assert.True(pw.GetSinkMuted("mute_closed"));
            Assert.True(mixer.Snapshot().Mixes.Single(m => m.Id == "monitor2").Muted);
            Assert.False(mixer.SyncOwnSinkLevels(out _));
            Assert.False(mixer.SyncDeviceVolumes());
            mixer.EnsureMonitorRoutes();
        }
        mixer.SetMonitorMuted(true);
        CaptureLevel(0, "OpenXLR_ch_test", "mute_first");
        CaptureLevel(0.1, "OpenXLR_ch_test", "OpenXLR_post_chat");
        mixer.SetMonitorMuted(false);
        CaptureLevel(firstWasMuted ? 0 : 0.1, "OpenXLR_ch_test", "mute_first");
    }

    [MonitorPipeWireFact]
    public void ABlanketDesktopMuteIsOnePulseAndPreservesMutedMixesAndOutputs()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(MonitorConfig());
        pw.CreateNullSink("mute_blanket_first", "Monitor blanket first");
        pw.CreateNullSink("mute_blanket_closed", "Monitor blanket closed");
        pw.SetSinkMuted("mute_blanket_closed", true);
        mixer.SetMonitorOutputs(["mute_blanket_first", "mute_blanket_closed"]);
        mixer.SetMixMuted("monitor2", true);
        mixer.SyncOwnSinkLevels(out _);
        mixer.SyncDeviceVolumes();

        foreach (bool paused in new[] { true, false })
        {
            foreach (string sink in pw.OwnSinkLevels().Select(s => s.Name).Concat(["mute_blanket_first", "mute_blanket_closed"]))
                pw.SetSinkMuted(sink, true);
            Assert.True(mixer.SyncOwnSinkLevels(out _));
            mixer.SyncDeviceVolumes();
            Assert.Equal(paused, mixer.MonitorMuted);
            Assert.Equal(["monitor2"], mixer.ExportSettings().MixMuted);
            Assert.True(pw.GetSinkMuted("OpenXLR_mix_monitor2"));
            Assert.False(pw.GetSinkMuted("OpenXLR_mix_monitor"));
            Assert.False(pw.GetSinkMuted("mute_blanket_first"));
            Assert.True(pw.GetSinkMuted("mute_blanket_closed"));
            Assert.False(mixer.SyncOwnSinkLevels(out _));
            Assert.False(mixer.SyncDeviceVolumes());
        }
    }

    [MonitorPipeWireFact]
    public void PausedMonitoringSurvivesFeedSelectionAndProfileChangesAndSettingsRecall()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.SetMonitorMuted(true); // Startup restores the pause before building any routes.
        mixer.Build(MonitorConfig());
        Assert.True(mixer.MonitorMuted);
        pw.CreateNullSink("mute_old_output", "Monitor old");
        pw.CreateNullSink("mute_new_output", "Monitor new");
        mixer.SetMonitorOutputs(["mute_old_output"]);
        mixer.SetLevel("test", "chat", 1);
        mixer.SetMonitorMuted(true);
        Assert.Null(mixer.SetMonitorFeed("mute_old_output", "chat"));
        mixer.SetMonitorOutputs(["mute_new_output"]);
        Assert.Null(mixer.SetMonitorFeed("mute_new_output", "chat"));
        mixer.SetOutputVolume(1.5);
        mixer.ApplyScene(mixer.ExportScene());
        Assert.True(mixer.MonitorMuted);
        Assert.False(mixer.EnsureMonitorRoutes());
        CaptureLevel(0, "OpenXLR_ch_test", "mute_new_output");
        CaptureLevel(0.1, "OpenXLR_ch_test", "OpenXLR_post_chat");
        MixerSettings saved = mixer.ExportSettings();
        Assert.True(saved.MonitorMuted);
        mixer.SetMonitorMuted(false);
        mixer.ApplySettings(saved);
        Assert.True(mixer.MonitorMuted);
        Assert.False(mixer.EnsureMonitorRoutes());
        mixer.ApplySettings(saved with { MonitorMuted = false });
        Assert.False(mixer.MonitorMuted);
        CaptureLevel(0.1 * Math.Pow(1.5, 3), "OpenXLR_ch_test", "mute_new_output");
    }

    [MonitorPipeWireFact]
    public void AnOutputReturningDuringMonitorMuteStaysSilentUntilTheMasterResumes()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(MonitorConfig());
        uint output = pw.CreateNullSink("mute_returning", "Returning output");
        mixer.SetMonitorOutputs(["mute_returning"]);
        mixer.SetLevel("test", "monitor", 1);
        mixer.SyncDeviceVolumes();
        mixer.SetMonitorMuted(true);
        pw.UnloadModule(output);
        Assert.True(SpinWait.SpinUntil(() => pw.GetSinkVolume("mute_returning") is null, TimeSpan.FromSeconds(3)));
        mixer.SyncDeviceVolumes();
        pw.CreateNullSink("mute_returning", "Returning output");
        Assert.True(SpinWait.SpinUntil(() => pw.GetSinkVolume("mute_returning") is not null, TimeSpan.FromSeconds(3)));
        mixer.SyncDeviceVolumes();
        Assert.True(mixer.MonitorMuted);
        Assert.False(mixer.EnsureMonitorRoutes());
        CaptureLevel(0, "OpenXLR_ch_test", "mute_returning");
        mixer.SetMonitorMuted(false);
        CaptureLevel(0.1, "OpenXLR_ch_test", "mute_returning");
    }
}
