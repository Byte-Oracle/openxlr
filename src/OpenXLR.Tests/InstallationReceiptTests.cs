using System.Text.Json;
using OpenXLR.UI;
using Receipt = OpenXLR.UI.InstallationReceipt;
using Owned = OpenXLR.UI.InstalledFile;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class InstallationReceiptTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "openxlr-receipt-" + Guid.NewGuid());
    private readonly string? _old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private string Entry => Path.Combine(_home, "autostart/openxlr.desktop");
    private string Manifest => Path.Combine(_home, "share/openxlr/installations/openxlr.json");
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    public InstallationReceiptTests()
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
    { Package = "openxlr", Manager = "debian", Version = "1.2.3", Commit = Commit, Prefix = _home, Files = [.. files, new() { Path = Manifest, Kind = "receipt" }] };

    private string WriteReceipt(Receipt receipt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Manifest)!);
        string path = Path.Combine(_home, "share/openxlr/installations/" + receipt.Package + ".json");
        receipt = receipt with { Files = [.. receipt.Files.Where(f => f.Kind != "receipt"), new() { Path = path, Kind = "receipt" }] };
        File.WriteAllText(path, JsonSerializer.Serialize(receipt, Receipt.Json));
        return path;
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"version\":\"a\",\"version\":\"b\"}")]
    [InlineData("{\"version\":\"a\",\"Version\":\"b\"}")]
    public void UnreadableReceiptsAreRejected(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Manifest)!);
        File.WriteAllText(Manifest, json);
        Assert.Throws<IOException>(() => Receipt.Read(Manifest));
    }

    [Fact]
    public void UnsafePathsKindsHashesAndDuplicateEntriesAreRejected()
    {
        string file = Path.Combine(_home, "file");
        Owned valid = new() { Path = file, Sha256 = new string('a', 64) };
        Package(valid).Validate();
        foreach (Owned invalid in new[] {
            valid with { Path = "/etc/passwd" }, valid with { Path = _home + "/../escape" },
            valid with { Path = _home + "-other/file" }, valid with { Path = "relative" },
            valid with { Sha256 = "bad" }, valid with { Sha256 = new string('A', 64) }, valid with { Mode = 4095 },
            valid with { Kind = "link", LinkTarget = null }, valid with { Kind = "unknown" } })
            Assert.Throws<IOException>(() => Package(invalid).Validate());
        Assert.Throws<IOException>(() => Package(valid, valid).Validate());
        Assert.Throws<IOException>(() => (Package() with { Files = [null!] }).Validate());
        Assert.Throws<IOException>(() => (Package() with { Prefix = "/" }).Validate());
        Assert.Throws<IOException>(() => (Package() with { Package = "openxlr; touch bad" }).Validate());
        Assert.Throws<IOException>(() => Receipt.Read(WriteReceipt(Package()) + ".missing"));
    }

    [Fact]
    public void OversizedReceiptsAndFifosCannotBlockTheDoctor()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Manifest)!);
        using (var file = File.Create(Manifest)) file.SetLength(Receipt.MaxBytes + 1);
        Assert.Throws<IOException>(() => Receipt.Read(Manifest));
        File.Delete(Manifest);
        if (!OperatingSystem.IsLinux()) return;
        var result = OpenXLR.Core.ProcessRunner.Run("mkfifo", [Manifest], TimeSpan.FromSeconds(5));
        Assert.True(result.Ok, result.Stderr);
        Assert.Throws<IOException>(() => Receipt.Read(Manifest));
    }

    [Fact]
    public async Task DoctorReportsOnlyExactOwnedPathsAndConcreteRepairs()
    {
        string file = Path.Combine(_home, "installed");
        File.WriteAllText(file, "installed");
        Owned entry = new() { Path = file, Sha256 = Receipt.Digest(file) };
        string manifest = WriteReceipt(Package(entry));
        DoctorReport healthy = await InstallationDoctor.Inspect([manifest], null, false);
        Assert.Empty(healthy.Findings);
        File.WriteAllText(Path.Combine(_home, "unrelated"), "not owned");
        Assert.Empty((await InstallationDoctor.Inspect([manifest], null, false)).Findings);
        File.WriteAllText(file, "edited");
        DoctorFinding changed = Assert.Single((await InstallationDoctor.Inspect([manifest], null, false)).Findings);
        Assert.Equal("contents were modified", changed.Problem);
        Assert.Equal("sudo apt-get install --reinstall 'openxlr'", changed.Repair);
        File.Delete(file);
        Assert.Equal("missing", Assert.Single((await InstallationDoctor.Inspect([manifest], null, false)).Findings).Problem);
    }

    [Fact]
    public async Task ACompanionReceiptCanBeCheckedWithoutInstallingTheMainPackage()
    {
        string file = Path.Combine(_home, "yabridge-host");
        File.WriteAllText(file, "companion");
        string path = WriteReceipt(Package(new Owned { Path = file, Sha256 = Receipt.Digest(file) }) with { Package = "openxlr-yabridge" });
        var output = new StringWriter();
        Assert.Equal(0, await InstallationCommands.Run(["doctor", "--receipt", path, "--no-runtime", "--json"], output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Empty(json.RootElement.GetProperty("findings").EnumerateArray());
    }

    [Fact]
    public void LinksModesAndRemovedLeftoversAreCheckedWithoutFollowingOtherFiles()
    {
        string file = Path.Combine(_home, "installed");
        File.WriteAllText(file, "installed");
        Owned entry = new() { Path = file, Sha256 = Receipt.Digest(file) };
        Assert.Equal("left behind after removal", Receipt.Problem(entry with { Removed = true }, _home));
        if (!OperatingSystem.IsWindows())
        {
            entry = entry with { Mode = (int)File.GetUnixFileMode(file) & 511 };
            File.SetUnixFileMode(file, UnixFileMode.UserRead);
            Assert.Equal("permissions were modified", Receipt.Problem(entry, _home));
        }
        string link = Path.Combine(_home, "link");
        File.CreateSymbolicLink(link, "missing");
        Owned ownedLink = new() { Path = link, Kind = "link", LinkTarget = "missing" };
        Assert.Null(Receipt.Problem(ownedLink, _home));
        Assert.Equal("left behind after removal", Receipt.Problem(ownedLink with { Removed = true }, _home));
        File.Delete(link);
        File.CreateSymbolicLink(link, file);
        Assert.Equal("symbolic link was modified", Receipt.Problem(ownedLink, _home));
        Directory.CreateSymbolicLink(Path.Combine(_home, "linked-parent"), _home);
        Assert.Equal("parent is a symbolic link", Receipt.Problem(entry with { Path = Path.Combine(_home, "linked-parent/installed") }, _home));
    }

    [Fact]
    public void ContradictoryEntryFieldsAreRejected()
    {
        Owned file = new() { Path = Path.Combine(_home, "installed"), Sha256 = new string('a', 64) };
        Owned link = new() { Path = file.Path, Kind = "link", LinkTarget = "target" };
        foreach (Owned invalid in new[] { file with { LinkTarget = "target" },
            file with { ReplacedMode = 384 }, link with { Sha256 = file.Sha256 }, link with { Mode = 511 } })
            Assert.Throws<IOException>(() => Package(invalid).Validate());
    }

    [Fact]
    public void AStagingPrefixLinkCannotMakeTheDoctorInspectOutsideItsRoot()
    {
        string outside = Path.Combine(_home, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "installed"), "foreign");
        string staging = Path.Combine(_home, "staging");
        Directory.CreateDirectory(staging);
        Directory.CreateSymbolicLink(Path.Combine(staging, "usr"), outside);
        Owned file = new() { Path = "/usr/installed", Sha256 = Receipt.Digest(Path.Combine(outside, "installed")) };
        Assert.Equal("parent is a symbolic link", Receipt.Problem(file, "/usr", staging));
    }

    [Fact]
    public async Task AStagedReceiptItselfCannotBeReadThroughARedirectedParent()
    {
        string staging = Path.Combine(_home, "staging");
        Directory.CreateDirectory(staging);
        string outside = Path.Combine(_home, "outside");
        Directory.CreateDirectory(outside);
        string manifest = Path.Combine(outside, "openxlr.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(Package() with { Prefix = "/usr" }, Receipt.Json));
        Directory.CreateSymbolicLink(Path.Combine(staging, "usr"), outside);
        var report = await InstallationDoctor.Inspect([Path.Combine(staging, "usr/openxlr.json")], staging, false);
        Assert.Empty(report.Receipts);
        Assert.Contains(report.Findings, f => f.Problem.Contains("symbolic link", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("match")]
    [InlineData("different")]
    [InlineData("old")]
    [InlineData("malformed")]
    [InlineData("unavailable")]
    public async Task DoctorComparesTheInstalledAssemblyWithTheActualRunningBuild(string scenario)
    {
        string assembly = Path.Combine(_home, "lib/OpenXLR.Daemon.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);
        File.Copy(typeof(OpenXLR.Daemon.StateMessage).Assembly.Location, assembly);
        string installed = InstallationDoctor.ModuleId(assembly);
        Assert.Equal(OpenXLR.Daemon.DaemonVersion.BuildId, installed);
        string state = scenario switch
        {
            "match" => JsonSerializer.Serialize(new OpenXLR.Daemon.StateMessage()),
            "different" => "{\"daemonBuildId\":\"" + Guid.NewGuid() + "\"}",
            "old" => "{\"daemonVersion\":\"0.1.1\"}",
            _ => "[]"
        };
        DoctorReport report = await InstallationDoctor.Inspect([WriteReceipt(Package(new Owned { Path = assembly, Sha256 = Receipt.Digest(assembly) }))], null, true,
            _ => scenario == "unavailable" ? Task.FromException<string>(new IOException("not running")) : Task.FromResult(state));
        Assert.Equal(scenario == "match", report.Findings.Count == 0);
        if (scenario == "match") Assert.Equal("matches installed build", report.Daemon!.Status);
        else Assert.Equal("systemctl --user restart openxlr-daemon.service", Assert.Single(report.Findings).Repair);
    }

    [Theory]
    [InlineData("older", "a daemon from before the update is still running and does not serve the control socket", "systemctl --user restart openxlr-daemon.service")]
    [InlineData("none", "not running", "systemctl --user start openxlr-daemon.service")]
    [InlineData("unknown", "not verified: connection refused", "systemctl --user restart openxlr-daemon.service")]
    public async Task DoctorSaysWhyTheDaemonCouldNotBeRead(string found, string status, string repair)
    {
        DaemonPresence presence = found switch
        {
            "older" => DaemonPresence.OlderDaemon,
            "none" => DaemonPresence.NotRunning,
            _ => DaemonPresence.Reachable,
        };
        string assembly = Path.Combine(_home, "lib/OpenXLR.Daemon.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);
        File.Copy(typeof(OpenXLR.Daemon.StateMessage).Assembly.Location, assembly);
        DoctorReport report = await InstallationDoctor.Inspect([WriteReceipt(Package(new Owned { Path = assembly, Sha256 = Receipt.Digest(assembly) }))], null, true,
            _ => Task.FromException<string>(new HttpRequestException("connection refused")), probe: _ => Task.FromResult(presence));
        Assert.Equal(status, report.Daemon!.Status);
        DoctorFinding finding = Assert.Single(report.Findings);
        Assert.Equal(status, finding.Problem);
        Assert.Equal(repair, finding.Repair);
    }

    [Fact]
    public void AnUnreadablePackageReceiptDoesNotAbortDiscoveryOfOtherInstallations()
    {
        if (OperatingSystem.IsWindows()) return;
        string denied = Path.Combine(_home, "denied/share/openxlr/installations");
        Directory.CreateDirectory(denied);
        File.WriteAllText(Path.Combine(denied, "openxlr.json"), "unreadable");
        string manifest = WriteReceipt(Package());
        string? previous = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_home, "denied/share") + ":" + Path.Combine(_home, "share"));
            File.SetUnixFileMode(denied, UnixFileMode.None);
            Assert.Contains(Path.Combine(denied, "openxlr.json"), InstallationHistory.PackageReceipts(Path.Combine(_home, "source-build")));
            Assert.Contains(manifest, InstallationHistory.PackageReceipts(Path.Combine(_home, "source-build")));
        }
        finally
        {
            File.SetUnixFileMode(denied, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", previous);
        }
    }

    [Theory]
    [InlineData("receipt", false)]
    [InlineData("receipt", true)]
    [InlineData("prefix", false)]
    [InlineData("prefix", true)]
    [InlineData("directory", false)]
    [InlineData("directory", true)]
    public async Task NixProfileDiscoveryResolvesLinkedReceiptsAndTheirParents(string kind, bool relative)
    {
        if (OperatingSystem.IsWindows()) return;
        string manifest = WriteReceipt(Package() with { Manager = "nix" });
        string profile = Path.Combine(_home, "profile");
        string Target(string link, string target) => relative ? Path.GetRelativePath(Path.GetDirectoryName(link)!, target) : target;
        if (kind == "prefix") Directory.CreateSymbolicLink(profile, Target(profile, _home));
        else
        {
            Directory.CreateDirectory(Path.Combine(profile, "share"));
            if (kind == "directory")
            {
                string link = Path.Combine(profile, "share/openxlr");
                Directory.CreateSymbolicLink(link, Target(link, Path.Combine(_home, "share/openxlr")));
            }
            else
            {
                string link = Path.Combine(profile, "share/openxlr/installations/openxlr.json");
                Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                File.CreateSymbolicLink(link, Target(link, manifest));
            }
        }
        string? previous = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(profile, "share"));
            string[] paths = [.. InstallationHistory.PackageReceipts(Path.Combine(_home, "source-build"))
                .Where(path => path.StartsWith(_home + '/', StringComparison.Ordinal))];
            Assert.Contains(manifest, paths);
            Assert.Empty((await InstallationDoctor.Inspect(paths, null, false)).Findings);
        }
        finally { Environment.SetEnvironmentVariable("XDG_DATA_DIRS", previous); }
    }

    [Fact]
    public async Task AProfileLinkLoopIsReportedWithoutHidingOtherInstallations()
    {
        if (OperatingSystem.IsWindows()) return;
        string manifest = WriteReceipt(Package());
        string loop = Path.Combine(_home, "loop");
        Directory.CreateSymbolicLink(loop, "loop");
        string? previous = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", loop + "/share:" + Path.Combine(_home, "share"));
            string[] paths = [.. InstallationHistory.PackageReceipts(Path.Combine(_home, "source-build"))
                .Where(path => path.StartsWith(_home + '/', StringComparison.Ordinal))];
            Assert.Contains(manifest, paths);
            DoctorReport report = await InstallationDoctor.Inspect(paths, null, false);
            Assert.Contains(report.Receipts, receipt => receipt.Path == manifest);
            Assert.Contains(report.Findings, finding => finding.Path.StartsWith(loop + '/', StringComparison.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable("XDG_DATA_DIRS", previous); }
    }

    [Fact]
    public async Task ArchivedOwnershipSurvivesAnUpgradeAndFindsOnlyRetiredPaths()
    {
        string oldFile = Path.Combine(_home, "old-helper");
        string shared = Path.Combine(_home, "shared-helper");
        File.WriteAllText(oldFile, "old build");
        File.WriteAllText(shared, "current build");
        WriteReceipt(Package(new Owned { Path = oldFile, Sha256 = Receipt.Digest(oldFile) }, new Owned { Path = shared, Sha256 = Receipt.Digest(shared) }));
        InstallationHistory.Remember(Manifest);
        InstallationHistory.Remember(Manifest);
        string archive = Assert.Single(InstallationHistory.ArchivedReceipts());
        WriteReceipt(Package(new Owned { Path = shared, Sha256 = Receipt.Digest(shared) }) with { Version = "1.2.4" });
        DoctorReport report = await InstallationDoctor.Inspect([Manifest, archive], null, false, prefixes: [_home]);
        DoctorFinding leftover = Assert.Single(report.Findings);
        Assert.Equal(oldFile, leftover.Path);
        Assert.Contains("sudo mv -T -i --", leftover.Repair);
        Assert.Equal("old build", File.ReadAllText(oldFile)); // doctor is read-only
        File.Delete(oldFile);
        Assert.Empty((await InstallationDoctor.Inspect([Manifest, archive], null, false)).Findings);
        // A damaged record is skipped when saving; the same inventory saved
        // again lands under the same name and reads again.
        File.Copy(archive, Manifest, overwrite: true);
        File.WriteAllText(archive, "modified archive");
        InstallationHistory.Remember(Manifest);
        Assert.Equal(archive, Assert.Single(InstallationHistory.ArchivedReceipts()));
        Assert.Equal("1.2.3", Receipt.Read(archive, archived: true).Version);
    }

    [Fact]
    public async Task ADamagedArchiveDoesNotStopNewerInventoriesFromBeingRecorded()
    {
        Directory.CreateDirectory(InstallationHistory.DirectoryPath);
        string corrupt = Path.Combine(InstallationHistory.DirectoryPath, new string('b', 64) + ".json");
        File.WriteAllText(corrupt, "not a receipt");
        string misnamed = Path.Combine(InstallationHistory.DirectoryPath, new string('c', 64) + ".json");
        string older = JsonSerializer.Serialize(Package() with { Version = "1.2.2" }, Receipt.Json);
        File.WriteAllText(misnamed, older);
        WriteReceipt(Package());
        InstallationHistory.Remember(Manifest);
        string[] archives = [.. InstallationHistory.ArchivedReceipts()];
        string record = Assert.Single(archives.Except([corrupt, misnamed]));
        Assert.Equal("1.2.3", Receipt.Read(record, archived: true).Version);
        Assert.Equal("not a receipt", File.ReadAllText(corrupt));
        Assert.Equal(older, File.ReadAllText(misnamed));
        DoctorReport report = await InstallationDoctor.Inspect([Manifest, .. archives], null, false);
        Assert.Equal(2, report.Findings.Count);
        foreach (string bad in new[] { corrupt, misnamed })
            Assert.StartsWith("mv -T -i -- ", Assert.Single(report.Findings, f => f.Path == bad).Repair);
    }

    [Theory]
    [InlineData("/usr", "/usr/bin/env", true)]
    [InlineData("/etc", "/etc/passwd", false)]
    public async Task AMoveCommandIsOnlySuggestedForLeftoversUnderThePackagePrefixes(string prefix, string path, bool movable)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path) || new FileInfo(path).LinkTarget is not null) return;
        Receipt history = Package() with
        {
            Prefix = prefix,
            Files = [new Owned { Path = path, Sha256 = Receipt.Digest(path) }, new() { Path = prefix + "/share/openxlr/installations/openxlr.json", Kind = "receipt" }],
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(history, Receipt.Json);
        Directory.CreateDirectory(InstallationHistory.DirectoryPath);
        string archive = Path.Combine(InstallationHistory.DirectoryPath, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) + ".json");
        File.WriteAllBytes(archive, bytes);
        DoctorReport report = await InstallationDoctor.Inspect([archive], null, false);
        DoctorFinding finding = Assert.Single(report.Findings, f => f.Path == path);
        if (movable) Assert.StartsWith("sudo mv -T -i -- ", finding.Repair);
        else
        {
            Assert.Equal("namei -l -- " + InstallationDoctor.Quote(path), finding.Repair);
            Assert.Contains("outside the package prefixes", finding.Problem);
        }
    }

    [Fact]
    public async Task AnExplicitRemovalAuditReportsLeftoversWithoutExpectingThePackageToExist()
    {
        string file = Path.Combine(_home, "leftover");
        File.WriteAllText(file, "installed");
        string manifest = WriteReceipt(Package(new Owned { Path = file, Sha256 = Receipt.Digest(file) }));
        string receipt = Path.Combine(_home, Receipt.Digest(manifest) + ".json");
        File.Copy(manifest, receipt);
        var output = new StringWriter();
        Assert.Equal(1, await InstallationCommands.Run(["doctor", "--removed", "--receipt", receipt, "--json"], output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(file, Assert.Single(json.RootElement.GetProperty("findings").EnumerateArray()).GetProperty("path").GetString());
        File.Delete(file);
        Assert.Equal(0, await InstallationCommands.Run(["doctor", "--removed", "--receipt", receipt], new StringWriter()));
        Assert.Equal(2, await InstallationCommands.Run(["doctor", "--removed"], new StringWriter()));
    }

    [Fact]
    public void AReceiptCannotClaimADifferentInstallationLocation()
    {
        string real = WriteReceipt(Package());
        string forged = Path.Combine(_home, "forged.json");
        File.Copy(real, forged);
        Assert.Throws<IOException>(() => Receipt.Read(forged));
        Assert.Throws<IOException>(() => InstallationHistory.Remember(forged));
        string hashName = Path.Combine(_home, Receipt.Digest(forged) + ".json");
        File.Move(forged, hashName);
        Assert.Equal("openxlr", Receipt.Read(hashName, archived: true).Package);
        File.AppendAllText(hashName, " ");
        Assert.Throws<IOException>(() => Receipt.Read(hashName, archived: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkDestinationIsIndependentOfAbsoluteOrRelativeSpelling(bool directory)
    {
        string link = Path.Combine(_home, directory ? "user" : "openxlr");
        string target = Path.Combine(_home, "target");
        if (directory) Directory.CreateDirectory(target);
        else File.WriteAllText(target, "target");
        File.CreateSymbolicLink(link, "target");
        Owned entry = new() { Path = link, Kind = "link", LinkTarget = target };
        Assert.Null(Receipt.Problem(entry, _home));
        File.Delete(link);
        File.CreateSymbolicLink(link, target);
        Assert.Null(Receipt.Problem(entry with { LinkTarget = "target" }, _home));
        Assert.Equal("symbolic link was modified", Receipt.Problem(entry with { LinkTarget = "different" }, _home));
        // Staging must resolve relative links in the installed namespace.
        string staged = Path.Combine(_home, "staging");
        string path = Path.Combine(staged, entry.Path.TrimStart('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.CreateSymbolicLink(path, "target");
        Assert.Null(Receipt.Problem(entry, _home, staged));
    }

    [Fact]
    public async Task SourceProvenanceIsInformationalAndOptionalDocsCanBeAbsent()
    {
        string doc = Path.Combine(_home, "share/doc/openxlr/README.md");
        var entry = new Owned { Path = doc, Sha256 = new string('a', 64), Optional = true };
        string path = WriteReceipt(Package(entry) with { Dirty = true, Commit = null });
        var output = new StringWriter();
        Assert.Equal(0, await InstallationCommands.Run(["doctor", "--receipt", path, "--no-runtime"], output));
        Assert.Contains("Information: source commit is unknown", output.ToString());
        Assert.Contains("Information: built from a modified source tree", output.ToString());
        Directory.CreateDirectory(Path.GetDirectoryName(doc)!);
        File.WriteAllText(doc, "modified");
        Assert.Equal("contents were modified", Assert.Single((await InstallationDoctor.Inspect([path], null, false)).Findings).Problem);
        Assert.Throws<IOException>(() => Package(entry with { Path = Path.Combine(_home, "daemon" ) }).Validate());
    }

    [Fact]
    public void HistoryKeepsOneInventoryPerVersionAndRetainsRetiredPaths()
    {
        string retired = Path.Combine(_home, "retired");
        WriteReceipt(Package(new Owned { Path = retired, Sha256 = new string('a', 64) }));
        InstallationHistory.Remember(Manifest);
        for (int i = 0; i < 132; i++)
        {
            WriteReceipt(Package() with { Commit = i.ToString("x40") });
            InstallationHistory.Remember(Manifest);
        }
        string archive = Assert.Single(InstallationHistory.ArchivedReceipts());
        Assert.Contains(Receipt.Read(archive, archived: true).Files, f => f.Path == retired);
        WriteReceipt(Package() with { Manager = "nix" });
        InstallationHistory.Remember(Manifest);
        Assert.Single(InstallationHistory.ArchivedReceipts());
        WriteReceipt(Package() with { Version = "1.2.4" });
        InstallationHistory.Remember(Manifest);
        Assert.Equal(2, InstallationHistory.ArchivedReceipts().Count());
    }

    [Fact]
    public async Task HeadlessArgumentsAreValidated()
    {
        Assert.Equal(2, await InstallationCommands.Run(["doctor", "--unknown"], new StringWriter()));
        Assert.Equal(2, await InstallationCommands.Run(["doctor", "--root", "/"], new StringWriter()));
    }
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void ArchivingChecksTheCombinedHistoryBudgetBeforeDeserializingIt(int count)
    {
        Directory.CreateDirectory(InstallationHistory.DirectoryPath);
        for (int i = 0; i < count; i++)
        {
            string json = JsonSerializer.Serialize(Package() with { Version = "old-" + i }, Receipt.Json);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json.PadRight(Receipt.MaxBytes, ' '));
            string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            File.WriteAllBytes(Path.Combine(InstallationHistory.DirectoryPath, hash + ".json"), bytes);
        }
        WriteReceipt(Package());
        var error = Assert.Throws<IOException>(() => InstallationHistory.Remember(Manifest));
        Assert.Contains("16 MiB", error.Message);
        Assert.Equal(count, InstallationHistory.ArchivedReceipts().Count());
    }

    [Fact]
    public async Task ARejectedCurrentReceiptCannotTurnActiveFilesIntoLeftovers()
    {
        string file = Path.Combine(_home, "active-daemon");
        File.WriteAllText(file, "installed");
        WriteReceipt(Package(new Owned { Path = file, Sha256 = Receipt.Digest(file) }));
        InstallationHistory.Remember(Manifest);
        File.WriteAllText(Manifest, "damaged current receipt");
        DoctorReport report = await InstallationDoctor.Inspect([Manifest, .. InstallationHistory.ArchivedReceipts()], null, false);
        Assert.DoesNotContain(report.Findings, finding => finding.Path == file);
        Assert.Contains(report.Findings, finding => finding.Path == Manifest);
        Assert.Equal("installed", File.ReadAllText(file));
    }

    [Fact]
    public async Task ACorruptHistoricalReceiptGetsUserRecoveryInsteadOfPackageReinstallation()
    {
        WriteReceipt(Package());
        InstallationHistory.Remember(Manifest);
        string archive = InstallationHistory.ArchivedReceipts().Single();
        File.AppendAllText(archive, " ");
        DoctorReport report = await InstallationDoctor.Inspect([Manifest, archive], null, false);
        DoctorFinding finding = Assert.Single(report.Findings);
        Assert.Equal(archive, finding.Path);
        Assert.StartsWith("mv -T -i -- ", finding.Repair);
        Assert.DoesNotContain("sudo", finding.Repair);
    }

}
