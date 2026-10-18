using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

public sealed partial class SkinWindowTests
{
    private static readonly Dictionary<string, string> SwitchSkins = new()
    {
        ["focus-strips"] = """
            {"schema":1,"name":"Strips","tokens":{"Ox.Card.Background":"#2c3038"},"layout":{"version":1,
             "regions":{"submixer":{"template":"mix-banks","visibleBanks":2,"selectionDefaults":["mix1","mix0"]}}}}
            """,
        ["focus-list"] = """
            {"schema":1,"name":"List","tokens":{"Ox.Card.Background":"#2c3038"},"layout":{"version":1,
             "regions":{"monitor":{"template":"output-toolbar"},"applications":{"template":"application-list"},
               "submixer":{"template":"mix-banks","orientation":"vertical","visibleBanks":3,"selectionDefaults":["mix2","mix1"],
                 "strip":{"template":"send-list"}}}}}
            """,
        ["focus-row"] = """
            {"schema":1,"name":"Row","tokens":{"Ox.Card.Background":"#2c3038"},"layout":{"version":1,
             "main":{"kind":"stack","id":"row","orientation":"horizontal","children":[
               {"kind":"region","region":"submixer"},{"kind":"region","region":"monitor"},{"kind":"region","region":"inputs"},
               {"kind":"region","region":"headphones"},{"kind":"region","region":"applications"}]}}}
            """,
    };

