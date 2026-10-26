using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using OpenXLR.UI.Views;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// The size picker under Return mixer to main window: a fixed size draws the
/// whole panel larger and wraps it at the width it has before it is
/// enlarged, a fader still follows the pointer, a narrow window scrolls
/// sideways; Fit picks the largest size that shows the panel whole, follows
/// the window and the panel, never goes under the skin's own size and
/// settles; the choice is kept in ui.json; and the panel docks back with no
/// width, scale or transform left on it.
/// </summary>
internal static partial class MixerPopOutWindowTests
{
    private static void CheckScale(MainWindow main, MainViewModel vm)
    {
        var host = main.FindControl<ContentControl>("MixerHost")!;
        var panel = Assert.IsType<MixerPanel>(host.Content);
        string view = vm.MixerView;
        Assert.Null(vm.ChooseMixerView(MainViewModel.FullView));
        Layout(main, 1040, 900);
        Size docked = panel.Bounds.Size;
        Assert.Null(UiSettings.Load().MixerScale);
        bool touch = SkinService.TouchControls;
        double volume = vm.Mixes[0].Volume;

        MixerWindow popped = main.PopOutMixer();
        try
        {
            Layout(popped, 800, 600);
            var picker = popped.FindControl<ComboBox>("MixerScalePicker")!;
            var scroll = popped.FindControl<ScrollViewer>("MixerScroll")!;
            var scaler = popped.FindControl<LayoutTransformControl>("PanelScale")!;
            var note = popped.FindControl<TextBlock>("MixerScaleNote")!;
            Button dock = popped.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Localizer.Text("DockMixer")));

            // Under the Return button, above the panel, at the skin's own size.
            Assert.True(picker.IsEffectivelyVisible);
            double pickerTop = picker.TranslatePoint(default, popped)!.Value.Y;
            Assert.True(pickerTop >= dock.TranslatePoint(default, popped)!.Value.Y + dock.Bounds.Height, "the size picker sits under Return");
            Assert.True(scroll.TranslatePoint(default, popped)!.Value.Y >= pickerTop + picker.Bounds.Height, "the size picker sits above the panel");
            Assert.Equal([Localizer.Text("MixerScaleFit"), .. new[] { 100, 125, 150, 175, 200 }.Select(p => Localizer.Format("MixerScalePercent", p))],
                picker.Items.Cast<string>());
            Assert.Equal(MixerScale.Default, popped.ScaleChoice);
            Assert.Equal(1, picker.SelectedIndex);
            Assert.Null(scaler.LayoutTransform);
            Assert.Equal(1, popped.Scale);
            Assert.Equal(scroll.Viewport.Width, panel.Width, 0.5);
            Assert.False(note.IsVisible);

