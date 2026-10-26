using OpenXLR.UI.Localization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>The steps of Mic check, in the order the window offers them.</summary>
public enum MicCheckStep
{
    /// <summary>Where the microphone goes for the unit it is on, and phantom power when it needs it.</summary>
    Position,
    /// <summary>The microphone's level before any processing, against a target band, beside the interface's gain.</summary>
    Level,
    /// <summary>A take, its waveform, raw and processed, and hearing yourself through the chain.</summary>
    Listen,
    /// <summary>The EQ, low cut and ClipGuard, the insert chain and the starting presets.</summary>
    Shape,
}

/// <summary>What the microphone of a Mic check is, as far as the Position step's tips go.</summary>
public enum MicCheckUnitKind
{
    /// <summary>On no unit OpenXLR drives: a USB microphone, a headset or an interface of another make.</summary>
    None,
    /// <summary>The built-in capsule of a Wave:3 or Wave:1.</summary>
    Capsule,
    /// <summary>A microphone on an XLR jack of a Wave XLR, a Wave XLR Pro or an XLR Dock.</summary>
    XlrJack,
}

/// <summary>One entry of the step bar: its name and whether it is the step shown.</summary>
public sealed class MicCheckStepItem(MicCheckViewModel owner, MicCheckStep step, string name) : ViewModelBase
{
    public MicCheckStep Step { get; } = step;
    public string Name { get; } = name;

    /// <summary>The step shown; choosing one shows it, and the step shown cannot be unchosen.</summary>
    public bool IsSelected
    {
        get => owner.Step == Step;
        set { if (value) owner.Step = Step; Raise(nameof(IsSelected)); }
    }

    internal void Refresh() => Raise(nameof(IsSelected));
}

/// <summary>
/// One microphone's Mic check. The daemon owns the session, the take and
/// the meters; this view sends the actions and shows what the daemon
/// reports: the Sound Check state, the meter before the chain, the take's
/// waveform and the loop's playhead. The interface's gain, phantom power,
/// low cut and ClipGuard are the main window's own controls for the input
/// this channel carries, and the EQ is the channel's own editor model, all
/// set from outside as the carrier changes; the insert chain is the one this
/// check belongs to.
/// </summary>
public sealed class MicCheckViewModel : ViewModelBase
{
    private readonly DaemonClient _client;
    private bool _busy, _active;
    private int _connectionEpoch;
    private string _mode = "idle";
    private double _seconds;
    private string? _error;

    /// <param name="chain">The channel's insert chain, which the Shape step lists and edits; null shows no chain.</param>
    public MicCheckViewModel(DaemonClient client, string channel, InsertsViewModel? chain = null)
    {
        _client = client;
        Channel = channel;
        Chain = chain;
        Steps = [new(this, MicCheckStep.Position, Localizer.Text("MicCheckStepPosition")),
            new(this, MicCheckStep.Level, Localizer.Text("MicCheckStepLevel")),
            new(this, MicCheckStep.Listen, Localizer.Text("MicCheckStepListen")),
            new(this, MicCheckStep.Shape, Localizer.Text("MicCheckStepShape"))];
    }

    /// <summary>The microphone channel this check belongs to.</summary>
    public string Channel { get; }

    // --- steps ---

    public ObservableCollection<MicCheckStepItem> Steps { get; }

    private MicCheckStep _step = MicCheckStep.Position;
    /// <summary>The step shown. A check starts on Position and keeps its last step while the UI runs.</summary>
    public MicCheckStep Step
    {
        get => _step;
        set
        {
            if (!Set(ref _step, value)) return;
            foreach (MicCheckStepItem item in Steps) item.Refresh();
            Raise(nameof(ShowsPosition)); Raise(nameof(ShowsLevel)); Raise(nameof(ShowsListen)); Raise(nameof(ShowsShape));
            // The editor button needs the catalogue to know which plugins have an editor of their own.
            if (value == MicCheckStep.Shape) Chain?.EnsurePluginsLoaded();
        }
    }
    public bool ShowsPosition => _step == MicCheckStep.Position;
    public bool ShowsLevel => _step == MicCheckStep.Level;
    public bool ShowsListen => _step == MicCheckStep.Listen;
    public bool ShowsShape => _step == MicCheckStep.Shape;

    // --- the session ---

