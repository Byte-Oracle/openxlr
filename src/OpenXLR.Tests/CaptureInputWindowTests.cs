using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using OpenXLR.UI.Views;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>Capture chain commands and compatibility, in their own process to keep the layout run short.</summary>
[Collection("xdg-config")]
public sealed class CaptureInputWindowTests
{
    internal static JsonNode State()
    {
        JsonNode state = JsonNode.Parse(InputBindingModelTests.ProState(
            InputBindingModelTests.Xlr("xlr1", 0, InputBindingModelTests.Input(1)) + "," +
            InputBindingModelTests.Xlr("xlr2", 1, InputBindingModelTests.Input(2)) + """
            ,{"id":"aux","name":"Aux In","hardware":true,"volume":1,"levels":{"monitor":1}},
            {"id":"guest","name":"Guest microphone with a long name","captureSource":"usb.guest","captureMonoChannel":0,
             "captureConnected":true,"microphone":true,"chainWidth":1,"volume":0.8,"levels":{"monitor":1}},
            {"id":"camera","name":"Stereo capture card","captureSource":"usb.camera","capturePair":1,
             "captureConnected":true,"microphone":false,"chainWidth":2,"volume":0.6,"levels":{"monitor":1}},
            {"id":"offline","name":"Disconnected microphone","captureSource":"usb.missing","captureMonoChannel":2,
             "captureConnected":false,"microphone":true,"chainWidth":1,"volume":1,"levels":{"monitor":1},
             "bindingWarning":"The selected microphone is not connected."}
            """, withMix: true))!;
        state["type"] = "state";
        state["daemonVersion"] = AppVersion.Current;
        state["capabilities"]!["auxInput"] = true;
        state["devices"]!.AsArray().Add(JsonNode.Parse("""{"name":"usb.guest","description":"USB microphone with a long source description that must fit beside its connection light","kind":1,"channels":1}"""));
        state["mixer"]!["inserts"]!["guest"] = JsonNode.Parse("""
            [{"insert":{"id":"compressor","plugin":"mono","kind":"lv2","label":"Guest compressor with a long label","params":{}},"latencyMilliseconds":2.5}]
            """);
        return state;
    }

    internal static void Apply(MainViewModel main, JsonNode state)
        => typeof(MainViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [state]);

