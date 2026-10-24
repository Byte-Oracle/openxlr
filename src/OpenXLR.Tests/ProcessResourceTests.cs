using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.UI;
using Resources = OpenXLR.Core.LinuxProcessResources;

namespace OpenXLR.Tests;

public sealed class ProcessResourceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("openxlr-process-resource-").FullName;
    private string ProcessDir => Path.Combine(_root, "12345");

    public ProcessResourceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "self"));
        File.WriteAllText(Path.Combine(_root, "self", "status"), "Uid:\t1000\t1000\t1000\t1000\n");
        Directory.CreateDirectory(Path.Combine(ProcessDir, "fd"));
        File.WriteAllText(Path.Combine(ProcessDir, "comm"), "pipewire-pulse\n");
        File.WriteAllText(Path.Combine(ProcessDir, "status"), "Name:\tpipewire-pulse\nUid:\t1000\t1000\t1000\t1000\nState:\tS (sleeping)\nThreads:\t3\nVmRSS:\t2048 kB\n");
        File.WriteAllText(Path.Combine(ProcessDir, "limits"), "Max open files            4                    8                    files\nMax locked memory         8388608              unlimited            bytes\n");
        for (int i = 0; i < 3; i++) File.WriteAllText(Path.Combine(ProcessDir, "fd", i.ToString()), "");
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void LinuxProcessMetadataIsReadableThroughTheBoundedFileReader()
    {
        if (!OperatingSystem.IsLinux()) return;
        string name = File.ReadAllText("/proc/self/comm").Trim();
        var result = Resources.Inspect("/proc/self", name, Resources.RealUid("/proc/self")!, descriptorCap: 1024);
        Assert.NotNull(result);
        Assert.NotNull(result.OpenFiles);
        Assert.NotNull(result.OpenFileSoftLimit);
        Assert.NotNull(result.ResidentMemoryBytes);
        Assert.NotNull(result.State);
    }

    [Fact]
    public void ResourcesDescribeTheRunningProcessWithoutIdsOrEnvironment()
    {
        var result = Resources.Inspect(ProcessDir, "pipewire-pulse", Resources.RealUid(Path.Combine(_root, "self"))!);
        Assert.NotNull(result);
        Assert.Equal(3, result.OpenFiles);
        Assert.Equal(4, result.OpenFileSoftLimit);
        Assert.Equal(8, result.OpenFileHardLimit);
        Assert.Equal(8388608, result.LockedMemorySoftLimit);
        Assert.Equal(-1, result.LockedMemoryHardLimit);
        Assert.Equal(2097152, result.ResidentMemoryBytes);
        Assert.Equal(3, result.Threads);
        Assert.Equal("S (sleeping)", result.State);
        string json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("12345", json);
        Assert.DoesNotContain(_root, json);
    }

    [Theory]
    [InlineData("pipewire-pulse", "2000")]
    [InlineData("another-program", "1000")]
    public void OtherUsersAndReplacedProcessesAreRefused(string name, string uid)
        => Assert.Null(Resources.Inspect(ProcessDir, name, uid));

    [Fact]
    public void MissingLimitsRemainUnknownAndDescriptorCountingHasAnExplicitCap()
    {
        File.Delete(Path.Combine(ProcessDir, "limits"));
        var result = Resources.Inspect(ProcessDir, "pipewire-pulse", "1000", descriptorCap: 2);
        Assert.NotNull(result);
        Assert.Equal(2, result.OpenFiles);
        Assert.True(result.OpenFilesCapped);
        Assert.Null(result.OpenFileSoftLimit);
        Assert.Null(result.LockedMemorySoftLimit);
        Directory.Delete(Path.Combine(ProcessDir, "fd"), true);
        result = Resources.Inspect(ProcessDir, "pipewire-pulse", "1000");
        Assert.NotNull(result);
        Assert.Null(result.OpenFiles);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void InvalidCountCapsCannotOverflow(int cap)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Resources.Inspect(ProcessDir, "pipewire-pulse", "1000", cap));

    [Theory]
    [InlineData("unlimited", "unlimited", "files", -1L, -1L)]
    [InlineData("9223372036854775807", "9223372036854775807", "files", long.MaxValue, long.MaxValue)]
    [InlineData("9223372036854775808", "broken", "files", null, null)]
    [InlineData("-2", "+42", "files", null, null)]
    [InlineData("1024", "65536", "bytes", null, null)]
    public void LimitsPreserveUnlimitedAndRejectMalformedValues(string soft, string hard, string unit, long? expectedSoft, long? expectedHard)
    {
        var result = Resources.ParseLimit($"Max open files   {soft} {hard} {unit}\n", "Max open files", "files");
        Assert.Equal(expectedSoft, result.Soft);
        Assert.Equal(expectedHard, result.Hard);
    }

    [Fact]
    public void RuntimeReportDistinguishesLoadedUnitLimitsFromRunningLimits()
    {
        var report = AudioRuntimeDiagnostics.Read("MainPID=12345\nLimitNOFILESoft=65536\nId=pipewire-pulse.service\n", _root);
        var pulse = Assert.Single(report, r => r.Service == "pipewire-pulse.service");
        Assert.NotNull(pulse.Resources);
        Assert.Equal(2, pulse.Warnings.Count);
        Assert.Contains(pulse.Warnings, w => w.Contains("75%"));
        Assert.Contains(pulse.Warnings, w => w.Contains("differs"));
        Assert.DoesNotContain("12345", JsonSerializer.Serialize(report));
        Assert.All(report.Where(r => r != pulse), r => { Assert.Null(r.Resources); Assert.NotNull(r.Note); });
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("3", true)]
    [InlineData("4", true)]
    [InlineData("5", false)]
    [InlineData("9223372036854775807", false)]
    [InlineData("unlimited", false)]
    public void FileWarningsUseTheActualLimitWithoutIntegerOverflow(string limit, bool warning)
    {
        File.WriteAllText(Path.Combine(ProcessDir, "limits"), $"Max open files   {limit} {limit} files\n");
        var pulse = Assert.Single(AudioRuntimeDiagnostics.Read("Id=pipewire-pulse.service\nMainPID=12345\n", _root),
            r => r.Service == "pipewire-pulse.service");
        Assert.Equal(warning, pulse.Warnings.Count != 0);
    }

    [Fact]
    public void MalformedMemoryAndThreadCountsStayUnknown()
    {
        File.WriteAllText(Path.Combine(ProcessDir, "status"), "Uid:\t1000\t1000\t1000\t1000\nVmRSS:\t9223372036854775807 kB\nThreads:\t-1\n");
        var result = Resources.Inspect(ProcessDir, "pipewire-pulse", "1000");
        Assert.NotNull(result);
        Assert.Null(result.ResidentMemoryBytes);
        Assert.Null(result.Threads);
    }

    [Fact]
    public void MalformedUnitPidsCannotEscapeTheProcessRoot()
    {
        var report = AudioRuntimeDiagnostics.Read("Id=pipewire-pulse.service\nMainPID=../12345\n\nId=unknown.service\nMainPID=12345\n", _root);
        Assert.Equal(4, report.Count);
        Assert.All(report, r => Assert.Null(r.Resources));
    }
}
