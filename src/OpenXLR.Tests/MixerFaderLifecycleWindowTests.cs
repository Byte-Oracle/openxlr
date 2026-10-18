using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

internal static class MixerFaderLifecycleWindowTests
{
    internal static void Check()
    {
        Task check = CheckCommands();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!check.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(check.IsCompleted, "Retired fader check hung.");
        check.GetAwaiter().GetResult();
        CheckDragAcrossSwitch();
    }

    /// <summary>Wait for a task on the UI thread, running its jobs meanwhile.</summary>
    private static T Await<T>(Task<T> task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(task.IsCompleted, "the fake daemon did not answer");
        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// A fader held down with the real pointer while a structural skin
    /// switch happens: the switch takes the pointer back from the fader, the
    /// drag goes no further however the pointer moves until it is released,
    /// and no value reaches the daemon after the switch. Before the switch
    /// the same drag did send, so the silence afterwards means something.
    /// The switch only changes the submixer's template, so MONITOR's volume
    /// stays under the pointer the whole time.
    /// </summary>
    private static void CheckDragAcrossSwitch()
    {
        string data = Directory.CreateTempSubdirectory("openxlr-drag-switch-").FullName;
        string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        SkinEntry worn = SkinService.Current;
        var commands = new ConcurrentQueue<string>();
        SocketTestServer server = Await(SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                if (cmd is "setOutputVolume" or "setLevel" or "setMixVolume" or "setChannelMuted" or "setMixMuted" or "setLayoutOrder" or "setMonitorOutputs")
                    commands.Enqueue(cmd);
                if (command["requestId"] is { } requestId)
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = requestId.GetValue<string>() }, stop);
            }
        }));
        MainWindow? main = null;
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(data, "none"));
            string folder = Path.Combine(data, "openxlr", "skins", "column-banks");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "skin.json"), """
                {"schema":1,"name":"Column banks","tokens":{"Ox.Card.Background":"#262a30"},"layout":{"version":1,"regions":{"submixer":{"template":"mix-banks"}}}}
                """);
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            var client = new DaemonClient(server.Url);
            main = new MainWindow(client);
            var vm = (MainViewModel)main.DataContext!;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!vm.DaemonConnected && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }
            Assert.True(vm.DaemonConnected);
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [JsonNode.Parse("""
                {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},{"id":"stream","name":"Stream"}],
                 "channels":[{"id":"music","name":"Music","levels":{"monitor":0.5,"stream":0.4}}]}
                """)]);
            main.Show();
            WindowLayoutTests.Layout(main, 1040, 900);
            main.Activate();
            IPointer? pressed = null;
            main.AddHandler(InputElement.PointerPressedEvent, (_, e) => pressed = e.Pointer, RoutingStrategies.Tunnel, handledEventsToo: true);
            var slider = main.FindControl<Slider>("OutputVolumeSlider")!;
            slider.BringIntoView();
            FaderInputWindowTests.Pump();
            using var input = new XPointer();
            input.Focus(main.TryGetPlatformHandle()!.Handle);
            var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
            PixelPoint point = thumb.PointToScreen(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2));
            Point before = slider.TranslatePoint(default, main)!.Value;
            double start = slider.Value;
            try
            {
                input.MoveTo(point.X, point.Y);
                FaderInputWindowTests.Pump();
                input.SetButton(true);
                FaderInputWindowTests.Pump();
                input.MoveTo(point.X + 40, point.Y);
                FaderInputWindowTests.Pump();
                double dragged = slider.Value;
                Assert.True(dragged > start + 0.05, $"the drag did not move the fader from {start}: {dragged}");
                Assert.NotNull(pressed);
                Assert.NotNull(pressed!.Captured);
                SliderSync.FlushPending();
                Assert.Null(Await(client.SetLayoutOrderAsync([], [])));
                Assert.Contains("setOutputVolume", commands);
                commands.Clear();

                Assert.Empty(SkinService.Apply(SkinCatalog.Find("column-banks")!));
                FaderInputWindowTests.Pump();
                Assert.NotNull(main.MixBanks);
                Assert.Equal(before, slider.TranslatePoint(default, main)!.Value);
                Assert.Null(pressed.Captured);
                // Still held, still over the fader: it goes no further.
                foreach (int dx in new[] { 60, 90, 20 })
                {
                    input.MoveTo(point.X + dx, point.Y);
                    FaderInputWindowTests.Pump();
                    Assert.Equal(dragged, slider.Value);
                }
            }
            finally
            {
                input.SetButton(false);
                FaderInputWindowTests.Pump();
            }
            Assert.Equal(slider.Value, vm.OutputVolume);
            SliderSync.FlushPending();
            Assert.Null(Await(client.SetLayoutOrderAsync([], [])));
            Assert.Equal(["setLayoutOrder"], commands.ToArray());
        }
        finally
        {
            SliderSync.Forget("outvol");
            main?.Quit();
            Dispatcher.UIThread.RunJobs();
            SkinService.Apply(worn);
            Await(server.DisposeAsync().AsTask().ContinueWith(_ => true));
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataHome);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", dataDirs);
            Directory.Delete(data, true);
        }
    }

    private static async Task CheckCommands()
    {
        var changes = new ConcurrentQueue<string>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                if (cmd is "setLevel" or "setMixVolume" or "setChannelMuted" or "setMixMuted"
                    or "setInsertParam" or "setInsertBypass" or "setInserts" or "showInsertUi" or "renameInsert"
                    or "setAuxPortEnabled")
                    changes.Enqueue($"{cmd}:{command["channel"]}:{command["mix"]}");
                if (command["requestId"] is { } requestId)
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = requestId.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var apply = typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var model = new MainViewModel(client);
        int boosts = 0;
        model.DesktopVolumeBoostRequested += _ => boosts++;
        JsonObject State(bool channel, bool mix) => new()
        {
            ["mixes"] = JsonNode.Parse(mix ? """[{"id":"headset","name":"Headset","volume":0.8},{"id":"monitor","name":"Monitor A"}]"""
                : """[{"id":"monitor","name":"Monitor A"}]"""),
            ["channels"] = JsonNode.Parse(channel ? """[{"id":"music","name":"Music","levels":{"headset":0.6,"monitor":0.7}},{"id":"game","name":"Game"}]"""
                : """[{"id":"game","name":"Game"}]"""),
        };
        foreach (bool removeMix in new[] { false, true })
        {
            apply.Invoke(model, [State(true, true)]);
            MixViewModel master = model.Mixes.Single(m => m.Id == "headset");
            ChannelViewModel channel = model.Channels.Single(c => c.Id == "music");
            SendViewModel send = channel.Sends.Single(s => s.MixId == "headset");
            string removedKey = removeMix ? "mixvol:headset" : "lvl:music:headset";
            changes.Clear();
            try
            {
                master.Volume = .24;
                send.Level = .17;
                // A routine state refresh keeps both objects and their pending edits.
                apply.Invoke(model, [State(true, true)]);
                Assert.Same(master, model.Mixes.Single(m => m.Id == "headset"));
                Assert.Equal(.24, master.Volume);
                Assert.Equal(.17, send.Level);
                apply.Invoke(model, [State(removeMix, !removeMix)]);
                Assert.False(SliderSync.RecentlyTouched(removedKey));
                Assert.False(SliderSync.RecentlyTouched("lvl:music:headset"));
                // Detached bindings cannot enqueue new changes for the reused id either.
                send.Level = .91;
                send.Muted = true;
                if (removeMix)
                {
                    master.Volume = 1.5;
                    master.Muted = true;
                    master.VolumeRange.Boost = true;
                    Assert.Equal(0, boosts);
                }
                apply.Invoke(model, [State(true, true)]);
                Assert.Equal(removeMix ? .8 : .24, model.Mixes.Single(m => m.Id == "headset").Volume);
                Assert.Equal(.6, model.Channels.Single(c => c.Id == "music").Sends.Single(s => s.MixId == "headset").Level);
                SliderSync.FlushPending();
                Assert.Null(await client.SetLayoutOrderAsync([], [])); // barrier after every buffered write
                Assert.Equal(removeMix ? Array.Empty<string>() : ["setMixVolume::headset"], changes.ToArray());
            }
            finally
            {
                SliderSync.Forget("mixvol:headset");
                SliderSync.Forget("lvl:music:headset");
            }
        }

        foreach (bool replace in new[] { false, true })
        {
            var chain = new InsertsViewModel(client, "mix:headset", 2);
            JsonArray Insert(string plugin) => new(new JsonObject
            {
                ["insert"] = new JsonObject { ["id"] = "same-slot", ["kind"] = "lv2", ["plugin"] = plugin,
                    ["params"] = new JsonObject { ["gain"] = .5 } },
            });
            chain.Apply(Insert("urn:old"));
            var old = Assert.Single(chain.Items);
            old.SendParam("gain", .2);
            if (!replace) chain.Apply(null);
            chain.Apply(Insert("urn:new"));
            var current = Assert.Single(chain.Items);
            changes.Clear();
            try
            {
                old.SendParam("gain", .9);
                old.Bypass = true;
                old.NativeHost = true;
                chain.Remove(old);
                await chain.ShowNativeEditorAsync(old);
                await chain.RenameEffectAsync(old, "Old editor");
                SliderSync.FlushPending();
                Assert.Null(await client.SetLayoutOrderAsync([], []));
                Assert.Empty(changes);
                Assert.False(SliderSync.RecentlyTouched(chain.ParameterKey(old.Id, "gain")));
                Assert.Equal(.5, chain.CaptureChain().Inserts[0]!["params"]!["gain"]!.GetValue<double>());

                current.SendParam("gain", .4);
                SliderSync.FlushPending();
                Assert.Null(await client.SetLayoutOrderAsync([], []));
                Assert.Single(changes);
            }
            finally { current.ForgetPendingParameters(); }
        }

        // A retired Aux mix cannot toggle the port either.
        apply.Invoke(model, [JsonNode.Parse("""{"mixes":[{"id":"auxout","name":"Aux"}],"channels":[]}""")!]);
        MixViewModel aux = model.Mixes.Single(m => m.Id == "auxout");
        Assert.True(aux.AuxPortEnabled);
        apply.Invoke(model, [State(true, true)]);
        changes.Clear();
        aux.AuxPortEnabled = false;
        Assert.Null(await client.SetLayoutOrderAsync([], []));
        Assert.Empty(changes);

        apply.Invoke(model, [State(true, true)]);
        var retiredChain = model.Mixes.Single(m => m.Id == "headset").Inserts;
        apply.Invoke(model, [State(true, false)]);
        apply.Invoke(model, [State(true, true)]);
        var activeChain = model.Mixes.Single(m => m.Id == "headset").Inserts;
        var plugin = new PluginChoice("urn:test", "Test", "", new JsonArray());
        retiredChain.SelectedPlugin = plugin;
        Assert.False(retiredChain.CanAdd);
        Assert.False(retiredChain.CanEditEffects);
        changes.Clear();
        retiredChain.Add(plugin);
        Assert.False(await retiredChain.ApplyEffectsAsync(new EffectChainData(1, 2, new JsonArray())));
        Assert.Null(await client.SetLayoutOrderAsync([], []));
        Assert.Empty(changes);
        activeChain.Add(plugin);
        Assert.Null(await client.SetLayoutOrderAsync([], []));
        Assert.Single(changes);
    }
}
