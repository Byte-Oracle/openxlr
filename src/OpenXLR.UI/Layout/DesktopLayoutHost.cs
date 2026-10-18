using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI.Layout;

/// <summary>
/// Whether a skin lets a region show. The window's own reasons to set a
/// section aside, the mini view or a missing software mixer, are bound
/// beside it in MainWindow.axaml, and a section shows only when both agree,
/// so a skin can hide a section but never bring one back the window hides.
/// </summary>
public sealed class RegionHost
{
    private RegionHost() { }

    public static readonly AttachedProperty<bool> ShownProperty =
        AvaloniaProperty.RegisterAttached<RegionHost, Control, bool>("Shown", true);

    public static bool GetShown(Control control) => control.GetValue(ShownProperty);

    public static void SetShown(Control control, bool shown) => control.SetValue(ShownProperty, shown);
}

/// <summary>
/// Builds the main window's tree from a <see cref="SectionArrangement"/>:
/// a <see cref="Grid"/>, <see cref="DockPanel"/> or <see cref="StackPanel"/>
/// for each container, and the window's own region hosts, the header card
/// and the five section cards, placed in them. Nothing else is ever made
/// from a skin. The hosts are never recreated: a new tree takes them from
/// wherever they stand, with the same contents and the same data context,
/// and a top-level stack reuses the panel the window was drawn with.
///
/// Every region keeps the width it needs, <see cref="SkinLayoutDefaults.RowSectionMinWidth"/>,
/// wherever its container gives it a width of its own; when the tree needs
/// more than the window has, it scrolls sideways instead of squeezing a
/// region. Heights follow the contents, since the page scrolls downwards.
/// </summary>
internal sealed class DesktopLayoutHost
{
    private readonly IReadOnlyDictionary<string, Control> _hosts;
    private readonly Control _header;
    private readonly Panel _page;
    private readonly StackPanel _classic;
    private readonly ScrollViewer _scroll;
    private readonly Thickness _headerMargin;
    private SectionArrangement? _arrangement;
    private Dictionary<string, Panel> _panels = new(StringComparer.Ordinal);
    private Dictionary<SkinLayoutChild, LayoutSlot> _slots = new(ReferenceEqualityComparer.Instance);

    /// <param name="hosts">Each region's host, by region id: the header card and the five section cards.</param>
    /// <param name="page">The page the header heads when the tree leaves it out.</param>
    /// <param name="classic">The panel the sections were drawn in, reused for a top-level stack.</param>
    /// <param name="scroll">The scroller the tree sits in, which scrolls it sideways when it is wider than the window.</param>
    internal DesktopLayoutHost(IReadOnlyDictionary<string, Control> hosts, Panel page, StackPanel classic, ScrollViewer scroll)
    {
        _hosts = hosts;
        _header = hosts[SkinLayoutRegions.Header];
        _page = page;
        _classic = classic;
        _scroll = scroll;
        _headerMargin = _header.Margin;
        _panels[SkinLayoutDefaults.ClassicId] = classic;
    }

    /// <summary>The panel at the top of the tree.</summary>
    internal Panel Root { get; private set; } = null!;

    /// <summary>The panel built for a container, by its id.</summary>
    internal Panel PanelFor(string container) => _panels[container];

    /// <summary>A region's host.</summary>
    internal Control HostFor(string region) => _hosts[region];

