using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The device half of the state comes from the last good read, never from a
/// USB read of its own: a unit that stops answering is shown with its last
/// values and a warning, said once in the journal, and a read that hangs does
/// not hold up a state message. The manager reads gain locks and daemon
/// settings from the configuration directory, which these tests redirect.
/// </summary>
[Collection("xdg-config")]
public sealed class DeviceStateCacheTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-state-cache-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private readonly TimeSpan _previousDelay = DeviceManager.PollRetryDelay;

    public DeviceStateCacheTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
        DeviceManager.PollRetryDelay = TimeSpan.Zero;   // reopen on the next sweep
    }

    public void Dispose()
    {
        DeviceManager.PollRetryDelay = _previousDelay;
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>A Wave:3 whose reads fail while <see cref="Failing"/> is set, or wait while <see cref="Block"/> is.</summary>
    private sealed class Unit : IAudioDevice
    {
        public volatile bool Failing;
        public int GainDb = 40;
        public int Reads;
        public ManualResetEventSlim? Block;
        public readonly ManualResetEventSlim Entered = new();
        public DeviceInfo Info { get; } = new("Elgato", "Wave:3", 0x0fd9, 0x0070);
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, Mute = true, HpVolume = true, RetainsSettings = true };
        public bool Connected { get; private set; }
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public void Dispose() { }
        public DeviceState ReadState()
        {
            Interlocked.Increment(ref Reads);
            if (Block is { } block)
            {
                Entered.Set();
                Assert.True(block.Wait(TimeSpan.FromSeconds(10)));
            }
            if (Failing) throw new InvalidOperationException("read config block: Operation timed out");
            return new DeviceState { GainDb = GainDb };
        }
        public void SetGainDb(int db) => GainDb = db;
        public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    [Fact]
    public void AUnitThatStopsAnsweringKeepsItsLastValuesAndIsReportedOnce()
    {
        var unit = new Unit();
        var log = new RecordingLogger<DeviceManager>();
        using var manager = new DeviceManager(log, new ConfigurationBuilder().Build(), () => [unit]);
        manager.SweepOnce();
        Assert.Equal(40, manager.Snapshot().State!.GainDb);
        Assert.Null(manager.Warning);

        // What a client sees while the unit is open again but has not answered.
        var reopened = new List<(StateMessage State, string? Warning)>();
        manager.StateChanged += message =>
        {
            if (message.Connected) reopened.Add((manager.Snapshot(), manager.Warning));
        };

        unit.Failing = true;
        for (int i = 0; i < 5; i++) manager.SweepOnce();   // fail, drop, reopen, fail again

        Assert.False(manager.Snapshot().Connected);
        Assert.Contains("Wave:3 does not answer USB reads (read config block: Operation timed out)", manager.Warning);
        Assert.NotEmpty(reopened);
        Assert.All(reopened, seen =>
        {
            Assert.Equal(40, seen.State.State!.GainDb);
            Assert.NotNull(seen.Warning);
        });
        // One sentence for the whole wedge, not one per reconnect.
        var warning = Assert.Single(log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains("does not answer USB reads", warning.Message);
        Assert.Null(warning.Exception);

        // Serving the state never reads the device.
        int reads = unit.Reads;
        for (int i = 0; i < 100; i++) _ = manager.Snapshot();
        Assert.Equal(reads, unit.Reads);

        unit.Failing = false;
        unit.GainDb = 30;
        manager.SweepOnce();
        Assert.True(manager.Snapshot().Connected);
        Assert.Equal(30, manager.Snapshot().State!.GainDb);
        Assert.Null(manager.Warning);
        Assert.Single(log.Entries, e => e.Message.Contains("answers USB reads again"));
        Assert.Single(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void ACommandStillReachesTheUnitAndItsResultIsServedAtOnce()
    {
        var unit = new Unit();
        using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [unit]);
        manager.SweepOnce();
        Assert.Null(manager.Apply("gain", JsonDocument.Parse("25").RootElement));
        Assert.Equal(25, unit.GainDb);
        Assert.Equal(25, manager.Snapshot().State!.GainDb);
    }

    [Fact]
    public void AUnitThatLeavesTheBusIsNoLongerReported()
    {
        var unit = new Unit();
        var attached = new List<IAudioDevice> { unit };
        using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [.. attached]);
        manager.SweepOnce();
        unit.Failing = true;
        manager.SweepOnce();
        Assert.NotNull(manager.Warning);
        attached.Clear();
        manager.SweepOnce();
        Assert.Null(manager.Warning);
        Assert.False(manager.Snapshot().Connected);
    }

    [Fact]
    public async Task AStateMessageDoesNotWaitForAReadThatHangs()
    {
        var unit = new Unit();
        using var stopping = new CancellationTokenSource();
        using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [unit]);
        using var mixer = new MixerService(NullLogger<MixerService>.Instance, new ConfigurationBuilder().Build(), manager);
        var hub = new WebSocketHub(manager, mixer, NullLogger<WebSocketHub>.Instance, new Lifetime(stopping.Token));
        manager.SweepOnce();

        using var release = new ManualResetEventSlim();
        unit.Block = release;
        Task poll = Task.Run(manager.SweepOnce);   // holds the device lock inside the read
        try
        {
            Assert.True(unit.Entered.Wait(TimeSpan.FromSeconds(5)));
            // A wait here would last as long as the read: it never ends.
            StateMessage state = await Task.Run(hub.Snapshot).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(state.Connected);
            Assert.Equal(40, state.State!.GainDb);
            Assert.Equal("0fd9:0070", state.Device!.UsbId);
            Assert.True(Assert.Single(state.Detected!).Active);
            Assert.False(poll.IsCompleted);
        }
        finally
        {
            release.Set();
            await poll.WaitAsync(TimeSpan.FromSeconds(10));
            stopping.Cancel();
            manager.SuspendSession();
        }
    }

    private sealed class Lifetime(CancellationToken stopping) : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}

/// <summary>Keeps what was logged, for tests that check how often and how a condition is reported.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public readonly record struct Entry(LogLevel Level, string Message, Exception? Exception);
    private readonly ConcurrentQueue<Entry> _entries = new();
    public IReadOnlyList<Entry> Entries => [.. _entries];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _entries.Enqueue(new(logLevel, formatter(state, exception), exception));
}
