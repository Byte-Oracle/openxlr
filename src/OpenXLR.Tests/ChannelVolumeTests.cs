using System.Reflection;
using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// A channel's own volume: the gain it puts on each send, what it accepts,
/// and how it is saved and recalled. The audio itself is measured on a
/// private PipeWire server in <see cref="MonitorVolumeIntegrationTests"/>.
/// </summary>
public sealed class ChannelVolumeTests
{
    [Theory]
    [InlineData(0.8, 1.0, 1.0, 0.8)]
    [InlineData(0.8, 0.5, 1.0, 0.4)]
    [InlineData(0.8, 0.5, 0.5, 0.2)]
    [InlineData(1.0, 0.0, 1.0, 0.0)]
    [InlineData(0.0, 1.0, 1.0, 0.0)]
    [InlineData(1.5, 2.0, 1.0, 1.0)]
    [InlineData(0.5, -1.0, 1.0, 0.0)]
    public void ASendCarriesItsLevelTimesTheChannelVolumeTimesTheMaster(double level, double channel, double master, double expected)
        => Assert.Equal(expected, Mixer.SendGain(level, channel, master), 12);

    [Fact]
    public void HalvingTheChannelVolumeTakesTheSameDecibelsOffEverySend()
    {
        // The stream volume is on the desktop's cubic scale, so its gain is
        // the cube of the number. A product of volumes is a sum of decibels.
        static double Decibels(double volume) => 20 * Math.Log10(Math.Pow(volume, 3));
        double drop = Decibels(0.5);
        foreach (double level in new[] { 1.0, 0.8, 0.3, 0.05 })
            Assert.Equal(drop, Decibels(Mixer.SendGain(level, 0.5, 1)) - Decibels(level), 9);
    }

    [Fact]
    public void RecallTakesSavedValuesAndPutsOldDescribedChannelsBackAtFullVolume()
    {
        var volumes = new Dictionary<string, double> { ["game"] = 0.3, ["music"] = 0.4, ["late"] = 0.5, ["cam"] = 0.6, ["xlr2"] = 0.6, ["aux"] = 0.3 };
        Mixer.RecallChannelVolumes(volumes, ["xlr1", "xlr2", "aux", "game", "music", "late", "cam"],
            new Dictionary<string, double> { ["game"] = 0.7, ["cam"] = 1.5, ["xlr1"] = 0.2, ["gone"] = 0.1, ["music"] = double.NaN },
            ["game|monitor", "music|monitor", "music|chat", "xlr1|monitor", "xlr2|monitor", "aux|stream", "bad"]);
        // A saved value is taken and clamped to 0..1.
        Assert.Equal(0.7, volumes["game"]);
        Assert.Equal(1.0, volumes["cam"]);
        // A channel whose sends the data describes but whose volume it does
        // not carry plays at full volume: an absent entry reads as unity.
        Assert.False(volumes.ContainsKey("music"));
        // A channel the data says nothing about keeps its volume.
        Assert.Equal(0.5, volumes["late"]);
        // A hardware input takes its saved volume like any channel.
        Assert.Equal(0.2, volumes["xlr1"]);
        // A file written before the hardware inputs took a volume describes
        // their sends without one: they play at full volume, as they did.
        Assert.False(volumes.ContainsKey("xlr2"));
        Assert.False(volumes.ContainsKey("aux"));
        // A channel that does not exist is not given one.
        Assert.False(volumes.ContainsKey("gone"));

        // A profile saved before channel volumes carries none at all.
        volumes["game"] = 0.2;
        Mixer.RecallChannelVolumes(volumes, ["game", "late"], null, ["game|monitor"]);
        Assert.False(volumes.ContainsKey("game"));
        Assert.Equal(0.5, volumes["late"]);
    }

