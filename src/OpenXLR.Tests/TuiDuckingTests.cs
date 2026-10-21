using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

/// <summary>
/// The terminal mixer's Inputs tab on a Wave XLR Pro: a Ducking group with
/// the switch, the two outputs and the three presets, each key sending one
/// setDucking field, and nothing of it on a unit without the capability.
/// </summary>
public sealed class TuiDuckingTests
{
    private const string ProJson = """
        {
          "type": "state",
          "daemonVersion": "0.1.71",
          "connected": true,
          "device": { "vendor": "Elgato", "model": "Wave XLR Pro", "usbId": "0fd9:00b4" },
          "capabilities": { "gain": true, "mute": true, "xlrInputs": 2, "xlrJacks": true, "hpOutputs": 2,
                            "hpVolume": true, "outputRouting": true, "auxInput": true, "ducking": true },
          "state": { "gainDb": 40, "mute": false },
          "ducking": { "enabled": false, "key": "xlr1", "targets": ["monitor"], "sensitivity": "medium",
                       "amount": "medium", "speed": "medium", "processing": "device", "backend": "device",
                       "device": { "unit": "0fd9:00b4", "name": "Wave XLR Pro", "firmware": null, "ready": true,
                                   "reason": null, "note": null, "held": true,
                                   "readback": { "sensitivity": "medium", "amount": "medium", "speed": "medium", "pairs": [], "entries": [4, 7, 8] } } },
          "mixer": {
            "mixes": [ { "id": "monitor", "name": "Monitor A", "volume": 1, "muted": false, "kind": "monitor" } ],
            "channels": [],
            "monitorOutputs": [],
            "lowCutHz": 0,
            "softClipGuard": false,
            "softClipGuardAvailable": true,
            "inserts": {}
          },
          "devices": [],
          "profiles": []
        }
        """;

    private static string Edit(Action<JsonNode> change)
    {
        JsonNode root = JsonNode.Parse(ProJson)!;
        change(root);
        return root.ToJsonString();
    }

    private static (App App, List<string> Sent) Ready(string json)
    {
        DaemonLink link = new();
        List<string> sent = [];
        link.Sent += sent.Add;
        link.Receive(json);
        App app = new(link, Theme.Material);
        app.ShowTab(2);
        app.Draw(new Screen(160, 80));
        return (app, sent);
    }

    private static string[] Draw(App app)
    {
        Screen screen = new(160, 80);
        app.Draw(screen);
        return [.. Enumerable.Range(0, screen.Height)
            .Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen.At(x, y).Ch).ToArray()))];
    }

    private static string Select(App app, string label)
    {
        for (int i = 0; i < 120; i++)
        {
            string? focused = Draw(app).FirstOrDefault(line => line.IndexOf(label, StringComparison.Ordinal) is int at and >= 2 && line[at - 2] == '┃');
            if (focused is not null) return focused[(focused.IndexOf(label, StringComparison.Ordinal) - 2)..];
            app.Handle(new KeyPress(Key.Down));
        }
        throw new Xunit.Sdk.XunitException($"No row named {label} on the Inputs tab.");
    }

    private static JsonElement Value(List<string> sent)
    {
        JsonElement command = JsonDocument.Parse(Assert.Single(sent)).RootElement;
        Assert.Equal("setDucking", command.GetProperty("cmd").GetString());
        sent.Clear();
        return command.GetProperty("value");
    }

    [Fact]
    public void EachRowSendsTheOneFieldItChanges()
    {
        (App app, List<string> sent) = Ready(ProJson);
        Assert.Contains(Draw(app), line => line.Contains("Ducking", StringComparison.Ordinal));

        Select(app, "Duck on XLR 1");
        app.Handle(new KeyPress(Key.Char, ' '));
        Assert.True(Value(sent).GetProperty("enabled").GetBoolean());

        // Rows read the daemon's state, which still says the phones and Line Out alone.
        Select(app, "Phones and Line Out");
        app.Handle(new KeyPress(Key.Char, ' '));
        Assert.Empty(Value(sent).GetProperty("targets").EnumerateArray());

        Select(app, "USB Aux out");
        app.Handle(new KeyPress(Key.Char, ' '));
        Assert.Equal(["monitor", "aux"], Value(sent).GetProperty("targets").EnumerateArray().Select(t => t.GetString()));

        Select(app, "Amount");
        app.Handle(new KeyPress(Key.Left));
        Assert.Equal("low", Value(sent).GetProperty("amount").GetString());

        Select(app, "Speed");
        app.Handle(new KeyPress(Key.Right));
        Assert.Equal("fast", Value(sent).GetProperty("speed").GetString());
    }

    [Fact]
    public void AUnitThatHasNotTakenOrCannotRunTheSetupSaysSoAndAnotherModelShowsNoDucking()
    {
        (App app, _) = Ready(Edit(root =>
        {
            root["ducking"]!["enabled"] = true;
            root["ducking"]!["device"]!["held"] = false;
        }));
        Assert.Contains("not held yet", Select(app, "Duck on XLR 1"), StringComparison.Ordinal);
        (App unfit, _) = Ready(Edit(root =>
        {
            root["ducking"]!["key"] = "xlr2";
            root["ducking"]!["backend"] = null;
            root["ducking"]!["device"]!["ready"] = false;
            root["ducking"]!["device"]!["reason"] = "keyNotSupported";
        }));
        Assert.Contains("not on the unit", Select(unfit, "Duck on XLR 1"), StringComparison.Ordinal);

        (App other, _) = Ready(Edit(root =>
        {
            root["capabilities"]!["ducking"] = false;
            root.AsObject().Remove("ducking");
        }));
        Assert.DoesNotContain(Draw(other), line => line.Contains("Duck on XLR 1", StringComparison.Ordinal));
    }
}
