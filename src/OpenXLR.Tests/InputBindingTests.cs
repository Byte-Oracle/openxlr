using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// Per-strip input bindings without an audio server: what the command and
/// the mixer accept, what mixer.json keeps, and which channel carries the
/// driven unit's microphone inputs.
/// </summary>
public sealed class InputBindingTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData(" ", 0)]
    [InlineData("OpenXLR_chat", 0)]
    [InlineData("speakers.monitor", 0)]
    [InlineData("mic\nother", 0)]
    [InlineData("mic", -1)]
    [InlineData("mic", 64)]
    public void InvalidBindingsAreRejectedByTheCommandTheAliasAndTheMixer(string source, int port)
    {
        using var mixer = new Mixer();
        foreach (string strip in (string[])["xlr1", "xlr2"])
        {
            Assert.NotNull(CommandValidation.Check(new Command
                { Cmd = "setInputBinding", Channel = strip, Source = source, CaptureMonoChannel = port }, mixer, _ => null));
            Assert.Throws<ArgumentException>(() => mixer.SetInputBinding(strip, new(source, 0, port)));
            Assert.Null(mixer.InputBinding(strip));
        }
        Assert.NotNull(CommandValidation.Check(new Command
            { Cmd = "setPrimaryInput", Source = source, CaptureMonoChannel = port }, mixer, _ => null));
    }

    [Fact]
    public void OnlyXlr1AndXlr2TakeABinding()
    {
        using var mixer = new Mixer();
        Assert.Contains("Aux In", CommandValidation.Check(new() { Cmd = "setInputBinding", Channel = "aux", Source = "mic" }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "setInputBinding", Channel = "game", Source = "mic" }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "setInputBinding", Source = "mic" }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "setInputBinding", Channel = "xlr3", Source = "mic" }, mixer, _ => null));
        Assert.Throws<ArgumentException>(() => mixer.SetInputBinding("aux", new("mic")));
        Assert.True(mixer.HasBindableStrip("xlr2"));
        Assert.False(mixer.HasBindableStrip("aux"));
    }

    [Fact]
    public void AStripReturnsToTheInterfaceWithoutAPortAndASourceTakesPairZero()
    {
        using var mixer = new Mixer();
        foreach (Command ok in new Command[]
        {
            new() { Cmd = "setInputBinding", Channel = "xlr2" },
            new() { Cmd = "setInputBinding", Channel = "xlr2", Source = "mic" },
            new() { Cmd = "setInputBinding", Channel = "xlr1", Source = "mic", CaptureMonoChannel = 63 },
            new() { Cmd = "setPrimaryInput" },
            new() { Cmd = "setPrimaryInput", Source = "mic", CaptureMonoChannel = 1 },
        })
            Assert.Null(CommandValidation.Check(ok, mixer, _ => null));
        foreach (Command bad in new Command[]
        {
            new() { Cmd = "setInputBinding", Channel = "xlr2", CaptureMonoChannel = 1 },
            new() { Cmd = "setInputBinding", Channel = "xlr2", Source = "mic", CapturePair = 1 },
            new() { Cmd = "setInputBinding", Channel = "xlr1", Source = new string('a', 257) },
            new() { Cmd = "setPrimaryInput", CaptureMonoChannel = 1 },
        })
            Assert.NotNull(CommandValidation.Check(bad, mixer, _ => null));
    }

    [Fact]
    public void EachStripKeepsItsOwnBindingAndTheAliasFieldsFollowXlr1()
    {
        using var mixer = new Mixer();
        mixer.SetInputBinding("xlr2", new("usb.mic", 0, 3));
        Assert.Null(mixer.PrimaryInput);
        Assert.Equal(new SourceBinding("usb.mic", 0, 3), mixer.ExportSettings().InputBindings!["xlr2"]);
        // XLR 2 alone leaves the Pro's direct microphone path alone.
        Assert.True(mixer.SetHardwareMicMonitor(true));
        Assert.False(mixer.SetHardwareMicMonitor(false));

        mixer.SetInputBinding("xlr1", new("webcam", 0, 1));
        Assert.Equal(new PrimaryInputBinding("webcam", 1), mixer.PrimaryInput);
        Assert.False(mixer.SetHardwareMicMonitor(true));
        MixerSettings saved = mixer.ExportSettings();
        Assert.Equal(2, saved.InputBindings!.Count);
        Assert.Null(saved.PrimaryInput);

        MixerState state = mixer.Snapshot();
        Assert.Equal(new PrimaryInputBinding("webcam", 1), state.PrimaryInput);
        Assert.False(state.PrimaryInputConnected);
        Dictionary<string, ChannelStatus> channels = state.Channels.ToDictionary(c => c.Id);
        Assert.Equal(new ChannelBinding("source", "webcam", 0, 1), channels["xlr1"].Binding);
        Assert.Equal(new ChannelBinding("source", "usb.mic", 0, 3), channels["xlr2"].Binding);
        Assert.Equal(new ChannelBinding("unit", Pair: 2), channels["aux"].Binding);
        Assert.Equal(new ChannelBinding("apps"), channels["game"].Binding);
        Assert.False(channels["xlr1"].BindingConnected);
        Assert.True(channels["game"].BindingConnected);
        // A bound strip is never the kind test the clients use for capture channels.
        Assert.True(channels["xlr1"].Hardware);
        Assert.Null(channels["xlr1"].CaptureSource);

        mixer.SetInputBindings(null);
        Assert.Null(mixer.ExportSettings().InputBindings);
        Assert.Equal(new ChannelBinding("unit", Pair: 0), mixer.Snapshot().Channels.Single(c => c.Id == "xlr1").Binding);
    }

    [Fact]
    public void SoftwareDspIsApplicableToABoundXlr1OrAUnitWithoutTheHardwareControl()
    {
        using var mixer = new Mixer();
        Assert.True(mixer.Snapshot().SoftLowCutApplicable);
        mixer.SetLowCutApplicable(false);
        mixer.SetClipGuardApplicable(false);
        Assert.False(mixer.Snapshot().SoftLowCutApplicable);
        Assert.False(mixer.Snapshot().SoftClipGuardApplicable);
        mixer.SetInputBinding("xlr2", new("usb.mic"));
        Assert.False(mixer.Snapshot().SoftLowCutApplicable);
        mixer.SetInputBinding("xlr1", new("usb.mic"));
        Assert.True(mixer.Snapshot().SoftLowCutApplicable);
        Assert.True(mixer.Snapshot().SoftClipGuardApplicable);
    }

    [Fact]
    public void UnitInputNamesTheDrivenUnitOnTheStripOnItsPair()
    {
        using var mixer = new Mixer();
        Assert.All(mixer.Snapshot().Channels, c => Assert.Null(c.UnitInput));
        Assert.True(mixer.SetDrivenUnit("0fd9:00b4@0123456789abcdef"));
        Assert.False(mixer.SetDrivenUnit("0fd9:00b4@0123456789abcdef"));
        mixer.SetInputJacks(2, true);
        Dictionary<string, ChannelStatus> pro = mixer.Snapshot().Channels.ToDictionary(c => c.Id);
        Assert.Equal(new UnitInputStatus("0fd9:00b4@0123456789abcdef", 1, true), pro["xlr1"].UnitInput);
        Assert.Equal(2, pro["xlr2"].UnitInput!.Input);
        Assert.Null(pro["aux"].UnitInput);
        // A one-jack unit has no input 2.
        mixer.SetInputJacks(1, false);
        Assert.Null(mixer.Snapshot().Channels.Single(c => c.Id == "xlr2").UnitInput);
        // XLR 1 on another source: no channel carries the unit's capsule.
        mixer.SetInputBinding("xlr1", new("webcam"));
        Assert.All(mixer.Snapshot().Channels, c => Assert.Null(c.UnitInput));
        Assert.True(mixer.SetDrivenUnit(null));
        mixer.SetInputBinding("xlr1", null);
        Assert.All(mixer.Snapshot().Channels, c => Assert.Null(c.UnitInput));
    }

    [Fact]
    public void TheStripOnTheUnitWinsOverACaptureChannelOnTheSamePort()
    {
        const string node = "alsa_input.usb-Elgato_Elgato_Wave_XLR_Pro_SER-00.pro-input-0";
        IReadOnlyList<ChannelDefinition> channels =
        [
            .. MixerConfig.Default().Channels,
            new("pro_mic", "Pro mic") { CaptureSource = node, CaptureMonoChannel = 0 },
            new("pro_mic_again", "Pro mic again") { CaptureSource = node, CaptureMonoChannel = 0 },
            new("pro_mic2", "Pro mic 2") { CaptureSource = node, CaptureMonoChannel = 2 },
            new("other", "Other") { CaptureSource = "alsa_input.usb-cam", CaptureMonoChannel = 0 },
        ];
        var none = new Dictionary<string, SourceBinding>();
        var xlr1Bound = new Dictionary<string, SourceBinding> { ["xlr1"] = new("alsa_input.usb-cam") };
        var bothBound = new Dictionary<string, SourceBinding> { ["xlr1"] = new("alsa_input.usb-cam"), ["xlr2"] = new("alsa_input.usb-cam") };

        Assert.Equal("xlr1", Mixer.UnitInputChannel(channels, none, node, 2, 1));
        Assert.Equal("xlr2", Mixer.UnitInputChannel(channels, none, node, 2, 2));
        // The first capture channel in layout order takes over, never the second.
        Assert.Equal("pro_mic", Mixer.UnitInputChannel(channels, xlr1Bound, node, 2, 1));
        Assert.Equal("xlr2", Mixer.UnitInputChannel(channels, xlr1Bound, node, 2, 2));
        Assert.Equal("pro_mic2", Mixer.UnitInputChannel(channels, bothBound, node, 2, 2));
        // A stereo pair on the unit's node counts as that pair's input.
        IReadOnlyList<ChannelDefinition> paired = [.. MixerConfig.Default().Channels, new("pro_pair", "Pro") { CaptureSource = node, CapturePair = 1 }];
        Assert.Equal("pro_pair", Mixer.UnitInputChannel(paired, bothBound, node, 2, 2));
        // No unit node, no second jack, no such input: nobody carries it.
        Assert.Null(Mixer.UnitInputChannel(channels, xlr1Bound, null, 2, 1));
        Assert.Null(Mixer.UnitInputChannel(channels, bothBound, node, 1, 2));
        Assert.Null(Mixer.UnitInputChannel(channels, none, node, 2, 3));
    }

    [Fact]
    public void TheEarlierPrimaryInputMovesToInputBindingsOnceAndBadEntriesAreDropped()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-bindings-").FullName;
        string path = Path.Combine(dir, "mixer.json");
        try
        {
            File.WriteAllText(path, """{"primaryInput":{"source":"usb.mic","channel":3}}""");
            MixerSettings migrated = MixerSettings.Load(path, out string? warning)!;
            Assert.Null(warning);
            Assert.Null(migrated.PrimaryInput);
            Assert.Equal(new SourceBinding("usb.mic", 0, 3), migrated.InputBindings!["xlr1"]);
            Assert.Null(migrated.Save(path));
            string written = File.ReadAllText(path);
            Assert.Contains("inputBindings", written);
            Assert.DoesNotContain("primaryInput", written);
            Assert.Equal(migrated.InputBindings, MixerSettings.Load(path)!.InputBindings);

            // The new field wins over a stale earlier one.
            File.WriteAllText(path, """{"primaryInput":{"source":"old.mic","channel":0},"inputBindings":{"xlr2":{"source":"new.mic","pair":0,"monoChannel":1}}}""");
            MixerSettings both = MixerSettings.Load(path)!;
            Assert.False(both.InputBindings!.ContainsKey("xlr1"));
            Assert.Equal(new SourceBinding("new.mic", 0, 1), both.InputBindings["xlr2"]);

            // A missing port is port 0; anything else malformed is dropped and named.
            File.WriteAllText(path, """{"inputBindings":{"xlr1":{"source":"usb.mic"},"aux":{"source":"usb.mic"},"xlr2":{"source":"OpenXLR_chat"},"game":null}}""");
            MixerSettings cleaned = MixerSettings.Load(path, out warning)!;
            Assert.Equal(new SourceBinding("usb.mic", 0, 0), Assert.Single(cleaned.InputBindings!).Value);
            Assert.Contains("inputBindings: aux", warning);
            Assert.Contains("inputBindings: invalid binding for xlr2", warning);
            Assert.Contains("inputBindings: game", warning);

            File.WriteAllText(path, """{"inputBindings":{"xlr1":{"source":"usb.mic","monoChannel":null}}}""");
            Assert.Null(MixerSettings.Load(path, out warning)!.InputBindings);
            Assert.Contains("xlr1", warning);
            File.WriteAllText(path, """{"primaryInput":{"source":"OpenXLR_chat","channel":0}}""");
            Assert.Null(MixerSettings.Load(path, out warning)!.InputBindings);
            Assert.Contains("primaryInput", warning);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ScenesNeverCarryBindings()
    {
        using var mixer = new Mixer();
        mixer.SetInputBinding("xlr1", new("usb.mic", 0, 2));
        string scene = System.Text.Json.JsonSerializer.Serialize(mixer.ExportScene());
        Assert.DoesNotContain("usb.mic", scene);
    }
}
