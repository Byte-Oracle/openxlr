using OpenXLR.Core.Devices;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave XLR Pro's ducking bytes, against the blocks the Windows captures
/// of 2026-10-21 recorded (24 to 31): every preset in all twelve entries of
/// block 0x0008, the ducked flag set and cleared per entry with bit 1 and
/// the other unknown bits kept, the high bit of each pair's 2-bit field in
/// all three mix sections of block 0x0001 with the low bits and every other
/// byte of that block kept, and the decode of what Wave Link leaves behind.
/// </summary>
public sealed class ProDuckingTests
{
    /// <summary>Byte 6 of each entry in Wave Link's state with no key (capture 24, before the write).</summary>
    internal static readonly byte[] IdleFlags = [0x00, 0x02, 0x00, 0x00, 0x03, 0x00, 0x00, 0x03, 0x03, 0x00, 0x00, 0x00];

    /// <summary>Byte 6 after Wave Link made XLR 1 the key for Browser, Music, Game and System (pairs 1, 5, 6, 7).</summary>
    internal static readonly byte[] WaveLinkFlags = [0x00, 0x02, 0x00, 0x00, 0x03, 0x01, 0x00, 0x03, 0x03, 0x01, 0x01, 0x01];

    internal static byte[] Block8(byte[] preset, byte[] flags)
    {
        var block = new byte[ProDucking.Length];
        for (int e = 0; e < ProDucking.EntryCount; e++)
        {
            preset.CopyTo(block, e * 8);
            block[e * 8 + 6] = flags[e];
        }
        return block;
    }

    internal static readonly byte[] MediumBytes = [0xf4, 0x0c, 0x50, 0x00, 0xb0, 0x04];
    internal static readonly byte[] LowLowSlowBytes = [0xfd, 0x06, 0x64, 0x00, 0xd0, 0x07];
    internal static readonly byte[] HighHighFastBytes = [0xe8, 0x18, 0x1e, 0x00, 0xc8, 0x00];

    /// <summary>
    /// A block 0x0001 with the bytes the protocol notes name: crossfade at 0,
    /// the head membership bytes 12 and 13, the aa that opens each of the
    /// five sections at 15, 33, 51, 69 and 87 with 55 55 after it, the aux
    /// cells at 45, 48 and 49 and the output selectors at 90 to 93. Every
    /// other byte holds a pattern, so a write that touches it shows.
    /// </summary>
    internal static byte[] Block1(byte b16 = 0x55, byte b17 = 0x55)
    {
        var block = new byte[ProDucking.MixBlockLength];
        for (int i = 0; i < block.Length; i++) block[i] = (byte)(0x80 + i * 7);
        block[0] = 0x64;
        block[12] = 0x24;
        block[13] = 0x0e;
        foreach (int at in new[] { 15, 33, 51, 69, 87 })
        {
            block[at] = 0xaa;
            block[at + 1] = 0x55;
            block[at + 2] = 0x55;
        }
        foreach (int at in ProDucking.MixFieldOffsets)
        {
            block[at] = b16;
            block[at + 1] = b17;
        }
        block[45] = 0x00;
        block[48] = 0x01;
        block[49] = 0x02;
        block[90] = 0x23; block[91] = 0x23; block[92] = 0x20; block[93] = 0x23;
        return block;
    }

    private static DuckingSettings On(params string[] targets) => new() { Enabled = true, Targets = targets };

    private static void AssertOnlyMixFieldsChanged(byte[] before, byte[] after)
    {
        for (int i = 0; i < before.Length; i++)
            if (!ProDucking.MixFieldOffsets.Any(at => i == at || i == at + 1))
                Assert.True(before[i] == after[i], $"block 0x0001 byte {i} changed from {before[i]:x2} to {after[i]:x2}");
    }

