using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;
using static OpenXLR.Tests.ProDuckingTests;

namespace OpenXLR.Tests;

/// <summary>
/// Ducking through the Pro's driver and the device manager, against a unit
/// modelled as its blocks: both blocks read fresh and written only when
/// they change, 0x0008 first, on the Pro's bank and with no commit; the
/// Pro's existing block 0x0001 writes carrying the ducking fields through;
/// the saved choice written on every connect, which clears what Wave Link
/// left while OpenXLR's is off; a change by another program written back no
/// sooner than the spacing and said once; and nothing on a model without it.
/// </summary>
[Collection("xdg-config")]
public sealed class ProDuckingDeviceTests
{
    /// <summary>Block 0x0004 of the desk's Pro as capture 16 read it.</summary>
    private const string SettingsBlock =
        "34d800d4200a00c8001832f4040a00640006" + "01003c00f6030de300ff05f45c03f20507fd27e8" +
        "1e0004d4200a00c800180af4040a00640006" + "01003c00f6030de300ff05f45c03f20507fd27e8" + "14000200";

    /// <summary>A Pro as Wave Link left the desk's unit: ducking on pairs 1, 5, 6 and 7, Low, Low, Slow.</summary>
    private static Mk2HardwareEqTests.Unit LeftByWaveLink()
    {
        var unit = new Mk2HardwareEqTests.Unit { Bank = 0x0103 };
        unit.Blocks.Clear();
        unit.Blocks[0x0001] = Block1(0x5d, 0xfd);
        unit.Blocks[0x0003] = new byte[12];
        unit.Blocks[0x0004] = Convert.FromHexString(SettingsBlock);
        unit.Blocks[0x0005] = Convert.FromHexString("0000000050000000");
        unit.Blocks[0x0008] = Block8(LowLowSlowBytes, WaveLinkFlags);
        return unit;
    }

    private static IEnumerable<ushort> Writes(Mk2HardwareEqTests.Unit unit)
        => unit.Calls.Where(c => c.Type == 0x41).Select(c => c.Block);

    private static byte[] Fields(byte[] block1)
        => [.. ProDucking.MixFieldOffsets.SelectMany(at => block1[at..(at + 2)])];

    [Fact]
    public void BothBlocksAreWrittenOnTheProsBankWithoutACommitAndOnlyWhenTheyChange()
    {
        var unit = LeftByWaveLink();
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        Assert.True(pro.Capabilities.Ducking);
        unit.Calls.Clear();

        DuckingWrite write = pro.SetDucking(new DuckingSettings { Enabled = true, Targets = ["monitor"] });
        Assert.True(write.Wrote8 && write.Wrote1);
        // Medium presets in every entry; pairs 5, 6 and 7 (entries 9 to 11, flags at 78, 86, 94) let go.
        Assert.StartsWith("0 fd->f4, 1 06->0c, 2 64->50, 4 d0->b0, 5 07->04, 8 fd->f4", write.Change8);
        Assert.Contains("78 01->00, ", write.Change8);
        Assert.EndsWith("86 01->00, 88 fd->f4, 89 06->0c, 90 64->50, 92 d0->b0, 93 07->04, 94 01->00", write.Change8);
        Assert.Equal(new ushort[] { 0x0008, 0x0001 }, Writes(unit));
        Assert.All(unit.Calls, c => Assert.Equal(0x0103, c.Bank));
        Assert.Equal(new byte[] { 0x5d, 0x55, 0x5d, 0x55, 0x5d, 0x55 }, Fields(unit.Blocks[0x0001]));
        byte[] flags = (byte[])IdleFlags.Clone();
        flags[5] = 0x01;
        Assert.Equal(Block8(MediumBytes, flags), unit.Blocks[0x0008]);
        Assert.Equal([1], pro.ReadDucking()!.Pairs);

        unit.Calls.Clear();
        Assert.False(pro.SetDucking(new DuckingSettings { Enabled = true, Targets = ["monitor"] }).Wrote);
        Assert.Empty(Writes(unit));
        Assert.Equal(2, unit.Calls.Count);   // the two reads
    }

