using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

/// <summary>
/// The terminal mixer's Inputs tab on a Wave XLR Pro: a Ducking group with
/// the switch and the three presets, each key sending one setDucking field,
/// a note saying where it runs or what is wrong, and nothing of it on a unit
/// without the capability.
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
          "ducking": { "enabled": false, "key": "xlr1", "channels": ["game", "music"], "mixes": ["monitor"], "sensitivity": "medium",
                       "amount": "medium", "speed": "medium", "processing": "device", "backend": "device",
                       "available": true, "error": null,
                       "device": { "unit": "0fd9:00b4", "name": "Wave XLR Pro", "firmware": null, "ready": true,
                                   "reason": null, "note": null, "outputs": ["monitor"], "held": true,
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

        Select(app, "Duck while talking");
        app.Handle(new KeyPress(Key.Char, ' '));
        Assert.True(Value(sent).GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain(Draw(app), line => line.Contains("Phones and Line Out", StringComparison.Ordinal));

        Select(app, "Sensitivity");
        app.Handle(new KeyPress(Key.Right));
        Assert.Equal("high", Value(sent).GetProperty("sensitivity").GetString());

        Select(app, "Amount");
        app.Handle(new KeyPress(Key.Left));
        Assert.Equal("low", Value(sent).GetProperty("amount").GetString());

        Select(app, "Speed");
        app.Handle(new KeyPress(Key.Right));
        Assert.Equal("fast", Value(sent).GetProperty("speed").GetString());
    }

    [Fact]
    public void TheSwitchSaysWhereDuckingRunsOrWhatIsWrongAndAnotherModelShowsNoDucking()
    {
        (App off, _) = Ready(ProJson);
        Assert.DoesNotContain("on the unit", Select(off, "Duck while talking"), StringComparison.Ordinal);
        (App device, _) = Ready(Edit(root => root["ducking"]!["enabled"] = true));
        Assert.Contains("on the unit", Select(device, "Duck while talking"), StringComparison.Ordinal);
        (App app, _) = Ready(Edit(root =>
        {
            root["ducking"]!["enabled"] = true;
            root["ducking"]!["device"]!["held"] = false;
        }));
        Assert.Contains("not held yet", Select(app, "Duck while talking"), StringComparison.Ordinal);
        (App software, _) = Ready(Edit(root =>
        {
            root["ducking"]!["enabled"] = true;
            root["ducking"]!["key"] = "xlr2";
            root["ducking"]!["backend"] = "software";
            root["ducking"]!["device"]!["ready"] = false;
            root["ducking"]!["device"]!["reason"] = "keyNotSupported";
        }));
        Assert.Contains("in software", Select(software, "Duck while talking"), StringComparison.Ordinal);
        (App stopped, _) = Ready(Edit(root =>
        {
            root["ducking"]!["enabled"] = true;
            root["ducking"]!["backend"] = null;
        }));
        Assert.Contains("not running", Select(stopped, "Duck while talking"), StringComparison.Ordinal);

        (App other, _) = Ready(Edit(root =>
        {
            root["capabilities"]!["ducking"] = false;
            root.AsObject().Remove("ducking");
        }));
        Assert.DoesNotContain(Draw(other), line => line.Contains("Duck while talking", StringComparison.Ordinal));
    }
}
