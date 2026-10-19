using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

/// <summary>
/// The terminal mixer in generic microphone mode: XLR 1 is fed by a PipeWire
/// source the user picked, the daemon drives no Elgato unit, and the state
/// says so through <c>mixer.primaryInput</c> while <c>connected</c> is false
/// and <c>capabilities</c> is null.
/// </summary>
public sealed class TuiGenericMicTests
{
    /// <summary>The state the daemon sends in generic mode, cut to what the tabs read.</summary>
    private const string GenericJson = """
        {
          "type": "state",
          "daemonVersion": "0.1.71",
          "connected": false,
          "mixer": {
            "primaryInput": { "source": "alsa_input.usb-Blue_Yeti_Nano", "channel": 1 },
            "primaryInputConnected": true,
            "mixes": [
              { "id": "monitor", "name": "Monitor A", "volume": 1, "muted": false, "kind": "monitor" },
              { "id": "stream", "name": "Stream", "volume": 0.8, "muted": false, "kind": "virtualMic" }
            ],
            "channels": [
              { "id": "xlr1", "name": "XLR 1", "levels": { "monitor": 1, "stream": 0.5 },
                "mutedIn": ["monitor"], "hardware": true },
              { "id": "xlr2", "name": "XLR 2", "levels": { "monitor": 1 }, "mutedIn": [], "hardware": true,
                "present": false },
              { "id": "music", "name": "Music", "levels": { "monitor": 0.8, "stream": 1 },
                "mutedIn": [], "hardware": false }
            ],
            "monitorOutputs": ["alsa_output.arctis"],
            "lowCutHz": 0,
            "softClipGuard": false,
            "softClipGuardAvailable": true,
            "inserts": {}
          },
          "devices": [
            { "name": "alsa_output.arctis", "description": "Arctis Nova Elite", "kind": 0, "isOwn": false },
            { "name": "alsa_input.usb-Blue_Yeti_Nano", "description": "Yeti Nano", "kind": 1, "isOwn": false },
            { "name": "alsa_input.pci-0000_00_1f.3", "description": "Built-in Audio", "kind": 1, "isOwn": false },
            { "name": "OpenXLR_chat", "description": "OpenXLR Chat", "kind": 1, "isOwn": true },
            { "name": "OpenXLR_odd", "description": "Not marked own", "kind": 1, "isOwn": false },
            { "name": "alsa_output.arctis.monitor", "description": "Monitor of Arctis", "kind": 1, "isOwn": false }
          ],
          "profiles": []
        }
        """;

    private static (App App, List<string> Sent) Ready(string json = GenericJson, int tab = 0, int width = 140, int height = 36)
    {
        DaemonLink link = new();
        List<string> sent = [];
        link.Sent += sent.Add;
        link.Receive(json);
        App app = new(link, Theme.Material);
        app.ShowTab(tab);
        app.Draw(new Screen(width, height));
        return (app, sent);
    }

    private static string Automatic()
    {
        JsonNode root = JsonNode.Parse(GenericJson)!;
        root["connected"] = true;
        root["device"] = new JsonObject { ["vendor"] = "Elgato", ["model"] = "Wave XLR", ["usbId"] = "0fd9:007d" };
        root["capabilities"] = new JsonObject { ["gain"] = true, ["mute"] = true, ["xlrInputs"] = 1 };
        root["state"] = new JsonObject { ["gainDb"] = 40, ["mute"] = false };
        root["mixer"]!.AsObject().Remove("primaryInput");
        return root.ToJsonString();
    }

