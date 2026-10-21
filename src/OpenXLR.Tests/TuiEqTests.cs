using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

/// <summary>
/// The terminal mixer's microphone EQ: the Inputs tab says what runs each
/// microphone's EQ and, while the microphone is on a unit that runs the EQ
/// itself, offers the choice between the unit and this computer, which one
/// key sends as setEqProcessing. A unit that cannot run it says why, and a
/// daemon that reports no choice, or no EQ at all, gets neither row.
/// </summary>
public sealed class TuiEqTests
{
    private const string Unit = "0fd9:00c7@0123456789abcdef";

    /// <summary>An XLR Dock MK.2 on firmware 1.0.15 running XLR 1's EQ, cut to what the tabs read.</summary>
    private const string DockJson = """
        {
          "type": "state",
          "daemonVersion": "0.1.71",
          "connected": true,
          "device": { "vendor": "Elgato", "model": "XLR Dock MK.2", "usbId": "0fd9:00c7" },
          "capabilities": { "gain": true, "mute": true, "lowCut": true, "phantom": true, "clipGuard": true,
                            "xlrInputs": 1, "xlrJacks": true, "hpOutputs": 1, "hpVolume": true, "hardwareEq": true },
          "state": { "gainDb": 40, "mute": false },
          "mixer": {
            "mixes": [ { "id": "monitor", "name": "Monitor A", "volume": 1, "muted": false, "kind": "monitor" } ],
            "channels": [
              { "id": "xlr1", "name": "XLR 1", "levels": { "monitor": 1 }, "mutedIn": [], "hardware": true,
                "binding": { "kind": "unit", "pair": 0 }, "bindingConnected": true,
                "unitInput": { "unit": "0fd9:00c7@0123456789abcdef", "input": 1, "primary": true },
                "eq": { "enabled": true, "bands": [], "backend": "device", "available": true, "error": null,
                        "processing": "device",
                        "device": { "unit": "0fd9:00c7@0123456789abcdef", "input": 1, "name": "XLR Dock MK.2",
                                    "firmware": "1.0.15", "ready": true, "reason": null, "note": null,
                                    "minimumFirmware": "1.0.15" } } },
              { "id": "xlr2", "name": "XLR 2", "levels": { "monitor": 1 }, "mutedIn": [], "hardware": true,
                "present": false, "binding": { "kind": "unit", "pair": 1 }, "bindingConnected": true,
                "eq": { "enabled": false, "bands": [], "backend": null, "available": true, "error": null,
                        "processing": "device", "device": null } },
              { "id": "music", "name": "Music", "levels": { "monitor": 1 }, "mutedIn": [], "hardware": false,
                "binding": { "kind": "apps" }, "volume": 1 }
            ],
            "monitorOutputs": [],
            "lowCutHz": 0,
            "softClipGuard": false,
            "softClipGuardAvailable": true,
            "inserts": {}
          },
          "devices": [],
          "waveInterfaces": [
            { "id": "0fd9:00c7@0123456789abcdef", "name": "XLR Dock MK.2", "active": true, "enabled": false, "connected": true }
          ],
          "profiles": []
        }
        """;

    private static string Edit(Action<JsonNode> change)
    {
        JsonNode root = JsonNode.Parse(DockJson)!;
        change(root);
        return root.ToJsonString();
    }

    private static JsonNode Eq(JsonNode root) => root["mixer"]!["channels"]![0]!["eq"]!;

    private static (App App, List<string> Sent) Ready(string json)
    {
        DaemonLink link = new();
        List<string> sent = [];
        link.Sent += sent.Add;
        link.Receive(json);
        App app = new(link, Theme.Material);
        app.ShowTab(2);
        app.Draw(new Screen(160, 60));
        return (app, sent);
    }

