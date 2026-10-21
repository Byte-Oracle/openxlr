using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

internal static class VolumeRangeWindowTests
{
    internal static void Check()
    {
        var main = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        try
        {
            SliderSync.ReleaseTouchGuards();
            var vm = (MainViewModel)main.DataContext!;
            var requests = new List<bool>();
            vm.DesktopVolumeBoostRequested += requests.Add;
            Apply(vm, """
                {"devices":[],"mixer":{"outputVolume":0.67,"mixes":[
                  {"id":"monitor","name":"Monitor A","kind":"monitor","volume":0.67},
                  {"id":"monitor2","name":"Monitor B","kind":"monitor","volume":0.5},
                  {"id":"stream","name":"Stream","kind":"virtualMic","volume":0.4}]}}
                """);
            main.Show();
            Dispatcher.UIThread.RunJobs();
            var output = main.FindControl<Slider>("OutputVolumeSlider")!;
            vm.ApplyDesktopVolumeBoost(false);
            Assert.Equal(1, output.Maximum);
            Assert.Equal(.67, output.Value);
            vm.ApplyDesktopVolumeBoost(true);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1.5, output.Maximum);
            Assert.Equal(.67, output.Value);
            Assert.Equal("67%", vm.OutputVolumeText);
            Assert.Equal(.67, vm.Mixes[0].Volume);
            Assert.Equal(1.5, vm.Mixes[0].VolumeRange.Maximum);
            Assert.Equal(1, vm.Mixes[2].VolumeRange.Maximum);
            Assert.Empty(requests); // Readback must not echo the desktop setting.

            vm.Mixes[0].VolumeRange.Boost = false;
            Assert.Equal(new[] { false }, requests);
            Assert.Equal(1, output.Maximum);
            Assert.Equal(1, vm.Mixes[1].VolumeRange.Maximum);
            Assert.Equal(.67, output.Value);
            vm.Mixes[1].Volume = 1.2; // Also opens Plasma's range for a boosted readback.
            Assert.Equal(new[] { false, true }, requests);
            Assert.Equal(1.5, output.Maximum);
            Assert.Equal(.67, vm.OutputVolume);
            output.Value = 1.5;
            Assert.Equal("150%", vm.OutputVolumeText);
            vm.ApplyDesktopVolumeBoost(false);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, output.Maximum);
            Assert.Equal(1, output.Value);
            Assert.Equal(1, vm.Mixes[1].Volume);
            Assert.Equal(.67, vm.Mixes[0].Volume);
            Assert.Equal(.4, vm.Mixes[2].Volume);

