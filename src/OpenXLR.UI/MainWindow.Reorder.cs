using OpenXLR.UI.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI;

/// <summary>
/// Arrange mode: drag handles on the five sections and on the channel and
/// mix tiles. Section order is a window preference in ui.json, kept per skin
/// when the skin sets its own layout (see <see cref="SectionArrangement"/>);
/// the skin also decides whether the sections run down the window or across
/// it, and the drag, the arrow keys and the autoscroll follow. Channel and
/// mix order is the layout's one order, so a tile move is a
/// <c>setLayoutOrder</c> and the tiles follow the state the daemon publishes
/// after saving it; nothing moves here before that.
/// </summary>
public partial class MainWindow
{
    private static readonly string ArrangeHint =
        Localizer.Text("DragAHandleOntoAnotherSectionOr");
    private Button? _dragHandle;
    private Button? _dropHandle;
    private Button[] _dragTargets = [];
    private IPointer? _dragPointer;
    private Point _dragOrigin, _dragPoint;
    private bool _dragging, _dropAfter, _reorderSaving;
    private readonly DispatcherTimer _dragScroll = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private SectionArrangement? _arrangement;
    private IPointer? _lastPointer;

    /// <summary>The arrangement the sections stand in now.</summary>
    internal SectionArrangement Arrangement => _arrangement!;

    /// <summary>True while the skin in force runs the sections across the window.</summary>
    private bool SectionsInARow => _arrangement?.Orientation == SkinLayoutOrientation.Horizontal;

