using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

internal static partial class MixerPopOutWindowTests
{
    /// <param name="outer">The layout test's own window, set aside while a skin's pop-out default is tried, since one process has one main window.</param>
    internal static void Check(MainWindow outer)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-popout-").FullName;
        string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        SkinEntry skin = SkinService.Current;
        bool touch = SkinService.TouchControls;
        MainWindow? main = null;
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
            main = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
            var vm = (MainViewModel)main.DataContext!;
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [JsonNode.Parse("""
                {"mixes":[{"id":"monitor","name":"Monitor A with a long name","kind":"monitor"},
                          {"id":"monitor2","name":"Monitor B","kind":"monitor"}],
                 "channels":[{"id":"music","name":"Music with an especially long channel name","levels":{"monitor":0.5,"monitor2":0.7}},
                             {"id":"game","name":"Game","levels":{"monitor":1,"monitor2":1}}]}
                """)]);
            main.Show();
            Layout(main, 1040, 900);
            var popOut = main.FindControl<Button>("PopOutMixerButton")!;
            Assert.True(popOut.IsVisible);
            var host = main.FindControl<ContentControl>("MixerHost")!;
            var panel = (Control)host.Content!;
            InsertWindows.OpenChain(main, vm.Mixes[0].Inserts, "mix:monitor");
            var originalChain = Assert.Single(main.OwnedWindows.OfType<MixInsertsWindow>());
            var insert = new InsertViewModel(vm.Mixes[0].Inserts, "popout-test", "urn:openxlr:test", "Test effect");
            InsertWindows.OpenControls(main, insert);
            var originalControls = Assert.Single(main.OwnedWindows.OfType<InsertControlsWindow>());
            MixerWindow popped = main.PopOutMixer();
            Layout(popped, 800, 480);
            Assert.Same(popped, main.PopOutMixer());
            Assert.False(popOut.IsVisible);
            Assert.Null(host.Content);
            Assert.Same(vm, popped.DataContext);
            Assert.Same(panel, popped.FindControl<ScrollViewer>("MixerScroll")!.Content);
            Slider master = Master(popped, vm);

            main.Hide();
            Assert.True(popped.IsVisible);
            vm.Mixes[0].ApplyFromDaemon(JsonNode.Parse("""{"volume":0.8,"kind":"monitor"}""")!);
            Layout(popped, 800, 480);
            Assert.Equal(0.8, master.Value);
            master.Value = 0.42;
            Assert.Equal(0.42, vm.Mixes[0].Volume);
            // A chain opened from the detached panel belongs to its visible window.
            popped.GetVisualDescendants().OfType<Button>().Single(b =>
                b.DataContext == vm.Mixes[0] && Equals(b.Content, vm.Mixes[0].Inserts.ButtonText))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var chain = Assert.Single(popped.OwnedWindows.OfType<MixInsertsWindow>());
            Assert.NotSame(originalChain, chain);
            Assert.False(originalChain.IsVisible);
            Assert.Same(vm.Mixes[0].Inserts, chain.DataContext);
            chain.Close();
            InsertWindows.OpenControls(popped, insert);
            var controls = Assert.Single(popped.OwnedWindows.OfType<InsertControlsWindow>());
            Assert.False(originalControls.IsVisible);
            Assert.Same(insert, controls.DataContext);
            controls.Hide();
            InsertWindows.OpenControls(popped, insert);
            var restored = Assert.Single(popped.OwnedWindows.OfType<InsertControlsWindow>());
            Assert.True(restored.IsVisible);
            Assert.Same(insert, restored.DataContext);
            restored.Close();
            Capture(popped, "popout-800");

            foreach (string id in new[] { SkinPackage.DefaultId, "opendeck" })
            {
                SkinService.Apply(SkinCatalog.Find(id)!);
                SkinService.ApplyControlSizing(true);
                Layout(popped, 480, 320);
                Assert.Contains("large-targets", popped.Classes);
                AssertInside(popped.FindControl<ScrollViewer>("MixerScroll")!, popped);
                Assert.True(popped.FindControl<ScrollViewer>("MixerScroll")!.Extent.Height
                    > popped.FindControl<ScrollViewer>("MixerScroll")!.Viewport.Height);
                foreach (var row in popped.GetVisualDescendants().OfType<Grid>()
                    .Where(g => g.DataContext is SendViewModel && g.Children.OfType<TextBlock>().Count() == 2))
                    AssertNoOverlap(row.Children.ToArray());
                Capture(popped, "popout-480-" + id);
            }

            vm.ChooseMixerView(MainViewModel.MiniView);
            Layout(popped, 320, 300);
            Assert.True(main.FindControl<ComboBox>("CompactChannelPicker")!.IsEffectivelyVisible);
            Capture(popped, "popout-mini-320");
            popped.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(main.IsVisible);
            Assert.Same(panel, host.Content);
            Assert.True(popOut.IsVisible);
            // The button follows the mixer, as Edit layout beside it does.
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [null]);
            Assert.False(popOut.IsVisible);
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [JsonNode.Parse("""
                {"mixes":[{"id":"monitor","name":"Monitor A with a long name","kind":"monitor","volume":0.42},
                          {"id":"monitor2","name":"Monitor B","kind":"monitor"}],
                 "channels":[{"id":"music","name":"Music with an especially long channel name","levels":{"monitor":0.5,"monitor2":0.7}},
                             {"id":"game","name":"Game","levels":{"monitor":1,"monitor2":1}}]}
                """)]);
            Assert.True(popOut.IsVisible);
            Layout(main, 1040, 900);
            Assert.Equal(0.42, Master(main, vm).Value);

            // Reopening moves the same controls; quitting closes the separate window too.
            popped = main.PopOutMixer();
            Layout(popped, 480, 320);
            Assert.Equal(0.42, Master(popped, vm).Value);
            main.Quit();
            Assert.False(popped.IsVisible);
            Assert.False(main.IsVisible);

            CheckRecreatedMix();
            CheckSkinSwitching(outer);
            MixerFaderLifecycleWindowTests.Check();
        }
        finally
        {
            main?.Quit();
            SkinService.ApplyControlSizing(touch);
            SkinService.Apply(skin);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// The mix banks detach with the panel they are drawn in: the same panel,
    /// the same banks and cells, the faders still writing to the window's
    /// sends, a width the banks fit, and the same panel back on docking.
    /// Run from the skin layout test while a skin with banks is worn.
    /// </summary>
    internal static void CheckBanks(MainWindow main, MainViewModel vm)
    {
        var host = main.FindControl<ContentControl>("MixerHost")!;
        var panel = Assert.IsType<OpenXLR.UI.Views.MixerPanel>(host.Content);
        var banks = Assert.IsType<OpenXLR.UI.Layout.MixerPresentationViewModel>(panel.Banks);
        OpenXLR.UI.Layout.MixBankViewModel[] bankModels = [.. banks.Banks];
        MixerWindow popped = main.PopOutMixer();
        try
        {
            Layout(popped, 800, 600);
            Assert.Null(host.Content);
            Assert.Same(panel, popped.FindControl<ScrollViewer>("MixerScroll")!.Content);
            Assert.Same(banks, panel.Banks);
            Assert.Equal(bankModels, banks.Banks);
            Assert.True(panel.BankItems.IsEffectivelyVisible);
            Border[] tiles = SkinLayoutWindowTests.Banks(popped);
            Assert.Equal(bankModels.Length, tiles.Length);
            AssertNoOverlap(tiles);
            Assert.True(panel.Bounds.Width >= panel.MinimumWidth() + 8 - 0.5, $"the panel is {panel.Bounds.Width} wide");
            var cell = SkinLayoutWindowTests.BankCells(popped)[0][0];
            var model = (OpenXLR.UI.Layout.BankCellViewModel)cell.DataContext!;
            Slider fader = cell.GetVisualDescendants().OfType<Slider>().Single();
            fader.Value = 0.33;
            Assert.Equal(0.33, model.Send!.Level);
            Assert.Same(vm.Channels.Single(c => c.Id == model.Channel.Id).Sends.Single(s => s.MixId == bankModels[0].Mix!.Id), model.Send);
            Layout(popped, 360, 400);
            Assert.True(panel.Bounds.Width >= panel.MinimumWidth() + 8 - 0.5);
            Assert.True(popped.FindControl<ScrollViewer>("MixerScroll")!.Extent.Width
                > popped.FindControl<ScrollViewer>("MixerScroll")!.Viewport.Width, "a narrow mixer window scrolls the banks sideways");
            Capture(popped, "popout-banks-360");
        }
        finally
        {
            popped.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Same(panel, host.Content);
        Assert.Same(banks, panel.Banks);
        Assert.Equal(bankModels, banks.Banks);
        Layout(main, 1040, 1200);
        Assert.True(panel.BankItems.IsEffectivelyVisible);
    }

    /// <summary>
    /// The mix matrix popped out and docked again: the same matrix, rows,
    /// columns and cells in the mixer's window, as wide as the whole matrix,
    /// a cell's fader writing to the window's own send, and the same objects
    /// back in the main window.
    /// </summary>
    internal static void CheckMatrix(MainWindow main, MainViewModel vm)
    {
        var host = main.FindControl<ContentControl>("MixerHost")!;
        var panel = Assert.IsType<OpenXLR.UI.Views.MixerPanel>(host.Content);
        var matrix = Assert.IsType<OpenXLR.UI.Layout.MixMatrixViewModel>(panel.Matrix);
        OpenXLR.UI.Layout.MatrixRowViewModel[] rows = [.. matrix.Rows];
        OpenXLR.UI.Layout.MatrixColumnViewModel[] columns = [.. matrix.Columns];
        OpenXLR.UI.Layout.MatrixCellViewModel[] cells = [.. matrix.Rows.SelectMany(r => r.Cells)];
        MixerWindow popped = main.PopOutMixer();
        try
        {
            Layout(popped, 1200, 700);
            Assert.Null(host.Content);
            Assert.Same(panel, popped.FindControl<ScrollViewer>("MixerScroll")!.Content);
            Assert.Same(matrix, panel.Matrix);
            Assert.Null(panel.Banks);
            Assert.Equal(rows, matrix.Rows);
            Assert.Equal(columns, matrix.Columns);
            Assert.Equal(cells, matrix.Rows.SelectMany(r => r.Cells));
            Assert.True(panel.MatrixItems.IsEffectivelyVisible);
            Assert.False(panel.BankItems.IsVisible);
            // The whole matrix: the row heads and every column beside them.
            double cell = (double)popped.FindResource("Ox.Matrix.CellWidth")!;
            double name = (double)popped.FindResource("Ox.Matrix.NameWidth")!;
            Assert.Equal(name + cell + columns.Length * (cell + OpenXLR.UI.Skinning.SkinLayoutDefaults.MatrixSpacing), panel.MinimumWidth(), 0.5);
            Assert.True(panel.Bounds.Width >= panel.MinimumWidth() + 8 - 0.5, $"the panel is {panel.Bounds.Width} wide");
            SkinLayoutWindowTests.CheckMatrixCells(popped, matrix, "detached");
            Border first = popped.GetVisualDescendants().OfType<Border>().First(b => b.Name == "MatrixCell" && b.IsEffectivelyVisible
                && b.DataContext is OpenXLR.UI.Layout.MatrixCellViewModel { IsEmpty: false });
            var model = (OpenXLR.UI.Layout.MatrixCellViewModel)first.DataContext!;
            Slider fader = first.GetVisualDescendants().OfType<Slider>().Single();
            fader.Value = 0.33;
            Assert.Equal(0.33, model.Send!.Level);
            Assert.Same(vm.Channels.Single(c => c.Id == model.Channel.Id).Sends.Single(s => s.MixId == model.Mix.Id), model.Send);
            Capture(popped, "popout-matrix");
            Layout(popped, 360, 400);
            Assert.True(panel.Bounds.Width >= panel.MinimumWidth() + 8 - 0.5);
            Assert.True(popped.FindControl<ScrollViewer>("MixerScroll")!.Extent.Width
                > popped.FindControl<ScrollViewer>("MixerScroll")!.Viewport.Width, "a narrow mixer window scrolls the matrix sideways");
        }
        finally
        {
            popped.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Same(panel, host.Content);
        Assert.Same(matrix, panel.Matrix);
        Assert.Equal(rows, matrix.Rows);
        Assert.Equal(cells, matrix.Rows.SelectMany(r => r.Cells));
        Layout(main, 1040, 1200);
        Assert.True(panel.MatrixItems.IsEffectivelyVisible);
    }

    private static Slider Master(Window window, MainViewModel vm) => window.GetVisualDescendants().OfType<Slider>()
        .Single(s => s.DataContext == vm.Mixes[0]);

    private static void CheckRecreatedMix()
    {
        var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var owner = new Window();
        try
        {
            owner.Show();
            var vm = new MainViewModel(client);
            var apply = typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            const string state = """{"mixes":[{"id":"headset","name":"Headset","kind":"monitor"}],"channels":[]}""";
            apply.Invoke(vm, [JsonNode.Parse(state)]);
            InsertWindows.OpenChain(owner, vm.Mixes[0].Inserts, "mix:headset");
            var old = Assert.Single(owner.OwnedWindows.OfType<MixInsertsWindow>());
            apply.Invoke(vm, [JsonNode.Parse("""{"mixes":[{"id":"headset","name":"Speakers","kind":"monitor"}],"channels":[]}""")]);
            Dispatcher.UIThread.RunJobs();
            string title = Localizer.Format("MixInsertsTitle", "Speakers");
            Assert.Equal(title, vm.Mixes[0].Inserts.Title);
            Assert.Equal(Localizer.Format("InsertsTitle", title), old.Title);
            var insert = new InsertViewModel(vm.Mixes[0].Inserts, "old-effect", "urn:openxlr:test", "Old effect");
            vm.Mixes[0].Inserts.Items.Add(insert);
            InsertWindows.OpenControls(owner, insert);
            var oldControls = Assert.Single(owner.OwnedWindows.OfType<InsertControlsWindow>());
            // Deleting and re-creating a name reuses its id, but not its live chain.
            apply.Invoke(vm, [JsonNode.Parse("""{"mixes":[],"channels":[]}""")]);
            Assert.False(old.IsVisible);
            Assert.False(oldControls.IsVisible);
            apply.Invoke(vm, [JsonNode.Parse(state)]);
            InsertWindows.OpenChain(owner, vm.Mixes[0].Inserts, "mix:headset");
            var current = Assert.Single(owner.OwnedWindows.OfType<MixInsertsWindow>());
            Assert.Same(vm.Mixes[0].Inserts, current.DataContext);
            Assert.False(old.IsVisible);
            InsertWindows.OpenChain(owner, vm.Mixes[0].Inserts, "mix:headset");
            Assert.Same(current, Assert.Single(owner.OwnedWindows.OfType<MixInsertsWindow>()));
        }
        finally { owner.Close(); client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
}
