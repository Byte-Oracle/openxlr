using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>The three commands the EQ editor sends. The daemon client sends them; a test records them.</summary>
public interface IEqCommands
{
    Task SetEqAsync(string channel, MicEq eq);
    Task SetEqEnabledAsync(string channel, bool enabled);
    Task SetEqBandAsync(string channel, int band, EqBandChange change);
}

/// <summary>
/// A microphone's EQ as the window edits it: the switch, the four bands, the
/// backend that runs it and why it cannot. Every value it shows is one the
/// daemon reported, or one the user just set, snapped with the daemon's own
/// rules (<see cref="MicEq"/>), so the two agree; a report that comes back
/// with something else wins once it is not an echo of an older edit.
/// </summary>
public sealed class MicEqViewModel : ViewModelBase, IDisposable
{
    /// <summary>How often one band's live changes go to the daemon while it is dragged.</summary>
    public static readonly TimeSpan SendInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>How long an edit waits for the daemon to report it back before a different report wins.</summary>
    internal const int EchoMilliseconds = 1000;

    private readonly IEqCommands _commands;
    private readonly EqBandSender _sender;
    private bool _enabled, _reportedEnabled, _comparing, _available = true;
    private bool? _awaitingEnabled;
    private long _awaitingEnabledUntil;
    private string? _backend, _error;
    private string _channelName;
    private EqBandViewModel? _selected;

    public MicEqViewModel(IEqCommands commands, string channel, string channelName, TimeSpan? sendInterval = null)
    {
        _commands = commands;
        Channel = channel;
        _channelName = channelName;
        _sender = new EqBandSender(sendInterval ?? SendInterval, (band, change) => _ = _commands.SetEqBandAsync(channel, band, change));
        Bands = [.. Enumerable.Range(1, MicEq.BandCount).Select(n => new EqBandViewModel(this, n, MicEq.Default.Bands[n - 1]))];
        EqClipboard.Changed += OnClipboardChanged;
    }

    /// <summary>The channel id the commands name: <c>xlr1</c> or <c>xlr2</c>.</summary>
    public string Channel { get; }

    public string ChannelName
    {
        get => _channelName;
        internal set
        {
            if (!Set(ref _channelName, value)) return;
            Raise(nameof(Title));
            Raise(nameof(ButtonName));
        }
    }

    /// <summary>The editor's title.</summary>
    public string Title => Localizer.Format("EqWindowTitle", _channelName);

    public IReadOnlyList<EqBandViewModel> Bands { get; }

    /// <summary>The ranges and steps the daemon reports, or the model's own from a daemon that sends none.</summary>
    public EqLimits Limits { get; private set; } = EqLimits.Instance;

