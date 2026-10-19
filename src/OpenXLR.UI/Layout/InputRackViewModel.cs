using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI.Layout;

/// <summary>What an input strip stands for, which decides the parts it has and the template that draws it.</summary>
public enum InputStripKind
{
    /// <summary>The first XLR input: its binding, hardware gain, switches and its plugin chain.</summary>
    Xlr1,
    /// <summary>The second XLR input, on an interface with two or bound to a source.</summary>
    Xlr2,
    /// <summary>The USB Aux and Line In stage: its level and level lock.</summary>
    AuxIn,
    /// <summary>A channel applications play into: its name and meter.</summary>
    Application,
    /// <summary>A channel bound to another capture device: its name and meter, and the interface's controls when it carries the interface's microphone.</summary>
    Capture,
}

/// <summary>
/// INPUTS as a rack: one strip per channel, in the mixer's channel order. A
/// strip for an XLR input or the Aux In stage stands for the hardware
/// controls the INPUTS panel has always shown for it, bound to the same
/// <see cref="MainViewModel"/> properties, so every capability, lock and
/// phantom settling rule there applies here unchanged. The interface's
/// input controls go on the channel that carries the interface's microphone
/// (<see cref="ChannelViewModel.ShownUnitInput"/>): its XLR strip, or a
/// capture channel on the unit's own node while the strip is bound to
/// another source. An application or capture channel otherwise has only
/// what it has: its name, its meter and the channel's own volume. Nothing
/// here holds a value of its own.
/// </summary>
public sealed class InputRackViewModel : IDisposable
{
    private readonly MainViewModel _main;
    private SkinStripOptions _options = SkinStripOptions.InputRowDefault;

    public InputRackViewModel(MainViewModel main)
    {
        _main = main;
        _main.Channels.CollectionChanged += OnChannelsChanged;
        _main.PropertyChanged += OnMainChanged;
        Sync();
    }

    public MainViewModel Main => _main;

    /// <summary>One strip per channel, in the mixer's order.</summary>
    public ObservableCollection<InputStripViewModel> Strips { get; } = [];

    /// <summary>The skin's strip options in force.</summary>
    public SkinStripOptions Options => _options;

    /// <summary>Take a skin's input-row options; strips are drawn again only when they draw differently.</summary>
    public void Configure(SkinStripOptions options)
    {
        if (options.Description() == _options.Description()) return;
        _options = options;
        foreach (InputStripViewModel strip in Strips) strip.Detach();
        Strips.Clear();
        Sync();
    }

    /// <summary>
    /// The parts a strip of this kind can have, in no particular order. A
    /// strip draws the gain and the switches only while it carries the
    /// interface's microphone; see <see cref="InputStripViewModel.Spec"/>.
    /// </summary>
    public static IReadOnlyCollection<string> PartsOf(InputStripKind kind) => kind switch
    {
        InputStripKind.Xlr1 or InputStripKind.Xlr2 => SkinStripParts.InputRow,
        InputStripKind.AuxIn => [SkinStripParts.Identity, SkinStripParts.Meter, SkinStripParts.Gain, SkinStripParts.HardwareControls],
        // A capture channel carrying the interface's microphone also has its switches.
        InputStripKind.Capture => [SkinStripParts.Identity, SkinStripParts.Meter, SkinStripParts.Gain, SkinStripParts.HardwareControls],
        // An application channel's gain is its channel volume.
        _ => [SkinStripParts.Identity, SkinStripParts.Meter, SkinStripParts.Gain],
    };

    /// <summary>
    /// Whether this kind's gain part is the channel volume rather than the
    /// interface's gain: an application or capture channel. It is drawn only
    /// once the daemon reports a volume for the channel.
    /// </summary>
    public static bool IsChannelVolumeKind(InputStripKind kind) => kind is InputStripKind.Application or InputStripKind.Capture;

    /// <summary>What a channel is, as the rack draws it.</summary>
    public static InputStripKind KindOf(ChannelViewModel channel) => channel switch
    {
        { IsHardware: true, Id: "xlr1" } => InputStripKind.Xlr1,
        { IsHardware: true, Id: "xlr2" } => InputStripKind.Xlr2,
        { IsHardware: true, Id: "aux" } => InputStripKind.AuxIn,
        { CaptureSource: not null } => InputStripKind.Capture,
        _ => InputStripKind.Application,
    };

