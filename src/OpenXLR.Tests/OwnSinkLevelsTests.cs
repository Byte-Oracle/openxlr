using System.Text;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class OwnSinkLevelsTests
{
    private static byte[] Dump(params string[] nodes) => Encoding.UTF8.GetBytes("[" + string.Join(",", nodes) + "]");

    private static string Node(string name, string mediaClass, string volumes, bool mute)
        => "{\"id\": 1, \"type\": \"PipeWire:Interface:Node\", \"info\": {\"props\": {\"node.name\": \"" + name +
           "\", \"media.class\": \"" + mediaClass + "\"}, \"params\": {\"Props\": [{\"volume\": 1.0, \"channelVolumes\": [" +
           volumes + "], \"mute\": " + (mute ? "true" : "false") + "}]}}}";

    [Fact]
    public void OnlyOurSinksAreListedWithTheirLowestChannelVolume()
    {
        byte[] json = Dump(
            Node("OpenXLR_ch_xlr1", "Audio/Sink", "0.166378, 0.166378", false),
            Node("OpenXLR_mix_stream", "Audio/Sink", "1.0, 1.0", true),
            Node("OpenXLR_ch_game", "Audio/Sink", "1.0, 0.5", false),
            Node("OpenXLR_ch_xlr1.monitor", "Audio/Source", "0.2, 0.2", false),
            Node("alsa_output.usb-Elgato_Wave_XLR_Pro-00.multichannel-output", "Audio/Sink", "1.0", false));
        var levels = PipeWireAdapter.OwnSinkLevels(json);
        Assert.Equal(["OpenXLR_ch_xlr1", "OpenXLR_mix_stream", "OpenXLR_ch_game"], levels.Select(l => l.Name));
        Assert.Equal(0.166378, levels[0].Volume, 6);
        Assert.False(levels[0].Muted);
        Assert.True(levels[1].Muted);
        Assert.Equal(0.5, levels[2].Volume);
    }

    [Fact]
    public void ABrokenDumpYieldsNothing()
        => Assert.Empty(PipeWireAdapter.OwnSinkLevels(Encoding.UTF8.GetBytes("[{\"type\": \"PipeWire:Interface:Node\"")));
    [Theory]
    [InlineData("0, 0", 0)]
    [InlineData("0.125, 0.125", 0.5)]
    [InlineData("1, 1", 1)]
    [InlineData("1.728, 1.728", 1.2)]
    [InlineData("3.375, 3.375", 1.5)]
    [InlineData("0.125, 1", 1)]
    public void DesktopPercentagesUseTheCubeRootOfTheLoudestChannel(string channels, double expected)
    {
        var sink = Assert.Single(PipeWireAdapter.OwnSinkLevels(Dump(Node("OpenXLR_mix_monitor", "Audio/Sink", channels, false))));
        Assert.Equal(expected, sink.DesktopVolume);
    }

    private static string Leg(string name, string serial, string volumes, bool mute)
        => "{\"id\": 7, \"type\": \"PipeWire:Interface:Node\", \"info\": {\"props\": {\"node.name\": \"" + name +
           "\", \"media.class\": \"Stream/Output/Audio\", \"object.serial\": " + serial + "}, \"params\": {\"Props\": [{\"volume\": 1.0, \"channelVolumes\": [" +
           volumes + "], \"mute\": " + (mute ? "true" : "false") + "}]}}}";

    [Fact]
    public void CombineLegsAreListedBySerialWithTheDesktopPercentageAndMute()
    {
        byte[] json = Dump(
            // 60% written through pactl lands as 0.6 cubed, give or take the 16-bit volume step.
            Leg("output.OpenXLR_ch_test_OpenXLR_mix_monitor2", "65", "0.215991, 0.215991", false),
            Leg("output.OpenXLR_ch_test_OpenXLR_mix_chat", "\"66\"", "1.0, 1.0", true),
            Leg("Firefox", "70", "0.5, 0.5", false),
            Leg("output.OpenXLR_ch_test_OpenXLR_mix_stream", "\"not a serial\"", "1.0, 1.0", false),
            Node("OpenXLR_mix_monitor2", "Audio/Sink", "1.0, 1.0", false));
        var legs = PipeWireAdapter.CombineLegLevels(json);
        Assert.Equal([65, 66], legs.Keys.Order());
        Assert.Equal((60, false), legs[65]);
        Assert.Equal((100, true), legs[66]);
        Assert.Equal(60, PipeWireAdapter.SinkInputPercent(0.6));
        Assert.Equal(100, PipeWireAdapter.SinkInputPercent(1.4));
    }
}
