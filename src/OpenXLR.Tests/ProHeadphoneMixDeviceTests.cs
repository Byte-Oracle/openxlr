using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;
using static OpenXLR.Tests.ProDuckingTests;

namespace OpenXLR.Tests;

/// <summary>
/// The Pro's Monitor return on the headphone jacks at the byte level: Wave
/// Link keeps a fader level in the return's cell of the jacks' mix, which
/// leaves the Monitor mix that far down on Linux with the membership bit
/// already set, and can leave the Line In jack a member of the same mix
/// (byte 12 bit 3), which plays it on the jacks beside the Monitor mix that
/// already carries the Line In channel. The return reads as on only at unity
/// with Line In out of the mix, and turning it on puts the cell back to 0 dB
/// and clears that one bit without touching the rest of the block.
/// </summary>
[Collection("xdg-config")]
public sealed class ProHeadphoneMixDeviceTests
{
    /// <summary>Block 0x0001 of a dual-booting owner's Pro: pair 1 a member of the jacks' mix at -27.5 dB.</summary>
    private const string LeftByWaveLink =
        "C80000C200C9C9C9C900A6C9E80D00AA55550000C4C2C4C9C9C4C9C4A6D6690D00EA555500C4F4C4C4E8C4C4F400C4C4000200EA5555" +
        "00FAFAC2C4C9C9C4C9C4C4D6690900EAFDF7000088000000000000000000000000AA55551E2320230202020200000000000000000000";

    /// <summary>
    /// The same unit's block as an owner's archive on 0.2.4 read it: the
    /// return at unity, as OpenXLR leaves it, with Line In still a member of
    /// the jacks' mix (byte 12 `e8`) and Headphones 1 playing it (byte 90 `1e`).
    /// </summary>
    private static string ReturnAtUnity()
    {
        byte[] block = Convert.FromHexString(LeftByWaveLink);
        block[5] = 0x00;
        return Convert.ToHexString(block);
    }

    private static Mk2HardwareEqTests.Unit Unit(string block1 = LeftByWaveLink)
    {
        var unit = new Mk2HardwareEqTests.Unit { Bank = 0x0103 };
        unit.Blocks.Clear();
        unit.Blocks[0x0001] = Convert.FromHexString(block1);
        unit.Blocks[0x0003] = new byte[12];
        unit.Blocks[0x0004] = Convert.FromHexString(ProDuckingDeviceTests.SettingsBlock);
        unit.Blocks[0x0005] = Convert.FromHexString("0000000050000000");
        unit.Blocks[0x0008] = Block8(LowLowSlowBytes, WaveLinkFlags);
        return unit;
    }

    [Fact]
    public void AReturnBelowUnityReadsAsOffAndTurningItOnOpensOnlyItsCellAndTakesLineInOut()
    {
        var unit = Unit();
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        byte[] before = unit.Blocks[0x0001];
        Assert.Equal(0xe8, before[12]);
        Assert.False(pro.ReadState().HpMixMonitorReturn);

        pro.SetHpMixMonitorReturn(true);
        byte[] after = unit.Blocks[0x0001];
        Assert.Equal(0x00, after[5]);
        // Bit 3 alone goes: bit 5 was already set, and the others stay as read.
        Assert.Equal(0xe0, after[12]);
        for (int i = 0; i < after.Length; i++)
            if (i is not (5 or 12)) Assert.Equal(before[i], after[i]);
        Assert.True(pro.ReadState().HpMixMonitorReturn);
    }

    [Fact]
    public void TurningTheReturnOnCommitsTheBlock()
    {
        var unit = Unit(ReturnAtUnity());
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        unit.Calls.Clear();

        pro.SetHpMixMonitorReturn(true);

        Assert.Equal(new[] { (0x41, 0x0001), (0x41, 0x0003) },
            unit.Calls.Where(c => c.Type == 0x41).Select(c => ((int)c.Type, (int)c.Block)));
    }

    [Fact]
    public void AReturnAtUnityWithLineInInTheMixReadsAsOff()
    {
        var unit = Unit(ReturnAtUnity());
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        Assert.Equal((0x00, 0xe8, 0x1e), (unit.Blocks[0x0001][5], unit.Blocks[0x0001][12], unit.Blocks[0x0001][90]));
        Assert.False(pro.ReadState().HpMixMonitorReturn);

        pro.SetHpMixMonitorReturn(true);
        Assert.Equal(0xe0, unit.Blocks[0x0001][12]);
        Assert.True(pro.ReadState().HpMixMonitorReturn);
    }

    [Fact]
    public void TurningTheReturnOffClearsItsBitAndLeavesTheLevelAndLineIn()
    {
        var unit = Unit();
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        pro.SetHpMixMonitorReturn(true);
        pro.SetHpMixMonitorReturn(false);
        byte[] block = unit.Blocks[0x0001];
        Assert.Equal((0x00, 0xc0), (block[5], block[12]));
        Assert.False(pro.ReadState().HpMixMonitorReturn);

        // A unit that still holds Line In in the mix keeps it when the return goes off.
        var held = Unit(ReturnAtUnity());
        using var other = new WaveXlrProDevice(held);
        other.Connect();
        other.SetHpMixMonitorReturn(false);
        Assert.Equal(0xc8, held.Blocks[0x0001][12]);
    }

    [Fact]
    public void TheDaemonTakesLineInOutOfAUnitThatConnectsWithTheReturnAlreadyOn()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-test-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            new DaemonSettings { Submixer = false }.Save();
            var unit = Unit(ReturnAtUnity());
            using var pro = new WaveXlrProDevice(unit);
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [pro]);
            manager.SweepOnce();
            Assert.False(manager.Snapshot().State!.HpMixMonitorReturn);

            // A jack is the monitor output: the return is asserted, which
            // writes the bit out, and the caller restarts the playback stream.
            bool mic = (unit.Blocks[0x0001][13] & 0x02) != 0;
            Assert.True(manager.EnsureHeadphoneMix(monitorReturn: true, micDirect: mic));
            Assert.Equal(0xe0, unit.Blocks[0x0001][12]);
            Assert.True(manager.Snapshot().State!.HpMixMonitorReturn);

            // Later passes find it set and write nothing.
            manager.SweepOnce();
            Assert.False(manager.EnsureHeadphoneMix(monitorReturn: true, micDirect: mic));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
