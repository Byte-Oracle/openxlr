using System.Text.Json;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class MonitorMuteTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", true)]
    [InlineData("null", false)]
    [InlineData("1", false)]
    [InlineData("\"true\"", false)]
    public void MonitorMuteRequiresABoolean(string value, bool valid)
    {
        using var mixer = new Mixer();
        Command command = JsonSerializer.Deserialize<Command>("{\"cmd\":\"setMonitorMuted\",\"value\":" + value + "}")!;
        Assert.Equal(valid, CommandValidation.Check(command, mixer, _ => null) is null);
    }

    [Fact]
    public void MonitorMuteRequiresAValue()
    {
        using var mixer = new Mixer();
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "setMonitorMuted" }, mixer, _ => null));
    }

    [Fact]
    public void TheMasterMuteIsSavedWithoutReplacingIndividualMixMutes()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-monitor-mute-").FullName;
        try
        {
            string path = Path.Combine(directory, "mixer.json");
            Assert.Null(new MixerSettings { MonitorMuted = true, MixMuted = ["monitor2"] }.Save(path));
            MixerSettings restored = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out var warning));
            Assert.Null(warning);
            Assert.True(restored.MonitorMuted);
            Assert.Equal(["monitor2"], restored.MixMuted);
            File.WriteAllText(path, "{}");
            Assert.False(MixerSettings.Load(path)!.MonitorMuted);
        }
        finally { Directory.Delete(directory, true); }
    }
}