    /// <summary>
    /// Stand the regions as <paramref name="arrangement"/> says. Hosts that
    /// keep their panel only change place in it; the others are moved with
    /// their data context held, so no binding inside them sees it change, and
    /// focus inside a moved host comes back to the same control.
    /// </summary>
    internal void Apply(SectionArrangement arrangement)
    {
        SkinLayoutContainer tree = arrangement.Tree;
        // A container keeps its panel while its id and kind stay, so a move
        // inside it only reorders the panel's children.
        var panels = new Dictionary<string, Panel>(StringComparer.Ordinal);
        foreach (SkinLayoutContainer container in tree.Containers())
            panels[container.Id] = ReferenceEquals(container, tree) && container is SkinLayoutStack ? _classic
                : _panels.TryGetValue(container.Id, out Panel? kept) && !ReferenceEquals(kept, _classic) && Fits(container, kept) ? kept
                : Create(container);
        foreach (SkinLayoutContainer container in tree.Containers()) Configure(container, panels[container.Id]);
        var slots = new Dictionary<SkinLayoutChild, LayoutSlot>(ReferenceEqualityComparer.Instance);
        foreach (LayoutSlot slot in arrangement.Slots) slots[slot.Child] = slot;

        // Where every host and panel goes, in placement order.
        var desired = new Dictionary<Panel, List<Control>>();
        var target = new Dictionary<Control, Panel>();
        foreach (SkinLayoutContainer container in tree.Containers())
        {
            Panel panel = panels[container.Id];
            var children = new List<Control>();
            foreach (SkinLayoutChild child in container.Placed)
            {
                Control control = child.Node is SkinLayoutContainer inner
                    ? panels[inner.Id] : _hosts[arrangement.Placement[slots[child].Id]];
                if (container is SkinLayoutGrid)
                {
                    Grid.SetRow(control, child.Row);
                    Grid.SetColumn(control, child.Column);
                    Grid.SetRowSpan(control, child.RowSpan);
                    Grid.SetColumnSpan(control, child.ColumnSpan);
                }
                if (container is SkinLayoutDock)
                    DockPanel.SetDock(control, child.Edge switch
                    {
                        SkinLayoutEdge.Top => Dock.Top,
                        SkinLayoutEdge.Bottom => Dock.Bottom,
                        SkinLayoutEdge.Right => Dock.Right,
                        _ => Dock.Left,
                    });
                children.Add(control);
                target[control] = panel;
            }
            desired[panel] = children;
        }
        if (!arrangement.HeaderInTree) target[_header] = _page;

        Control? focused = TopLevel.GetTopLevel(_page)?.FocusManager?.GetFocusedElement() as Control;
        bool refocus = focused is not null && _hosts.Values.Any(h => focused.GetSelfAndVisualAncestors().Contains(h));
        object? context = _page.DataContext;
        foreach (Control host in _hosts.Values) host.DataContext = context;
        try
        {
            Panel root = panels[tree.Id];
            if (_scroll.Content is Control shown && !ReferenceEquals(shown, root)) _scroll.Content = null;
            if (root.Parent is Panel above) above.Children.Remove(root);
            foreach (Control control in _hosts.Values.Concat(target.Keys).Distinct())
                if (control.Parent is Panel parent && (!target.TryGetValue(control, out Panel? goes) || !ReferenceEquals(parent, goes)))
                    parent.Children.Remove(control);
            foreach (Panel old in _panels.Values.Where(p => !ReferenceEquals(p, _classic) && !panels.ContainsValue(p)))
                old.Children.Clear();
            if (!panels.ContainsValue(_classic)) _classic.Children.Clear();
            foreach ((Panel panel, List<Control> children) in desired) SetChildren(panel, children);

            Root = root;
            if (!ReferenceEquals(_scroll.Content, Root)) _scroll.Content = Root;
            if (arrangement.HeaderInTree)
                _header.Margin = default;
            else
            {
                _header.Margin = _headerMargin;
                if (_header.Parent is null) _page.Children.Insert(0, _header);
            }
        }
        finally
        {
            foreach (Control host in _hosts.Values) host.ClearValue(StyledElement.DataContextProperty);
        }
        foreach (string region in SkinLayoutRegions.Sections)
            RegionHost.SetShown(_hosts[region], !arrangement.Hidden.Contains(region, StringComparer.Ordinal));

        _arrangement = arrangement;
        _panels = panels;
        _slots = slots;
        UpdateWidths();
        if (refocus && focused is { IsEffectivelyVisible: true } && focused.IsAttachedToVisualTree()) focused.Focus();
    }

