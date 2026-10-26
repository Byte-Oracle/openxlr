using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

#if OPENXLR_UI
namespace OpenXLR.UI;
#elif OPENXLR_TUI
namespace OpenXLR.Tui;
#else
namespace OpenXLR.Core;
#endif

/// <summary>What a client finds when it looks for the daemon.</summary>
#if OPENXLR_UI || OPENXLR_TUI
internal
#else
public
#endif
enum DaemonPresence
{
    /// <summary>The control socket accepted this user's connection.</summary>
    Reachable,
    /// <summary>No daemon holds the lock and nothing answers on the old port.</summary>
    NotRunning,
    /// <summary>
    /// A daemon is alive but does not serve the control socket: in practice
    /// one from before the update, which listens on the loopback port.
    /// </summary>
    OlderDaemon,
    /// <summary>The control socket answered for another user and was refused.</summary>
    OtherUser,
}

/// <summary>
/// Tells the cases apart after a client could not connect, so each client can
/// say what to do. Packages do not restart a running daemon, so after an
/// upgrade the old daemon still runs, listening only on the loopback port.
///
/// Two signals say a daemon is alive. The single-instance lock comes first:
/// every release since 2026-09 takes it at the same path, which the Flatpak
/// shares with the host (xdg-run/openxlr), so it works for native, Flatpak and
/// source installs alike. It is a flock, and Linux has no way to test one
/// without taking it (F_GETLK does not see flock locks, and /proc/locks hides
/// holders in another PID namespace, such as a Flatpak instance), so the probe
/// takes a shared lock and drops it at once; DaemonLock.TryAcquire retries
/// contention briefly so a daemon starting in that instant is not turned away.
/// The loopback port of the older releases is the second signal. Only a bare
/// TCP connect is made there; nothing is sent.
/// </summary>
#if OPENXLR_UI || OPENXLR_TUI
internal
#else
public
#endif
static class DaemonProbe
{
    /// <summary>The loopback port the releases before the control socket listened on.</summary>
    public const int LegacyPort = 37890;

    /// <summary>How long a daemon that has just taken the lock gets to bind its socket.</summary>
    internal static readonly TimeSpan StartupGrace = TimeSpan.FromMilliseconds(250);

    public static async Task<DaemonPresence> ProbeAsync(string? socketPath = null, string? lockPath = null,
        int legacyPort = LegacyPort, CancellationToken cancel = default)
    {
        socketPath ??= OpenXlrPaths.ControlSocketPath;
        lockPath ??= OpenXlrPaths.DaemonLockPath;
        if (await SocketAsync(socketPath, cancel).ConfigureAwait(false) is { } answered) return answered;
        if (LockHeld(lockPath))
        {
            // A daemon of this release binds its socket right after taking
            // the lock. Give one that is starting now the moment it needs.
            await Task.Delay(StartupGrace, cancel).ConfigureAwait(false);
            if (await SocketAsync(socketPath, cancel).ConfigureAwait(false) is { } late) return late;
            return LockHeld(lockPath) ? DaemonPresence.OlderDaemon : DaemonPresence.NotRunning;
        }
        return await LegacyPortAnswersAsync(legacyPort, cancel).ConfigureAwait(false)
            ? DaemonPresence.OlderDaemon : DaemonPresence.NotRunning;
    }

    /// <summary>The command that restarts the daemon in this installation.</summary>
    public static string RestartCommand => Environment.GetEnvironmentVariable("FLATPAK_ID") == "io.github.emaspa.openxlr"
        ? "systemctl --user restart openxlr-flatpak" : "systemctl --user restart openxlr-daemon";

    /// <summary>One plain English sentence for clients without the UI's catalogue.</summary>
    public static string Describe(DaemonPresence presence) => presence switch
    {
        DaemonPresence.OlderDaemon => "An OpenXLR daemon from before the update is still running and cannot "
            + "accept this client. Restart it: " + RestartCommand,
        DaemonPresence.OtherUser => "The OpenXLR control socket belongs to another user and was refused.",
        DaemonPresence.NotRunning => "The OpenXLR daemon is not running.",
        _ => "The OpenXLR daemon did not accept the connection.",
    };

    /// <summary>The short name clients see in an openxlr-control error line.</summary>
    public static string Reason(DaemonPresence presence) => presence switch
    {
        DaemonPresence.OlderDaemon => "olderDaemon",
        DaemonPresence.OtherUser => "otherUser",
        DaemonPresence.NotRunning => "notRunning",
        _ => "unavailable",
    };

    /// <summary>Null when the socket is absent, refuses, or does not answer in time.</summary>
    private static async Task<DaemonPresence?> SocketAsync(string path, CancellationToken cancel)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), deadline.Token).ConfigureAwait(false);
            UnixPeerCredentials.RequireSameUser(socket);
            return DaemonPresence.Reachable;
        }
        catch (UnauthorizedAccessException) { return DaemonPresence.OtherUser; }
        // A socket of another user's daemon is 0600 in its own 0700 directory.
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied) { return DaemonPresence.OtherUser; }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ArgumentException
            && !cancel.IsCancellationRequested) { return null; }
    }

    /// <summary>
    /// Whether another process holds the daemon lock. The shared lock taken
    /// here conflicts only with the daemon's exclusive one and is released
    /// before this returns. .NET opens a read-only FileStream with
    /// flock(LOCK_SH | LOCK_NB), the same call family as the daemon's
    /// FileShare.None, and reports EWOULDBLOCK as an IOException.
    /// </summary>
    internal static bool LockHeld(string path)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return false;
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static async Task<bool> LegacyPortAnswersAsync(int port, CancellationToken cancel)
    {
        if (port is <= 0 or > 65535) return false;
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            return false;
        }
    }
}
