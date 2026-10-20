using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace OpenXLR.UI.Layout;

/// <summary>
/// APPLICATIONS' <c>"application-columns"</c> template: the chips in
/// columns of at most <see cref="PerColumn"/>, top to bottom, the next chip
/// starting the next column to the right. Each chip keeps the size it asks
/// for, which is the size the classic wrap gives it, since both draw the same
/// chip template; a column is as wide as its widest chip. A column that does
/// not fit beside the ones before it goes on below them, whole, so the order
/// stays column by column and nothing scrolls sideways.
/// </summary>
public sealed class ApplicationColumnsPanel : Panel
{
    public static readonly StyledProperty<int> PerColumnProperty =
        AvaloniaProperty.Register<ApplicationColumnsPanel, int>(nameof(PerColumn), Skinning.SkinFormat.DefaultApplicationsPerColumn,
            coerce: (_, value) => Math.Max(1, value));

    static ApplicationColumnsPanel() => AffectsMeasure<ApplicationColumnsPanel>(PerColumnProperty);

    /// <summary>How many chips one column holds.</summary>
    public int PerColumn
    {
        get => GetValue(PerColumnProperty);
        set => SetValue(PerColumnProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var constraint = new Size(availableSize.Width, double.PositiveInfinity);
        foreach (Control child in Children) child.Measure(constraint);
        return Place(availableSize.Width, null);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Place(finalSize.Width, finalSize.Width);
        return finalSize;
    }

    /// <summary>
    /// Lays the chips out in columns and the columns in bands as wide as
    /// <paramref name="width"/>, arranging each chip when <paramref name="arrange"/>
    /// gives the width to arrange in; returns the size the whole takes.
    /// </summary>
    private Size Place(double width, double? arrange)
    {
        var shown = new List<Control>();
        foreach (Control child in Children)
            if (child.IsVisible) shown.Add(child);
        int perColumn = PerColumn;
        double x = 0, bandTop = 0, bandHeight = 0, widest = 0;
        for (int start = 0; start < shown.Count; start += perColumn)
        {
            int end = Math.Min(start + perColumn, shown.Count);
            double columnWidth = 0, columnHeight = 0;
            for (int i = start; i < end; i++)
            {
                columnWidth = Math.Max(columnWidth, shown[i].DesiredSize.Width);
                columnHeight += shown[i].DesiredSize.Height;
            }
            // A column that would run past the width starts a band below; the
            // first column of a band stays, however wide it is.
            if (x > 0 && x + columnWidth > width)
            {
                bandTop += bandHeight;
                x = 0;
                bandHeight = 0;
            }
            if (arrange is { } room)
            {
                double y = bandTop;
                for (int i = start; i < end; i++)
                {
                    Size desired = shown[i].DesiredSize;
                    shown[i].Arrange(new Rect(x, y, Math.Min(desired.Width, Math.Max(0, room - x)), desired.Height));
                    y += desired.Height;
                }
            }
            x += columnWidth;
            widest = Math.Max(widest, x);
            bandHeight = Math.Max(bandHeight, columnHeight);
        }
        return new Size(widest, bandTop + bandHeight);
    }
}