    private static bool Fits(SkinLayoutContainer container, Panel panel) => container switch
    {
        SkinLayoutGrid => panel is Grid,
        SkinLayoutDock => panel is DockPanel,
        _ => panel is StackPanel,
    };

    private static Panel Create(SkinLayoutContainer container) => container switch
    {
        SkinLayoutGrid => new Grid(),
        SkinLayoutDock => new DockPanel { LastChildFill = true },
        _ => new StackPanel(),
    };

    /// <summary>A container's own settings: direction and gap, tracks, edges.</summary>
    private static void Configure(SkinLayoutContainer container, Panel panel)
    {
        switch (container, panel)
        {
            case (SkinLayoutStack stack, StackPanel stackPanel):
                stackPanel.Orientation = stack.Orientation == SkinLayoutOrientation.Horizontal ? Orientation.Horizontal : Orientation.Vertical;
                stackPanel.Spacing = stack.Spacing;
                break;
            case (SkinLayoutGrid grid, Grid gridPanel):
                gridPanel.RowSpacing = grid.Spacing;
                gridPanel.ColumnSpacing = grid.Spacing;
                // The page scrolls downwards, so a row is as tall as what it
                // holds: a fixed size is the least it takes, and a star row
                // takes its contents' height like an automatic one.
                gridPanel.RowDefinitions = new RowDefinitions();
                foreach (SkinLayoutTrack row in grid.Rows)
                    gridPanel.RowDefinitions.Add(row.Unit switch
                    {
                        SkinTrackUnit.Star => new RowDefinition(row.Value, GridUnitType.Star),
                        SkinTrackUnit.Pixel => new RowDefinition(GridLength.Auto) { MinHeight = row.Value },
                        _ => new RowDefinition(GridLength.Auto),
                    });
                gridPanel.ColumnDefinitions = new ColumnDefinitions();
                foreach (SkinLayoutTrack column in grid.Columns)
                    gridPanel.ColumnDefinitions.Add(new ColumnDefinition(Length(column, 0)));
                break;
            case (SkinLayoutDock dock, DockPanel dockPanel):
                dockPanel.HorizontalSpacing = dock.Spacing;
                dockPanel.VerticalSpacing = dock.Spacing;
                break;
        }
    }

    private static GridLength Length(SkinLayoutTrack track, double minimum) => track.Unit switch
    {
        SkinTrackUnit.Star => new GridLength(track.Value, GridUnitType.Star),
        SkinTrackUnit.Pixel => new GridLength(Math.Max(track.Value, minimum), GridUnitType.Pixel),
        _ => GridLength.Auto,
    };

    /// <summary>Make a panel hold exactly these children in this order, moving the ones it has rather than re-adding them.</summary>
    internal static void SetChildren(Panel panel, IReadOnlyList<Control> children)
    {
        for (int i = 0; i < children.Count; i++)
        {
            int at = panel.Children.IndexOf(children[i]);
            if (at == i) continue;
            if (at >= 0) panel.Children.Move(at, i);
            else panel.Children.Insert(i, children[i]);
        }
        while (panel.Children.Count > children.Count) panel.Children.RemoveAt(children.Count);
    }

    // ---- widths ----

    private sealed record Sizing(double Width = double.NaN, HorizontalAlignment? Horizontal = null,
        VerticalAlignment? Vertical = null, double MinHeight = double.NaN);

    private readonly Dictionary<Control, Sizing> _sizes = new();

