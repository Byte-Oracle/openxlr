using System.Reflection;
using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class DuckingSetupTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void DefaultsSelectOnlyApplicationsInMonitorAWithMediumPresets()
    {
        MixerConfig layout = MixerConfig.Default();
        layout = layout with { Channels = [.. layout.Channels, new("mic", "Mic") { CaptureSource = "source", CaptureMonoChannel = 0 }] };
        DuckingSetup setup = DuckingSetup.Default(layout);
        Assert.False(setup.Enabled);
        Assert.Equal("xlr1", setup.Key);
        Assert.Equal(layout.Channels.Where(c => c.IsApplication).Select(c => c.Id), setup.Channels);
        Assert.Equal(["monitor"], setup.Mixes);
        Assert.Equal(("medium", "medium", "medium", "device"), (setup.Sensitivity, setup.Amount, setup.Speed, setup.Processing));
        Assert.Equal((-12.0, 12.0, 80.0, 1200.0), Values(setup.ToPlan()));
    }

    [Theory]
    [InlineData("low", "low", "slow", -3, 6, 100, 2000)]
    [InlineData("medium", "medium", "medium", -12, 12, 80, 1200)]
    [InlineData("high", "high", "fast", -24, 24, 30, 200)]
    public void PlansUseTheHardwarePresetTableAndExcludeTheKey(string sensitivity, string amount, string speed,
        double threshold, double depth, double attack, double release)
    {
        var setup = new DuckingSetup { Key = "mic", Channels = ["mic", "music", "game"], Mixes = ["monitor", "stream"],
            Sensitivity = sensitivity, Amount = amount, Speed = speed, Enabled = true };
        DuckingPlan plan = setup.ToPlan();
        Assert.Equal((threshold, depth, attack, release), Values(plan));
        Assert.True(plan.Enabled);
        Assert.Equal("mic", plan.Key);
        Assert.True(plan.Cells.SetEquals(["music|monitor", "music|stream", "game|monitor", "game|stream"]));
        Assert.Empty((setup with { Channels = [] }).ToPlan().Cells);
        Assert.Empty((setup with { Mixes = [] }).ToPlan().Cells);
        byte[] bytes = ProDucking.PresetBytes(sensitivity, amount, speed);
        Assert.Equal(unchecked((sbyte)bytes[0]), plan.ThresholdDb);
    }

    private static (double, double, double, double) Values(DuckingPlan plan)
        => (plan.ThresholdDb, plan.DepthDb, plan.AttackMs, plan.ReleaseMs);

    public static TheoryData<DuckingSetup, string> InvalidSetups => new()
    {
        { new() { Key = "music" }, "key must be a microphone channel" },
        { new() { Key = "gone" }, "key must be a microphone channel" },
        { new() { Key = null! }, "key must be a microphone channel" },
        { new() { Channels = ["xlr1"] }, "channels must be distinct channel ids other than the key" },
        { new() { Channels = ["music", "music"] }, "channels must be distinct channel ids other than the key" },
        { new() { Channels = ["gone"] }, "channels must be distinct channel ids other than the key" },
        { new() { Channels = null! }, "channels must be distinct channel ids other than the key" },
        { new() { Channels = [null!] }, "channels must be distinct channel ids other than the key" },
        { new() { Mixes = ["monitor", "monitor"] }, "mixes must be distinct mix ids" },
        { new() { Mixes = ["gone"] }, "mixes must be distinct mix ids" },
        { new() { Mixes = null! }, "mixes must be distinct mix ids" },
        { new() { Mixes = [null!] }, "mixes must be distinct mix ids" },
        { new() { Sensitivity = "custom" }, "sensitivity must be low, medium or high" },
        { new() { Amount = null! }, "amount must be low, medium or high" },
        { new() { Speed = "low" }, "speed must be slow, medium or fast" },
        { new() { Processing = "hardware" }, "processing must be device or software" },
    };

    [Theory]
    [MemberData(nameof(InvalidSetups))]
    public void ValidationReportsEveryFieldBeforeSaving(DuckingSetup setup, string problem)
    {
        using var mixer = new Mixer();
        Assert.Equal(problem, DuckingSetup.Validate(setup, mixer));
        bool saved = false;
        ArgumentException error = Assert.Throws<ArgumentException>(() => mixer.SetDuckingSetup(setup, _ => { saved = true; return null; }));
        Assert.Equal("setDucking: " + problem, error.Message);
        Assert.False(saved);
    }

    [Fact]
    public void MonoCaptureKeysAreAcceptedAndStereoCaptureKeysAreRefused()
    {
        using var mixer = new Mixer();
        MixerConfig layout = MixerConfig.Default();
        Set(mixer, "_config", layout with { Channels = [.. layout.Channels,
            new("mic", "Mic") { CaptureSource = "source", CaptureMonoChannel = 0 },
            new("stereo", "Stereo") { CaptureSource = "source" }] });
        Assert.Null(DuckingSetup.Validate(new() { Key = "mic" }, mixer));
        Assert.Equal("key must be a microphone channel", DuckingSetup.Validate(new() { Key = "stereo" }, mixer));
        Assert.Null(DuckingSetup.Validate(new() { Channels = [], Mixes = [], Processing = "software" }, mixer));
    }

    [Fact]
    public void PruningKeepsRenamesDropsTargetsAndDisablesAMissingKeyWithoutAddingNewChannels()
    {
        MixerConfig layout = MixerConfig.Default();
        var setup = new DuckingSetup { Enabled = true, Key = "mic", Channels = ["music", "game", "xlr1"], Mixes = ["monitor", "stream"] };
        layout = layout with { Channels = [.. layout.Channels, new("mic", "Mic") { CaptureSource = "source", CaptureMonoChannel = 0 }] };
        DuckingSetup kept = DuckingSetup.Prune(setup, layout.WithChannelName("music", "Songs").WithMixName("stream", "Live"));
        Assert.True(kept.Enabled);
        Assert.Equal(setup.Channels, kept.Channels);
        Assert.Equal(setup.Mixes, kept.Mixes);
        kept = DuckingSetup.Prune(setup, layout.WithoutChannel("music").WithoutMix("stream"));
        Assert.Equal(["game", "xlr1"], kept.Channels);
        Assert.Equal(["monitor"], kept.Mixes);
        kept = DuckingSetup.Prune(setup, layout.WithoutChannel("mic"));
        Assert.False(kept.Enabled);
        Assert.Equal("xlr1", kept.Key);
        Assert.Equal(["music", "game"], kept.Channels);
        Assert.Equal(setup.Channels, DuckingSetup.Prune(setup, layout with { Channels = [.. layout.Channels, new("new", "New")] }).Channels);
    }

    [Fact]
    public void SetupSavesBeforeApplyingAndCopiesBothIncomingAndOutgoingLists()
    {
        using var mixer = new Mixer();
        var channels = new List<string> { "music" };
        var setup = new DuckingSetup { Enabled = true, Channels = channels, Processing = "software" };
        mixer.SetDuckingSetup(setup, settings =>
        {
            Assert.False(mixer.DuckingSnapshot().Setup.Enabled);
            Assert.True(settings.Ducking!.Enabled);
            Assert.NotSame(channels, settings.Ducking.Channels);
            Assert.False(settings.Ducking.Channels is IList<string> { IsReadOnly: false });
            return null;
        });
        channels.Add("game");
        Assert.Equal(["music"], mixer.DuckingSnapshot().Setup.Channels);
        Assert.False(mixer.DuckingSnapshot().Setup.Channels is IList<string> { IsReadOnly: false });
        Assert.Equal(["music"], mixer.ExportSettings().Ducking!.Channels);
        Assert.Throws<IOException>(() => mixer.SetDuckingSetup(setup with { Enabled = false }, _ => "read only"));
        Assert.True(mixer.DuckingSnapshot().Setup.Enabled);
    }

    [Fact]
    public void ProfilesOmitProcessingPruneRecallAndLeaveAnAbsentSetupAlone()
    {
        using var mixer = new Mixer();
        mixer.SetDuckingSetup(new() { Enabled = true, Channels = ["music"], Processing = "software" }, _ => null);
        MixerScene scene = mixer.ExportScene();
        Assert.DoesNotContain("processing", JsonSerializer.Serialize(scene, Json), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["music"], scene.Ducking!.Channels);
        mixer.ApplyScene(new());
        Assert.True(mixer.DuckingSnapshot().Setup.Enabled);
        mixer.ApplyScene(new() { Ducking = new() { Enabled = true, Key = "gone", Channels = ["game", "missing", "xlr1"], Mixes = ["monitor", "missing"] } });
        DuckingSetup recalled = mixer.DuckingSnapshot().Setup;
        Assert.False(recalled.Enabled);
        Assert.Equal("xlr1", recalled.Key);
        Assert.Equal(["game"], recalled.Channels);
        Assert.Equal(["monitor"], recalled.Mixes);
        Assert.Equal("software", recalled.Processing);
        mixer.ApplyScene(scene);
        Assert.True(mixer.DuckingSnapshot().Setup.Enabled);
        Assert.Equal(["music"], mixer.DuckingSnapshot().Setup.Channels);
    }

    [Fact]
    public void SettingsRoundTripAndBadEntriesAreDroppedWithIndividualNotes()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-duck-settings-").FullName;
        try
        {
            string path = Path.Combine(dir, "mixer.json");
            OpenXlrPaths.WriteAtomic(path, """
                {"channelVolumes":{"music":0.4},"ducking":{"enabled":true,"key":"music",
                "channels":["music","gone","xlr1","game","game",null],"mixes":["monitor","gone","monitor"],
                "sensitivity":"wrong","amount":"high","speed":null,"processing":"wrong"}}
                """);
            MixerSettings saved = MixerSettings.Load(path, out string? warning)!;
            Assert.NotNull(warning);
            foreach (string field in new[] { "key", "channels", "mixes", "sensitivity", "speed", "processing" })
                Assert.Contains("ducking." + field, warning);
            Assert.Equal(.4, saved.ChannelVolumes["music"]);
            DuckingSetup setup = saved.Ducking!;
            Assert.False(setup.Enabled);
            Assert.Equal("xlr1", setup.Key);
            Assert.Equal(["music", "game"], setup.Channels);
            Assert.Equal(["monitor"], setup.Mixes);
            Assert.Equal(("medium", "high", "medium", "device"), (setup.Sensitivity, setup.Amount, setup.Speed, setup.Processing));
            Assert.Null(saved.Save(path));
            MixerSettings restored = MixerSettings.Load(path, out warning)!;
            Assert.Null(warning);
            Assert.Equal(JsonSerializer.Serialize(setup, Json), JsonSerializer.Serialize(restored.Ducking, Json));
            using var mixer = new Mixer();
            mixer.ApplySettings(restored);
            Assert.Equal(JsonSerializer.Serialize(setup, Json), JsonSerializer.Serialize(mixer.ExportSettings().Ducking, Json));
            OpenXlrPaths.WriteAtomic(path, """{"ducking":{"channels":null,"mixes":null,"amount":"wrong"}}""");
            restored = MixerSettings.Load(path, out warning)!;
            Assert.Empty(restored.Ducking!.Channels);
            Assert.Empty(restored.Ducking.Mixes);
            Assert.Contains("ducking.amount", warning);
            Assert.Contains("ducking.channels", warning);
            Assert.Contains("ducking.mixes", warning);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MalformedProfilesAreRefusedBeforeChangingTheSetup()
    {
        using var mixer = new Mixer();
        foreach (DuckingScene invalid in new DuckingScene[]
        {
            new() { Key = null! }, new() { Channels = null! }, new() { Channels = ["xlr1"] },
            new() { Channels = ["music", "music"] }, new() { Mixes = [null!] },
            new() { Mixes = ["monitor", "monitor"] }, new() { Sensitivity = "wrong" },
            new() { Amount = "wrong" }, new() { Speed = "wrong" },
        }) Assert.Throws<JsonException>(() => mixer.ApplyScene(new() { Ducking = invalid }));
        Assert.False(mixer.DuckingSnapshot().Setup.Enabled);
    }

    [Fact]
    public void LayoutPersistencePrunesInTheSameSaveAndAFailedSaveKeepsTheSetup()
    {
        using var mixer = new Mixer();
        mixer.SetDuckingSetup(new() { Enabled = true, Channels = ["music", "game"], Mixes = ["monitor", "stream"] }, _ => null);
        MixerConfig layout = MixerConfig.Default().WithoutChannel("music").WithoutMix("stream");
        Set(mixer, "_config", layout);
        var persist = typeof(Mixer).GetMethod("PersistLocked", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Func<MixerSettings, string?> fail = settings =>
        {
            Assert.Equal(["game"], settings.Ducking!.Channels);
            Assert.Equal(["monitor"], settings.Ducking.Mixes);
            Assert.DoesNotContain(settings.UserChannels!, c => c.Id == "music");
            Assert.DoesNotContain(settings.UserMixes!, m => m.Id == "stream");
            return "read only";
        };
        Assert.IsType<IOException>(Assert.Throws<TargetInvocationException>(() => persist.Invoke(mixer, [fail])).InnerException);
        Assert.Equal(["music", "game"], mixer.DuckingSnapshot().Setup.Channels);
        Func<MixerSettings, string?> save = _ => null;
        persist.Invoke(mixer, [save]);
        Assert.Equal(["game"], mixer.DuckingSnapshot().Setup.Channels);
        Assert.Equal(["monitor"], mixer.DuckingSnapshot().Setup.Mixes);
    }

    internal static void Set(Mixer mixer, string name, object value)
        => typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, value);
}
