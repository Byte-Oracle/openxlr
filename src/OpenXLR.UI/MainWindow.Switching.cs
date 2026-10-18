using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using OpenXLR.UI.Layout;

namespace OpenXLR.UI;

/// <summary>
/// A structural skin switch, made safe. A switch that changes only colours
/// never gets here; one that moves regions or changes a template does, and
/// keeps to this order:
///
/// 1. every gesture that measured the old geometry ends: an Arrange drag, the
///    swap menu, and a fader drag in the main window or the mixer window;
/// 2. what the user had is noted: the focused control in each window, by
///    what it stands for (<see cref="FocusIdentity"/>), and every scroller's
///    offset;
/// 3. the caller puts on the templates, in whichever window holds the mixer
///    panel, and stands the regions in the new tree;
/// 4. the layout runs, the scrollers that are still there get their offsets
///    back, and focus goes to the same control, or to the one that stands
///    for the same channel, mix and role when the template built it again.
///
/// Nothing here sends a command, opens or closes a window, or touches a
/// channel, mix or send; the sections' folds and the compact view's choices
/// are never touched at all, so they come through as they were.
/// </summary>
public partial class MainWindow
{
    private IPointer? _lastMixerPointer;

    /// <summary>End every gesture measured against the geometry a switch is about to change.</summary>
    private void CancelGestures()
    {
        CancelReorder();
        SwapMenu?.Hide();
        foreach (IPointer? pointer in new[] { _lastPointer, _lastMixerPointer })
            if (pointer?.Captured is Visual captured && captured.GetSelfAndVisualAncestors()
                    .Any(v => ReferenceEquals(v, MixerContent) || ReferenceEquals(v, MixerPanel)))
                pointer.Capture(null);
    }

    private sealed class SwitchState
    {
        private readonly MainWindow _main;
        private readonly List<(TopLevel Root, Control Focused, FocusIdentity? Identity)> _focus = [];
        private readonly List<(ScrollViewer Scroller, Vector Offset)> _offsets = [];

        private SwitchState(MainWindow main) => _main = main;

        internal static SwitchState Capture(MainWindow main)
        {
            main.CancelGestures();
            var state = new SwitchState(main);
            foreach (TopLevel root in state.Roots())
            {
                if (root.FocusManager?.GetFocusedElement() is Control focused)
                    state._focus.Add((root, focused, FocusIdentity.Of(focused)));
                foreach (ScrollViewer scroller in root.GetVisualDescendants().OfType<ScrollViewer>())
                    state._offsets.Add((scroller, scroller.Offset));
            }
            return state;
        }

        private IEnumerable<TopLevel> Roots()
        {
            yield return _main;
            if (_main._mixerWindow is { } detached) yield return detached;
        }

        /// <summary>
        /// Give back what <see cref="Capture"/> noted. The page keeps its
        /// place; a new tree is drawn from its left, since the old sideways
        /// offset measured another tree; and the focused control, wherever it
        /// now stands, is scrolled into view.
        /// </summary>
        internal void Restore(bool newTree)
        {
            foreach (TopLevel root in Roots().Where(r => r.IsVisible)) root.UpdateLayout();
            foreach ((ScrollViewer scroller, Vector offset) in _offsets)
            {
                if (!scroller.IsAttachedToVisualTree() || newTree && ReferenceEquals(scroller, _main.SectionScroll)) continue;
                if (scroller.Offset != offset) scroller.Offset = offset;
            }
            if (newTree) _main.SectionScroll.Offset = default;
            foreach ((TopLevel root, Control focused, FocusIdentity? identity) in _focus)
            {
                if (!root.IsVisible) continue;
                Control? now = root.FocusManager?.GetFocusedElement() as Control;
                if (ReferenceEquals(now, focused) && focused.IsEffectivelyVisible) continue;
                Control? restored = focused.IsAttachedToVisualTree() && focused.IsEffectivelyVisible
                    && TopLevel.GetTopLevel(focused) == root && focused.Focus(NavigationMethod.Unspecified)
                    ? focused : identity?.Restore(root);
                restored?.BringIntoView();
            }
        }
    }
}
