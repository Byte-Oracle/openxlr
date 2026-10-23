using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

internal static class FaderInputWindowTests
{
    internal static void Check(MainWindow main)
    {
        FaderInputTests.StepsDoNotQuantizeIncomingValuesOrPluginControls();
        main.Activate();
        SkinEntry saved = SkinService.Current;
        try
        {
            foreach (string id in new[] { SkinPackage.DefaultId, "opendeck" })
            {
                SkinService.Apply(SkinCatalog.Find(id)!);
                WindowLayoutTests.Layout(main, 640, 900);
                var slider = main.FindControl<Slider>("OutputVolumeSlider")!;
                slider.BringIntoView();
                Pump();
                using var input = new XPointer();
                input.Focus(main.TryGetPlatformHandle()!.Handle);
                slider.Focus();
                slider.Value = .79;
                input.Key(0xff53); // Right
                Pump();
                Assert.Equal(.8, slider.Value, 10);
                input.Key(0xff55); // Page Up
                Pump();
                Assert.Equal(.9, slider.Value, 10);
                slider.Value = 1.49;
                input.Key(0xff53);
                input.Key(0xff53);
                Pump();
                Assert.Equal(1.5, slider.Value, 10);

                // Actual pointer travel through each control template.
                double fine = Drag(slider, input, fine: true);
                double normal = Drag(slider, input, fine: false);
                Assert.InRange(fine, .51, .59);
                Assert.True(normal - .5 > (fine - .5) * 5, $"Normal {normal}, fine {fine}.");
                Assert.Equal(Math.Round(fine * 100), fine * 100, 8);
                Drag(slider, input, fine: true, cancel: s =>
                {
                    s.IsEnabled = false;
                    Pump();
                    s.IsEnabled = true;
                });
                Drag(slider, input, fine: true, cancel: s =>
                {
                    var parent = (Panel)s.Parent!;
                    int index = parent.Children.IndexOf(s);
                    parent.Children.RemoveAt(index);
                    parent.Children.Insert(index, s);
                });
                var vm = (MainViewModel)main.DataContext!;
                Drag(slider, input, fine: true, cancel: _ => vm.OutputVolumeRange.Apply(false));
                Drag(slider, input, fine: true, cancel: _ => vm.OutputVolumeRange.Apply(true));
                Drag(slider, input, fine: true, cancel: s =>
                {
                    var parent = (Control)s.Parent!;
                    parent.IsVisible = false;
                    Pump();
                    parent.IsVisible = true;
                });
                Assert.InRange(Drag(slider, input, fine: true), .51, .59);
                CheckPointerOwnership(main, slider);
            }
        }
        finally { SkinService.Apply(saved); }
        CheckOrientations();
        CheckAdjusting();
    }

