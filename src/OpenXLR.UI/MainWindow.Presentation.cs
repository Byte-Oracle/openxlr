using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI;

/// <summary>
/// The templates a skin's layout picks for INPUTS and the submixer: the
/// hardware panel or an input rack, the channel tiles or mix banks. The rack
/// and the banks are projections over the window's own view models, made
/// while they show and dropped when they stop; switching between templates
/// moves no audio, sends no command and replaces no channel, mix or send.
/// </summary>
public partial class MainWindow
{
    private InputRackViewModel? _rack;
    private MixerPresentationViewModel? _banks;

    /// <summary>The input rack shown now, or null while INPUTS is the hardware panel.</summary>
    internal InputRackViewModel? InputRackModel => _rack;

    /// <summary>The mix banks shown now, or null while the submixer is the channel tiles.</summary>
    internal MixerPresentationViewModel? MixBanks => _banks;

    /// <summary>
    /// Put on the templates <paramref name="arrangement"/>'s layout picks,
    /// keeping a rack or banks that already belong to the window's model.
    /// </summary>
    private void ApplyTemplates(SectionArrangement arrangement)
    {
        SkinLayout? layout = arrangement.Layout;
        var model = DataContext as MainViewModel;

        bool rack = layout?.Inputs.IsRack == true && model is not null;
        if (_rack is not null && (!rack || !ReferenceEquals(_rack.Main, model)))
        {
            InputRack.DataContext = null;
            _rack.Dispose();
            _rack = null;
        }
        if (rack)
        {
            _rack ??= new InputRackViewModel(model!);
            _rack.Configure(layout!.Inputs.Strip);
            InputRack.DataContext = _rack;
        }
        InputRack.IsVisible = rack;
        HardwarePanel.IsVisible = !rack;

        bool banks = layout?.Submixer.IsBanks == true && model is not null;
        if (_banks is not null && (!banks || !ReferenceEquals(_banks.Main, model)))
        {
            MixerPanel.Show(null);
            _banks.SelectionChosen -= SaveBanks;
            _banks.ShapeChanged -= OnBanksShapeChanged;
            _banks.Dispose();
            _banks = null;
        }
        if (banks)
        {
            if (_banks is null)
            {
                _banks = new MixerPresentationViewModel(model!);
                _banks.SelectionChosen += SaveBanks;
                _banks.ShapeChanged += OnBanksShapeChanged;
            }
            _banks.Configure(layout!.Submixer, UiSettings.Load().SkinBanksFor(arrangement.Skin!, arrangement.Version!.Value));
            // One handle per channel: the rack's while it shows, otherwise the first bank's.
            _banks.OwnsHandles = !rack || arrangement.Hidden.Contains(SkinLayoutRegions.Inputs);
        }
        MixerPanel.Show(_banks);
        _mixerWindow?.UpdatePanelWidth();
    }

    /// <summary>A pick from a bank's picker, kept for the skin in force.</summary>
    private void SaveBanks(IReadOnlyList<string> banks)
    {
        if (_arrangement is not { Skin: { } skin, Version: { } version }) return;
        string? error = UiSettings.Load().WithSkinBanks(skin, version, banks).Save();
        if (error is not null) ReportArrangement(Localizer.Format("BankChoiceNotSaved", error));
    }

    private void OnBanksShapeChanged()
    {
        _layoutHost.UpdateWidths();
        _mixerWindow?.UpdatePanelWidth();
    }

    /// <summary>
    /// The narrowest a region is drawn where its container gives it a width
    /// of its own, by the template it wears now (see
    /// <see cref="SkinLayoutDefaults.RegionMinWidth"/>).
    /// </summary>
    private double RegionMinWidth(string region)
    {
        double Token(string key, double fallback) => this.TryFindResource(key, out object? value) && value is double d ? d : fallback;
        var sizes = new RegionSizes(Token("Ox.Mixer.StripWidth", 76), Token("Ox.Mixer.MixWidth", 232),
            Token("Ox.Mixer.BankSpacing", 12), _banks?.Banks.Count ?? 1, _banks?.VisibleChannels ?? 0);
        return SkinLayoutDefaults.RegionMinWidth(region, _arrangement?.Layout, sizes);
    }

    private void DisposePresentations()
    {
        if (_rack is not null) { InputRack.DataContext = null; _rack.Dispose(); _rack = null; }
        if (_banks is not null)
        {
            MixerPanel.Show(null);
            _banks.SelectionChosen -= SaveBanks;
            _banks.ShapeChanged -= OnBanksShapeChanged;
            _banks.Dispose();
            _banks = null;
        }
    }
}