    /// <summary>
    /// A structural switch that builds the focused fader again: focus goes to
    /// the new fader for the same channel and mix, through upright strips, a
    /// send list and back to the channel tiles. The sections' folds, the
    /// compact view's channel and mix and the page's scroll come through a
    /// switch that keeps the tree. Eighteen switches between three skins leave
    /// <see cref="SkinService.Changed"/> with the handlers it had, and the last
    /// nine cost no more than the first nine.
    /// </summary>
    private static void AStructuralSwitchKeepsFocusFoldsAndScrollAndLeavesNothingBehind(MainWindow main)
    {
        string data = Path.Combine(Path.GetTempPath(), "openxlr-skinswitch-" + Guid.NewGuid());
        string? oldHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? oldDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        var vm = (MainViewModel)main.DataContext!;
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(data, "none"));
            foreach ((string id, string json) in SwitchSkins)
            {
                string folder = Path.Combine(data, "openxlr", "skins", id);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "skin.json"), json);
            }
            double height = 900;
            void Wear(string id)
            {
                SkinEntry entry = id == SkinPackage.DefaultId ? new SkinEntry(SkinPackage.Default, []) : SkinCatalog.Find(id)!;
                Assert.Empty(entry.Errors);
                Assert.Empty(SkinService.Apply(entry));
                Pump(main);
                Layout(main, 1040, height);
            }
            ChannelViewModel game = vm.Channels.Single(c => c.Id == "game");
            MixViewModel stream = vm.Mixes.Single(m => m.Id == "mix1");
            Control Focused() => Assert.IsAssignableFrom<Control>(main.FocusManager!.GetFocusedElement());
            Slider BankFader() => main.GetVisualDescendants().OfType<Slider>().Single(s => s.IsEffectivelyVisible
                && s.DataContext is BankCellViewModel cell && cell.Channel == game && cell.Bank.Mix == stream);

            var headphones = main.FindControl<Expander>("HeadphonesTile")!;
            headphones.IsExpanded = false;
            vm.SelectedCompactChannel = game;
            vm.SelectedCompactMix = stream;
            Wear("focus-strips");
            Slider strip = BankFader();
            Assert.True(strip.Focus());
            Assert.Same(strip, Focused());

            // Upright strips to a send list: the template builds the fader again, and focus finds it.
            Wear("focus-list");
            Slider row = BankFader();
            Assert.NotSame(strip, row);
            Assert.Same(row, Focused());
            Assert.Equal(Avalonia.Layout.Orientation.Horizontal, row.Orientation);
            // And to the channel tiles: the tile's own send fader for the same pair.
            Wear(SkinPackage.DefaultId);
            Slider tile = Assert.IsType<Slider>(Focused());
            SendViewModel send = Assert.IsType<SendViewModel>(tile.DataContext);
            Assert.Same(game.Sends.Single(s => s.MixId == "mix1"), send);
            Assert.True(tile.IsEffectivelyVisible);
            // Back to strips: the bank fader once more.
            Wear("focus-strips");
            Assert.Same(BankFader(), Focused());

            // A mute keeps its role: the mute for the same pair, not the fader.
            ToggleButton Mute() => main.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.IsEffectivelyVisible
                && t.Classes.Contains("mute") && t.DataContext is BankCellViewModel cell && cell.Channel == game && cell.Bank.Mix == stream);
            Assert.True(Mute().Focus());
            ToggleButton before = Mute();
            Wear("focus-list");
            Assert.NotSame(before, Focused());
            Assert.Same(Mute(), Focused());

            // What the user set comes through every switch untouched.
            Assert.False(headphones.IsExpanded);
            Assert.All(new[] { "InputsTile", "MonitorTile", "ApplicationsTile", "SubmixerTile" },
                name => Assert.True(main.FindControl<Expander>(name)!.IsExpanded, name));
            Assert.Same(game, vm.SelectedCompactChannel);
            Assert.Same(stream, vm.SelectedCompactMix);

            // The page keeps its scroll across a switch that keeps the tree,
            // with focus outside the regions so nothing scrolls it into view.
            Assert.True(main.FindControl<Button>("OptionsButton")!.Focus());
            var tree = main.FindControl<ScrollViewer>("SectionScroll")!;
            ScrollViewer page = tree.GetVisualAncestors().OfType<ScrollViewer>().First();
            height = 600;
            Wear("focus-strips");
            Assert.True(page.Extent.Height - page.Viewport.Height > 300, $"the page is {page.Extent.Height} tall in {page.Viewport.Height}");
            page.Offset = new Vector(0, 240);
            Layout(main, 1040, height);
            Wear("focus-list");
            Assert.Equal(240, page.Offset.Y, 0.5);
            // The tree's own sideways scroll holds while the tree does, and a new tree starts at its left.
            Wear("focus-row");
            Assert.True(tree.Extent.Width > tree.Viewport.Width + 300, $"the row is {tree.Extent.Width} wide");
            tree.Offset = new Vector(300, 0);
            Layout(main, 1040, height);
            Assert.Equal(300, tree.Offset.X, 0.5);
            SkinService.ChooseControlSizing(SkinService.TouchControls, out _);
            Pump(main);
            Assert.Equal(300, tree.Offset.X, 0.5);
            Wear("focus-strips");
            Assert.Equal(0, tree.Offset.X, 0.5);
            headphones.IsExpanded = true;
            height = 900;

            // Nothing retained: the same handlers after eighteen switches, and no growing cost.
            string[] cycle = ["focus-strips", "focus-list", "focus-row"];
            foreach (string id in cycle.Concat(cycle)) Wear(id);
            Wear(cycle[0]);
            int subscribers = SkinService.ChangedSubscribers;
            // Eighteen switches, so each half makes three whole rounds of the three skins.
            long[] costs = new long[18];
            for (int i = 0; i < costs.Length; i++)
            {
                // Only the switch itself is measured: applying the skin, its jobs
                // and one layout pass, all on this thread. The timed pump after it,
                // whose render ticks vary with the clock, stays outside.
                long start = GC.GetAllocatedBytesForCurrentThread();
                Assert.Empty(SkinService.Apply(SkinCatalog.Find(cycle[(i + 1) % cycle.Length])!));
                Dispatcher.UIThread.RunJobs();
                main.UpdateLayout();
                costs[i] = GC.GetAllocatedBytesForCurrentThread() - start;
                Pump(main);
            }
            Wear(cycle[0]);
            Assert.Equal(subscribers, SkinService.ChangedSubscribers);
            long first = costs[..9].Sum(), last = costs[9..].Sum();
            // Both halves make the same nine switches in the same order after a
            // warm-up of two rounds, so they should cost the same; a retained
            // tree, handler or list that grows with each switch makes the later
            // ones dearer. Measured here, one switch costs 25 to 36 MB depending
            // on the skin and the same switch varies by under 0.1% from round to
            // round, so a tenth more, plus 256 KiB, leaves room for that noise and
            // for a one-off cache filling late, and still fails a cost that grows.
            Assert.True(last <= first * 1.10 + 256 * 1024,
                $"the last nine switches allocated {last} bytes, the first nine {first}: [{string.Join(", ", costs)}]");
        }
        finally
        {
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            Pump(main);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldHome);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", oldDirs);
            if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
        }
    }
}
