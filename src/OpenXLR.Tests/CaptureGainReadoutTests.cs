using System.Reflection;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

/// <summary>
/// A capture input's own gain as a projection, with no window: the strip has
/// a gain where the daemon reports one, its readout shows that gain, switches
/// to the channel's volume while the volume fader moves and returns to the
/// gain after the hold, as Aux In and Line In do. A gain another program set
/// arrives in the state and is shown without being sent back. A capture input
/// that carries a Wave unit's input gets no such gain.
///
/// The window's model reads ui.json when it is made, from the configuration
/// directory the tests of that collection redirect for the whole process.
/// </summary>
[Collection("xdg-config")]
public sealed class CaptureGainReadoutTests
{
    private static readonly MethodInfo ApplyMixer =
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static string State(string? gain, string unitInput = "null") => $$"""
        {"channels":[{"id":"xlr1","name":"XLR 1","hardware":true,"volume":1},
                     {"id":"brio","name":"Brio","captureSource":"alsa_input.usb-webcam","captureConnected":true,
                      "microphone":false,"chainWidth":2,"volume":0.8,"unitInput":{{unitInput}}{{(gain is null ? "" : ",\"captureGain\":" + gain)}}},
                     {"id":"game","name":"Game","volume":1}],
         "mixes":[{"id":"monitor","name":"Monitor A"}]}
        """;

    private const string Hardware = """{"gainDb":36,"minDb":18,"maxDb":54,"hardware":true}""";

    private static MainViewModel Model(string? gain = Hardware)
    {
        var model = new MainViewModel(new DaemonClient());
        ApplyMixer.Invoke(model, [JsonNode.Parse(State(gain))]);
        model.RefreshBindings();
        return model;
    }

    [Fact]
    public void ACaptureInputWithAGainDrawsItAndItsReadoutEverywhere()
    {
        MainViewModel model = Model();
        using var rack = new InputRackViewModel(model);
        using var matrix = new MixMatrixViewModel(model);
        InputStripViewModel strip = rack.Strips.Single(s => s.Channel.Id == "brio");
        MatrixRowViewModel row = matrix.Rows.Single(r => r.Channel.Id == "brio");
        ChannelViewModel channel = strip.Channel;

        Assert.Equal(InputStripKind.Capture, strip.Kind);
        Assert.True(channel.HasCaptureGain);
        Assert.True(channel.ShowsCaptureGain);
        Assert.True(channel.CaptureGainHardware);
        Assert.Equal((18, 54), (channel.CaptureGainMinDb, channel.CaptureGainMaxDb));
        Assert.Equal(36, channel.CaptureGainDb);
        Assert.Equal(Localizer.Format("CaptureGainName", "Brio"), channel.CaptureGainName);
        Assert.Equal(Localizer.Text("CaptureGainHardwareTip"), channel.CaptureGainTip);
        Assert.True(strip.ShowsCaptureGain);
        Assert.Contains(SkinStripParts.Gain, strip.Spec.Parts);
        Assert.True(row.ShowsCaptureGain);
        Assert.True(row.HasGainFader);
        Assert.True(row.Stacked);
        Assert.True(row.ShowsReadout);
        Assert.False(row.VolumeShowsMeter);
        Assert.Equal("36 dB", channel.Readout);
        Assert.Equal("36 dB", row.Readout);
        Assert.Equal(channel.CaptureGainName, row.ReadoutName);

        // An application channel has neither.
        Assert.False(model.Channels.Single(c => c.Id == "game").HasCaptureGain);
        Assert.False(matrix.Rows.Single(r => r.Channel.Id == "game").ShowsReadout);
    }