    [Fact]
    public void TheProsOtherBlockOneWritesCarryTheDuckingFieldsThrough()
    {
        var unit = LeftByWaveLink();
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        pro.SetDucking(new DuckingSettings { Enabled = true, Targets = ["monitor", "aux"] });
        byte[] ducked = Fields(unit.Blocks[0x0001]);
        Assert.Equal(new byte[] { 0x5d, 0x5d, 0x5d, 0x5d, 0x5d, 0x5d }, ducked);

        // Every writer of block 0x0001 the Pro has: crossfade, the four
        // selectors with the aux cell, and the headphone mix membership.
        Action[] writers =
        [
            () => pro.SetCrossfade(150), () => pro.SetOutHp1(true), () => pro.SetOutHp2(true),
            () => pro.SetOutLineOut(true), () => pro.SetOutUsbAux(true), () => pro.SetOutUsbAux(false),
            () => pro.SetHpMixMonitorReturn(false), () => pro.SetHpMixMicDirect(false),
        ];
        foreach (Action write in writers)
        {
            write();
            Assert.Equal(ducked, Fields(unit.Blocks[0x0001]));
        }
        byte[] routed = unit.Blocks[0x0001];
        Assert.Equal((150, 0x1e, 0x1e, 0x1e), (routed[0], routed[90], routed[91], routed[93]));

        // And ducking off keeps what those writers set.
        pro.SetDucking(DuckingSettings.Default);
        byte[] after = unit.Blocks[0x0001];
        for (int i = 0; i < after.Length; i++)
            if (!ProDucking.MixFieldOffsets.Any(at => i == at || i == at + 1)) Assert.Equal(routed[i], after[i]);
        Assert.Equal(new byte[] { 0x55, 0x55, 0x55, 0x55, 0x55, 0x55 }, Fields(after));
    }

    [Fact]
    public void ThePollReadsTheDuckingAndAUnitThatRefusesItsBlockStillAnswers()
    {
        var unit = LeftByWaveLink();
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        Assert.Null(pro.ReadDucking());
        DeviceState state = pro.ReadState();
        Assert.Equal(52, state.GainDb);
        DuckingReadback ducking = pro.ReadDucking()!;
        Assert.Equal([1, 5, 6, 7], ducking.Pairs);
        Assert.Equal(("low", "low", "slow"), (ducking.Sensitivity, ducking.Amount, ducking.Speed));

        unit.Blocks.Remove(0x0008);
        Assert.Equal(state, pro.ReadState());
        Assert.Null(pro.ReadDucking());
    }

    // --- the device manager ---

