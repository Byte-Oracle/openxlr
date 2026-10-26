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

internal static class MicSetupWindowTests
{
    internal static async Task CheckCaptureChain(MainViewModel main, MixInsertsWindow chain, ConcurrentQueue<JsonNode> commands,
        ConcurrentQueue<JsonNode> meterWatches, ConcurrentQueue<JsonNode> eqCommands, ConcurrentQueue<JsonNode> bypassCommands)
    {
        InsertsViewModel inserts = main.CaptureInputs.Single(c => c.Id == "guest").Inserts!;
        MicSetupViewModel model = inserts.MicSetup;
        Click(chain, "MicSetup");
        await Until(() => chain.OwnedWindows.OfType<MicSetupWindow>().Any(w => w.IsVisible));
        MicSetupWindow window = chain.OwnedWindows.OfType<MicSetupWindow>().Single(w => w.IsVisible);
        WindowLayoutTests.Layout(window, 600, 560);
        Assert.Same(model, window.DataContext);
        Assert.False(main.CaptureInputs.Single(c => c.Id == "camera").Inserts!.CanMicSetup);
        try
        {
            // Position first: a USB microphone on no unit OpenXLR drives gets
            // the general tips and is told to switch phantom power elsewhere.
            Assert.True(model.ShowsPosition);
            Assert.False(window.FindControl<Control>("MicSetupLevel")!.IsVisible);
            Assert.Equal(MicSetupUnitKind.None, model.UnitKind);
            Assert.Equal(Localizer.Text("MicSetupPositionNone"), window.FindControl<TextBlock>("MicSetupPositionIntro")!.Text);
            Assert.Equal(4, model.PositionTips.Count);
            Assert.True(window.FindControl<Control>("MicSetupPhantomTile")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Control>("MicSetupPhantom")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<TextBlock>("MicSetupPhantomElsewhere")!.IsEffectivelyVisible);

            // Then Level: the window keeps the meter before the chain
            // running, and its reading and clip reach this microphone only.
            Step(window, "MicSetupStepLevel");
            Assert.True(model.ShowsLevel);
            await Until(() => meterWatches.Any(c => c["channel"]?.GetValue<string>() == "guest") && model.Clipped);
            Assert.Equal(.8, model.InputPeak);
            Assert.Equal(.5, model.InputLevel);
            Assert.Equal(0, main.Inserts.MicSetup.InputPeak);
            Assert.Equal(Localizer.Format("MicSetupPeak", .8 * 60 - 60), window.FindControl<TextBlock>("MicSetupPeakValue")!.Text);
            Assert.True(window.FindControl<TextBlock>("MicSetupNoGain")!.IsEffectivelyVisible);
            window.FindControl<Button>("MicSetupClip")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(model.Clipped);
            // Suggest gain on a microphone OpenXLR sets no gain for: it
            // listens, says the change on the device, and offers no Accept.
            var time = new ManualTime();
            model.Time = time;
            Click(window, "MicSetupSuggestGain");
            Assert.True(model.Measuring);
            Assert.False(window.FindControl<Button>("MicSetupSuggestGain")!.IsEnabled);
            await Until(() => window.FindControl<TextBlock>("MicSetupSuggestAdvice")!.IsEffectivelyVisible);
            for (int i = 0; i < 100 && model.Measuring; i++)
            {
                time.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 15 + 1));
                model.ApplyInputLevel(new JsonArray(.5, .7, 0));
            }
            Assert.Equal(GainOutcome.Elsewhere, model.Suggestion!.Outcome);
            await Until(() => window.FindControl<Button>("MicSetupSuggestGain")!.IsEnabled
                && window.FindControl<TextBlock>("MicSetupSuggestMeasured")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Button>("MicSetupAcceptGain")!.IsVisible);
            Assert.True(window.FindControl<Button>("MicSetupDiscardGain")!.IsEffectivelyVisible);
            Click(window, "MicSetupDiscardGain");
            await Until(() => !window.FindControl<TextBlock>("MicSetupSuggestAdvice")!.IsVisible);
            Assert.False(window.FindControl<Control>("MicSetupListen")!.IsVisible);

            // Then Listen, from the step bar.
            Step(window, "MicSetupStepListen");
            Assert.True(model.ShowsListen);
            Assert.False(window.FindControl<Control>("MicSetupLevel")!.IsVisible);
            Click(window, "Record");
            await Until(() => model.Active && model.CanLoop);
            Assert.False(main.Inserts.MicSetup.Active);
            Assert.False(main.Inserts2.MicSetup.Active);
            Assert.False(main.CaptureInputs.Single(c => c.Id == "offline").Inserts!.MicSetup.Active);
            Assert.Equal(Localizer.Format("SoundCheckRecording", 1.5), window.FindControl<TextBlock>("MicSetupStatus")!.Text);
            Assert.False(model.HasTake);
            Click(window, "MicSetupLoopTake");
            // The state names a take this view has not seen, so it asks for it.
            await Until(() => model.Status == Localizer.Format("MicSetupLooping", 1.5) && model.HasTake);
            Assert.Equal(200, model.Peaks!.Count);
            Assert.Same(model.Peaks, window.FindControl<TakeWaveform>("MicSetupWaveform")!.Peaks);
            Assert.Equal(Localizer.Format("MicSetupHearMyselfLatency", 42.7), window.FindControl<TextBlock>("MicSetupLatency")!.Text);
            Assert.True(window.FindControl<TextBlock>("MicSetupNotHeard")!.IsEffectivelyVisible);

