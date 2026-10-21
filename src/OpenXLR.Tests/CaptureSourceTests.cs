using System.Reflection;
using System.Text.Json;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class CaptureSourceTests
{
    private const string Unit = "0fd9:0070@0123456789abcdef";
    private const string OtherUnit = "0fd9:0070@fedcba9876543210";
    private static readonly InsertDefinition Insert = new() { Id = "gain", Plugin = "urn:test:gain", Kind = "lv2", Bypass = true };

    private static Mixer Layout(int? mono = 0)
    {
        var mixer = new Mixer();
        MixerConfig config = MixerConfig.Default() with
        {
            Channels = [.. MixerConfig.Default().Channels, new("capture", "Guest microphone")
                { CaptureSource = "old.mic", CaptureMonoChannel = mono, CaptureUnit = Unit }],
            ExclusiveGroups = [new("mics", "Microphones", ["capture", "xlr1"])],
        };
        typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, config);
        foreach (ChannelDefinition channel in config.Channels)
            foreach (MixDefinition mix in config.Mixes)
            {
                string cell = channel.Id + "|" + mix.Id;
                Field<HashSet<string>>(mixer, "_cells").Add(cell);
                Field<Dictionary<string, double>>(mixer, "_levels")[cell] = .37;
            }
        return mixer;
    }

    private static T Field<T>(Mixer mixer, string name)
        => (T)typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!;

    [Theory]
    [InlineData(null)]
    [InlineData("missing")]
    [InlineData("xlr1")]
    [InlineData("xlr2")]
    [InlineData("aux")]
    [InlineData("system")]
    public void OnlyCaptureInputsAcceptASourceChange(string? channel)
    {
        using var mixer = Layout();
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "setCaptureSource", Channel = channel, Source = "new.mic" }, mixer, _ => null));
        Assert.Throws<ArgumentException>(() => mixer.SetCaptureSource(channel!, "new.mic", 0, _ => null));
    }

    [Theory]
    [InlineData(null, 0, null)]
    [InlineData("", 0, null)]
    [InlineData(" ", 0, null)]
    [InlineData("mic\nother", 0, null)]
    [InlineData("OpenXLR_chat", 0, null)]
    [InlineData("speakers.monitor", 0, null)]
    [InlineData("new.mic", -1, null)]
    [InlineData("new.mic", 32, null)]
    [InlineData("new.mic", 0, -1)]
    [InlineData("new.mic", 0, 64)]
    [InlineData("new.mic", 1, 0)]
    public void InvalidCaptureSourcesAndPortsAreRefused(string? source, int pair, int? mono)
    {
        using var mixer = Layout();
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "setCaptureSource", Channel = "capture", Source = source,
            CapturePair = pair, CaptureMonoChannel = mono }, mixer, _ => null));
        Assert.Throws<ArgumentException>(() => mixer.SetCaptureSource("capture", source!, pair, _ => null, mono));
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "setCaptureSource", Channel = "capture", Source = new string('a', 257) }, mixer, _ => null));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(31, null)]
    [InlineData(0, 0)]
    [InlineData(0, 63)]
    public void ValidBindingsCanNameAnOfflineSource(int pair, int? mono)
    {
        using var mixer = Layout();
        Assert.Null(CommandValidation.Check(new() { Cmd = "setCaptureSource", Channel = "capture", Source = "offline.mic",
            CapturePair = pair, CaptureMonoChannel = mono }, mixer, _ => null));
        mixer.SetCaptureSource("capture", "offline.mic", pair, _ => null, mono);
        UserChannelDefinition saved = mixer.ExportSettings().UserChannels!.Single(c => c.Id == "capture");
        Assert.Equal("offline.mic", saved.CaptureSource);
        Assert.Equal(pair, saved.CapturePair);
        Assert.Equal(mono, saved.CaptureMonoChannel);
        Assert.Null(saved.CaptureUnit);
    }

    [Fact]
    public void RebindingKeepsTheChannelSendsVolumeChainAndGroupAndMutesOnlyItsMonitors()
    {
        using var mixer = Layout();
        mixer.SetInserts("capture", [Insert]);
        mixer.SetChannelVolume("capture", .62);
        MixerSettings before = mixer.ExportSettings();
        MixerSettings? written = null;
        mixer.SetCaptureSource("capture", "new.mic", 0, settings => { written = settings; return null; }, 1);
        MixerSettings after = mixer.ExportSettings();
        UserChannelDefinition channel = after.UserChannels!.Single(c => c.Id == "capture");
        Assert.Equal("Guest microphone", channel.Name);
        Assert.Equal(before.UserChannels!.Select(c => c.Id), after.UserChannels!.Select(c => c.Id));
        Assert.Equal(before.Levels, after.Levels);
        Assert.Equal(before.ChannelVolumes, after.ChannelVolumes);
        Assert.Equal(JsonSerializer.Serialize(before.Inserts), JsonSerializer.Serialize(after.Inserts));
        Assert.Equal(JsonSerializer.Serialize(before.ExclusiveGroups), JsonSerializer.Serialize(after.ExclusiveGroups));
        Assert.Equal(new[] { "capture|monitor", "capture|monitor2" }, after.ChannelMuted.Order());
        Assert.Equal(JsonSerializer.Serialize(after), JsonSerializer.Serialize(written));
        Assert.Equal(1, mixer.Snapshot().Channels.Single(c => c.Id == "capture").ChainWidth);
    }

    [Theory]
    [InlineData("old.mic", null, Unit)]
    [InlineData("old.mic", OtherUnit, OtherUnit)]
    [InlineData("new.mic", null, null)]
    [InlineData("new.mic", OtherUnit, OtherUnit)]
    public void TheUnitIsKeptOnlyForTheSameSourceOrRelearnedForTheSelectedSource(string source, string? learned, string? expected)
    {
        using var mixer = Layout();
        mixer.SetCaptureSource("capture", source, 0, _ => null, 1, learned);
        Assert.Equal(expected, mixer.ExportSettings().UserChannels!.Single(c => c.Id == "capture").CaptureUnit);
        Assert.Equal(expected, mixer.Snapshot().Channels.Single(c => c.Id == "capture").Binding.Unit);
    }

    [Fact]
    public void ANewUnitOnTheSameWiringIsSavedWithoutMutingTheMonitors()
    {
        using var mixer = Layout();
        Assert.Throws<IOException>(() => mixer.SetCaptureSource("capture", "old.mic", 0, _ => "disk full", 0, OtherUnit));
        Assert.Equal(Unit, mixer.ExportSettings().UserChannels!.Single(c => c.Id == "capture").CaptureUnit);
        mixer.SetCaptureSource("capture", "old.mic", 0, _ => null, 0, OtherUnit);
        MixerSettings after = mixer.ExportSettings();
        Assert.Equal(OtherUnit, after.UserChannels!.Single(c => c.Id == "capture").CaptureUnit);
        Assert.DoesNotContain(after.ChannelMuted, cell => cell.StartsWith("capture|", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(null, 0)]
    public void AnyInsertRefusesAWidthChangeBeforeSaving(int? beforePort, int? afterPort)
    {
        using var mixer = Layout(beforePort);
        foreach (bool bypass in new[] { false, true })
        {
            mixer.SetInserts("capture", [Insert with { Bypass = bypass }]);
            string before = JsonSerializer.Serialize(mixer.ExportSettings());
            Assert.Contains("Remove", Assert.Throws<InvalidOperationException>(() => mixer.SetCaptureSource("capture", "new.mic", 0,
                _ => throw new InvalidOperationException("must not save"), afterPort)).Message);
            Assert.Equal(before, JsonSerializer.Serialize(mixer.ExportSettings()));
        }
        mixer.SetInserts("capture", []);
        mixer.SetCaptureSource("capture", "new.mic", 0, _ => null, afterPort);
        Assert.Equal(afterPort is null ? 2 : 1, mixer.InsertChannels("capture"));
    }

    [Fact]
    public void FailedSaveRestoresTheBindingAndMutesAndAnUnchangedBindingDoesNotMute()
    {
        using var mixer = Layout();
        string before = JsonSerializer.Serialize(mixer.ExportSettings());
        Assert.Throws<IOException>(() => mixer.SetCaptureSource("capture", "new.mic", 0, _ => "disk full", 1));
        Assert.Equal(before, JsonSerializer.Serialize(mixer.ExportSettings()));
        mixer.SetCaptureSource("capture", "old.mic", 0, _ => null, 0);
        Assert.Equal(before, JsonSerializer.Serialize(mixer.ExportSettings()));
    }

    [Fact]
    public void CapturePortChoicesNamePairsAndMonoPortsAndRetainMissingSelections()
    {
        var device = new AudioDeviceItem("multi", "Multi input", false, Channels: 4, Positions: "FL,FR,AUX0,AUX1");
        var ports = InputBindingDialog.CapturePortChoices(device, mono: 63);
        Assert.Equal(2, ports.Count(p => p.Stereo));
        Assert.Contains(ports, p => p.Stereo && p.Pair == 1 && p.Label.Contains("AUX0, AUX1", StringComparison.Ordinal));
        Assert.Contains(ports, p => p.MonoChannel == 2 && p.Label.Contains("AUX0", StringComparison.Ordinal));
        Assert.Contains(ports, p => p.MonoChannel == 63 && p.Pair == 0);
        Assert.Contains(InputBindingDialog.CapturePortChoices(device, pair: 31), p => p.Stereo && p.Pair == 31);
        Assert.Equal(96, InputBindingDialog.CapturePortChoices(null).Count);
    }
}
