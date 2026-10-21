using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI.Layout;

/// <summary>
/// A short flat fader drawn over a channel's level meter, the meter standing
/// in for the fader's groove: how a mix matrix shows a send, and a row head
/// its source level, in one bar. The fader is the <see cref="Slider"/> the
/// markup puts inside, with its own binding, name and fine steps; this panel
/// adds the channel's two meters behind it and, while they show, clears the
/// slider's groove so the meter reads through. The thumb keeps whatever face
/// the skin's <c>controls.fader</c> gives every fader.
///
/// The meters read the channel, never the send, and a screen reader hears
/// them by the channel's name alone; the fader carries the channel and the
/// mix in its own name.
/// </summary>
public sealed class MeterFader : Panel
{
    public static readonly StyledProperty<double> LeftLevelProperty =
        AvaloniaProperty.Register<MeterFader, double>(nameof(LeftLevel));

    public static readonly StyledProperty<double> RightLevelProperty =
        AvaloniaProperty.Register<MeterFader, double>(nameof(RightLevel));

    public static readonly StyledProperty<string?> LeftNameProperty =
        AvaloniaProperty.Register<MeterFader, string?>(nameof(LeftName));

    public static readonly StyledProperty<string?> RightNameProperty =
        AvaloniaProperty.Register<MeterFader, string?>(nameof(RightName));

    public static readonly StyledProperty<bool> ShowsMeterProperty =
        AvaloniaProperty.Register<MeterFader, bool>(nameof(ShowsMeter), true);

    public static readonly StyledProperty<bool> MonoProperty =
        AvaloniaProperty.Register<MeterFader, bool>(nameof(Mono));

    /// <summary>The groove keys the framework's and the console fader's templates paint with; the meter takes their place.</summary>
    private static readonly string[] GrooveKeys =
    [
        "SliderTrackFill", "SliderTrackFillPointerOver", "SliderTrackFillPressed",
        "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed",
    ];

    private readonly StackPanel _meters;

    public MeterFader()
    {
        var left = new LevelMeter { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
        var right = new LevelMeter { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
        left.Bind(LevelMeter.LevelProperty, this.GetObservable(LeftLevelProperty));
        right.Bind(LevelMeter.LevelProperty, this.GetObservable(RightLevelProperty));
        left.Bind(AutomationProperties.NameProperty, this.GetObservable(LeftNameProperty));
        right.Bind(AutomationProperties.NameProperty, this.GetObservable(RightNameProperty));
        _meters = new StackPanel
        {
            Name = "MeterTrack",
            Orientation = Orientation.Vertical,
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Children = { left, right },
        };
        // First, so the slider the markup adds after it draws over it.
        Children.Add(_meters);
        UpdateMeter();
    }

    /// <summary>The channel's left level, 0 to 1 on the meter's scale.</summary>
    public double LeftLevel { get => GetValue(LeftLevelProperty); set => SetValue(LeftLevelProperty, value); }

    public double RightLevel { get => GetValue(RightLevelProperty); set => SetValue(RightLevelProperty, value); }

    /// <summary>What a screen reader calls the left meter: the channel's level, never the send's.</summary>
    public string? LeftName { get => GetValue(LeftNameProperty); set => SetValue(LeftNameProperty, value); }

    public string? RightName { get => GetValue(RightNameProperty); set => SetValue(RightNameProperty, value); }

    /// <summary>True while the meter runs under the fader; false leaves a plain fader with its own groove.</summary>
    public bool ShowsMeter { get => GetValue(ShowsMeterProperty); set => SetValue(ShowsMeterProperty, value); }

    /// <summary>A microphone input draws one meter.</summary>
    public bool Mono { get => GetValue(MonoProperty); set => SetValue(MonoProperty, value); }

    /// <summary>The meter drawn under the fader, for the tests.</summary>
    internal StackPanel Meters => _meters;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowsMeterProperty || change.Property == MonoProperty) UpdateMeter();
    }

    private void UpdateMeter()
    {
        bool shows = ShowsMeter;
        _meters.IsVisible = shows;
        _meters.Children[1].IsVisible = !Mono;
        foreach (string key in GrooveKeys)
        {
            if (shows) Resources[key] = Brushes.Transparent;
            else Resources.Remove(key);
        }
    }
}