            // Raw and processed: the lit side is the daemon's.
            window.FindControl<ToggleButton>("MicSetupRaw")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => model.Raw && window.FindControl<ToggleButton>("MicSetupRaw")!.IsChecked == true
                && window.FindControl<ToggleButton>("MicSetupProcessed")!.IsChecked == false);
            window.FindControl<ToggleButton>("MicSetupProcessed")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !model.Raw && window.FindControl<ToggleButton>("MicSetupProcessed")!.IsChecked == true);
            // Hear myself, and the note that the microphone is not heard goes.
            window.FindControl<CheckBox>("MicSetupHearMyself")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => model.HearMyself && !model.NotHeard);
            Assert.False(window.FindControl<TextBlock>("MicSetupNotHeard")!.IsEffectivelyVisible);

            await CheckShape(window, main, model, eqCommands, bypassCommands);
            Step(window, "MicSetupStepListen");

            Click(window, "MicSetupLiveMic");
            await Until(() => model.Status == Localizer.Format("MicSetupReady", 1.5) && model.CanLoop);
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
            Click(chain, "MicSetup");
            await Until(() => chain.OwnedWindows.OfType<MicSetupWindow>().Any(w => w.IsVisible));
            window = chain.OwnedWindows.OfType<MicSetupWindow>().Single(w => w.IsVisible);
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
    private static async Task CheckShape(MicSetupWindow window, MainViewModel main, MicSetupViewModel model,
        ConcurrentQueue<JsonNode> eqCommands, ConcurrentQueue<JsonNode> bypassCommands)
    {
        MicEqViewModel eq = main.CaptureInputs.Single(c => c.Id == "guest").Eq!;
        Step(window, "MicSetupStepShape");
        WindowLayoutTests.Layout(window, 600, 560);
        Assert.True(model.ShowsShape);
        Assert.False(window.FindControl<Control>("MicSetupListen")!.IsVisible);
        Assert.Same(eq, model.Eq);
        Assert.Same(eq, window.FindControl<EqGraph>("MicSetupEqGraph")!.DataContext);
        Assert.Equal(4, window.FindControl<EqGraph>("MicSetupEqGraph")!.Handles.Count);
        Assert.True(window.FindControl<TextBlock>("MicSetupNoFilters")!.IsEffectivelyVisible);
        Assert.False(window.FindControl<Control>("MicSetupUnitFilters")!.IsEffectivelyVisible);
        Assert.False(window.FindControl<Control>("MicSetupSoftwareFilters")!.IsEffectivelyVisible);

        eqCommands.Clear();
        MicEq before = eq.Current;
        Assert.False(model.CanUndoPreset);
        Click(window, "MicSetupPresetWarm");
        await Until(() => eqCommands.Count == 1);
        JsonNode applied = eqCommands.Single();
        Assert.Equal("setEq", applied["cmd"]!.GetValue<string>());
        Assert.Equal("guest", applied["channel"]!.GetValue<string>());
        Assert.True(applied["eq"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(MicSetupPresets.Warm.Eq.Bands.Select(b => b.Frequency),
            applied["eq"]!["bands"]!.AsArray().Select(b => b!["frequency"]!.GetValue<double>()));
        Assert.Equal(MicSetupPresets.Warm.Eq, eq.Current);
        Assert.Equal(Localizer.Format("MicSetupPresetApplied", MicSetupPresets.Warm.Name),
            window.FindControl<TextBlock>("MicSetupPresetStatus")!.Text);
        Assert.True(window.FindControl<Button>("MicSetupUndoPreset")!.IsEffectivelyEnabled);
        window.FindControl<Button>("MicSetupUndoPreset")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => eqCommands.Count == 2);
        Assert.Equal(before, eq.Current);
        Assert.Equal(before.Bands.Select(b => b.Type), eqCommands.Last()["eq"]!["bands"]!.AsArray().Select(b => b!["type"]!.GetValue<string>()));
        Assert.False(model.CanUndoPreset);
        Assert.Equal(Localizer.Text("MicSetupPresetUndone"), window.FindControl<TextBlock>("MicSetupPresetStatus")!.Text);
        eqCommands.Clear();

        ToggleButton bypass = window.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Name == "MicSetupBypass");
        Assert.Equal(Localizer.Format("MicSetupBypassName", "Guest compressor with a long label"),
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
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MicSetupInsertEditor")
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
        Assert.True(ready(), "The capture Mic setup window did not settle.");
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
        Assert.True(check.IsCompleted, "Mic setup close test hung.");
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
        var window = new MicSetupWindow { DataContext = new MicSetupViewModel(client, "xlr1") };
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
