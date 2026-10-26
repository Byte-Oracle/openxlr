using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI;

/// <summary>
/// A Sound Check take as Mic setup draws it: one bar per peak the daemon
/// sent, mirrored about the centre line on a linear scale, and a playhead
/// while the take loops. It draws in code with the EQ graph's flat colours,
/// so a skin that tints the graph tints this too.
/// </summary>
public sealed class TakeWaveform : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> PeaksProperty =
        AvaloniaProperty.Register<TakeWaveform, IReadOnlyList<double>?>(nameof(Peaks));

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<TakeWaveform, double>(nameof(Position), -1);

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<TakeWaveform, CornerRadius>(nameof(CornerRadius));

    static TakeWaveform()
    {
        AffectsRender<TakeWaveform>(PeaksProperty, PositionProperty, CornerRadiusProperty);
    }

    /// <summary>The take's peaks in order, each 0 to 1; null or empty draws the ground alone.</summary>
    public IReadOnlyList<double>? Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }

    /// <summary>Where the loop is, 0 to 1 across the take; below 0 draws no playhead.</summary>
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }

    public CornerRadius CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SkinService.Changed += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SkinService.Changed -= InvalidateVisual;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        context.DrawRectangle(SkinService.LiveBrush("Ox.Eq.Background"), null, new RoundedRect(bounds, CornerRadius));
        double middle = Math.Round(bounds.Height / 2) + 0.5;
        context.DrawLine(new Pen(SkinService.LiveBrush("Ox.Eq.Grid"), 1), new Point(0, middle), new Point(bounds.Width, middle));

        IReadOnlyList<double>? peaks = Peaks;
        if (peaks is { Count: > 0 })
        {
            double step = bounds.Width / peaks.Count, half = bounds.Height / 2 - 2;
            IBrush fill = SkinService.LiveBrush("Ox.Eq.CurveFill");
            var outline = new Pen(SkinService.LiveBrush("Ox.Eq.Curve"), 1);
            for (int i = 0; i < peaks.Count; i++)
            {
                double reach = Math.Clamp(peaks[i], 0, 1) * half;
                if (reach < 0.5) continue;
                var bar = new Rect(i * step, middle - reach, Math.Max(1, step - 1), 2 * reach);
                context.DrawRectangle(fill, null, bar);
                context.DrawLine(outline, bar.TopLeft, bar.TopRight);
                context.DrawLine(outline, bar.BottomLeft, bar.BottomRight);
            }
        }

        double position = Position;
        if (position >= 0 && position <= 1)
        {
            double x = Math.Round(position * (bounds.Width - 2)) + 1;
            context.DrawLine(new Pen(SkinService.LiveBrush("Ox.Eq.Curve"), 2), new Point(x, 0), new Point(x, bounds.Height));
        }
    }
}