    /// <summary>
    /// Give every region and container the width its place calls for, and
    /// scroll the tree sideways when it needs more than the window has. Run
    /// after each arrangement, when the window's width changes and when a
    /// section shows or hides.
    /// </summary>
    internal void UpdateWidths()
    {
        if (_arrangement is not { } arrangement) return;
        _sizes.Clear();
        SkinLayoutContainer tree = arrangement.Tree;
        double viewport = Math.Max(0, _scroll.Bounds.Width);
        bool scrolls;
        if (tree is SkinLayoutStack { Orientation: SkinLayoutOrientation.Horizontal } row)
        {
            // A row shares the window's width, each region at least as wide as it needs.
            scrolls = true;
            Assign(row, viewport);
        }
        else
        {
            // A column, as the classic window, keeps the window's width;
            // only a container inside the tree can ask for more.
            double need = tree is SkinLayoutStack
                ? tree.Placed.Where(c => c.Node is SkinLayoutContainer).Select(c => Need(c, tree)).DefaultIfEmpty(0).Max()
                : NeedOf(tree);
            scrolls = need > viewport + 0.5;
            if (scrolls) _sizes[Root] = new Sizing(need);
            Assign(tree, Math.Max(viewport, need));
        }
        _scroll.HorizontalScrollBarVisibility = scrolls ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        // Room under the tree for the sideways scroll bar, which is drawn over the content.
        Root.Margin = scrolls ? new Thickness(0, 0, 0, 12) : default;

        foreach (Control control in _hosts.Values.Where(h => !ReferenceEquals(h, _header) || arrangement.HeaderInTree)
                     .Concat(_panels.Values))
        {
            Sizing size = _sizes.TryGetValue(control, out Sizing? s) ? s : new Sizing();
            Set(control, Layoutable.WidthProperty, size.Width);
            Set(control, Layoutable.MinHeightProperty, size.MinHeight);
            if (size.Horizontal is { } horizontal) control.HorizontalAlignment = horizontal;
            else control.ClearValue(Layoutable.HorizontalAlignmentProperty);
            if (size.Vertical is { } vertical) control.VerticalAlignment = vertical;
            else control.ClearValue(Layoutable.VerticalAlignmentProperty);
        }
        if (!arrangement.HeaderInTree)
        {
            _header.ClearValue(Layoutable.WidthProperty);
            _header.ClearValue(Layoutable.HorizontalAlignmentProperty);
            _header.ClearValue(Layoutable.VerticalAlignmentProperty);
        }
    }

    private static void Set(Control control, AvaloniaProperty<double> property, double value)
    {
        if (double.IsNaN(value)) control.ClearValue(property);
        else control.SetValue(property, value);
    }

    private string RegionOf(SkinLayoutChild child) => _arrangement!.Placement[_slots[child].Id];

    private Control ControlOf(SkinLayoutChild child) =>
        child.Node is SkinLayoutContainer inner ? _panels[inner.Id] : _hosts[RegionOf(child)];

    /// <summary>False for a region the window or the skin is not showing; containers always count.</summary>
    private bool Shows(SkinLayoutChild child) => child.Node is SkinLayoutContainer || _hosts[RegionOf(child)].IsVisible;

    /// <summary>True where the container gives a child a width of its own, so a preferred width means something.</summary>
    private static bool OwnWidth(SkinLayoutContainer parent, SkinLayoutChild child) => parent switch
    {
        SkinLayoutStack stack => stack.Orientation == SkinLayoutOrientation.Horizontal,
        SkinLayoutDock => child.Edge is SkinLayoutEdge.Left or SkinLayoutEdge.Right,
        _ => true,
    };

    /// <summary>A region's preferred width where its place honours one, raised to what it needs.</summary>
    private double RegionWidth(SkinLayoutContainer parent, SkinLayoutChild child)
    {
        double? preferred = OwnWidth(parent, child) ? _arrangement!.OptionsFor(RegionOf(child)).Width : null;
        return Math.Max(SkinLayoutDefaults.RowSectionMinWidth, preferred ?? 0);
    }

    /// <summary>The width a child needs to show without squeezing anything in it.</summary>
    private double Need(SkinLayoutChild child, SkinLayoutContainer parent)
    {
        if (child.Node is SkinLayoutContainer inner) return NeedOf(inner);
        return Shows(child) ? RegionWidth(parent, child) : 0;
    }

