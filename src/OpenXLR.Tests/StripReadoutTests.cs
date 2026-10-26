using System.Reflection;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

/// <summary>
/// The Aux In and Line In strips as a projection, with no window. Line In is
/// a strip of its own, with its gain where the interface has the input. Each
/// strip's readout shows the input's level or gain, switches to the channel's
/// volume while that volume's fader moves, follows it, and returns to the
/// gain once the hold after the fader stops has run out; a move inside the
/// hold starts it again. The input rack, the mix matrix and the classic
/// INPUTS panel read the same readout.
///
/// The window's model reads ui.json when it is made, from the configuration
/// directory the tests of that collection redirect for the whole process,
/// so these run with them rather than beside them.
/// </summary>
[Collection("xdg-config")]
public sealed class StripReadoutTests
{
    private static readonly MethodInfo ApplyMixer =
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static string State(double auxVolume, double lineVolume, bool linePresent = true) => $$"""
        {"channels":[{"id":"xlr1","name":"XLR 1","hardware":true,"volume":1},
                     {"id":"aux","name":"Aux In","hardware":true,"volume":{{auxVolume}}},
                     {"id":"line","name":"Line In","hardware":true,"volume":{{lineVolume}},"present":{{(linePresent ? "true" : "false")}}},
                     {"id":"game","name":"Game","volume":1}],
         "mixes":[{"id":"monitor","name":"Monitor A"}]}
        """;

    private static MainViewModel Model(bool linePresent = true)
    {
        var model = new MainViewModel(new DaemonClient()) { CapAuxInput = true, CapLineInput = true };
        ApplyMixer.Invoke(model, [JsonNode.Parse(State(0.8, 0.6, linePresent))]);
        model.RefreshBindings();
        model.AuxLevelDb = -12;
        model.LineInGainDb = 20;
        return model;
    }

    [Fact]
    public void LineInIsAStripOfItsOwnWithItsGainWhereTheInterfaceHasTheInput()
    {
        MainViewModel model = Model();
        using var rack = new InputRackViewModel(model);
        using var matrix = new MixMatrixViewModel(model);
        InputStripViewModel strip = rack.Strips.Single(s => s.Channel.Id == "line");
        MatrixRowViewModel row = matrix.Rows.Single(r => r.Channel.Id == "line");

        Assert.Equal(InputStripKind.LineIn, strip.Kind);
        Assert.Equal(InputStripKind.LineIn, row.Kind);
        Assert.Equal(InputStripKind.AuxIn, rack.Strips.Single(s => s.Channel.Id == "aux").Kind);
        Assert.Equal("line", StripTemplateSelector.Key(strip));
        Assert.Equal("line", StripTemplateSelector.Key(row));
        // A gain and no lock: the level lock is Aux In's.
        Assert.DoesNotContain(SkinStripParts.HardwareControls, InputRackViewModel.PartsOf(InputStripKind.LineIn));
        Assert.Contains(SkinStripParts.Gain, strip.Spec.Parts);
        Assert.True(row.HasGainFader);
        Assert.True(row.Stacked);
        Assert.True(row.ShowsReadout);
        Assert.True(model.ShowLineInRow);

        // Without the input the strip keeps its volume and loses the gain and its readout.
        model.CapLineInput = false;
        Assert.False(row.HasGainFader);
        Assert.False(row.ShowsReadout);
        Assert.True(row.VolumeShowsMeter);
        Assert.Contains(SkinStripParts.Gain, strip.Spec.Parts);
        Assert.False(model.LineStrip!.HasGainReadout);
        Assert.Equal("", model.LineStrip.Readout);
        Assert.False(model.ShowLineInRow);
        model.CapLineInput = true;
        Assert.True(row.ShowsReadout);
        Assert.Equal("20 dB", row.Readout);

        // A source layout without Line In: the daemon says absent, and the INPUTS panel drops the row.
        ApplyMixer.Invoke(model, [JsonNode.Parse(State(0.8, 0.6, linePresent: false))]);
        model.RefreshBindings();
        Assert.False(model.ShowLineInRow);
        Assert.False(model.LineStrip.Visible);
    }