    /// <summary>The band the graph and the keyboard work on.</summary>
    public EqBandViewModel? SelectedBand
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            foreach (EqBandViewModel band in Bands) band.RaiseSelected();
        }
    }

    /// <summary>
    /// The EQ's switch. While a comparison bypasses it, the switch stays on:
    /// it says what the EQ will be when the comparison ends.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value && !_comparing) return;
            SetComparing(false, send: false);
            _enabled = value;
            Expect(value);
            _ = _commands.SetEqEnabledAsync(Channel, value);
            RaiseState();
        }
    }

    /// <summary>
    /// Bypass to compare: the daemon switches the EQ off while this is on and
    /// back on when it ends, the switch staying on throughout. Only an EQ that
    /// is on can be compared.
    /// </summary>
    public bool Comparing
    {
        get => _comparing;
        set
        {
            if (value && !_enabled) value = false;
            SetComparing(value, send: true);
        }
    }

    private void SetComparing(bool value, bool send)
    {
        if (_comparing == value) { Raise(nameof(Comparing)); return; }
        _comparing = value;
        if (send)
        {
            Expect(!value);
            _ = _commands.SetEqEnabledAsync(Channel, !value);
        }
        RaiseState();
    }

    private void Expect(bool enabled)
    {
        _awaitingEnabled = enabled;
        _awaitingEnabledUntil = Environment.TickCount64 + EchoMilliseconds;
    }

    /// <summary>Which backend runs the EQ: <c>software</c>, later <c>device</c>, null while nothing runs it.</summary>
    public string? Backend => _backend;

    /// <summary>False when the EQ cannot run, and <see cref="Error"/> says why.</summary>
    public bool Available => _available;

    public string? Error => _error;

    public bool HasError => _error is not null || !_available;

    /// <summary>The EQ is on and its stage runs: the button is lit.</summary>
    public bool IsLit => _enabled && !_comparing && _available && _error is null && _backend is not null;

    /// <summary>The EQ cannot run: the button takes the alert colour.</summary>
    public bool IsFaulty => !_available || _error is not null;

    /// <summary>The EQ button's name for a screen reader: whose EQ, and its state.</summary>
    public string ButtonName => IsFaulty ? Localizer.Format("EqButtonNameUnavailable", _channelName)
        : _enabled ? Localizer.Format("EqButtonNameOn", _channelName) : Localizer.Format("EqButtonNameOff", _channelName);

    /// <summary>The editor's footer: what runs the EQ, or why nothing does.</summary>
    public string Status => _error is { } error ? error
        : !_available ? Localizer.Text("EqUnavailable")
        : _comparing ? Localizer.Text("EqStatusComparing")
        : !_enabled ? Localizer.Text("EqStatusOff")
        : _backend switch
        {
            "software" => Localizer.Text("EqStatusSoftware"),
            "device" => Localizer.Text("EqStatusDevice"),
            _ => Localizer.Text("EqStatusStarting"),
        };

    private void RaiseState()
    {
        Raise(nameof(Enabled));
        Raise(nameof(Comparing));
        Raise(nameof(IsLit));
        Raise(nameof(IsFaulty));
        Raise(nameof(HasError));
        Raise(nameof(ButtonName));
        Raise(nameof(Status));
        Raise(nameof(Backend));
        Raise(nameof(Available));
        Raise(nameof(Error));
        Changed?.Invoke();
    }

    /// <summary>Something the graph draws changed: a band, the switch or a comparison.</summary>
    public event Action? Changed;

    internal void RaiseChanged() => Changed?.Invoke();

    /// <summary>
    /// Take a channel's <c>eq</c> from a state push, and the mixer's
    /// <c>eqLimits</c>. A band being dragged keeps what the user is doing, and
    /// a report that differs from an edit not yet reported back is taken as
    /// older than the edit, for a second at most.
    /// </summary>
    public void Apply(JsonNode eq, JsonNode? limits)
    {
        if (ReadLimits(limits) is { } read && read != Limits)
        {
            Limits = read;
            foreach (EqBandViewModel band in Bands) band.RaiseLimits();
        }
        bool enabled = eq["enabled"]?.GetValue<bool>() ?? false;
        _backend = eq["backend"]?.GetValue<string>();
        _available = eq["available"]?.GetValue<bool>() ?? true;
        _error = eq["error"]?.GetValue<string>();
        _reportedEnabled = enabled;
        if (_awaitingEnabled is bool expected && expected != enabled && Environment.TickCount64 < _awaitingEnabledUntil)
        {
            // An older report than the switch the user just moved.
        }
        else
        {
            _awaitingEnabled = null;
            if (_comparing)
            {
                // Switched on from elsewhere while compared: the comparison is over.
                if (enabled) { _comparing = false; _enabled = true; }
            }
            else _enabled = enabled;
        }
        if (eq["bands"] is JsonArray bands)
            for (int i = 0; i < Bands.Count && i < bands.Count; i++)
                if (ReadBand(bands[i]) is { } band) Bands[i].ApplyReported(band);
        RaiseState();
    }

    private static EqBand? ReadBand(JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            return new EqBand
            {
                Enabled = node["enabled"]?.GetValue<bool>() ?? false,
                Type = node["type"]?.GetValue<string>() ?? MicEq.Peak,
                Frequency = node["frequency"]?.GetValue<double>() ?? 1000,
                Gain = node["gain"]?.GetValue<double>() ?? 0,
                Q = node["q"]?.GetValue<double>() ?? MicEq.DefaultQ,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return null; }
    }

    private static EqLimits? ReadLimits(JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            EqLimits.EqRange Range(JsonNode? range, EqLimits.EqRange fallback) => range is null ? fallback
                : new(range["min"]?.GetValue<double>() ?? fallback.Min, range["max"]?.GetValue<double>() ?? fallback.Max,
                    range["step"]?.GetValue<double>() ?? fallback.Step);
            EqLimits defaults = EqLimits.Instance;
            JsonNode? q = node["q"];
            return defaults with
            {
                Frequency = Range(node["frequency"], defaults.Frequency),
                Gain = Range(node["gain"], defaults.Gain),
                Q = q is null ? defaults.Q : new(q["min"]?.GetValue<double>() ?? defaults.Q.Min,
                    q["max"]?.GetValue<double>() ?? defaults.Q.Max, q["log10Step"]?.GetValue<double>() ?? defaults.Q.Log10Step),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>The EQ as shown: the switch as the user set it and the four bands.</summary>
    public MicEq Current => new() { Enabled = _enabled, Bands = [.. Bands.Select(b => b.Band)] };

    /// <summary>Put an EQ on this microphone: one <c>setEq</c>, in place of any band change still waiting.</summary>
    public void Replace(MicEq eq)
    {
        eq = MicEq.Snap(eq);
        _sender.Clear();
        SetComparing(false, send: false);
        _enabled = eq.Enabled;
        Expect(eq.Enabled);
        for (int i = 0; i < Bands.Count; i++) Bands[i].ShowLocal(eq.Bands[i]);
        _ = _commands.SetEqAsync(Channel, eq);
        RaiseState();
    }

    /// <summary>Wave Link's four bands again, the switch left as it is.</summary>
    public void Reset() => Replace(MicEq.Default with { Enabled = _enabled });

    public void Copy() => EqClipboard.Value = Current;

    public bool CanPaste => EqClipboard.Value is not null;

    public void Paste()
    {
        if (EqClipboard.Value is { } copied) Replace(copied);
    }

    private void OnClipboardChanged() => Raise(nameof(CanPaste));

    /// <summary>Queue part of a band for the daemon, at most once a send interval per band.</summary>
    internal void Send(int band, EqBandChange change) => _sender.Queue(band, change);

    /// <summary>The window closes: a comparison ends and the EQ comes back on.</summary>
    public void EndComparison() => Comparing = false;

    public void Dispose()
    {
        EqClipboard.Changed -= OnClipboardChanged;
        _sender.Dispose();
    }
}

/// <summary>
/// One band of the editor. Its values are snapped and held inside the ranges
/// here, with the daemon's rules, so what is shown is what the daemon will
/// keep, and every change goes out through the EQ's rate limited sender.
/// </summary>
public sealed class EqBandViewModel : ViewModelBase
{
    private EqBand _band;
    private EqBand? _awaiting;
    private long _awaitingUntil;
    private EqBand? _reported;
    private bool _dragging;

    internal EqBandViewModel(MicEqViewModel owner, int number, EqBand band)
    {
        Owner = owner;
        Number = number;
        _band = band;
    }

    public MicEqViewModel Owner { get; }

    /// <summary>1 to 4, as the commands and the handles number the bands.</summary>
    public int Number { get; }

    public EqBand Band => _band;

    public bool Enabled
    {
        get => _band.Enabled;
        set { if (value != _band.Enabled) Edit(_band with { Enabled = value }, new EqBandChange(Enabled: value)); }
    }

    public string Type
    {
        get => _band.Type;
        set { if (value != _band.Type && MicEq.TypeIndex(value) >= 0) Edit(_band with { Type = value }, new EqBandChange(Type: value)); }
    }

    /// <summary>The type as the picker lists it.</summary>
    public EqTypeChoice TypeChoice
    {
        get => EqTypeChoice.Of(_band.Type);
        set { if (value is not null) Type = value.Type; }
    }

    public double Frequency
    {
        get => _band.Frequency;
        set => SetFrequency(value);
    }

    public double Gain
    {
        get => _band.Gain;
        set => SetGain(value);
    }

    public double Q
    {
        get => _band.Q;
        set => SetQ(value);
    }

    /// <summary>The frequency field: whole hertz.</summary>
    public decimal? FrequencyValue
    {
        get => (decimal)_band.Frequency;
        set { if (value is decimal v) SetFrequency((double)v); else Raise(); }
    }

    /// <summary>The gain field: quarter decibels.</summary>
    public decimal? GainValue
    {
        get => (decimal)_band.Gain;
        set { if (value is decimal v) SetGain((double)v); else Raise(); }
    }

    /// <summary>
    /// The Q field. Its spinner steps 0.01, finer than the grid near 1, so a
    /// value that snaps back onto the Q it came from moves one grid step its
    /// way instead, and the spinner always moves.
    /// </summary>
    public decimal? QValue
    {
        get => Math.Round((decimal)_band.Q, 2);
        set
        {
            if (value is not decimal v) { Raise(); return; }
            double wanted = (double)v, snapped = MicEq.SnapQ(Math.Clamp(wanted, Owner.Limits.Q.Min, Owner.Limits.Q.Max));
            if (snapped == _band.Q && Math.Abs(wanted - _band.Q) > 1e-9) StepQ(wanted > _band.Q ? 1 : -1);
            else SetQ(wanted);
        }
    }

    public decimal FrequencyMinimum => (decimal)Owner.Limits.Frequency.Min;
    public decimal FrequencyMaximum => (decimal)Owner.Limits.Frequency.Max;
    public decimal GainMinimum => (decimal)Owner.Limits.Gain.Min;
    public decimal GainMaximum => (decimal)Owner.Limits.Gain.Max;
    public decimal GainIncrement => (decimal)Owner.Limits.Gain.Step;
    public decimal QMinimum => (decimal)Owner.Limits.Q.Min;
    public decimal QMaximum => (decimal)Owner.Limits.Q.Max;

    /// <summary>The fields' names for a screen reader, each with the band's number.</summary>
    public string SwitchName => Localizer.Format("EqBandSwitchName", Number);
    public string TypeFieldName => Localizer.Format("EqBandTypeName", Number);
    public string FrequencyName => Localizer.Format("EqBandFrequencyName", Number);
    public string GainName => Localizer.Format("EqBandGainName", Number);
    public string QName => Localizer.Format("EqBandQName", Number);

    internal void RaiseLimits()
    {
        foreach (string name in new[] { nameof(FrequencyMinimum), nameof(FrequencyMaximum), nameof(GainMinimum), nameof(GainMaximum),
                     nameof(GainIncrement), nameof(QMinimum), nameof(QMaximum) })
            Raise(name);
    }

    /// <summary>The type takes a gain: low shelf, bell and high shelf. The others ignore theirs and keep it.</summary>
    public bool UsesGain => MicEq.UsesGain(_band.Type);

    public bool IsSelected => ReferenceEquals(Owner.SelectedBand, this);

    internal void RaiseSelected() => Raise(nameof(IsSelected));

    public string TypeName => EqTypeChoice.Of(_band.Type).Name;

    public string FrequencyText => EqText.Frequency(_band.Frequency);

    public string GainText => EqText.Gain(_band.Gain);

    public string QText => EqText.Q(_band.Q);

    /// <summary>A handle's name for a screen reader: "Band 2, Bell, 310 Hz, 0 dB, Q 0.70", and off when it is.</summary>
    public string Description
    {
        get
        {
            string values = UsesGain
                ? Localizer.Format("EqBandValues", TypeName, FrequencyText, GainText, QText)
                : Localizer.Format("EqBandValuesNoGain", TypeName, FrequencyText, QText);
            return _band.Enabled ? Localizer.Format("EqBandName", Number, values) : Localizer.Format("EqBandNameOff", Number, values);
        }
    }

    /// <summary>The handle's tooltip: its values, then what the pointer does with it.</summary>
    public string Tip => Description + "\n" + (UsesGain ? Localizer.Text("EqHandleTip") : Localizer.Text("EqHandleTipNoGain"));

    /// <summary>A drag is under way: reports keep their hands off the values until it ends.</summary>
    public bool Dragging => _dragging;

    public void BeginDrag()
    {
        _dragging = true;
        Owner.SelectedBand = this;
    }

    public void EndDrag()
    {
        _dragging = false;
        // A report that came in meanwhile and differs from the last edit is
        // older than it; the echo of the last edit settles the values.
        if (_reported is { } reported && _awaiting is null) Show(reported);
    }

    public void SetFrequency(double hz)
    {
        EqLimits.EqRange range = Owner.Limits.Frequency;
        double value = Math.Clamp(MicEq.SnapFrequency(hz), range.Min, range.Max);
        if (value != _band.Frequency) Edit(_band with { Frequency = value }, new EqBandChange(Frequency: value));
        else Raise(nameof(FrequencyValue));
    }

    public void SetGain(double db)
    {
        EqLimits.EqRange range = Owner.Limits.Gain;
        double value = Math.Clamp(MicEq.SnapGain(db), range.Min, range.Max);
        if (value != _band.Gain) Edit(_band with { Gain = value }, new EqBandChange(Gain: value));
        else Raise(nameof(GainValue));
    }

    public void SetQ(double q)
    {
        double value = MicEq.SnapQ(Math.Clamp(q, Owner.Limits.Q.Min, Owner.Limits.Q.Max));
        if (value != _band.Q) Edit(_band with { Q = value }, new EqBandChange(Q: value));
        else Raise(nameof(QValue));
    }

    /// <summary>A drag's position: frequency and gain together, in one change.</summary>
    public void DragTo(double hz, double db)
    {
        EqLimits limits = Owner.Limits;
        double frequency = Math.Clamp(MicEq.SnapFrequency(hz), limits.Frequency.Min, limits.Frequency.Max);
        double gain = Math.Clamp(MicEq.SnapGain(db), limits.Gain.Min, limits.Gain.Max);
        if (frequency == _band.Frequency && gain == _band.Gain) return;
        Edit(_band with { Frequency = frequency, Gain = gain },
            new EqBandChange(Frequency: frequency != _band.Frequency ? frequency : null, Gain: gain != _band.Gain ? gain : null));
    }

    /// <summary>A gainless band's drag: frequency sideways, Q up and down, in one change.</summary>
    public void DragToWithQ(double hz, double q)
    {
        EqLimits limits = Owner.Limits;
        double frequency = Math.Clamp(MicEq.SnapFrequency(hz), limits.Frequency.Min, limits.Frequency.Max);
        double snapped = MicEq.SnapQ(Math.Clamp(q, limits.Q.Min, limits.Q.Max));
        if (frequency == _band.Frequency && snapped == _band.Q) return;
        Edit(_band with { Frequency = frequency, Q = snapped },
            new EqBandChange(Frequency: frequency != _band.Frequency ? frequency : null, Q: snapped != _band.Q ? snapped : null));
    }

    /// <summary>Move Q by whole steps of the daemon's grid: one per wheel notch or key press.</summary>
    public void StepQ(int steps)
    {
        if (steps == 0) return;
        EqLimits.EqQRange range = Owner.Limits.Q;
        double log = MicEq.QLog10(_band.Q) + steps * range.Log10Step;
        SetQ(Math.Clamp(Math.Pow(10, log), range.Min, range.Max));
    }

    /// <summary>Move the frequency by a fraction of an octave, at least one hertz.</summary>
    public void StepFrequency(double octaves)
    {
        if (octaves == 0) return;
        double target = MicEq.SnapFrequency(_band.Frequency * Math.Pow(2, octaves));
        if (target == _band.Frequency) target += Math.Sign(octaves) * MicEq.FrequencyStep;
        SetFrequency(target);
    }

    public void StepGain(double db) => SetGain(_band.Gain + db);

    /// <summary>The daemon reported this band.</summary>
    internal void ApplyReported(EqBand band)
    {
        _reported = band;
        if (_dragging) return;
        if (_awaiting is { } awaiting && Environment.TickCount64 < _awaitingUntil && awaiting != band) return;
        _awaiting = null;
        Show(band);
    }

    /// <summary>Show values set here without sending them; the caller sends them its own way.</summary>
    internal void ShowLocal(EqBand band)
    {
        _awaiting = band;
        _awaitingUntil = Environment.TickCount64 + MicEqViewModel.EchoMilliseconds;
        Show(band);
    }

    private void Edit(EqBand band, EqBandChange change)
    {
        ShowLocal(band);
        Owner.Send(Number, change);
    }

    private void Show(EqBand band)
    {
        if (band == _band) return;
        EqBand old = _band;
        _band = band;
        if (old.Enabled != band.Enabled) Raise(nameof(Enabled));
        if (old.Type != band.Type)
        {
            Raise(nameof(Type));
            Raise(nameof(TypeChoice));
            Raise(nameof(TypeName));
            Raise(nameof(UsesGain));
        }
        if (old.Frequency != band.Frequency) { Raise(nameof(Frequency)); Raise(nameof(FrequencyValue)); Raise(nameof(FrequencyText)); }
        if (old.Gain != band.Gain) { Raise(nameof(Gain)); Raise(nameof(GainValue)); Raise(nameof(GainText)); }
        if (old.Q != band.Q) { Raise(nameof(Q)); Raise(nameof(QValue)); Raise(nameof(QText)); }
        Raise(nameof(Band));
        Raise(nameof(Description));
        Raise(nameof(Tip));
        Owner.RaiseChanged();
    }
}

/// <summary>
/// The band changes on their way to the daemon. Each band sends at most once
/// an interval and always its last value: a change inside the interval waits
/// and merges with the ones after it, so one <c>setEqBand</c> carries every
/// field that moved, and the last change of a drag is never lost.
/// </summary>
internal sealed class EqBandSender : IDisposable
{
    private readonly object _gate = new();
    private readonly long _interval;
    private readonly Action<int, EqBandChange> _send;
    private readonly EqBandChange?[] _pending = new EqBandChange?[MicEq.BandCount];
    private readonly long[] _last = new long[MicEq.BandCount];
    private readonly Timer _timer;
    private bool _disposed;

    public EqBandSender(TimeSpan interval, Action<int, EqBandChange> send)
    {
        _interval = (long)(interval.TotalSeconds * Stopwatch.Frequency);
        _send = send;
        Array.Fill(_last, long.MinValue / 2);
        _timer = new Timer(_ => Due());
    }

    public void Queue(int band, EqBandChange change)
    {
        lock (_gate)
        {
            if (_disposed) return;
            int i = band - 1;
            _pending[i] = Merge(_pending[i], change);
            long now = Stopwatch.GetTimestamp();
            // Sent under the lock, so two sends of one band keep their order
            // on their way into the client's send queue.
            if (now - _last[i] >= _interval) Take(i, now);
            else Arm(now);
        }
    }

    /// <summary>Forget what waits: a whole EQ is about to replace it.</summary>
    public void Clear()
    {
        lock (_gate) Array.Clear(_pending);
    }

    private void Due()
    {
        lock (_gate)
        {
            if (_disposed) return;
            long now = Stopwatch.GetTimestamp();
            for (int i = 0; i < _pending.Length; i++)
                if (_pending[i] is not null && now - _last[i] >= _interval) Take(i, now);
            Arm(now);
        }
    }

    private void Take(int i, long now)
    {
        EqBandChange change = _pending[i]!;
        _pending[i] = null;
        _last[i] = now;
        _send(i + 1, change);
    }

    private void Arm(long now)
    {
        long next = long.MaxValue;
        for (int i = 0; i < _pending.Length; i++)
            if (_pending[i] is not null) next = Math.Min(next, _last[i] + _interval);
        if (next == long.MaxValue) return;
        double milliseconds = Math.Max(0, next - now) * 1000.0 / Stopwatch.Frequency;
        _timer.Change(TimeSpan.FromMilliseconds(Math.Ceiling(milliseconds)), Timeout.InfiniteTimeSpan);
    }

    private static EqBandChange Merge(EqBandChange? earlier, EqBandChange later) => earlier is null ? later : new(
        later.Enabled ?? earlier.Enabled, later.Type ?? earlier.Type, later.Frequency ?? earlier.Frequency,
        later.Gain ?? earlier.Gain, later.Q ?? earlier.Q);

    public void Dispose()
    {
        lock (_gate)
        {
            // What waits still goes: the last value of a drag is never lost.
            for (int i = 0; i < _pending.Length; i++)
                if (_pending[i] is not null) Take(i, Stopwatch.GetTimestamp());
            _disposed = true;
        }
        _timer.Dispose();
    }
}

/// <summary>One EQ held between microphones: Copy on one, Paste on another, for this run of the window.</summary>
public static class EqClipboard
{
    private static MicEq? _value;

    public static MicEq? Value
    {
        get => _value;
        set { _value = value; Changed?.Invoke(); }
    }

    public static event Action? Changed;
}

/// <summary>A band type as the editor names and draws it.</summary>
public sealed record EqTypeChoice(string Type, string Icon)
{
    /// <summary>
    /// The six types in the order of the daemon's list. The icons are our own
    /// line drawings of each curve in a 24 by 24 box, drawn with a stroke.
    /// </summary>
    public static IReadOnlyList<EqTypeChoice> All { get; } =
    [
        new(MicEq.LowCut, "M2,20 C6,20 8,7 12,7 L22,7"),
        new(MicEq.LowShelf, "M2,7 L6,7 C10,7 10,15 14,15 L22,15"),
        new(MicEq.Peak, "M2,16 L6,16 C9,16 10,5 12,5 C14,5 15,16 18,16 L22,16"),
        new(MicEq.Notch, "M2,7 L8,7 C10,7 11,21 12,21 C13,21 14,7 16,7 L22,7"),
        new(MicEq.HighShelf, "M2,15 L10,15 C14,15 14,7 18,7 L22,7"),
        new(MicEq.HighCut, "M2,7 L12,7 C16,7 18,20 22,20"),
    ];

    public static EqTypeChoice Of(string type) => All.FirstOrDefault(c => c.Type == type) ?? All[2];

    /// <summary>The name in the window's language. The bell is a bell here, whatever other editors call it.</summary>
    public string Name => Type switch
    {
        MicEq.LowCut => Localizer.Text("EqTypeLowCut"),
        MicEq.LowShelf => Localizer.Text("EqTypeLowShelf"),
        MicEq.Notch => Localizer.Text("EqTypeNotch"),
        MicEq.HighShelf => Localizer.Text("EqTypeHighShelf"),
        MicEq.HighCut => Localizer.Text("EqTypeHighCut"),
        _ => Localizer.Text("EqTypePeak"),
    };

    /// <summary>What the type does, for its tooltip.</summary>
    public string Hint => Type switch
    {
        MicEq.LowCut => Localizer.Text("EqTypeLowCutTip"),
        MicEq.LowShelf => Localizer.Text("EqTypeLowShelfTip"),
        MicEq.Notch => Localizer.Text("EqTypeNotchTip"),
        MicEq.HighShelf => Localizer.Text("EqTypeHighShelfTip"),
        MicEq.HighCut => Localizer.Text("EqTypeHighCutTip"),
        _ => Localizer.Text("EqTypePeakTip"),
    };

    public override string ToString() => Name;
}

/// <summary>How the editor writes the EQ's numbers.</summary>
public static class EqText
{
    public static string Frequency(double hz) => hz >= 1000
        ? Localizer.Format("EqKilohertz", (hz / 1000).ToString(hz >= 10000 ? "0.#" : "0.##", System.Globalization.CultureInfo.CurrentCulture))
        : Localizer.Format("EqHertz", hz.ToString("0", System.Globalization.CultureInfo.CurrentCulture));

    public static string Gain(double db) =>
        Localizer.Format("EqDecibels", db.ToString("+0.##;-0.##;0", System.Globalization.CultureInfo.CurrentCulture));

    public static string Q(double q) => q.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture);
}