    public bool Active => _active;
    public bool CanRecord => !_busy && _mode != "recording";
    public bool CanLoop => !_busy && _active && _seconds >= 0.1;
    public bool CanLive => !_busy;
    public bool CanStop => !_busy && (_active || Error is not null);
    public bool CanSwitch => !_busy;
    public string Status => _mode switch
    {
        "recording" => Localizer.Format("SoundCheckRecording", _seconds),
        "looping" => Localizer.Format("MicCheckLooping", _seconds),
        "live" when _active && _seconds >= 0.1 => Localizer.Format("MicCheckReady", _seconds),
        _ => Localizer.Text("MicCheckIdle"),
    };
    public string? Error { get => _error; private set => Set(ref _error, value); }

    private bool _raw;
    /// <summary>The session plays the signal past the chain.</summary>
    public bool Raw => _raw;
    /// <summary>The Processed half of the switch: choosing it sends processed.</summary>
    public bool ListenProcessed { get => !_raw; set { if (value && _raw) _ = SetRawAsync(false); else Raise(nameof(ListenProcessed)); } }
    /// <summary>The Raw half of the switch: choosing it sends raw.</summary>
    public bool ListenRaw { get => _raw; set { if (value && !_raw) _ = SetRawAsync(true); else Raise(nameof(ListenRaw)); } }

    private bool _hearMyself;
    /// <summary>Hear myself, as the daemon holds it; the check box sends the change.</summary>
    public bool HearMyself
    {
        get => _hearMyself;
        set { if (value != _hearMyself) _ = SetHearMyselfAsync(value); }
    }

    private bool _heard;
    /// <summary>A session runs and the microphone reaches no monitor output, so neither a take nor the live microphone is heard.</summary>
    public bool NotHeard => _active && !_heard;

    private double? _latencyMs;
    /// <summary>What hearing yourself through this computer adds, said in the window; empty while the daemon has no figure.</summary>
    public string LatencyText => _latencyMs is double ms
        ? Localizer.Format("MicCheckHearMyselfLatency", ms) : Localizer.Text("MicCheckHearMyselfLatencyUnknown");

    private bool _proDirect;
    /// <summary>This channel carries the Wave XLR Pro's first input, whose zero-latency path is off during a session.</summary>
    public bool ProDirect { get => _proDirect; internal set => Set(ref _proDirect, value); }

    // --- the take ---

    private int _take, _requestedTake;
    private IReadOnlyList<double>? _peaks;
    /// <summary>The take's waveform, or null before one is recorded.</summary>
    public IReadOnlyList<double>? Peaks { get => _peaks; private set { if (Set(ref _peaks, value)) Raise(nameof(HasTake)); } }
    public bool HasTake => _peaks is { Count: > 0 };

    private double _position = -1;
    /// <summary>Where the loop is across the take, 0 to 1, or -1 while it does not loop.</summary>
    public double Position { get => _position; private set => Set(ref _position, value); }

    // --- the level before the chain ---

    private double _inputLevel, _inputPeak;
    /// <summary>The microphone's RMS before the chain, on the meters' 0 to 1 scale.</summary>
    public double InputLevel { get => _inputLevel; private set => Set(ref _inputLevel, value); }
    /// <summary>The microphone's peak before the chain, on the same scale.</summary>
    public double InputPeak
    {
        get => _inputPeak;
        private set { if (Set(ref _inputPeak, value)) Raise(nameof(PeakText)); }
    }
    public string PeakText => _inputPeak <= 0 ? Localizer.Text("MicCheckPeakSilent") : Localizer.Format("MicCheckPeak", _inputPeak * 60 - 60);

    private bool _clipped;
    /// <summary>A sample reached the clip level since the light was last cleared.</summary>
    public bool Clipped { get => _clipped; private set { if (Set(ref _clipped, value)) Raise(nameof(ClipName)); } }
    public string ClipName => _clipped ? Localizer.Text("MicCheckClipLit") : Localizer.Text("MicCheckClipDark");

    public void ClearClip() => Clipped = false;

    // --- the interface's gain ---

    private UnitInputViewModel? _unit;
    /// <summary>The primary unit's input this channel carries, whose gain the Level step shows, or null.</summary>
    public UnitInputViewModel? Unit
    {
        get => _unit;
        internal set { if (Set(ref _unit, value)) { Raise(nameof(CarriesUnit)); Raise(nameof(HasGain)); Raise(nameof(NoGain)); RaiseCarrier(); } }
    }
    public bool CarriesUnit => _unit is not null;

