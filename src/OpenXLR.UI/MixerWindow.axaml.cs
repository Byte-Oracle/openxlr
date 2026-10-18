using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI;

/// <summary>The main window's mixer panel, moved here without creating another client or mixer.</summary>
public partial class MixerWindow : Window
{
    private Control? _panel;
    private double _minimum;
    private Size _decoration;
    private Screen? _screen;

    public MixerWindow()
    {
        InitializeComponent();
        UpdateControlSizing();
        SkinService.Changed += UpdateControlSizing;
        MixerScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty) UpdatePanelWidth();
        };
        Closed += (_, _) => SkinService.Changed -= UpdateControlSizing;
    }

    internal void Attach(Control panel, object? model, Window origin)
    {
        DataContext = model;
        _panel = panel;
        MixerScroll.Content = panel;
        _minimum = Minimum();
        UpdatePanelWidth();
        // Start on the originating screen, with enough room for its frame.
        // The window stays unowned, so hiding the main window leaves this one usable.
        if (origin.Screens.ScreenFromWindow(origin) is { } screen)
        {
            Size frame = origin.FrameSize ?? origin.ClientSize;
            var decoration = new Size(Math.Max(0, frame.Width - origin.ClientSize.Width),
                Math.Max(0, frame.Height - origin.ClientSize.Height));
            _screen = screen;
            _decoration = decoration;
            var size = FlowWindow.FitClientSize(new(Width, Height), screen.WorkingArea.Size, screen.Scaling, decoration);
            MinWidth = Math.Min(MinWidth, size.Width);
            MinHeight = Math.Min(MinHeight, size.Height);
            Width = size.Width;
            Height = size.Height;
            Position = screen.WorkingArea.Position;
        }
    }

    internal Control Detach()
    {
        var panel = (Control)MixerScroll.Content!;
        MixerScroll.Content = null;
        _panel = null;
        panel.ClearValue(WidthProperty);
        DataContext = null;
        return panel;
    }

    private void UpdateControlSizing()
    {
        Classes.Set("large-targets", SkinService.LargeTargets);
        UpdatePanelWidth();
    }

    /// <summary>
    /// Keep wrapping at the viewport width. A panel that needs more than the
    /// window has, wide tiles or banks side by side, scrolls sideways instead
    /// of clipping a control. How much it needs is the active presentation's
    /// to say: the wider tile for the channel tiles, or what the banks need.
    /// </summary>
    internal void UpdatePanelWidth()
    {
        if (_panel is null) return;
        _panel.Width = Math.Max(MixerScroll.Viewport.Width, Minimum() + 8);
    }

    private double Minimum() => _panel is Views.MixerPanel mixer ? mixer.MinimumWidth()
        : Math.Max((double)this.FindResource("Ox.Mixer.MixWidth")!, (double)this.FindResource("Ox.Mixer.ChannelWidth")!);

    /// <summary>
    /// The panel shows another presentation: wrap it at the new width, and
    /// widen the window when the presentation needs more than the one before
    /// it did and more than the window has, never past the screen it opened
    /// on. A presentation that needs less leaves the window as the user
    /// sized it, and so does one that needs more than a window the user made
    /// narrower on purpose, until the need grows again; the panel scrolls
    /// sideways meanwhile.
    /// </summary>
    internal void FitPresentation()
    {
        if (_panel is null) return;
        double minimum = Minimum();
        bool grew = minimum > _minimum + 0.5;
        _minimum = minimum;
        UpdatePanelWidth();
        // The panel, its own gap, and the window's margin on both sides.
        double need = minimum + 8 + 16;
        if (!grew || ClientSize.Width >= need) return;
        double most = _screen is { } screen
            ? FlowWindow.FitClientSize(new(need, Height), screen.WorkingArea.Size, screen.Scaling, _decoration).Width
            : need;
        Width = Math.Max(ClientSize.Width, Math.Min(need, most));
    }

    /// <summary>The narrowest the panel's presentation was drawn at when the window last fitted it, for the tests.</summary>
    internal double FittedMinimum => _minimum;

    private void OnDockMixer(object? sender, RoutedEventArgs e) => Close();
}
