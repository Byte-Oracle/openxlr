using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

public sealed class CaptureInputModelTests
{
    [Fact]
    public async Task CaptureChainsFollowWidthRenameAndRemoval()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var channel = new ChannelViewModel(client, "guest", "Guest", []);
        JsonNode state = JsonNode.Parse("""{"captureSource":"usb.mic","name":"Guest","microphone":true,"chainWidth":1}""")!;
        channel.ApplyFromDaemon(state);
        InsertsViewModel first = Assert.IsType<InsertsViewModel>(channel.Inserts);
        Assert.True(channel.Microphone);
        Assert.True(channel.MonoMeter);
        Assert.True(first.CanMicCheck);
        MicCheckViewModel soundCheck = first.MicCheck;
        soundCheck.Apply(JsonNode.Parse("""{"channel":"guest","mode":"looping","seconds":1.5}"""));
        Assert.False(channel.HasEq);
        Assert.Equal(Localizer.Text("InputChainHint"), first.ChainHint);
        Assert.Equal(Localizer.Text("MonoPickerHint"), first.PickerHint);
        state["name"] = "Guest renamed";
        channel.ApplyFromDaemon(state);
        Assert.Same(first, channel.Inserts);
        Assert.Same(soundCheck, channel.Inserts!.MicCheck);
        Assert.True(soundCheck.Active);
        Assert.Equal("Guest renamed", first.Title);
        first.ResetForNewConnection();
        Assert.False(soundCheck.Active);
        Assert.False(soundCheck.CanLoop);
        soundCheck.Apply(JsonNode.Parse("""{"channel":"guest","mode":"looping","seconds":1.5}"""));

        state["chainWidth"] = 2;
        state["microphone"] = false;
        channel.ApplyFromDaemon(state);
        InsertsViewModel second = Assert.IsType<InsertsViewModel>(channel.Inserts);
        Assert.True(first.IsDetached);
        Assert.NotSame(first, second);
        Assert.False(channel.Microphone);
        Assert.False(channel.MonoMeter);
        Assert.False(second.CanMicCheck);
        Assert.False(soundCheck.Active);
        Assert.Equal(Localizer.Text("InputChainHint"), second.ChainHint);
        Assert.Equal(Localizer.Text("StereoInputPickerHint"), second.PickerHint);

        state.AsObject().Remove("chainWidth");
        state.AsObject().Remove("microphone");
        channel.ApplyFromDaemon(state);
        Assert.Null(channel.Inserts);
        Assert.True(second.IsDetached);
        Assert.False(channel.Microphone);
        state["chainWidth"] = 1;
        channel.ApplyFromDaemon(state);
        InsertsViewModel last = channel.Inserts!;
        Assert.False(last.CanMicCheck);
        channel.Detach();
        Assert.True(last.IsDetached);
        Assert.Null(channel.Inserts);
    }

    [Theory]
    [InlineData("xlr1", true)]
    [InlineData("xlr2", true)]
    [InlineData("aux", false)]
    [InlineData("guest", false)]
    public async Task OlderStatesKeepTheXlrMonoMeterRule(string id, bool mono)
    {
        await using var client = new DaemonClient();
        var channel = new ChannelViewModel(client, id, id, []);
        channel.ApplyFromDaemon(new JsonObject());
        Assert.Equal(mono, channel.Microphone);
        // The XLR strips keep the pair of meter bars they have always drawn.
        Assert.False(channel.MonoMeter);
        Assert.Null(channel.Inserts);
    }

    [Fact]
    public async Task CapturePartsRespectExplicitOrdersAndUpdateWhenWidthDisappears()
    {
        await using var client = new DaemonClient();
        var main = new MainViewModel(client);
        var channel = new ChannelViewModel(client, "guest", "Guest", []);
        JsonNode state = JsonNode.Parse("""{"captureSource":"usb.mic","chainWidth":1,"volume":1}""")!;
        channel.ApplyFromDaemon(state);
        main.Channels.Add(channel);
        using var rack = new InputRackViewModel(main);
        using var matrix = new MixMatrixViewModel(main);
        Assert.True(rack.Strips[0].Has(SkinStripParts.Binding));
        Assert.True(rack.Strips[0].Has(SkinStripParts.Effects));
        Assert.True(matrix.Rows[0].ShowsBinding);
        Assert.True(matrix.Rows[0].Has(SkinStripParts.Effects));
        Assert.Same(channel.Inserts, rack.Strips[0].Inserts);
        Assert.Same(channel.Inserts, matrix.Rows[0].Inserts);
        Assert.Equal("guest", matrix.Rows[0].InsertsKey);
        Assert.False(rack.Strips[0].ShowsCaptureLabel);
        Assert.False(matrix.Rows[0].ShowsCaptureLabel);

        string[] ordered = [SkinStripParts.Gain, SkinStripParts.Identity, SkinStripParts.Meter];
        rack.Configure(SkinStripOptions.InputRowDefault with { ExplicitOrder = ordered });
        matrix.Configure(SkinMatrixOptions.Default with
        {
            RowHead = SkinMatrixOptions.Default.RowHead with { ExplicitOrder = ordered },
        });
        Assert.False(rack.Strips[0].Has(SkinStripParts.Binding));
        Assert.False(rack.Strips[0].Has(SkinStripParts.Effects));
        Assert.False(matrix.Rows[0].ShowsBinding);
        Assert.False(matrix.Rows[0].Has(SkinStripParts.Effects));
        // Without the chip, the strip keeps the connection line it drew before.
        Assert.True(rack.Strips[0].ShowsCaptureLabel);
        Assert.True(matrix.Rows[0].ShowsCaptureLabel);

        rack.Configure(SkinStripOptions.InputRowDefault);
        matrix.Configure(SkinMatrixOptions.Default);
        state["chainWidth"] = null;
        channel.ApplyFromDaemon(state);
        Assert.False(rack.Strips[0].Has(SkinStripParts.Effects));
        Assert.False(matrix.Rows[0].Has(SkinStripParts.Effects));
        Assert.True(rack.Strips[0].Has(SkinStripParts.Binding));
        Assert.True(matrix.Rows[0].ShowsBinding);
    }
}
