using OpenXLR.Daemon;
using ProcessRunner = OpenXLR.Core.ProcessRunner;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class FlatpakRuntimeTests
{
    [Fact]
    public void FlatpakNeverHandsItsWindowToTheNativeInstallation()
    {
        string? previous = Environment.GetEnvironmentVariable("FLATPAK_ID");
        try
        {
            Environment.SetEnvironmentVariable("FLATPAK_ID", null);
            string native = OpenXLR.UI.SingleInstance.DefaultPath;
            Environment.SetEnvironmentVariable("FLATPAK_ID", OpenXLR.UI.FlatpakEnvironment.AppId);
            Assert.NotEqual(native, OpenXLR.UI.SingleInstance.DefaultPath);
            Assert.Equal(Path.GetDirectoryName(native), Path.GetDirectoryName(OpenXLR.UI.SingleInstance.DefaultPath));
            Assert.False(OpenXLR.UI.StartupIntegration.SetWindowAtLogin(true));
            Assert.False(OpenXLR.UI.StartupIntegration.SetDaemonAtLogin(true));
        }
        finally { Environment.SetEnvironmentVariable("FLATPAK_ID", previous); }
    }

    [Fact]
    public void SessionRulesKeepExistingUserFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "openxlr-rules-" + Guid.NewGuid());
        string source = Path.Combine(root, "source"), config = Path.Combine(root, "config");
        Directory.CreateDirectory(source); Directory.CreateDirectory(config);
        try
        {
            foreach (string rule in OpenXLR.UI.FlatpakSetup.Rules) File.WriteAllText(Path.Combine(source, rule), "rule");
            OpenXLR.UI.FlatpakSetup.InstallRules(source, config);
            OpenXLR.UI.FlatpakSetup.InstallRules(source, config);
            string target = Path.Combine(config, "wireplumber", "wireplumber.conf.d", OpenXLR.UI.FlatpakSetup.Rules[0]);
            File.WriteAllText(target, "user rule");
            Assert.Throws<IOException>(() => OpenXLR.UI.FlatpakSetup.InstallRules(source, config));
            Assert.Equal("user rule", File.ReadAllText(target));
            File.Delete(target); File.CreateSymbolicLink(target, Path.Combine(source, OpenXLR.UI.FlatpakSetup.Rules[0]));
            Assert.Throws<IOException>(() => OpenXLR.UI.FlatpakSetup.InstallRules(source, config));
            Assert.Equal("rule", File.ReadAllText(target));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RestartWaitsForThePreviousDaemonToStop()
    {
        string root = Path.Combine(Path.GetTempPath(), "openxlr-session-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            string script = ExecutableScript.Write(Path.Combine(root, "child"),
                "#!/bin/sh\nroot='" + root + "'\ntrap 'echo stop >> \"$root/events\"; exit 0' TERM\necho start >> \"$root/events\"\nwhile :; do sleep 0.05; done\n");
            using (var session = new OpenXLR.UI.FlatpakSession(script))
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                string path = Path.Combine(root, "events");
                while (!File.Exists(path)) await Task.Delay(10, deadline.Token);
                Assert.True(session.Restart());
                while (File.ReadAllLines(path).Length < 3) await Task.Delay(10, deadline.Token);
                Assert.Equal(new[] { "start", "stop", "start" }, File.ReadAllLines(path));
            }
            Assert.Equal(new[] { "start", "stop", "start", "stop" }, File.ReadAllLines(Path.Combine(root, "events")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedChildStopsGracefullyOrIsKilledWithinItsDeadline(bool ignoreTerm)
    {
        string root = Path.Combine(Path.GetTempPath(), "openxlr-owned-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        using var stop = new CancellationTokenSource();
        Task<int>? run = null;
        try
        {
            string script = ExecutableScript.Write(Path.Combine(root, "child"),
                "#!/bin/sh\n" + (ignoreTerm ? "trap '' TERM\n" : "trap 'echo stopped > \"$1/done\"; exit 0' TERM\n")
                + "echo ready > \"$1/ready\"\nwhile :; do sleep 0.05; done\n");
            run = ProcessRunner.RunOwnedAsync(script, [root], stop.Token, TimeSpan.FromMilliseconds(300));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(Path.Combine(root, "ready"))) await Task.Delay(10, deadline.Token);
            Assert.False(run.IsCompleted);
            stop.Cancel();
            int exit = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(!ignoreTerm, File.Exists(Path.Combine(root, "done")));
            Assert.Equal(!ignoreTerm, exit == 0);
        }
        finally
        {
            stop.Cancel();
            if (run is not null) await run.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CancelledOwnerNeverStartsItsChild()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunOwnedAsync(
            "/missing-openxlr-child", [], stop.Token, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void LogRotatesAndRefusesSymbolicLinks()
    {
        string root = Path.Combine(Path.GetTempPath(), "openxlr-log-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "daemon.log");
            using var log = new FlatpakLog(path);
            for (int i = 0; i < 140; i++) log.Write(new string('a', 9000));
            Assert.True(File.Exists(path + ".old"));
            Assert.InRange(new FileInfo(path).Length, 1, FlatpakLog.Limit + 8193);
            Assert.InRange(new FileInfo(path + ".old").Length, 1, FlatpakLog.Limit + 8193);
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            string target = Path.Combine(root, "private");
            File.WriteAllText(target, "keep");
            File.Delete(path); File.CreateSymbolicLink(path, target);
            log.Write("do not follow");
            Assert.Equal("keep", File.ReadAllText(target));
        }
        finally { Directory.Delete(root, true); }
    }
}
