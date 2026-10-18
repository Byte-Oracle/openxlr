using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI.Views;

/// <summary>
/// The submixer's panel: the one control Pop out moves between the main
/// window and the mixer window, with whichever presentation the skin chose.
/// The channel tiles are markup of the main window's, handed in through
/// <see cref="Tiles"/> so their names and handlers stay the main window's;
/// the mix banks are this panel's own. Only one shows at a time, and the
/// banks are bound only while they show.
/// </summary>
public partial class MixerPanel : UserControl
{
    public static readonly StyledProperty<object?> HeaderProperty =
        AvaloniaProperty.Register<MixerPanel, object?>(nameof(Header));

    public static readonly StyledProperty<object?> TilesProperty =
        AvaloniaProperty.Register<MixerPanel, object?>(nameof(Tiles));

    public MixerPanel()
    {
        InitializeComponent();
    }

    /// <summary>What every presentation shows above it: the placeholder and the compact view's pickers.</summary>
    public object? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    /// <summary>The channel tiles and the mix masters under them, the classic presentation.</summary>
    public object? Tiles { get => GetValue(TilesProperty); set => SetValue(TilesProperty, value); }

    /// <summary>The banks shown now, or null while the channel tiles show.</summary>
    public MixerPresentationViewModel? Banks { get; private set; }

    /// <summary>The list the banks are drawn in, for the tests.</summary>
    internal ItemsControl BankItems => BankList;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HeaderProperty) HeaderPresenter.Content = change.NewValue;
        else if (change.Property == TilesProperty) TilesPresenter.Content = change.NewValue;
    }

    /// <summary>
    /// Show the mix banks of <paramref name="banks"/>, or the channel tiles
    /// when it is null. Nothing in either is rebuilt but the banks' own
    /// cells; the tiles and every view model stay as they are.
    /// </summary>
    public void Show(MixerPresentationViewModel? banks)
    {
        if (ReferenceEquals(Banks, banks) && BankList.IsVisible == banks is not null) return;
        Banks = banks;
        TilesPresenter.IsVisible = banks is null;
        BankList.IsVisible = banks is not null;
        BankList.DataContext = banks;
    }

    /// <summary>
    /// The narrowest this panel can be drawn without clipping a control, by
    /// the presentation it shows: the wider of a channel tile and a mix tile
    /// for the classic one, or the width the banks need, which is reckoned as
    /// the window reckons the submixer region's.
    /// </summary>
    public double MinimumWidth()
    {
        double Token(string key, double fallback) => this.TryFindResource(key, out object? value) && value is double d ? d : fallback;
        double mix = Token("Ox.Mixer.MixWidth", 232);
        if (Banks is not { } banks)
            return Math.Max(mix, Token("Ox.Mixer.ChannelWidth", 132));
        var layout = new SkinLayout(SkinFormat.LayoutVersion, SkinLayoutDefaults.Classic.Main) { Submixer = banks.Options };
        var sizes = new RegionSizes(Token("Ox.Mixer.StripWidth", 76), mix, Token("Ox.Mixer.BankSpacing", 12),
            banks.Banks.Count, banks.VisibleChannels);
        return SkinLayoutDefaults.RegionMinWidth(SkinLayoutRegions.Submixer, layout, sizes) - SkinLayoutDefaults.CardPadding;
    }

    private void OnMixInserts(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is BankMasterViewModel master && TopLevel.GetTopLevel((Control)sender!) is Window window)
            InsertWindows.OpenChain(window, master.Mix.Inserts, $"mix:{master.Mix.Id}");
    }

    private async void OnInsertControls(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not InsertViewModel insert) return;
        // As in the main window: a plugin running in its own process opens its own editor.
        if (insert.NativeEditorAvailable) await insert.Owner.ShowNativeEditorAsync(insert);
        else if (TopLevel.GetTopLevel((Control)sender!) is Window window) InsertWindows.OpenControls(window, insert);
    }
}
