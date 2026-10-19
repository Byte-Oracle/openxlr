using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class MixerShutdownTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopJoinsTheSweepAndCancelsItsPendingDefaultWrite(bool hostDeadlineExpired)
    {
        using var fixture = new Fixture();
        fixture.SetBuilt(true);
        fixture.Mixer.SetEnforcedDefaults("wanted-output", "wanted-input");
        fixture.Write("pause");
        using var sweep = new Timer(_ => fixture.Service.SweepOnce(), null, Timeout.Infinite, Timeout.Infinite);
        ServiceField("_streamSweep").SetValue(fixture.Service, sweep);
        try
        {
            sweep.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            Assert.True(SpinWait.SpinUntil(() => fixture.Exists("entered"), TimeSpan.FromSeconds(3)));
            Task stop = fixture.Service.StopAsync(new CancellationToken(hostDeadlineExpired));
            bool returnedBeforeCallback = stop.IsCompleted;
            fixture.Write("release");
            await stop.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.False(returnedBeforeCallback, "Stop returned while the default-device check was still running.");
            Assert.False(fixture.Exists("writes"));
            Assert.False(fixture.Service.Built);
            string[] calls = fixture.Read("calls");
            // A callback queued just before disposal must also stop at entry.
            fixture.Service.SweepOnce();
            Assert.Equal(calls, fixture.Read("calls"));
        }
        finally { fixture.Write("release"); await sweep.DisposeAsync(); }
    }

    [Fact]
    public async Task StopJoinsMeterCallbacksEvenAfterTheHostDeadlineExpires()
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var meter = new Timer(_ =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(5));
        }, null, Timeout.Infinite, Timeout.Infinite);
        ServiceField("_meterPush").SetValue(fixture.Service, meter);
        try
        {
            meter.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Task stop = fixture.Service.StopAsync(new CancellationToken(true));
            bool returnedBeforeCallback = stop.IsCompleted;
            release.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(returnedBeforeCallback);
        }
        finally { release.Set(); await meter.DisposeAsync(); }
    }

    [Fact]
    public async Task AnExpiredHostDeadlineStillDrainsTheStartupDefaultDefense()
    {
        using var fixture = new Fixture();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ServiceField("_defaultDefense").SetValue(fixture.Service, pending.Task);
        Task stop = fixture.Service.StopAsync(new CancellationToken(true));
        bool returnedBeforeDefense = stop.IsCompleted;
        pending.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(returnedBeforeDefense);
    }

    [Fact]
    public void DefaultsOnlyWriteWhileTheGraphIsBuilt()
    {
        using var fixture = new Fixture();
        fixture.Mixer.SetEnforcedDefaults("wanted-output", "wanted-input");
        Assert.False(fixture.Exists("calls"));
        fixture.SetBuilt(true);
        fixture.Write("different");
        Assert.True(fixture.Mixer.EnforceDefaults());
        Assert.Equal(["set-default-sink wanted-output", "set-default-source wanted-input"], fixture.Read("writes"));
        fixture.Mixer.TearDown();
        string[] calls = fixture.Read("calls");
        Assert.False(fixture.Mixer.EnforceDefaults());
        Assert.Equal(calls, fixture.Read("calls"));
    }

    [Fact]
    public async Task ShutdownWaitsForADefaultWriteInProgressBeforeTearingDown()
    {
        using var fixture = new Fixture();
        fixture.SetBuilt(true);
        fixture.Mixer.SetEnforcedDefaults("wanted-output", null);
        fixture.Write("different");
        fixture.Write("pause-write");
        Task<bool> repair = Task.Run(() => fixture.Mixer.EnforceDefaults());
        try
        {
            Assert.True(SpinWait.SpinUntil(() => fixture.Exists("entered"), TimeSpan.FromSeconds(3)));
            Task stop = Task.Run(() => fixture.Service.StopAsync(CancellationToken.None));
            // The write holds the mixer, so teardown cannot run underneath it.
            Assert.False(SpinWait.SpinUntil(() => stop.IsCompleted, TimeSpan.FromMilliseconds(300)));
            Assert.True(fixture.Mixer.Built);
            fixture.Write("release");
            Assert.True(await repair.WaitAsync(TimeSpan.FromSeconds(3)));
            await stop.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(fixture.Mixer.Built);
            Assert.Equal(["set-default-sink wanted-output"], fixture.Read("writes"));
            Assert.False(fixture.Mixer.EnforceDefaults());
        }
        finally { fixture.Write("release"); await repair.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task ADefaultCheckStillReadingNeitherHoldsTheMixerNorWritesAfterTeardown()
    {
        using var fixture = new Fixture();
        fixture.SetBuilt(true);
        fixture.Mixer.SetEnforcedDefaults("wanted-output", null);
        fixture.Write("pause");
        Task<bool> repair = Task.Run(() => fixture.Mixer.EnforceDefaults());
        try
        {
            Assert.True(SpinWait.SpinUntil(() => fixture.Exists("entered"), TimeSpan.FromSeconds(3)));
            // The read runs without the mixer lock: shutdown tears down while it waits.
            await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(fixture.Mixer.Built);
            fixture.Write("release");
            Assert.False(await repair.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(fixture.Exists("writes"));
        }
        finally { fixture.Write("release"); await repair.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task ADefaultCheckSkipsItsWriteWhenTheChoiceChangedWhileItRead()
    {
        using var fixture = new Fixture();
        fixture.SetBuilt(true);
        fixture.Mixer.SetEnforcedDefaults("wanted-output", null);
        fixture.Write("pause");
        Task<bool> repair = Task.Run(() => fixture.Mixer.EnforceDefaults());
        try
        {
            Assert.True(SpinWait.SpinUntil(() => fixture.Exists("entered"), TimeSpan.FromSeconds(3)));
            fixture.Mixer.SetEnforcedDefaults(null, null);
            fixture.Write("release");
            Assert.False(await repair.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(fixture.Exists("writes"));
        }
        finally { fixture.Write("release"); await repair.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task AStalledAudioServerIsLeftAtTheStopDeadlineAndItsHelperEnded()
    {
        var log = new LineLogger();
        using var fixture = new Fixture(TimeSpan.FromHours(1), log);
        fixture.SetBuilt(true);
        fixture.Own(11, 12, 13, 14, 15, 16);
        fixture.Write("hang-unload");
        Task stop = Task.Run(() => fixture.Service.StopAsync(CancellationToken.None));
        Assert.True(SpinWait.SpinUntil(() => fixture.Exists("entered"), TimeSpan.FromSeconds(5)));
        int helper = int.Parse(fixture.Read("pid")[0]);
        // The deadline passes while the first unload hangs. Six unloads that
        // each waited out their own deadline would take half a minute.
        ((CancellationTokenSource)ServiceField("_stopDeadline").GetValue(fixture.Service)!).Cancel();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(fixture.Service.Built);
        Assert.Equal(["unload-module 16"], fixture.Read("calls").Where(c => c.StartsWith("unload-module", StringComparison.Ordinal)));
        Assert.False(Directory.Exists($"/proc/{helper}"), "The hung pactl outlived the stop.");
        Assert.Contains(log.Lines, line => line.Contains("6 OpenXLR module(s) stay loaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheBudgetCountsFromTheStartOfTheHostStop()
    {
        var log = new LineLogger();
        using var lifetime = new StoppingLifetime();
        using var fixture = new Fixture(TimeSpan.Zero, log, lifetime);
        fixture.SetBuilt(true);
        fixture.Own(21, 22);
        // Earlier services spent the whole budget: the mixer runs no helper.
        lifetime.StopApplication();
        await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(fixture.Service.Built);
        Assert.False(fixture.Exists("calls"));
        Assert.Contains(log.Lines, line => line.Contains("2 OpenXLR module(s) stay loaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAnsweringAudioServerIsTornDownCompletelyAndQuietly()
    {
        var log = new LineLogger();
        using var fixture = new Fixture(TimeSpan.FromHours(1), log);
        fixture.SetBuilt(true);
        fixture.Own(31, 32);
        await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["unload-module 32", "unload-module 31"], fixture.Read("calls").Where(c => c.StartsWith("unload-module", StringComparison.Ordinal)));
        Assert.DoesNotContain(log.Lines, line => line.StartsWith("stop:", StringComparison.Ordinal));
    }

    private sealed class StoppingLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }

    private sealed class LineLogger : ILogger<MixerService>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();
        public IReadOnlyCollection<string> Lines => _lines;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _lines.Enqueue(formatter(state, exception));
    }

    private static FieldInfo ServiceField(string name)
        => typeof(MixerService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-stop-").FullName;
        private readonly string? _oldPath = Environment.GetEnvironmentVariable("PATH");
        private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly DeviceManager _devices;
        internal MixerService Service { get; }
        internal Mixer Mixer { get; }

        internal Fixture(TimeSpan? stopBudget = null, ILogger<MixerService>? log = null, IHostApplicationLifetime? lifetime = null)
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
            ExecutableScript.Write(Path.Combine(_directory, "pw-dump"), "#!/bin/sh\necho '[]'\n");
            ExecutableScript.Write(Path.Combine(_directory, "pactl"), """
                #!/bin/sh
                dir=${0%/*}
                echo "$*" >> "$dir/calls"
                case "$1" in
                  get-default-sink)
                    if [ -e "$dir/pause" ]; then
                      : > "$dir/entered"
                      while [ ! -e "$dir/release" ]; do /bin/sleep .01; done
                      echo other-output
                    elif [ -e "$dir/different" ]; then echo other-output
                    else echo wanted-output
                    fi ;;
                  get-default-source)
                    if [ -e "$dir/different" ]; then echo other-input
                    else echo wanted-input
                    fi ;;
                  set-default-sink|set-default-source)
                    if [ -e "$dir/pause-write" ]; then
                      : > "$dir/entered"
                      while [ ! -e "$dir/release" ]; do /bin/sleep .01; done
                    fi
                    echo "$*" >> "$dir/writes" ;;
                  unload-module)
                    if [ -e "$dir/hang-unload" ]; then
                      echo $$ > "$dir/pid"
                      : > "$dir/entered"
                      while [ ! -e "$dir/release" ]; do /bin/sleep .01; done
                    fi ;;
                  *) exit 1 ;;
                esac
                """);
            Environment.SetEnvironmentVariable("PATH", _directory);
            var config = new ConfigurationBuilder().Build();
            _devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
            Service = new MixerService(log ?? NullLogger<MixerService>.Instance, config, _devices, lifetime)
            {
                StopBudget = stopBudget ?? TimeSpan.FromHours(1),
            };
            Mixer = (Mixer)ServiceField("_mixer").GetValue(Service)!;
            // No physical graph or audio process is needed for these lifetime checks.
            typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Mixer, new MixerConfig { Channels = [], Mixes = [] });
        }

        /// <summary>Modules the adapter believes it loaded, as a build would leave them.</summary>
        internal void Own(params uint[] modules)
        {
            object adapter = ServiceField("_pipeWire").GetValue(Service)!;
            var owned = (List<uint>)typeof(PipeWireAdapter).GetField("_modules", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
            owned.AddRange(modules);
        }

        internal void SetBuilt(bool built) => typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer, built);
        internal void Write(string name) => File.WriteAllText(Path.Combine(_directory, name), "");
        internal bool Exists(string name) => File.Exists(Path.Combine(_directory, name));
        internal string[] Read(string name) => File.ReadAllLines(Path.Combine(_directory, name));
        public void Dispose()
        {
            Write("release");
            Service.Dispose();
            Mixer.Dispose();
            _devices.Dispose();
            Environment.SetEnvironmentVariable("PATH", _oldPath);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
