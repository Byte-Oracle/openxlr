using OpenXLR.Core;

namespace OpenXLR.Daemon;

/// <summary>
/// One daemon per user. The lock file lives next to the control socket in the
/// runtime directory and is held for the life of the process; the kernel
/// releases it when the process ends, however it ends. A second instance,
/// started by hand while the service runs, stops here with a clear message
/// instead of waiting a minute for a port it will never get.
/// </summary>
public static class DaemonLock
{
    public static string Path => System.IO.Path.Combine(OpenXlrPaths.RuntimeDir, "daemon.lock");

    /// <summary>The held lock, or null when another process holds it.</summary>
    public static FileStream? TryAcquire(string? path = null)
    {
        path ??= Path;
        ControlSocketListener.PrepareDirectory(System.IO.Path.GetDirectoryName(path)!);
        try
        {
            // FileShare.None is an exclusive advisory lock on Unix; the
            // stream is never written, only held.
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        // Unix EWOULDBLOCK or the runtime's mapped sharing violation means
        // contention. Directory, permission and other I/O failures must reach
        // the caller rather than claim that a healthy daemon already exists.
        catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32) { return null; }
    }
}