    /// <summary>Five ordinary resizes on the existing layout test's UI thread.</summary>
    internal static void ThreeCaptureInputsFitClassicWidths()
    {
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        StopConnecting(window);
        try
        {
            if (Environment.GetEnvironmentVariable("OPENXLR_LAYOUT_FONT") is { Length: > 0 } font)
                window.FontFamily = new Avalonia.Media.FontFamily(font);
            var main = (MainViewModel)window.DataContext!;
            Apply(main, State());
            window.Show();
            Assert.Equal(["guest", "camera", "offline"], main.CaptureInputs.Select(c => c.Id));
            foreach (double width in new[] { 640d, 760, 1040, 1800, 2400 })
            {
                Layout(window, width, 900);
                StackPanel[] blocks = [.. window.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Name == "CaptureInputBlock")];
                Assert.Equal(3, blocks.Length);
                foreach (StackPanel block in blocks)
                {
                    AssertInside(block, window);
                    Assert.All(block.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible
                        && c is (Button and not RepeatButton) or Slider or TextBlock or CaptureSourceChip), c => AssertInside(c, block));
                    Assert.Single(block.GetVisualDescendants().OfType<Button>(), b => b.Name == "CaptureInserts" && b.IsEffectivelyVisible);
                    Assert.DoesNotContain(block.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, Localizer.Text("Eq")));
                    // A name without an icon starts where the chip and the labels under it start.
                    TextBlock name = block.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("strip") && t.IsEffectivelyVisible);
                    CaptureSourceChip chip = block.GetVisualDescendants().OfType<CaptureSourceChip>().Single();
                    Assert.Equal(chip.TranslatePoint(default, block)!.Value.X, name.TranslatePoint(default, block)!.Value.X, 0.5);
                }
                Assert.True(blocks[0].TranslatePoint(default, window)!.Value.Y < blocks[1].TranslatePoint(default, window)!.Value.Y);
                Assert.True(blocks[1].TranslatePoint(default, window)!.Value.Y < blocks[2].TranslatePoint(default, window)!.Value.Y);
                Capture(window, $"capture-inputs-{width}");
            }
            FlowNode guest = FlowGraph.From(main).Nodes.Single(n => n.Key == "ch:guest");
            Assert.Contains("Guest compressor", guest.Processing);
            JsonNode state = State();
            JsonArray channels = state["mixer"]!["channels"]!.AsArray();
            JsonNode moved = channels[5]!;
            channels.RemoveAt(5);
            channels.Insert(3, moved);
            Apply(main, state);
            Assert.Equal(["offline", "guest", "camera"], main.CaptureInputs.Select(c => c.Id));
        }
        finally { window.Close(); }
    }

    [CaptureWindowFact]
    public void CaptureButtonsSendTheirIdsAndOlderStatesRemoveTheButtons()
    {
        string root = Path.Combine(Path.GetTempPath(), "openxlr-capture-window-" + Guid.NewGuid());
        string[] names = ["XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_DATA_DIRS", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS"];
        string?[] old = [.. names.Select(Environment.GetEnvironmentVariable)];
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                for (int i = 0; i < names.Length - 1; i++) Environment.SetEnvironmentVariable(names[i], Path.Combine(root, names[i]));
                Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "unix:path=/nonexistent");
                Directory.CreateDirectory(root);
                AppBuilder.Configure<App>().UseSkia().UseHarfBuzz().UseX11().SetupWithoutStarting();
                Task pending = CheckCommands();
                DateTime deadline = DateTime.UtcNow.AddSeconds(90);
                while (!pending.IsCompleted && DateTime.UtcNow < deadline)
                {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(5);
                }
                Assert.True(pending.IsCompleted, "The capture chain window check hung.");
                pending.GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                for (int i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], old[i]);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "The capture chain window check hung.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task CheckCommands()
    {
        var commands = new ConcurrentQueue<JsonNode>();
        var gate = new object();
        JsonNode state = State();
        using var pushes = new SemaphoreSlim(0);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            using var sending = new SemaphoreSlim(1);
            async Task Send(object payload)
            {
                await sending.WaitAsync(stop);
                try { await SocketTestServer.Send(socket, payload, stop); }
                finally { sending.Release(); }
            }
            JsonNode Snapshot() { lock (gate) return state.DeepClone(); }
            await Send(Snapshot());
            Task push = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    await pushes.WaitAsync(stop);
                    await Send(Snapshot());
                }
            }, stop);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    JsonNode cmd = await SocketTestServer.Receive(socket, stop);
                    string? name = cmd["cmd"]?.GetValue<string>();
                    if (name == "auth") continue;
                    if (name == "listPlugins")
                    {
                        await Send(new { type = "plugins", plugins = new[]
                        {
                            new { plugin = "mono", name = "Mono compressor", kind = "lv2", widths = new[] { 1 }, @params = Array.Empty<object>() },
                            new { plugin = "stereo", name = "Stereo compressor", kind = "lv2", widths = new[] { 2 }, @params = Array.Empty<object>() },
                        } });
                    }
                    else if (name == "setInserts")
                    {
                        commands.Enqueue(cmd);
                        lock (gate)
                            state["mixer"]!["inserts"]![cmd["channel"]!.GetValue<string>()] = new JsonArray(
                                [.. cmd["inserts"]!.AsArray().Select(i => (JsonNode)new JsonObject { ["insert"] = i!.DeepClone() })]);
                        await Send(Snapshot());
                    }
                    // Like the daemon: a reply, then the commandResult that says which request it answers.
                    if (cmd["requestId"] is { } requestId)
                        await Send(new { type = "commandResult", requestId = requestId.GetValue<string>(), error = (string?)null });
                }
            }
            finally
            {
                // Server disposal cancels both loops before it returns.
                try { await push; } catch (OperationCanceledException) { }
            }
        });
        SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
        var window = new MainWindow(new DaemonClient(server.Url));
        var main = (MainViewModel)window.DataContext!;
        try
        {
            window.Show();
            await Until(() => main.CaptureInputs.Count == 3 && main.CaptureInputs.All(c => c.Inserts?.PluginChoices.Count == 1),
                () => $"{main.CaptureInputs.Count} capture inputs, choices {string.Join(",", main.CaptureInputs.Select(c => $"{c.Id}:{c.Inserts?.PluginChoices.Count}:{c.Inserts?.Note}"))}, connected {main.DaemonConnected}, status {main.Status}");
            foreach ((string id, string plugin) in new[] { ("guest", "mono"), ("camera", "stereo") })
            {
                Layout(window, 1040, 900);
                Button button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CaptureInserts"
                    && b.DataContext is ChannelViewModel c && c.Id == id && b.IsEffectivelyVisible);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => window.OwnedWindows.OfType<MixInsertsWindow>().Any(w => w.IsVisible));
                MixInsertsWindow chain = window.OwnedWindows.OfType<MixInsertsWindow>().Single(w => w.IsVisible);
                Layout(chain, 560, 360);
                InsertsViewModel model = main.CaptureInputs.Single(c => c.Id == id).Inserts!;
                Assert.Same(model, chain.DataContext);
                Assert.False(model.CanSoundCheck);
                chain.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Localizer.Text("AddPlugin")))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => chain.OwnedWindows.OfType<PluginPickerWindow>().Any(w => w.IsVisible));
                PluginPickerWindow picker = chain.OwnedWindows.OfType<PluginPickerWindow>().Single(w => w.IsVisible);
                ListBox list = picker.FindControl<ListBox>("List")!;
                PluginChoice choice = Assert.Single(list.ItemsSource!.Cast<PluginChoice>());
                Assert.Equal(plugin, choice.Uri);
                list.SelectedItem = choice;
                picker.FindControl<Button>("AddButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => commands.Any(c => c["channel"]!.GetValue<string>() == id));
                JsonNode command = commands.Single(c => c["channel"]!.GetValue<string>() == id);
                Assert.Equal("setInserts", command["cmd"]!.GetValue<string>());
                Assert.Equal(plugin, command["inserts"]!.AsArray().Last()!["plugin"]!.GetValue<string>());
                chain.Close();
            }
            ChannelViewModel guest = main.CaptureInputs[0];
            InsertsViewModel kept = guest.Inserts!;
            lock (gate) state["mixer"]!["channels"]![3]!["name"] = "Renamed guest";
            pushes.Release();
            await Until(() => guest.Name == "Renamed guest");
            Assert.Same(kept, guest.Inserts);
            Assert.Equal("Renamed guest", kept.Title);
            InsertWindows.OpenChain(window, kept, guest.Id);
            lock (gate)
                foreach (JsonNode? channel in state["mixer"]!["channels"]!.AsArray())
                {
                    channel!.AsObject().Remove("chainWidth");
                    channel.AsObject().Remove("microphone");
                }
            pushes.Release();
            await Until(() => main.CaptureInputs.All(c => !c.HasInserts));
            Layout(window, 1040, 900);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Name == "CaptureInserts" && b.IsEffectivelyVisible);
            Assert.DoesNotContain(window.OwnedWindows.OfType<MixInsertsWindow>(), w => w.IsVisible);
            Assert.True(kept.IsDetached);
            Assert.All(main.CaptureInputs, c => Assert.False(c.Microphone));
            Assert.Equal(2, commands.Count);
        }
        finally { window.Close(); }
    }

    private static async Task Until(Func<bool> ready, Func<string>? describe = null)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(15);
        Assert.True(ready(), "The capture chain state did not arrive. " + describe?.Invoke());
    }
}

public sealed class CaptureWindowFactAttribute : FactAttribute
{
    public CaptureWindowFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_TEST_CAPTURE") != "1")
            Skip = "Run separately with OPENXLR_TEST_CAPTURE=1 under xvfb-run, filtered to CaptureInputWindowTests.";
    }
}