    private static string[] Draw(App app)
    {
        Screen screen = new(160, 60);
        app.Draw(screen);
        return [.. Enumerable.Range(0, screen.Height)
            .Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen.At(x, y).Ch).ToArray()))];
    }

    /// <summary>
    /// Walk down the Inputs tab until the selected row is the one with this
    /// label: its marker stands two cells before the label, in the label's own
    /// card, since two cards share a line on a wide terminal.
    /// </summary>
    private static string Select(App app, string label)
    {
        for (int i = 0; i < 80; i++)
        {
            string? focused = Draw(app).FirstOrDefault(line => line.IndexOf(label, StringComparison.Ordinal) is int at and >= 2 && line[at - 2] == '┃');
            if (focused is not null) return focused[(focused.IndexOf(label, StringComparison.Ordinal) - 2)..];
            app.Handle(new KeyPress(Key.Down));
        }
        throw new Xunit.Sdk.XunitException($"No row named {label} on the Inputs tab.");
    }

    private static JsonElement Last(List<string> sent) => JsonDocument.Parse(Assert.Single(sent)).RootElement;

    [Fact]
    public void TheStatusLineSaysWhatRunsEachMicrophonesEq()
    {
        Snapshot state = Snapshot.Parse(DockJson)!;
        EqEntry eq = state.Mixer.Channels[0].Eq!;
        Assert.True(eq.Offered);
        Assert.Equal("on, on the XLR Dock MK.2", eq.Status);
        Assert.Null(eq.Why);
        Assert.Null(state.Mixer.Channels[2].Eq);
        Assert.False(state.Mixer.Channels[1].Eq!.Offered);

        EqEntry Of(Action<JsonNode> change) => Snapshot.Parse(Edit(root => change(Eq(root))))!.Mixer.Channels[0].Eq!;
        Assert.Equal("on, in software", Of(e => e["backend"] = "software").Status);
        Assert.Equal("off", Of(e => e["enabled"] = false).Status);
        Assert.Equal("on, starting", Of(e => e["backend"] = null).Status);
        Assert.Equal("cannot run", Of(e => e["available"] = false).Status);
        Assert.Equal("the EQ needs the native plugin host", Of(e => e["error"] = "the EQ needs the native plugin host").Status);

        string[] frame = Draw(Ready(DockJson).App);
        string all = string.Join('\n', frame);
        Assert.Contains("Microphone EQ", all, StringComparison.Ordinal);
        Assert.Contains(frame, line => line.Contains("XLR 1 EQ", StringComparison.Ordinal) && line.Contains("on, on the XLR Dock MK.2", StringComparison.Ordinal));
        Assert.Contains(frame, line => line.Contains("XLR 1 EQ runs", StringComparison.Ordinal) && line.Contains("on the unit", StringComparison.Ordinal));
        // XLR 2 is not on this unit, so it is not listed; a channel without an EQ never is.
        Assert.DoesNotContain("XLR 2 EQ", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Music EQ", all, StringComparison.Ordinal);
        Assert.DoesNotContain("XLR 1 unit", all, StringComparison.Ordinal);
    }

    [Fact]
    public void OneKeySwitchesWhereTheEqRuns()
    {
        (App app, List<string> sent) = Ready(DockJson);
        Select(app, "XLR 1 EQ runs");
        app.Handle(new KeyPress(Key.Char, ' '));
        JsonElement command = Last(sent);
        Assert.Equal("setEqProcessing", command.GetProperty("cmd").GetString());
        Assert.Equal("xlr1", command.GetProperty("channel").GetString());
        Assert.Equal("software", command.GetProperty("value").GetString());

        // The daemon reports the choice: the row shows it, and the key goes back to the unit.
        sent.Clear();
        app.Link.Receive(Edit(root =>
        {
            Eq(root)["processing"] = "software";
            Eq(root)["backend"] = "software";
        }));
        string line = Select(app, "XLR 1 EQ runs");
        Assert.Contains("in software", line, StringComparison.Ordinal);
        Assert.Contains(Draw(app), row => row.Contains("XLR 1 EQ", StringComparison.Ordinal) && row.Contains("on, in software", StringComparison.Ordinal));
        app.Handle(new KeyPress(Key.Left));
        command = Last(sent);
        Assert.Equal("setEqProcessing", command.GetProperty("cmd").GetString());
        Assert.Equal("device", command.GetProperty("value").GetString());

        // The row keeps the daemon's value until it reports another: a key sends, it never edits the row.
        Assert.Contains("in software", Select(app, "XLR 1 EQ runs"), StringComparison.Ordinal);
    }

    [Fact]
    public void AUnitThatCannotRunTheEqSaysWhyAndTheChoiceStays()
    {
        (App app, List<string> sent) = Ready(Edit(root =>
        {
            JsonNode eq = Eq(root);
            eq["backend"] = "software";
            eq["device"]!["firmware"] = "1.0.0";
            eq["device"]!["ready"] = false;
            eq["device"]!["reason"] = "firmwareOld";
            eq["device"]!["note"] = "This XLR Dock MK.2 has firmware 1.0.0.";
        }));
        string[] frame = Draw(app);
        Assert.Contains(frame, line => line.Contains("XLR 1 EQ", StringComparison.Ordinal) && line.Contains("on, in software", StringComparison.Ordinal));
        Assert.Contains(frame, line => line.Contains("XLR 1 EQ runs", StringComparison.Ordinal) && line.Contains("on the unit", StringComparison.Ordinal));
        Assert.Contains(frame, line => line.Contains("XLR 1 unit", StringComparison.Ordinal)
            && line.Contains("firmware 1.0.0, needs 1.0.15", StringComparison.Ordinal));
        Select(app, "XLR 1 EQ runs");
        app.Handle(new KeyPress(Key.Enter));
        Assert.Equal("software", Last(sent).GetProperty("value").GetString());

        // The other reasons, and the daemon's own sentence for one this client does not know.
        foreach ((string reason, string why) in new[]
        {
            ("firmwareUnknown", "firmware unknown, needs 1.0.15"),
            ("unsupportedBlock", "EQ settings not recognised"),
            ("failed", "the unit did not keep the EQ"),
            ("somethingNew", "daemon text"),
        })
        {
            EqEntry eq = Snapshot.Parse(Edit(root =>
            {
                JsonNode e = Eq(root);
                e["backend"] = "software";
                e["device"]!["ready"] = false;
                e["device"]!["reason"] = reason;
                e["device"]!["note"] = "daemon text";
            }))!.Mixer.Channels[0].Eq!;
            Assert.Equal(why, eq.Why);
        }
    }

    [Fact]
    public void ADaemonWithoutTheChoiceOrTheEqGetsNeitherRow()
    {
        // Before the hardware EQ: the status line, no choice.
        (App before, List<string> sent) = Ready(Edit(root =>
        {
            JsonObject eq = Eq(root).AsObject();
            eq["backend"] = "software";
            eq.Remove("processing");
            eq.Remove("device");
        }));
        string all = string.Join('\n', Draw(before));
        Assert.Contains("XLR 1 EQ", all, StringComparison.Ordinal);
        Assert.Contains("on, in software", all, StringComparison.Ordinal);
        Assert.DoesNotContain("EQ runs", all, StringComparison.Ordinal);
        Assert.Empty(sent);

        // Before the EQ: no group at all.
        (App older, _) = Ready(Edit(root =>
        {
            foreach (JsonNode? channel in root["mixer"]!["channels"]!.AsArray()) channel!.AsObject().Remove("eq");
        }));
        Assert.DoesNotContain("Microphone EQ", string.Join('\n', Draw(older)), StringComparison.Ordinal);
    }
}