    private void OnChannelsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Sync();

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        // XLR 1's software low cut and ClipGuard keep its switches part while it is bound elsewhere.
        if (e.PropertyName is nameof(MainViewModel.ShowSoftLowCut) or nameof(MainViewModel.ShowSoftClipGuard))
            foreach (InputStripViewModel strip in Strips.Where(s => s.Kind == InputStripKind.Xlr1)) strip.RefreshSpec();
    }

    /// <summary>
    /// Follow the channels: keep a channel's strip while it is the same kind,
    /// make a new one when the kind changed, and keep the mixer's order.
    /// </summary>
    internal void Sync()
    {
        ChannelViewModel[] wanted = [.. _main.Channels];
        for (int i = Strips.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Strips[i].Channel) || Strips[i].Kind != KindOf(Strips[i].Channel))
            {
                Strips[i].Detach();
                Strips.RemoveAt(i);
            }
        for (int i = 0; i < wanted.Length; i++)
        {
            int at = -1;
            for (int j = 0; j < Strips.Count; j++)
                if (ReferenceEquals(Strips[j].Channel, wanted[i])) { at = j; break; }
            if (at == i) continue;
            if (at >= 0) Strips.Move(at, i);
            else Strips.Insert(i, new InputStripViewModel(this, wanted[i], KindOf(wanted[i])));
        }
    }

    public void Dispose()
    {
        _main.Channels.CollectionChanged -= OnChannelsChanged;
        _main.PropertyChanged -= OnMainChanged;
        foreach (InputStripViewModel strip in Strips) strip.Detach();
        Strips.Clear();
    }
}

/// <summary>One channel in the input rack.</summary>
public sealed class InputStripViewModel : ViewModelBase
{
    private readonly InputRackViewModel _rack;
    private bool _detached;

    internal InputStripViewModel(InputRackViewModel rack, ChannelViewModel channel, InputStripKind kind)
    {
        _rack = rack;
        Channel = channel;
        Kind = kind;
        Spec = CreateSpec();
        channel.PropertyChanged += OnChannelChanged;
        channel.Appearance.PropertyChanged += OnAppearanceChanged;
        rack.Main.PropertyChanged += OnMainChanged;
    }

    public ChannelViewModel Channel { get; }

    /// <summary>The item's appearance, where the <c>layoutAccent</c> style looks for the colour the user chose.</summary>
    public LayoutAppearanceViewModel Appearance => Channel.Appearance;

    public InputStripKind Kind { get; }

    /// <summary>The window's model: the hardware controls bind to its properties, exactly as the INPUTS panel does.</summary>
    public MainViewModel Main => _rack.Main;

    /// <summary>How the strip is drawn: the skin's parts, less the ones this kind of channel does not have.</summary>
    public StripSpec Spec { get; private set; }

    private StripSpec CreateSpec() => StripSpec.From(_rack.Options, Orientation.Vertical,
        [.. InputRackViewModel.PartsOf(Kind).Where(Available)], SkinStripParts.Gain);

    /// <summary>
    /// Whether this channel has the part now. The gain and the switches belong
    /// to the interface's input, so a strip bound to another source has
    /// neither, except XLR 1's software low cut and ClipGuard, and a capture
    /// channel carrying the interface's microphone has both. A channel's own
    /// volume shows once the daemon reports one.
    /// </summary>
    private bool Available(string part) => (Kind, part) switch
    {
        (InputStripKind.Xlr1, SkinStripParts.HardwareControls) => CarriesUnit || CarriesExtraUnit || Main.ShowSoftLowCut || Main.ShowSoftClipGuard,
        (InputStripKind.Xlr1 or InputStripKind.Xlr2, SkinStripParts.Gain) => CarriesUnit || CarriesExtraUnit,
        (InputStripKind.Xlr2 or InputStripKind.Capture, SkinStripParts.HardwareControls) => CarriesUnit || CarriesExtraUnit,
        (InputStripKind.Capture or InputStripKind.Application, SkinStripParts.Gain) => CarriesUnit || CarriesExtraUnit || Channel.HasVolume,
        _ => true,
    };