    private WaveUnitInputViewModel? _extraUnit;
    /// <summary>An additional unit's input this channel carries, or null.</summary>
    public WaveUnitInputViewModel? ExtraUnit
    {
        get => _extraUnit;
        internal set { if (Set(ref _extraUnit, value)) { Raise(nameof(CarriesExtraUnit)); Raise(nameof(HasGain)); Raise(nameof(NoGain)); RaiseCarrier(); } }
    }
    public bool CarriesExtraUnit => _extraUnit is { HasGain: true };

    public bool HasGain => CarriesUnit || CarriesExtraUnit;
    public bool NoGain => !HasGain;

    // --- Position: the unit the microphone is on, tips for it, phantom power ---

    private MicCheckUnitKind _unitKind;
    private string _unitName = "";
    private int _unitInput = 1;

    /// <summary>What the microphone is, for the tips: a unit's capsule, on a unit's XLR jack, or on no unit OpenXLR drives.</summary>
    public MicCheckUnitKind UnitKind => _unitKind;

    /// <summary>
    /// The unit this channel's microphone is on, its name and the input; set
    /// after every state from the same carrier as the gain.
    /// </summary>
    internal void Place(MicCheckUnitKind kind, string? name, int input)
    {
        name ??= "";
        if (kind == _unitKind && name == _unitName && input == _unitInput) return;
        _unitKind = kind; _unitName = name; _unitInput = input;
        Raise(nameof(UnitKind)); Raise(nameof(PositionIntro)); Raise(nameof(PositionTips)); RaiseCarrier();
    }

    /// <summary>The Position step's first line: which unit the microphone is on, or that OpenXLR drives none.</summary>
    public string PositionIntro => _unitKind switch
    {
        MicCheckUnitKind.Capsule => Localizer.Format("MicCheckPositionCapsule", UnitLabel),
        MicCheckUnitKind.XlrJack => Localizer.Format("MicCheckPositionXlr", UnitLabel, _unitInput),
        _ => Localizer.Text("MicCheckPositionNone"),
    };

    private string UnitLabel => _unitName.Length > 0 ? _unitName : Localizer.Text("MicCheckYourInterface");

    /// <summary>
    /// Distance and angle tips for this microphone: first what its kind of
    /// unit asks for, then what holds for any speech microphone. General
    /// practice, with no distances in centimetres, since none were measured.
    /// </summary>
    public IReadOnlyList<string> PositionTips
    {
        get
        {
            var tips = new List<string>();
            switch (_unitKind)
            {
                case MicCheckUnitKind.Capsule:
                    tips.Add(Localizer.Text("MicCheckTipCapsuleFront"));
                    tips.Add(Localizer.Text("MicCheckTipCapsuleRoom"));
                    break;
                case MicCheckUnitKind.XlrJack:
                    tips.Add(Localizer.Text("MicCheckTipXlrAddress"));
                    tips.Add(Localizer.Text("MicCheckTipXlrDistance"));
                    break;
                default:
                    tips.Add(Localizer.Text("MicCheckTipOtherDevice"));
                    break;
            }
            tips.Add(Localizer.Text("MicCheckTipDistance"));
            tips.Add(Localizer.Text("MicCheckTipAngle"));
            tips.Add(Localizer.Text("MicCheckTipNoise"));
            return tips;
        }
    }

    /// <summary>The unit behind the microphone has phantom power OpenXLR switches.</summary>
    public bool HasPhantom => _unit is { CapPhantom: true } || _extraUnit is { HasPhantom: true };

    /// <summary>A built-in capsule needs no phantom power, so the step leaves it out; any other microphone may need it.</summary>
    public bool ShowsPhantom => _unitKind != MicCheckUnitKind.Capsule;

    /// <summary>Phantom power is said, but OpenXLR cannot switch it: set it on the microphone's own interface.</summary>
    public bool PhantomElsewhere => ShowsPhantom && !HasPhantom;

    // --- Shape: the EQ, low cut and ClipGuard, the chain and the presets ---

    /// <summary>The channel's insert chain, or null.</summary>
    public InsertsViewModel? Chain { get; }

    private MainViewModel? _software;
    /// <summary>
    /// The main window's model while this is XLR 1's check, whose software
    /// low cut and ClipGuard sit after the EQ; null on every other channel.
    /// </summary>
    public MainViewModel? Software
    {
        get => _software;
        internal set { if (Set(ref _software, value)) RaiseCarrier(); }
    }