    [Fact]
    public void TheReadoutShowsTheVolumeWhileItsFaderMovesAndTheGainASecondAfter()
    {
        MainViewModel model = Model();
        using var matrix = new MixMatrixViewModel(model);
        MatrixRowViewModel row = matrix.Rows.Single(r => r.Channel.Id == "brio");
        ChannelViewModel channel = row.Channel;
        var hold = new ManualHold();
        channel.StartReadoutHold = hold.Start;

        row.VolumeAdjusting = true;
        Assert.Equal("80%", channel.Readout);
        Assert.Equal("80%", row.Readout);
        Assert.Equal(Localizer.Format("ChannelVolumeName", "Brio"), row.ReadoutName);
        channel.Volume = 0.5;
        Assert.Equal("50%", row.Readout);
        row.VolumeAdjusting = false;
        Assert.Equal(ChannelViewModel.VolumeReadoutHold, Assert.Single(hold.Handles).After);
        Assert.Equal("50%", row.Readout);
        hold.Handles[0].Elapse();
        Assert.Equal("36 dB", row.Readout);
        Assert.Equal(channel.CaptureGainName, row.ReadoutName);
    }

    [Fact]
    public void AGainSetElsewhereIsShownAndNotSentBack()
    {
        MainViewModel model = Model();
        ChannelViewModel channel = model.Channels.Single(c => c.Id == "brio");
        SliderSync.Forget("capgain:brio");
        ApplyMixer.Invoke(model, [JsonNode.Parse(State("""{"gainDb":24.5,"minDb":18,"maxDb":54,"hardware":true}"""))]);
        Assert.Equal(24.5, channel.CaptureGainDb);
        Assert.Equal("24.5 dB", channel.Readout);
        Assert.False(SliderSync.RecentlyTouched("capgain:brio"));

        // A move in the UI is sent, and the next state does not pull the slider back meanwhile.
        channel.CaptureGainDb = 30;
        Assert.True(SliderSync.RecentlyTouched("capgain:brio"));
        ApplyMixer.Invoke(model, [JsonNode.Parse(State(Hardware))]);
        Assert.Equal(30, channel.CaptureGainDb);
        SliderSync.Forget("capgain:brio");

        // A software level and its range; the range moves first, so the gain is never clamped on the way.
        ApplyMixer.Invoke(model, [JsonNode.Parse(State("""{"gainDb":-6,"minDb":-60,"maxDb":10,"hardware":false}"""))]);
        Assert.Equal((-60, 10, -6), (channel.CaptureGainMinDb, channel.CaptureGainMaxDb, channel.CaptureGainDb));
        Assert.Equal(Localizer.Text("CaptureGainSoftwareTip"), channel.CaptureGainTip);
        ApplyMixer.Invoke(model, [JsonNode.Parse(State(Hardware))]);
        Assert.Equal((18, 54, 36), (channel.CaptureGainMinDb, channel.CaptureGainMaxDb, channel.CaptureGainDb));
        Assert.False(SliderSync.RecentlyTouched("capgain:brio"));
    }

    [Fact]
    public void WithoutAGainTheStripKeepsItsVolumeAlone()
    {
        // The source went away, or the channel takes a Wave unit's input: the daemon sends no gain.
        MainViewModel model = Model();
        using var rack = new InputRackViewModel(model);
        using var matrix = new MixMatrixViewModel(model);
        ChannelViewModel channel = model.Channels.Single(c => c.Id == "brio");
        MatrixRowViewModel row = matrix.Rows.Single(r => r.Channel.Id == "brio");
        ApplyMixer.Invoke(model, [JsonNode.Parse(State(null))]);
        Assert.False(channel.HasCaptureGain);
        Assert.False(channel.HasGainReadout);
        Assert.Equal("", channel.Readout);
        Assert.False(rack.Strips.Single(s => s.Channel.Id == "brio").ShowsCaptureGain);
        Assert.False(row.HasGainFader);
        Assert.False(row.ShowsReadout);
        Assert.True(row.ShowsVolume);
        Assert.True(row.VolumeShowsMeter);

        // A strip that draws a unit's gain never draws a capture gain beside it, whatever the state says.
        ApplyMixer.Invoke(model, [JsonNode.Parse(State(Hardware))]);
        Assert.True(row.ShowsCaptureGain);
        channel.ShownUnitInput = 1;
        Assert.False(channel.ShowsCaptureGain);
        Assert.False(row.ShowsCaptureGain);
        Assert.False(rack.Strips.Single(s => s.Channel.Id == "brio").ShowsCaptureGain);
    }
}
