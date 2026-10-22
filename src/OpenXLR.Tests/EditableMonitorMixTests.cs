using OpenXLR.Core.Mixing;
using OpenXLR.UI;
using System.Reflection;
using System.Text.Json.Nodes;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class EditableMonitorMixTests
{
    [Theory]
    [InlineData("monitor")]
    [InlineData("monitor2")]
    public void BuiltInMonitorNamesCanChangeWithoutChangingTheirIds(string id)
    {
        MixerConfig config = MixerConfig.Default();
        MixerConfig renamed = config.WithMixName(id, "Headphones");
        Assert.Equal("Headphones", renamed.Mixes.Single(m => m.Id == id).Name);
        Assert.Equal(config.Mixes.Select(m => m.Id), renamed.Mixes.Select(m => m.Id));
        Assert.True(renamed.Mixes.Single(m => m.Id == id).IsEditable);
    }

    [Theory]
    [InlineData("monitor", "monitor2")]
    [InlineData("monitor2", "monitor")]
    public void EitherBuiltInMonitorCanBeRemovedButTheLastMonitorStays(string removed, string retained)
    {
        MixerConfig config = MixerConfig.Default().WithoutMix(removed);
        Assert.Equal(retained, Assert.Single(config.Mixes, m => m.Kind == MixKind.Monitor).Id);
        Assert.Throws<InvalidOperationException>(() => config.WithoutMix(retained));
        Assert.All(config.Channels, channel =>
        {
            Assert.False(channel.Levels.ContainsKey(removed));
            Assert.DoesNotContain(removed, channel.MutedIn);
        });
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanonicalLayoutPersistsMonitorRenameDeletionAndOrder(bool removeFirst)
    {
        MixerConfig config = MixerConfig.Default().WithoutMix(removeFirst ? "monitor" : "monitor2")
            .WithMix(new("phones", "Phones", MixKind.Monitor));
        string remaining = removeFirst ? "monitor2" : "monitor";
        config = config.WithMixName(remaining, "Speakers").WithOrder(
            [.. config.Channels.Where(c => c.InputPair is null).Select(c => c.Id)], ["phones", "chat", remaining, "stream"]);
        using var mixer = new Mixer();
        SetConfig(mixer, config);
        mixer.SetDuckingSetup(DuckingSetup.Default(config), _ => null);
        MixerSettings saved = mixer.ExportSettings();
        // 0.2.0 reads only userMixes, behind its fixed Monitor A and B.
        Assert.Equal(["phones", "chat", "stream"], saved.UserMixes!.Select(m => m.Id));
        Assert.Equal(["monitor", "virtualMic", "virtualMic"], saved.UserMixes!.Select(m => m.Kind));
        string directory = Directory.CreateTempSubdirectory("openxlr-editable-monitors-").FullName;
        MixerConfig restored, downgraded;
        try
        {
            string path = Path.Combine(directory, "mixer.json");
            Assert.Null(saved.Save(path));
            JsonObject file = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(["phones", "chat", "stream"], file["userMixes"]!.AsArray().Select(m => (string?)m!["id"]));
            MixerSettings loaded = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out string? warning));
            Assert.Null(warning);
            restored = MixerConfig.FromSettings(loaded);
            // A 0.2.0 save drops mixes; reading that file back keeps every added mix.
            file.Remove("mixes");
            OpenXLR.Core.OpenXlrPaths.WriteAtomic(path, file.ToJsonString());
            downgraded = MixerConfig.FromSettings(MixerSettings.Load(path, out warning));
            Assert.Null(warning);
        }
        finally { Directory.Delete(directory, true); }
        Assert.Equal(config.Mixes, restored.Mixes);
        Assert.Equal(["monitor", "monitor2", "phones", "chat", "stream", "auxout"], downgraded.Mixes.Select(m => m.Id));
        Assert.Equal(MixKind.Monitor, downgraded.Mixes.Single(m => m.Id == "phones").Kind);
        Assert.Equal("Speakers", restored.Mixes.Single(m => m.Id == remaining).Name);
        Assert.DoesNotContain(restored.Mixes, m => m.Id == (removeFirst ? "monitor" : "monitor2"));
    }

    [Fact]
    public void CanonicalListWinsOverLegacyUserMixesAndNeverRevivesMonitorB()
    {
        MixerConfig config = MixerConfig.FromSettings(new()
        {
            Mixes = [new("phones", "Phones") { Kind = "monitor" }],
            UserMixes = [new("stale", "Old microphone")],
        });
        Assert.Equal(["phones", "auxout"], config.Mixes.Select(m => m.Id));
        Assert.All(config.Channels, c => Assert.Contains("phones", c.MutedIn));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(18)]
    [InlineData(100)]
    public void MissingMonitorIsRepairedSilentlyWithoutExceedingTheGraphLimit(int count)
    {
        MixerConfig config = MixerConfig.FromSettings(new()
        {
            Mixes = Enumerable.Range(0, count).Select(i => new UserMixDefinition($"mic{i}", "Mic")).ToList(),
        });
        MixDefinition monitor = Assert.Single(config.Mixes, m => m.Kind == MixKind.Monitor);
        Assert.Equal("monitor", monitor.Id);
        Assert.True(config.Mixes.Count(m => m.IsEditable) <= MixerConfig.MaxEditableMixes);
        Assert.All(config.Channels, c => Assert.Contains("monitor", c.MutedIn));
        Assert.Throws<InvalidOperationException>(() => config.WithoutMix("monitor"));
    }

    [Fact]
    public void MalformedCanonicalEntriesCannotReplaceAuxOrReuseReservedMonitorIdsAsMicrophones()
    {
        MixerConfig config = MixerConfig.FromSettings(new()
        {
            Mixes = [null!, new("monitor", "Wrong kind"), new("monitor2", "Wrong kind"),
                new("auxout", "Wrong Aux") { Kind = "monitor" }, new("bad/id", "Bad") { Kind = "monitor" },
                new("valid", "Good") { Kind = "monitor" }, new("valid", "Duplicate"),
                new("unknown", "Unknown") { Kind = "unrecognized" }],
        });
        Assert.Equal(["valid", "auxout"], config.Mixes.Select(m => m.Id));
        Assert.Equal(MixKind.AuxPort, config.Mixes[1].Kind);
    }

    [Fact]
    public void DefaultMonitorAndDeletionAvailabilityDoNotFollowDisplayOrder()
    {
        using var mixer = new Mixer();
        MixerConfig config = MixerConfig.Default();
        string[] channels = [.. config.Channels.Where(c => c.InputPair is null).Select(c => c.Id)];
        SetConfig(mixer, config.WithOrder(channels, ["chat", "monitor2", "stream", "monitor"]));
        Assert.Equal("monitor", mixer.Snapshot().DefaultMonitorMix);
        Assert.True(mixer.Snapshot().Mixes.Single(m => m.Id == "monitor").Deletable);
        Assert.False(mixer.Snapshot().Mixes.Single(m => m.Id == "auxout").Deletable);
        config = config.WithoutMix("monitor").WithMix(new("a-speakers", "Speakers", MixKind.Monitor));
        SetConfig(mixer, config);
        Assert.Equal("a-speakers", mixer.Snapshot().DefaultMonitorMix);
        Assert.Equal(["a-speakers"], DuckingSetup.Default(config).Mixes);
        SetConfig(mixer, config.WithoutMix("monitor2"));
        Assert.False(mixer.Snapshot().Mixes.Single(m => m.Id == "a-speakers").Deletable);
        Assert.True(mixer.Snapshot().Mixes.Single(m => m.Id == "stream").Deletable);
    }

    [Fact]
    public void ProfileRecallKeepsOutputsOnTheMonitorTheyPlayedAfterTheDefaultChanged()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-recall-default-").FullName;
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "pactl"), "exit 0");
            ExecutableScript.Write(Path.Combine(directory, "pw-dump"), "echo '[]'");
            ExecutableScript.Write(Path.Combine(directory, "pw-link"), "exit 0");
            Environment.SetEnvironmentVariable("PATH", directory);
            using var mixer = new Mixer(new PipeWireAdapter());
            typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, true);
            MixerConfig config = MixerConfig.Default().WithoutMix("monitor");
            SetConfig(mixer, config);
            mixer.SetMonitorOutputs(["headphones", "speakers"]);
            Assert.Null(mixer.SetMonitorFeed("speakers", "monitor2+stream"));
            Assert.Equal("monitor2", mixer.MonitorFeedOf("headphones"));

            OpenXLR.Core.MixerScene gaming = mixer.ExportScene();
            Assert.Equal("monitor2", gaming.MonitorFeeds!["headphones"]);

            // Monitor C's id sorts before monitor2, so it is the default now.
            SetConfig(mixer, config.WithMix(new("monitor-c", "Monitor C", MixKind.Monitor)));
            Assert.Equal("monitor-c", mixer.Config.DefaultMonitor!.Id);
            Assert.Null(mixer.SetMonitorFeed("headphones", "monitor-c"));
            mixer.ApplyScene(gaming);
            Assert.Equal("monitor2", mixer.MonitorFeedOf("headphones"));
            Assert.Equal("monitor2+stream", mixer.MonitorFeedOf("speakers"));

            // A profile that names only removed mixes, or an older one with no
            // entry for the output, follows the default at recall time.
            mixer.ApplyScene(gaming with { MonitorFeeds = new() { ["speakers"] = "removed" } });
            Assert.Equal("monitor-c", mixer.MonitorFeedOf("headphones"));
            Assert.Equal("monitor-c", mixer.MonitorFeedOf("speakers"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task WindowHonoursDeletionAvailabilityAndOldDaemonEditability()
    {
        await using var client = new DaemonClient();
        var mix = new MixViewModel(client, "monitor", "Monitor A");
        mix.ApplyFromDaemon(JsonNode.Parse("""{"kind":"monitor","editable":true,"deletable":false}""")!);
        Assert.True(mix.IsEditable);
        Assert.False(mix.IsDeletable);
        mix.ApplyFromDaemon(JsonNode.Parse("""{"kind":"monitor","editable":true,"deletable":true}""")!);
        Assert.True(mix.IsDeletable);
        mix.ApplyFromDaemon(JsonNode.Parse("""{"kind":"monitor","editable":false}""")!);
        Assert.False(mix.IsEditable);
        Assert.False(mix.IsDeletable);
    }

    private static void SetConfig(Mixer mixer, MixerConfig config)
        => typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, config);
}
