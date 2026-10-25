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
    /// <summary>The microphone's level before any processing, against a target band, beside the interface's gain.</summary>
    Level,
    /// <summary>A take, its waveform, raw and processed, and hearing yourself through the chain.</summary>
    Listen,
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
/// waveform and the loop's playhead. The interface's gain is the main
/// window's own control for the input this channel carries, set from
/// outside as the carrier changes.
/// </summary>
public sealed class MicCheckViewModel : ViewModelBase
{
    private readonly DaemonClient _client;
    private bool _busy, _active;
    private int _connectionEpoch;
    private string _mode = "idle";
    private double _seconds;
    private string? _error;

    public MicCheckViewModel(DaemonClient client, string channel)
    {
        _client = client;
        Channel = channel;
        Steps = [new(this, MicCheckStep.Level, Localizer.Text("MicCheckStepLevel")),
            new(this, MicCheckStep.Listen, Localizer.Text("MicCheckStepListen"))];
    }

    /// <summary>The microphone channel this check belongs to.</summary>
    public string Channel { get; }

    // --- steps ---

    public ObservableCollection<MicCheckStepItem> Steps { get; }

    private MicCheckStep _step = MicCheckStep.Level;
    public MicCheckStep Step
    {
        get => _step;
        set
        {
            if (!Set(ref _step, value)) return;
            foreach (MicCheckStepItem item in Steps) item.Refresh();
            Raise(nameof(ShowsLevel)); Raise(nameof(ShowsListen));
        }
    }
    public bool ShowsLevel => _step == MicCheckStep.Level;
    public bool ShowsListen => _step == MicCheckStep.Listen;

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
        internal set { if (Set(ref _unit, value)) { Raise(nameof(CarriesUnit)); Raise(nameof(HasGain)); Raise(nameof(NoGain)); } }
    }
    public bool CarriesUnit => _unit is not null;

    private WaveUnitInputViewModel? _extraUnit;
    /// <summary>An additional unit's input this channel carries, or null.</summary>
    public WaveUnitInputViewModel? ExtraUnit
    {
        get => _extraUnit;
        internal set { if (Set(ref _extraUnit, value)) { Raise(nameof(CarriesExtraUnit)); Raise(nameof(HasGain)); Raise(nameof(NoGain)); } }
    }
    public bool CarriesExtraUnit => _extraUnit is { HasGain: true };

    public bool HasGain => CarriesUnit || CarriesExtraUnit;
    public bool NoGain => !HasGain;

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
