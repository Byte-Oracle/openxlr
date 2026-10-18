using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace OpenXLR.UI.Layout;

/// <summary>
/// Lays out the named parts of a strip template in the order its
/// <see cref="StripSpec"/> gives. Each part is a child that says which part
/// it is through <see cref="PartProperty"/>; the template supplies all of
/// them and this panel only arranges them, so a skin reorders and leaves out
/// parts without supplying any markup. A part the spec leaves out is made
/// invisible, which takes it out of the tab order too, and comes back when a
/// later spec lists it.
///
/// Parts follow one another along <see cref="StripSpec.Flow"/>. In a column
/// each part takes the column's width; in a row the fader, and a flat meter,
/// share what the other parts leave. A meter placed beside another part
/// stands to that part's right, in its place.
/// </summary>
public sealed class StripPanel : Panel
{
    public static readonly AttachedProperty<string?> PartProperty =
        AvaloniaProperty.RegisterAttached<StripPanel, Control, string?>("Part");

    public static readonly StyledProperty<StripSpec?> SpecProperty =
        AvaloniaProperty.Register<StripPanel, StripSpec?>(nameof(Spec));

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<StripPanel, double>(nameof(Spacing), 6);

    static StripPanel()
    {
        AffectsMeasure<StripPanel>(SpecProperty, SpacingProperty);
    }

    public static string? GetPart(Control control) => control.GetValue(PartProperty);

    public static void SetPart(Control control, string? part) => control.SetValue(PartProperty, part);

    public StripSpec? Spec { get => GetValue(SpecProperty); set => SetValue(SpecProperty, value); }

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    /// <summary>One place in the flow: a part, and the meter beside it when the spec puts it there.</summary>
    private readonly record struct Slot(Control Part, Control? Beside);

