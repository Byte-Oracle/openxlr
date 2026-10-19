using System.Diagnostics;
using System.Globalization;

namespace OpenXLR.Daemon;

/// <summary>A crashed window must not leave a sandbox daemon holding the audio graph.</summary>
internal sealed class FlatpakParentLifetime(IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("OPENXLR_PARENT_PID"), NumberStyles.None,
                CultureInfo.InvariantCulture, out int pid) || pid <= 1) return;
        if (await WaitForOwnerAsync(pid, stop)) lifetime.StopApplication();
    }

    internal static async Task<bool> WaitForOwnerAsync(int pid, CancellationToken stop)
    {
        try
        {
            using var parent = Process.GetProcessById(pid);
            await parent.WaitForExitAsync(stop).ConfigureAwait(false);
            return true;
        }
        catch (ArgumentException) { return true; } // the owner exited before subscription
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return false; }
    }
}
