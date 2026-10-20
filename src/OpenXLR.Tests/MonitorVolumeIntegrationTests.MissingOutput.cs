using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    private sealed class StopLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stop.Token;
        public CancellationToken ApplicationStopped => _stop.Token;
        public void StopApplication() => _stop.Cancel();
        public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
    }

    /// <summary>
    /// A selected monitor output whose sink is missing (a headset whose card
    /// failed its probe, a base station unplugged) stays selected and saved:
    /// chosen while missing, across a daemon restart while still missing,
    /// while it comes and goes, and with another output present that could
    /// stand in. The monitor routes to it whenever it is there.
    /// </summary>
    [MonitorPipeWireFact]
    public async Task AMissingMonitorOutputStaysSelectedAndSavedWhileItComesAndGoes()
    {
        Assert.StartsWith("openxlr-monitor-test-", Path.GetFileName(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")!));
        const string headset = "kept_headset", speakers = "kept_speakers";
        string dir = Directory.CreateTempSubdirectory("openxlr-kept-output-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        var pw = new PipeWireAdapter();
        uint speakersModule = pw.CreateNullSink(speakers, "Kept speakers");
        uint? headsetModule = null;
        try
        {
            new DaemonSettings { Submixer = true }.Save();
            new MixerSettings
            {
                UserChannels = [new("game", "Game")], UserMixes = [],
                MonitorOutputs = [speakers],
                EnforcedDefaultSink = "OpenXLR_ch_game",
            }.Save();
            var config = new ConfigurationBuilder().Build();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
            using var lifetime = new StopLifetime();

            // Chosen while its sink is missing, then a restart while it still is.
            await Run(async (mixer, hub) =>
            {
                Assert.Equal([speakers], mixer.Snapshot()!.MonitorOutputs);
                var picked = await hub.ExecuteForApiAsync($$"""{"cmd":"setMonitorOutputs","devices":["{{headset}}"]}""");
                Assert.True(picked.Ok);
                await Sweeps();
                Assert.Equal([headset], mixer.Snapshot()!.MonitorOutputs);
            });
            Assert.Equal([headset], MixerSettings.Load()!.MonitorOutputs);
            await Run(async (mixer, _) =>
            {
                await Sweeps();
                Assert.Equal([headset], mixer.Snapshot()!.MonitorOutputs);
                Assert.DoesNotContain(MixLinks(), link => link.To.StartsWith(speakers + ":", StringComparison.Ordinal));

                // It returns and the monitor plays on it, leaves, and returns again.
                headsetModule = pw.CreateNullSink(headset, "Kept headset");
                Assert.True(SpinWait.SpinUntil(() => MixLinks().Any(link => link.To.StartsWith(headset + ":", StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(5)), "The returning output was not linked.");
                pw.UnloadModule(headsetModule.Value);
                headsetModule = null;
                Assert.True(SpinWait.SpinUntil(() => pw.GetSinkVolume(headset) is null, TimeSpan.FromSeconds(3)));
                await Sweeps();
                Assert.Equal([headset], mixer.Snapshot()!.MonitorOutputs);
                headsetModule = pw.CreateNullSink(headset, "Kept headset");
                Assert.True(SpinWait.SpinUntil(() => MixLinks().Any(link => link.To.StartsWith(headset + ":", StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(5)), "The output did not get its route back.");
                Assert.Equal([headset], mixer.Snapshot()!.MonitorOutputs);
                Assert.DoesNotContain(MixLinks(), link => link.To.StartsWith(speakers + ":", StringComparison.Ordinal));
            });
            Assert.Equal([headset], MixerSettings.Load()!.MonitorOutputs);

            async Task Run(Func<MixerService, WebSocketHub, Task> body)
            {
                using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
                var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);
                try
                {
                    await mixer.StartAsync(CancellationToken.None);
                    Assert.True(mixer.Built);
                    await body(mixer, hub);
                }
                finally { await mixer.StopAsync(CancellationToken.None); }
            }
            // Several daemon sweeps (one a second), and a debounced save.
            static Task Sweeps() => Task.Delay(TimeSpan.FromSeconds(3));
        }
        finally
        {
            if (headsetModule is uint module) pw.UnloadModule(module);
            pw.UnloadModule(speakersModule);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }
}
