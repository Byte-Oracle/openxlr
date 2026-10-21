using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// The classic channel tiles keep their send rows level: every tile has a
/// Volume row, XLR 1, XLR 2 and Aux In included, so no tile holds an empty
/// space; against a daemon that gave volumes to application and capture
/// channels only, while any shown tile has a Volume row, a tile without one
/// leaves an empty space exactly that tall, so Monitor A sits at one height
/// across the panel. Each tile's icon stands before its name on one line.
/// Run from the window layout and skin tests, on their UI
/// thread, with preferences of its own.
/// </summary>
internal static class ChannelTileAlignmentWindowTests
{
    private const string Mixes = """
        "mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},{"id":"monitor2","name":"Monitor B","kind":"monitor"},
                 {"id":"stream","name":"Stream","kind":"virtualMic"},{"id":"chat","name":"Chat","kind":"virtualMic"},
                 {"id":"auxout","name":"Aux","kind":"auxPort"}]
        """;

    /// <summary>
    /// Three hardware strips, two applications and a capture channel, each with
    /// the icon its kind gets, as a current daemon reports them: every channel
    /// with a volume, or, with <paramref name="hardwareVolume"/> false, as a
    /// daemon that gave volumes to application and capture channels only. With
    /// <paramref name="awkwardNames"/>, Music has no icon and the capture
    /// channel a name longer than its tile.
    /// </summary>
    private static string Fixture(bool hideApps = false, string mixes = Mixes, bool hardwareVolume = true, bool awkwardNames = false)
    {
        string hidden = hideApps ? ",\"hidden\":true" : "";
        string hw = hardwareVolume ? "\"volume\":0.9," : "";
        string musicIcon = awkwardNames ? "\"icon\":\"none\"," : "";
        string camera = awkwardNames ? LongName : "Camera";
        return "{" + mixes + $$$"""
            ,"channels":[{"id":"xlr1","name":"XLR 1","hardware":true,{{{hw}}}"appearance":{"defaultIcon":"mic"},
                          "levels":{"monitor":1,"monitor2":1,"stream":1,"chat":1,"auxout":1}},
                         {"id":"xlr2","name":"XLR 2","hardware":true,{{{hw}}}"appearance":{"defaultIcon":"mic"}},
                         {"id":"aux","name":"Aux In","hardware":true,{{{hw}}}"appearance":{"defaultIcon":"jack"}},
                         {"id":"game","name":"Game","volume":1,"levels":{"monitor":1,"stream":1},"appearance":{"defaultIcon":"game"{{{hidden}}}}},
                         {"id":"music","name":"Music","volume":0.8,"levels":{"monitor":0.8},"appearance":{{{{musicIcon}}}"defaultIcon":"music"{{{hidden}}}}},
                         {"id":"cam","name":"{{{camera}}}","captureSource":"alsa_input.usb","captureConnected":true,"volume":0.5,
                          "appearance":{"defaultIcon":"microphone"{{{hidden}}}}}],
             "inserts":{}}
            """;
    }

    private const string LongName = "Camera microphone on the desk arm";

    /// <summary>The same channels from a daemon that reports no channel volume.</summary>
    private static readonly string OlderDaemon = Fixture(hardwareVolume: false)
        .Replace("\"volume\":1,", "").Replace("\"volume\":0.8,", "").Replace("\"volume\":0.5,", "");

    /// <summary>The same channels from a daemon that gave volumes to application and capture channels only.</summary>
    private static readonly string PreviousDaemon = Fixture(hardwareVolume: false);