    [Fact]
    public void TheMonitorAndAuxPairsAreThoseTheGraphStreamsTo()
    {
        // PipeWireAdapter routes "#usbaux" to pair 5 and the jacks to pair 1
        // through these constants; ducking names the same pairs.
        Assert.Equal(1, ProReturnPairs.Monitor);
        Assert.Equal(5, ProReturnPairs.Aux);
        Assert.Equal(ProReturnPairs.Monitor, ProDucking.PairOf("monitor"));
        Assert.Equal(ProReturnPairs.Aux, ProDucking.PairOf("aux"));
    }

    [Fact]
    public void WaveLinksDuckingDecodesToItsFourPairs()
    {
        byte[] b8 = Block8(MediumBytes, WaveLinkFlags);
        byte[] b1 = Block1(0x5d, 0xfd);   // capture 24: 55 55 -> 5d fd in each section
        DuckingReadback unit = ProDucking.Decode(b8, b1);
        Assert.Equal(("medium", "medium", "medium"), (unit.Sensitivity, unit.Amount, unit.Speed));
        Assert.Equal([1, 5, 6, 7], unit.Pairs);
        Assert.Equal([4, 5, 7, 8, 9, 10, 11], unit.Entries);

        DuckingReadback left = ProDucking.Decode(Block8(LowLowSlowBytes, WaveLinkFlags), b1);
        Assert.Equal(("low", "low", "slow"), (left.Sensitivity, left.Amount, left.Speed));
        DuckingReadback high = ProDucking.Decode(Block8(HighHighFastBytes, IdleFlags), Block1());
        Assert.Equal(("high", "high", "fast"), (high.Sensitivity, high.Amount, high.Speed));
        Assert.Empty(high.Pairs);
    }

    [Fact]
    public void OffClearsWhatWaveLinkLeftAndKeepsTheUnknownEntries()
    {
        // The unit as Wave Link left it on the desk: Low, Low, Slow on pairs 1, 5, 6 and 7.
        byte[] b8 = Block8(LowLowSlowBytes, WaveLinkFlags);
        byte[] b1 = Block1(0x5d, 0xfd);
        byte[] before1 = (byte[])b1.Clone();
        ProDucking.Apply(b8, b1, DuckingSettings.Default);

        // Wave Link's own state with no key: the 02 and 03 entries stay, the
        // presets are not written while ducking is off.
        Assert.Equal(Block8(LowLowSlowBytes, IdleFlags), b8);
        foreach (int at in ProDucking.MixFieldOffsets) Assert.Equal(new byte[] { 0x55, 0x55 }, b1[at..(at + 2)]);
        AssertOnlyMixFieldsChanged(before1, b1);
        DuckingReadback unit = ProDucking.Decode(b8, b1);
        Assert.Empty(unit.Pairs);
        Assert.Equal([4, 7, 8], unit.Entries);
        Assert.True(ProDucking.Holds(unit, DuckingSettings.Default));
    }

    [Theory]
    [InlineData("monitor", 0x5d, 0x55, 5)]
    [InlineData("aux", 0x55, 0x5d, 9)]
    public void OneTargetDucksItsPairInEveryMixSection(string target, byte b16, byte b17, int entry)
    {
        byte[] b8 = Block8(MediumBytes, IdleFlags);
        byte[] b1 = Block1();
        byte[] before1 = (byte[])b1.Clone();
        ProDucking.Apply(b8, b1, On(target));

        byte[] flags = (byte[])IdleFlags.Clone();
        flags[entry] |= 0x01;
        Assert.Equal(Block8(MediumBytes, flags), b8);
        foreach (int at in ProDucking.MixFieldOffsets) Assert.Equal(new[] { b16, b17 }, b1[at..(at + 2)]);
        // The fourth and fifth sections, and every other byte, stay.
        AssertOnlyMixFieldsChanged(before1, b1);
        Assert.Equal([ProDucking.PairOf(target)], ProDucking.Decode(b8, b1).Pairs);
    }

