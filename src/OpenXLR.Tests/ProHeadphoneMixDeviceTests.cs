using OpenXLR.Core.Devices;
using static OpenXLR.Tests.ProDuckingTests;

namespace OpenXLR.Tests;

/// <summary>
/// The Pro's Monitor return on the headphone jacks at the byte level: Wave
/// Link keeps a fader level in the return's cell of the jacks' mix, which
/// leaves the Monitor mix that far down on Linux with the membership bit
/// already set. The return reads as on only at unity, and turning it on puts
/// the cell back to 0 dB without touching the rest of the block.
/// </summary>
public sealed class ProHeadphoneMixDeviceTests
{
    /// <summary>Block 0x0001 of a dual-booting owner's Pro: pair 1 a member of the jacks' mix at -27.5 dB.</summary>
    private const string LeftByWaveLink =
        "C80000C200C9C9C9C900A6C9E80D00AA55550000C4C2C4C9C9C4C9C4A6D6690D00EA555500C4F4C4C4E8C4C4F400C4C4000200EA5555" +
        "00FAFAC2C4C9C9C4C9C4C4D6690900EAFDF7000088000000000000000000000000AA55551E2320230202020200000000000000000000";

    private static Mk2HardwareEqTests.Unit Unit()
    {
        var unit = new Mk2HardwareEqTests.Unit { Bank = 0x0103 };
        unit.Blocks.Clear();
        unit.Blocks[0x0001] = Convert.FromHexString(LeftByWaveLink);
        unit.Blocks[0x0003] = new byte[12];
        unit.Blocks[0x0004] = Convert.FromHexString(ProDuckingDeviceTests.SettingsBlock);
        unit.Blocks[0x0005] = Convert.FromHexString("0000000050000000");
        unit.Blocks[0x0008] = Block8(LowLowSlowBytes, WaveLinkFlags);
        return unit;
    }

    [Fact]
    public void AReturnBelowUnityReadsAsOffAndTurningItOnOpensOnlyItsCell()
    {
        var unit = Unit();
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        byte[] before = unit.Blocks[0x0001];
        Assert.NotEqual(0, before[12] & 0x20);
        Assert.False(pro.ReadState().HpMixMonitorReturn);

        pro.SetHpMixMonitorReturn(true);
        byte[] after = unit.Blocks[0x0001];
        Assert.Equal(0x00, after[5]);
        for (int i = 0; i < after.Length; i++)
            if (i != 5) Assert.Equal(before[i], after[i]);
        Assert.True(pro.ReadState().HpMixMonitorReturn);
    }

    [Fact]
    public void TurningTheReturnOffClearsTheBitAndLeavesTheLevel()
    {
        var unit = Unit();
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        pro.SetHpMixMonitorReturn(true);
        pro.SetHpMixMonitorReturn(false);
        byte[] block = unit.Blocks[0x0001];
        Assert.Equal((0x00, 0xc8), (block[5], block[12]));
        Assert.False(pro.ReadState().HpMixMonitorReturn);
    }
}