    /// <summary>The interface's input this strip carries, whose controls it draws, or null.</summary>
    public UnitInputViewModel? Unit => Channel.ShownUnitInput is 1 or 2 ? Main.UnitInputOf(Channel.ShownUnitInput) : null;

    /// <summary>The strip draws the interface's gain and switches.</summary>
    public bool CarriesUnit => Unit is not null;

    /// <summary>An additional interface's input this strip carries, whose gain and switches it draws, or null.</summary>
    public WaveUnitInputViewModel? ExtraUnit => Channel.ExtraUnitInput;

    /// <summary>The strip draws an additional interface's gain and switches.</summary>
    public bool CarriesExtraUnit => Channel.CarriesExtraUnit;

    /// <summary>A capture channel carrying the interface's microphone draws its own volume beside the interface's gain.</summary>
    public bool ShowsVolume => Channel.HasVolume;

    internal void RefreshSpec()
    {
        Spec = CreateSpec();
        Raise(nameof(Spec));
    }

    /// <summary>The input's plugin chain, for the two XLR inputs; null for every other channel.</summary>
    public InsertsViewModel? Inserts => Kind switch
    {
        InputStripKind.Xlr1 => _rack.Main.Inserts,
        InputStripKind.Xlr2 => _rack.Main.Inserts2,
        _ => null,
    };

    /// <summary>The chain's key, as the chain window names it.</summary>
    public string? InsertsKey => Kind switch
    {
        InputStripKind.Xlr1 => "xlr1",
        InputStripKind.Xlr2 => "xlr2",
        _ => null,
    };

    /// <summary>True when the strip draws this part.</summary>
    public bool Has(string part) => Spec.Parts.Contains(part, StringComparer.Ordinal);

    /// <summary>
    /// True while the view draws the channel. The second XLR input also needs
    /// an interface with two, which is the rule the INPUTS panel shows it by,
    /// or a source of its own.
    /// </summary>
    public bool Shown => Channel.DisplayVisible && (Kind != InputStripKind.Xlr2 || _rack.Main.HasXlr2 || Channel.IsSourceBound);

    /// <summary>True when the strip shows no Arrange handle: the channel keeps its place.</summary>
    public bool HandleHidden => !Channel.IsEditable;

    public bool ShowsIcon => Spec.WantsIcon && Channel.Appearance.HasIcon;

    public bool ShowsName => Spec.WantsName || !Channel.Appearance.HasIcon;

    public string LeftLevelName => Localizer.Format("ChannelLevelLeft", Channel.Name);

    public string RightLevelName => Localizer.Format("ChannelLevelRight", Channel.Name);

    private void OnChannelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChannelViewModel.DisplayVisible): Raise(nameof(Shown)); break;
            case nameof(ChannelViewModel.IsEditable): Raise(nameof(HandleHidden)); break;
            case nameof(ChannelViewModel.Name): Raise(nameof(LeftLevelName)); Raise(nameof(RightLevelName)); break;
            case nameof(ChannelViewModel.HasVolume): RefreshSpec(); Raise(nameof(ShowsVolume)); break;
            case nameof(ChannelViewModel.ShownUnitInput):
                Raise(nameof(Unit)); Raise(nameof(CarriesUnit)); RefreshSpec();
                break;
            case nameof(ChannelViewModel.ExtraUnitInput):
                Raise(nameof(ExtraUnit)); Raise(nameof(CarriesExtraUnit)); RefreshSpec();
                break;
            case nameof(ChannelViewModel.IsSourceBound): Raise(nameof(Shown)); break;
            case nameof(ChannelViewModel.IsHardware) or nameof(ChannelViewModel.CaptureSource):
                if (!_detached && InputRackViewModel.KindOf(Channel) != Kind) _rack.Sync();
                break;
        }
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.HasXlr2) && Kind == InputStripKind.Xlr2) Raise(nameof(Shown));
    }

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        Raise(nameof(ShowsIcon));
        Raise(nameof(ShowsName));
    }

    internal void Detach()
    {
        if (_detached) return;
        _detached = true;
        Channel.PropertyChanged -= OnChannelChanged;
        Channel.Appearance.PropertyChanged -= OnAppearanceChanged;
        _rack.Main.PropertyChanged -= OnMainChanged;
    }
}