    [Fact]
    public void BothTargetsDuckPairsOneAndFive()
    {
        byte[] b8 = Block8(MediumBytes, IdleFlags);
        byte[] b1 = Block1();
        ProDucking.Apply(b8, b1, On("monitor", "aux"));
        foreach (int at in ProDucking.MixFieldOffsets) Assert.Equal(new byte[] { 0x5d, 0x5d }, b1[at..(at + 2)]);
        Assert.Equal([4, 5, 7, 8, 9], ProDucking.Decode(b8, b1).Entries);

        // And off again: back to the idle state byte for byte.
        ProDucking.Apply(b8, b1, On());
        Assert.Equal(Block8(MediumBytes, IdleFlags), b8);
        Assert.Equal(Block1(), b1);
    }

    public static TheoryData<string, string, string> Presets()
    {
        var data = new TheoryData<string, string, string>();
        foreach (string s in ProDucking.Sensitivities)
            foreach (string a in ProDucking.Amounts)
                foreach (string v in ProDucking.Speeds)
                    data.Add(s, a, v);
        return data;
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void EveryPresetGoesToAllTwelveEntriesAsWaveLinkWritesIt(string sensitivity, string amount, string speed)
    {
        // The table from captures 25 to 31: each field moved alone, in every entry.
        byte threshold = sensitivity switch { "low" => 0xfd, "medium" => 0xf4, _ => 0xe8 };
        byte depth = amount switch { "low" => 0x06, "medium" => 0x0c, _ => 0x18 };
        byte[] times = speed switch
        {
            "slow" => [0x64, 0x00, 0xd0, 0x07],
            "medium" => [0x50, 0x00, 0xb0, 0x04],
            _ => [0x1e, 0x00, 0xc8, 0x00],
        };
        var s = new DuckingSettings { Enabled = true, Sensitivity = sensitivity, Amount = amount, Speed = speed };
        byte[] b8 = Block8(LowLowSlowBytes, IdleFlags);
        for (int e = 0; e < 12; e++) b8[e * 8 + 7] = (byte)(0x40 + e);   // byte 7 is not ours either
        byte[] b1 = Block1();
        ProDucking.Apply(b8, b1, s);
        for (int e = 0; e < 12; e++)
        {
            Assert.Equal(new[] { threshold, depth, times[0], times[1], times[2], times[3] }, b8[(e * 8)..(e * 8 + 6)]);
            Assert.Equal((byte)(0x40 + e), b8[e * 8 + 7]);
        }
        Assert.Equal(ProDucking.PresetBytes(s), b8[..6]);
        DuckingReadback unit = ProDucking.Decode(b8, b1);
        Assert.Equal((sensitivity, amount, speed), (unit.Sensitivity, unit.Amount, unit.Speed));
        Assert.True(ProDucking.Holds(unit, s));
    }

    [Fact]
    public void UnknownBitsAreKept()
    {
        // Flag bits 2 to 7 on every entry, bit 1 on some; fields whose low bit is clear.
        byte[] flags = [0xfc, 0xfe, 0xf4, 0x85, 0xff, 0x7c, 0x04, 0xfe, 0x00, 0xfd, 0x10, 0x03];
        byte[] b8 = Block8(MediumBytes, flags);
        byte[] b1 = Block1(0x00, 0xa0);
        byte[] before1 = (byte[])b1.Clone();
        ProDucking.Apply(b8, b1, On("monitor", "aux"));
        byte[] wanted = (byte[])flags.Clone();
        wanted[5] |= 0x01;   // pair 1
        wanted[9] |= 0x01;   // pair 5
        // Entries without bit 1 lose a ducked bit they should not have; those with it keep theirs.
        wanted[3] = 0x84;    // Line In: bit 1 clear, so its ducked bit goes
        wanted[11] = 0x03;   // pair 7 has bit 1: kept as read
        Assert.Equal(wanted, Enumerable.Range(0, 12).Select(e => b8[e * 8 + 6]));
        // Low bits kept, high bits set for pairs 1 and 5 and cleared for 6 and 7 (0xa0: field 6 = 10, field 7 = 10).
        foreach (int at in ProDucking.MixFieldOffsets) Assert.Equal(new byte[] { 0x08, 0x08 }, b1[at..(at + 2)]);
        AssertOnlyMixFieldsChanged(before1, b1);
    }

    [Fact]
    public void ADisagreeingOrForeignPresetReadsAsCustomAndIsNotHeld()
    {
        byte[] b8 = Block8(MediumBytes, IdleFlags);
        b8[8 * 3] = 0xe8;               // one entry more sensitive than the rest
        b8[8 * 5 + 1] = 0x09;           // a depth no preset writes, in one entry
        b8[8 * 7 + 4] = 0xb1;           // a release that is not 1200
        DuckingReadback unit = ProDucking.Decode(b8, Block1());
        Assert.Equal(("custom", "custom", "custom"), (unit.Sensitivity, unit.Amount, unit.Speed));
        Assert.False(ProDucking.Holds(unit, On("monitor")));
        // Off leaves the presets alone, so that unit holds "off" all the same.
        Assert.True(ProDucking.Holds(unit, DuckingSettings.Default));
        Assert.False(ProDucking.Holds(new DuckingReadback("medium", "medium", "medium", [], []), DuckingSettings.Default));
    }

    [Theory]
    [InlineData("xlr3", "monitor", "medium", "medium", "medium")]
    [InlineData("xlr1", "game", "medium", "medium", "medium")]
    [InlineData("xlr1", "monitor", "loud", "medium", "medium")]
    [InlineData("xlr1", "monitor", "medium", "slow", "medium")]
    [InlineData("xlr1", "monitor", "medium", "medium", "high")]
    public void ValuesTheUnitDoesNotTakeAreRefused(string key, string target, string sensitivity, string amount, string speed)
    {
        var s = new DuckingSettings { Enabled = true, Key = key, Targets = [target], Sensitivity = sensitivity, Amount = amount, Speed = speed };
        Assert.NotNull(ProDucking.Valid(s));
        Assert.Throws<ArgumentException>(() => ProDucking.Apply(new byte[96], new byte[108], s));
        Assert.NotNull(ProDucking.Valid(On("monitor", "monitor")));
        Assert.Throws<ArgumentException>(() => ProDucking.Apply(new byte[95], new byte[108], On()));
        Assert.Throws<ArgumentException>(() => ProDucking.Apply(new byte[96], new byte[107], On()));
    }

    [Fact]
    public void OnlyASetupKeyedByXlr1FitsTheUnitAndAnotherIsWrittenAsOff()
    {
        var xlr2 = new DuckingSettings { Enabled = true, Key = "xlr2" };
        Assert.Null(ProDucking.Valid(xlr2));
        Assert.Equal("keyNotSupported", ProDucking.Unfit(xlr2));
        Assert.Null(ProDucking.Unfit(On("monitor", "aux")));
        Assert.False(ProDucking.OnUnit(xlr2).Enabled);
        Assert.Equal(On("aux"), ProDucking.OnUnit(On("aux")));
        Assert.NotNull(ProDucking.Valid(On() with { Processing = "software" }));
    }

    [Fact]
    public void AChangeKeepsWhatItDoesNotName()
    {
        var saved = new DuckingSettings { Enabled = true, Targets = ["aux"], Sensitivity = "high", Amount = "low", Speed = "fast" };
        Assert.Equal(saved with { Enabled = false }, new DuckingChange(Enabled: false).ApplyTo(saved));
        Assert.Equal(saved with { Targets = ["monitor", "aux"] }, new DuckingChange(Targets: ["aux", "monitor"]).ApplyTo(saved));
        Assert.Equal(saved with { Targets = [] }, new DuckingChange(Targets: []).ApplyTo(saved));
        Assert.Equal(saved, new DuckingChange().ApplyTo(saved));
        Assert.Equal(saved with { Key = "xlr2" }, new DuckingChange(Key: "xlr2").ApplyTo(saved));
    }
}