    /// <summary>
    /// A fader says it is being changed from the press until the release,
    /// through a drag, and for a moment after a key moves it.
    /// </summary>
    internal static void CheckAdjusting()
    {
        var window = new Window { Width = 400, Height = 200 };
        var slider = new Slider { Minimum = 0, Maximum = 1, Width = 200, Margin = new Thickness(40, 80), VerticalAlignment = VerticalAlignment.Top };
        FaderInput.SetStep(slider, 0.01);
        window.Content = slider;
        try
        {
            window.Show();
            WindowLayoutTests.Layout(window, 400, 200);
            slider.Value = .5;
            Pump();
            Assert.False(FaderInput.GetIsAdjusting(slider));
            using var input = new XPointer();
            input.Focus(window.TryGetPlatformHandle()!.Handle);
            var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
            PixelPoint point = thumb.PointToScreen(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2));
            input.MoveTo(point.X, point.Y);
            Pump();
            try
            {
                input.SetButton(true);
                Pump();
                Assert.True(FaderInput.GetIsAdjusting(slider));
                input.MoveTo(point.X + 60, point.Y);
                Pump();
                Assert.True(FaderInput.GetIsAdjusting(slider));
                Assert.True(slider.Value > .5);
            }
            finally
            {
                input.SetButton(false);
                Pump();
            }
            Assert.False(FaderInput.GetIsAdjusting(slider));

            slider.Focus();
            slider.Value = .5;
            Pump();
            input.Key(0xff53); // Right
            Pump();
            Assert.Equal(.51, slider.Value, 10);
            Assert.True(FaderInput.GetIsAdjusting(slider));
            for (int i = 0; i < 20 && FaderInput.GetIsAdjusting(slider); i++) Pump();
            Assert.False(FaderInput.GetIsAdjusting(slider));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// The faders a skin's strip templates draw: upright, and reversed in
    /// either orientation, in the flat appearance and in the console one. The
    /// top of the range sits where the direction says, the keyboard and the
    /// framework's drag move the way the fader runs, and the fine drag
    /// travels a tenth as far the same way.
    /// </summary>
    internal static void CheckOrientations()
    {
        SkinEntry saved = SkinService.Current;
        var window = new Window { Width = 400, Height = 420 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 40, Margin = new Thickness(40) };
        Slider Make(Orientation orientation, bool reversed)
        {
            var slider = new Slider
            {
                Minimum = 0, Maximum = 1, Orientation = orientation, IsDirectionReversed = reversed,
                Width = orientation == Orientation.Horizontal ? 200 : double.NaN, Height = orientation == Orientation.Vertical ? 200 : double.NaN,
                VerticalAlignment = VerticalAlignment.Top,
            };
            FaderInput.SetStep(slider, 0.01);
            row.Children.Add(slider);
            return slider;
        }
        Slider upright = Make(Orientation.Vertical, false);
        Slider uprightReversed = Make(Orientation.Vertical, true);
        Slider flatReversed = Make(Orientation.Horizontal, true);
        window.Content = row;
        try
        {
            window.Show();
            foreach (string id in new[] { SkinPackage.DefaultId, "opendeck" })
            {
                SkinService.Apply(SkinCatalog.Find(id)!);
                WindowLayoutTests.Layout(window, 400, 420);
                using var input = new XPointer();
                input.Focus(window.TryGetPlatformHandle()!.Handle);
                foreach ((Slider slider, int dx, int dy, bool increases) in new[]
                         {
                             (upright, 0, -60, true), (uprightReversed, 0, -60, false), (flatReversed, 60, 0, false),
                         })
                {
                    string what = $"{slider.Orientation}{(slider.IsDirectionReversed ? " reversed" : "")} in {id}";
                    var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
                    Assert.True(thumb.IsEffectivelyVisible, $"no cap on the {what} fader");
                    // The top of the range is at the top or the right, or opposite when reversed.
                    slider.Value = 1;
                    Pump();
                    Point top = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), slider)!.Value;
                    bool atStart = slider.Orientation == Orientation.Vertical ? top.Y < slider.Bounds.Height / 2 : top.X < slider.Bounds.Width / 2;
                    Assert.True(atStart == (slider.Orientation == Orientation.Vertical ? !slider.IsDirectionReversed : slider.IsDirectionReversed),
                        $"the {what} fader's maximum is at {top}");

                    slider.Focus();
                    slider.Value = .5;
                    Pump();
                    input.Key(slider.Orientation == Orientation.Vertical ? 0xff52UL : 0xff53UL); // Up or Right
                    Pump();
                    Assert.Equal(increases ? .51 : .49, slider.Value, 10);

                    double fine = DragBy(slider, input, true, dx, dy);
                    double normal = DragBy(slider, input, false, dx, dy);
                    if (increases)
                    {
                        Assert.InRange(fine, .51, .59);
                        Assert.True(normal - .5 > (fine - .5) * 5, $"{what}: normal {normal}, fine {fine}.");
                    }
                    else
                    {
                        Assert.InRange(fine, .41, .49);
                        Assert.True(.5 - normal > (.5 - fine) * 5, $"{what}: normal {normal}, fine {fine}.");
                    }
                    Assert.Equal(Math.Round(fine * 100), fine * 100, 8);
                }
            }
        }
        finally
        {
            window.Close();
            SkinService.Apply(saved);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>A drag from the cap's centre by the given distance, with Shift held for a fine one.</summary>
    private static double DragBy(Slider slider, XPointer input, bool fine, int dx, int dy)
    {
        slider.Value = .5;
        Pump();
        var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
        PixelPoint point = thumb.PointToScreen(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2));
        input.MoveTo(point.X, point.Y);
        if (fine) input.SetKey(0xffe1, true); // Shift
        try
        {
            Pump();
            input.SetButton(true);
            Pump();
            input.MoveTo(point.X + dx, point.Y + dy);
            Pump();
        }
        finally
        {
            input.SetButton(false);
            input.SetKey(0xffe1, false);
            Pump();
        }
        return slider.Value;
    }

    internal static double Drag(Slider slider, XPointer input, bool fine, Action<Slider>? cancel = null)
    {
        slider.Value = .5;
        Pump();
        var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
        PixelPoint point = thumb.PointToScreen(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2));
        input.MoveTo(point.X, point.Y);
        if (fine) input.SetKey(0xffe1, true); // Shift
        try
        {
            Pump();
            input.SetButton(true);
            Pump();
            input.MoveTo(point.X + 60, point.Y);
            Pump();
            if (cancel is not null)
            {
                cancel(slider);
                Pump();
                double stopped = slider.Value;
                input.MoveTo(point.X + 120, point.Y);
                Pump();
                Assert.Equal(stopped, slider.Value);
            }
        }
        finally
        {
            input.SetButton(false);
            input.SetKey(0xffe1, false);
            Pump();
        }
        return slider.Value;
    }

    internal static void Pump()
    {
        using var stop = new CancellationTokenSource();
        DispatcherTimer.RunOnce(stop.Cancel, TimeSpan.FromMilliseconds(100));
        Dispatcher.UIThread.MainLoop(stop.Token);
    }

    private static void CheckPointerOwnership(Window window, Slider slider)
    {
        // Logical touch pointers check ownership separately from the XTEST mouse.
        using var first = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true);
        using var second = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, false);
        var properties = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        Point origin = slider.TranslatePoint(new Point(slider.Bounds.Width / 2, slider.Bounds.Height / 2), window)!.Value;
        void Press(IPointer pointer) => slider.RaiseEvent(new PointerPressedEventArgs(
            slider, pointer, window, origin, 0, properties, KeyModifiers.Shift));
        void Move(double distance) => slider.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, slider, first, window, origin + new Vector(distance, 0),
            0, properties, KeyModifiers.Shift));

        slider.Value = .5;
        Press(first);
        Assert.Same(slider, first.Captured);
        Press(second);
        Assert.Null(second.Captured);
        slider.RaiseEvent(new PointerCaptureLostEventArgs(slider, second));
        Move(60);
        Assert.InRange(slider.Value, .51, .59);

        double stopped = slider.Value;
        var next = (InputElement)slider.Parent!;
        first.Capture(next);
        Assert.Same(next, first.Captured);
        Move(120);
        Assert.Equal(stopped, slider.Value);
        first.Capture(null);
        Press(first);
        Move(60);
        Assert.True(slider.Value > stopped);
    }
}
