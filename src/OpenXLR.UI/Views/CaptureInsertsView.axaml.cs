using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI.Views;

/// <summary>A capture input's chain button and insert rows, shared by the classic panel and rack.</summary>
public partial class CaptureInsertsView : UserControl
{
    /// <summary>True in the INPUTS panel: the row is labelled Inserts and the button stands at its end, as XLR 1's does.</summary>
    public static readonly StyledProperty<bool> LabelledProperty =
        AvaloniaProperty.Register<CaptureInsertsView, bool>(nameof(Labelled));

    public CaptureInsertsView()
    {
        InitializeComponent();
        ApplyLabelled();
    }

    public bool Labelled { get => GetValue(LabelledProperty); set => SetValue(LabelledProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelledProperty) ApplyLabelled();
    }

    // In a rack strip the button stands alone at the start, as the XLR strips' does.
    private void ApplyLabelled()
    {
        InsertsLabel.IsVisible = Labelled;
        Grid.SetColumn(CaptureInserts, Labelled ? 2 : 0);
        Grid.SetColumnSpan(CaptureInserts, Labelled ? 1 : 3);
        CaptureInserts.HorizontalAlignment = Labelled ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Left;
    }

    private void OnInserts(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChannelViewModel { Inserts: { } inserts } channel && TopLevel.GetTopLevel(this) is Window window)
            InsertWindows.OpenChain(window, inserts, channel.Id);
    }

    private void OnControls(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel insert && TopLevel.GetTopLevel(this) is Window window)
            InsertWindows.OpenControls(window, insert);
    }
}