    /// <summary>The unit's own low cut or ClipGuard is offered for this microphone; they act before the take.</summary>
    public bool HasUnitFilters => _unit is { CapLowCut: true } or { CapClipGuard: true }
        || _extraUnit is { HasLowCut: true } or { HasClipGuard: true };

    /// <summary>XLR 1's software low cut or ClipGuard applies; they act after the EQ, in the loop.</summary>
    public bool HasSoftwareFilters => _software is { ShowSoftwareControls: true };

    /// <summary>This microphone has neither a low cut nor ClipGuard outside the EQ.</summary>
    public bool NoFilters => !HasUnitFilters && !HasSoftwareFilters;

    /// <summary>
    /// What the carrier offers changed: called after every state, since the
    /// capabilities behind these come with it.
    /// </summary>
    internal void RaiseCarrier()
    {
        Raise(nameof(HasPhantom)); Raise(nameof(ShowsPhantom)); Raise(nameof(PhantomElsewhere));
        Raise(nameof(HasUnitFilters)); Raise(nameof(HasSoftwareFilters)); Raise(nameof(NoFilters));
    }

    private MicEqViewModel? _eq;
    private MicEq? _beforePreset;
    private string? _presetStatus;

    /// <summary>The channel's EQ editor model, the one the EQ window edits; null while the daemon reports no EQ for it.</summary>
    public MicEqViewModel? Eq
    {
        get => _eq;
        internal set
        {
            if (!Set(ref _eq, value)) return;
            // An undo belongs to the EQ it was taken from.
            _beforePreset = null;
            _presetStatus = null;
            Raise(nameof(HasEq)); Raise(nameof(NoEq)); RaisePresets();
        }
    }
    public bool HasEq => _eq is not null;
    public bool NoEq => _eq is null;

    /// <summary>The starting presets, in order.</summary>
    public IReadOnlyList<MicCheckPreset> Presets => MicCheckPresets.All;

    public bool CanApplyPreset => _eq is not null;

    /// <summary>A preset was applied and its EQ can be put back.</summary>
    public bool CanUndoPreset => _eq is not null && _beforePreset is not null;

    /// <summary>What the last preset press did, or how the presets work before one.</summary>
    public string PresetStatus => _presetStatus ?? Localizer.Text("MicCheckPresetHint");

    /// <summary>
    /// Put a preset on the EQ as one <c>setEq</c>, so it is heard at once,
    /// on the live microphone or the looped take, and keep the EQ it
    /// replaces for one undo. Edits made after it are not kept by the undo.
    /// </summary>
    public void ApplyPreset(MicCheckPreset preset)
    {
        if (_eq is not { } eq) return;
        _beforePreset = eq.Current;
        eq.Replace(preset.For(eq.Limits));
        _presetStatus = Localizer.Format("MicCheckPresetApplied", preset.Name);
        RaisePresets();
    }

    /// <summary>Put back the EQ the last preset replaced, again as one <c>setEq</c>.</summary>
    public void UndoPreset()
    {
        if (_eq is not { } eq || _beforePreset is not { } before) return;
        _beforePreset = null;
        eq.Replace(before);
        _presetStatus = Localizer.Text("MicCheckPresetUndone");
        RaisePresets();
    }

    private void RaisePresets()
    {
        Raise(nameof(CanApplyPreset)); Raise(nameof(CanUndoPreset)); Raise(nameof(PresetStatus));
    }

    /// <summary>The daemon's <c>mixer.soundCheck</c>; null or another channel's session reads as idle.</summary>
    public void Apply(JsonNode? state)
    {
        bool own = state?["channel"]?.GetValue<string>() == Channel;
        _mode = own ? state?["mode"]?.GetValue<string>() ?? "idle" : "idle";
        _active = own && _mode != "idle";
        _seconds = _active ? state?["seconds"]?.GetValue<double>() ?? 0 : 0;
        Error = own ? state?["error"]?.GetValue<string>() : null;
        _raw = _active && (state?["raw"]?.GetValue<bool>() ?? false);
        _hearMyself = _active && (state?["hearMyself"]?.GetValue<bool>() ?? false);
        _heard = _active && (state?["heard"]?.GetValue<bool>() ?? false);
        _latencyMs = _active ? state?["monitorLatencyMs"]?.GetValue<double>() : null;
        // The waveform arrives once, when the daemon finishes the take; a
        // state that names a take this view never received asks for it.
        int take = _active ? state?["take"]?.GetValue<int>() ?? 0 : 0;
        if (take == 0) { _take = _requestedTake = 0; Peaks = null; }
        else if (take != _take && take != _requestedTake)
        {
            _requestedTake = take;
            _ = _client.RequestSoundCheckTakeAsync();
        }
        if (_mode != "looping") Position = -1;
        Raise(nameof(Active)); Raise(nameof(Status)); Raise(nameof(Raw)); Raise(nameof(ListenProcessed)); Raise(nameof(ListenRaw));
        Raise(nameof(HearMyself)); Raise(nameof(NotHeard)); Raise(nameof(LatencyText));
        RaiseButtons();
    }