    [Fact]
    public void EveryChannelTakesAVolumeAndItIsSaved()
    {
        using var mixer = new Mixer();
        Set(mixer, "_config", new MixerConfig
        {
            Channels =
            [
                new("xlr1", "XLR 1") { InputPair = 0 },
                new("game", "Game"),
                new("cam", "Capture") { CaptureSource = "alsa_input.capture" },
            ],
            Mixes = [new("monitor", "Monitor A", MixKind.Monitor), new("chat", "Chat", MixKind.VirtualMic)],
        });
        var cells = Field<HashSet<string>>(mixer, "_cells");
        var levels = Field<Dictionary<string, double>>(mixer, "_levels");
        foreach (string channel in new[] { "xlr1", "game", "cam" })
            foreach (string mix in new[] { "monitor", "chat" })
            {
                cells.Add($"{channel}|{mix}");
                levels[$"{channel}|{mix}"] = 0.8;
            }
        Set(mixer, "_built", true);
        try
        {
            // Nothing is changed before a value is set: every channel is at unity.
            Assert.Equal(new Dictionary<string, double> { ["xlr1"] = 1, ["game"] = 1, ["cam"] = 1 }, mixer.ExportSettings().ChannelVolumes);

            mixer.SetChannelVolume("game", 0.5);
            mixer.SetChannelVolume("cam", 7);
            mixer.SetChannelVolume("xlr1", 0.1);
            mixer.SetChannelVolume("missing", 0.1);
            // The fixture has no graph, so every send of the channel waits
            // for its leg and the sweep pushes the stored volume when it comes.
            Assert.Contains("game|monitor", Field<HashSet<string>>(mixer, "_pendingCells"));
            Assert.Contains("game|chat", Field<HashSet<string>>(mixer, "_pendingCells"));
            Assert.Contains("xlr1|monitor", Field<HashSet<string>>(mixer, "_pendingCells"));
            Assert.Contains("xlr1|chat", Field<HashSet<string>>(mixer, "_pendingCells"));

            MixerSettings settings = mixer.ExportSettings();
            Assert.Equal(new Dictionary<string, double> { ["xlr1"] = 0.1, ["game"] = 0.5, ["cam"] = 1 }, settings.ChannelVolumes);
            Assert.Equal(settings.ChannelVolumes, mixer.ExportScene().ChannelVolumes);
            // The send levels themselves are untouched.
            Assert.All(settings.Levels.Values, level => Assert.Equal(0.8, level));
            mixer.SetChannelVolume("game", -1);
            Assert.Equal(0, mixer.ExportSettings().ChannelVolumes["game"]);
        }
        finally { Set(mixer, "_built", false); } // The fixture never created an audio graph.
    }

