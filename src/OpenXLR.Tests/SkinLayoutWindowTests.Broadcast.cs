using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using OpenXLR.UI.Views;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// The shipped Broadcast skin on a real window, the way a user meets it: the
/// header across the top, MONITOR's toolbar at the top right under it, the
/// submixer as a mix matrix across the window, a row per channel and a
/// column per mix, INPUTS hidden until Arrange mode shows it, and
/// APPLICATIONS and HEADPHONES folded under the matrix. At 640, 1040 and 2560
/// pixels, in Standard and Touch sizing, with six mixes, an interface
/// without XLR 2, a capture channel that is not connected and a 40 character
/// name, nothing is clipped or overlaps and every channel has a cell in
/// every mix. Then the compact and mini views, the matrix in its own window,
/// and the way back to Material with every object kept.
/// </summary>
internal static partial class SkinLayoutWindowTests
{
    private static readonly string[] BroadcastMixes = ["monitor", "monitor2", "stream", "chat", "auxout", "music-bed"];

    private const string LongName = "Stream soundboard with stingers and beds";

    private static void CheckBroadcast()
    {
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        bool touch = SkinService.TouchControls;
        try
        {
            Assert.Equal(40, LongName.Length);
            var model = (MainViewModel)window.DataContext!;
            string mixer = """
                {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},{"id":"monitor2","name":"Monitor B","kind":"monitor"},
                          {"id":"stream","name":"Stream","kind":"virtualMic"},{"id":"chat","name":"Chat","kind":"virtualMic"},
                          {"id":"auxout","name":"Aux","kind":"auxPort"},{"id":"music-bed","name":"Music bed","kind":"virtualMic"}],
                 "channels":[{"id":"xlr1","name":"XLR 1","hardware":true,"levels":{"monitor":0.8,"stream":0.6}},
                             {"id":"xlr2","name":"XLR 2","hardware":true,"present":false},
                             {"id":"aux","name":"Aux In","hardware":true},
                             {"id":"music","name":"Music","levels":{"monitor":0.5,"stream":0.4},"mutedIn":["stream"],"appearance":{"icon":"♫"}},
                             {"id":"board","name":"LONGNAME","levels":{"monitor":1,"stream":1}},
                             {"id":"game","name":"Game","levels":{"monitor":1,"stream":1}},
                             {"id":"cam","name":"Capture card","captureSource":"alsa_input.usb-capture","captureConnected":false}],
                 "monitorOutputs":["speakers"],"outputVolume":0.6,"inserts":{}}
                """.Replace("LONGNAME", LongName);
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse(mixer)]);
            typeof(MainViewModel).GetMethod("ApplyDevices", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse("""
                [{"kind":0,"name":"speakers","description":"Desk speakers with a long description from the driver"},
                 {"kind":0,"name":"headset","description":"USB headset"}]
                """), JsonNode.Parse(mixer)]);
            ChannelChoice[] choices = [new("music", "Music"), new("game", "Game"), new("board", LongName)];
            foreach (string name in new[] { "Google Chrome with a very long window title", "Discord" })
            {
                var app = new AppStreamViewModel(new DaemonClient("ws://127.0.0.1:1/ws"), name, name, choices);
                app.ApplyFromDaemon("music", true, true);
                model.Apps.Add(app);
                model.ActiveApps.Add(app);
            }
            // A Wave XLR Pro's view of the inputs: one XLR input, so XLR 2 is
            // not there, and the daemon says so of its channel too.
            model.HasXlr2 = false;
            // Levels under the faders, so the screenshots show the meter as the faders' track.
            double[] levels = [0.82, 0, 0.4, 0.62, 0.95, 0.55, 0];
            for (int i = 0; i < model.Channels.Count; i++)
            {
                model.Channels[i].MeterL = levels[i];
                model.Channels[i].MeterR = levels[i] * 0.9;
            }
            foreach (MixViewModel mix in model.Mixes) { mix.MeterL = 0.6; mix.MeterR = 0.58; }
            MethodInfo apply = typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            ChannelViewModel[] channels = [.. model.Channels];
            MixViewModel[] mixes = [.. model.Mixes];
            SendViewModel[] sends = [.. model.Channels.SelectMany(c => c.Sends)];
            window.Show();
            Layout(window, 1040, 900);

            Expander headphones = window.FindControl<Expander>("HeadphonesTile")!;
            Expander applications = window.FindControl<Expander>("ApplicationsTile")!;
            Assert.True(headphones.IsExpanded);
            Expander[] expanders = [.. Usual.Select(name => window.FindControl<Expander>(name)!)];
            Border[] cards = [.. expanders.Select(e => (Border)e.Parent!)];
            object?[] contents = [.. expanders.Select(e => e.Content)];
            Border Card(string tile) => (Border)window.FindControl<Expander>(tile)!.Parent!;

            Wear("broadcast");
            Layout(window, 1040, 900);
            Assert.Equal("broadcast", SkinService.Current.Id);
            // HEADPHONES and APPLICATIONS are folded the first time, and only then.
            Assert.False(headphones.IsExpanded);
            Assert.False(applications.IsExpanded);
            Assert.True(UiSettings.Load().HasSeenSkinLayout("broadcast", 1));

            // The header stays above the tree, across the window.
            var header = window.FindControl<Border>("HeaderCard")!;
            var content = window.FindControl<StackPanel>("MixerContent")!;
            Assert.Same(content, header.Parent);
            StackPanel desk = Assert.IsType<StackPanel>(window.LayoutHost.PanelFor("desk"));
            Grid grid = Assert.IsType<Grid>(window.LayoutHost.PanelFor("mixes"));
            var scroll = window.FindControl<ScrollViewer>("SectionScroll")!;
            Assert.Same(desk, scroll.Content);
            Assert.Same(desk, grid.Parent);
            Assert.Same(grid, Card("MonitorTile").Parent);
            Assert.Same(grid, Card("SubmixerTile").Parent);
            Assert.Same(desk, Card("HeadphonesTile").Parent);
            Assert.Equal(["ProfilesButton", "FlowButton", "ArrangeButton", "HeaderVersion", "HeaderOverflow"],
                window.FindControl<WrapPanel>("HeaderActions")!.Children.Select(c => c.Name));
            Assert.Equal(["RestartDaemonButton", "AboutButton"],
                window.FindControl<StackPanel>("HeaderOverflowItems")!.Children.Select(c => c.Name));

            // The matrix, not banks and no rack: INPUTS is hidden, the row heads carry its controls.
            Assert.False(Card("InputsTile").IsVisible);
            Assert.Null(window.MixBanks);
            Assert.Null(window.InputRackModel);
            MixMatrixViewModel matrix = Assert.IsType<MixMatrixViewModel>(window.MixMatrix);
            Assert.Equal(SkinLayoutTemplates.OutputToolbar, window.MonitorTemplate);
            Assert.Equal(BroadcastMixes, matrix.Columns.Select(c => c.Mix.Id));
            Assert.Equal(["xlr1", "aux", "music", "board", "game", "cam"], matrix.Rows.Select(r => r.Channel.Id));
            Assert.True(matrix.OwnsHandles);
            Assert.Equal(Localizer.Text("MatrixOneOutput"), matrix.Columns[0].OutputsText);
            Assert.Equal(Localizer.Text("MatrixNoOutput"), matrix.Columns[1].OutputsText);
            Assert.Equal(Localizer.Text("VirtualMicrophone"), matrix.Columns[2].OutputsText);

            headphones.IsExpanded = true;
            applications.IsExpanded = true;
            foreach (bool enlarged in new[] { false, true })
            {
                SkinService.ApplyControlSizing(enlarged);
                foreach (double width in new[] { 640d, 1040, 2560 })
                {
                    Layout(window, width, 1400);
                    CheckBroadcastAt(window, model, $"{(enlarged ? "touch" : "standard")} at {width}");
                    if (!enlarged) Capture(window, $"skin-broadcast-{width}");
                }
            }
            SkinService.ApplyControlSizing(false);
            Layout(window, 1040, 1400);

            // A muted send's cell is tinted, its mute lettered in the muted colour; the others are not.
            Border muted = MatrixCell(window, "music", "stream");
            Assert.Contains("tinted", muted.Classes);
            Assert.Equal(((ISolidColorBrush)window.FindResource("Ox.Matrix.MutedBackground")!).Color, ((ISolidColorBrush)muted.Background!).Color);
            ToggleButton mute = muted.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Classes.Contains("matrixMute"));
            Assert.True(mute.IsChecked);
            Assert.Equal(((ISolidColorBrush)window.FindResource("Ox.Matrix.MutedForeground")!).Color,
                ((ISolidColorBrush)mute.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First().Foreground!).Color);
            Assert.DoesNotContain("tinted", MatrixCell(window, "music", "monitor").Classes);
            mute.IsChecked = false;
            Assert.False(model.Channels.Single(c => c.Id == "music").Sends.Single(s => s.MixId == "stream").Muted);
            Layout(window, 1040, 1400);
            Assert.DoesNotContain("tinted", muted.Classes);
            mute.IsChecked = true;
            Layout(window, 1040, 1400);
            Assert.Contains("tinted", muted.Classes);

            // A capture channel that is not connected keeps its row, with its name and meter in its head.
            Border cam = RowHead(window, "cam");
            Assert.True(cam.IsEffectivelyVisible);
            Assert.Null(cam.GetVisualDescendants().OfType<Slider>().FirstOrDefault(s => s.IsEffectivelyVisible));
            Assert.Contains(cam.GetVisualDescendants().OfType<LevelMeter>(), m => m.IsEffectivelyVisible);
            // XLR 1's head has its gain over its meter, its mute and its chain.
            Border xlr1 = RowHead(window, "xlr1");
            Assert.Single(xlr1.GetVisualDescendants().OfType<Slider>(), s => s.IsEffectivelyVisible);
            Assert.True(xlr1.GetVisualDescendants().OfType<MeterFader>().Single().Meters.IsEffectivelyVisible);
            Assert.Single(xlr1.GetVisualDescendants().OfType<ToggleButton>(), t => t.IsEffectivelyVisible && t.Classes.Contains("matrixMute"));
            Assert.Single(xlr1.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Classes.Contains("matrixEffects"));
            // XLR 2 has no row, comes back when the interface has it, and goes again.
            Assert.DoesNotContain(matrix.Rows, r => r.Channel.Id == "xlr2");
            model.HasXlr2 = true;
            apply.Invoke(model, [JsonNode.Parse(mixer.Replace("\"present\":false", "\"present\":true"))]);
            Layout(window, 1040, 1400);
            Assert.Contains(matrix.Rows, r => r.Channel.Id == "xlr2");
            CheckBroadcastAt(window, model, "with XLR 2");
            model.HasXlr2 = false;
            apply.Invoke(model, [JsonNode.Parse(mixer)]);
            Layout(window, 1040, 1400);
            Assert.DoesNotContain(matrix.Rows, r => r.Channel.Id == "xlr2");

            // INPUTS comes back from Arrange mode, under the matrix, and Reset hides it again.
            var hidden = window.FindControl<WrapPanel>("HiddenSections")!;
            Arrange(window, true);
            Button show = Assert.Single(hidden.Children.OfType<Button>());
            Assert.Equal(Localizer.Format("ShowSection", Localizer.Text("INPUTS")), show.Content);
            // In Arrange mode a channel's handle is on its row head, a mix's on its column head, once each.
            Button[] handles = [.. window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("reorderHandle") && b.IsEffectivelyVisible)];
            Assert.Equal(["music", "board", "game", "cam"],
                handles.Select(h => h.Tag).OfType<ChannelViewModel>().Select(c => c.Id));
            Assert.Equal(["stream", "chat", "music-bed"], handles.Select(h => h.Tag).OfType<MixViewModel>().Select(m => m.Id));
            show.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(window, 1040, 1400);
            Assert.True(Card("InputsTile").IsVisible);
            Assert.True(window.FindControl<StackPanel>("HardwarePanel")!.IsEffectivelyVisible);
            Assert.True(Card("InputsTile").TranslatePoint(default, desk)!.Value.Y
                >= Card("SubmixerTile").TranslatePoint(default, desk)!.Value.Y + Card("SubmixerTile").Bounds.Height - 0.5);
            AssertNoOverlap([.. cards.Where(c => c.IsVisible), header]);
            window.FindControl<Button>("ResetSectionsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(window, 1040, 1400);
            Assert.False(Card("InputsTile").IsVisible);
            Arrange(window, false);

            // The compact view: the chosen channel alone, in every mix.
            model.SelectedCompactChannel = model.Channels.Single(c => c.Id == "board");
            Assert.Null(model.ChooseMixerView(MainViewModel.CompactView));
            foreach (double width in new[] { 640d, 1040 })
            {
                Layout(window, width, 1200);
                string where = $"compact at {width}";
                Assert.Equal(["board"], matrix.Rows.Select(r => r.Channel.Id));
                Assert.Equal(BroadcastMixes, matrix.Columns.Select(c => c.Mix.Id));
                CheckMatrixCells(window, matrix, where);
                Border[] shown = [.. cards.Where(c => c.IsVisible)];
                AssertNoOverlap([.. shown, header]);
                foreach (Border card in shown) AssertControlsInside(card, scroll);
                Assert.True(Card("SubmixerTile").IsVisible, where);
            }
            Capture(window, "skin-broadcast-compact-1040");

            // The mini view: one channel in one mix, the other sections set aside.
            model.SelectedCompactMix = model.Mixes.Single(m => m.Id == "stream");
            Assert.Null(model.ChooseMixerView(MainViewModel.MiniView));
            foreach (double width in new[] { 640d, 1040 })
            {
                Layout(window, width, 1000);
                string where = $"mini at {width}";
                Assert.False(Card("InputsTile").IsVisible, where);
                Assert.False(Card("HeadphonesTile").IsVisible, where);
                Assert.False(Card("ApplicationsTile").IsVisible, where);
                Assert.Equal(["board"], matrix.Rows.Select(r => r.Channel.Id));
                Assert.Equal(["stream"], matrix.Columns.Select(c => c.Mix.Id));
                CheckMatrixCells(window, matrix, where);
                Border[] shown = [.. cards.Where(c => c.IsVisible)];
                AssertNoOverlap([.. shown, header]);
                foreach (Border card in shown) AssertControlsInside(card, scroll);
            }
            Capture(window, "skin-broadcast-mini-1040");
            Assert.Null(model.ChooseMixerView(MainViewModel.FullView));
            Layout(window, 1040, 1200);
            Assert.Equal(BroadcastMixes, matrix.Columns.Select(c => c.Mix.Id));
            Assert.Same(matrix, window.MixMatrix);

            // Detached: the matrix goes with the panel into the mixer's window and comes back.
            MixerPopOutWindowTests.CheckMatrix(window, model);

            // To Material and back: the same sections, channels, mixes and sends throughout.
            Wear(SkinPackage.DefaultId);
            Layout(window, 1040, 900);
            Assert.Null(window.MixMatrix);
            Assert.True(Card("InputsTile").IsVisible);
            Wear("broadcast");
            Layout(window, 1040, 900);
            Assert.NotNull(window.MixMatrix);
            // Not folded again on a second wearing.
            Assert.True(headphones.IsExpanded);
            Assert.True(applications.IsExpanded);
            Assert.Equal(expanders, Usual.Select(name => window.FindControl<Expander>(name)!));
            Assert.Equal(cards, expanders.Select(e => (Border)e.Parent!));
            Assert.Equal(contents, expanders.Select(e => e.Content));
            Assert.Equal(channels, model.Channels);
            Assert.Equal(mixes, model.Mixes);
            Assert.Equal(sends, model.Channels.SelectMany(c => c.Sends));
            foreach (MatrixRowViewModel row in window.MixMatrix!.Rows)
                foreach (MatrixCellViewModel cell in row.Cells)
                    Assert.Contains(cell.Send!, sends);
            Wear(SkinPackage.DefaultId);
            Layout(window, 1040, 900);
        }
        finally
        {
            SkinService.ApplyControlSizing(touch);
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// One size of the Broadcast window: the toolbar at the top right, the
    /// matrix across the window under it, the folded sections under the
    /// matrix, nothing overlapping or clipped, and every cell of the matrix
    /// in its place.
    /// </summary>
    private static void CheckBroadcastAt(MainWindow window, MainViewModel model, string where)
    {
        Border Card(string tile) => (Border)window.FindControl<Expander>(tile)!.Parent!;
        var desk = (StackPanel)window.LayoutHost.PanelFor("desk")!;
        Point At(string tile) => Card(tile).TranslatePoint(default, desk)!.Value;
        double Right(string tile) => At(tile).X + Card(tile).Bounds.Width;
        double Bottom(string tile) => At(tile).Y + Card(tile).Bounds.Height;
        var header = window.FindControl<Border>("HeaderCard")!;
        var scroll = window.FindControl<ScrollViewer>("SectionScroll")!;
        string[] shownTiles = ["MonitorTile", "SubmixerTile", "ApplicationsTile", "HeadphonesTile"];
        Border[] cards = [.. shownTiles.Select(Card)];
        Assert.All(cards, card => Assert.True(card.IsVisible, $"{((Expander)card.Child!).Name} is hidden {where}"));
        Assert.False(Card("InputsTile").IsVisible, where);
        AssertNoOverlap([.. cards, header]);
        foreach (Border card in cards) AssertControlsInside(card, scroll);
        AssertInside(window.FindControl<Grid>("WindowHeader")!, window);

        // The toolbar at the top right, the matrix under it across the whole width.
        Assert.Equal(0, At("MonitorTile").Y, 0.5);
        Assert.Equal(desk.Bounds.Width, Right("MonitorTile"), 0.5);
        Assert.True(At("MonitorTile").X > desk.Bounds.Width / 3, $"the toolbar starts at {At("MonitorTile").X} {where}");
        Assert.Equal(0, At("SubmixerTile").X, 0.5);
        Assert.Equal(desk.Bounds.Width, Card("SubmixerTile").Bounds.Width, 0.5);
        Assert.True(At("SubmixerTile").Y >= Bottom("MonitorTile") - 0.5, where);
        // APPLICATIONS and HEADPHONES under the matrix, across the whole width.
        foreach (string tile in new[] { "ApplicationsTile", "HeadphonesTile" })
        {
            Assert.Equal(0, At(tile).X, 0.5);
            Assert.Equal(desk.Bounds.Width, Card(tile).Bounds.Width, 0.5);
        }
        Assert.True(At("ApplicationsTile").Y >= Bottom("SubmixerTile") - 0.5, where);
        Assert.True(At("HeadphonesTile").Y >= Bottom("ApplicationsTile") - 0.5, where);

        // The toolbar's controls inside it, the picker above the volume in its narrow column.
        var picker = window.FindControl<DropDownButton>("MonitorDestination")!;
        var volume = window.FindControl<Slider>("OutputVolumeSlider")!;
        foreach (Control part in new Control[] { picker, volume }) AssertInside(part, Card("MonitorTile"));
        AssertNoOverlap([picker, volume]);

        MixMatrixViewModel matrix = window.MixMatrix!;
        Assert.Equal(model.HasXlr2 ? 7 : 6, matrix.Rows.Count);
        Assert.Equal(BroadcastMixes.Length, matrix.Columns.Count);
        CheckMatrixCells(window, matrix, where);

        // The 40 character name shortens inside its row head rather than running out of it.
        Border board = RowHead(window, "board");
        TextBlock name = board.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == LongName);
        AssertInside(name, name.GetVisualAncestors().OfType<Border>().First(b => b.Name == "MatrixRowName"));

        // The application list: a line per application inside the list's section.
        Border[] lines = ApplicationLines(window);
        Assert.Equal(model.ActiveApps.Count, lines.Length);
        foreach (Border line in lines) AssertInside(line, Card("ApplicationsTile"));

        // The tree fits the window; a matrix wider than its card scrolls inside the card.
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1, $"the tree is {scroll.Extent.Width} wide in {scroll.Viewport.Width} {where}");
    }

    /// <summary>
    /// The matrix as drawn: a column head per column and a row of a head and
    /// a cell per column for each row, every cell the token's size, the heads
    /// over their cells, the parts of each cell and row head apart and inside
    /// it, and a matrix wider than its card scrolling sideways to its last
    /// column.
    /// </summary>
    internal static void CheckMatrixCells(Window window, MixMatrixViewModel matrix, string where)
    {
        MixMatrixView view = window.GetVisualDescendants().OfType<MixMatrixView>().Single(v => v.IsEffectivelyVisible);
        ScrollViewer scroller = view.Scroller;
        double cellWidth = (double)window.FindResource("Ox.Matrix.CellWidth")!;
        double rowHeight = (double)window.FindResource("Ox.Matrix.RowHeight")!;
        double nameWidth = (double)window.FindResource("Ox.Matrix.NameWidth")!;
        Border[] heads = [.. view.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "MatrixColumn" && b.IsEffectivelyVisible)];
        Assert.Equal(matrix.Columns.Select(c => c.Mix), heads.Select(h => ((MatrixColumnViewModel)h.DataContext!).Mix));
        StackPanel[] rows = [.. view.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Name == "MatrixRow" && p.IsEffectivelyVisible)];
        Assert.Equal(matrix.Rows.Select(r => r.Channel), rows.Select(r => ((MatrixRowViewModel)r.DataContext!).Channel));
        AssertNoOverlap(heads);
        foreach (Border head in heads)
        {
            Assert.Equal(cellWidth, head.Bounds.Width, 0.5);
            foreach (Control inner in head.GetVisualDescendants().OfType<Control>()
                         .Where(c => c is TextBlock or Slider or ToggleButton or Button && c.IsEffectivelyVisible && c.Bounds.Width > 0))
                AssertInside(inner, head);
        }
        foreach (StackPanel row in rows)
        {
            var model = (MatrixRowViewModel)row.DataContext!;
            Border head = row.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MatrixRowHead");
            Assert.Equal(nameWidth + cellWidth, head.Bounds.Width, 0.5);
            Assert.Equal(rowHeight, head.Bounds.Height, 0.5);
            Border source = head.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MatrixSource");
            AssertPartsApart(source);
            Border[] cells = [.. row.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "MatrixCell" && b.IsEffectivelyVisible)];
            Assert.Equal(matrix.Columns.Count, cells.Length);
            AssertNoOverlap([head, .. cells]);
            for (int i = 0; i < cells.Length; i++)
            {
                var cell = (MatrixCellViewModel)cells[i].DataContext!;
                Assert.Same(model.Channel, cell.Channel);
                Assert.Same(matrix.Columns[i].Mix, cell.Mix);
                Assert.Same(cell.Channel.Sends.SingleOrDefault(s => s.MixId == cell.Mix.Id), cell.Send);
                Assert.Equal(cellWidth, cells[i].Bounds.Width, 0.5);
                Assert.Equal(rowHeight, cells[i].Bounds.Height, 0.5);
                // A column's head stands over its cells.
                Assert.Equal(heads[i].TranslatePoint(default, view)!.Value.X, cells[i].TranslatePoint(default, view)!.Value.X, 0.5);
                if (cell.IsEmpty) continue;
                AssertPartsApart(cells[i]);
                Slider fader = cells[i].GetVisualDescendants().OfType<Slider>().Single();
                Assert.Equal(Orientation.Horizontal, fader.Orientation);
                Assert.Equal(cell.SendName, Avalonia.Automation.AutomationProperties.GetName(fader));
                MeterFader track = cells[i].GetVisualDescendants().OfType<MeterFader>().Single();
                Assert.Equal(matrix.MeterUnderFader, track.Meters.IsEffectivelyVisible);
                Assert.All(track.Meters.Children.OfType<LevelMeter>(), m =>
                    Assert.Contains(cell.Channel.Name, Avalonia.Automation.AutomationProperties.GetName(m), StringComparison.Ordinal));
            }
        }
        if (scroller.Extent.Width > scroller.Viewport.Width + 1)
        {
            Assert.Equal(ScrollBarVisibility.Auto, scroller.HorizontalScrollBarVisibility);
            scroller.Offset = new Vector(scroller.Extent.Width - scroller.Viewport.Width, 0);
            Layout(window, window.ClientSize.Width, window.ClientSize.Height);
            Border last = heads[^1];
            double right = last.TranslatePoint(new Point(last.Bounds.Width, 0), scroller)!.Value.X;
            Assert.InRange(right, 0, scroller.Viewport.Width + 0.5);
            scroller.Offset = default;
            Layout(window, window.ClientSize.Width, window.ClientSize.Height);
        }
    }

    /// <summary>A channel's row head in the matrix.</summary>
    internal static Border RowHead(Visual root, string channel) =>
        root.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Name == "MatrixRowHead" && b.DataContext is MatrixRowViewModel r && r.Channel.Id == channel);

    /// <summary>The cell where a channel's row crosses a mix's column.</summary>
    internal static Border MatrixCell(Visual root, string channel, string mix) =>
        root.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Name == "MatrixCell" && b.DataContext is MatrixCellViewModel c && c.Channel.Id == channel && c.Mix.Id == mix);
}