    private List<Slot> _slots = [];

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SpecProperty) UpdateParts();
    }

    protected override void ChildrenChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        base.ChildrenChanged(sender, e);
        UpdateParts();
    }

    /// <summary>Show the parts the spec lists and hide the rest, and work out their places.</summary>
    private void UpdateParts()
    {
        StripSpec? spec = Spec;
        var byPart = new Dictionary<string, Control>(StringComparer.Ordinal);
        foreach (Control child in Children)
            if (GetPart(child) is { } part) byPart.TryAdd(part, child);
        var slots = new List<Slot>();
        var shown = new HashSet<Control>();
        if (spec is not null)
        {
            string? anchor = spec.MeterAnchor;
            byPart.TryGetValue(Skinning.SkinStripParts.Meter, out Control? meter);
            bool meterBeside = anchor is not null && meter is not null && spec.Parts.Contains(Skinning.SkinStripParts.Meter)
                && byPart.ContainsKey(anchor);
            foreach (string part in spec.Parts)
            {
                if (!byPart.TryGetValue(part, out Control? control)) continue;
                if (meterBeside && part == Skinning.SkinStripParts.Meter) continue;
                Control? beside = meterBeside && part == anchor ? meter : null;
                if (beside is not null && spec.Flow == Orientation.Horizontal)
                {
                    // In a row a part beside another is simply the next one.
                    slots.Add(new Slot(control, null));
                    slots.Add(new Slot(beside, null));
                }
                else slots.Add(new Slot(control, beside));
                shown.Add(control);
                if (beside is not null) shown.Add(beside);
            }
        }
        foreach (Control child in Children)
            if (GetPart(child) is not null) child.SetCurrentValue(IsVisibleProperty, shown.Contains(child));
        _slots = slots;
        InvalidateMeasure();
    }

    private bool Stretches(Control part) =>
        Spec is { Flow: Orientation.Horizontal } spec
        && (GetPart(part) == Skinning.SkinStripParts.Fader && spec.FaderOrientation == Orientation.Horizontal
            || GetPart(part) == Skinning.SkinStripParts.Gain
            || GetPart(part) == Skinning.SkinStripParts.Meter && spec.MeterOrientation == Orientation.Horizontal);

    protected override Size MeasureOverride(Size availableSize)
    {
        double spacing = Spacing;
        if (Spec is not { } spec || _slots.Count == 0) return default;
        if (spec.Flow == Orientation.Vertical)
        {
            double width = 0, height = 0;
            foreach (Slot slot in _slots)
            {
                Size beside = default;
                if (slot.Beside is { } b)
                {
                    b.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                    beside = b.DesiredSize;
                }
                double room = Math.Max(0, availableSize.Width - (slot.Beside is null ? 0 : beside.Width + spacing));
                slot.Part.Measure(new Size(room, availableSize.Height));
                Size part = slot.Part.DesiredSize;
                if (slot.Beside is { } again && part.Height > beside.Height)
                {
                    // A meter beside a part is as tall as that part.
                    again.Measure(new Size(double.PositiveInfinity, part.Height));
                    beside = again.DesiredSize;
                }
                width = Math.Max(width, part.Width + (slot.Beside is null ? 0 : beside.Width + spacing));
                height += Math.Max(part.Height, beside.Height);
            }
            height += spacing * (_slots.Count - 1);
            return new Size(width, height);
        }
        else
        {
            double fixedWidth = 0, height = 0;
            var stretching = new List<Control>();
            foreach (Slot slot in _slots)
            {
                if (Stretches(slot.Part)) { stretching.Add(slot.Part); continue; }
                slot.Part.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                fixedWidth += slot.Part.DesiredSize.Width;
                height = Math.Max(height, slot.Part.DesiredSize.Height);
            }
            double gaps = spacing * (_slots.Count - 1);
            double left = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity
                : Math.Max(0, availableSize.Width - fixedWidth - gaps);
            double stretchWidth = 0;
            foreach (Control part in stretching)
            {
                part.Measure(new Size(double.IsInfinity(left) ? double.PositiveInfinity : Share(part, left, stretching), availableSize.Height));
                stretchWidth += part.DesiredSize.Width;
                height = Math.Max(height, part.DesiredSize.Height);
            }
            return new Size(fixedWidth + stretchWidth + gaps, height);
        }
    }

    /// <summary>A fader takes two thirds of what is left when a flat meter shares the row with it, all of it otherwise.</summary>
    private double Share(Control part, double left, List<Control> stretching)
    {
        if (stretching.Count == 1) return left;
        bool meter = GetPart(part) == Skinning.SkinStripParts.Meter;
        return meter ? left / 3 : left * 2 / 3 / Math.Max(1, stretching.Count - 1);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double spacing = Spacing;
        if (Spec is not { } spec) return finalSize;
        if (spec.Flow == Orientation.Vertical)
        {
            double y = 0;
            foreach (Slot slot in _slots)
            {
                double besideWidth = slot.Beside?.DesiredSize.Width ?? 0;
                double rowHeight = Math.Max(slot.Part.DesiredSize.Height, slot.Beside?.DesiredSize.Height ?? 0);
                double partWidth = Math.Max(0, finalSize.Width - (slot.Beside is null ? 0 : besideWidth + spacing));
                if (slot.Beside is { } beside)
                {
                    // The part and its meter, centred together across the column.
                    double together = Math.Min(finalSize.Width, slot.Part.DesiredSize.Width + spacing + besideWidth);
                    // An upright fader keeps its own width so the pair stands centred; anything else
                    // takes the column's width less the meter's.
                    bool stretch = !(GetPart(slot.Part) == Skinning.SkinStripParts.Fader && spec.FaderUpright)
                        && slot.Part.HorizontalAlignment == HorizontalAlignment.Stretch;
                    double x = stretch ? 0 : Math.Max(0, (finalSize.Width - together) / 2);
                    double width = stretch ? partWidth : slot.Part.DesiredSize.Width;
                    slot.Part.Arrange(new Rect(x, y, width, rowHeight));
                    beside.Arrange(new Rect(x + width + spacing, y, besideWidth, rowHeight));
                }
                else slot.Part.Arrange(new Rect(0, y, finalSize.Width, rowHeight));
                y += rowHeight + spacing;
            }
        }
        else
        {
            double fixedWidth = _slots.Where(s => !Stretches(s.Part)).Sum(s => s.Part.DesiredSize.Width);
            List<Control> stretching = [.. _slots.Select(s => s.Part).Where(Stretches)];
            double left = Math.Max(0, finalSize.Width - fixedWidth - spacing * (_slots.Count - 1));
            double x = 0;
            foreach (Slot slot in _slots)
            {
                double width = Stretches(slot.Part) ? Share(slot.Part, left, stretching) : slot.Part.DesiredSize.Width;
                slot.Part.Arrange(new Rect(x, 0, width, finalSize.Height));
                x += width + spacing;
            }
        }
        return finalSize;
    }
}
