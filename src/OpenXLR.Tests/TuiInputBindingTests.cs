using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

/// <summary>
/// The terminal mixer with XLR 1 bound to another microphone. The interface
/// stays driven, so <c>connected</c> and <c>capabilities</c> describe it as
/// always; each channel's <c>binding</c> says what feeds it and
/// <c>unitInput</c> names the channel that carries the interface's own
/// microphone, which here is a capture channel on the Pro's first port.
/// </summary>
public sealed class TuiInputBindingTests
{
    private const string Unit = "0fd9:00b4@0123456789abcdef";

    /// <summary>The state the daemon sends while XLR 1 takes a Yeti Nano, cut to what the tabs read.</summary>
    private const string BoundJson = """
        {
          "type": "state",
          "daemonVersion": "0.1.71",
          "connected": true,
          "device": { "vendor": "Elgato", "model": "Wave XLR Pro", "usbId": "0fd9:00b4" },
          "capabilities": { "gain": true, "mute": true, "lowCut": true, "phantom": true, "clipGuard": true,
                            "compressor": true, "voiceTune": true, "expander": true, "xlrInputs": 2,
                            "xlrJacks": true, "hpOutputs": 2, "hpVolume": true },
          "state": { "gainDb": 52, "mute": false, "gain2Db": 30, "mute2": true },
          "mixer": {
            "mixes": [
              { "id": "monitor", "name": "Monitor A", "volume": 1, "muted": false, "kind": "monitor" },
              { "id": "stream", "name": "Stream", "volume": 0.8, "muted": false, "kind": "virtualMic" }
            ],
            "channels": [
              { "id": "xlr1", "name": "Mic 1", "levels": { "monitor": 1, "stream": 0.5 }, "mutedIn": ["monitor"],
                "hardware": true,
                "binding": { "kind": "source", "source": "alsa_input.usb-Blue_Yeti_Nano", "pair": 0, "monoChannel": 1 },
                "bindingConnected": true },
              { "id": "xlr2", "name": "XLR 2", "levels": { "monitor": 1, "stream": 1 }, "mutedIn": [],
                "hardware": true, "binding": { "kind": "unit", "pair": 1 }, "bindingConnected": true,
                "unitInput": { "unit": "0fd9:00b4@0123456789abcdef", "input": 2, "primary": true } },
              { "id": "music", "name": "Music", "levels": { "monitor": 0.8, "stream": 1 }, "mutedIn": [],
                "hardware": false, "binding": { "kind": "apps" }, "volume": 1 },
              { "id": "cap1", "name": "Pro mic", "levels": { "monitor": 1, "stream": 1 }, "mutedIn": ["stream"],
                "hardware": false, "captureSource": "alsa_input.usb-Elgato_Wave_XLR_Pro", "capturePair": 0,
                "captureMonoChannel": 0, "captureConnected": true, "volume": 1,
                "binding": { "kind": "source", "source": "alsa_input.usb-Elgato_Wave_XLR_Pro", "pair": 0, "monoChannel": 0 },
                "unitInput": { "unit": "0fd9:00b4@0123456789abcdef", "input": 1, "primary": true } }
            ],
            "monitorOutputs": ["alsa_output.arctis"],
            "lowCutHz": 80,
            "softClipGuard": false,
            "softClipGuardAvailable": true,
            "inserts": {}
          },
          "devices": [
            { "name": "alsa_output.arctis", "description": "Arctis Nova Elite", "kind": 0, "isOwn": false },
            { "name": "alsa_input.usb-Blue_Yeti_Nano", "description": "Yeti Nano", "kind": 1, "isOwn": false,
              "channels": 2, "positions": ["FL", "FR"] },
            { "name": "alsa_input.usb-Logitech_BRIO", "description": "BRIO 4K Stream Edition", "kind": 1, "isOwn": false,
              "channels": 2, "positions": ["FL", "FR"] },
            { "name": "alsa_input.usb-Elgato_Wave_XLR_Pro", "description": "Wave XLR Pro", "kind": 1, "isOwn": false,
              "drivenUnit": "0fd9:00b4@0123456789abcdef" },
            { "name": "alsa_input.usb-Elgato_Wave_3", "description": "Wave:3", "kind": 1, "isOwn": false,
              "drivenUnit": "0fd9:0070@fedcba9876543210" },
            { "name": "OpenXLR_chat", "description": "OpenXLR Chat", "kind": 1, "isOwn": true },
            { "name": "OpenXLR_odd", "description": "Not marked own", "kind": 1, "isOwn": false },
            { "name": "alsa_output.arctis.monitor", "description": "Monitor of Arctis", "kind": 1, "isOwn": false }
          ],
          "waveInterfaces": [
            { "id": "0fd9:00b4@0123456789abcdef", "name": "Wave XLR Pro", "active": true, "enabled": false,
              "connected": true },
            { "id": "0fd9:0070@fedcba9876543210", "name": "Wave:3", "active": false, "enabled": false,
              "connected": true, "capabilities": { "gain": true, "mute": true, "clipGuard": true, "gainMaxDb": 40 },
              "state": { "gainDb": 30, "mute": false, "clipGuard": true } }
          ],
          "profiles": []
        }
        """;

