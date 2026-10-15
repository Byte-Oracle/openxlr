using System.Text.Json;
using OpenXLR.UI;
using Receipt = OpenXLR.UI.InstallationReceipt;
using Owned = OpenXLR.UI.InstalledFile;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class StartupReceiptTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "openxlr-receipt-" + Guid.NewGuid());
    private readonly string? _old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private string Entry => Path.Combine(_home, "autostart/openxlr.desktop");
    private string Manifest => Path.Combine(_home, "share/openxlr/installations/openxlr.json");
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    public StartupReceiptTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _old);
        Directory.Delete(_home, true);
    }

    private Receipt Package(params Owned[] files) => new()
    { Package = "openxlr", Manager = "debian", Version = "1.2.3", Commit = Commit, Prefix = _home, Files = [.. files] };

    private string WriteReceipt(Receipt receipt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Manifest)!);
        receipt = receipt with { Files = [.. receipt.Files, new() { Path = Manifest, Kind = "receipt" }] };
        File.WriteAllText(Manifest, JsonSerializer.Serialize(receipt, Receipt.Json));
        return Manifest;
    }

    [Fact]
    public void SpecialPermissionChangesAreReportedAndPreventRemoval()
    {
        if (OperatingSystem.IsWindows()) return;
        StartupReceipt.Write(Entry, "installed");
        Owned entry = StartupReceipt.Load().Files.Single(f => f.Path == Entry);
        File.SetUnixFileMode(Entry, (UnixFileMode)entry.Mode! | UnixFileMode.SetUser);
        Assert.Equal("permissions were modified", Receipt.Problem(entry, _home));
        Assert.Throws<IOException>(() => StartupReceipt.Remove(Entry));
        Assert.Equal("installed", File.ReadAllText(Entry));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUpgradeRefusesADamagedOriginalBackupBeforeChangingTheReceiptOrStartupFile(bool missing)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "original");
        StartupReceipt.Write(Entry, "installed");
        Owned backup = StartupReceipt.Load().Files.Single(f => f.Path.Contains("/installation-backups/"));
        if (missing) File.Delete(backup.Path);
        else File.WriteAllText(backup.Path, "damaged");
        byte[] before = File.ReadAllBytes(StartupReceipt.Path);
        Assert.Throws<IOException>(() => StartupReceipt.Write(Entry, "upgrade"));
        Assert.Equal(before, File.ReadAllBytes(StartupReceipt.Path));
        Assert.Equal("installed", File.ReadAllText(Entry));
    }

    [Fact]
    public void ReplacementRecordsTheOriginalAndRemovalRestoresItsBytesAndMode()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "user original");
        int? originalMode = null;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Entry, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            originalMode = (int)File.GetUnixFileMode(Entry);
        }
        string originalHash = Receipt.Digest(Entry);
        StartupReceipt.Write(Entry, "installed");
        Owned entry = StartupReceipt.Load().Files.Single(f => f.Path == Entry);
        Assert.Equal(originalHash, entry.ReplacedSha256);
        Assert.Equal(originalMode, entry.ReplacedMode);
        Assert.Contains(StartupReceipt.Load().Files, f => f.Sha256 == originalHash);
        StartupReceipt.Write(Entry, "upgrade");
        Assert.Equal(originalHash, StartupReceipt.Load().Files.Single(f => f.Path == Entry).ReplacedSha256);
        StartupReceipt.Remove(Entry);
        Assert.Equal("user original", File.ReadAllText(Entry));
        if (!OperatingSystem.IsWindows()) Assert.Equal(originalMode, (int)File.GetUnixFileMode(Entry));
        Assert.True(StartupReceipt.Load().Files.Single(f => f.Path == Entry).Removed);
        // Repeating removal recognizes the restored original without deleting it.
        StartupReceipt.Remove(Entry);
        Assert.Equal("user original", File.ReadAllText(Entry));
    }

    [Fact]
    public void RemovalRefusesUnownedModifiedAndRedirectedPaths()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "foreign");
        Assert.Throws<IOException>(() => StartupReceipt.Remove(Entry));
        Assert.Equal("foreign", File.ReadAllText(Entry));
        StartupReceipt.Write(Entry, "installed");
        File.WriteAllText(Entry, "modified");
        Assert.Throws<IOException>(() => StartupReceipt.Remove(Entry));
        File.Delete(Entry);
        File.CreateSymbolicLink(Entry, Path.Combine(_home, "unrelated"));
        Assert.Throws<IOException>(() => StartupReceipt.Remove(Entry));
        Assert.NotNull(new FileInfo(Entry).LinkTarget);
        Assert.Throws<IOException>(() => StartupReceipt.Write(Path.Combine(_home, "other"), "not allowed"));
    }

    [Fact]
    public void CorruptReceiptOrOriginalBackupPreventsDestructiveChanges()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "original");
        StartupReceipt.Write(Entry, "installed");
        Owned backup = StartupReceipt.Load().Files.Single(f => f.Path.Contains("/installation-backups/"));
        File.WriteAllText(backup.Path, "corrupt");
        Assert.Throws<IOException>(() => StartupReceipt.Remove(Entry));
        Assert.Equal("installed", File.ReadAllText(Entry));
        File.WriteAllText(StartupReceipt.Path, "{ broken");
        Assert.Throws<IOException>(() => StartupReceipt.Write(Entry, "next version"));
        Assert.Throws<IOException>(() => StartupReceipt.Remove(Entry));
        Assert.Equal("installed", File.ReadAllText(Entry));
    }

    [Fact]
    public void StartupDirectoriesAndForeignLinksAreNeverDeletedOrTraversed()
    {
        Directory.CreateDirectory(Entry);
        Assert.Throws<IOException>(() => StartupReceipt.Remove(Entry));
        Directory.Delete(Entry);
        Directory.Delete(Path.GetDirectoryName(Entry)!);
        string elsewhere = Path.Combine(_home, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateSymbolicLink(Path.GetDirectoryName(Entry)!, elsewhere);
        Assert.Throws<IOException>(() => StartupReceipt.Write(Entry, "installed"));
        Assert.Empty(Directory.GetFiles(elsewhere));
    }

    [Fact]
    public void AnEnabledUnitLinkIsRecordedAndRemovedExactly()
    {
        string link = StartupReceipt.StartupPaths.Single(p => p.Contains("default.target.wants"));
        StartupReceipt.EnableLink(link, "/usr/lib/systemd/user/openxlr-daemon.service");
        StartupReceipt.EnableLink(link, "/usr/lib/systemd/user/openxlr-daemon.service");
        Assert.Equal("link", StartupReceipt.Load().Files.Single(f => f.Path == link).Kind);
        StartupReceipt.Remove(link);
        Assert.Null(new FileInfo(link).LinkTarget);
        File.CreateSymbolicLink(link, "/foreign/unit");
        Assert.Throws<IOException>(() => StartupReceipt.EnableLink(link, "/usr/lib/systemd/user/openxlr-daemon.service"));
        Assert.Equal("/foreign/unit", new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void EnablingAdoptsARelativeLinkToTheSameUnitWithoutChangingItsSpelling()
    {
        string link = StartupReceipt.StartupPaths[2];
        string unit = StartupReceipt.StartupPaths[1];
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.WriteAllText(unit, "unit");
        File.CreateSymbolicLink(link, "../openxlr-daemon.service");
        StartupReceipt.EnableLink(link, unit);
        Assert.Equal("../openxlr-daemon.service", StartupReceipt.Load().Files.Single(f => f.Path == link).LinkTarget);
        StartupReceipt.Remove(link);
        Assert.True(File.Exists(unit));
    }

    [Fact]
    public async Task CommandSupportsHeadlessJsonAndRefusesUnownedRemovalBeforeDeletingAnything()
    {
        var output = new StringWriter();
        Assert.Equal(0, await InstallationCommands.Run(["doctor", "--receipt", WriteReceipt(Package()), "--no-runtime", "--json"], output));
        using JsonDocument json = JsonDocument.Parse(output.ToString());
        Assert.Empty(json.RootElement.GetProperty("findings").EnumerateArray());
        Assert.Equal(2, await InstallationCommands.Run(["doctor", "--unknown"], new StringWriter()));
        Assert.Equal(2, await InstallationCommands.Run(["doctor", "--root", "/"], new StringWriter()));
        StartupReceipt.Write(Entry, "owned");
        string unit = StartupReceipt.StartupPaths.Single(p => p.EndsWith("/user/openxlr-daemon.service", StringComparison.Ordinal));
        Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
        File.WriteAllText(unit, "foreign");
        Assert.Equal(2, await InstallationCommands.Run(["installation", "remove-startup"], new StringWriter()));
        Assert.Equal("owned", File.ReadAllText(Entry));
        Assert.Equal("foreign", File.ReadAllText(unit));
    }

    [Fact]
    public void DisablingAnAlreadyMissingOwnedFileRecordsItsRemoval()
    {
        StartupReceipt.Write(Entry, "installed");
        File.Delete(Entry);
        StartupReceipt.Remove(Entry);
        Assert.True(StartupReceipt.Load().Files.Single(f => f.Path == Entry).Removed);
        Assert.Null(Receipt.Problem(StartupReceipt.Load().Files.Single(f => f.Path == Entry), _home));
    }

    [Fact]
    public void AConcurrentReceiptWriterIsRefusedBeforeTouchingStartupFiles()
    {
        string directory = Path.Combine(_home, "openxlr");
        Directory.CreateDirectory(directory);
        using (var lease = new FileStream(Path.Combine(directory, "installation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => StartupReceipt.Write(Entry, "installed"));
            Assert.False(File.Exists(Entry));
        }
        StartupReceipt.Write(Entry, "installed");
        Assert.Equal("installed", File.ReadAllText(Entry));
    }

    [Fact]
    public void PackageDiscoveryResolvesProfileReceiptLinksWithoutChangingStartupOwnership()
    {
        string manifest = WriteReceipt(Package());
        string share = Path.Combine(_home, "profile/share");
        string alias = Path.Combine(share, "openxlr/installations/openxlr.json");
        Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
        File.CreateSymbolicLink(alias, manifest);
        string? previous = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", share);
            Assert.Contains(manifest, InstallationHistory.PackageReceipts(Path.Combine(_home, "source-build")));
            Assert.DoesNotContain(alias, InstallationHistory.PackageReceipts(Path.Combine(_home, "source-build")));
        }
        finally { Environment.SetEnvironmentVariable("XDG_DATA_DIRS", previous); }
        Directory.CreateDirectory(Path.GetDirectoryName(StartupReceipt.Path)!);
        File.CreateSymbolicLink(StartupReceipt.Path, manifest);
        Assert.Throws<IOException>(() => StartupReceipt.Write(Entry, "installed"));
    }

    [Fact]
    public void ANewInstallationDoesNotRestoreAnOriginalRemovedAfterThePreviousUninstall()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "original");
        StartupReceipt.Write(Entry, "installed");
        StartupReceipt.Remove(Entry);
        File.Delete(Entry);
        StartupReceipt.Write(Entry, "new install");
        StartupReceipt.Remove(Entry);
        Assert.False(File.Exists(Entry));
    }

    [Fact]
    public void AFailedReceiptWriteCannotReplaceTheOriginal()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "original");
        Directory.CreateDirectory(StartupReceipt.Path);
        Assert.Throws<IOException>(() => StartupReceipt.Write(Entry, "installed"));
        Assert.Equal("original", File.ReadAllText(Entry));
        // A private, content-addressed backup may remain, but the original was never replaced.
    }

    [Fact]
    public void RestoringAnOriginalUnitCannotSilentlyShadowThePackagedDaemon()
    {
        string unit = StartupReceipt.StartupPaths[1];
        Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
        File.WriteAllText(unit, "original custom unit");
        StartupReceipt.Write(unit, "generated source unit");
        StartupReceipt.EnableLink(StartupReceipt.StartupPaths[2], unit);
        string packaged = Path.Combine(_home, "packaged.service");
        File.WriteAllText(packaged, "packaged unit");
        string bin = Path.Combine(_home, "bin");
        Directory.CreateDirectory(bin);
        ExecutableScript.Write(Path.Combine(bin, "systemctl"), "#!/bin/sh\nexit 0\n");
        string? previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + previous);
            Assert.False(StartupIntegration.SetDaemonAtLogin(true, packaged));
            Assert.Equal("original custom unit", File.ReadAllText(unit));
            Assert.Null(new FileInfo(StartupReceipt.StartupPaths[2]).LinkTarget);
        }
        finally { Environment.SetEnvironmentVariable("PATH", previous); }
    }

    [Fact]
    public void ARefusedUnitRepairNeverStartsTheUnownedUnit()
    {
        string foreign = Path.Combine(_home, "foreign-units/user");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "openxlr-daemon.service"), "[Service]\nExecStart=/missing/openxlr\n");
        Directory.CreateSymbolicLink(Path.Combine(_home, "systemd"), Path.Combine(_home, "foreign-units"));
        string bin = Path.Combine(_home, "bin");
        Directory.CreateDirectory(bin);
        string log = Path.Combine(_home, "systemctl.log");
        ExecutableScript.Write(Path.Combine(bin, "systemctl"), "#!/bin/sh\nprintf '%s\\n' \"$*\" >> " + InstallationDoctor.Quote(log) + "\nexit 0\n");
        string? previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + previous);
            Assert.True(StartupIntegration.HasStaleUserUnit());
            StartupIntegration.RepairDaemonUnit();
            Assert.DoesNotContain(File.Exists(log) ? File.ReadAllLines(log) : Array.Empty<string>(),
                line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("start", StringComparer.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable("PATH", previous); }
    }

    [Fact]
    public async Task AForeignStartupEntryIsNamedAndRestoringItDoesNotPretendStartupIsDisabled()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, "foreign startup entry");
        var output = new StringWriter();
        Assert.Equal(1, await InstallationCommands.Run(["doctor", "--no-runtime", "--json"], output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Contains(json.RootElement.GetProperty("findings").EnumerateArray(), f => f.GetProperty("path").GetString() == Entry);
        string executable = Path.Combine(_home, "OpenXLR.UI");
        File.WriteAllText(executable, "fixture");
        Assert.True(StartupIntegration.SetWindowAtLogin(true, executable));
        Assert.False(StartupIntegration.SetWindowAtLogin(false, executable));
        Assert.Equal("foreign startup entry", File.ReadAllText(Entry));
    }

    [Fact]
    public void LegacyGeneratedAutostartCanBeDisabledWithoutEnablingItFirst()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        string binary = Path.Combine(_home, "old source/My Projects/OpenXLR.UI");
        File.WriteAllText(Entry, StartupIntegration.AutostartEntry(binary));
        Assert.True(StartupIntegration.SetWindowAtLogin(false, binary));
        Assert.False(File.Exists(Entry));
        Assert.True(StartupReceipt.Load().Files.Single(f => f.Path == Entry).Removed);
    }

    [Fact]
    public async Task LegacySourceUnitAndItsWantsLinkAreRemovedTogether()
    {
        string unit = StartupReceipt.StartupPaths[1];
        Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
        File.WriteAllText(unit, StartupIntegration.SourceUnit("/old missing/OpenXLR.Daemon"));
        Directory.CreateDirectory(Path.GetDirectoryName(StartupReceipt.StartupPaths[2])!);
        File.CreateSymbolicLink(StartupReceipt.StartupPaths[2], "../openxlr-daemon.service");
        Assert.Equal(0, await InstallationCommands.Run(["installation", "remove-startup"], new StringWriter()));
        Assert.False(File.Exists(unit));
        Assert.Null(new FileInfo(StartupReceipt.StartupPaths[2]).LinkTarget);
    }

    [Fact]
    public void LegacyPackagedEnableLinkCanBeDisabledButForeignLinksRemain()
    {
        string packaged = Path.Combine(_home, "package/openxlr-daemon.service");
        Directory.CreateDirectory(Path.GetDirectoryName(packaged)!);
        File.WriteAllText(packaged, "package unit");
        string wants = StartupReceipt.StartupPaths[2];
        Directory.CreateDirectory(Path.GetDirectoryName(wants)!);
        File.CreateSymbolicLink(wants, packaged);
        StartupReceipt.PrepareRemoval(packaged);
        StartupReceipt.Remove(wants, packaged);
        Assert.Null(new FileInfo(wants).LinkTarget);
        Assert.Equal("package unit", File.ReadAllText(packaged));
        File.CreateSymbolicLink(wants, "/foreign.service");
        Assert.Throws<IOException>(() => StartupReceipt.Remove(wants, packaged));
        Assert.Equal("/foreign.service", new FileInfo(wants).LinkTarget);
    }

    [Fact]
    public void AStaleGeneratedUnitCanBeReplacedByThePackagedUnit()
    {
        string unit = StartupReceipt.StartupPaths[1];
        Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
        File.WriteAllText(unit, StartupIntegration.SourceUnit("/does-not-exist/OpenXLR.Daemon"));
        string packaged = Path.Combine(_home, "packaged.service");
        File.WriteAllText(packaged, "package unit");
        string bin = Path.Combine(_home, "bin");
        Directory.CreateDirectory(bin);
        ExecutableScript.Write(Path.Combine(bin, "systemctl"), "#!/bin/sh\nexit 0\n");
        string? previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + previous);
            Assert.True(StartupIntegration.SetDaemonAtLogin(true, packaged));
            Assert.False(File.Exists(unit));
            Assert.Equal(packaged, new FileInfo(StartupReceipt.StartupPaths[2]).LinkTarget);
        }
        finally { Environment.SetEnvironmentVariable("PATH", previous); }
    }

    [Fact]
    public void MigrationMatchesTheManualUnitExactlyAndRejectsAdditionalDirectives()
    {
        using var stream = typeof(StartupIntegration).Assembly.GetManifestResourceStream("OpenXLR.StartupUnit")!;
        using var reader = new StreamReader(stream);
        string reference = reader.ReadToEnd();
        string unit = StartupReceipt.StartupPaths[1];
        Assert.True(StartupIntegration.IsGeneratedStartupFile(unit, reference));
        Assert.False(StartupIntegration.IsGeneratedStartupFile(unit, reference + "Environment=USER_CUSTOM=1\n"));
        Assert.False(StartupIntegration.IsGeneratedStartupFile(Entry, StartupIntegration.AutostartEntry("/old/OpenXLR.UI") + "Exec=/foreign\n"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"formatVersion\":99}")]
    public async Task InvalidStartupReceiptsHaveUserRecoveryRatherThanPackageRepair(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StartupReceipt.Path)!);
        File.WriteAllText(StartupReceipt.Path, contents);
        Assert.False(StartupIntegration.SetWindowAtLogin(false, "/missing"));
        DoctorReport report = await InstallationDoctor.Inspect([StartupReceipt.Path], null, false);
        Assert.Contains(report.Findings, f => f.Repair == "openxlr installation reset-startup-receipt");
        var output = new StringWriter();
        Assert.Equal(0, await InstallationCommands.Run(["installation", "reset-startup-receipt"], output));
        string saved = Directory.GetFiles(Path.GetDirectoryName(StartupReceipt.Path)!, "installation.json.before-recovery-*").Single();
        Assert.Equal(contents, File.ReadAllText(saved));
        Assert.False(File.Exists(StartupReceipt.Path));
        Assert.True(StartupIntegration.SetWindowAtLogin(false, "/missing"));
    }

    [Fact]
    public async Task RepairWithNoEnabledPreferencesDoesNotClaimToReapplyAnything()
    {
        var output = new StringWriter();
        Assert.Equal(0, await InstallationCommands.Run(["installation", "repair-startup"], output));
        Assert.Contains("No startup preferences are enabled", output.ToString());
        Assert.False(File.Exists(StartupReceipt.Path));
    }
    [Fact]
    public void MigrationDoesNotNormalizeInvalidUtf8IntoAGeneratedFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(StartupIntegration.AutostartEntry("/old/\ufffd/OpenXLR.UI"));
        int index = Array.IndexOf(bytes, (byte)0xef);
        bytes[index] = 0xff;
        File.WriteAllBytes(Entry, bytes);
        Assert.False(StartupIntegration.SetWindowAtLogin(false, "/unused"));
        Assert.Equal(bytes, File.ReadAllBytes(Entry));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AnExistingDirectoryIsRejectedWithoutClaimingIt(int index)
    {
        string path = StartupReceipt.StartupPaths[index];
        Directory.CreateDirectory(path);
        if (index == 2) Assert.Throws<IOException>(() => StartupReceipt.EnableLink(path, StartupReceipt.StartupPaths[1]));
        else Assert.Throws<IOException>(() => StartupReceipt.Write(path, "generated"));
        Assert.True(Directory.Exists(path));
        Assert.False(File.Exists(StartupReceipt.Path));
    }

}