    /// <summary>
    /// The default skin through every case: both control sizes at a narrow and
    /// a wide window, hidden channels and mixes, Show hidden, an older daemon,
    /// the compact view and the popped-out mixer.
    /// </summary>
    internal static void Check() => WithWindow((window, model) =>
    {
        // No heading between the SUBMIXER header and the tiles.
        Assert.Null(window.FindControl<TextBlock>("InputChannelHeading"));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "INPUT CHANNELS");
        var tiles = window.FindControl<ItemsControl>("ChannelTiles")!;
        var scroller = tiles.FindAncestorOfType<ScrollViewer>()!;
        var section = (DockPanel)scroller.Parent!;
        Assert.Equal(0, scroller.TranslatePoint(default, section)!.Value.Y, 0.5);
        var output = window.FindControl<TextBlock>("OutputMixHeading")!;
        Assert.True(output.IsEffectivelyVisible);
        Assert.True(output.TranslatePoint(default, window)!.Value.Y
            >= scroller.TranslatePoint(default, window)!.Value.Y + scroller.Bounds.Height);

        SweepSizes(window, "default");
        // Every tile has its Volume row, so none holds an empty space.
        AssertEveryTileHasItsVolume(window, "from a current daemon");

        // While the Wave XLR Pro's direct path carries XLR 1 to the jacks, a
        // mark beside its Volume label and the fader's tooltip say the volume
        // reaches the mixes only; the rows stay level.
        ChannelViewModel xlr1 = model.Channels.Single(c => c.Id == "xlr1");
        TextBlock note = Tile(window, "xlr1").GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ChannelVolumeNote");
        Slider xlr1Volume = Tile(window, "xlr1").GetVisualDescendants().OfType<Slider>().First(s => s.DataContext is ChannelViewModel);
        Assert.False(note.IsEffectivelyVisible);
        Assert.Equal(Localizer.Text("ChannelVolumeTip"), ToolTip.GetTip(xlr1Volume));
        StopConnecting(window);
        xlr1.DirectMonitored = true;
        foreach (bool touch in new[] { false, true })
        {
            SkinService.ApplyControlSizing(touch);
            foreach (double width in new[] { 640d, 1040 })
            {
                Layout(window, width, 900);
                string where = $"with the direct path, {(touch ? "touch" : "standard")} at {width}";
                Assert.Equal(6, AssertSendRowsLineUp(window, where).Length);
                note = Tile(window, "xlr1").GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ChannelVolumeNote");
                xlr1Volume = Tile(window, "xlr1").GetVisualDescendants().OfType<Slider>().First(s => s.DataContext is ChannelViewModel);
                Assert.True(note.IsEffectivelyVisible, where);
                AssertInside(note, Tile(window, "xlr1"));
                Assert.Equal(Localizer.Text("ChannelVolumeMixesOnly"), Avalonia.Automation.AutomationProperties.GetName(note));
                Assert.Equal(Localizer.Text("ChannelVolumeDirectTip"), ToolTip.GetTip(note));
                Assert.Equal(Localizer.Text("ChannelVolumeDirectTip"), ToolTip.GetTip(xlr1Volume));
                if (width == 1040) Capture(window, $"tiles-direct-{(touch ? "touch" : "standard")}-1040");
            }
        }
        SkinService.ApplyControlSizing(false);
        xlr1.DirectMonitored = false;
        Layout(window, 1040, 900);
        Assert.False(Tile(window, "xlr1").GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ChannelVolumeNote").IsEffectivelyVisible);

        // A daemon that gave volumes to application and capture channels only:
        // the hardware tiles keep an empty space as tall as the Volume row.
        Apply(model, PreviousDaemon);
        foreach (double width in new[] { 640d, 1040 })
        {
            Layout(window, width, 900);
            Assert.Equal(6, AssertSendRowsLineUp(window, $"against the previous daemon at {width}").Length);
        }
        Capture(window, "tiles-aligned-previous-1040");

        // From that daemon, every channel with a volume hidden: nothing is reserved above XLR 1's sends.
        Apply(model, Fixture(hideApps: true, hardwareVolume: false));
        Layout(window, 1040, 900);
        Assert.Equal(["xlr1", "xlr2", "aux"], AssertSendRowsLineUp(window, "with the volume channels hidden"));
        // Show hidden brings them back faded, Volume row and all, and the hardware tiles make room again.
        model.ShowHidden = true;
        Layout(window, 1040, 900);
        Assert.Equal(6, AssertSendRowsLineUp(window, "with hidden channels shown").Length);
        Assert.True(Tile(window, "game").Opacity < 1);
        Capture(window, "tiles-aligned-revealed-1040");
        model.ShowHidden = false;
        Layout(window, 1040, 900);
        Assert.Equal(3, AssertSendRowsLineUp(window, "with hidden channels folded again").Length);

        // Monitor A hidden: the first row of every tile is Monitor B, still level.
        Apply(model, Fixture(mixes: Mixes.Replace("\"name\":\"Monitor A\",\"kind\":\"monitor\"",
            "\"name\":\"Monitor A\",\"kind\":\"monitor\",\"appearance\":{\"hidden\":true}")));
        Layout(window, 1040, 900);
        Assert.Equal(6, AssertSendRowsLineUp(window, "with Monitor A hidden").Length);
        Assert.All(window.FindControl<ItemsControl>("ChannelTiles")!.GetVisualDescendants().OfType<StackPanel>()
            .Where(p => p.DataContext is SendViewModel && p.IsEffectivelyVisible), p => Assert.NotEqual("monitor", ((SendViewModel)p.DataContext!).MixId));

        // An older daemon reports no volume, so nothing is reserved anywhere.
        Apply(model, OlderDaemon);
        foreach (double width in new[] { 640d, 1040 })
        {
            Layout(window, width, 900);
            Assert.Equal(6, AssertSendRowsLineUp(window, $"against an older daemon at {width}").Length);
        }

        // The compact view shows one tile with its Volume row, XLR 1's too; from
        // the previous daemon a hardware one reserves nothing.
        Apply(model, PreviousDaemon);
        Assert.Null(model.ChooseMixerView(MainViewModel.CompactView));
        model.SelectedCompactChannel = model.Channels.Single(c => c.Id == "xlr1");
        Layout(window, 640, 900);
        Assert.Equal(["xlr1"], AssertSendRowsLineUp(window, "in the compact view on XLR 1 from the previous daemon"));
        Apply(model, Fixture());
        model.SelectedCompactChannel = model.Channels.Single(c => c.Id == "xlr1");
        Layout(window, 640, 900);
        Assert.Equal(["xlr1"], AssertSendRowsLineUp(window, "in the compact view on XLR 1"));
        AssertEveryTileHasItsVolume(window, "in the compact view on XLR 1");
        model.SelectedCompactChannel = model.Channels.Single(c => c.Id == "game");
        Layout(window, 640, 900);
        Assert.Equal(["game"], AssertSendRowsLineUp(window, "in the compact view on Game"));
        Assert.Null(model.ChooseMixerView(MainViewModel.FullView));
        Layout(window, 1040, 900);

        // The popped-out mixer carries the same tiles.
        MixerWindow popped = window.PopOutMixer();
        foreach (double width in new[] { 640d, 1040 })
        {
            Layout(popped, width, 700);
            Assert.Equal(6, AssertSendRowsLineUp(popped, $"in the popped-out mixer at {width}").Length);
            AssertEveryTileHasItsVolume(popped, $"in the popped-out mixer at {width}");
            AssertIconsShareTheNameLine(popped, $"in the popped-out mixer at {width}");
        }
        popped.Close();
        Dispatcher.UIThread.RunJobs();
    });

