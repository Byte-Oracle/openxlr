using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

#if OPENXLR_UI
namespace OpenXLR.UI;
#else
namespace OpenXLR.Core;
#endif

/// <summary>Resource data from one same-user process, without its id, arguments or environment.</summary>
internal sealed record LinuxProcessResourceSnapshot(string Name, string? State, int? Threads,
    long? ResidentMemoryBytes, int? OpenFiles, bool OpenFilesCapped,
    long? OpenFileSoftLimit, long? OpenFileHardLimit, long? LockedMemorySoftLimit, long? LockedMemoryHardLimit);

/// <summary>Bounded Linux process metadata and resource-limit parsing shared with memory-lock reporting.</summary>
internal static class LinuxProcessResources
{
    private const int MaximumBytes = 1024 * 1024;

    internal static string? RealUid(string directory)
    {
        string? status = Read(Path.Combine(directory, "status"));
        return status is null ? null : Fields(status).GetValueOrDefault("Uid")?
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    internal static LinuxProcessResourceSnapshot? Inspect(string directory, string expectedName,
        string uid, int? descriptorCap = null)
    {
        if (descriptorCap is <= 0 or int.MaxValue) throw new ArgumentOutOfRangeException(nameof(descriptorCap));
        if (Read(Path.Combine(directory, "comm"))?.Trim() != expectedName) return null;
        string? status = Read(Path.Combine(directory, "status"));
        if (status is null) return null;
        var fields = Fields(status);
        if (fields.GetValueOrDefault("Uid")?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() != uid) return null;
        string? limits = Read(Path.Combine(directory, "limits"));
        var files = ParseLimit(limits ?? "", "Max open files", "files");
        var memory = ParseLimit(limits ?? "", "Max locked memory", "bytes");
        int? count = null;
        bool capped = false;
        try
        {
            var entries = Directory.EnumerateFileSystemEntries(Path.Combine(directory, "fd"));
            count = descriptorCap is { } cap ? entries.Take(cap + 1).Count() : entries.Count();
            if (descriptorCap is { } maximum && count > maximum) { count = maximum; capped = true; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        // A vanished process must not leave a snapshot attributed to a new owner.
        if (Read(Path.Combine(directory, "comm"))?.Trim() != expectedName || RealUid(directory) != uid) return null;
        long? resident = null;
        string[] rss = (fields.GetValueOrDefault("VmRSS") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (rss.Length == 2 && rss[1] == "kB" && Number(rss[0]) is long kb && kb <= long.MaxValue / 1024)
            resident = kb * 1024;
        int? threads = Number(fields.GetValueOrDefault("Threads")) is long t && t <= int.MaxValue ? (int)t : null;
        return new(expectedName, fields.GetValueOrDefault("State"), threads, resident, count, capped,
            files.Soft, files.Hard, memory.Soft, memory.Hard);
    }

    internal static (long? Soft, long? Hard) ParseLimit(string text, string label, string unit)
    {
        foreach (string line in text.Split('\n'))
        {
            if (!line.StartsWith(label, StringComparison.Ordinal)) continue;
            string[] fields = line[label.Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return fields.Length == 3 && fields[2] == unit
                ? (Limit(fields[0]), Limit(fields[1])) : (null, null);
        }
        return (null, null);
    }

    private static long? Limit(string value) => value == "unlimited" ? -1 : Number(value);
    private static long? Number(string? value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long n) ? n : null;

    private static Dictionary<string, string> Fields(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) fields[line[..colon]] = line[(colon + 1)..].Trim();
        }
        return fields;
    }

    private static string? Read(string path)
    {
        try { return RegularFile.ReadText(path, MaximumBytes); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
