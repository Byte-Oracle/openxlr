using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

internal static partial class SkinLayoutWindowTests
{
    private static void CheckCompactDesk()
    {
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        try
        {
            var model = (MainViewModel)window.DataContext!;
            FillBroadcastModel(model);
            model.Apps.Clear();
            model.ActiveApps.Clear();
            ChannelChoice[] choices = [new("music", "Music"), new("game", "Game"), new("chat", "Voice Chat")];
            AppStreamViewModel[] apps = [.. ColumnApps.Select((name, index) =>
            {
                var app = new AppStreamViewModel(new DaemonClient("ws://127.0.0.1:1/ws"), name, name, choices);
                app.ApplyFromDaemon(choices[index % choices.Length].Id, true, true);
                model.Apps.Add(app);
                return app;
            })];
            ApplicationIconWindowTests.Assign(model.Apps);
            Expander[] sections = [.. Usual.Select(name => window.FindControl<Expander>(name)!)];
            object?[] contents = [.. sections.Select(section => section.Content)];
            window.Show();
            Wear("compact-desk");
            Assert.Equal(["ProfilesButton", "FlowButton", "ArrangeButton", "HeaderOverflow"],
                window.FindControl<WrapPanel>("HeaderActions")!.Children.Select(c => c.Name));
            Assert.Equal(["RestartDaemonButton", "HeaderVersion", "AboutButton"],
                window.FindControl<StackPanel>("HeaderOverflowItems")!.Children.Select(c => c.Name));
            Assert.True(window.FindControl<DropDownButton>("HeaderOverflow")!.IsVisible);

            foreach (bool touch in new[] { false, true })
            foreach (bool second in new[] { false, true })
            {
                SkinService.ApplyControlSizing(touch);
                model.HasHp2 = second;
                model.CapCrossfade = second;
                foreach (int count in new[] { 3, 13 })
                {
                    ShowApps(model, apps, count);
                    foreach (double width in new[] { 640d, 1280, 1600, 2560 })
                    {
                        Layout(window, width, 900);
                        string where = $"Compact Desk: {width}, {count} apps, two jacks {second}, touch {touch}";
                        Border Card(string name) => (Border)window.FindControl<Expander>(name)!.Parent!;
                        var scroll = window.FindControl<ScrollViewer>("SectionScroll")!;
                        Border headphones = Card("HeadphonesTile"), monitor = Card("MonitorTile"), mixer = Card("SubmixerTile");
                        Assert.Equal(600, headphones.Bounds.Width, 0.5);
                        AssertNoOverlap([headphones, monitor, mixer, Card("ApplicationsTile")]);
                        foreach (string name in new[] { "HeadphonesTile", "MonitorTile", "ApplicationsTile" })
                            AssertControlsInside(Card(name), scroll);
                        Assert.Equal(window.LayoutHost.Root.Bounds.Width, mixer.Bounds.Width, 0.5);
                        if (width >= 1280)
                        {
                            Assert.True(monitor.Bounds.Width > 360, where);
                            Assert.Equal(window.FindControl<StackPanel>("MixerContent")!.Bounds.Width, mixer.Bounds.Width, 0.5);
                        }
                        var rows = window.FindControl<ItemsControl>("ApplicationRows")!;
                        var panel = Assert.IsType<ApplicationColumnsPanel>(rows.ItemsPanelRoot);
                        Assert.Equal(1, panel.PerColumn);
                        Border[] tiles = ApplicationTiles(window);
                        Assert.Equal(count, tiles.Length);
                        AssertNoOverlap(tiles);
                        CheckTileParts(window, tiles, where);
                        foreach (Border tile in tiles) AssertInside(tile, Card("ApplicationsTile"));
                        if (width >= 1280 && count == 3)
                            Assert.All(tiles, tile => Assert.Equal(0, tile.TranslatePoint(default, panel)!.Value.Y, 0.5));
                        Assert.NotNull(window.MixMatrix);
                        Assert.Equal(6, window.MixMatrix.Columns.Count);
                        CheckMatrixCells(window, window.MixMatrix, where);
                        Assert.False(Card("InputsTile").IsVisible);
                        if (count == 3 && !second && !touch)
                            Capture(window, $"skin-compact-desk-{width}");
                        if (width == 1280 && count == 13 && second)
                            Capture(window, $"skin-compact-desk-many-{(touch ? "touch" : "standard")}-1280");
                    }
                }
            }
            // Returning to another skin keeps the same section contents and application objects.
            Wear(SkinPackage.DefaultId);
            Layout(window, 1280, 900);
            Assert.Null(window.MixMatrix);
            for (int i = 0; i < sections.Length; i++) Assert.Same(contents[i], sections[i].Content);
            Assert.Equal(apps, model.ActiveApps);
        }
        finally
        {
            SkinService.ApplyControlSizing(false);
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }
}