    private const string Wave3 = "0fd9:0070@fedcba9876543210";

    /// <summary>XLR 2 bound to the Wave:3, driven as an additional interface, which carries its input.</summary>
    private static string OnWave3() => Edit(root =>
    {
        JsonNode xlr2 = Channel(root, "xlr2");
        xlr2["name"] = "Mic 2";
        xlr2["binding"] = new JsonObject
        {
            ["kind"] = "source", ["source"] = "alsa_input.usb-Elgato_Wave_3", ["pair"] = 0, ["monoChannel"] = 0,
            ["unit"] = Wave3,
        };
        xlr2["unitInput"] = new JsonObject { ["unit"] = Wave3, ["input"] = 1, ["primary"] = false };
        root["waveInterfaces"]![1]!["enabled"] = true;
    });

    private static (App App, List<string> Sent) Ready(string json = BoundJson, int tab = 0, int width = 140, int height = 36)
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

    private static string Edit(Action<JsonNode> change)
    {
        JsonNode root = JsonNode.Parse(BoundJson)!;
        change(root);
        return root.ToJsonString();
    }

    private static JsonNode Channel(JsonNode root, string id) =>
        root["mixer"]!["channels"]!.AsArray().First(channel => (string?)channel!["id"] == id)!;

    /// <summary>XLR 1 back on the interface and no capture channel: the state before any binding.</summary>
    private static string Unbound() => Edit(root =>
    {
        JsonNode xlr1 = Channel(root, "xlr1");
        xlr1["name"] = "XLR 1";
        xlr1["binding"] = new JsonObject { ["kind"] = "unit", ["pair"] = 0 };
        xlr1["unitInput"] = new JsonObject { ["unit"] = Unit, ["input"] = 1, ["primary"] = true };
        JsonArray channels = root["mixer"]!["channels"]!.AsArray();
        channels.Remove(Channel(root, "cap1"));
    });

