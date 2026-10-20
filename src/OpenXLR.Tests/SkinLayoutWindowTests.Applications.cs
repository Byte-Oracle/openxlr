using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// APPLICATIONS in columns, as the shipped Broadcast and Wave skins draw it:
/// with 1, 6, 7 and 13 applications, at 640, 1600 and 2560 pixels, in
/// Standard and Touch sizing, the chips stand in columns of at most six in
/// the applications' order, the seventh starting the next column, and a
/// column that does not fit goes on below, whole, with nothing scrolling
/// sideways. Each chip is exactly as wide as the same skin draws it in the
/// classic wrap, since both are the same chip.
/// </summary>
internal static partial class SkinLayoutWindowTests
{
    private static readonly string[] ColumnApps =
    [
        "Looking Glass", "Discord", "Google Chrome", "Karere", "Signal", "Steam", "Firefox",
        "Spotify", "OBS Studio", "VLC media player", "Telegram", "Zoom", "Thunderbird",
    ];

    private static void CheckApplicationColumns(string dir)
    {
        // Each shipped skin, and a copy of it that draws the chips in the classic
        // wrap, so a chip's width is compared under the same tokens.
        foreach (string id in new[] { "broadcast", "wave" })
        {
            JsonNode skin;
            using (Stream stream = typeof(SkinCatalog).Assembly.GetManifestResourceStream($"OpenXLR.UI.Assets.Skins.{id}.skin.json")!)
                skin = JsonNode.Parse(stream)!;
            JsonNode applications = skin["layout"]!["regions"]!["applications"]!;
            Assert.Equal(SkinLayoutTemplates.ApplicationColumns, applications["template"]!.GetValue<string>());
            Assert.Equal(6, applications["perColumn"]!.GetValue<int>());
            skin["layout"]!["regions"]!["applications"] = new JsonObject { ["template"] = SkinLayoutTemplates.ApplicationChips };
            skin["id"] = $"{id}-chips";
            skin["name"] = $"{id} chips";
            Write(dir, $"{id}-chips", skin.ToJsonString());
        }

        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        try
        {
            var model = (MainViewModel)window.DataContext!;
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse("""
                {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},{"id":"stream","name":"Stream","kind":"virtualMic"}],
                 "channels":[{"id":"music","name":"Music","levels":{"monitor":0.5}},{"id":"game","name":"Game"},
                             {"id":"chat","name":"Voice Chat"}]}
                """)]);
            ChannelChoice[] choices = [new("music", "Music"), new("game", "Game"), new("chat", "Voice Chat")];
            AppStreamViewModel[] all = [.. ColumnApps.Select((name, i) =>
            {
                var app = new AppStreamViewModel(new DaemonClient("ws://127.0.0.1:1/ws"), name.ToLowerInvariant(), name, choices);
                app.ApplyFromDaemon(choices[i % choices.Length].Id, i % 4 != 1, true);
                model.Apps.Add(app);
                return app;
            })];
            window.Show();
            Layout(window, 1040, 900);

            // A chip's width depends on the skin and the sizing, not on how many
            // there are or the window's width, so the wrap is measured once for each.
            foreach (string id in new[] { "broadcast", "wave" })
            foreach (bool touch in new[] { false, true })
            {
                SkinService.ApplyControlSizing(touch);
                ShowApps(model, all, all.Length);
                Wear($"{id}-chips");
                Layout(window, 1600, 1400);
                Assert.IsType<WrapPanel>(window.FindControl<ItemsControl>("ApplicationRows")!.ItemsPanelRoot);
                Dictionary<AppStreamViewModel, double> wrapped = ApplicationChips(window)
                    .ToDictionary(c => (AppStreamViewModel)c.DataContext!, c => c.Bounds.Width);
                Assert.Equal(all.Length, wrapped.Count);

                Wear(id);
                foreach (int count in new[] { 1, 6, 7, 13 })
                {
                    ShowApps(model, all, count);
                    foreach (double width in new[] { 640d, 1600, 2560 })
                    {
                        Layout(window, width, 1400);
                        CheckColumnsAt(window, model, wrapped, $"{id}, {count} applications, {(touch ? "touch" : "standard")} at {width}");
                        if (count == 13 && !touch) Capture(window, $"skin-{id}-applications-13-{width}");
                    }
                }
            }
            SkinService.ApplyControlSizing(false);