    [Fact]
    public void SettingsKeepChannelVolumesAndAnOlderFileReadsAsFullVolume()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-chanvol-").FullName;
        try
        {
            string path = Path.Combine(dir, "mixer.json");
            Assert.Null(new MixerSettings { ChannelVolumes = new() { ["game"] = 0.25 } }.Save(path));
            Assert.Contains("\"channelVolumes\"", File.ReadAllText(path));
            Assert.Equal(0.25, MixerSettings.Load(path)!.ChannelVolumes["game"]);

            File.WriteAllText(path, """{"levels":{"game|monitor":0.5}}""");
            MixerSettings old = MixerSettings.Load(path, out string? warning)!;
            Assert.Null(warning);
            Assert.Empty(old.ChannelVolumes);

            File.WriteAllText(path, """{"channelVolumes":null,"levels":{"game|monitor":0.5}}""");
            MixerSettings nulled = MixerSettings.Load(path, out warning)!;
            Assert.Contains("channelVolumes: null entry", warning);
            Assert.Empty(nulled.ChannelVolumes);
            Assert.Equal(0.5, nulled.Levels["game|monitor"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AProfileCarriesChannelVolumesAndAnOlderOneCarriesNone()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        MixerScene old = JsonSerializer.Deserialize<MixerScene>("""{"levels":{"game|monitor":0.5}}""", options)!;
        Assert.Null(old.ChannelVolumes);
        var scene = new MixerScene { ChannelVolumes = new() { ["game"] = 0.4 } };
        MixerScene back = JsonSerializer.Deserialize<MixerScene>(JsonSerializer.Serialize(scene, options), options)!;
        Assert.Equal(0.4, back.ChannelVolumes!["game"]);
    }

    [Fact]
    public void AStripKeepsItsVolumeWhenItsBindingChangesAndAProfileRecallsIt()
    {
        using var mixer = new Mixer();
        Set(mixer, "_config", MixerConfig.Default());
        mixer.SetChannelVolume("xlr1", 0.4);
        mixer.SetChannelVolume("aux", 0.6);
        // A binding moves the strip's source, not its level.
        mixer.SetInputBinding("xlr1", new SourceBinding("alsa_input.usb-webcam", 0, 0));
        Assert.Equal(0.4, mixer.ExportSettings().ChannelVolumes["xlr1"]);
        mixer.SetInputBindings(null);
        Assert.Equal(0.4, mixer.ExportSettings().ChannelVolumes["xlr1"]);
        Assert.Equal(0.6, mixer.ExportSettings().ChannelVolumes["aux"]);

        // A profile carries the strips' volumes; one saved before they had
        // any recalls each strip it describes at full volume.
        MixerScene scene = mixer.ExportScene();
        Assert.Equal(0.4, scene.ChannelVolumes!["xlr1"]);
        Assert.Equal(0.6, scene.ChannelVolumes["aux"]);
        var volumes = new Dictionary<string, double> { ["xlr1"] = 0.1, ["aux"] = 0.1 };
        Mixer.RecallChannelVolumes(volumes, ["xlr1", "aux"], scene.ChannelVolumes, scene.Levels.Keys);
        Assert.Equal(0.4, volumes["xlr1"]);
        Assert.Equal(0.6, volumes["aux"]);
        Mixer.RecallChannelVolumes(volumes, ["xlr1", "aux"], new Dictionary<string, double> { ["game"] = 0.5 }, ["xlr1|monitor", "aux|monitor"]);
        Assert.Empty(volumes);
    }

    [Fact]
    public void TheCommandTakesAnyChannelAndAFiniteValue()
    {
        using var mixer = new Mixer();
        Set(mixer, "_config", MixerConfig.Default() with
        {
            Channels = [.. MixerConfig.Default().Channels, new("cam", "Capture") { CaptureSource = "alsa_input.capture" }],
        });
        static Command Cmd(string json) => JsonSerializer.Deserialize<Command>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        string? Check(string json) => CommandValidation.Check(Cmd(json), mixer, _ => null);
        Assert.Null(Check("""{"cmd":"setChannelVolume","channel":"game","value":0.5}"""));
        Assert.Null(Check("""{"cmd":"setChannelVolume","channel":"cam","value":2}"""));   // clamped by the mixer
        // The hardware inputs take one too, after the interface's gain.
        Assert.Null(Check("""{"cmd":"setChannelVolume","channel":"xlr1","value":0.5}"""));
        Assert.Null(Check("""{"cmd":"setChannelVolume","channel":"xlr2","value":0.5}"""));
        Assert.Null(Check("""{"cmd":"setChannelVolume","channel":"aux","value":0.5}"""));
        Assert.Contains("unknown channel 'nope'", Check("""{"cmd":"setChannelVolume","channel":"nope","value":0.5}"""));
        Assert.Contains("unknown channel", Check("{\"cmd\":\"setChannelVolume\",\"channel\":\"" + new string('g', 37) + "\",\"value\":0.5}"));
        Assert.Contains("finite number", Check("""{"cmd":"setChannelVolume","channel":"xlr1","value":"loud"}"""));
        Assert.Contains("need 'channel'", Check("""{"cmd":"setChannelVolume","value":0.5}"""));
        Assert.Contains("finite number", Check("""{"cmd":"setChannelVolume","channel":"game","value":"loud"}"""));
        Assert.Contains("finite number", Check("""{"cmd":"setChannelVolume","channel":"game"}"""));
    }

    private static T Field<T>(Mixer mixer, string name)
        => (T)typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!;

    private static void Set(Mixer mixer, string name, object value)
        => typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, value);
}