            // A fixed size: the whole panel half as large again, wrapped at the
            // width it has before it is enlarged, with nothing to scroll sideways.
            picker.SelectedIndex = MixerScale.Choices.ToList().IndexOf("150");
            Layout(popped, 800, 600);
            Assert.Equal("150", popped.ScaleChoice);
            Assert.Equal(1.5, popped.Scale);
            Assert.Equal("150", UiSettings.Load().MixerScale);
            Assert.False(note.IsVisible);
            Assert.Equal(Math.Floor(scroll.Viewport.Width / 1.5) - 1, panel.Width, 0.5);
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width, $"{scroll.Extent.Width} wide in a {scroll.Viewport.Width} viewport");
            Slider master = Master(popped, vm);
            master.BringIntoView();
            Settle(popped);
            Assert.Equal(1.5 * master.Bounds.Width, Drawn(master, popped).Width, 0.5);
            CheckPointer(popped, master, vm);
            Capture(popped, "popout-scale-150");

            // Narrower than the enlarged panel: it scrolls sideways and clips nothing.
            Layout(popped, 360, 600);
            Assert.True(panel.Width >= panel.MinimumWidth() + 8 - 0.5, $"the panel is {panel.Width} wide");
            Assert.True(scroll.Extent.Width > scroll.Viewport.Width, "a narrow window scrolls the enlarged panel sideways");
            AssertInside(scroll, popped);

            // Fit in a large window: the largest size that shows the panel whole.
            picker.SelectedIndex = 0;
            Layout(popped, 1600, 1200);
            Settle(popped);
            Assert.Equal(MixerScale.Fit, popped.ScaleChoice);
            Assert.Equal(MixerScale.Fit, UiSettings.Load().MixerScale);
            double large = popped.Scale;
            Assert.True(large > 1, $"Fit drew the panel at {large} in a large window");
            AssertWhole(scroll, "fitted in a large window");
            AssertLargest(popped, panel, scroll);
            Capture(popped, "popout-scale-fit-1600");
            // It settles: another round of jobs and layout changes nothing.
            var transform = scaler.LayoutTransform;
            Settle(popped);
            Assert.Same(transform, scaler.LayoutTransform);
            Assert.Equal(large, popped.Scale);

            // A smaller window, a smaller size; one too small for the panel stays at the skin's size and scrolls.
            Layout(popped, 1000, 760);
            Settle(popped);
            double medium = popped.Scale;
            Assert.True(medium < large, $"Fit drew {medium} in a smaller window, {large} in the larger one");
            if (medium > 1) AssertWhole(scroll, "fitted in a smaller window");
            Layout(popped, 360, 300);
            Settle(popped);
            Assert.Equal(1, popped.Scale);
            Assert.Null(scaler.LayoutTransform);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "a window too small for the panel scrolls");

            // The panel changes under Fit: a view that shows less is never drawn smaller, and
            // the full view comes back at its own size. When the full view is a single row
            // the two are as tall, which depends on the font, so equal sizes are allowed.
            Layout(popped, 1200, 900);
            Settle(popped);
            double full = popped.Scale;
            Assert.Null(vm.ChooseMixerView(MainViewModel.MiniView));
            Settle(popped);
            Assert.True(popped.Scale >= full, $"the mini view was drawn at {popped.Scale}, the full one at {full}");
            AssertWhole(scroll, "fitted in the mini view");
            Assert.Null(vm.ChooseMixerView(MainViewModel.FullView));
            Settle(popped);
            Assert.Equal(full, popped.Scale);
            AssertWhole(scroll, "fitted after the full view came back");
            // Touch sizing draws the panel larger to begin with, so Fit enlarges it less.
            SkinService.ApplyControlSizing(true);
            Settle(popped);
            Assert.True(popped.Scale <= full, $"touch sizing was drawn at {popped.Scale}, standard at {full}");
            if (popped.Scale > 1) AssertWhole(scroll, "fitted with touch sizing");
            SkinService.ApplyControlSizing(touch);
            Settle(popped);
            Assert.Equal(full, popped.Scale);

            // Docking gives the panel back as it came.
            popped.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Same(panel, host.Content);
            Assert.False(panel.IsSet(Layoutable.WidthProperty));
            Assert.Null(panel.RenderTransform);
            Layout(main, 1040, 900);
            Assert.Equal(docked, panel.Bounds.Size);
            Slider docks = Master(main, vm);
            Assert.Equal(docks.Bounds.Width, Drawn(docks, main).Width, 0.5);

            // The next pop out comes back at the size chosen last.
            popped = main.PopOutMixer();
            Layout(popped, 1600, 1200);
            Settle(popped);
            picker = popped.FindControl<ComboBox>("MixerScalePicker")!;
            scroll = popped.FindControl<ScrollViewer>("MixerScroll")!;
            scaler = popped.FindControl<LayoutTransformControl>("PanelScale")!;
            Assert.Equal(MixerScale.Fit, popped.ScaleChoice);
            Assert.Equal(0, picker.SelectedIndex);
            Assert.Equal(large, popped.Scale);
            // Back to the skin's size: no transform, wrapped at the viewport as before sizes existed.
            picker.SelectedIndex = 1;
            Layout(popped, 800, 600);
            Assert.Equal(MixerScale.Default, UiSettings.Load().MixerScale);
            Assert.Null(scaler.LayoutTransform);
            Assert.Equal(1, popped.Scale);
            Assert.Equal(scroll.Viewport.Width, panel.Width, 0.5);
        }
        finally
        {
            SkinService.ApplyControlSizing(touch);
            popped.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Same(panel, host.Content);
        Layout(main, 1040, 900);
        Master(main, vm).Value = volume;
        Assert.Null(vm.ChooseMixerView(view));
        Layout(main, 1040, 900);
    }

    /// <summary>A control's box as the window draws it, enlargement and all.</summary>
    private static Rect Drawn(Visual control, Visual window) =>
        new Rect(control.Bounds.Size).TransformToAABB(control.TransformToVisual(window)!.Value);

    /// <summary>The scroll viewer shows its content whole: nothing to scroll either way.</summary>
    private static void AssertWhole(ScrollViewer scroll, string where)
    {
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width, $"{where}: {scroll.Extent.Width} wide in {scroll.Viewport.Width}");
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height, $"{where}: {scroll.Extent.Height} tall in {scroll.Viewport.Height}");
    }

    /// <summary>One step larger than Fit chose would not show the panel whole.</summary>
    private static void AssertLargest(MixerWindow popped, MixerPanel panel, ScrollViewer scroll)
    {
        double next = popped.Scale + MixerScale.FitStepPercent / 100.0;
        if (next > MixerScale.FitMaximumPercent / 100.0 + 1e-9) return;
        double width = Math.Floor(scroll.Viewport.Width / next) - 1;
        bool fits = width >= panel.MinimumWidth() + 8;
        if (fits)
        {
            panel.Width = width;
            panel.Measure(new Size(width, double.PositiveInfinity));
            fits = panel.DesiredSize.Height * next <= scroll.Viewport.Height - 1;
        }
        popped.UpdatePanelWidth();
        Settle(popped);
        Assert.False(fits, $"Fit chose {popped.Scale} where {next} fits too");
    }

    /// <summary>
    /// The enlarged fader under a real pointer: the point at its cap's centre
    /// hits the cap, and a drag moves the cap with the pointer, as far on the
    /// screen as the pointer went, writing the mix's volume.
    /// </summary>
    private static void CheckPointer(MixerWindow popped, Slider slider, MainViewModel vm)
    {
        slider.Value = .5;
        Settle(popped);
        // Hit testing reads the scene as last drawn, so let a frame be drawn first.
        FaderInputWindowTests.Pump();
        var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
        var centre = new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2);
        Point inWindow = thumb.TranslatePoint(centre, popped)!.Value;
        Assert.True(new Rect(popped.ClientSize).Contains(inWindow), $"the fader's cap at {inWindow} is off the window");
        Visual hit = Assert.IsAssignableFrom<Visual>(popped.InputHitTest(inWindow));
        Assert.True(ReferenceEquals(hit, thumb) || thumb.IsVisualAncestorOf(hit), $"{hit.GetType().Name} is under the cap: "
            + string.Join(" < ", hit.GetVisualAncestors().Take(8).Select(v => v.GetType().Name + (v is Control { Name: { } n } ? "#" + n : ""))));

        popped.Activate();
        using var input = new XPointer();
        bool horizontal = slider.Orientation == Orientation.Horizontal;
        PixelPoint start = thumb.PointToScreen(centre);
        input.MoveTo(start.X, start.Y);
        FaderInputWindowTests.Pump();
        input.SetButton(true);
        try
        {
            FaderInputWindowTests.Pump();
            input.MoveTo(start.X + (horizontal ? 60 : 0), start.Y - (horizontal ? 0 : 60));
            FaderInputWindowTests.Pump();
        }
        finally
        {
            input.SetButton(false);
            FaderInputWindowTests.Pump();
        }
        PixelPoint end = thumb.PointToScreen(centre);
        double moved = horizontal ? end.X - start.X : start.Y - end.Y;
        Assert.InRange(moved, 56, 64);
        Assert.True(slider.Value > .5, $"the drag left the fader at {slider.Value}");
        Assert.Equal(slider.Value, vm.Mixes[0].Volume);
    }

    /// <summary>Run what the layout posted, Fit's refit among it, until the window is still.</summary>
    private static void Settle(Window window)
    {
        for (int i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }
}
