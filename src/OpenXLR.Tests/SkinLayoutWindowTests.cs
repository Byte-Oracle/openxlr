using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// A skin's layout on a real window: the sections in the skin's order, in a
/// row that keeps every section whole and scrolls, the user's moves kept per
/// skin, Reset clearing only the skin in force, and Material bringing the
/// legacy order back. Run from the window layout test, on its UI thread, with
/// preferences and skins of its own.
/// </summary>
internal static class SkinLayoutWindowTests
{
    private static readonly string[] Usual = ["InputsTile", "HeadphonesTile", "MonitorTile", "ApplicationsTile", "SubmixerTile"];
    private static readonly string[] Legacy = ["HeadphonesTile", "SubmixerTile", "InputsTile", "MonitorTile", "ApplicationsTile"];
    private static readonly string[] ColumnOrder = ["SubmixerTile", "MonitorTile", "InputsTile", "HeadphonesTile", "ApplicationsTile"];
    private static readonly string[] RowOrder = ["MonitorTile", "InputsTile", "SubmixerTile", "HeadphonesTile", "ApplicationsTile"];

    internal static void Check()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-skin-layout-window-").FullName;
        string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        SkinEntry skin = SkinService.Current;
        MainWindow? window = null;
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(dir, "config"));
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(dir, "data"));
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(dir, "none"));
            Install(dir, "column-first", "vertical", 16, ColumnOrder);
            Install(dir, "row", "horizontal", 8, RowOrder);
            // The legacy order, and an arrangement made under another skin
            // that Reset in "row" must leave alone.
            Assert.Null(new UiSettings { CheckForUpdates = false, SectionOrder = Legacy }
                .WithSkinSectionOrder("column-first", 1, ["applications", "inputs"]).Save());

            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
            var model = (MainViewModel)window.DataContext!;
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse("""
                {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},{"id":"stream","name":"Stream","kind":"virtualMic"}],
                 "channels":[{"id":"music","name":"Music","levels":{"monitor":0.5,"stream":0.4}},
                             {"id":"game","name":"Game","levels":{"monitor":1,"stream":1}},
                             {"id":"chat","name":"Voice chat","levels":{"monitor":1,"stream":1}}]}
                """)]);
            window.Show();
            Layout(window, 1040, 900);
            MainWindow main = window;

            Expander[] expanders = [.. Usual.Select(name => main.FindControl<Expander>(name)!)];
            Border[] cards = [.. expanders.Select(e => (Border)e.Parent!)];
            object?[] contents = [.. expanders.Select(e => e.Content)];
            Assert.All(cards, card => Assert.True(card.IsVisible, $"{((Expander)card.Child!).Name} is hidden in the fixture"));
            Assert.Equal(Legacy, Order(main));

            // A vertical layout: the skin's order and gap, top to bottom.
            Wear("column-first");
            Layout(main, 1040, 900);
            // The arrangement saved under this skin goes over its own order.
            Assert.Equal(["ApplicationsTile", "InputsTile", "SubmixerTile", "MonitorTile", "HeadphonesTile"], Order(main));
            Assert.Equal(Order(main), OnScreen(main, byX: false));
            Assert.Equal(16, Cards(main).Spacing);
            Assert.Equal(Orientation.Vertical, Cards(main).Orientation);
            Assert.Equal("↕", SectionHandle(main, "InputsTile").Content);

            // A horizontal layout: one row, every section whole, scrolling.
            Wear("row");
            Layout(main, 1040, 900);
            Assert.Equal(RowOrder, Order(main));
            Assert.Equal(Orientation.Horizontal, Cards(main).Orientation);
            Assert.Equal(RowOrder, OnScreen(main, byX: true));
            CheckRow(main, cards);
            Assert.Equal("↔", SectionHandle(main, "InputsTile").Content);
            Capture(main, "skin-layout-row-1040");

            // A collapsed section in the row is its header, not the row's height.
            Expander monitor = main.FindControl<Expander>("MonitorTile")!;
            monitor.IsExpanded = false;
            Layout(main, 1040, 900);
            Border monitorCard = (Border)monitor.Parent!;
            Assert.True(monitorCard.Bounds.Height + 1 < cards.Max(c => c.Bounds.Height),
                $"a collapsed section is {monitorCard.Bounds.Height} tall in a row of {cards.Max(c => c.Bounds.Height)}");
            Assert.Equal(VerticalAlignment.Top, monitorCard.VerticalAlignment);
            monitor.IsExpanded = true;
            Layout(main, 1040, 900);

            // The keyboard moves a section along the row, and the move is
            // kept for this skin alone.
            Arrange(main, true);
            Button handle = SectionHandle(main, "MonitorTile");
            handle.Focus();
            handle.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right, Source = handle });
            Layout(main, 1040, 900);
            string[] moved = ["InputsTile", "MonitorTile", "SubmixerTile", "HeadphonesTile", "ApplicationsTile"];
            Assert.Equal(moved, Order(main));
            UiSettings saved = UiSettings.Load();
            Assert.Equal(Legacy, saved.SectionOrder);
            Assert.Equal(["inputs", "monitor", "submixer", "headphones", "applications"], saved.SkinSectionOrderFor("row", 1)!.Order);
            Assert.Equal(["applications", "inputs"], saved.SkinSectionOrderFor("column-first", 1)!.Order);
            Assert.Equal(Localizer.Text("PutTheSectionsBackInTheSkinsOrder"),
                ToolTip.GetTip(main.FindControl<Button>("ResetSectionsButton")!));

            // The move survives a switch away and back.
            Wear(SkinPackage.DefaultId);
            Wear("row");
            Layout(main, 1040, 900);
            Assert.Equal(moved, Order(main));

            // Reset clears only this skin's arrangement and returns to its order.
            main.FindControl<Button>("ResetSectionsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(main, 1040, 900);
            Assert.Equal(RowOrder, Order(main));
            saved = UiSettings.Load();
            Assert.Null(saved.SkinSectionOrderFor("row", 1));
            Assert.Equal(["applications", "inputs"], saved.SkinSectionOrderFor("column-first", 1)!.Order);
            Assert.Equal(Legacy, saved.SectionOrder);
            Arrange(main, false);

            // Material brings the legacy order back, in the column it always was.
            Wear(SkinPackage.DefaultId);
            Layout(main, 1040, 900);
            Assert.Equal(Legacy, Order(main));
            Assert.Equal(Legacy, OnScreen(main, byX: false));
            Assert.Equal(Orientation.Vertical, Cards(main).Orientation);
            Assert.Equal(SkinLayoutDefaults.Spacing, Cards(main).Spacing);
            Assert.Equal(ScrollBarVisibility.Disabled, main.FindControl<ScrollViewer>("SectionScroll")!.HorizontalScrollBarVisibility);
            Assert.All(cards, card => Assert.True(double.IsNaN(card.Width)));
            Assert.Equal(Localizer.Text("PutTheFiveSectionsBackInTheir"),
                ToolTip.GetTip(main.FindControl<Button>("ResetSectionsButton")!));
            var content = main.FindControl<StackPanel>("MixerContent")!;
            Assert.InRange(content.Bounds.Width, 1040 - 72, 1040 - 16);
            Assert.All(cards, card => AssertInside(card, main));

            // Nothing was rebuilt along the way.
            Assert.Equal(expanders, Usual.Select(name => main.FindControl<Expander>(name)!));
            Assert.Equal(cards, expanders.Select(e => (Border)e.Parent!));
            Assert.Equal(contents, expanders.Select(e => e.Content));
            Assert.All(expanders, e => Assert.Same(model, e.DataContext));
        }
        finally
        {
            window?.Close();
            SkinService.Apply(skin);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataHome);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", dataDirs);
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// The row: one line along the top, sections side by side and apart, each
    /// at least its minimum width with its controls inside it, the header at
    /// the window's width, and the row scrolling to its last section.
    /// </summary>
    private static void CheckRow(MainWindow main, Border[] cards)
    {
        var scroll = main.FindControl<ScrollViewer>("SectionScroll")!;
        double top = cards[0].TranslatePoint(default, main)!.Value.Y;
        foreach (Border card in cards)
        {
            Assert.InRange(card.TranslatePoint(default, main)!.Value.Y, top - 0.5, top + 0.5);
            Assert.True(card.Bounds.Width >= SkinLayoutDefaults.RowSectionMinWidth, $"a section is {card.Bounds.Width} wide");
            // Every control the section draws itself sits inside it; the
            // channel row has a scroller of its own and is checked by it.
            foreach (Control child in card.GetVisualDescendants().OfType<Control>()
                         .Where(c => c is TextBlock or Slider or Button or ComboBox && c.IsEffectivelyVisible && c.Bounds.Width > 0
                                     && ReferenceEquals(c.GetVisualAncestors().OfType<ScrollViewer>().First(), scroll)))
                AssertInside(child, card);
        }
        AssertNoOverlap(cards);

        var content = main.FindControl<StackPanel>("MixerContent")!;
        Assert.InRange(content.Bounds.Width, 1040 - 72, 1040 - 16);
        AssertInside(main.FindControl<Grid>("WindowHeader")!, main);

        Assert.Equal(ScrollBarVisibility.Auto, scroll.HorizontalScrollBarVisibility);
        Assert.True(scroll.Extent.Width > scroll.Viewport.Width + 1,
            $"the row is {scroll.Extent.Width} wide in a {scroll.Viewport.Width} viewport");
        scroll.Offset = new Vector(scroll.Extent.Width - scroll.Viewport.Width, 0);
        Layout(main, 1040, 900);
        Border last = cards.OrderBy(c => c.TranslatePoint(default, scroll)!.Value.X).Last();
        double right = last.TranslatePoint(default, scroll)!.Value.X + last.Bounds.Width;
        Assert.InRange(right, 0, scroll.Viewport.Width + 0.5);
        scroll.Offset = default;
        Layout(main, 1040, 900);
    }

    private static void Install(string dir, string id, string orientation, double spacing, string[] order)
    {
        string folder = Path.Combine(dir, "data", "openxlr", "skins", id);
        Directory.CreateDirectory(folder);
        string children = string.Join(",", order.Select(tile =>
            $"{{\"kind\":\"region\",\"region\":\"{SectionArrangement.RegionFor(tile)}\"}}"));
        File.WriteAllText(Path.Combine(folder, "skin.json"),
            $"{{\"schema\":1,\"name\":\"{id}\",\"tokens\":{{\"Ox.Card.Background\":\"#22262e\"}},"
            + $"\"layout\":{{\"version\":1,\"main\":{{\"kind\":\"stack\",\"orientation\":\"{orientation}\","
            + $"\"spacing\":{spacing},\"children\":[{children}]}}}}}}");
    }

    private static void Wear(string id)
    {
        SkinEntry entry = SkinCatalog.Find(id)!;
        Assert.Empty(entry.Errors);
        Assert.Empty(SkinService.Apply(entry));
    }

    private static void Arrange(MainWindow main, bool on)
    {
        var arrange = main.FindControl<ToggleButton>("ArrangeButton")!;
        arrange.IsChecked = on;
        arrange.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(main, 1040, 900);
    }

    private static StackPanel Cards(MainWindow main) => main.FindControl<StackPanel>("SectionCards")!;

    private static string[] Order(MainWindow main) =>
        [.. Cards(main).Children.OfType<Border>().Select(b => ((Expander)b.Child!).Name!)];

    /// <summary>The sections as they stand on screen, down the window or across it.</summary>
    private static string[] OnScreen(MainWindow main, bool byX) =>
        [.. Cards(main).Children.OfType<Border>()
            .OrderBy(b => byX ? b.TranslatePoint(default, main)!.Value.X : b.TranslatePoint(default, main)!.Value.Y)
            .Select(b => ((Expander)b.Child!).Name!)];

    private static Button SectionHandle(MainWindow main, string tile) =>
        ((Panel)main.FindControl<Expander>(tile)!.Header!).Children.OfType<Button>()
            .Single(b => b.Classes.Contains("reorderHandle"));
}
