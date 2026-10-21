using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// Ducking in the Options window's interface card, against a fake daemon:
/// shown only while the driven unit says it can duck, the switch and the
/// three presets showing the daemon's choice, each change sending one
/// setDucking field and nothing echoed back when the daemon's state comes
/// in, the line that says what runs it, that the unit does not hold the
/// setting yet, or why nothing runs it, and the whole group fitting the
/// card. Run from the window layout test, on its UI thread.
/// </summary>
internal static class DuckingWindowTests
{
    private sealed class Daemon
    {
        public bool Ducks = true;
        public bool Held = true;
        public string? Error;
        public JsonObject Ducking = new()
        {
            ["enabled"] = false, ["key"] = "xlr1",
            ["channels"] = new JsonArray("game", "music", "browser", "system", "voicechat", "sfx"), ["mixes"] = new JsonArray("monitor"),
            ["sensitivity"] = "medium", ["amount"] = "medium", ["speed"] = "medium",
        };

        public JsonNode State()
        {
            JsonNode state = JsonNode.Parse(InputBindingModelTests.ProState(""))!;
            state["type"] = "state";
            state["capabilities"]!["ducking"] = Ducks;
            if (Ducks)
            {
                var ducking = (JsonObject)Ducking.DeepClone();
                bool ready = ducking["key"]!.GetValue<string>() == "xlr1";
                ducking["processing"] = "device";
                ducking["backend"] = Error is not null ? null : ready ? "device" : "software";
                ducking["available"] = Error is null;
                ducking["error"] = Error;
                ducking["device"] = new JsonObject
                {
                    ["unit"] = "0fd9:00b4", ["name"] = "Wave XLR Pro", ["firmware"] = null, ["ready"] = ready,
                    ["reason"] = ready ? null : "keyNotSupported", ["note"] = ready ? null : "keyed by XLR 2",
                    ["outputs"] = new JsonArray("monitor"), ["held"] = Held,
                };
                state["ducking"] = ducking;
            }
            return state;
        }

        public void Handle(JsonNode value)
        {
            foreach ((string field, JsonNode? v) in value.AsObject()) Ducking[field] = v?.DeepClone();
        }
    }

    internal static void Check()
    {
        Task pending = CheckAll();
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(pending.IsCompleted, "The ducking window check hung.");
        pending.GetAwaiter().GetResult();
    }

