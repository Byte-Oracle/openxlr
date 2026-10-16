using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

#if OPENXLR_UI
namespace OpenXLR.UI;
#else
namespace OpenXLR.Core;
#endif

/// <summary>Reject devices, sockets and FIFOs before an ordinary read can block.</summary>
internal static class RegularFile
{
    internal static void Require(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        const uint type = 1; // STATX_TYPE
        LinuxFileStatus status;
        try
        {
            if (Statx(-100, path, 0, type, out status) != 0)
                throw new IOException($"Could not inspect the regular file {path}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        { throw new IOException($"Could not inspect the regular file {path}: statx is unavailable.", ex); }
        if ((status.Mask & type) == 0 || (status.Mode & 0xf000) != 0x8000)
            throw new IOException($"{path} is not a regular file.");
    }

    // Linux's fixed statx UAPI layout is the same on every supported architecture.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out LinuxFileStatus status);
}
