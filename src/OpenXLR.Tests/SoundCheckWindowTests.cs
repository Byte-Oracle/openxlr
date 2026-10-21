using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

internal static class SoundCheckWindowTests
{
    internal static async Task CheckCaptureChain(MainViewModel main, MixInsertsWindow chain, ConcurrentQueue<JsonNode> commands)
    {
        InsertsViewModel inserts = main.CaptureInputs.Single(c => c.Id == "guest").Inserts!;
        SoundCheckViewModel model = inserts.SoundCheck;
        Click(chain, "SoundCheck");
        await Until(() => chain.OwnedWindows.OfType<SoundCheckWindow>().Any(w => w.IsVisible));
        SoundCheckWindow window = chain.OwnedWindows.OfType<SoundCheckWindow>().Single(w => w.IsVisible);
        WindowLayoutTests.Layout(window, 520, 360);
        Assert.Same(model, window.DataContext);
        Assert.False(main.CaptureInputs.Single(c => c.Id == "camera").Inserts!.CanSoundCheck);
        try
        {
            Click(window, "Record");
            await Until(() => model.Active && model.CanLoop);
            Assert.False(main.Inserts.SoundCheck.Active);
            Assert.False(main.Inserts2.SoundCheck.Active);
            Assert.False(main.CaptureInputs.Single(c => c.Id == "offline").Inserts!.SoundCheck.Active);
            Assert.Equal(Localizer.Format("SoundCheckRecording", 1.5), window.FindControl<TextBlock>("SoundCheckStatus")!.Text);
            Click(window, "LoopSample");
            await Until(() => model.Status == Localizer.Format("SoundCheckLooping", 1.5) && model.CanLoop);
            Click(window, "HearLiveMic");
            await Until(() => model.Status == Localizer.Format("SoundCheckReady", 1.5) && model.CanLoop);
            Click(window, "StopAndDiscard");
            await Until(() => !model.Active && model.CanRecord);
            Click(window, "Record");
            await Until(() => model.Active && model.CanLoop);
            window.Close();
            await Until(() => !window.IsVisible && !model.Active);
            Assert.Equal(["record", "loop", "live", "stop", "record", "stop"],
                commands.Select(c => c["action"]!.GetValue<string>()));
            Assert.All(commands, c => Assert.Equal("guest", c["channel"]!.GetValue<string>()));
            Click(chain, "SoundCheck");
            await Until(() => chain.OwnedWindows.OfType<SoundCheckWindow>().Any(w => w.IsVisible));
            window = chain.OwnedWindows.OfType<SoundCheckWindow>().Single(w => w.IsVisible);
            WindowLayoutTests.Layout(window, 520, 360);
            Assert.Same(model, window.DataContext);
            Assert.True(model.CanRecord);
            window.Close();
            await Until(() => !window.IsVisible);
        }
        finally { window.Close(); }
    }

    private static void Click(Window window, string key)
        => window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Localizer.Text(key)))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task Until(Func<bool> ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(15);
        Assert.True(ready(), "The capture Sound Check window did not settle.");
    }

    // Use the existing layout test's real X11 application and dispatcher.
    internal static void CheckPendingClose()
    {
        Task check = Verify();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!check.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(check.IsCompleted, "Sound Check close test hung.");
        check.GetAwaiter().GetResult();
    }

    private static async Task Verify()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() == "auth") continue;
                Assert.Equal("stop", command["action"]!.GetValue<string>());
                received.TrySetResult();
                await release.Task.WaitAsync(stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var window = new SoundCheckWindow { DataContext = new SoundCheckViewModel(client, "xlr1") };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            window.Show();
            window.Close();
            await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(window.IsVisible);
            Assert.False(window.IsEnabled);
            Assert.All(window.GetVisualDescendants().OfType<Button>(), button => Assert.False(button.IsEffectivelyEnabled));
            release.TrySetResult();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(window.IsVisible);
        }
        finally { release.TrySetResult(); window.Close(); }
    }
}
