using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave XLR Pro's analog Line In gain, against a unit modelled as its
/// blocks: block 0x0004 byte 76 holds it in dB, as measured on the desk unit
/// (20 dB there, the centre pair of the ALSA capture volume). A write is one
/// read and one whole-block write with no commit, held to 0 to 40 dB (the
/// unit takes 80 but hisses above about 20 dB on an owner's unit), and
/// leaves every other byte as read, bytes 77 to 79 included. A unit set
/// higher elsewhere reads back its own value. The daemon offers the control
/// on a unit that has the input only, logs each write with what the unit
/// reads back, and a saved state from before the field existed leaves the
/// gain alone.
/// </summary>
[Collection("xdg-config")]
public sealed class ProLineInGainTests
{
    private static WaveXlrProDevice Connected(ProHardwareEqTests.Unit unit)
    {
        var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        return pro;
    }

    [Fact]
    public void TheLineInGainIsByte76OfTheSettingsBlock()
    {
        var unit = new ProHardwareEqTests.Unit();
        using WaveXlrProDevice pro = Connected(unit);
        Assert.True(pro.Capabilities.LineInput);
        Assert.Equal(0x14, unit.Blocks[0x0004][76]);
        Assert.Equal(20, pro.ReadState().LineInGainDb);
        Assert.Equal(40, pro.Capabilities.LineInGainMaxDb);
        Assert.Equal(80, pro.Capabilities.GainMaxDb);
    }

    [Fact]
    public void AUnitSetAboveTheCapReadsBackItsOwnGain()
    {
        var unit = new ProHardwareEqTests.Unit();
        unit.Blocks[0x0004][76] = 52;
        using WaveXlrProDevice pro = Connected(unit);
        Assert.Equal(52, pro.ReadState().LineInGainDb);
    }

    [Fact]
    public void AWriteChangesByte76AloneWithNoCommit()
    {
        var unit = new ProHardwareEqTests.Unit();
        using WaveXlrProDevice pro = Connected(unit);
        // The bytes after the gain hold values a write must hand back.
        unit.Blocks[0x0004][77] = 0x04;
        unit.Blocks[0x0004][78] = 0x02;
        unit.Blocks[0x0004][79] = 0x51;
        byte[] before = (byte[])unit.Blocks[0x0004].Clone();
        unit.Calls.Clear();

        Assert.Equal(33, pro.SetLineInGainDb(33));

        byte[] written = Assert.Single(unit.SettingsWrites);
        Assert.Empty(unit.Commits);
        Assert.Equal(80, written.Length);
        for (int i = 0; i < written.Length; i++)
            Assert.True(i == 76 ? written[i] == 33 : written[i] == before[i], $"byte {i}: {written[i]:x2}, read {before[i]:x2}");
        // One read of the block, then the write, nothing else.
        Assert.Equal([(byte)0xC1, (byte)0x41], unit.Calls.Select(c => c.Type));
        Assert.Equal(33, pro.ReadState().LineInGainDb);
    }

    [Theory]
    [InlineData(95, 40)]
    [InlineData(80, 40)]
    [InlineData(41, 40)]
    [InlineData(40, 40)]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    public void AWriteIsHeldToTheGainRange(int asked, int stored)
    {
        var unit = new ProHardwareEqTests.Unit();
        using WaveXlrProDevice pro = Connected(unit);
        Assert.Equal(stored, pro.SetLineInGainDb(asked));
        Assert.Equal(stored, unit.Blocks[0x0004][76]);
    }

    [Fact]
    public void OnlyAUnitWithTheInputOffersTheControl()
    {
        Assert.Null(DeviceManager.UnsupportedControlReason(new DeviceCapabilities { LineInput = true }, ControlNames.LineInGain));
        Assert.Equal("control 'lineInGain' is not supported by the active device",
            DeviceManager.UnsupportedControlReason(new DeviceCapabilities { Gain = true, AuxInput = true, XlrInputs = 2 }, ControlNames.LineInGain));
    }

    [Fact]
    public void TheDaemonWritesTheGainAndLogsWhatTheUnitReadsBack()
    {
        WithConfigDir(() =>
        {
            var unit = new ProHardwareEqTests.Unit();
            var log = new Lines();
            using var pro = new WaveXlrProDevice(unit);
            using var manager = new DeviceManager(log, new ConfigurationBuilder().Build(), () => [pro]);
            manager.SweepOnce();
            Assert.True(manager.Snapshot().Capabilities!.LineInput);
            Assert.Equal(20, manager.Snapshot().State!.LineInGainDb);
            unit.Calls.Clear();

            using var value = JsonDocument.Parse("37");
            Assert.Null(manager.Apply(ControlNames.LineInGain, value.RootElement));

            Assert.Equal(37, unit.Blocks[0x0004][76]);
            Assert.Equal(37, manager.Snapshot().State!.LineInGainDb);
            Assert.Contains(log.Messages, m => m.Contains("Line In gain written as 37 dB", StringComparison.Ordinal)
                && m.Contains("read back as 37 dB", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void ASavedStateWithoutTheGainLeavesItAlone()
    {
        WithConfigDir(() =>
        {
            var unit = new ProHardwareEqTests.Unit();
            using var pro = new WaveXlrProDevice(unit);
            using var manager = new DeviceManager(new Lines(), new ConfigurationBuilder().Build(), () => [pro]);
            manager.SweepOnce();
            DeviceState live = manager.Snapshot().State!;
            unit.Calls.Clear();

            Assert.Null(manager.ApplyProfile(live with { LineInGainDb = null }));
            Assert.Empty(unit.SettingsWrites);
            Assert.Equal(0x14, unit.Blocks[0x0004][76]);

            Assert.Null(manager.ApplyProfile(live with { LineInGainDb = 32 }));
            Assert.Equal(32, Assert.Single(unit.SettingsWrites)[76]);
        });
    }

    [Fact]
    public void ASavedGainAboveTheCapIsWrittenAsTheCapOnce()
    {
        WithConfigDir(() =>
        {
            var unit = new ProHardwareEqTests.Unit();
            using var pro = new WaveXlrProDevice(unit);
            using var manager = new DeviceManager(new Lines(), new ConfigurationBuilder().Build(), () => [pro]);
            manager.SweepOnce();
            Assert.Equal(40, manager.Snapshot().Capabilities!.LineInGainMaxDb);
            DeviceState live = manager.Snapshot().State!;
            unit.Calls.Clear();

            Assert.Null(manager.ApplyProfile(live with { LineInGainDb = 52 }));
            Assert.Equal(40, Assert.Single(unit.SettingsWrites)[76]);

            // The unit now reads the cap, so the same saved state writes nothing again.
            Assert.Null(manager.ApplyProfile(manager.Snapshot().State! with { LineInGainDb = 52 }));
            Assert.Single(unit.SettingsWrites);
        });
    }

    [Fact]
    public void ASnapshotSavedBeforeTheFieldExistedReadsAsNoGain()
    {
        DeviceState old = JsonSerializer.Deserialize<DeviceState>("""{"gainDb":30,"auxLevelDb":-30}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(old.LineInGainDb);
    }

    private static void WithConfigDir(Action body)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-test-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            new DaemonSettings { Submixer = false }.Save();
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>The daemon's log lines, formatted.</summary>
    private sealed class Lines : ILogger<DeviceManager>
    {
        public readonly List<string> Messages = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
        }
    }
}