    private void SetupReordering()
    {
        ApplyArrangement(ResolveArrangement());
        SkinService.Changed += OnSkinArrangement;
        // The last pointer that pressed anywhere in the window, so a skin
        // switch can end a fader drag before it moves the sections.
        AddHandler(PointerPressedEvent, (_, e) => _lastPointer = e.Pointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        foreach (Border card in SectionCards.Children.OfType<Border>())
            card.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty) UpdateRowWidths();
            };
        SectionScroll.SizeChanged += (_, _) => UpdateRowWidths();
        // Tunnel so a handle is seen before the expander header's
        // press-to-toggle; every other control keeps its own input.
        AddHandler(PointerPressedEvent, OnReorderPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnReorderMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnReorderReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, (_, _) => CancelReorder(), RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnReorderKey, RoutingStrategies.Tunnel);
        _dragScroll.Tick += (_, _) => ScrollReorder();
        Deactivated += (_, _) => CancelReorder();
        Closed += (_, _) =>
        {
            SkinService.Changed -= OnSkinArrangement;
            CancelReorder();
        };
        DataContextChanged += (_, _) => WatchMixerView();
        WatchMixerView();
    }

    private MainViewModel? _watchedView;

    /// <summary>The mini view sets most sections aside, so it ends Arrange mode.</summary>
    private void WatchMixerView()
    {
        if (_watchedView is not null) _watchedView.PropertyChanged -= OnMixerViewChanged;
        _watchedView = DataContext as MainViewModel;
        if (_watchedView is not null) _watchedView.PropertyChanged += OnMixerViewChanged;
    }

    private void OnMixerViewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsMiniView) || _watchedView?.IsMiniView != true
            || ArrangeButton.IsChecked != true) return;
        ArrangeButton.IsChecked = false;
        OnArrange(this, new RoutedEventArgs());
    }

    private void OnArrange(object? sender, RoutedEventArgs e)
    {
        CancelReorder();
        bool arranging = ArrangeButton.IsChecked == true;
        Classes.Set("arranging", arranging);
        ResetSectionsButton.IsVisible = arranging;
        ReportArrangement(null);
    }

    /// <summary>
    /// Reset puts back the order the skin in force starts from: the usual one
    /// for a skin without a layout, the skin's own for one with. Only that
    /// skin's arrangement is cleared; another skin's, and the legacy order
    /// under a skin with a layout, stay as they are.
    /// </summary>
    private void OnResetSections(object? sender, RoutedEventArgs e)
    {
        CancelReorder();
        SectionArrangement arrangement = Arrangement;
        ApplyArrangement(arrangement with { Order = arrangement.SkinOrder });
        string? error = arrangement.Reset(UiSettings.Load()).Save();
        ReportArrangement(error is null ? null : Localizer.Format("SectionOrderNotSaved", error));
    }

    /// <summary>The note under the Arrange button: the hint, or why the last move failed.</summary>
    private void ReportArrangement(string? error)
    {
        ArrangementNote.Text = error ?? ArrangeHint;
        ArrangementNote.IsVisible = error is not null || ArrangeButton.IsChecked == true;
    }

    /// <summary>
    /// A saved order made whole: known sections in their saved order, then
    /// any it leaves out in their usual order. Unknown and repeated names go.
    /// </summary>
    internal static string[] CompleteSectionOrder(IEnumerable<string>? saved) =>
        CompleteSectionOrder(saved, SectionTiles);

    /// <summary>
    /// The same, made whole against another order: a skin's own, for the
    /// moves saved under that skin.
    /// </summary>
    internal static string[] CompleteSectionOrder(IEnumerable<string>? saved, IReadOnlyList<string> usual) =>
        [.. (saved ?? []).Concat(usual).Where(usual.Contains).Distinct(StringComparer.Ordinal)];

    /// <summary>The order with <paramref name="source"/> moved to just before or after <paramref name="target"/>.</summary>
    internal static string[] PlaceSection(IReadOnlyList<string> order, string source, string target, bool after)
    {
        var result = order.ToList();
        if (source == target || !result.Contains(source) || !result.Contains(target)) return [.. result];
        result.Remove(source);
        result.Insert(result.IndexOf(target) + (after ? 1 : 0), source);
        return [.. result];
    }

    private SectionArrangement ResolveArrangement() => SectionArrangement.Resolve(
        SkinService.Current.Id, SkinService.Layout, UiSettings.Load());

    /// <summary>
    /// A skin was put on, or the sizing changed. Only a skin whose layout
    /// differs from what the window shows moves anything; a change of colours
    /// alone stays the resource update it has always been. While the order
    /// still belongs to the same skin and starts from the same place, the one
    /// on screen is kept, so a move that could not be saved lasts the run.
    /// </summary>
    private void OnSkinArrangement()
    {
        SectionArrangement next = ResolveArrangement();
        if (_arrangement is { } current && current.Skin == next.Skin && current.Version == next.Version
            && current.SkinOrder.SequenceEqual(next.SkinOrder, StringComparer.Ordinal))
            next = next with { Order = current.Order };
        ApplyArrangement(next);
    }

    /// <summary>
    /// Stand the sections as <paramref name="next"/> says. The cards, their
    /// expanders and everything inside them are the same objects before and
    /// after: they change place in the one panel and the panel changes
    /// direction. A drag in progress, of a section or of a fader, ends first,
    /// since the geometry it was measured against is about to change.
    /// </summary>
    private void ApplyArrangement(SectionArrangement next)
    {
        SectionArrangement? previous = _arrangement;
        _arrangement = next;
        ToolTip.SetTip(ResetSectionsButton, next.Skin is null
            ? Localizer.Text("PutTheFiveSectionsBackInTheir") : Localizer.Text("PutTheSectionsBackInTheSkinsOrder"));
        if (previous is not null && previous.SameStructure(next)) return;
        if (previous is not null)
        {
            CancelReorder();
            if (_lastPointer?.Captured is Visual captured && captured.GetVisualAncestors().Contains(SectionCards))
                _lastPointer.Capture(null);
        }

        string[] complete = CompleteSectionOrder(next.Order);
        for (int index = 0; index < complete.Length; index++)
            if (this.FindControl<Expander>(complete[index])?.Parent is Control card)
                SectionCards.Children.Move(SectionCards.Children.IndexOf(card), index);

        bool row = next.Orientation == SkinLayoutOrientation.Horizontal;
        SectionCards.Orientation = row ? Orientation.Horizontal : Orientation.Vertical;
        SectionCards.Spacing = next.Spacing;
        // A row is as wide as its sections; the column follows the window.
        SectionScroll.HorizontalScrollBarVisibility = row ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        // Room under the row for its scroll bar, which is drawn over the content.
        SectionCards.Margin = row ? new Thickness(0, 0, 0, 12) : default;
        foreach (Border card in SectionCards.Children.OfType<Border>())
        {
            // In a row a collapsed section is as tall as its header rather
            // than as tall as the tallest section beside it.
            if (row) card.VerticalAlignment = VerticalAlignment.Top;
            else card.ClearValue(VerticalAlignmentProperty);
        }
        foreach (Button handle in SectionHandles()) ClearDropTarget(handle);
        UpdateRowWidths();
        if (previous?.Orientation != next.Orientation) SectionScroll.Offset = default;
    }

    /// <summary>
    /// In a row, the shown sections share the window's width, each at least
    /// <see cref="SkinLayoutDefaults.RowSectionMinWidth"/> wide; the row
    /// scrolls when that is more than the window has. In a column a section
    /// takes the window's width, as it always has.
    /// </summary>
    private void UpdateRowWidths()
    {
        Border[] cards = [.. SectionCards.Children.OfType<Border>()];
        if (!SectionsInARow)
        {
            foreach (Border card in cards) card.ClearValue(WidthProperty);
            return;
        }
        int shown = Math.Max(1, cards.Count(c => c.IsVisible));
        double available = SectionScroll.Bounds.Width - SectionCards.Spacing * (shown - 1);
        double width = Math.Max(SkinLayoutDefaults.RowSectionMinWidth, Math.Floor(available / shown));
        foreach (Border card in cards) card.Width = width;
    }

    /// <summary>The handle in each section's header.</summary>
    private IEnumerable<Button> SectionHandles() => SectionTiles
        .Select(name => this.FindControl<Expander>(name)?.Header as Panel)
        .SelectMany(header => header?.Children.OfType<Button>() ?? [])
        .Where(b => b.Classes.Contains("reorderHandle"));

    private string[] CurrentSections() =>
        [.. SectionCards.Children.OfType<Border>().Select(card => ((Expander)card.Child!).Name!)];

    /// <summary>
    /// Show the order for this run and save it, under the skin in force when
    /// it sets a layout and as the legacy order otherwise; a failed save is
    /// said once in the note.
    /// </summary>
    private void SaveSectionOrder(IReadOnlyList<string> order)
    {
        SectionArrangement arrangement = Arrangement;
        ApplyArrangement(arrangement with { Order = [.. order] });
        string? error = arrangement.Save(UiSettings.Load(), order).Save();
        ReportArrangement(error is null ? null : Localizer.Format("SectionOrderNotSaved", error));
    }

    private static Button? Handle(object? source) => source is Visual visual
        ? visual.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("reorderHandle"))
        : null;

    /// <summary>0 for a section, 1 for a channel tile, 2 for a mix tile: a handle only drops within its own group.</summary>
    private static int Group(Button handle) => handle.Tag switch
    {
        string => 0, ChannelViewModel => 1, MixViewModel => 2, _ => -1,
    };

    private static string Id(Button handle) => handle.Tag switch
    {
        string id => id, ChannelViewModel channel => channel.Id, MixViewModel mix => mix.Id, _ => "",
    };

    private IEnumerable<Button> Handles(int group) => this.GetVisualDescendants().OfType<Button>()
        .Where(h => h.Classes.Contains("reorderHandle") && h.IsEffectivelyVisible && Group(h) == group);

    private static Border? Card(Button handle) => handle.GetVisualAncestors().OfType<Border>()
        .FirstOrDefault(b => b.Classes.Contains(Group(handle) == 0 ? "card" : "tile"));

    private bool CanReorder(Button handle) => !_reorderSaving && ArrangeButton.IsChecked == true
        && (Group(handle) == 0 || DataContext is MainViewModel { CanEditLayout: true });

    private void OnReorderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Handle(e.Source) is not { } handle || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (!CanReorder(handle)) return;
        CancelReorder();
        _dragHandle = handle;
        _dragTargets = [.. Handles(Group(handle))];
        _dragPointer = e.Pointer;
        _dragPoint = _dragOrigin = e.GetPosition(this);
        handle.Focus();
        e.Pointer.Capture(handle);
    }

    private void OnReorderMoved(object? sender, PointerEventArgs e)
    {
        if (_dragHandle is null || e.Pointer != _dragPointer) return;
        e.Handled = true;
        _dragPoint = e.GetPosition(this);
        if (!_dragging && new Vector(_dragPoint.X - _dragOrigin.X, _dragPoint.Y - _dragOrigin.Y).Length < 6) return;
        _dragging = true;
        _dragScroll.Start();
        UpdateDropTarget();
    }

    /// <summary>
    /// Mark the handle of the tile under the pointer, with an arrow for the
    /// side the dragged one lands on. A tile only counts where it can be seen.
    /// </summary>
    private void UpdateDropTarget()
    {
        if (_dragHandle is not { } source || !CanReorder(source) || !source.IsEffectivelyVisible)
        { ClearDropTarget(); return; }
        foreach (Button target in _dragTargets)
        {
            if (ReferenceEquals(source, target) || !target.IsEffectivelyVisible || TopLevel.GetTopLevel(target) != this
                || Card(target) is not { } card
                || card.TranslatePoint(default, this) is not { } origin) continue;
            var bounds = new Rect(origin, card.Bounds.Size);
            Rect visible = bounds.Intersect(new Rect(Bounds.Size));
            foreach (Control ancestor in card.GetVisualAncestors().OfType<Control>().Where(c => c.ClipToBounds))
                if (ancestor.TranslatePoint(default, this) is { } clip)
                    visible = visible.Intersect(new Rect(clip, ancestor.Bounds.Size));
            if (!visible.Contains(_dragPoint)) continue;
            bool after = AlongY(source) ? _dragPoint.Y > bounds.Center.Y : _dragPoint.X > bounds.Center.X;
            // Pointer moves and scroll ticks mostly land on the same side of
            // the same tile; leave its style and arrow alone then.
            if (ReferenceEquals(_dropHandle, target) && _dropAfter == after) return;
            if (!ReferenceEquals(_dropHandle, target))
            {
                ClearDropTarget();
                _dropHandle = target;
                target.Classes.Add("reorderTarget");
            }
            _dropAfter = after;
            target.Content = AlongY(source) ? (after ? "↓" : "↑") : (after ? "→" : "←");
            return;
        }
        ClearDropTarget();
    }

    private void ClearDropTarget()
    {
        if (_dropHandle is not { } target) return;
        ClearDropTarget(target);
        _dropHandle = null;
    }

    /// <summary>A handle at rest: no target style, and an arrow along the way its group moves.</summary>
    private void ClearDropTarget(Button target)
    {
        target.Classes.Remove("reorderTarget");
        target.Content = AlongY(target) ? "↕" : "↔";
    }

    /// <summary>
    /// True when a handle's group runs down the window: the sections in a
    /// column. Sections in a row, and the channel and mix tiles, run across.
    /// </summary>
    private bool AlongY(Button handle) => Group(handle) == 0 && !SectionsInARow;

    private async void OnReorderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragHandle is not { } source || e.Pointer != _dragPointer) return;
        e.Handled = true;
        // Only the left button finishes a drag; another button released
        // meanwhile leaves it running.
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        _dragPoint = e.GetPosition(this);
        UpdateDropTarget();
        Button? target = _dragging ? _dropHandle : null;
        bool after = _dropAfter;
        CancelReorder();
        if (target is not null) await PlaceTile(source, target, after);
    }

    private void CancelReorder()
    {
        _dragScroll.Stop();
        ClearDropTarget();
        IPointer? pointer = _dragPointer;
        _dragPointer = null;
        _dragHandle = null;
        _dragTargets = [];
        _dragging = false;
        pointer?.Capture(null);
    }

    private async void OnReorderKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _dragHandle is not null)
        {
            CancelReorder();
            e.Handled = true;
            return;
        }
        if (Handle(e.Source) is not { } source || e.KeyModifiers != KeyModifiers.None) return;
        int direction = e.Key switch { Key.Left or Key.Up => -1, Key.Right or Key.Down => 1, _ => 0 };
        if (direction == 0) return;
        e.Handled = true;
        if (!CanReorder(source) || _dragHandle is not null) return;
        var handles = Handles(Group(source)).ToList();
        int target = handles.IndexOf(source) + direction;
        if (target >= 0 && target < handles.Count)
        {
            await PlaceTile(source, handles[target], after: direction > 0);
            source.BringIntoView();
        }
    }

    private async Task PlaceTile(Button source, Button target, bool after)
    {
        if (!CanReorder(source) || !source.IsEffectivelyVisible || !target.IsEffectivelyVisible
            || TopLevel.GetTopLevel(source) != this || TopLevel.GetTopLevel(target) != this || Group(source) != Group(target)) return;
        if (Group(source) == 0)
        {
            string[] order = CurrentSections();
            string[] updated = PlaceSection(order, Id(source), Id(target), after);
            if (!order.SequenceEqual(updated)) SaveSectionOrder(updated);
            return;
        }
        if (DataContext is not MainViewModel vm) return;
        // One tile move at a time: the next one starts from the order the
        // daemon published for this one.
        _reorderSaving = true;
        try { ReportArrangement(await vm.PlaceInLayoutOrder(Id(source), Id(target), after, isMix: Group(source) == 2)); }
        finally { _reorderSaving = false; }
    }

    /// <summary>
    /// While a handle is held near the edge of a scrolled view, scroll it:
    /// the page vertically, and sideways the channel row or a row of sections.
    /// </summary>
    private void ScrollReorder()
    {
        if (_dragHandle is not { } handle || !CanReorder(handle) || !handle.IsEffectivelyVisible)
        { CancelReorder(); return; }
        foreach (var scroll in handle.GetVisualAncestors().OfType<ScrollViewer>())
        {
            if (scroll.TranslatePoint(default, this) is not { } origin) continue;
            double top = Math.Max(0, origin.Y), bottom = Math.Min(Bounds.Height, origin.Y + scroll.Bounds.Height);
            double left = Math.Max(0, origin.X), right = Math.Min(Bounds.Width, origin.X + scroll.Bounds.Width);
            if (_dragPoint.X < left || _dragPoint.X > right || _dragPoint.Y < top || _dragPoint.Y > bottom) continue;
            double x = scroll.Offset.X, y = scroll.Offset.Y;
            if (scroll.Extent.Width > scroll.Viewport.Width && Group(handle) is 0 or 1 && !AlongY(handle))
                x += _dragPoint.X < left + 32 ? -16 : _dragPoint.X > right - 32 ? 16 : 0;
            if (scroll.Extent.Height > scroll.Viewport.Height)
                y += _dragPoint.Y < top + 32 ? -16 : _dragPoint.Y > bottom - 32 ? 16 : 0;
            scroll.Offset = new Vector(Math.Clamp(x, 0, Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width)),
                Math.Clamp(y, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
        }
        UpdateDropTarget();
    }
}