    /// <summary>A <c>soundCheckTake</c> message: this channel's take replaces the one shown.</summary>
    public void ApplyTake(JsonNode? message)
    {
        if (message?["channel"]?.GetValue<string>() != Channel || !_active) return;
        int take = message["take"]?.GetValue<int>() ?? 0;
        if (take == 0) return;
        _take = take;
        Peaks = message["peaks"] is JsonArray peaks
            ? [.. peaks.Select(p => Number(p) is double d ? Math.Clamp(d, 0, 1) : 0)]
            : null;
    }

    /// <summary>The meters' <c>in:</c> entry for this channel: rms, peak and the clip flag.</summary>
    public void ApplyInputLevel(JsonArray level)
    {
        double Read(int i) => level.Count > i && Number(level[i]) is double d ? Math.Clamp(d, 0, 1) : 0;
        InputLevel = Read(0);
        InputPeak = Read(1);
        if (Read(2) >= 1) Clipped = true;
    }

    /// <summary>The meters' <c>take:</c> entry for this channel, while the take loops.</summary>
    public void ApplyPosition(JsonArray position)
    {
        if (_mode == "looping" && position.Count > 0 && Number(position[0]) is double at)
            Position = Math.Clamp(at, 0, 1);
    }

    /// <summary>A finite number in a JSON value, written as an integer or not; null for anything else.</summary>
    private static double? Number(JsonNode? node)
        => node is JsonValue v && (v.TryGetValue(out double d) || v.TryGetValue(out int i) && (d = i) == i) && double.IsFinite(d) ? d : null;

    /// <summary>
    /// The connection dropped: a reply still on its way belongs to the old
    /// connection and must not change what this one shows.
    /// </summary>
    public void Reset()
    {
        _connectionEpoch++;
        _busy = false;
        Apply(null);
        InputLevel = InputPeak = 0;
        Clipped = false;
        // The daemon may hold another EQ by the time it is back.
        _beforePreset = null;
        _presetStatus = null;
        RaisePresets();
    }

    private void RaiseButtons()
    {
        Raise(nameof(CanRecord)); Raise(nameof(CanLoop)); Raise(nameof(CanLive)); Raise(nameof(CanStop)); Raise(nameof(CanSwitch));
    }

    /// <summary>One request at a time; its error shows until the next one or the state clears it.</summary>
    private async Task SendAsync(Func<Task<string?>> request)
    {
        if (_busy) return;
        int epoch = _connectionEpoch;
        _busy = true; RaiseButtons();
        try
        {
            string? error = await request();
            if (epoch == _connectionEpoch) Error = error;
        }
        finally
        {
            if (epoch == _connectionEpoch) { _busy = false; RaiseButtons(); }
            // A refused switch snaps back to what the daemon holds.
            Raise(nameof(ListenProcessed)); Raise(nameof(ListenRaw)); Raise(nameof(HearMyself));
        }
    }

    public Task RunAsync(string action) => SendAsync(() => _client.SoundCheckAsync(Channel, action));

    public Task SetRawAsync(bool raw) => SendAsync(() => _client.SetSoundCheckRawAsync(Channel, raw));

    public Task SetHearMyselfAsync(bool on) => SendAsync(() => _client.SetSoundCheckHearMyselfAsync(Channel, on));

    /// <summary>Keep the meter before the chain running while the window shows it.</summary>
    public Task KeepMeterAsync() => _client.WatchInputMeterAsync(Channel);


    /// <summary>
    /// Closing can follow a pending record command; the stop is sent behind
    /// it rather than dropped because the buttons are disabled for a moment.
    /// </summary>
    public async Task StopOnCloseAsync()
    {
        int epoch = _connectionEpoch;
        string? error = await _client.SoundCheckAsync(Channel, "stop");
        if (epoch == _connectionEpoch) Error = error;
    }
}
