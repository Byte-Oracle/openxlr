using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Views;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

internal static partial class InputBindingWindowTests
{
    internal static void CheckCaptureInputs()
    {
        Task pending = CaptureBindingsAndCreationUseNamedPorts();
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(pending.IsCompleted, "The capture binding window check hung.");
        pending.GetAwaiter().GetResult();
    }

    private static async Task CaptureBindingsAndCreationUseNamedPorts()
    {
        JsonNode state = CaptureInputWindowTests.State();
        state["devices"]!.AsArray().Add(JsonNode.Parse("""
            {"name":"four.ports","description":"Four inputs","kind":1,"channels":4,"positions":["FL","FR","AUX0","AUX1"]}
            """));
        state["devices"]!.AsArray().Add(JsonNode.Parse("""
            {"name":"OpenXLR_chat","description":"Own source","kind":1,"isOwn":true}
            """));
        var commands = new ConcurrentQueue<JsonNode>();
        int rejects = 1;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Send(socket, state, stop);
            while (!stop.IsCancellationRequested)
            {
                JsonNode cmd = await SocketTestServer.Receive(socket, stop);
                if (cmd["cmd"]?.GetValue<string>() is not ("setCaptureSource" or "createCaptureChannel")) continue;
                commands.Enqueue(cmd);
                string? error = null;
                if (cmd["cmd"]!.GetValue<string>() == "setCaptureSource")
                {
                    if (rejects-- > 0) error = "Settings could not be saved.";
                    else
                    {
                        JsonNode channel = state["mixer"]!["channels"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == cmd["channel"]!.GetValue<string>())!;
                        channel["captureSource"] = cmd["source"]!.DeepClone();
                        channel["capturePair"] = cmd["capturePair"]!.DeepClone();
                        channel["captureMonoChannel"] = cmd["captureMonoChannel"]?.DeepClone();
                        channel["binding"] = new JsonObject { ["kind"] = "source", ["source"] = cmd["source"]!.DeepClone(),
                            ["pair"] = cmd["capturePair"]!.DeepClone(), ["monoChannel"] = cmd["captureMonoChannel"]?.DeepClone() };
                    }
                }
                await SocketTestServer.Send(socket, state, stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = cmd["requestId"]!.GetValue<string>(), error }, stop);
            }
        });
        var window = new MainWindow(new DaemonClient(server.Url));
        var main = (MainViewModel)window.DataContext!;
        using var rack = new InputRackViewModel(main);
        using var matrix = new MixMatrixViewModel(main);
        var surface = new Window { Width = 1040, Height = 1000, Content = new ScrollViewer
            { Content = new StackPanel { Children = { new InputRackView { DataContext = rack }, new MixMatrixView { DataContext = matrix } } } } };
        var setup = new MixerSetupWindow { DataContext = main };
        try
        {
            window.Show();
            await Until(() => main.CaptureInputs.Count == 3);
            surface.Show();
            Layout(surface, 1040, 1000);
            ChannelViewModel guest = main.Channels.Single(c => c.Id == "guest");
            Button chip = RackStrip(surface, "guest").GetVisualDescendants().OfType<CaptureSourceChip>().Single().FindControl<Button>("Chip")!;
            Assert.True(chip.Focusable);
            Assert.True(chip.IsHitTestVisible);
            chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Window dialog = await Owned(surface);
            ComboBox source = Find<ComboBox>(dialog, "InputBindingSource"), port = Find<ComboBox>(dialog, "InputBindingPort");
            Assert.DoesNotContain(source.Items.OfType<DeviceChoice>(), c => c.Name is null || c.Name.StartsWith("OpenXLR", StringComparison.Ordinal));
            Assert.Contains(source.Items.OfType<DeviceChoice>(), c => c.Name == InputBindingModelTests.ProNode);
            Assert.False(Find<CheckBox>(dialog, "KeepUnitMicrophone").IsVisible);
            Assert.Equal(0, ((InputBindingDialog.PortChoice)port.SelectedItem!).MonoChannel);
            source.SelectedItem = source.Items.OfType<DeviceChoice>().Single(c => c.Name == "four.ports");
            Assert.Equal(0, ((InputBindingDialog.PortChoice)port.SelectedItem!).MonoChannel);
            port.SelectedItem = port.Items.OfType<InputBindingDialog.PortChoice>().Single(p => p.MonoChannel == 2);
            Assert.Contains("AUX0", ((InputBindingDialog.PortChoice)port.SelectedItem!).Label);
            Layout(dialog, 360, 420);
            foreach (Control control in new Control[] { source, port, Find<Button>(dialog, "ApplyInputBinding") }) AssertInside(control, dialog);
            Find<Button>(dialog, "ApplyInputBinding").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Find<TextBlock>(dialog, "InputBindingError").Text?.Contains("could not be saved", StringComparison.Ordinal) == true);
            Assert.True(dialog.IsVisible);
            Assert.Equal("usb.guest", guest.CaptureSource);
            Find<Button>(dialog, "ApplyInputBinding").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !dialog.IsVisible);
            JsonNode sent = commands.Last();
            Assert.Equal("setCaptureSource", sent["cmd"]!.GetValue<string>());
            Assert.Equal("guest", sent["channel"]!.GetValue<string>());
            Assert.Equal("four.ports", sent["source"]!.GetValue<string>());
            Assert.Equal(0, sent["capturePair"]!.GetValue<int>());
            Assert.Equal(2, sent["captureMonoChannel"]!.GetValue<int>());
            Assert.Same(guest, main.Channels.Single(c => c.Id == "guest"));

            // The matrix opens the stereo form, preserving a missing source and its pair.
            SkinLayoutWindowTests.RowHead(surface, "camera").GetVisualDescendants().OfType<Button>()
                .Single(b => b.Name == "MatrixBinding").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog = await Owned(surface);
            source = Find<ComboBox>(dialog, "InputBindingSource");
            port = Find<ComboBox>(dialog, "InputBindingPort");
            Assert.Equal("usb.camera", ((DeviceChoice)source.SelectedItem!).Name);
            Assert.Equal(1, ((InputBindingDialog.PortChoice)port.SelectedItem!).Pair);
            Assert.Null(((InputBindingDialog.PortChoice)port.SelectedItem!).MonoChannel);
            source.SelectedItem = source.Items.OfType<DeviceChoice>().Single(c => c.Name == "four.ports");
            port.SelectedItem = port.Items.OfType<InputBindingDialog.PortChoice>().Single(p => p.Stereo && p.Pair == 1);
            Assert.Contains("AUX0, AUX1", ((InputBindingDialog.PortChoice)port.SelectedItem!).Label);
            Find<Button>(dialog, "ApplyInputBinding").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !dialog.IsVisible);
            sent = commands.Last();
            Assert.Equal("camera", sent["channel"]!.GetValue<string>());
            Assert.Equal(1, sent["capturePair"]!.GetValue<int>());
            Assert.Null(sent["captureMonoChannel"]);

            // The classic chip keeps an unavailable mono port selected; cancelling sends nothing.
            window.GetVisualDescendants().OfType<CaptureSourceChip>().Single(c => c.DataContext is ChannelViewModel { Id: "offline" })
                .FindControl<Button>("Chip")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog = await Owned(window);
            Assert.Equal(2, ((InputBindingDialog.PortChoice)Find<ComboBox>(dialog, "InputBindingPort").SelectedItem!).MonoChannel);
            int before = commands.Count;
            Find<Button>(dialog, "CancelInputBinding").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !dialog.IsVisible);
            Assert.Equal(before, commands.Count);

            // Adding uses the same picker, selects mono on a mono source and writes the chosen port.
            setup.Show();
            Layout(setup, 600, 640);
            typeof(MixerSetupWindow).GetMethod("OnAddCapture", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(setup, [null, new RoutedEventArgs()]);
            dialog = await Owned(setup);
            Find<TextBox>(dialog, "CaptureName").Text = "New microphone";
            source = Find<ComboBox>(dialog, "CaptureSource");
            source.SelectedItem = source.Items.OfType<AudioDeviceItem>().Single(c => c.Name == "usb.guest");
            port = Find<ComboBox>(dialog, "CapturePort");
            Assert.Equal(0, ((InputBindingDialog.PortChoice)port.SelectedItem!).MonoChannel);
            source.SelectedItem = source.Items.OfType<AudioDeviceItem>().Single(c => c.Name == "four.ports");
            Assert.True(((InputBindingDialog.PortChoice)port.SelectedItem!).Stereo);
            port.SelectedItem = port.Items.OfType<InputBindingDialog.PortChoice>().Single(p => p.MonoChannel == 3);
            Assert.Contains("AUX1", ((InputBindingDialog.PortChoice)port.SelectedItem!).Label);
            Find<Button>(dialog, "CreateCapture").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => commands.Count == before + 1 && setup.IsEnabled);
            sent = commands.Last();
            Assert.Equal("createCaptureChannel", sent["cmd"]!.GetValue<string>());
            Assert.Equal(0, sent["capturePair"]!.GetValue<int>());
            Assert.Equal(3, sent["captureMonoChannel"]!.GetValue<int>());
        }
        finally
        {
            foreach (Window owner in new[] { setup, surface, window })
                foreach (Window owned in owner.OwnedWindows.ToArray()) owned.Close();
            setup.Close(); surface.Close(); window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
