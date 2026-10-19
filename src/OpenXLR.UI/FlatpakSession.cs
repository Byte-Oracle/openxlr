using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>A sandbox owns its daemon without talking to the host's systemd.</summary>
internal sealed class FlatpakSession : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _generation;
    private readonly Task _run;
    internal static FlatpakSession? Current { get; private set; }

    internal FlatpakSession(string binary)
    {
        _run = RunAsync(binary);
    }

    internal static void Start()
    {
        if (FlatpakEnvironment.IsFlatpak)
            Current = new FlatpakSession(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "daemon", "OpenXLR.Daemon")));
    }

    internal bool Restart()
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested || _run.IsCompleted) return false;
            _generation?.Cancel();
            return true;
        }
    }

    private async Task RunAsync(string binary)
    {
        int failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            using var generation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            lock (_gate) _generation = generation;
            long began = Environment.TickCount64;
            try
            {
                int exit = await ProcessRunner.RunOwnedAsync(binary, [], generation.Token, TimeSpan.FromSeconds(45),
                    ["NOTIFY_SOCKET", "WATCHDOG_USEC", "WATCHDOG_PID"],
                    new System.Collections.Generic.Dictionary<string, string>
                    { ["OPENXLR_PARENT_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ConfigureAwait(false);
                // A host installation owns the API port or lock: do not keep
                // claiming hardware in a retry loop against another daemon.
                if (exit == 73 && !generation.IsCancellationRequested) return;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Console.Error.WriteLine("Could not start the sandbox daemon: " + ex.Message);
            }
            finally { lock (_gate) _generation = null; }
            if (_stop.IsCancellationRequested) break;
            failures = Environment.TickCount64 - began > 60_000 ? 0 : Math.Min(failures + 1, 5);
            try { await Task.Delay(TimeSpan.FromSeconds(failures * 2), _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _run.GetAwaiter().GetResult();
        _stop.Dispose();
        if (Current == this) Current = null;
    }
}
