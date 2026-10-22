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
        => CheckEarlierMonitor(null);

    [MonitorPipeWireFact]
    public void CreatingAnEarlierMonitorPreservesFallbackAndPartiallyValidSavedFeeds()
    {
        CheckEarlierMonitor("removed");
        CheckEarlierMonitor("removed+monitor2");
    }

    private static void CheckEarlierMonitor(string? savedFeed)
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
            if (savedFeed is not null)
                mixer.ApplySettings(mixer.ExportSettings() with { MonitorFeeds = new() { ["edit_new_default"] = savedFeed } });
            AssertEditableMonitorFeeds(mixer, "edit_new_default", "monitor2");
            Assert.Throws<IOException>(() => mixer.CreateMix("A speakers", _ => "disk full", MixKind.Monitor));
            Assert.Equal(savedFeed, mixer.ExportSettings().MonitorFeeds.GetValueOrDefault("edit_new_default"));
            Assert.Equal("monitor2", mixer.Snapshot().DefaultMonitorMix);
            mixer.CreateMix("A speakers", _ => null, MixKind.Monitor);
            Assert.Equal("a-speakers", mixer.Snapshot().DefaultMonitorMix);
            Assert.Equal(savedFeed == "removed+monitor2" ? savedFeed : "monitor2",
                mixer.ExportSettings().MonitorFeeds["edit_new_default"]);
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
    public void CreatingAMonitorCannotReviveAStaleSavedFeedWhileItsDefaultStaysTheSame()
        => CheckReusedMonitorFeed("phones", paused: false);

    [MonitorPipeWireFact]
    public void CreatingAMonitorCannotReviveAStalePartOfAPausedProfileFeed()
        => CheckReusedMonitorFeed("removed+phones+monitor2", paused: true);

    private static void CheckReusedMonitorFeed(string staleFeed, bool paused)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-reused-monitor-").FullName;
        string path = Path.Combine(directory, "mixer.json");
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        var outputs = new PipeWireAdapter();
        using var mixer = new Mixer(pw);
        string expected = paused ? "monitor2" : "monitor";
        try
        {
            outputs.CreateNullSink("edit_reused_monitor", "Reused monitor output");
            mixer.Build(MonitorConfig());
            mixer.SetMonitorOutputs(["edit_reused_monitor"]);
            var feeds = new Dictionary<string, string> { ["edit_reused_monitor"] = staleFeed };
            if (paused)
                mixer.ApplyScene(new() { MonitorFeeds = feeds });
            else mixer.ApplySettings(mixer.ExportSettings() with { MonitorFeeds = feeds });
            AssertEditableMonitorFeeds(mixer, "edit_reused_monitor", expected);
            if (paused) mixer.SetMonitorMuted(true);
            Assert.Throws<IOException>(() => mixer.CreateMix("Phones", _ => "disk full", MixKind.Monitor));
            Assert.Equal(staleFeed, mixer.ExportSettings().MonitorFeeds["edit_reused_monitor"]);
            Assert.Equal(paused, mixer.MonitorMuted);
            Assert.False(mixer.HasMix("phones"));

            mixer.CreateMix("Phones", settings => settings.Save(path), MixKind.Monitor);

            Assert.Equal("monitor", mixer.Snapshot().DefaultMonitorMix);
            Assert.Equal(expected, mixer.MonitorFeedOf("edit_reused_monitor"));
            Assert.Equal(expected, mixer.ExportSettings().MonitorFeeds["edit_reused_monitor"]);
            MixerSettings saved = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out string? warning));
            Assert.Null(warning);
            mixer.TearDown();
            mixer.SetMonitorMuted(saved.MonitorMuted);
            mixer.Build(MixerConfig.FromSettings(saved));
            mixer.ApplySettings(saved);
            Assert.Equal(paused, mixer.MonitorMuted);
            Assert.Equal(expected, mixer.MonitorFeedOf("edit_reused_monitor"));
            if (paused)
            {
                Assert.Contains("phones", mixer.ExportSettings().MixMuted);
                mixer.SetMonitorMuted(false);
            }
            AssertEditableMonitorFeeds(mixer, "edit_reused_monitor", expected);
            CaptureLevel(0.1, "OpenXLR_mix_" + expected, "edit_reused_monitor");
        }
        finally { outputs.TearDown(); Directory.Delete(directory, true); }
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

    [MonitorPipeWireFact]
    public void PausedMonitorSelectionSurvivesLayoutEditsAndDiskRestart()
    {
        CheckPausedLayoutEdits(restart: false);
        CheckPausedLayoutEdits(restart: true);
    }

    private static void CheckPausedLayoutEdits(bool restart)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-monitor-selection-").FullName;
        string path = Path.Combine(directory, "mixer.json");
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var original = new Mixer(pw);
        var outputs = new PipeWireAdapter();
        Mixer? restored = null;
        try
        {
            original.Build(MonitorConfig());
            original.CreateMix("Headset", settings => settings.Save(path), MixKind.Monitor);
            outputs.CreateNullSink("edit_pause_a", "Pause A");
            outputs.CreateNullSink("edit_pause_b", "Pause B");
            original.SetMonitorOutputs(["edit_pause_a", "edit_pause_b"]);
            Assert.Null(original.SetMonitorFeed("edit_pause_a", "monitor+headset"));
            Assert.Null(original.SetMonitorFeed("edit_pause_b", "monitor2+headset"));
            original.SetMixVolume("headset", .83);
            original.SetLevel("test", "headset", .37);
            original.SetChannelVolume("test", .72);
            original.SetChannelMuted("test", "headset", false);
            original.SetMixMuted("monitor2", true);
            original.SetMonitorMuted(true);
            original.RenameMix("monitor", "Speakers renamed", settings => settings.Save(path));
            Assert.True(original.MonitorMuted);
            Assert.DoesNotContain("monitor", original.ExportSettings().MixMuted);
            original.CreateMix("New monitor", settings => settings.Save(path), MixKind.Monitor);
            Assert.Contains("new-monitor", original.ExportSettings().MixMuted);
            Assert.Throws<IOException>(() => original.DeleteMix("monitor", _ => "disk full"));
            Assert.True(original.MonitorMuted);
            Assert.True(original.HasMix("monitor"));
            original.DeleteMix("monitor", settings => settings.Save(path));
            Assert.True(original.MonitorMuted); // Headset is still in the listening selection.
            original.DeleteMix("monitor2", settings => settings.Save(path));
            original.RenameMix("headset", "Headset renamed", settings => settings.Save(path));
            Assert.True(original.MonitorMuted);

            Mixer current = original;
            if (restart)
            {
                MixerSettings saved = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out string? warning));
                Assert.Null(warning);
                original.TearDown();
                restored = new Mixer(new PipeWireAdapter());
                // The daemon establishes the pause before it builds any output route.
                restored.SetMonitorMuted(saved.MonitorMuted);
                restored.Build(MixerConfig.FromSettings(saved));
                restored.ApplySettings(saved);
                current = restored;
                Assert.True(current.MonitorMuted);
                Assert.Equal(["edit_pause_a", "edit_pause_b"], current.MonitorOutputs);
                Assert.Equal("headset", current.MonitorFeedOf("edit_pause_a"));
                Assert.Equal("headset", current.MonitorFeedOf("edit_pause_b"));
                Assert.Equal("Headset renamed", current.Config.Mixes.Single(m => m.Id == "headset").Name);
                MixerSettings recalled = current.ExportSettings();
                Assert.Equal(.83, recalled.MixVolumes["headset"]);
                Assert.Equal(.37, recalled.Levels["test|headset"]);
                Assert.Equal(.72, recalled.ChannelVolumes["test"]);
                Assert.DoesNotContain("test|headset", recalled.ChannelMuted);
                Assert.Contains("new-monitor", current.ExportSettings().MixMuted);
                Assert.DoesNotContain(current.Config.Mixes, m => m.Id is "monitor" or "monitor2");
            }

            Assert.Throws<IOException>(() => current.DeleteMix("headset", _ => "disk full"));
            Assert.True(current.MonitorMuted);
            Assert.DoesNotContain("headset", current.ExportSettings().MixMuted);
            current.DeleteMix("headset", settings => settings.Save(path));
            Assert.False(current.MonitorMuted); // The last remembered open monitor was removed.
            Assert.Equal(["new-monitor"], current.ExportSettings().MixMuted);
            Assert.False(MixerSettings.Load(path)!.MonitorMuted);
            current.ToggleMonitorMuted();
            current.ToggleMonitorMuted();
            Assert.Equal(["new-monitor"], current.ExportSettings().MixMuted);
            Assert.Throws<InvalidOperationException>(() => current.DeleteMix("new-monitor", _ => null));
        }
        finally { restored?.Dispose(); outputs.TearDown(); Directory.Delete(directory, true); }
    }

    [MonitorPipeWireFact]
    public void RecreatingADeletedMonitorDuringAPauseDoesNotReviveItsOldListeningSelection()
    {
        using var mixer = new Mixer();
        mixer.Build(MonitorConfig());
        mixer.SetMixMuted("monitor2", true);
        mixer.CreateMix("Phones", _ => null, MixKind.Monitor);
        mixer.SetMonitorMuted(true);
        mixer.DeleteMix("phones", _ => null);
        Assert.True(mixer.MonitorMuted); // The original Monitor A still belongs to the pause.
        Assert.Throws<IOException>(() => mixer.CreateMix("Phones", _ => "disk full", MixKind.Monitor));
        Assert.True(mixer.MonitorMuted);
        Assert.False(mixer.HasMix("phones"));
        Assert.DoesNotContain("phones", mixer.ExportSettings().MixMuted);
        mixer.CreateMix("Phones", _ => null, MixKind.Monitor);
        Assert.Contains("phones", mixer.ExportSettings().MixMuted);
        mixer.DeleteMix("monitor", _ => null);
        Assert.False(mixer.MonitorMuted);
        Assert.Equal(["monitor2", "phones"], mixer.ExportSettings().MixMuted.Order(StringComparer.Ordinal));
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