            // The classic wrap of the same thirteen, for comparison.
            Wear(SkinPackage.DefaultId);
            Layout(window, 1600, 1400);
            Assert.Equal(ColumnApps.Length, ApplicationChips(window).Length);
            Assert.IsType<WrapPanel>(window.FindControl<ItemsControl>("ApplicationRows")!.ItemsPanelRoot);
            Capture(window, "default-applications-13-1600");
        }
        finally
        {
            SkinService.ApplyControlSizing(false);
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    private static void ShowApps(MainViewModel model, AppStreamViewModel[] all, int count)
    {
        model.ActiveApps.Clear();
        foreach (AppStreamViewModel app in all.Take(count)) model.ActiveApps.Add(app);
    }

    /// <summary>The chips in columns of six, in order, each as wide as in the wrap, whole columns side by side or below.</summary>
    private static void CheckColumnsAt(MainWindow window, MainViewModel model, Dictionary<AppStreamViewModel, double> wrapped, string where)
    {
        const int perColumn = 6;
        var rows = window.FindControl<ItemsControl>("ApplicationRows")!;
        ApplicationColumnsPanel panel = Assert.IsType<ApplicationColumnsPanel>(rows.ItemsPanelRoot);
        Assert.Equal(perColumn, panel.PerColumn);
        Border card = (Border)window.FindControl<Expander>("ApplicationsTile")!.Parent!;
        Assert.True(card.IsVisible, where);
        AppStreamViewModel[] order = [.. model.ActiveApps];
        Border[] all = ApplicationChips(window);
        Assert.Equal(order.Length, all.Length);
        Assert.Empty(ApplicationLines(window));
        Border[] chips = [.. order.Select(app => all.Single(c => c.DataContext == app))];
        Point At(int i) => chips[i].TranslatePoint(default, panel)!.Value;
        double Right(int i) => At(i).X + chips[i].Bounds.Width;
        // What a chip takes in its column: its width and its margin, which spaces the columns.
        double Taken(int i) => chips[i].Margin.Left + chips[i].Bounds.Width + chips[i].Margin.Right;
        double Bottom(int i) => At(i).Y + chips[i].Bounds.Height;

        AssertNoOverlap(chips);
        for (int i = 0; i < chips.Length; i++)
        {
            string chip = $"{order[i].Label} ({i}) {where}";
            Assert.True(Math.Abs(wrapped[order[i]] - chips[i].Bounds.Width) <= 0.5,
                $"{chip}: {chips[i].Bounds.Width:0.#} wide in a column, {wrapped[order[i]]:0.#} in the wrap");
            AssertInside(chips[i], card);
            if (i == 0)
            {
                Assert.Equal(0, At(0).X, 0.5);
                Assert.Equal(0, At(0).Y, 0.5);
            }
            else if (i % perColumn != 0)
            {
                // Down the column, under the one before.
                Assert.True(Math.Abs(At(i).X - At(i - 1).X) <= 0.5, $"{chip}: at x {At(i).X:0.#}, the chip above at {At(i - 1).X:0.#}");
                Assert.True(At(i).Y >= Bottom(i - 1) - 0.5, $"{chip}: at y {At(i).Y:0.#}, the chip above ends at {Bottom(i - 1):0.#}");
            }
            else
            {
                // A new column: to the right of the last one, level with its top, or a whole row of columns below.
                int top = i - perColumn;
                double columnRight = Enumerable.Range(top, perColumn).Max(Right);
                bool beside = Math.Abs(At(i).Y - At(top).Y) <= 0.5 && At(i).X >= columnRight - 0.5;
                bool below = Math.Abs(At(i).X) <= 0.5 && At(i).Y >= Enumerable.Range(0, i).Max(Bottom) - 0.5;
                Assert.True(beside || below, $"{chip}: at {At(i)}, the column before starts at {At(top)} and ends at x {columnRight:0.#}");
                // It goes below only when it would not fit beside.
                double end = Enumerable.Range(top, perColumn).Max(j => At(j).X - chips[j].Margin.Left + Taken(j));
                double columnWidth = Enumerable.Range(i, Math.Min(perColumn, chips.Length - i)).Max(Taken);
                if (below) Assert.True(end + columnWidth > panel.Bounds.Width + 0.5, $"{chip} went below with room beside");
            }
        }
        // Two or three columns of these chips fit side by side from 1600 pixels.
        if (window.Bounds.Width >= 1600)
            Assert.All(Enumerable.Range(0, chips.Length).Where(i => i % perColumn == 0), i => Assert.Equal(0, At(i).Y, 0.5));
        // Nothing scrolls sideways for the applications.
        var scroll = window.FindControl<ScrollViewer>("SectionScroll")!;
        Assert.True(panel.Bounds.Width <= card.Bounds.Width, where);
        if (scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled)
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1, $"the page is {scroll.Extent.Width} wide in {scroll.Viewport.Width} {where}");
        ApplicationIconWindowTests.Check(window, where);
    }
}
