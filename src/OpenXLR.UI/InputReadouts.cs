using System.ComponentModel;
using System.Linq;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>
/// The Wave XLR Pro's Line In gain, and the gain readouts of Aux In and Line
/// In: each strip's readout shows the input's hardware level or gain, and
/// the channel's volume while that volume's fader moves and for a second
/// after (<see cref="ChannelViewModel.VolumeReadoutHold"/>). The input rack
/// and the mix matrix read the readout from the channel; the classic INPUTS
/// panel reads it here, beside the level and gain sliders it has always had.
/// </summary>
public sealed partial class MainViewModel
{
    private int _lineInGainDb;
    /// <summary>The Line In's gain in dB, 0 to the unit's highest gain.</summary>
    public int LineInGainDb
    {
        get => _lineInGainDb;
        set
        {
            if (!Set(ref _lineInGainDb, value)) return;
            Raise(nameof(LineInGainText));
            RefreshGainReadouts();
            if (_applying) return;
            int v = value;
            SliderSync.Touch("line");
            SliderSync.Send("line", () => _ = _client.SetControlAsync("lineInGain", v));
        }
    }
    public string LineInGainText => $"{_lineInGainDb} dB";

    private ChannelViewModel? _auxStrip, _lineStrip;
    /// <summary>Aux In's channel while the mixer has one.</summary>
    public ChannelViewModel? AuxStrip { get => _auxStrip; private set => Watch(ref _auxStrip, value, nameof(AuxStrip)); }
    /// <summary>Line In's channel while the mixer has one.</summary>
    public ChannelViewModel? LineStrip { get => _lineStrip; private set => Watch(ref _lineStrip, value, nameof(LineStrip)); }

    /// <summary>
    /// The INPUTS panel's Line In row: the interface has the input and, with
    /// the mixer running, the daemon reports the strip present, which it does
    /// only on the capture layout Line In was measured on.
    /// </summary>
    public bool ShowLineInRow => CapLineInput && (!HasMixer || _lineStrip is { Present: true });

    /// <summary>The Aux In row's readout: its level, or the channel's volume while that fader moves.</summary>
    public string AuxReadout => _auxStrip?.HasGainReadout == true ? _auxStrip.Readout : AuxLevelText;
    public string AuxReadoutName => _auxStrip?.HasGainReadout == true ? _auxStrip.ReadoutName : Localizer.Text("USBAuxIn");

    /// <summary>The Line In row's readout: its gain, or the channel's volume while that fader moves.</summary>
    public string LineInReadout => _lineStrip?.HasGainReadout == true ? _lineStrip.Readout : LineInGainText;
    public string LineInReadoutName => _lineStrip?.HasGainReadout == true ? _lineStrip.ReadoutName : Localizer.Text("LineInGain");

    /// <summary>Find the two strips after a state push and give each its readout.</summary>
    private void RefreshInputStrips()
    {
        ChannelViewModel[] channels = HasMixer ? [.. Channels] : [];
        AuxStrip = channels.FirstOrDefault(c => c.IsHardware && c.Id == "aux");
        LineStrip = channels.FirstOrDefault(c => c.IsHardware && c.Id == "line");
        RefreshGainReadouts();
        Raise(nameof(ShowLineInRow));
    }

    /// <summary>Each strip reads its hardware level or gain where the interface has the stage.</summary>
    private void RefreshGainReadouts()
    {
        _auxStrip?.SetGainReadout(CapAuxInput ? AuxLevelText : null, CapAuxInput ? Localizer.Text("USBAuxIn") : null);
        _lineStrip?.SetGainReadout(CapLineInput ? LineInGainText : null, CapLineInput ? Localizer.Text("LineInGain") : null);
        RaiseReadouts();
        Raise(nameof(ShowLineInRow));
    }

    private void Watch(ref ChannelViewModel? field, ChannelViewModel? value, string name)
    {
        if (ReferenceEquals(field, value)) return;
        if (field is not null) field.PropertyChanged -= OnReadoutStripChanged;
        field = value;
        if (value is not null) value.PropertyChanged += OnReadoutStripChanged;
        Raise(name);
        RaiseReadouts();
    }

    private void OnReadoutStripChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChannelViewModel.Readout) or nameof(ChannelViewModel.ReadoutName)
            or nameof(ChannelViewModel.HasGainReadout)) RaiseReadouts();
    }

    private void RaiseReadouts()
    {
        Raise(nameof(AuxReadout));
        Raise(nameof(AuxReadoutName));
        Raise(nameof(LineInReadout));
        Raise(nameof(LineInReadoutName));
    }
}
