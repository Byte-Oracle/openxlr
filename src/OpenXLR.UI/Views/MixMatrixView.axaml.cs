using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenXLR.UI.Layout;

namespace OpenXLR.UI.Views;

/// <summary>The submixer as a mix matrix. Its data context is a <see cref="MixMatrixViewModel"/>.</summary>
public partial class MixMatrixView : UserControl
{
    public MixMatrixView()
    {
        InitializeComponent();
    }

    /// <summary>The scroller the matrix moves sideways in when it is wider than its card, for the tests.</summary>
    internal ScrollViewer Scroller => MatrixScroll;

    private void OnXlrInserts(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MatrixRowViewModel { Inserts: { } inserts, InsertsKey: { } key }
            && TopLevel.GetTopLevel((Control)sender!) is Window window)
            InsertWindows.OpenChain(window, inserts, key);
    }

    private void OnMixInserts(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MatrixColumnViewModel column && TopLevel.GetTopLevel((Control)sender!) is Window window)
            InsertWindows.OpenChain(window, column.Mix.Inserts, $"mix:{column.Mix.Id}");
    }
}