    /// <summary>Each skin that draws the classic tiles, at both control sizes and three widths.</summary>
    internal static void CheckSkins() => WithWindow((window, _) =>
    {
        string mode = SkinService.Mode;
        ThemeVariant? requested = Application.Current!.RequestedThemeVariant;
        try
        {
            Assert.Empty(SkinService.ApplyMode(AppearanceModes.Light, new SkinEntry(SkinPackage.Default, [])));
            SweepSizes(window, "material-light");
            Assert.Empty(SkinService.ApplyMode(AppearanceModes.Dark, new SkinEntry(SkinPackage.Default, [])));
            SweepSizes(window, "material");
        }
        finally
        {
            SkinService.ApplyMode(mode, new SkinEntry(SkinPackage.Default, []));
            Application.Current!.RequestedThemeVariant = requested;
        }
        foreach (SkinEntry entry in SkinCatalog.BuiltIn())
        {
            // A skin with an arrangement of its own is checked here by its palette on the classic tiles.
            SkinEntry skin = entry.Package.Layout is null
                ? entry
                : entry with { Package = entry.Package with { Id = entry.Id + "-palette", Layout = null } };
            Assert.Empty(SkinService.Apply(skin));
            SweepSizes(window, skin.Id);
        }
    });

    private static void SweepSizes(MainWindow window, string skin)
    {
        var model = (MainViewModel)window.DataContext!;
        foreach (bool touch in new[] { false, true })
        {
            SkinService.ApplyControlSizing(touch);
            foreach (double width in new[] { 640d, 1040, 1800 })
            {
                Layout(window, width, 900);
                string where = $"{skin}, {(touch ? "touch" : "standard")} controls, at {width}";
                Assert.Equal(6, AssertSendRowsLineUp(window, where).Length);
                AssertEveryTileHasItsVolume(window, where);
                AssertIconsShareTheNameLine(window, where);
                if (width == 1040) Capture(window, $"tiles-aligned-{skin}-{(touch ? "touch" : "standard")}-1040");
            }
            // A channel with no icon and one whose name is longer than its tile.
            Apply(model, Fixture(awkwardNames: true));
            Layout(window, 640, 900);
            string awkward = $"{skin}, {(touch ? "touch" : "standard")} controls, without an icon and with a long name";
            AssertIconsShareTheNameLine(window, awkward);
            Assert.False(Tile(window, "music").GetVisualDescendants().OfType<LayoutIconBlock>().Single().IsEffectivelyVisible, awkward);
            Assert.True(IsCutShort(Tile(window, "cam").GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ChannelName")), awkward);
            Capture(window, $"tiles-names-{skin}-{(touch ? "touch" : "standard")}-640");
            Apply(model, Fixture());
        }
        SkinService.ApplyControlSizing(false);
    }

    /// <summary>
    /// Every shown tile opens with one line that holds its icon and its name:
    /// the icon at the tile's left edge and the name after it, the two centred
    /// on each other, and the meters straight under the line, so the icon has
    /// no row of its own. A tile without an icon starts its name at the edge.
    /// The name stays one line inside the tile, cut short when it is longer.
    /// </summary>
    internal static void AssertIconsShareTheNameLine(TopLevel root, string where)
    {
        Border[] tiles = [.. root.GetVisualDescendants().OfType<Border>()
            .Where(b => b.DataContext is ChannelViewModel && b.Classes.Contains("tile") && b.IsEffectivelyVisible)];
        Assert.NotEmpty(tiles);
        foreach (Border tile in tiles)
        {
            var channel = (ChannelViewModel)tile.DataContext!;
            string what = $"{channel.Name} {where}";
            Grid line = tile.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "ChannelNameLine");
            LayoutIconBlock icon = line.Children.OfType<LayoutIconBlock>().Single();
            TextBlock name = line.Children.OfType<TextBlock>().Single(t => t.Name == "ChannelName");
            // The tile's content box: inside its border and its padding.
            double left = tile.BorderThickness.Left + tile.Padding.Left;
            double right = tile.Bounds.Width - tile.BorderThickness.Right - tile.Padding.Right;
            Rect lineBox = Box(line, tile), nameBox = Box(name, tile);

            // The line is the tile's first row and the meters follow it directly.
            Assert.Equal(tile.BorderThickness.Top + tile.Padding.Top, lineBox.Top, 0.5);
            var column = (StackPanel)line.Parent!;
            Control next = column.Children[column.Children.IndexOf(line) + 1];
            Assert.True(next is StackPanel && next.GetVisualDescendants().OfType<LevelMeter>().Any(), what);
            Assert.Equal(lineBox.Bottom + column.Spacing, Box(next, tile).Top, 0.5);

            Assert.True(nameBox.Left >= left - 0.5 && nameBox.Right <= right + 0.5,
                $"{what}: the name spans {nameBox.Left:0.#} to {nameBox.Right:0.#}, outside {left:0.#} to {right:0.#}.");
            Assert.Single(name.TextLayout.TextLines);
            Assert.True(name.TextLayout.WidthIncludingTrailingWhitespace <= nameBox.Width + 0.5,
                $"{what}: the name draws {name.TextLayout.WidthIncludingTrailingWhitespace:0.#} wide in {nameBox.Width:0.#}.");

            Assert.Equal(channel.Appearance.HasIcon, icon.IsEffectivelyVisible);
            if (icon.IsEffectivelyVisible)
            {
                Rect iconBox = Box(icon, tile);
                Assert.True(iconBox.Width > 4 && iconBox.Height > 4, what);
                Assert.Equal(left, iconBox.Left, 0.5);
                Assert.True(iconBox.Right <= nameBox.Left + 0.5,
                    $"{what}: the icon ends at {iconBox.Right:0.#}, past the name's start at {nameBox.Left:0.#}.");
                Assert.True(Math.Abs(iconBox.Center.Y - nameBox.Center.Y) <= 0.5,
                    $"{what}: the icon is centred at {iconBox.Center.Y:0.#}, the name at {nameBox.Center.Y:0.#}.");
                Assert.Equal(Math.Max(iconBox.Height, nameBox.Height), lineBox.Height, 0.5);
            }
            else
            {
                Assert.Equal(left, nameBox.Left, 0.5);
                Assert.Equal(nameBox.Height, lineBox.Height, 0.5);
            }
        }
    }

    /// <summary>Whether a one-line name is narrower than its whole text would be.</summary>
    private static bool IsCutShort(TextBlock name)
    {
        var whole = new TextBlock
        {
            Text = name.Text, FontFamily = name.FontFamily, FontSize = name.FontSize,
            FontWeight = name.FontWeight, FontStyle = name.FontStyle,
        };
        whole.Measure(Size.Infinity);
        return whole.DesiredSize.Width > name.Bounds.Width + 1;
    }

    private static Rect Box(Control control, Visual to) => new(control.TranslatePoint(default, to)!.Value, control.Bounds.Size);

    /// <summary>
    /// The first shown send row of every shown tile starts at one height. While
    /// any of those tiles has a Volume row, the rows sit exactly that row lower
    /// in every tile and the space in a tile without one holds nothing to click
    /// or focus; with none, nothing is reserved. Returns the tiles' channel ids.
    /// </summary>
    internal static string[] AssertSendRowsLineUp(TopLevel root, string where)
    {
        Border[] tiles = [.. root.GetVisualDescendants().OfType<Border>()
            .Where(b => b.DataContext is ChannelViewModel && b.Classes.Contains("tile") && b.IsEffectivelyVisible)];
        Assert.NotEmpty(tiles);
        StackPanel Volume(Border tile) => tile.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "ChannelVolume");
        Border? withVolume = tiles.FirstOrDefault(t => ((ChannelViewModel)t.DataContext!).HasVolume);
        double reserve = 0;
        if (withVolume is not null)
        {
            StackPanel volume = Volume(withVolume);
            Assert.True(volume.IsEffectivelyVisible, where);
            Assert.True(volume.Bounds.Height > 0, where);
            reserve = volume.Bounds.Height + volume.Margin.Bottom;
        }
        double? level = null;
        foreach (Border tile in tiles)
        {
            var channel = (ChannelViewModel)tile.DataContext!;
            string what = $"{channel.Name} {where}";
            Grid area = tile.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "ChannelSendArea");
            StackPanel first = area.GetVisualDescendants().OfType<StackPanel>()
                .Where(p => p.DataContext is SendViewModel && p.IsEffectivelyVisible)
                .OrderBy(p => p.TranslatePoint(default, area)!.Value.Y).First();
            Assert.Equal(reserve, first.TranslatePoint(default, area)!.Value.Y, 0.5);
            double y = first.TranslatePoint(default, root)!.Value.Y;
            level ??= y;
            Assert.True(Math.Abs(level.Value - y) < 0.5, $"{what}: the first send row starts at {y:0.#}, not {level:0.#}.");

            StackPanel volume = Volume(tile);
            Assert.Equal(channel.HasVolume, volume.IsEffectivelyVisible);
            if (channel.HasVolume)
            {
                // The Volume row itself stays where it was, right under the meters.
                var column = (StackPanel)area.Parent!;
                Control above = column.Children[column.Children.IndexOf(area) - 1];
                Assert.Equal(above.TranslatePoint(default, tile)!.Value.Y + above.Bounds.Height + column.Spacing,
                    volume.TranslatePoint(default, tile)!.Value.Y, 0.5);
            }
            else
            {
                Assert.DoesNotContain(area.GetVisualDescendants().OfType<Control>(),
                    c => c.IsEffectivelyVisible && c.Focusable && c.TranslatePoint(default, area)!.Value.Y < reserve - 0.5);
            }
        }
        return [.. tiles.Select(t => ((ChannelViewModel)t.DataContext!).Id)];
    }

    /// <summary>Every shown tile draws its Volume row, so no tile holds the empty space kept for a tile without one.</summary>
    private static void AssertEveryTileHasItsVolume(TopLevel root, string where)
    {
        Border[] tiles = [.. root.GetVisualDescendants().OfType<Border>()
            .Where(b => b.DataContext is ChannelViewModel && b.Classes.Contains("tile") && b.IsEffectivelyVisible)];
        Assert.NotEmpty(tiles);
        foreach (Border tile in tiles)
        {
            var channel = (ChannelViewModel)tile.DataContext!;
            Assert.True(channel.HasVolume, $"{channel.Name} {where}");
            StackPanel volume = tile.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "ChannelVolume");
            Assert.True(volume.IsEffectivelyVisible && volume.Bounds.Height > 0, $"{channel.Name} {where}");
            Slider fader = volume.GetVisualDescendants().OfType<Slider>().Single();
            Assert.Equal(channel.Volume, fader.Value, 3);
            Assert.Equal(Localizer.Format("ChannelVolumeName", channel.Name), Avalonia.Automation.AutomationProperties.GetName(fader));
        }
    }

    private static Border Tile(TopLevel root, string id) => root.GetVisualDescendants().OfType<Border>()
        .Single(b => b.DataContext is ChannelViewModel { Id: var c } && c == id && b.Classes.Contains("tile"));

    private static void Apply(MainViewModel model, string state)
        => typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(model, [JsonNode.Parse(state)]);

    private static void WithWindow(Action<MainWindow, MainViewModel> check)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-tile-alignment-").FullName;
        string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        SkinEntry skin = SkinService.Current;
        bool touch = SkinService.TouchControls;
        MainWindow? window = null;
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
            window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
            var model = (MainViewModel)window.DataContext!;
            Apply(model, Fixture());
            window.Show();
            Layout(window, 1040, 900);
            check(window, model);
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            SkinService.ApplyControlSizing(touch);
            SkinService.Apply(skin);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
            Directory.Delete(dir, true);
        }
    }
}
