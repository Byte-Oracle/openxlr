using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI.Views;

/// <summary>A capture input's source and connection, opening its binding dialog.</summary>
public partial class CaptureSourceChip : UserControl
{
    public CaptureSourceChip()
    {
        InitializeComponent();
    }

    private void OnChip(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChannelViewModel { CaptureSource: not null } channel)
            _ = InputBindingDialog.OpenFrom(this, channel.Id);
    }
}
