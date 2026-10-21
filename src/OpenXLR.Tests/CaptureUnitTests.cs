using System.Text.Json;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class CaptureUnitTests
{
    private const string Unit = "0fd9:0070@0123456789abcdef";
    private const string Other = "0fd9:0070@fedcba9876543210";
    private const string Source = "alsa_input.usb-Elgato_Wave_3_SERIAL-00.mono-fallback";
    private const string Renamed = "alsa_input.usb-Elgato_Wave_3_SERIAL-00.analog-stereo";

    [Theory]
    [InlineData(null)]
    [InlineData("0fd9:0070")]
    [InlineData(Unit)]
    public void CaptureUnitRoundTripsWithTheLayout(string? unit)
    {
        var saved = new MixerSettings { UserChannels = [new("mic", "Microphone", Source, 0, 0, unit)] };
        Assert.Equal(unit, MixerConfig.FromSettings(saved).Channels.Single(c => c.Id == "mic").CaptureUnit);
        string json = JsonSerializer.Serialize(saved, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        MixerSettings restored = JsonSerializer.Deserialize<MixerSettings>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(unit, restored.UserChannels!.Single(c => c.Id == "mic").CaptureUnit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../unit")]
    [InlineData("0fd9:0070@bad")]
    [InlineData("0fd9:007g")]
    [InlineData("0fd9:0070\n")]
    public void InvalidCaptureUnitsAreRejected(string unit)
    {
        Assert.DoesNotContain(MixerConfig.FromSettings(new()
            { UserChannels = [new("mic", "Microphone", Source, 0, 0, unit)] }).Channels, c => c.Id == "mic");
        using var mixer = new Mixer();
        Assert.Throws<InvalidOperationException>(() => mixer.CreateCaptureChannel("Microphone", Source, 0, _ => null, 0, unit));
    }

    [Fact]
    public void ApplicationChannelsCannotHaveACaptureUnit()
    {
        Assert.DoesNotContain(MixerConfig.FromSettings(new()
            { UserChannels = [new("app", "Application", CaptureUnit: Unit)] }).Channels, c => c.Id == "app");
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(2, 0, 2)]
    [InlineData(2, 1, null)]
    public void PrimaryAndAdditionalCarriersFollowCaptureUnitAcrossANodeRename(int input, int pair, int? port)
    {
        IReadOnlyList<ChannelDefinition> channels = [.. MixerConfig.Default().Channels,
            new("mic", "Microphone") { CaptureSource = Source, CapturePair = pair, CaptureMonoChannel = port, CaptureUnit = Unit },
            new("duplicate", "Duplicate") { CaptureSource = Source, CapturePair = pair, CaptureMonoChannel = port, CaptureUnit = Unit }];
        var bindings = new Dictionary<string, SourceBinding> { ["xlr1"] = new("webcam"), ["xlr2"] = new("headset") };
        var additional = new AttachedUnit(Unit, ["Wave_3_SERIAL-"], 2, false, true);
        Assert.Equal("mic", Mixer.UnitInputChannel(channels, bindings, Renamed, 2, input, Unit));
        Assert.Equal("mic", Mixer.UnitInputChannel(channels, bindings, null, 2, input, Unit));
        Assert.Equal("mic", Mixer.AdditionalUnitInputChannel(channels, bindings, additional, input));
        Assert.Null(Mixer.UnitInputChannel(channels, bindings, Renamed, 2, input, Other));
        Assert.Equal("mic", Mixer.UnitInputChannel(channels, bindings, Source, 2, input, Other));
        Assert.Null(Mixer.AdditionalUnitInputChannel(channels, bindings, additional with { Id = Other, Fragments = ["Wave_3_OTHER-"] }, input));
        Assert.Equal("mic", Mixer.AdditionalUnitInputChannel(channels, bindings, additional with { Id = Other }, input));
        Assert.Null(Mixer.AdditionalUnitInputChannel(channels, bindings, additional with { Driven = false, Fragments = [] }, input));
        Assert.Null(Mixer.AdditionalUnitInputChannel(channels, bindings, additional with { Fragments = [] }, input));
        bindings.Remove(input == 1 ? "xlr1" : "xlr2");
        Assert.Equal(input == 1 ? "xlr1" : "xlr2", Mixer.UnitInputChannel(channels, bindings, Renamed, 2, input, Unit));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", true)]
    [InlineData("null", false)]
    [InlineData("1", false)]
    [InlineData("\"true\"", false)]
    [InlineData("{}", false)]
    public void KeepChannelsMustBeAnOptionalBoolean(string json, bool valid)
    {
        using var mixer = new Mixer();
        var command = new Command { Cmd = "setWaveInterfaceEnabled", Device = Unit, Value = JsonSerializer.SerializeToElement(false) };
        Assert.Null(CommandValidation.Check(command, mixer, _ => null));
        Assert.Equal(valid, CommandValidation.Check(command with { KeepChannels = JsonSerializer.Deserialize<JsonElement>(json) }, mixer, _ => null) is null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisablingFindsCaptureChannelsByUnitEvenWithoutSourceFragments(bool keep)
    {
        ChannelStatus Capture(string id, string? unit) => new(id, id, new Dictionary<string, double>(), [], CaptureSource: Source)
            { Binding = new("source", Source, 0, 0, unit) };
        ChannelStatus[] channels = [Capture("mic", Unit), Capture("other", Other), Capture("legacy", null)];
        Assert.Equal(keep ? [] : new[] { "mic" }, WaveInterfaces.CaptureChannelsFrom(channels, [], Unit, keep));
        Assert.Equal(keep ? [] : new[] { "mic", "legacy" }, WaveInterfaces.CaptureChannelsFrom(channels, ["Wave_3"], Unit, keep));
    }
}