    private static T WithConfigDir<T>(Func<T> body)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-ducking-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try { return body(); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static DeviceManager Manager(IAudioDevice unit, ManualTime time, DeviceEqWriterTests.Lines log)
    {
        var manager = new DeviceManager(log, new ConfigurationBuilder().Build(), () => [unit], time: time);
        manager.SweepOnce();
        Assert.True(unit.Connected);
        return manager;
    }

    private static DuckingChange Change(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Null(CommandValidation.CheckDucking(new Command { Cmd = "setDucking", Value = doc.RootElement.Clone() }, out DuckingChange? change));
        return change!;
    }

    [Fact]
    public void AConnectWithDuckingOffClearsWhatWaveLinkLeft() => WithConfigDir(() =>
    {
        var unit = LeftByWaveLink();
        var log = new DeviceEqWriterTests.Lines();
        using DeviceManager manager = Manager(new WaveXlrProDevice(unit), new ManualTime(), log);

        Assert.Equal(Block8(LowLowSlowBytes, IdleFlags), unit.Blocks[0x0008]);
        Assert.Equal(new byte[] { 0x55, 0x55, 0x55, 0x55, 0x55, 0x55 }, Fields(unit.Blocks[0x0001]));
        Assert.Contains(log.Written, line => line.StartsWith("ducking (connect) on Elgato Wave XLR Pro: off. Block 0x0008 written: ", StringComparison.Ordinal)
            && line.Contains("Block 0x0001 written: 16 5d->55, 17 fd->55, 34 5d->55", StringComparison.Ordinal));

        StateMessage state = manager.Snapshot();
        Assert.True(state.Capabilities!.Ducking);
        DuckingState ducking = state.Ducking!;
        Assert.False(ducking.Enabled);
        Assert.Equal(("xlr1", "medium", "device", "device"), (ducking.Key, ducking.Sensitivity, ducking.Processing, ducking.Backend));
        Assert.Equal(["monitor"], ducking.Targets);
        DuckingDevice device = ducking.Device!;
        Assert.Equal(("0fd9:00b4", "Wave XLR Pro", true, null, null), (device.Unit, device.Name, device.Ready, device.Reason, device.Note));
        Assert.True(device.Held);
        Assert.Empty(device.Readback!.Pairs);
        Assert.Equal([4, 7, 8], device.Readback.Entries);
        // Off does not write presets, so the unit still holds Wave Link's.
        Assert.Equal("low", device.Readback.Sensitivity);
        return 0;
    });

    [Fact]
    public void TheChoiceIsSavedAndEveryConnectRestoresIt() => WithConfigDir(() =>
    {
        var unit = LeftByWaveLink();
        var log = new DeviceEqWriterTests.Lines();
        var time = new ManualTime();
        using (DeviceManager manager = Manager(new WaveXlrProDevice(unit), time, log))
        {
            Assert.Null(manager.SetDucking(Change("""{"enabled":true,"targets":["monitor","aux"],"sensitivity":"high","amount":"high","speed":"fast"}""")));
            Assert.Null(manager.SetDucking(Change("""{"amount":"low"}""")));
            Assert.True(File.Exists(Path.Combine(OpenXlrPaths.ConfigDir, "devices", "0fd9-00b4", "ducking.json")));
            DuckingState state = manager.Snapshot().Ducking!;
            Assert.True(state.Enabled && state.Device!.Held);
            DuckingReadback read = state.Device.Readback!;
            Assert.Equal(("high", "low", "fast"), (read.Sensitivity, read.Amount, read.Speed));
            Assert.Equal([1, 5], read.Pairs);
            Assert.Contains(log.Written, line => line.StartsWith("ducking (setDucking) on Elgato Wave XLR Pro: on, key xlr1, targets monitor aux, sensitivity high, amount low, speed fast.", StringComparison.Ordinal));
        }

        // Wave Link had the unit meanwhile and put its own back.
        unit.Blocks[0x0001] = Block1(0x5d, 0xfd);
        unit.Blocks[0x0008] = Block8(LowLowSlowBytes, WaveLinkFlags);
        using (DeviceManager manager = Manager(new WaveXlrProDevice(unit), time, log))
        {
            DuckingReadback held = manager.Snapshot().Ducking!.Device!.Readback!;
            Assert.Equal([1, 5], held.Pairs);
            Assert.Equal(("high", "low", "fast"), (held.Sensitivity, held.Amount, held.Speed));
            byte[] flags = (byte[])IdleFlags.Clone();
            flags[5] = flags[9] = 0x01;
            Assert.Equal(Block8([0xe8, 0x06, 0x1e, 0x00, 0xc8, 0x00], flags), unit.Blocks[0x0008]);
        }
        return 0;
    });

    [Fact]
    public void AChangeByAnotherProgramIsWrittenBackNoSoonerThanTheSpacingAndSaidOnce() => WithConfigDir(() =>
    {
        var unit = LeftByWaveLink();
        var log = new DeviceEqWriterTests.Lines();
        var time = new ManualTime();
        using DeviceManager manager = Manager(new WaveXlrProDevice(unit), time, log);
        byte[] off8 = unit.Blocks[0x0008], off1 = unit.Blocks[0x0001];

        void Meddle()
        {
            unit.Blocks[0x0001] = Block1(0x5d, 0xfd);
            unit.Blocks[0x0008] = Block8(LowLowSlowBytes, WaveLinkFlags);
        }
        Meddle();
        unit.Calls.Clear();
        manager.SweepOnce();
        Assert.Equal(off8, unit.Blocks[0x0008]);
        Assert.Equal(off1, unit.Blocks[0x0001]);
        Assert.Equal(new ushort[] { 0x0008, 0x0001 }, Writes(unit));

        Meddle();
        unit.Calls.Clear();
        for (int i = 0; i < 49; i++) { time.Advance(TimeSpan.FromMilliseconds(100)); manager.SweepOnce(); }
        Assert.Empty(Writes(unit));
        Assert.False(manager.Snapshot().Ducking!.Device!.Held);
        time.Advance(TimeSpan.FromMilliseconds(100));
        manager.SweepOnce();
        Assert.Equal(new ushort[] { 0x0008, 0x0001 }, Writes(unit));
        Assert.Single(log.Written, line => line.Contains("no longer matches OpenXLR's", StringComparison.Ordinal));
        Assert.Equal(3, log.Written.Count(line => line.StartsWith("ducking (", StringComparison.Ordinal)));
        Assert.True(manager.Snapshot().Ducking!.Device!.Held);
        return 0;
    });

    [Fact]
    public void ASetupTheUnitCannotRunIsWrittenAsOffAndSaysWhy() => WithConfigDir(() =>
    {
        var unit = LeftByWaveLink();
        var log = new DeviceEqWriterTests.Lines();
        using DeviceManager manager = Manager(new WaveXlrProDevice(unit), new ManualTime(), log);
        Assert.Null(manager.SetDucking(Change("""{"enabled":true,"targets":["monitor"]}""")));
        Assert.Equal([1], manager.Snapshot().Ducking!.Device!.Readback!.Pairs);

        // Keyed by XLR 2: saved as asked, but the unit is given ducking off.
        Assert.Null(manager.SetDucking(Change("""{"key":"xlr2"}""")));
        DuckingState state = manager.Snapshot().Ducking!;
        Assert.True(state.Enabled);
        Assert.Equal("xlr2", state.Key);
        Assert.Null(state.Backend);
        Assert.False(state.Device!.Ready);
        Assert.Equal("keyNotSupported", state.Device.Reason);
        Assert.Equal("The Wave XLR Pro ducks only with XLR 1 as the key, so it does not run a setup keyed by XLR 2.", state.Device.Note);
        Assert.True(state.Device.Held);
        Assert.Empty(state.Device.Readback!.Pairs);
        Assert.Equal(new byte[] { 0x55, 0x55, 0x55, 0x55, 0x55, 0x55 }, Fields(unit.Blocks[0x0001]));
        Assert.Contains(log.Written, line => line.Contains("speed medium, written as off (keyNotSupported).", StringComparison.Ordinal));
        Assert.Equal("xlr2", DeviceStateStore.LoadDucking("0fd9:00b4")!.Key);

        // Back to XLR 1: the unit ducks again.
        Assert.Null(manager.SetDucking(Change("""{"key":"xlr1"}""")));
        Assert.Equal(("device", true), (manager.Snapshot().Ducking!.Backend, manager.Snapshot().Ducking!.Device!.Ready));
        Assert.Equal([1], manager.Snapshot().Ducking!.Device!.Readback!.Pairs);
        return 0;
    });

    [Fact]
    public void AModelWithoutDuckingHasNoneAndRefusesTheCommand() => WithConfigDir(() =>
    {
        var usb = new Mk2HardwareEqTests.Unit();
        using DeviceManager manager = Manager(new XlrDockMk2Device(usb), new ManualTime(), new DeviceEqWriterTests.Lines());
        StateMessage state = manager.Snapshot();
        Assert.False(state.Capabilities!.Ducking);
        Assert.Null(state.Ducking);
        Assert.Equal("setDucking: the active device has no hardware ducking", manager.SetDucking(Change("""{"enabled":true}""")));
        Assert.DoesNotContain(usb.Calls, c => c.Block == 0x0008);
        Assert.False(Directory.Exists(Path.Combine(OpenXlrPaths.ConfigDir, "devices", "0fd9-00c7")));
        return 0;
    });

    [Fact]
    public void ASavedFileTheUnitCannotTakeIsDuckingOff() => WithConfigDir(() =>
    {
        string dir = Path.Combine(OpenXlrPaths.ConfigDir, "devices", "0fd9-00b4");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ducking.json"), """{"enabled":true,"key":"xlr3","targets":["monitor"]}""");
        Assert.Null(DeviceStateStore.LoadDucking("0fd9:00b4"));
        File.WriteAllText(Path.Combine(dir, "ducking.json"), """{"enabled":true,"processing":"software"}""");
        Assert.Null(DeviceStateStore.LoadDucking("0fd9:00b4"));
        File.WriteAllText(Path.Combine(dir, "ducking.json"), "not json");
        Assert.Null(DeviceStateStore.LoadDucking("0fd9:00b4"));
        var saved = new DuckingSettings { Enabled = true, Targets = ["aux"], Speed = "slow" };
        DeviceStateStore.SaveDucking("0fd9:00b4", saved);
        Assert.Equal(saved, DeviceStateStore.LoadDucking("0fd9:00b4"));
        return 0;
    });

    // --- the command ---

    [Theory]
    [InlineData("""{"enabled":true}""", true, null, null, null)]
    [InlineData("""{"targets":[]}""", null, "", null, null)]
    [InlineData("""{"targets":["aux","monitor"],"speed":"fast"}""", null, "monitor aux", null, "fast")]
    [InlineData("""{"key":"xlr1","sensitivity":"low","amount":"high"}""", null, null, "low", null)]
    [InlineData("""{"key":"xlr2"}""", null, null, null, null)]
    public void AWellFormedSetDuckingNamesOnlyWhatItCarries(string value, bool? enabled, string? targets, string? sensitivity, string? speed)
    {
        DuckingChange change = Change(value);
        Assert.Equal(enabled, change.Enabled);
        Assert.Equal(targets, change.Targets is null ? null : string.Join(" ", ProDucking.Ordered(change.Targets)));
        Assert.Equal(sensitivity, change.Sensitivity);
        Assert.Equal(speed, change.Speed);
    }

    [Theory]
    [InlineData("true", "value must be an object")]
    [InlineData("{}", "value needs at least one of")]
    [InlineData("""{"enabled":"yes"}""", "enabled must be a boolean")]
    [InlineData("""{"key":"xlr3"}""", "key must be xlr1 or xlr2")]
    [InlineData("""{"processing":"device"}""", "unknown field 'processing'")]
    [InlineData("""{"targets":"monitor"}""", "targets must be a list")]
    [InlineData("""{"targets":["monitor","monitor"]}""", "targets must be distinct")]
    [InlineData("""{"targets":["game"]}""", "targets must be distinct entries of monitor and aux")]
    [InlineData("""{"targets":["monitor","aux","monitor"]}""", "targets must be a list")]
    [InlineData("""{"sensitivity":"medium","amount":"slow"}""", "amount must be low, medium or high")]
    [InlineData("""{"speed":"high"}""", "speed must be slow, medium or fast")]
    [InlineData("""{"sensitivity":12}""", "sensitivity must be low, medium or high")]
    [InlineData("""{"enable":true}""", "unknown field 'enable'")]
    public void AMalformedSetDuckingIsRefusedBeforeTheDevice(string value, string error)
    {
        using JsonDocument doc = JsonDocument.Parse(value);
        var cmd = new Command { Cmd = "setDucking", Value = doc.RootElement.Clone() };
        Assert.StartsWith("setDucking: " + error, CommandValidation.CheckDucking(cmd, out DuckingChange? change));
        Assert.Null(change);
        // The general check knows the command too.
        Assert.StartsWith("setDucking: ", CommandValidation.Check(cmd, null!, _ => null));
    }

    [Theory]
    [InlineData("\"device\"", null)]
    [InlineData("\"software\"", "setDuckingProcessing: ducking does not run in software yet; the unit runs it")]
    [InlineData("\"unit\"", "setDuckingProcessing: value must be 'device' or 'software'")]
    [InlineData("true", "setDuckingProcessing: value must be 'device' or 'software'")]
    public void WhereDuckingRunsTakesTheDeviceAloneForNow(string value, string? error)
    {
        using JsonDocument doc = JsonDocument.Parse(value);
        var cmd = new Command { Cmd = "setDuckingProcessing", Value = doc.RootElement.Clone() };
        Assert.Equal(error, CommandValidation.CheckDuckingProcessing(cmd));
        Assert.Equal(error, CommandValidation.Check(cmd, null!, _ => null));
    }
}
