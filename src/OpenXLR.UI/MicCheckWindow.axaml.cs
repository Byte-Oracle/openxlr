using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace OpenXLR.UI;

/// <summary>
/// Mic check for one microphone. While it is open it keeps the daemon's
/// meter before the chain running. Closing it stops the session; a close the
/// user asked for waits for that stop, with the window disabled, so nothing
/// can be queued behind it.
/// </summary>
public partial class MicCheckWindow : Window
{
    /// <summary>How often the meter's lease is renewed: well inside the daemon's fifteen seconds.</summary>
    internal static readonly TimeSpan MeterRenewal = TimeSpan.FromSeconds(5);

    private readonly DispatcherTimer _meter = new() { Interval = MeterRenewal };
    // Suggest gain's countdown, and its end when the meter sends nothing.
    private readonly DispatcherTimer _measure = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _closing, _closed;

    public MicCheckWindow()
    {
        InitializeComponent();
        _meter.Tick += (_, _) => KeepMeter();
        _measure.Tick += (_, _) =>
        {
            Check?.MeasureTick();
            if (Check is not { Measuring: true }) _measure.Stop();
        };
        Opened += (_, _) => { KeepMeter(); _meter.Start(); };
        Closed += (_, _) =>
        {
            _meter.Stop();
            _measure.Stop();
            Check?.CancelGainMeasure();
        };
        Closing += async (_, e) =>
        {
            if (_closed || DataContext is not MicCheckViewModel vm) return;
            if (e.CloseReason != WindowCloseReason.WindowClosing)
            {
                _ = vm.StopOnCloseAsync();
                return;
            }
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            _meter.Stop();
            IsEnabled = false; // Do not queue a new recording behind the pending stop.
            await vm.StopOnCloseAsync();
            _closed = true;
            Close();
        };
    }

    private MicCheckViewModel? Check => DataContext as MicCheckViewModel;

    private void KeepMeter() { if (Check is { } vm) _ = vm.KeepMeterAsync(); }

    private void OnStep(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MicCheckStepItem item) item.IsSelected = true;
    }

    private void OnClearClip(object? sender, RoutedEventArgs e) => Check?.ClearClip();

    private void OnSuggestGain(object? sender, RoutedEventArgs e)
    {
        if (Check is not { } vm) return;
        vm.StartGainMeasure();
        _measure.Start();
    }

    private void OnAcceptGain(object? sender, RoutedEventArgs e) => Check?.AcceptGain();
    private void OnDiscardGain(object? sender, RoutedEventArgs e) => Check?.DiscardGain();
    private async void OnRecord(object? sender, RoutedEventArgs e) { if (Check is { } vm) await vm.RunAsync("record"); }
    private async void OnLoop(object? sender, RoutedEventArgs e) { if (Check is { } vm) await vm.RunAsync("loop"); }
    private async void OnLive(object? sender, RoutedEventArgs e) { if (Check is { } vm) await vm.RunAsync("live"); }
    private async void OnStop(object? sender, RoutedEventArgs e) { if (Check is { } vm) await vm.RunAsync("stop"); }
    private void OnProcessed(object? sender, RoutedEventArgs e) { if (Check is { } vm) vm.ListenProcessed = true; }
    private void OnRaw(object? sender, RoutedEventArgs e) { if (Check is { } vm) vm.ListenRaw = true; }
    private void OnHearMyself(object? sender, RoutedEventArgs e) { if (Check is { } vm) vm.HearMyself = !vm.HearMyself; }
    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // --- Shape ---

    private void OnPreset(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MicCheckPreset preset) Check?.ApplyPreset(preset);
    }

    private void OnUndoPreset(object? sender, RoutedEventArgs e) => Check?.UndoPreset();

    /// <summary>The full editor, with a row of fields per band, on the same EQ.</summary>
    private void OnEqEditor(object? sender, RoutedEventArgs e)
    {
        if (Check?.Eq is { } eq) EqWindows.Show(this, eq);
    }

    private void OnCycleSoftLowCut(object? sender, RoutedEventArgs e) => Check?.Software?.CycleSoftLowCut();

    private async void OnAddEffect(object? sender, RoutedEventArgs e)
    {
        if (Check?.Chain is not { CanEditEffects: true } chain) return;
        PluginChoice? choice = await new PluginPickerWindow { DataContext = chain }.ShowDialog<PluginChoice?>(this);
        if (choice is not null) chain.Add(choice);
    }

    /// <summary>
    /// As the chain's cog does: a plugin running in its own process opens its
    /// own editor, anything else the generated controls.
    /// </summary>
    private async void OnInsertEditor(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not InsertViewModel insert) return;
        if (insert.NativeEditorAvailable) await insert.Owner.ShowNativeEditorAsync(insert);
        else InsertWindows.OpenControls(this, insert);
    }
}
