using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI.Views;

/// <summary>A hardware strip's binding chip. Its data context is the strip's <see cref="ChannelViewModel"/>.</summary>
public partial class BindingChip : UserControl
{
    public BindingChip()
    {
        InitializeComponent();
    }

    private void OnChip(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChannelViewModel { IsBindable: true } strip) _ = InputBindingDialog.OpenFrom(this, strip.Id);
    }
}