    private static async Task CheckAll()
    {
        var commands = new ConcurrentQueue<JsonNode>();
        var daemon = new Daemon();
        WebSocketHolder? live = null;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            var holder = new WebSocketHolder(socket, stop);
            await holder.Push(daemon.State());
            live = holder;
            while (!stop.IsCancellationRequested)
            {
                JsonNode cmd = await SocketTestServer.Receive(socket, stop);
                if (cmd["cmd"]?.GetValue<string>() != "setDucking") continue;
                daemon.Handle(cmd["value"]!);
                commands.Enqueue(cmd);
                await holder.Push(daemon.State());
            }
        });

        var window = new MainWindow(new DaemonClient(server.Url));
        var main = (MainViewModel)window.DataContext!;
        var client = (DaemonClient)typeof(MainWindow).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var options = new OptionsWindow { DataContext = new OptionsViewModel(client, main) };
        try
        {
            options.Show();
            await Until(() => main.ShowDucking);
            Layout(options, 980, 900);
            StackPanel group = Find<StackPanel>(options, "Ducking");
            Assert.True(group.IsEffectivelyVisible);
            CheckBox enabled = Find<CheckBox>(options, "DuckingEnabled");
            ComboBox sensitivity = Find<ComboBox>(options, "DuckingSensitivity"), amount = Find<ComboBox>(options, "DuckingAmount"),
                speed = Find<ComboBox>(options, "DuckingSpeed");
            TextBlock status = Find<TextBlock>(options, "DuckingStatus");
            Border badge = Find<Border>(options, "DuckingHardwareBadge");

            // The daemon's choice, and a state push sends nothing back.
            Assert.False(enabled.IsChecked);
            Assert.Equal((1, 1, 1), (sensitivity.SelectedIndex, amount.SelectedIndex, speed.SelectedIndex));
            Assert.True(badge.IsEffectivelyVisible);
            Assert.Equal(Localizer.Text("DuckingStatusOff"), status.Text);
            Assert.DoesNotContain("warn", status.Classes);
            foreach (Control control in new Control[] { enabled, sensitivity, amount, speed }) AssertInside(control, options);
            group.BringIntoView();
            Layout(options, 980, 900);
            Capture(options, "ducking-options");
            Assert.Empty(commands);

            // Each control sends the one field it changes.
            async Task<JsonNode> Sent(Action change)
            {
                int before = commands.Count;
                change();
                await Until(() => commands.Count == before + 1);
                return commands.ToArray()[^1]["value"]!;
            }
            Assert.True((await Sent(() => enabled.IsChecked = true))["enabled"]!.GetValue<bool>());
            Assert.Equal("high", (await Sent(() => sensitivity.SelectedIndex = 2))["sensitivity"]!.GetValue<string>());
            Assert.Equal("low", (await Sent(() => amount.SelectedIndex = 0))["amount"]!.GetValue<string>());
            Assert.Equal("fast", (await Sent(() => speed.SelectedIndex = 2))["speed"]!.GetValue<string>());
            Assert.All(commands, c => Assert.Single(c["value"]!.AsObject()));

            // The daemon's answers came back as state and changed nothing on their own.
            await Until(() => main.DuckingSpeed == 2 && main.DuckingAmount == 0 && main.DuckingEnabled);
            Assert.Equal(4, commands.Count);

            // The controls change at once; the line follows the daemon's state.
            await Until(() => status.Text == Localizer.Format("DuckingStatusDevice", "Wave XLR Pro"));

            // A unit that has not taken the setting: the line says so.
            daemon.Held = false;
            await live!.Push(daemon.State());
            await Until(() => main.DuckingWarns);
            Layout(options, 980, 900);
            Assert.Equal(Localizer.Text("DuckingNotHeld"), status.Text);
            Assert.Contains("warn", status.Classes);
            AssertInside(status, options);

            // A setup the unit cannot run runs on this computer: no badge, and no warning.
            daemon.Held = true;
            daemon.Ducking["key"] = "xlr2";
            await live.Push(daemon.State());
            await Until(() => !main.DuckingOnDevice);
            Layout(options, 980, 900);
            Assert.False(badge.IsEffectivelyVisible);
            Assert.Equal(Localizer.Text("DuckingStatusSoftware"), status.Text);
            Assert.DoesNotContain("warn", status.Classes);

            // Nothing runs it: the line gives the daemon's reason.
            daemon.Error = "Ducking needs the OpenXLR native plugin host.";
            await live.Push(daemon.State());
            await Until(() => main.DuckingWarns);
            Layout(options, 980, 900);
            Assert.Equal(daemon.Error, status.Text);
            AssertInside(status, options);

            // A unit without ducking: the group goes, and nothing is sent.
            daemon.Ducks = false;
            await live.Push(daemon.State());
            await Until(() => !main.ShowDucking);
            Layout(options, 980, 900);
            Assert.False(group.IsEffectivelyVisible);
            Assert.Equal(4, commands.Count);
        }
        finally
        {
            options.Close();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>The fake daemon's socket: one send at a time, from its loop or from the test.</summary>
    private sealed class WebSocketHolder(System.Net.WebSockets.WebSocket socket, CancellationToken stop)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task Push(JsonNode state)
        {
            await _gate.WaitAsync(stop);
            try { await SocketTestServer.Send(socket, state, stop); }
            finally { _gate.Release(); }
        }
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static async Task Until(Func<bool> ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(ready());
    }
}
