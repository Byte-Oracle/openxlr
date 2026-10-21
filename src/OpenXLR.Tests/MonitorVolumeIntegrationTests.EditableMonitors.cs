using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void BuiltInMonitorDeletionRewiresImplicitOutputsAndRollsBackFailedSaves()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        var outputs = new PipeWireAdapter();
        using var mixer = new Mixer(pw);
        try
        {
            foreach (string name in new[] { "edit_implicit", "edit_sum", "edit_explicit" })
                outputs.CreateNullSink(name, name);
            mixer.Build(MonitorConfig());
            mixer.SetMonitorOutputs(["edit_implicit", "edit_sum", "edit_explicit"]);
            Assert.Null(mixer.SetMonitorFeed("edit_sum", "monitor+chat"));
            Assert.Null(mixer.SetMonitorFeed("edit_explicit", "monitor2"));
            mixer.RenameMix("monitor", "Speakers", _ => null);
            mixer.SetEnforcedDefaults("OpenXLR_mix_monitor", null);
            AssertEditableMonitorFeeds(mixer, "edit_implicit", "monitor");
            Assert.Throws<IOException>(() => mixer.DeleteMix("monitor", _ => "disk full"));
            Assert.True(mixer.HasMix("monitor"));
            Assert.Equal("Speakers", mixer.Snapshot().Mixes.Single(m => m.Id == "monitor").Name);
            Assert.Equal("OpenXLR_mix_monitor", mixer.EnforcedDefaults.Sink);
            AssertEditableMonitorFeeds(mixer, "edit_implicit", "monitor");
            AssertEditableMonitorFeeds(mixer, "edit_sum", "monitor", "chat");

            MixerSettings? saved = null;
            mixer.DeleteMix("monitor", settings => { saved = settings; return null; });
            Assert.False(mixer.HasMix("monitor"));
            Assert.Equal("monitor2", mixer.Snapshot().DefaultMonitorMix);
            Assert.Null(mixer.EnforcedDefaults.Sink);
            Assert.DoesNotContain(saved!.Mixes!, m => m.Id == "monitor");
            Assert.DoesNotContain(saved.Levels, p => p.Key.EndsWith("|monitor", StringComparison.Ordinal));
            Assert.DoesNotContain(saved.Inserts.Keys, key => key == "mix:monitor");
            AssertEditableMonitorFeeds(mixer, "edit_implicit", "monitor2");
            AssertEditableMonitorFeeds(mixer, "edit_sum", "chat");
            AssertEditableMonitorFeeds(mixer, "edit_explicit", "monitor2");
            Assert.False(mixer.Snapshot().Mixes.Single(m => m.Id == "monitor2").Deletable);
            bool persisted = false;
            Assert.Throws<InvalidOperationException>(() => mixer.DeleteMix("monitor2", _ => { persisted = true; return null; }));
            Assert.False(persisted);
            CaptureLevel(0.1, "OpenXLR_mix_monitor2", "edit_implicit");
            mixer.Build(MixerConfig.FromSettings(saved));
            mixer.ApplySettings(saved);
            Assert.False(mixer.HasMix("monitor"));
            AssertEditableMonitorFeeds(mixer, "edit_implicit", "monitor2");
        }
        finally { outputs.TearDown(); }
    }

    [MonitorPipeWireFact]
    public void CreatingAnEarlierMonitorPinsExistingImplicitOutputsAndRollsBackThatPin()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        var outputs = new PipeWireAdapter();
        using var mixer = new Mixer(pw);
        try
        {
            outputs.CreateNullSink("edit_new_default", "New default output");
            mixer.Build(MonitorConfig().WithoutMix("monitor"));
            mixer.SetMonitorOutputs(["edit_new_default"]);
            AssertEditableMonitorFeeds(mixer, "edit_new_default", "monitor2");
            Assert.Throws<IOException>(() => mixer.CreateMix("A speakers", _ => "disk full", MixKind.Monitor));
            Assert.False(mixer.ExportSettings().MonitorFeeds.ContainsKey("edit_new_default"));
            Assert.Equal("monitor2", mixer.Snapshot().DefaultMonitorMix);
            mixer.CreateMix("A speakers", _ => null, MixKind.Monitor);
            Assert.Equal("a-speakers", mixer.Snapshot().DefaultMonitorMix);
            Assert.Equal("monitor2", mixer.ExportSettings().MonitorFeeds["edit_new_default"]);
            AssertEditableMonitorFeeds(mixer, "edit_new_default", "monitor2");
            string[] order = [.. mixer.Config.Mixes.Where(m => m.IsEditable).Select(m => m.Id).Reverse()];
            mixer.SetLayoutOrder(["test"], order, _ => null);
            Assert.Equal("a-speakers", mixer.Snapshot().DefaultMonitorMix);
            AssertEditableMonitorFeeds(mixer, "edit_new_default", "monitor2");
            mixer.CreateMix("Monitor", _ => null, MixKind.Monitor);
            Assert.True(mixer.HasMix("monitor-2"));
            Assert.False(mixer.HasMix("monitor"));
        }
        finally { outputs.TearDown(); }
    }

    [MonitorPipeWireFact]
    public void ARepairedMonitorIgnoresOrphanedStartupStateButAcceptsLaterExplicitSettingsAndProfiles()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        var settings = new MixerSettings
        {
            Mixes = [], UserChannels = [new("test", "Test")],
            MixVolumes = new() { ["monitor"] = 1.5 },
            Levels = new() { ["test|monitor"] = 1 },
        };
        mixer.Build(MixerConfig.FromSettings(settings));
        mixer.ApplySettings(settings);
        Assert.Equal(1, mixer.Snapshot().Mixes.Single(m => m.Id == "monitor").Volume);
        Assert.Contains("monitor", mixer.Snapshot().Channels.Single(c => c.Id == "test").MutedIn);
        mixer.ApplyScene(new() { Levels = new() { ["test|monitor"] = .5 } });
        Assert.DoesNotContain("monitor", mixer.Snapshot().Channels.Single(c => c.Id == "test").MutedIn);
        MixerSettings explicitSettings = mixer.ExportSettings();
        mixer.SetChannelMuted("test", "monitor", true);
        mixer.ApplySettings(explicitSettings);
        Assert.DoesNotContain("monitor", mixer.Snapshot().Channels.Single(c => c.Id == "test").MutedIn);
    }

    private static void AssertEditableMonitorFeeds(Mixer mixer, string output, params string[] ids)
    {
        Assert.True(SpinWait.SpinUntil(() =>
        {
            mixer.EnsureFilterRoutes();
            mixer.EnsureMonitorRoutes();
            HashSet<string> sources = MixLinks().Where(link => link.To.StartsWith(output + ":", StringComparison.Ordinal))
                .Select(link => link.From.Split(':')[0]).ToHashSet(StringComparer.Ordinal);
            return sources.SetEquals(ids.Select(id => "OpenXLR_mix_" + id));
        }, TimeSpan.FromSeconds(5)), "The output did not follow its expected mix feeds: " + output);
    }
}