            vm.ApplyDesktopVolumeBoost(true);
            Apply(vm, """
                {"devices":[],"mixer":{"outputVolume":1,"mixes":[
                  {"id":"new","name":"New monitor","kind":"monitor","volume":0.25}]}}
                """);
            Assert.Equal(1.5, vm.Mixes[0].VolumeRange.Maximum);
            Assert.Equal(.25, vm.Mixes[0].Volume);
            vm.VolumeRangeError = "Desktop preference is locked.";
            Assert.True(vm.HasVolumeRangeError);
            vm.VolumeRangeError = null;
            Assert.False(vm.HasVolumeRangeError);
        }
        finally { main.Quit(); }

        Task pending = CheckMonitorMuteBinding();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(pending.IsCompleted, "Monitoring mute binding check hung.");
        pending.GetAwaiter().GetResult();
    }

    private static async Task CheckMonitorMuteBinding()
    {
        var commands = new ConcurrentQueue<JsonNode>();
        var errors = new ConcurrentQueue<string>();
        bool paused = false;
        int rejectNextToggle = 0;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() == "auth") continue;
                commands.Enqueue(command);
                if (command["cmd"]!.GetValue<string>() == "toggleMonitorMuted")
                {
                    if (Interlocked.Exchange(ref rejectNextToggle, 0) == 0) paused = !paused;
                    else
                    {
                        await SocketTestServer.Send(socket, new { type = "error", message = "Monitor routes are unavailable" }, stop);
                        await SocketTestServer.Send(socket, new { type = "state", mixer = new { monitorMuted = paused } }, stop);
                    }
                }
                if (command["requestId"] is not { } requestId) continue;
                if (command["cmd"]!.GetValue<string>() == "listPlugins")
                    await SocketTestServer.Send(socket, new { type = "plugins", plugins = Array.Empty<object>() }, stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = requestId.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        client.ErrorReceived += errors.Enqueue;
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        MainWindow? window = null;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window = new MainWindow(client);
                window.Show();
            });
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var model = (MainViewModel)window!.DataContext!;
                var mute = window.FindControl<ToggleButton>("MonitorMute")!;
                Assert.False(model.MonitorMuted);
                Assert.False(mute.IsChecked);
                Apply(model, """{"devices":[],"mixer":{"monitorMuted":true,"mixes":[],"channels":[],"inserts":{}}}""");
                Assert.True(model.MonitorMuted);
                Assert.True(mute.IsChecked);
                Apply(model, """{"devices":[],"mixer":{"mixes":[],"channels":[],"inserts":{}}}""");
                Assert.False(model.MonitorMuted); // An older daemon must clear a previously read pause.
                Assert.False(mute.IsChecked);
                Apply(model, """{"mixer":{"monitorMuted":true}}""");
                Assert.True(mute.IsChecked); // The state does not depend on device enumeration.
                Apply(model, """{"devices":[]}""");
                Assert.False(mute.IsChecked);
            });
            // An acknowledged command on the same socket is a barrier for
            // any accidental edits caused by the preceding binding readback.
            Assert.Null(await client.AdjustOutputVolumeAsync("fixture", 0));
            Assert.DoesNotContain(commands, command => command["cmd"]!.GetValue<string>() == "toggleMonitorMuted");
            for (int press = 0; press < 3; press++)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var model = (MainViewModel)window!.DataContext!;
                    var mute = window.FindControl<ToggleButton>("MonitorMute")!;
                    // Each delayed frame describes the state before the last
                    // command. It must never determine the next command's value.
                    if (press > 0) Apply(model, new JsonObject
                    {
                        ["mixer"] = new JsonObject { ["monitorMuted"] = press % 2 == 0 },
                    }.ToJsonString());
                    mute.IsChecked = !mute.IsChecked;
                });
                Assert.Null(await client.AdjustOutputVolumeAsync("fixture", 0));
                Assert.Equal(press % 2 == 0, paused);
            }
            JsonNode[] edits = commands.Where(command => command["cmd"]!.GetValue<string>() == "toggleMonitorMuted").ToArray();
            Assert.Equal(3, edits.Length);
            Assert.All(edits, command => Assert.Single(command.AsObject()));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Apply((MainViewModel)window!.DataContext!, """{"mixer":{"monitorMuted":true}}""");
                Assert.True(window.FindControl<ToggleButton>("MonitorMute")!.IsChecked);
            });

            Interlocked.Exchange(ref rejectNextToggle, 1);
            await Dispatcher.UIThread.InvokeAsync(() => window!.FindControl<ToggleButton>("MonitorMute")!.IsChecked = false);
            Assert.Null(await client.AdjustOutputVolumeAsync("fixture", 0));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Assert.True(window!.FindControl<ToggleButton>("MonitorMute")!.IsChecked);
                Assert.True(((MainViewModel)window!.DataContext!).MonitorMuted);
            });
            Assert.True(paused);
            Assert.Contains("Monitor routes are unavailable", errors);
            Assert.Equal(4, commands.Count(command => command["cmd"]!.GetValue<string>() == "toggleMonitorMuted"));

            await client.DisposeAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Assert.False(((MainViewModel)window!.DataContext!).DaemonConnected);
                window.FindControl<ToggleButton>("MonitorMute")!.IsChecked = false;
            });
            Assert.Contains(Localizer.Text("DaemonDisconnectedNoChange"), errors);
            Assert.Equal(4, commands.Count(command => command["cmd"]!.GetValue<string>() == "toggleMonitorMuted"));
        }
        finally
        {
            if (window is not null) await Dispatcher.UIThread.InvokeAsync(window.Quit);
        }
    }

    private static void Apply(MainViewModel model, string state)
        => typeof(MainViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [JsonNode.Parse(state)!]);
}
