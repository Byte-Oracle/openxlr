using Avalonia;
using Avalonia.Controls;

namespace OpenXLR.UI.Views;

/// <summary>Which of an additional interface's input controls a <see cref="WaveUnitControls"/> draws.</summary>
public enum WaveUnitControlsPart
{
    /// <summary>The gain and the switches, as the additional interfaces window shows them.</summary>
    All,
    /// <summary>The gain only, for a strip's gain part.</summary>
    Gain,
    /// <summary>The switches only, for a strip's hardware controls part.</summary>
    Switches,
}

/// <summary>
/// One input of an additional interface. Its data context is a
/// <see cref="WaveUnitInputViewModel"/>; <see cref="Part"/> picks what it
/// draws, so a strip can place the gain and the switches where its skin puts
/// its own.
/// </summary>
public partial class WaveUnitControls : UserControl
{
    public static readonly StyledProperty<WaveUnitControlsPart> PartProperty =
        AvaloniaProperty.Register<WaveUnitControls, WaveUnitControlsPart>(nameof(Part));

    public WaveUnitControlsPart Part
    {
        get => GetValue(PartProperty);
        set => SetValue(PartProperty, value);
    }

    public WaveUnitControls()
    {
        InitializeComponent();
        ShowPart();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PartProperty) ShowPart();
    }

    private void ShowPart()
    {
        WaveUnitControlsPart part = Part;
        this.FindControl<Panel>("GainPart")!.IsVisible = part != WaveUnitControlsPart.Switches;
        this.FindControl<WrapPanel>("SwitchesPart")!.IsVisible = part != WaveUnitControlsPart.Gain;
    }
}
