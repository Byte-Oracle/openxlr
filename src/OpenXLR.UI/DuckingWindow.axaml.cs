using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>
/// The Ducking window, opened from the layout editor and from Options. One
/// is open at a time: a second click raises it, or reopens it under the
/// window that asked, since an owned window cannot change its owner.
/// </summary>
public partial class DuckingWindow : Window
{
    private static DuckingWindow? _open;

    public DuckingWindow() => InitializeComponent();

    public static DuckingWindow Open(Window owner, DuckingViewModel ducking)
    {
        if (_open is { } open)
        {
            if (open.Owner == owner && open.DataContext == ducking)
            {
                open.Activate();
                return open;
            }
            open.Close();
        }
        var window = new DuckingWindow { DataContext = ducking };
        window.Closed += (_, _) => { if (_open == window) _open = null; };
        _open = window;
        window.Show(owner);
        return window;
    }

    private void OnProcessOnUnit(object? sender, RoutedEventArgs e) => (DataContext as DuckingViewModel)?.ChooseProcessing("device");

    private void OnProcessInSoftware(object? sender, RoutedEventArgs e) => (DataContext as DuckingViewModel)?.ChooseProcessing("software");

    private void OnAllApplications(object? sender, RoutedEventArgs e) => (DataContext as DuckingViewModel)?.ChooseAllApplications();

    private void OnNoChannels(object? sender, RoutedEventArgs e) => (DataContext as DuckingViewModel)?.ChooseNoChannels();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