    private static string Frame(Screen screen) => string.Join('\n', Enumerable.Range(0, screen.Height)
        .Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen.At(x, y).Ch).ToArray())));

    private static JsonElement Last(List<string> sent) => JsonDocument.Parse(Assert.Single(sent)).RootElement;

    [Fact]
    public void TheStateNamesTheGenericSourceAndListsOnlySourcesTheDaemonTakes()
    {
        DaemonLink link = new();
        link.Receive(GenericJson);
        Snapshot state = link.State!;

        Assert.True(state.Generic);
        Assert.False(state.Connected);
        Assert.True(state.Mixer.PrimaryInputConnected);
        Assert.Equal("Yeti Nano, port 2", state.GenericLabel);
        Assert.Equal("generic microphone", link.Status);
        // Sinks, OpenXLR's own nodes, an OpenXLR name and a monitor tap are all left out.
        Assert.Equal(["alsa_input.usb-Blue_Yeti_Nano", "alsa_input.pci-0000_00_1f.3"],
            state.PrimaryInputChoices.Select(device => device.Name));

        Snapshot automatic = Snapshot.Parse(Automatic())!;
        Assert.False(automatic.Generic);
        Assert.Null(automatic.GenericLabel);
    }

    [Theory]
    [InlineData(140, 36)]
    [InlineData(100, 30)]
    public void TheHeaderAndSidebarNameTheGenericMicrophoneRatherThanOffline(int width, int height)
    {
        (App app, _) = Ready(width: width, height: height);
        Screen screen = new(width, height);
        app.Draw(screen);
        string frame = Frame(screen);

        Assert.Contains("Generic mic: Yeti Nano, port 2", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("no interface", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("Offline", frame, StringComparison.Ordinal);
        if (width >= 120)
        {
            Assert.Contains("GENERIC MIC", frame, StringComparison.Ordinal);
            Assert.DoesNotContain("INTERFACE", frame, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnUnlinkedGenericMicrophoneSaysSoInTheSidebar()
    {
        JsonNode root = JsonNode.Parse(GenericJson)!;
        root["mixer"]!["primaryInputConnected"] = false;
        (App app, _) = Ready(root.ToJsonString());
        Screen screen = new(140, 36);
        app.Draw(screen);
        Assert.Contains("not linked", Frame(screen), StringComparison.Ordinal);
    }

    [Fact]
    public void SpaceOnTheXlr1StripMutesItsSendBecauseThereIsNoHardwareMute()
    {
        (App app, List<string> sent) = Ready();
        app.Handle(new KeyPress(Key.Down));      // XLR 1; XLR 2 is absent in generic mode
        app.Handle(new KeyPress(Key.Char, ' '));

        JsonElement command = Last(sent);
        Assert.Equal("setChannelMuted", command.GetProperty("cmd").GetString());
        Assert.Equal("xlr1", command.GetProperty("channel").GetString());
        Assert.Equal("monitor", command.GetProperty("mix").GetString());
        Assert.False(command.GetProperty("value").GetBoolean());   // muted in Monitor A, so this unmutes
    }

    [Fact]
    public void TheInputsTabKeepsHardwareRowsFadedWithAReasonAndTheSoftwareRowsLive()
    {
        (App app, List<string> sent) = Ready(tab: 2);
        Screen screen = new(140, 36);
        app.Draw(screen);
        string frame = Frame(screen);
        Assert.Contains(InputsView.GenericOff, frame, StringComparison.Ordinal);
        Assert.Contains("set on the mic", frame, StringComparison.Ordinal);
        Assert.Contains("Yeti Nano, port 2", frame, StringComparison.Ordinal);
        Assert.Contains("linked to XLR 1", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("XLR 2", frame, StringComparison.Ordinal);

        app.Handle(new KeyPress(Key.Right));     // gain: the daemon refuses it, so nothing goes out
        app.Handle(new KeyPress(Key.Down));      // hardware mute
        app.Handle(new KeyPress(Key.Char, ' '));
        Assert.Empty(sent);

        // XLR 1's nine control rows, Source and Route, then Gain lock, then the software low cut.
        for (int press = 0; press < 10; press++) app.Handle(new KeyPress(Key.Down));
        app.Handle(new KeyPress(Key.Char, ' '));
        Assert.Empty(sent);                      // the gain lock is a hardware guard
        app.Handle(new KeyPress(Key.Down));
        app.Handle(new KeyPress(Key.Right));
        JsonElement command = Last(sent);
        Assert.Equal("setLowCutHz", command.GetProperty("cmd").GetString());
        Assert.Equal(80, command.GetProperty("value").GetInt32());
    }

    [Fact]
    public void OptionsSendsTheChosenSourceAndPortOnlyOnApply()
    {
        (App app, List<string> sent) = Ready(Automatic(), tab: 7);
        Screen screen = new(140, 36);
        app.Draw(screen);
        Assert.Contains("automatic, the Elgato interface", Frame(screen), StringComparison.Ordinal);

        app.Handle(new KeyPress(Key.Down));      // Source
        app.Handle(new KeyPress(Key.Right));     // the first capture source
        app.Handle(new KeyPress(Key.Down));      // Port
        app.Handle(new KeyPress(Key.Right));     // port 2
        app.Draw(screen);
        Assert.Contains("Yeti Nano", Frame(screen), StringComparison.Ordinal);
        Assert.Contains("not applied", Frame(screen), StringComparison.Ordinal);
        Assert.Empty(sent);

        app.Handle(new KeyPress(Key.Down));      // Apply
        app.Handle(new KeyPress(Key.Enter));
        JsonElement command = Last(sent);
        Assert.Equal("setPrimaryInput", command.GetProperty("cmd").GetString());
        Assert.Equal("alsa_input.usb-Blue_Yeti_Nano", command.GetProperty("source").GetString());
        Assert.Equal(1, command.GetProperty("captureMonoChannel").GetInt32());

        app.Link.Receive($$"""{"type":"commandResult","requestId":"{{command.GetProperty("requestId").GetString()}}"}""");
        app.Draw(screen);
        Assert.Contains("XLR 1 is Yeti Nano, port 2", Frame(screen), StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsReturnsToAutomaticWithoutAPort()
    {
        (App app, List<string> sent) = Ready(tab: 7);
        Screen screen = new(140, 36);
        app.Draw(screen);
        Assert.Contains("generic, Yeti Nano, port 2", Frame(screen), StringComparison.Ordinal);

        app.Handle(new KeyPress(Key.Down));      // Source, on the Yeti Nano
        app.Handle(new KeyPress(Key.Left));      // Automatic Elgato interface
        app.Handle(new KeyPress(Key.Down));      // Port, which automatic does not take
        app.Handle(new KeyPress(Key.Right));
        Assert.Empty(sent);
        app.Draw(screen);
        Assert.Contains("automatic takes no port", Frame(screen), StringComparison.Ordinal);

        app.Handle(new KeyPress(Key.Down));      // Apply
        app.Handle(new KeyPress(Key.Enter));
        JsonElement command = Last(sent);
        Assert.Equal("setPrimaryInput", command.GetProperty("cmd").GetString());
        Assert.Equal(JsonValueKind.Null, command.GetProperty("source").ValueKind);
        Assert.False(command.TryGetProperty("captureMonoChannel", out _));

        app.Link.Receive($$"""{"type":"commandResult","requestId":"{{command.GetProperty("requestId").GetString()}}","error":"refused for the test"}""");
        app.Draw(screen);
        Assert.Contains("refused for the test", Frame(screen), StringComparison.Ordinal);
        Assert.Contains("not applied", Frame(screen), StringComparison.Ordinal);   // the draft survives a refusal
    }

    [Fact]
    public void AnUnpluggedSavedSourceStaysChosen()
    {
        JsonNode root = JsonNode.Parse(GenericJson)!;
        root["mixer"]!["primaryInput"]!["source"] = "alsa_input.gone";
        root["mixer"]!["primaryInputConnected"] = false;
        (App app, _) = Ready(root.ToJsonString(), tab: 7);
        Screen screen = new(140, 36);
        app.Draw(screen);
        string frame = Frame(screen);
        Assert.Contains("alsa_input.gone (missing)", frame, StringComparison.Ordinal);
        Assert.Contains("not linked", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("not applied", frame, StringComparison.Ordinal);
    }
}
