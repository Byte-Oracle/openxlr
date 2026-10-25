using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpenXLR.UI;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

internal static class OptionsInteractionWindowTests
{
    internal static void Check()
    {
        UiSettings previous = UiSettings.Load();
        var main = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        try
        {
            typeof(MainViewModel).GetMethod("ApplyMixer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(main.DataContext, [System.Text.Json.Nodes.JsonNode.Parse("""{"mixes":[],"channels":[]}""")]);
            main.Show();
            Layout(main, 640, 900);
            var button = main.FindControl<Button>("OptionsButton")!;
            void Open() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Open();
            var options = Assert.Single(main.OwnedWindows.OfType<OptionsWindow>());
            Assert.True(main.IsEffectivelyEnabled, "Options must leave the mixer interactive.");
            Assert.Same(main.DataContext, ((OptionsViewModel)options.DataContext!).Main);
            Layout(options, 640, 800);
            options.Position = new PixelPoint(main.Position.X + (int)Math.Ceiling(main.Bounds.Width * main.DesktopScaling) + 20, main.Position.Y);
            Open();
            Assert.Same(options, Assert.Single(main.OwnedWindows.OfType<OptionsWindow>()));
            options.WindowState = WindowState.Minimized;
            Open();
            Assert.Equal(WindowState.Normal, options.WindowState);

            var child = new Window { Width = 240, Height = 120 };
            _ = child.ShowDialog(options);
            Assert.True(main.IsEffectivelyEnabled, "An Options child dialog must not disable the mixer.");
            Open();
            Assert.Same(options, Assert.Single(main.OwnedWindows.OfType<OptionsWindow>()));
            child.Close();

            // A native pointer click, not a programmatic value change, proves
            // the mixer accepts input while its Options window remains open.
            var arrange = main.FindControl<ToggleButton>("ArrangeButton")!;
            main.Activate();
            using (var pointer = new XPointer())
            {
                PixelPoint point = arrange.PointToScreen(new Point(arrange.Bounds.Width / 2, arrange.Bounds.Height / 2));
                pointer.MoveTo(point.X, point.Y);
                pointer.Click();
                using var stop = new CancellationTokenSource();
                DispatcherTimer.RunOnce(stop.Cancel, TimeSpan.FromMilliseconds(150));
                Dispatcher.UIThread.MainLoop(stop.Token);
            }
            Assert.True(arrange.IsChecked);
            Assert.True(options.IsVisible);
            Capture(main, "settings-modeless-main");
            Capture(options, "settings-modeless-options");
            var model = (MainViewModel)main.DataContext!;
            Assert.Null(model.ChooseMixerView(MainViewModel.CompactView));
            Open();
            Assert.Equal(MainViewModel.CompactView, ((OptionsViewModel)options.DataContext!).SelectedMixerView!.Id);
            options.Close();
            Open();
            Assert.NotSame(options, Assert.Single(main.OwnedWindows.OfType<OptionsWindow>()));
            main.Quit();
            Assert.Empty(main.OwnedWindows);
        }
        finally { main.Quit(); Assert.Null(previous.Save()); }
    }
}