    private double NeedOf(SkinLayoutContainer container)
    {
        switch (container)
        {
            case SkinLayoutStack { Orientation: SkinLayoutOrientation.Horizontal }:
            {
                SkinLayoutChild[] shown = [.. container.Placed.Where(Shows)];
                return shown.Sum(c => Need(c, container)) + container.Spacing * Math.Max(0, shown.Length - 1);
            }
            case SkinLayoutStack:
                return container.Placed.Select(c => Need(c, container)).DefaultIfEmpty(0).Max();
            case SkinLayoutGrid grid:
                return ColumnMinimums(grid).Sum() + grid.Spacing * (grid.Columns.Count - 1);
            default:
            {
                // From the filling child outwards: a side adds its width, a top or a bottom spans what is inside it.
                double need = 0;
                for (int i = container.Placed.Count - 1; i >= 0; i--)
                {
                    SkinLayoutChild child = container.Placed[i];
                    double width = Need(child, container);
                    need = child.Edge switch
                    {
                        SkinLayoutEdge.Fill => width,
                        SkinLayoutEdge.Left or SkinLayoutEdge.Right => need + width + (width > 0 ? container.Spacing : 0),
                        _ => Math.Max(need, width),
                    };
                }
                return need;
            }
        }
    }

    /// <summary>
    /// The least width of each grid column: a fixed column's size, and
    /// enough for every child it holds, a child across several columns
    /// sharing what it lacks among the ones that can grow.
    /// </summary>
    private double[] ColumnMinimums(SkinLayoutGrid grid)
    {
        double[] minimums = [.. grid.Columns.Select(c => c.Unit == SkinTrackUnit.Pixel ? c.Value : 0)];
        foreach (SkinLayoutChild child in grid.Placed.OrderBy(c => c.ColumnSpan))
        {
            double need = Need(child, grid);
            int[] columns = [.. Enumerable.Range(child.Column, child.ColumnSpan)];
            double have = columns.Sum(i => minimums[i]) + grid.Spacing * (columns.Length - 1);
            if (have >= need) continue;
            int[] growing = [.. columns.Where(i => grid.Columns[i].Unit != SkinTrackUnit.Pixel)];
            if (growing.Length == 0) growing = columns;
            foreach (int i in growing) minimums[i] += (need - have) / growing.Length;
        }
        return minimums;
    }

