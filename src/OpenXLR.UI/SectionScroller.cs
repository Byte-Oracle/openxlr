using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace OpenXLR.UI;

/// <summary>
/// The scroller around the main window's sections. It only ever scrolls
/// sideways, for a skin whose layout is wider than the window, so the keys that
/// page and scroll are left to the page around it: Page Up and Page Down in a
/// section still move the window's page, as they did before the sections had
/// a scroller of their own. Focus moving into a section off to the side
/// still brings it into view.
/// </summary>
public sealed class SectionScroller : ScrollViewer
{
    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    protected override void OnKeyDown(KeyEventArgs e)
    {
    }
}
