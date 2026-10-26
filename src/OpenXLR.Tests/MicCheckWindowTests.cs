using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

internal static class MicCheckWindowTests
{
    internal static async Task CheckCaptureChain(MainViewModel main, MixInsertsWindow chain, ConcurrentQueue<JsonNode> commands,
        ConcurrentQueue<JsonNode> meterWatches, ConcurrentQueue<JsonNode> eqCommands, ConcurrentQueue<JsonNode> bypassCommands)
    {
        InsertsViewModel inserts = main.CaptureInputs.Single(c => c.Id == "guest").Inserts!;
        MicCheckViewModel model = inserts.MicCheck;
        Click(chain, "MicCheck");
        await Until(() => chain.OwnedWindows.OfType<MicCheckWindow>().Any(w => w.IsVisible));
        MicCheckWindow window = chain.OwnedWindows.OfType<MicCheckWindow>().Single(w => w.IsVisible);
        WindowLayoutTests.Layout(window, 600, 560);
        Assert.Same(model, window.DataContext);
        Assert.False(main.CaptureInputs.Single(c => c.Id == "camera").Inserts!.CanMicCheck);
        try
        {
            // Position first: a USB microphone on no unit OpenXLR drives gets
            // the general tips and is told to switch phantom power elsewhere.
            Assert.True(model.ShowsPosition);
            Assert.False(window.FindControl<Control>("MicCheckLevel")!.IsVisible);
            Assert.Equal(MicCheckUnitKind.None, model.UnitKind);
            Assert.Equal(Localizer.Text("MicCheckPositionNone"), window.FindControl<TextBlock>("MicCheckPositionIntro")!.Text);
            Assert.Equal(4, model.PositionTips.Count);
            Assert.True(window.FindControl<Control>("MicCheckPhantomTile")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Control>("MicCheckPhantom")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<TextBlock>("MicCheckPhantomElsewhere")!.IsEffectivelyVisible);

            // Then Level: the window keeps the meter before the chain
            // running, and its reading and clip reach this microphone only.
            Step(window, "MicCheckStepLevel");
            Assert.True(model.ShowsLevel);
            await Until(() => meterWatches.Any(c => c["channel"]?.GetValue<string>() == "guest") && model.Clipped);
            Assert.Equal(.8, model.InputPeak);
            Assert.Equal(.5, model.InputLevel);
            Assert.Equal(0, main.Inserts.MicCheck.InputPeak);
            Assert.Equal(Localizer.Format("MicCheckPeak", .8 * 60 - 60), window.FindControl<TextBlock>("MicCheckPeakValue")!.Text);
            Assert.True(window.FindControl<TextBlock>("MicCheckNoGain")!.IsEffectivelyVisible);
            window.FindControl<Button>("MicCheckClip")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(model.Clipped);
            Assert.False(window.FindControl<Control>("MicCheckListen")!.IsVisible);

            // Then Listen, from the step bar.
            Step(window, "MicCheckStepListen");
            Assert.True(model.ShowsListen);
            Assert.False(window.FindControl<Control>("MicCheckLevel")!.IsVisible);
            Click(window, "Record");
            await Until(() => model.Active && model.CanLoop);
            Assert.False(main.Inserts.MicCheck.Active);
            Assert.False(main.Inserts2.MicCheck.Active);
            Assert.False(main.CaptureInputs.Single(c => c.Id == "offline").Inserts!.MicCheck.Active);
            Assert.Equal(Localizer.Format("SoundCheckRecording", 1.5), window.FindControl<TextBlock>("MicCheckStatus")!.Text);
            Assert.False(model.HasTake);
            Click(window, "MicCheckLoopTake");
            // The state names a take this view has not seen, so it asks for it.
            await Until(() => model.Status == Localizer.Format("MicCheckLooping", 1.5) && model.HasTake);
            Assert.Equal(200, model.Peaks!.Count);
            Assert.Same(model.Peaks, window.FindControl<TakeWaveform>("MicCheckWaveform")!.Peaks);
            Assert.Equal(Localizer.Format("MicCheckHearMyselfLatency", 42.7), window.FindControl<TextBlock>("MicCheckLatency")!.Text);
            Assert.True(window.FindControl<TextBlock>("MicCheckNotHeard")!.IsEffectivelyVisible);

            // Raw and processed: the lit side is the daemon's.
            window.FindControl<ToggleButton>("MicCheckRaw")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => model.Raw && window.FindControl<ToggleButton>("MicCheckRaw")!.IsChecked == true
                && window.FindControl<ToggleButton>("MicCheckProcessed")!.IsChecked == false);
            window.FindControl<ToggleButton>("MicCheckProcessed")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !model.Raw && window.FindControl<ToggleButton>("MicCheckProcessed")!.IsChecked == true);
            // Hear myself, and the note that the microphone is not heard goes.
            window.FindControl<CheckBox>("MicCheckHearMyself")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => model.HearMyself && !model.NotHeard);
            Assert.False(window.FindControl<TextBlock>("MicCheckNotHeard")!.IsEffectivelyVisible);

            await CheckShape(window, main, model, eqCommands, bypassCommands);
            Step(window, "MicCheckStepListen");

            Click(window, "MicCheckLiveMic");
            await Until(() => model.Status == Localizer.Format("MicCheckReady", 1.5) && model.CanLoop);
            Click(window, "StopAndDiscard");
            await Until(() => !model.Active && model.CanRecord && !model.HasTake);
            Click(window, "Record");
            await Until(() => model.Active && model.CanLoop);
            window.Close();
            await Until(() => !window.IsVisible && !model.Active);
            Assert.Equal(["record", "loop", "raw:True", "raw:False", "hear:True", "live", "stop", "record", "stop"],
                commands.Select(c => c["cmd"]!.GetValue<string>() switch
                {
                    "setSoundCheckRaw" => $"raw:{c["value"]!.GetValue<bool>()}",
                    "setSoundCheckHearMyself" => $"hear:{c["value"]!.GetValue<bool>()}",
                    _ => c["action"]!.GetValue<string>(),
                }));
            Assert.All(commands, c => Assert.Equal("guest", c["channel"]!.GetValue<string>()));

            // A reopened check starts on its last step with nothing running.
            Click(chain, "MicCheck");
            await Until(() => chain.OwnedWindows.OfType<MicCheckWindow>().Any(w => w.IsVisible));
            window = chain.OwnedWindows.OfType<MicCheckWindow>().Single(w => w.IsVisible);
            WindowLayoutTests.Layout(window, 600, 560);
            Assert.Same(model, window.DataContext);
            Assert.True(model.CanRecord);
            window.Close();
            await Until(() => !window.IsVisible);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Shape while the take loops: the graph is the guest's own EQ, a preset
    /// goes out as one setEq and its undo as another with the EQ from before,
    /// a bypass reaches the guest's insert, the editor button opens that
    /// insert's controls under the check, and a capture input says it has no
    /// low cut or ClipGuard of its own.
    /// </summary>
    private static async Task CheckShape(MicCheckWindow window, MainViewModel main, MicCheckViewModel model,
        ConcurrentQueue<JsonNode> eqCommands, ConcurrentQueue<JsonNode> bypassCommands)
    {
        MicEqViewModel eq = main.CaptureInputs.Single(c => c.Id == "guest").Eq!;
        Step(window, "MicCheckStepShape");
        WindowLayoutTests.Layout(window, 600, 560);
        Assert.True(model.ShowsShape);
        Assert.False(window.FindControl<Control>("MicCheckListen")!.IsVisible);
        Assert.Same(eq, model.Eq);
        Assert.Same(eq, window.FindControl<EqGraph>("MicCheckEqGraph")!.DataContext);
        Assert.Equal(4, window.FindControl<EqGraph>("MicCheckEqGraph")!.Handles.Count);
        Assert.True(window.FindControl<TextBlock>("MicCheckNoFilters")!.IsEffectivelyVisible);
        Assert.False(window.FindControl<Control>("MicCheckUnitFilters")!.IsEffectivelyVisible);
        Assert.False(window.FindControl<Control>("MicCheckSoftwareFilters")!.IsEffectivelyVisible);

        eqCommands.Clear();
        MicEq before = eq.Current;
        Assert.False(model.CanUndoPreset);
        Click(window, "MicCheckPresetWarm");
        await Until(() => eqCommands.Count == 1);
        JsonNode applied = eqCommands.Single();
        Assert.Equal("setEq", applied["cmd"]!.GetValue<string>());
        Assert.Equal("guest", applied["channel"]!.GetValue<string>());
        Assert.True(applied["eq"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(MicCheckPresets.Warm.Eq.Bands.Select(b => b.Frequency),
            applied["eq"]!["bands"]!.AsArray().Select(b => b!["frequency"]!.GetValue<double>()));
        Assert.Equal(MicCheckPresets.Warm.Eq, eq.Current);
        Assert.Equal(Localizer.Format("MicCheckPresetApplied", MicCheckPresets.Warm.Name),
            window.FindControl<TextBlock>("MicCheckPresetStatus")!.Text);
        Assert.True(window.FindControl<Button>("MicCheckUndoPreset")!.IsEffectivelyEnabled);
        window.FindControl<Button>("MicCheckUndoPreset")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => eqCommands.Count == 2);
        Assert.Equal(before, eq.Current);
        Assert.Equal(before.Bands.Select(b => b.Type), eqCommands.Last()["eq"]!["bands"]!.AsArray().Select(b => b!["type"]!.GetValue<string>()));
        Assert.False(model.CanUndoPreset);
        Assert.Equal(Localizer.Text("MicCheckPresetUndone"), window.FindControl<TextBlock>("MicCheckPresetStatus")!.Text);
        eqCommands.Clear();

        ToggleButton bypass = window.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Name == "MicCheckBypass");
        Assert.Equal(Localizer.Format("MicCheckBypassName", "Guest compressor with a long label"),
            Avalonia.Automation.AutomationProperties.GetName(bypass));
        bypass.IsChecked = true;
        await Until(() => bypassCommands.Any());
        JsonNode sent = Assert.Single(bypassCommands);
        Assert.Equal(("guest", "compressor", true),
            (sent["channel"]!.GetValue<string>(), sent["insertId"]!.GetValue<string>(), sent["value"]!.GetValue<bool>()));
        await Until(() => model.Chain!.Items.Single().Bypass);
        bypass.IsChecked = false;
        await Until(() => bypassCommands.Count == 2 && !model.Chain!.Items.Single().Bypass);

        // No plugin host runs the insert here, so the editor is the generated controls, owned by the check.
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MicCheckInsertEditor")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => window.OwnedWindows.OfType<InsertControlsWindow>().Any(w => w.IsVisible));
        InsertControlsWindow controls = window.OwnedWindows.OfType<InsertControlsWindow>().Single(w => w.IsVisible);
        Assert.Same(model.Chain!.Items.Single(), controls.DataContext);
        controls.Close();
        await Until(() => !controls.IsVisible);
    }

    private static void Click(Window window, string key)
        => window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Localizer.Text(key)) && b is not ToggleButton)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Step(Window window, string key)
        => window.GetVisualDescendants().OfType<ToggleButton>().Single(b => Equals(b.Content, Localizer.Text(key)))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task Until(Func<bool> ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(15);
        Assert.True(ready(), "The capture Mic check window did not settle.");
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
        Assert.True(check.IsCompleted, "Mic check close test hung.");
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
                if (command["cmd"]!.GetValue<string>() == "watchInputMeter") continue;
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
        var window = new MicCheckWindow { DataContext = new MicCheckViewModel(client, "xlr1") };
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
