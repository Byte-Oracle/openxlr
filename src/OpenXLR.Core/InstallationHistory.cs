using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

#if OPENXLR_UI
namespace OpenXLR.UI;
#else
namespace OpenXLR.Core;
#endif

/// <summary>Keep package inventories across upgrades and removal, separate from user settings.</summary>
internal static class InstallationHistory
{
    internal static string DirectoryPath => OpenXlrPaths.ConfigFile("installation-history");

    /// <summary>The prefixes a package of this build is looked for under: the running installation's own, then the system ones.</summary>
    internal static IEnumerable<string> PackagePrefixes(string baseDirectory)
    {
        var directory = new DirectoryInfo(baseDirectory);
        var package = directory.Name is "ui" or "daemon" or "yabridge" ? directory.Parent : directory;
        if (package?.Name == "openxlr" && package.Parent?.Name == "lib" && package.Parent.Parent is { } prefix)
            yield return prefix.FullName;
        yield return "/usr";
        yield return "/usr/local";
    }

    internal static IEnumerable<string> PackageReceipts(string baseDirectory)
    {
        var prefixes = new List<string>(PackagePrefixes(baseDirectory));
        foreach (string share in (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            if (Path.IsPathFullyQualified(share) && new DirectoryInfo(share).Name == "share")
                prefixes.Add(Path.GetDirectoryName(share)!);
        if (Environment.GetEnvironmentVariable("OPENXLR_YABRIDGE") is { Length: > 0 } bridge && Path.IsPathFullyQualified(bridge))
        {
            var parent = new DirectoryInfo(bridge);
            if (parent.Name == "yabridge" && parent.Parent?.Name == "openxlr" && parent.Parent.Parent?.Name == "lib" && parent.Parent.Parent.Parent is { } bridgePrefix)
                prefixes.Add(bridgePrefix.FullName);
        }
        foreach (string installation in prefixes.Distinct(StringComparer.Ordinal))
            foreach (string name in new[] { "openxlr", "openxlr-yabridge" })
            {
                string path = Path.Combine(installation, "share", "openxlr", "installations", name + ".json");
                // Discovery is best effort. Yield unreadable candidates so the
                // caller can report them and continue with other installations.
                string resolved = path;
                try
                {
                    // Exists hides access errors. Only a proven missing path
                    // may be skipped, otherwise doctor needs this candidate.
                    try { _ = File.GetAttributes(path); }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    { continue; }
                    // A Nix profile can link the receipt or any parent directory.
                    resolved = ResolveReceiptPath(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { /* the ordinary reader reports this candidate */ }
                yield return resolved;
            }
    }

    internal static IEnumerable<string> ArchivedReceipts()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        if (new DirectoryInfo(DirectoryPath).LinkTarget is not null) throw new IOException("The installation history directory is a symbolic link.");
        // Read a bounded inventory, never infer ownership from a filename.
        string[] files = Directory.EnumerateFiles(DirectoryPath, "*.json").Take(129).ToArray();
        if (files.Length > 128) throw new IOException("Installation history exceeds 128 receipts; archive older records before continuing.");
        return files.Order(StringComparer.Ordinal);
    }

    private static readonly object Gate = new();

    internal static void Remember(string path)
    {
        InstallationReceipt receipt = InstallationReceipt.Read(path);
        if (receipt.Manager is "user" or "nix") return;
        lock (Gate)
        {
            string boundary = Path.GetFullPath(OpenXlrPaths.ConfigHome);
            string lockPath = OpenXlrPaths.ConfigFile("installation.lock");
            if (InstallationReceipt.HasLinkedParent(lockPath, boundary) || new FileInfo(lockPath).LinkTarget is not null ||
                InstallationReceipt.HasLinkedParent(Path.Combine(DirectoryPath, "record"), boundary))
                throw new IOException("The installation history or lock directory is a symbolic link.");
            OpenXlrPaths.EnsurePrivateDir(OpenXlrPaths.ConfigDir);
            if (File.Exists(lockPath)) RegularFile.Require(lockPath);
            var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var lease = new FileStream(lockPath, options);
            string[] archives = [.. ArchivedReceipts()];
            long totalBytes = 0;
            foreach (string archive in archives)
                if ((totalBytes += new FileInfo(archive).Length) > 16 * 1024 * 1024)
                    throw new IOException("Installation history exceeds the 16 MiB inspection limit; archive older versions before continuing.");
            // A damaged or misnamed archive stays on disk for doctor to report.
            // It must not stop newer inventories from being recorded.
            var previous = new List<(string Path, InstallationReceipt Receipt)>();
            foreach (string archive in archives)
            {
                try { previous.Add((archive, InstallationReceipt.Read(archive, archived: true))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* doctor reports this archive */ }
            }
            var sameVersion = previous.Where(p => p.Receipt.Package == receipt.Package &&
                p.Receipt.Prefix == receipt.Prefix && p.Receipt.Version == receipt.Version).ToArray();
            // Retain retired paths even when several rebuilds share a version.
            // The newest inventory wins for paths still shipped by that version.
            var files = new Dictionary<string, InstalledFile>(StringComparer.Ordinal);
            foreach (var old in sameVersion)
                foreach (InstalledFile file in old.Receipt.Files) files[file.Path] = file;
            foreach (InstalledFile file in receipt.Files) files[file.Path] = file;
            receipt = receipt with { Files = [.. files.Values.OrderBy(f => f.Path, StringComparer.Ordinal)] };
            receipt.Validate();
            byte[] bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(receipt, InstallationReceipt.Json);
            if (bytes.Length > InstallationReceipt.MaxBytes) throw new IOException("Archived receipt exceeds 4 MiB.");
            long retainedBytes = totalBytes - sameVersion.Sum(p => new FileInfo(p.Path).Length);
            if (retainedBytes + bytes.Length > 16 * 1024 * 1024)
                throw new IOException("Installation history would exceed 16 MiB; archive older versions before continuing.");
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            string destination = Path.Combine(DirectoryPath, hash + ".json");
            if (sameVersion.Any(p => p.Path == destination)) return;
            if (archives.Length - sameVersion.Length >= 128) throw new IOException("Installation history is full; archive older versions before continuing.");
            OpenXlrPaths.WriteAtomic(destination, bytes);
            // Only validated, content-addressed records for this same version
            // are replaced, after the new inventory has been published.
            foreach (var old in sameVersion) File.Delete(old.Path);
        }
    }

    private static string ResolveReceiptPath(string path)
    {
        for (int hop = 0; hop < 40; hop++)
        {
            string? ancestor = path;
            while (ancestor is not null)
            {
                var info = new FileInfo(ancestor);
                if (info.LinkTarget is not null)
                {
                    string target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                        ?? throw new IOException("The installation receipt link cannot be resolved.");
                    path = Path.GetFullPath(Path.GetRelativePath(ancestor, path), target);
                    break;
                }
                ancestor = Path.GetDirectoryName(ancestor);
            }
            if (ancestor is null) return path;
        }
        throw new IOException("The installation receipt path has too many symbolic links.");
    }
}
