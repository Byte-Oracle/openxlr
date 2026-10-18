using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Skinning;
using OpenXLR.UI.Views;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// The shipped Broadcast skin on a real window, the way a user meets it: the
/// header across the top, the input rack and the application list in a
/// column docked on the left as wide as the rack needs, two banks of upright strips on Monitor A
/// and Stream beside it with MONITOR's toolbar under them, and HEADPHONES
/// folded along the bottom. At 640, 1040 and 2560 pixels, in Standard and
/// Touch sizing, with six mixes, an interface without XLR 2, a capture
/// channel that is not connected and a 40 character name, nothing is
/// clipped or overlaps. Then the compact and mini views, the submixer in its
/// own window, and the way back to Material with every object kept.
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
                             {"id":"music","name":"Music","levels":{"monitor":0.5,"stream":0.4},"appearance":{"icon":"♫"}},
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
            MethodInfo apply = typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            ChannelViewModel[] channels = [.. model.Channels];
            MixViewModel[] mixes = [.. model.Mixes];
            SendViewModel[] sends = [.. model.Channels.SelectMany(c => c.Sends)];
            window.Show();
            Layout(window, 1040, 900);

            Expander headphones = window.FindControl<Expander>("HeadphonesTile")!;
            Assert.True(headphones.IsExpanded);
            Expander[] expanders = [.. Usual.Select(name => window.FindControl<Expander>(name)!)];
            Border[] cards = [.. expanders.Select(e => (Border)e.Parent!)];
            object?[] contents = [.. expanders.Select(e => e.Content)];
            Border Card(string tile) => (Border)window.FindControl<Expander>(tile)!.Parent!;

            Wear("broadcast");
            Layout(window, 1040, 900);
            Assert.Equal("broadcast", SkinService.Current.Id);
            // HEADPHONES is folded the first time, and only then.
            Assert.False(headphones.IsExpanded);
            Assert.True(UiSettings.Load().HasSeenSkinLayout("broadcast", 1));

            // The header stays above the tree, across the window.
            var header = window.FindControl<Border>("HeaderCard")!;
            var content = window.FindControl<StackPanel>("MixerContent")!;
            Assert.Same(content, header.Parent);
            StackPanel desk = Assert.IsType<StackPanel>(window.LayoutHost.PanelFor("desk"));
            DockPanel console = Assert.IsType<DockPanel>(window.LayoutHost.PanelFor("console"));
            var scroll = window.FindControl<ScrollViewer>("SectionScroll")!;
            Assert.Same(desk, scroll.Content);
            Assert.Same(desk, console.Parent);
            Assert.Same(desk, Card("HeadphonesTile").Parent);
            Assert.Equal(["ProfilesButton", "FlowButton", "ArrangeButton", "HeaderVersion", "HeaderOverflow"],
                window.FindControl<WrapPanel>("HeaderActions")!.Children.Select(c => c.Name));
            Assert.Equal(["RestartDaemonButton", "AboutButton"],
                window.FindControl<StackPanel>("HeaderOverflowItems")!.Children.Select(c => c.Name));

            var rack = window.FindControl<InputRackView>("InputRack")!;
            Assert.True(rack.IsVisible);
            MixerPresentationViewModel banks = Assert.IsType<MixerPresentationViewModel>(window.MixBanks);
            Assert.Equal(["monitor", "stream"], banks.Selection);
            Assert.Equal(SkinLayoutTemplates.OutputToolbar, window.MonitorTemplate);
            // Every bank's picker reaches all six mixes.
            ComboBox[] pickers = Pickers(window);
            Assert.Equal(2, pickers.Length);
            foreach (ComboBox picker in pickers)
                Assert.Equal(BroadcastMixes, picker.Items.Cast<MixViewModel>().Select(m => m.Id));

            headphones.IsExpanded = true;
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

            // A capture channel that is not connected keeps its strip, with its name and meter only.
            Border cam = RackStrip(rack, "cam");
            Assert.True(cam.IsVisible);
            Assert.Null(cam.GetVisualDescendants().OfType<Slider>().FirstOrDefault(s => s.IsEffectivelyVisible));
            // XLR 2 has no strip in the rack or in a bank, comes back when the
            // interface has it, and goes again.
            Assert.False(RackStrip(rack, "xlr2").IsVisible);
            Assert.DoesNotContain(BankCells(window).SelectMany(c => c), b => ((BankCellViewModel)b.DataContext!).Channel.Id == "xlr2");
            model.HasXlr2 = true;
            apply.Invoke(model, [JsonNode.Parse(mixer.Replace("\"present\":false", "\"present\":true"))]);
            Layout(window, 1040, 1400);
            Assert.True(RackStrip(rack, "xlr2").IsVisible);
            Assert.Contains(BankCells(window).SelectMany(c => c), b => ((BankCellViewModel)b.DataContext!).Channel.Id == "xlr2");
            CheckBroadcastAt(window, model, "with XLR 2");
            model.HasXlr2 = false;
            apply.Invoke(model, [JsonNode.Parse(mixer)]);
            Layout(window, 1040, 1400);

            // The compact view: two banks, each showing the chosen channel alone.
            model.SelectedCompactChannel = model.Channels.Single(c => c.Id == "board");
            Assert.Null(model.ChooseMixerView(MainViewModel.CompactView));
            foreach (double width in new[] { 640d, 1040 })
            {
                Layout(window, width, 1200);
                string where = $"compact at {width}";
                Border[] tiles = Banks(window);
                Assert.Equal(2, tiles.Length);
                AssertNoOverlap(tiles);
                Assert.All(BankCells(window), cells => Assert.Single(cells));
                foreach (Border cell in BankCells(window).SelectMany(c => c)) AssertPartsApart(cell);
                Border[] shown = [.. cards.Where(c => c.IsVisible)];
                AssertNoOverlap([.. shown, header]);
                foreach (Border card in shown) AssertControlsInside(card, scroll);
                Assert.True(Card("SubmixerTile").IsVisible, where);
            }
            Capture(window, "skin-broadcast-compact-1040");

            // The mini view sets INPUTS, HEADPHONES and APPLICATIONS aside, and
            // the rack's column folds away with them: the one bank starts at the left.
            model.SelectedCompactMix = model.Mixes.Single(m => m.Id == "stream");
            Assert.Null(model.ChooseMixerView(MainViewModel.MiniView));
            foreach (double width in new[] { 640d, 1040 })
            {
                Layout(window, width, 1000);
                string where = $"mini at {width}";
                Assert.False(Card("InputsTile").IsVisible, where);
                Assert.False(Card("HeadphonesTile").IsVisible, where);
                Assert.False(Card("ApplicationsTile").IsVisible, where);
                Assert.Single(Banks(window));
                double left = Card("SubmixerTile").TranslatePoint(default, desk)!.Value.X;
                Assert.True(left <= console.HorizontalSpacing + 0.5, $"the submixer starts at {left} {where}");
                Border[] shown = [.. cards.Where(c => c.IsVisible)];
                AssertNoOverlap([.. shown, header]);
                foreach (Border card in shown) AssertControlsInside(card, scroll);
            }
            Capture(window, "skin-broadcast-mini-1040");
            Assert.Null(model.ChooseMixerView(MainViewModel.FullView));
            Layout(window, 1040, 1200);
            Assert.Equal(2, Banks(window).Length);
            // The mini view showed Stream alone, so the first bank moved on to it
            // and the second takes the next mix no bank shows. That is the view
            // moving the banks, not a pick: nothing is saved for the skin.
            Assert.Equal(["stream", "monitor"], window.MixBanks!.Selection);
            Assert.Null(UiSettings.Load().SkinBanksFor("broadcast", 1));

            // Detached: the banks go with the panel into the mixer's window and come back.
            MixerPopOutWindowTests.CheckBanks(window, model);
            Assert.Equal(["stream", "monitor"], window.MixBanks!.Selection);

            // To Material and back: the same sections, channels, mixes and sends throughout.
            Wear(SkinPackage.DefaultId);
            Layout(window, 1040, 900);
            Assert.Null(window.MixBanks);
            Assert.Null(window.InputRackModel);
            Wear("broadcast");
            Layout(window, 1040, 900);
            Assert.Equal(["monitor", "stream"], window.MixBanks!.Selection);
            // Not folded again on a second wearing.
            Assert.True(headphones.IsExpanded);
            Assert.Equal(expanders, Usual.Select(name => window.FindControl<Expander>(name)!));
            Assert.Equal(cards, expanders.Select(e => (Border)e.Parent!));
            Assert.Equal(contents, expanders.Select(e => e.Content));
            Assert.Equal(channels, model.Channels);
            Assert.Equal(mixes, model.Mixes);
            Assert.Equal(sends, model.Channels.SelectMany(c => c.Sends));
            foreach (MixBankViewModel bank in window.MixBanks.Banks)
                foreach (BankCellViewModel cell in bank.Cells)
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
    /// One size of the Broadcast window: the rack and the list in their
    /// column at the rack's width, the banks and the toolbar beside them,
    /// HEADPHONES under both, nothing overlapping or clipped, the toolbar in
    /// two rows exactly when its room is under the threshold, and a tree
    /// wider than the window scrolling sideways under a header that keeps
    /// the window's width.
    /// </summary>
    private static void CheckBroadcastAt(MainWindow window, MainViewModel model, string where)
    {
        Border Card(string tile) => (Border)window.FindControl<Expander>(tile)!.Parent!;
        var desk = (StackPanel)window.LayoutHost.PanelFor("desk")!;
        var console = (DockPanel)window.LayoutHost.PanelFor("console")!;
        Point At(string tile) => Card(tile).TranslatePoint(default, desk)!.Value;
        double Right(string tile) => At(tile).X + Card(tile).Bounds.Width;
        double Bottom(string tile) => At(tile).Y + Card(tile).Bounds.Height;
        var header = window.FindControl<Border>("HeaderCard")!;
        var scroll = window.FindControl<ScrollViewer>("SectionScroll")!;
        Border[] cards = [.. Usual.Select(Card)];
        Assert.All(cards, card => Assert.True(card.IsVisible, $"{((Expander)card.Child!).Name} is hidden {where}"));
        AssertNoOverlap([.. cards, header]);
        foreach (Border card in cards) AssertControlsInside(card, scroll);
        AssertInside(window.FindControl<Grid>("WindowHeader")!, window);

        // The rack's column is as wide as the rack needs, at the left, with the list under the rack.
        double strip = (double)window.FindResource("Ox.Mixer.StripWidth")!;
        double rackWidth = SkinLayoutDefaults.RegionMinWidth(SkinLayoutRegions.Inputs, SkinService.Layout,
            new RegionSizes(strip, (double)window.FindResource("Ox.Mixer.MixWidth")!, (double)window.FindResource("Ox.Mixer.BankSpacing")!, 2, 7));
        Assert.Equal(3 * strip + SkinLayoutDefaults.CardPadding, rackWidth);
        Assert.Equal(rackWidth, Card("InputsTile").Bounds.Width, 0.5);
        Assert.Equal(rackWidth, Card("ApplicationsTile").Bounds.Width, 0.5);
        Assert.Equal(0, At("InputsTile").X, 0.5);
        Assert.Equal(0, At("ApplicationsTile").X, 0.5);
        Assert.True(At("ApplicationsTile").Y >= Bottom("InputsTile") - 0.5, where);

        // The banks beside the rack, the toolbar under the banks, in the same column.
        Assert.True(At("SubmixerTile").X >= Right("InputsTile") + console.HorizontalSpacing - 0.5, where);
        Assert.Equal(Right("SubmixerTile"), desk.Bounds.Width, 0.5);
        Assert.Equal(At("SubmixerTile").X, At("MonitorTile").X, 0.5);
        Assert.Equal(Card("SubmixerTile").Bounds.Width, Card("MonitorTile").Bounds.Width, 0.5);
        Assert.True(At("MonitorTile").Y >= Bottom("SubmixerTile") - 0.5, where);
        Assert.Equal(At("InputsTile").Y, At("SubmixerTile").Y, 0.5);
        // HEADPHONES along the bottom, across the whole width.
        Assert.Equal(0, At("HeadphonesTile").X, 0.5);
        Assert.True(At("HeadphonesTile").Y >= Math.Max(Bottom("ApplicationsTile"), Bottom("MonitorTile")) - 0.5, where);
        Assert.Equal(desk.Bounds.Width, Card("HeadphonesTile").Bounds.Width, 0.5);

        // The rack: every shown strip inside it, one under another, each part apart.
        var rack = window.FindControl<InputRackView>("InputRack")!;
        Border[] strips = [.. rack.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "InputStrip" && b.IsEffectivelyVisible)];
        Assert.Equal(model.HasXlr2 ? 7 : 6, strips.Length);
        AssertNoOverlap(strips);
        foreach (Border s in strips)
        {
            AssertInside(s, Card("InputsTile"));
            AssertPartsApart(s);
        }
        // The 40 character name shortens inside its strip rather than running out of it.
        Border board = RackStrip(rack, "board");
        TextBlock name = board.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == LongName);
        AssertInside(name, board);

        // Two banks side by side on Monitor A and Stream, upright faders and meters inside them.
        Border[] tiles = Banks(window);
        Assert.Equal(2, tiles.Length);
        AssertNoOverlap(tiles);
        Assert.True(tiles[1].TranslatePoint(default, window)!.Value.X > tiles[0].TranslatePoint(default, window)!.Value.X, where);
        Assert.Equal(["monitor", "stream"], window.MixBanks!.Selection);
        double length = (double)window.FindResource("Ox.Mixer.VerticalFaderLength")!;
        double meterWidth = (double)window.FindResource("Ox.Meter.VerticalWidth")!;
        Assert.Equal(6, meterWidth);
        foreach (Border bank in tiles)
        {
            AssertInside(bank, Card("SubmixerTile"));
            var cells = bank.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "BankCells");
            var master = bank.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "BankMaster");
            AssertInside(cells, bank);
            AssertInside(master, bank);
            AssertNoOverlap([cells, master]);
            foreach (Slider fader in bank.GetVisualDescendants().OfType<Slider>().Where(f => f.IsEffectivelyVisible))
            {
                Assert.Equal(Orientation.Vertical, fader.Orientation);
                Assert.True(fader.Bounds.Height >= length - 0.5, $"an upright fader is {fader.Bounds.Height} tall {where}");
            }
            foreach (LevelMeter meter in bank.GetVisualDescendants().OfType<LevelMeter>().Where(m => m.IsEffectivelyVisible))
            {
                Assert.Equal(Orientation.Vertical, meter.Orientation);
                Assert.Equal(meterWidth, meter.Bounds.Width, 0.5);
            }
            // More strips than the bank has room for scroll inside it.
            if (cells.Extent.Width > cells.Viewport.Width + 1)
                Assert.Equal(ScrollBarVisibility.Auto, cells.HorizontalScrollBarVisibility);
        }
        foreach (Border cell in BankCells(window).SelectMany(c => c)) AssertPartsApart(cell);

        // The toolbar stands in two rows exactly when its room is under the threshold.
        var controls = window.FindControl<Grid>("MonitorControls")!;
        var picker = window.FindControl<DropDownButton>("MonitorDestination")!;
        var volume = window.FindControl<Slider>("OutputVolumeSlider")!;
        foreach (Control part in new Control[] { picker, volume }) AssertInside(part, Card("MonitorTile"));
        AssertNoOverlap([picker, volume]);
        bool stacked = controls.Bounds.Width < SkinLayoutDefaults.MonitorStackWidth;
        double pickerTop = picker.TranslatePoint(default, controls)!.Value.Y;
        double volumeTop = volume.TranslatePoint(default, controls)!.Value.Y;
        Assert.True(stacked ? volumeTop > pickerTop + picker.Bounds.Height - 0.5 : Math.Abs(volumeTop - pickerTop) < picker.Bounds.Height,
            $"the toolbar is {controls.Bounds.Width} wide and {(stacked ? "not stacked" : "stacked")} {where}");
        if (where.EndsWith(" at 640", StringComparison.Ordinal)) Assert.True(stacked, where);
        if (where.EndsWith(" at 2560", StringComparison.Ordinal)) Assert.False(stacked, where);

        // The application list: a line per application inside the list's section.
        Border[] lines = ApplicationLines(window);
        Assert.Equal(model.ActiveApps.Count, lines.Length);
        foreach (Border line in lines) AssertInside(line, Card("ApplicationsTile"));

        // Wider than the window, the tree scrolls sideways under a header that keeps the window's width.
        if (scroll.Extent.Width > scroll.Viewport.Width + 1)
        {
            Assert.Equal(ScrollBarVisibility.Auto, scroll.HorizontalScrollBarVisibility);
            scroll.Offset = new Vector(scroll.Extent.Width - scroll.Viewport.Width, 0);
            Layout(window, window.ClientSize.Width, window.ClientSize.Height);
            double right = Card("SubmixerTile").TranslatePoint(new Point(Card("SubmixerTile").Bounds.Width, 0), scroll)!.Value.X;
            Assert.InRange(right, 0, scroll.Viewport.Width + 0.5);
            AssertInside(window.FindControl<Grid>("WindowHeader")!, window);
            scroll.Offset = default;
            Layout(window, window.ClientSize.Width, window.ClientSize.Height);
        }
        else Assert.False(where.EndsWith(" at 640", StringComparison.Ordinal), $"the tree fits {where}");
    }
}