    /// <summary>
    /// Set the widths inside a container that is <paramref name="width"/>
    /// wide, or of unknown width inside a grid's growing cell. A child that
    /// would otherwise be measured without a width, in a row, an automatic
    /// column or a dock's side, is given one.
    /// </summary>
    private void Assign(SkinLayoutContainer container, double? width)
    {
        switch (container)
        {
            case SkinLayoutStack { Orientation: SkinLayoutOrientation.Horizontal }:
            {
                SkinLayoutChild[] shown = [.. container.Placed.Where(Shows)];
                Dictionary<SkinLayoutChild, double> widths = Share(container, shown, width);
                foreach (SkinLayoutChild child in container.Placed)
                {
                    double w = widths.TryGetValue(child, out double given) ? given : Need(child, container);
                    Give(child, container, w, null, VerticalAlignment.Top);
                }
                break;
            }
            case SkinLayoutStack:
                foreach (SkinLayoutChild child in container.Placed)
                    Give(child, container, double.NaN, null, null, width);
                break;
            case SkinLayoutGrid grid:
            {
                double[] minimums = ColumnMinimums(grid);
                if (_panels[grid.Id] is Grid panel)
                    for (int i = 0; i < grid.Columns.Count; i++)
                    {
                        ColumnDefinition column = panel.ColumnDefinitions[i];
                        GridLength length = Length(grid.Columns[i], minimums[i]);
                        if (!column.Width.Equals(length)) column.Width = length;
                        double minimum = grid.Columns[i].Unit == SkinTrackUnit.Pixel ? 0 : minimums[i];
                        if (!column.MinWidth.Equals(minimum)) column.MinWidth = minimum;
                    }
                foreach (SkinLayoutChild child in grid.Placed)
                {
                    bool automatic = Enumerable.Range(child.Column, child.ColumnSpan)
                        .Any(i => grid.Columns[i].Unit == SkinTrackUnit.Auto);
                    double? preferred = child.Node is SkinLayoutRegion ? _arrangement!.OptionsFor(RegionOf(child)).Width : null;
                    if (automatic || preferred is not null)
                        Give(child, grid, Need(child, grid), HorizontalAlignment.Left, VerticalAlignment.Top);
                    else
                        Give(child, grid, double.NaN, null, VerticalAlignment.Top, null);
                }
                break;
            }
            default:
            {
                double sides = 0;
                foreach (SkinLayoutChild child in container.Placed)
                {
                    if (child.Edge is SkinLayoutEdge.Left or SkinLayoutEdge.Right)
                    {
                        double w = Need(child, container);
                        sides += w + (w > 0 ? container.Spacing : 0);
                        Give(child, container, w, null, VerticalAlignment.Top);
                    }
                    else if (child.Edge == SkinLayoutEdge.Fill)
                        Give(child, container, double.NaN, null, VerticalAlignment.Top, width - sides);
                    else
                        Give(child, container, double.NaN, null, null, width);
                }
                break;
            }
        }
    }

    /// <summary>
    /// Shares a row's width among its shown children: a region with a
    /// preferred width keeps it, a child that needs more than an equal share
    /// keeps what it needs, and the rest split what is left. Without a known
    /// width every child takes what it needs.
    /// </summary>
    private Dictionary<SkinLayoutChild, double> Share(SkinLayoutContainer row, SkinLayoutChild[] shown, double? width)
    {
        var widths = new Dictionary<SkinLayoutChild, double>(ReferenceEqualityComparer.Instance);
        if (width is not { } total)
        {
            foreach (SkinLayoutChild child in shown) widths[child] = Need(child, row);
            return widths;
        }
        double left = total - row.Spacing * Math.Max(0, shown.Length - 1);
        var flexible = new List<SkinLayoutChild>();
        foreach (SkinLayoutChild child in shown)
        {
            if (child.Node is SkinLayoutRegion && _arrangement!.OptionsFor(RegionOf(child)).Width is not null)
            {
                widths[child] = Need(child, row);
                left -= widths[child];
            }
            else flexible.Add(child);
        }
        while (flexible.Count > 0)
        {
            double share = Math.Floor(left / flexible.Count);
            SkinLayoutChild? large = flexible.FirstOrDefault(c => Need(c, row) > share);
            if (large is null)
            {
                foreach (SkinLayoutChild child in flexible) widths[child] = share;
                break;
            }
            widths[large] = Need(large, row);
            left -= widths[large];
            flexible.Remove(large);
        }
        return widths;
    }

    /// <summary>
    /// Record a child's width and alignment, its preferred height as the
    /// least it takes, and go on inside it when it is a container.
    /// </summary>
    private void Give(SkinLayoutChild child, SkinLayoutContainer parent, double width, HorizontalAlignment? horizontal,
        VerticalAlignment? vertical, double? inner = null)
    {
        Control control = ControlOf(child);
        double minHeight = child.Node is SkinLayoutRegion && SkinLayoutRegions.IsSection(RegionOf(child))
            && _arrangement!.OptionsFor(RegionOf(child)).Height is { } height ? height : double.NaN;
        _sizes[control] = new Sizing(width, horizontal, vertical, minHeight);
        if (child.Node is SkinLayoutContainer container)
            Assign(container, double.IsNaN(width) ? inner : width);
    }
}