    [Theory]
    [InlineData("aux")]
    [InlineData("line")]
    public void TheReadoutShowsTheVolumeWhileItsFaderMovesAndTheGainASecondAfter(string id)
    {
        MainViewModel model = Model();
        using var rack = new InputRackViewModel(model);
        using var matrix = new MixMatrixViewModel(model);
        MatrixRowViewModel row = matrix.Rows.Single(r => r.Channel.Id == id);
        ChannelViewModel channel = row.Channel;
        ChannelViewModel other = model.Channels.Single(c => c.Id == (id == "aux" ? "line" : "aux"));
        Assert.Same(channel, rack.Strips.Single(s => s.Channel.Id == id).Channel);
        string gain = id == "aux" ? "-12 dB" : "20 dB";
        string gainName = Localizer.Text(id == "aux" ? "USBAuxIn" : "LineInGain");
        string volume = id == "aux" ? "80%" : "60%";
        string Panel() => id == "aux" ? model.AuxReadout : model.LineInReadout;
        string PanelName() => id == "aux" ? model.AuxReadoutName : model.LineInReadoutName;
        var hold = new ManualHold();
        channel.StartReadoutHold = hold.Start;
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // The gain, in every place the strip is drawn.
        Assert.True(channel.HasGainReadout);
        Assert.Equal(gain, channel.Readout);
        Assert.Equal(gainName, channel.ReadoutName);
        Assert.Equal(gain, row.Readout);
        Assert.Equal(gainName, row.ReadoutName);
        Assert.Equal(gain, Panel());
        Assert.Equal(gainName, PanelName());

        // The fader moves: the volume, under the volume's name, in every place, and the other strip keeps its gain.
        row.VolumeAdjusting = true;
        Assert.True(channel.VolumeAdjusting);
        Assert.True(channel.ShowsVolumeReadout);
        Assert.Contains(nameof(MatrixRowViewModel.Readout), raised);
        Assert.Equal(volume, channel.Readout);
        Assert.Equal(volume, row.Readout);
        Assert.Equal(volume, Panel());
        Assert.Equal(Localizer.Format("ChannelVolumeName", channel.Name), channel.ReadoutName);
        Assert.Equal(Localizer.Format("ChannelVolumeName", channel.Name), PanelName());
        Assert.False(other.ShowsVolumeReadout);
        Assert.Empty(hold.Handles);

        // It follows the fader.
        raised.Clear();
        channel.Volume = 0.45;
        Assert.Contains(nameof(MatrixRowViewModel.Readout), raised);
        Assert.Equal("45%", row.Readout);
        Assert.Equal("45%", Panel());

        // The fader stops: the volume stays for the hold, then the gain comes back.
        row.VolumeAdjusting = false;
        Assert.Equal(TimeSpan.FromSeconds(1), ChannelViewModel.VolumeReadoutHold);
        Assert.Equal(ChannelViewModel.VolumeReadoutHold, Assert.Single(hold.Handles).After);
        Assert.Equal("45%", channel.Readout);
        // A gain change meanwhile waits for the hold.
        if (id == "aux") model.AuxLevelDb = -20; else model.LineInGainDb = 33;
        gain = id == "aux" ? "-20 dB" : "33 dB";
        Assert.Equal("45%", channel.Readout);
        hold.Handles[0].Elapse();
        Assert.False(channel.ShowsVolumeReadout);
        Assert.Equal(gain, channel.Readout);
        Assert.Equal(gain, row.Readout);
        Assert.Equal(gain, Panel());
        Assert.Equal(gainName, PanelName());

        // A move inside the hold starts it again; the hold it replaced does nothing when it ends.
        channel.VolumeAdjusting = true;
        channel.VolumeAdjusting = false;
        channel.VolumeAdjusting = true;
        Assert.True(hold.Handles[1].Disposed);
        hold.Handles[1].Elapse();
        Assert.True(channel.ShowsVolumeReadout);
        channel.VolumeAdjusting = false;
        hold.Handles[2].Elapse();
        Assert.Equal(gain, channel.Readout);

        // A channel that leaves the mixer drops its hold.
        channel.VolumeAdjusting = true;
        channel.VolumeAdjusting = false;
        channel.Detach();
        Assert.True(hold.Handles[3].Disposed);
    }

    [Fact]
    public void AChannelWithoutAGainReadoutKeepsItsVolumeElsewhere()
    {
        MainViewModel model = Model();
        ChannelViewModel game = model.Channels.Single(c => c.Id == "game");
        Assert.False(game.HasGainReadout);
        Assert.Equal("", game.Readout);
        game.StartReadoutHold = new ManualHold().Start;
        game.VolumeAdjusting = true;
        Assert.Equal("100%", game.Readout);
        // The INPUTS panel's rows read their own strips only.
        Assert.Equal("-12 dB", model.AuxReadout);
        Assert.Equal("20 dB", model.LineInReadout);
    }

    [Fact]
    public void TheLineInSlidersStopAtTheCapAndTheReadoutShowsTheUnitsOwnGain()
    {
        var model = new MainViewModel(new DaemonClient());
        SliderSync.Forget("line");
        typeof(MainViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(JsonNode)])!
            .Invoke(model, [JsonNode.Parse("""
                {"connected":true,"capabilities":{"lineInput":true,"auxInput":true,"gainMaxDb":80,"lineInGainMaxDb":40},
                 "state":{"gainDb":30,"lineInGainDb":52}}
                """)!]);
        Assert.Equal(40, model.LineInGainMaxDb);
        Assert.Equal(80, model.GainMaxDb);
        // A unit set higher elsewhere: the readout says so, the slider sits at its top.
        Assert.Equal(52, model.LineInGainDb);
        Assert.Equal(40, model.LineInGainSliderDb);
        Assert.Equal("52 dB", model.LineInReadout);

        // Moving the slider writes the gain; putting it where it already sits writes nothing.
        model.LineInGainSliderDb = 40;
        Assert.Equal(52, model.LineInGainDb);
        model.LineInGainSliderDb = 30;
        Assert.Equal((30, 30), (model.LineInGainDb, model.LineInGainSliderDb));
        Assert.Equal("30 dB", model.LineInReadout);
        SliderSync.Forget("line");
    }
}

/// <summary>A readout hold run by hand: each start is kept, and ends only when the test says so.</summary>
internal sealed class ManualHold
{
    public List<Handle> Handles { get; } = [];

    public IDisposable Start(TimeSpan after, Action elapsed)
    {
        var handle = new Handle(after, elapsed);
        Handles.Add(handle);
        return handle;
    }

    internal sealed class Handle(TimeSpan after, Action elapsed) : IDisposable
    {
        public TimeSpan After => after;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
        /// <summary>The hold ends, as a timer that was disposed would still call back if it was already queued.</summary>
        public void Elapse() => elapsed();
    }
}
