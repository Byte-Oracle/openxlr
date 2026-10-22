using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void ACombinedPauseCannotMuteMicrophonesOrApplicationsAfterAFreshBuild()
    {
        MixerConfig config = MonitorConfig() with
        {
            Channels =
            [
                new("xlr1", "Mic") { InputPair = 0, Levels = new Dictionary<string, double> { ["chat"] = 1 } },
                new("test", "Application") { Levels = new Dictionary<string, double> { ["chat"] = 1 } },
            ],
        };
        using (var paused = new Mixer())
        {
            paused.Build(config with { Channels = [config.Channels[0]] });
            paused.SetMonitorMuteMicrophones(true);
            paused.SetMonitorMuted(true);
            Assert.True(new PipeWireAdapter().GetSinkMuted("OpenXLR_ch_xlr1"));
            SettleSends(paused);
            WaitForChatPost(paused);
            CaptureLevel(0, "OpenXLR_ch_xlr1", "OpenXLR_post_chat");
        }

        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var restarted = new Mixer(pw);
        restarted.Build(config);
        Assert.False(restarted.MonitorMuteMicrophones);
        Assert.False(restarted.MonitorMuted);
        SettleSends(restarted);
        WaitForChatPost(restarted);
        Assert.False(pw.GetSinkMuted("OpenXLR_ch_xlr1"));
        Assert.False(pw.GetSinkMuted("OpenXLR_ch_test"));
        CaptureLevel(.1, "OpenXLR_ch_xlr1", "OpenXLR_post_chat");
        CaptureLevel(.1, "OpenXLR_ch_test", "OpenXLR_post_chat");
    }

    [MonitorPipeWireFact]
    public void MicrophonePauseSurvivesDiskRecallAndDoesNotCoverStereoCaptures()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-microphone-pause-restart-").FullName;
        string path = Path.Combine(directory, "mixer.json");
        var config = MonitorConfig() with
        {
            Channels =
            [
                new("xlr1", "Mic") { InputPair = 0, Levels = new Dictionary<string, double> { ["chat"] = 1 } },
                new("mono", "Mono capture") { CaptureSource = "absent_mono", CaptureMonoChannel = 0, Levels = new Dictionary<string, double> { ["chat"] = 1 } },
                new("stereo", "Stereo capture") { CaptureSource = "absent_stereo", Levels = new Dictionary<string, double> { ["chat"] = 1 } },
                new("test", "Application"),
            ],
        };
        try
        {
            using (var original = new Mixer())
            {
                original.Build(config);
                original.SetChannelMuted("xlr1", "chat", true);
                original.SetMonitorMuteMicrophones(true);
                original.SetMonitorMuted(true);
                Assert.Null(original.ExportSettings().Save(path));
            }
            MixerSettings saved = MixerSettings.Load(path)!;
            var pw = new PipeWireAdapter();
            using var registry = pw.WatchGraph();
            using var restored = new Mixer(pw);
            restored.SetMonitorMuteMicrophones(saved.MonitorMuteMicrophones);
            restored.SetMonitorMuted(saved.MonitorMuted);
            restored.Build(MixerConfig.FromSettings(saved));
            restored.ApplySettings(saved);
            Assert.True(pw.GetSinkMuted("OpenXLR_ch_xlr1"));
            Assert.True(pw.GetSinkMuted("OpenXLR_ch_mono"));
            Assert.False(pw.GetSinkMuted("OpenXLR_ch_stereo"));
            Assert.False(restored.Snapshot().MicrophonePausePending);
            restored.ApplyScene(restored.ExportScene());
            Assert.True(restored.MonitorMuteMicrophones);
            Assert.True(restored.MonitorMuted);
            Assert.True(pw.GetSinkMuted("OpenXLR_ch_mono"));
            SettleSends(restored);
            WaitForChatPost(restored);
            CaptureLevel(0, "OpenXLR_ch_mono", "OpenXLR_post_chat");
            CaptureLevel(.1, "OpenXLR_ch_stereo", "OpenXLR_post_chat");
            restored.SetMonitorMuteMicrophones(false);
            Assert.True(restored.MonitorMuted);
            Assert.False(pw.GetSinkMuted("OpenXLR_ch_mono"));
            Assert.True(restored.IsChannelMutedIn("xlr1", "chat"));
            CaptureLevel(.1, "OpenXLR_ch_mono", "OpenXLR_post_chat");
        }
        finally { Directory.Delete(directory, true); }
    }

    [MonitorPipeWireFact]
    public void DeletingTheLastOpenPausedMonitorResumesMicrophonesOnlyAfterItsSave()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(MonitorConfig() with { Channels = [new("xlr1", "Mic") { InputPair = 0 }, new("test", "Application")] });
        mixer.SetMixMuted("monitor2", true);
        mixer.SetMonitorMuteMicrophones(true);
        mixer.SetMonitorMuted(true);
        Assert.Throws<IOException>(() => mixer.DeleteMix("monitor", _ => "disk full"));
        Assert.True(pw.GetSinkMuted("OpenXLR_ch_xlr1"));
        mixer.DeleteMix("monitor", _ => null);
        Assert.False(mixer.MonitorMuted);
        Assert.False(pw.GetSinkMuted("OpenXLR_ch_xlr1"));
        Assert.True(mixer.Snapshot().Mixes.Single(m => m.Id == "monitor2").Muted);
    }

    [MonitorPipeWireFact]
    public void OptingIntoTheMonitorPauseSilencesMicrophonesWithoutChangingTheirSends()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        MixerConfig config = MonitorConfig() with
        {
            Channels =
            [
                new("xlr1", "Mic") { InputPair = 0, Levels = new Dictionary<string, double> { ["monitor"] = 1, ["chat"] = 1 } },
                new("test", "Application") { Levels = new Dictionary<string, double> { ["monitor"] = 1, ["chat"] = 1 } },
            ],
        };
        mixer.Build(config);
        SettleSends(mixer);
        WaitForChatPost(mixer);

        // A combine's sink mute gates all its slaves without rewriting sends.
        pw.SetSinkMuted("OpenXLR_ch_xlr1", true);
        CaptureLevel(0, "OpenXLR_ch_xlr1", "OpenXLR_post_chat");
        pw.SetSinkMuted("OpenXLR_ch_xlr1", false);
        CaptureLevel(.1, "OpenXLR_ch_xlr1", "OpenXLR_post_chat");

        mixer.SetMonitorMuted(true);
        CaptureLevel(.1, "OpenXLR_ch_xlr1", "OpenXLR_post_chat");
        mixer.SetMonitorMuted(false);
        MixerSettings before = mixer.ExportSettings();
        var json = JsonSerializer.SerializeToNode(before, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        json["monitorMuteMicrophones"] = true;
        mixer.ApplySettings(json.Deserialize<MixerSettings>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!);
        mixer.SetMonitorMuted(true);

        CaptureLevel(0, "OpenXLR_ch_xlr1", "OpenXLR_post_chat");
        CaptureLevel(.1, "OpenXLR_ch_test", "OpenXLR_post_chat");
        Assert.Equal(before.ChannelMuted.Order(), mixer.ExportSettings().ChannelMuted.Order());
        Assert.Equal(before.MixMuted.Order(), mixer.ExportSettings().MixMuted.Order());
        Assert.Equal(before.Levels, mixer.ExportSettings().Levels);
        mixer.SetMonitorMuted(false);
        CaptureLevel(.1, "OpenXLR_ch_xlr1", "OpenXLR_post_chat");
    }
}
