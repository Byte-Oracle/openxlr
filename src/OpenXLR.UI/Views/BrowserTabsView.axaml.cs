using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI.Views;

/// <summary>The same browser tab controls in APPLICATIONS and its management window.</summary>
public partial class BrowserTabsView : UserControl
{
    public BrowserTabsView() => InitializeComponent();

    private void OnForgetWebsite(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is BrowserTabViewModel tab) tab.ForgetWebsite();
    }
}
