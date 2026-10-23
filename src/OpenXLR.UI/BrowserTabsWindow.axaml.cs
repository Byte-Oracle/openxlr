using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>
/// Browser tab routing, opened from the button beside Manage: the option that
/// turns it on and one row for each tab the browser companion reports.
/// </summary>
public partial class BrowserTabsWindow : Window
{
    private const string Guide = "https://github.com/emaspa/openxlr/blob/main/docs/browser-routing.md";

    public BrowserTabsWindow()
    {
        InitializeComponent();
        Opened += (_, _) => ConstrainToScreen();
    }

    /// <summary>
    /// Keep the window inside the screen it opens on, as Manage apps does. It
    /// sizes itself to its rows and cannot be resized, so on a short screen
    /// the list scrolls instead of taking Close off the bottom.
    /// </summary>
    private void ConstrainToScreen()
    {
        var screen = Screens.ScreenFromWindow(Owner as Window ?? this) ?? Screens.Primary;
        if (screen is null) return;
        double usable = screen.WorkingArea.Height / screen.Scaling;
        if (usable > 240) MaxHeight = System.Math.Min(MaxHeight, usable - 60);
    }

    private void OnManual(object? sender, RoutedEventArgs e) => ExternalLink.Open(Guide);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