    private static string Frame(Screen screen) => string.Join('\n', Enumerable.Range(0, screen.Height)
        .Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen.At(x, y).Ch).ToArray())));

    private static string Draw(App app, int width = 140, int height = 36)
    {
        Screen screen = new(width, height);
        app.Draw(screen);
        return Frame(screen);
    }

    private static JsonElement Last(List<string> sent) => JsonDocument.Parse(Assert.Single(sent)).RootElement;

    private static void Press(App app, Key key, int times = 1)
    {
        for (int i = 0; i < times; i++) app.Handle(new KeyPress(key));
    }

    // Down from the first gain on Inputs: eight more rows of input 1, then
    // input 2's gain and its eight, then the first binding's source.
    private const int ToXlr1Source = 18;
    private const int ToXlr2Source = ToXlr1Source + 4;

    [Fact]
    public void TheStateNamesWhatFeedsEachStripAndWhichChannelCarriesTheInterfacesInputs()
    {
        DaemonLink link = new();
        link.Receive(BoundJson);
        Snapshot state = link.State!;
        ChannelEntry xlr1 = state.Strip(1)!, xlr2 = state.Strip(2)!;
        ChannelEntry capture = state.Mixer.Channels.Single(channel => channel.Id == "cap1");

        Assert.Equal("connected", link.Status);
        Assert.True(xlr1.SourceBound);
        Assert.False(xlr1.UnitBound);
        Assert.True(xlr2.UnitBound);
        Assert.False(capture.Bindable);
        Assert.Equal("Yeti Nano, port 2", state.BindingLabel(xlr1));
        Assert.Equal("Interface jack (XLR 2)", state.BindingLabel(xlr2));

        Assert.Same(capture, state.Carrier(1));
        Assert.Same(xlr2, state.Carrier(2));
        Assert.Null(state.UnitMute(xlr1));
        Assert.Equal(new UnitControl(null, "mute"), state.UnitMute(capture));
        Assert.Equal(new UnitControl(null, "mute2"), state.UnitMute(xlr2));
        Assert.Equal("Input 1 on Pro mic", state.InputHeading(1));
        Assert.Equal("XLR 2", state.InputHeading(2));

        // Sinks, OpenXLR's own nodes, an OpenXLR name, a monitor tap and the
        // primary unit's own node are left out; another unit's node is offered.
        Assert.Equal(["alsa_input.usb-Blue_Yeti_Nano", "alsa_input.usb-Logitech_BRIO", "alsa_input.usb-Elgato_Wave_3"],
            state.BindingChoices.Select(device => device.Name));
        Assert.Equal(Wave3, state.UndrivenUnit("alsa_input.usb-Elgato_Wave_3")?.Id);
        Assert.Null(state.UndrivenUnit("alsa_input.usb-Blue_Yeti_Nano"));
    }

    [Fact]
    public void WithoutUnitInputAStripOnTheInterfaceStillCarriesItsInput()
    {
        // A daemon that predates bindings sends neither field: XLR 1 and XLR 2
        // are the interface's inputs, as they always were.
        Snapshot old = Snapshot.Parse(Edit(root =>
        {
            foreach (JsonNode? channel in root["mixer"]!["channels"]!.AsArray())
            {
                channel!.AsObject().Remove("binding");
                channel.AsObject().Remove("unitInput");
            }
            Channel(root, "xlr1")["name"] = "XLR 1";
        }))!;
        Assert.Equal(new UnitControl(null, "mute"), old.UnitMute(old.Strip(1)!));
        Assert.Equal(new UnitControl(null, "mute2"), old.UnitMute(old.Strip(2)!));
        Assert.Equal("XLR 1", old.InputHeading(1));

        // The daemon names input 2 but not input 1: nothing carries input 1.
        Snapshot elsewhere = Snapshot.Parse(Edit(root => Channel(root, "cap1").AsObject().Remove("unitInput")))!;
        Assert.Null(elsewhere.Carrier(1));
        Assert.Null(elsewhere.UnitMute(elsewhere.Strip(1)!));
        Assert.Equal("Input 1, not in the mixer", elsewhere.InputHeading(1));

        // No interface connected: no strip keys a hardware mute the daemon would refuse.
        Snapshot offline = Snapshot.Parse(Edit(root => root["connected"] = false))!;
        Assert.Null(offline.UnitMute(offline.Strip(2)!));
    }

    [Fact]
    public void AWave3StripIsMicOneAndItsOwnInputIsTheBuiltInMicrophone()
    {
        Snapshot state = Snapshot.Parse(Edit(root =>
        {
            root["capabilities"]!["xlrJacks"] = false;
            root["capabilities"]!["xlrInputs"] = 1;
            JsonNode xlr1 = Channel(root, "xlr1");
            xlr1["binding"] = new JsonObject { ["kind"] = "unit", ["pair"] = 0 };
            xlr1["unitInput"] = new JsonObject { ["unit"] = Unit, ["input"] = 1, ["primary"] = true };
            Channel(root, "cap1").AsObject().Remove("unitInput");
        }))!;
        Assert.Equal("Mic 1", state.InputHeading(1));
        Assert.Equal("Built-in microphone", state.BindingLabel(state.Strip(1)!));
        Assert.Equal(new UnitControl(null, "mute"), state.UnitMute(state.Strip(1)!));
    }

    [Theory]
    [InlineData(140, 36)]
    [InlineData(100, 30)]
    public void TheHeaderNamesTheInterfaceWhileAStripTakesAnotherMicrophone(int width, int height)
    {
        (App app, _) = Ready(width: width, height: height);
        string frame = Draw(app, width, height);
        Assert.Contains("Elgato Wave XLR Pro", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("Generic", frame, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no interface", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRailShowsTheInterfacesGainsByTheChannelsThatCarryThem()
    {
        (App app, _) = Ready();
        string frame = Draw(app);
        Assert.Contains("INTERFACE", frame, StringComparison.Ordinal);
        Assert.Contains("In 1  52 dB", frame, StringComparison.Ordinal);
        Assert.Contains("XLR 2  30 dB", frame, StringComparison.Ordinal);

        (App unbound, _) = Ready(Unbound());
        Assert.Contains("XLR 1  52 dB", Draw(unbound), StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoInterfaceTheRailKeepsTheBoundMicrophonesMeter()
    {
        (App app, _) = Ready(Edit(root =>
        {
            root["connected"] = false;
            root.AsObject().Remove("device");
            root.AsObject().Remove("capabilities");
        }));
        string frame = Draw(app);
        Assert.Contains("no interface", frame, StringComparison.Ordinal);
        Assert.Contains("Offline", frame, StringComparison.Ordinal);
        Assert.Contains("Mic 1", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMixerKeysTheInterfacesMuteOnTheChannelThatCarriesIt()
    {
        (App app, List<string> sent) = Ready();
        app.Handle(new KeyPress(Key.Down));      // Mic 1, bound to the Yeti Nano
        app.Handle(new KeyPress(Key.Char, ' '));
        JsonElement command = Last(sent);
        Assert.Equal("setChannelMuted", command.GetProperty("cmd").GetString());
        Assert.Equal("xlr1", command.GetProperty("channel").GetString());
        Assert.False(command.GetProperty("value").GetBoolean());   // muted in Monitor A, so this unmutes

        sent.Clear();
        Press(app, Key.Down, 3);                 // XLR 2, Music, then Pro mic
        app.Handle(new KeyPress(Key.Char, ' '));
        command = Last(sent);
        Assert.Equal("set", command.GetProperty("cmd").GetString());
        Assert.Equal("mute", command.GetProperty("control").GetString());
        Assert.True(command.GetProperty("value").GetBoolean());
    }

    [Fact]
    public void ABoundStripNamesItsSourceOnTheDeskAndInTheLineUnderIt()
    {
        (App app, _) = Ready(width: 160, height: 40);
        app.Handle(new KeyPress(Key.Down));      // Mic 1
        string frame = Draw(app, 160, 40);
        Assert.Contains("Yeti Nano", frame, StringComparison.Ordinal);
        Assert.Contains("Mic 1 > Monitor A", frame, StringComparison.Ordinal);
        Assert.Contains("from Yeti Nano, port 2", frame, StringComparison.Ordinal);

        // A source that went away: the strip says why, in the warning colour.
        (App gone, _) = Ready(Edit(root =>
        {
            JsonNode xlr1 = Channel(root, "xlr1");
            xlr1["bindingConnected"] = false;
            xlr1["bindingWarning"] = "Yeti Nano is not connected.";
        }), width: 160, height: 40);
        gone.Handle(new KeyPress(Key.Down));
        Screen screen = new(160, 40);
        gone.Draw(screen);
        string goneFrame = Frame(screen);
        Assert.Contains("Yeti Nano is not connected.", goneFrame, StringComparison.Ordinal);
        string[] rows = goneFrame.Split('\n');
        int y = Array.FindIndex(rows, row => row.Contains("Yeti Nano", StringComparison.Ordinal)
            && !row.Contains("not connected", StringComparison.Ordinal));
        Assert.True(y >= 0);
        Assert.Equal(Theme.Material.TextWarning, screen.At(rows[y].IndexOf("Yeti", StringComparison.Ordinal), y).Fore);
    }

    [Fact]
    public void TheMatrixPrintsABoundStripsSourceUnderItsName()
    {
        (App app, _) = Ready(tab: 1, width: 140, height: 40);
        string[] rows = Draw(app, 140, 40).Split('\n');
        int name = Array.FindIndex(rows, row => row.Contains("Mic 1", StringComparison.Ordinal));
        Assert.True(name >= 0);
        Assert.Contains("Yeti Nano, port 2", rows[name + 1], StringComparison.Ordinal);
        Assert.DoesNotContain("Interface jack", string.Join('\n', rows), StringComparison.Ordinal);

        (App gone, _) = Ready(Edit(root =>
        {
            JsonNode xlr1 = Channel(root, "xlr1");
            xlr1["bindingConnected"] = false;
            xlr1["bindingWarning"] = "Yeti Nano is not connected.";
        }), tab: 1, width: 140, height: 40);
        rows = Draw(gone, 140, 40).Split('\n');
        name = Array.FindIndex(rows, row => row.Contains("Mic 1", StringComparison.Ordinal));
        Assert.Contains("Yeti Nano is not connected.", rows[name + 1], StringComparison.Ordinal);
    }

    [Fact]
    public void TheInputsTabHeadsEachInputByTheChannelThatCarriesItAndKeepsItsControls()
    {
        (App app, List<string> sent) = Ready(tab: 2);
        string frame = Draw(app);
        Assert.Contains("Input 1 on Pro mic", frame, StringComparison.Ordinal);
        Assert.Contains("XLR 2", frame, StringComparison.Ordinal);

        // The interface's controls still act, wherever its microphone plays.
        app.Handle(new KeyPress(Key.Down));      // Mute, under input 1's heading
        app.Handle(new KeyPress(Key.Char, ' '));
        JsonElement command = Last(sent);
        Assert.Equal("set", command.GetProperty("cmd").GetString());
        Assert.Equal("mute", command.GetProperty("control").GetString());

        (App nowhere, _) = Ready(Edit(root => Channel(root, "cap1").AsObject().Remove("unitInput")), tab: 2);
        Assert.Contains("Input 1, not in the mixer", Draw(nowhere), StringComparison.Ordinal);

        (App unbound, _) = Ready(Unbound(), tab: 2);
        string plain = Draw(unbound);
        Assert.Contains("XLR 1", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("Input 1", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBindingsGroupShowsEachStripsSourceAndConnection()
    {
        (App app, _) = Ready(tab: 2, width: 160, height: 60);
        string frame = Draw(app, 160, 60);
        Assert.Contains("Bindings", frame, StringComparison.Ordinal);
        Assert.Contains("Yeti Nano", frame, StringComparison.Ordinal);
        Assert.Contains("linked, Yeti Nano, port 2", frame, StringComparison.Ordinal);
        Assert.Contains("Interface jack (XLR 2)", frame, StringComparison.Ordinal);
        Assert.Contains("2 (FR)", frame, StringComparison.Ordinal);
        Assert.Contains("the interface takes no port", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("Also drive", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void BindingToAnotherUnitAlsoDrivesItUnlessTheBoxIsCleared()
    {
        (App app, List<string> sent) = Ready(tab: 2, width: 160, height: 60);
        Press(app, Key.Down, ToXlr2Source);
        Press(app, Key.Left);                    // the last source, the Wave:3
        string frame = Draw(app, 160, 60);
        Assert.Contains("Also drive unit", frame, StringComparison.Ordinal);
        Press(app, Key.Down, 3);                 // port, the drive box, Apply
        app.Handle(new KeyPress(Key.Enter));
        JsonElement bind = Last(sent);
        Assert.Equal("setInputBinding", bind.GetProperty("cmd").GetString());
        Assert.Equal("alsa_input.usb-Elgato_Wave_3", bind.GetProperty("source").GetString());
        sent.Clear();
        app.Link.Receive($$"""{"type":"commandResult","requestId":"{{bind.GetProperty("requestId").GetString()}}"}""");
        Draw(app, 160, 60);                      // the frame after the reply sends the second step
        JsonElement drive = Last(sent);
        Assert.Equal("setWaveInterfaceEnabled", drive.GetProperty("cmd").GetString());
        Assert.Equal(Wave3, drive.GetProperty("device").GetString());
        Assert.True(drive.GetProperty("value").GetBoolean());
        app.Link.Receive($$"""{"type":"commandResult","requestId":"{{drive.GetProperty("requestId").GetString()}}","error":"four units already"}""");
        Assert.Contains("the interface was not driven: four units already", Draw(app, 160, 60), StringComparison.Ordinal);

        // With the box cleared, only the binding goes out.
        (App other, List<string> only) = Ready(tab: 2, width: 160, height: 60);
        Press(other, Key.Down, ToXlr2Source);
        Press(other, Key.Left);
        Press(other, Key.Down, 2);               // the drive box
        other.Handle(new KeyPress(Key.Char, ' '));
        other.Handle(new KeyPress(Key.Down));    // Apply
        other.Handle(new KeyPress(Key.Enter));
        JsonElement alone = Last(only);
        only.Clear();
        other.Link.Receive($$"""{"type":"commandResult","requestId":"{{alone.GetProperty("requestId").GetString()}}"}""");
        Assert.Contains("XLR 2 takes Wave:3, port 1", Draw(other, 160, 60), StringComparison.Ordinal);
        Draw(other, 160, 60);
        Assert.Empty(only);
    }

    [Fact]
    public void AStripOnADrivenAdditionalUnitCarriesThatUnitsControls()
    {
        DaemonLink link = new();
        link.Receive(OnWave3());
        Snapshot state = link.State!;
        ChannelEntry mic2 = state.Strip(2)!;
        Assert.Equal(new UnitControl(Wave3, "mute"), state.UnitMute(mic2));
        // The primary's input 2 is on no channel now; its mute is not this strip's.
        Assert.Null(state.Carrier(2));
        Assert.Equal("Input 2, not in the mixer", state.InputHeading(2));

        (App app, List<string> sent) = Ready(OnWave3(), tab: 2, width: 160, height: 60);
        Assert.Contains("Wave:3 input 1 on Mic 2", Draw(app, 160, 60), StringComparison.Ordinal);
        // Down from the first gain: both primary inputs' controls, then the Wave:3's gain.
        Press(app, Key.Down, 18);
        app.Handle(new KeyPress(Key.Right));
        JsonElement gain = Last(sent);
        Assert.Equal("setWaveControl", gain.GetProperty("cmd").GetString());
        Assert.Equal(Wave3, gain.GetProperty("device").GetString());
        Assert.Equal("gain", gain.GetProperty("control").GetString());
        Assert.Equal(31, gain.GetProperty("value").GetInt32());

        (App mixer, List<string> keys) = Ready(OnWave3());
        Press(mixer, Key.Down, 2);               // Mic 1, then Mic 2
        mixer.Handle(new KeyPress(Key.Char, ' '));
        JsonElement mute = Last(keys);
        Assert.Equal("setWaveControl", mute.GetProperty("cmd").GetString());
        Assert.Equal("mute", mute.GetProperty("control").GetString());
        Assert.True(mute.GetProperty("value").GetBoolean());

        // Not driven: no controls on the strip, and its key is a send mute.
        Snapshot idle = Snapshot.Parse(Edit(root =>
        {
            JsonNode xlr2 = Channel(root, "xlr2");
            xlr2["unitInput"] = new JsonObject { ["unit"] = Wave3, ["input"] = 1, ["primary"] = false };
        }))!;
        Assert.Null(idle.UnitMute(idle.Strip(2)!));
    }

    [Fact]
    public void AStripFollowsItsUnitsRenamedNode()
    {
        (App app, _) = Ready(Edit(root =>
        {
            JsonNode xlr1 = Channel(root, "xlr1");
            xlr1["binding"] = new JsonObject
            {
                ["kind"] = "source", ["source"] = "alsa_input.usb-Elgato_Wave_3.mono-fallback", ["pair"] = 0,
                ["monoChannel"] = 0, ["unit"] = Wave3,
            };
            foreach (JsonNode? device in root["devices"]!.AsArray())
                if ((string?)device!["name"] == "alsa_input.usb-Elgato_Wave_3")
                    device["name"] = "alsa_input.usb-Elgato_Wave_3.analog-stereo";
        }), tab: 2, width: 160, height: 60);
        string frame = Draw(app, 160, 60);
        Assert.Contains("linked, Wave:3, port 1", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("(missing)", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("not applied", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void ABindingIsSentOnlyOnApplyWithTheChosenSourceAndPort()
    {
        (App app, List<string> sent) = Ready(tab: 2, width: 160, height: 60);
        Press(app, Key.Down, ToXlr2Source);      // XLR 2's source, on the interface jack
        app.Handle(new KeyPress(Key.Right));     // the Yeti Nano
        app.Handle(new KeyPress(Key.Right));     // the BRIO
        app.Handle(new KeyPress(Key.Down));      // Port
        app.Handle(new KeyPress(Key.Right));     // port 2
        string frame = Draw(app, 160, 60);
        Assert.Contains("BRIO 4K Stream Edition", frame, StringComparison.Ordinal);
        Assert.Contains("not applied", frame, StringComparison.Ordinal);
        Assert.Empty(sent);

        app.Handle(new KeyPress(Key.Down));      // Apply to XLR 2
        app.Handle(new KeyPress(Key.Enter));
        JsonElement command = Last(sent);
        Assert.Equal("setInputBinding", command.GetProperty("cmd").GetString());
        Assert.Equal("xlr2", command.GetProperty("channel").GetString());
        Assert.Equal("alsa_input.usb-Logitech_BRIO", command.GetProperty("source").GetString());
        Assert.Equal(1, command.GetProperty("captureMonoChannel").GetInt32());

        app.Link.Receive($$"""{"type":"commandResult","requestId":"{{command.GetProperty("requestId").GetString()}}"}""");
        Assert.Contains("XLR 2 takes BRIO 4K Stream Edition, port 2", Draw(app, 160, 60), StringComparison.Ordinal);
    }

    [Fact]
    public void ReturningAStripToTheInterfaceSendsNoPortAndARefusalKeepsTheDraft()
    {
        (App app, List<string> sent) = Ready(tab: 2, width: 160, height: 60);
        Press(app, Key.Down, ToXlr1Source);      // Mic 1's source, on the Yeti Nano
        app.Handle(new KeyPress(Key.Left));      // the interface jack
        app.Handle(new KeyPress(Key.Down));      // Port, which the interface does not take
        app.Handle(new KeyPress(Key.Right));
        Assert.Empty(sent);

        app.Handle(new KeyPress(Key.Down));      // Apply to Mic 1
        app.Handle(new KeyPress(Key.Enter));
        JsonElement command = Last(sent);
        Assert.Equal("setInputBinding", command.GetProperty("cmd").GetString());
        Assert.Equal("xlr1", command.GetProperty("channel").GetString());
        Assert.Equal(JsonValueKind.Null, command.GetProperty("source").ValueKind);
        Assert.False(command.TryGetProperty("captureMonoChannel", out _));

        app.Link.Receive($$"""{"type":"commandResult","requestId":"{{command.GetProperty("requestId").GetString()}}","error":"refused for the test"}""");
        string frame = Draw(app, 160, 60);
        Assert.Contains("refused for the test", frame, StringComparison.Ordinal);
        Assert.Contains("not applied", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void ASavedSourceTheListLeavesOutStaysChosen()
    {
        (App app, _) = Ready(Edit(root =>
        {
            JsonNode xlr1 = Channel(root, "xlr1");
            xlr1["binding"]!["source"] = "alsa_input.gone";
            xlr1["bindingConnected"] = false;
            xlr1["bindingWarning"] = "alsa_input.gone is not connected.";
        }), tab: 2, width: 160, height: 60);
        string frame = Draw(app, 160, 60);
        Assert.Contains("alsa_input.gone (missing)", frame, StringComparison.Ordinal);
        Assert.Contains("alsa_input.gone is not connected.", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("not applied", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void ABluetoothHeadsetShowsItsHintWhenChosen()
    {
        (App app, _) = Ready(Edit(root => root["devices"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "bluez_input.00_11_22", ["description"] = "Headset", ["kind"] = 1, ["isOwn"] = false,
        })), tab: 2, width: 160, height: 60);
        Assert.DoesNotContain("headset mode", Draw(app, 160, 60), StringComparison.Ordinal);
        Press(app, Key.Down, ToXlr2Source);
        Press(app, Key.Left);                    // the last source in the list, the headset
        Assert.Contains("stays in headset mode while bound", Draw(app, 160, 60), StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsHasNoMicrophonePickerAnyMore()
    {
        (App app, _) = Ready(tab: 7);
        string frame = Draw(app);
        Assert.DoesNotContain("Primary microphone", frame, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Generic", frame, StringComparison.OrdinalIgnoreCase);
    }
}
