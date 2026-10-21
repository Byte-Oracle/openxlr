namespace OpenXLR.Core.Devices;

/// <summary>
/// The Wave XLR Pro's USB playback pairs OpenXLR streams to. Pair n is
/// playback channels AUX(2n) and AUX(2n+1) of the unit's pro-audio sink.
/// </summary>
public static class ProReturnPairs
{
    /// <summary>The monitor feed of the jacks and Line Out: the unit's Personal mix takes it (channels 2/3).</summary>
    public const int Monitor = 1;

    /// <summary>The Aux mix for the USB Aux port: the aux mix's Music cell takes it (channels 10/11).</summary>
    public const int Aux = 5;
}

/// <summary>
/// The user's ducking setup: whether it is on, the key input, the outputs
/// it lowers, the three presets, and where it runs. Only the unit runs it
/// today (<c>device</c>); whether the unit can run a given setup is
/// <see cref="ProDucking.Unfit"/>. Values are the API's words;
/// <see cref="ProDucking.Valid"/> checks them.
/// </summary>
public sealed record DuckingSettings
{
    public bool Enabled { get; init; }

    /// <summary>The input whose level ducks the targets: <c>xlr1</c> or <c>xlr2</c>. The unit runs only <c>xlr1</c>.</summary>
    public string Key { get; init; } = ProDucking.KeyXlr1;

    /// <summary><c>monitor</c> and <c>aux</c>, the Pro outputs OpenXLR feeds.</summary>
    public IReadOnlyList<string> Targets { get; init; } = [ProDucking.TargetMonitor];

    public string Sensitivity { get; init; } = ProDucking.Medium;
    public string Amount { get; init; } = ProDucking.Medium;
    public string Speed { get; init; } = ProDucking.Medium;

    /// <summary>Where the setup runs: <c>device</c>, the only place today.</summary>
    public string Processing { get; init; } = ProDucking.Device;

    public static DuckingSettings Default { get; } = new();

    public bool Equals(DuckingSettings? other)
        => other is not null && Enabled == other.Enabled && Key == other.Key && Sensitivity == other.Sensitivity
            && Amount == other.Amount && Speed == other.Speed && Processing == other.Processing && Targets.SequenceEqual(other.Targets);

    public override int GetHashCode() => HashCode.Combine(Enabled, Key, Sensitivity, Amount, Speed, Targets.Count);

    public override string ToString() => Enabled
        ? $"on, key {Key}, targets {string.Join(" ", Targets)}, sensitivity {Sensitivity}, amount {Amount}, speed {Speed}"
        : "off";
}

/// <summary>
/// A change to <see cref="DuckingSettings"/>: each field present replaces
/// the setting's, an absent one keeps it.
/// </summary>
public sealed record DuckingChange(bool? Enabled = null, string? Key = null, IReadOnlyList<string>? Targets = null,
    string? Sensitivity = null, string? Amount = null, string? Speed = null, string? Processing = null)
{
    public DuckingSettings ApplyTo(DuckingSettings s) => s with
    {
        Enabled = Enabled ?? s.Enabled,
        Key = Key ?? s.Key,
        Targets = Targets is null ? s.Targets : ProDucking.Ordered(Targets),
        Sensitivity = Sensitivity ?? s.Sensitivity,
        Amount = Amount ?? s.Amount,
        Speed = Speed ?? s.Speed,
        Processing = Processing ?? s.Processing,
    };
}

/// <summary>
/// What the unit held at the last read: the preset each of the three
/// fields matches across all twelve entries (<c>custom</c> when the entries
/// disagree or hold a value no preset writes), the USB pairs ducked in any
/// of block 0x0001's three mix sections, and the block 0x0008 entries whose
/// flag bit 0 is set. <see cref="Raw"/> is the bytes the codec reads, hex,
/// so a check that the unit holds a setting needs no transfer.
/// </summary>
public sealed record DuckingReadback(string Sensitivity, string Amount, string Speed,
    IReadOnlyList<int> Pairs, IReadOnlyList<int> Entries)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Raw { get; init; } = "";

    public bool Equals(DuckingReadback? other)
        => other is not null && Raw == other.Raw && Sensitivity == other.Sensitivity && Amount == other.Amount
            && Speed == other.Speed && Pairs.SequenceEqual(other.Pairs) && Entries.SequenceEqual(other.Entries);

    public override int GetHashCode() => Raw.GetHashCode(StringComparison.Ordinal);
}

/// <summary>
/// The Wave XLR Pro's ducking, as Wave Link 3.5 writes it (captures 24 to
/// 31 of 2026-10-21; the unit then ducks by itself, capture 28):
///
/// Block 0x0008, 96 bytes: twelve entries of eight bytes, entries 0 to 3 the
/// hardware inputs (XLR 1, XLR 2, USB Aux in, Line In), entries 4 to 11 the
/// USB playback pairs 0 to 7.
///   byte 0      key threshold, int8 dB: sensitivity Low -3, Medium -12, High -24
///   byte 1      depth, dB: amount Low 6, Medium 12, High 24
///   bytes 2, 3  attack, LE16 ms; bytes 4, 5 release, LE16 ms:
///               speed Slow 100/2000, Medium 80/1200, Fast 30/200
///   byte 6      flags: bit 0 this channel is ducked; bit 1 unknown (Wave
///               Link leaves 02 and 03 on some entries with no key at all)
///   byte 7      00, kept
/// Wave Link writes the three presets to all twelve entries.
///
/// Block 0x0001, in each of its three mix sections (after the aa at 15, 33
/// and 51), bytes 16 to 17, 34 to 35 and 52 to 53: eight 2-bit fields, LE,
/// field k for USB pair k, 01 normally and 11 while the pair is ducked in
/// that mix. Only the high bit of each field is written here.
///
/// How the key input is encoded was not found: only XLR 1 was ever the key,
/// and nothing in these two blocks names it. Every byte and bit not listed
/// as written is kept as read.
/// </summary>
public static class ProDucking
{
    public const ushort Block = 0x0008;
    public const int Length = 96;
    public const int EntryCount = 12;
    public const int EntryLength = 8;
    /// <summary>The entry of USB playback pair 0; pair k is entry k + 4.</summary>
    public const int FirstPairEntry = 4;
    public const int PairCount = 8;
    public const int FlagsByte = 6;
    public const byte DuckedBit = 0x01;
    /// <summary>Unknown; an entry that has it keeps its flags byte as read unless it is a target.</summary>
    public const byte UnknownBit = 0x02;

    /// <summary>The first byte of each mix section's eight 2-bit pair fields in block 0x0001.</summary>
    public static IReadOnlyList<int> MixFieldOffsets { get; } = [16, 34, 52];
    public const int MixBlockLength = 108;

    public const string KeyXlr1 = "xlr1";
    public const string KeyXlr2 = "xlr2";
    public const string Device = "device";
    /// <summary>A <see cref="Unfit"/> reason: the setup's key is one the unit is not known to take.</summary>
    public const string KeyNotSupported = "keyNotSupported";
    public const string TargetMonitor = "monitor";
    public const string TargetAux = "aux";
    public const string Low = "low", Medium = "medium", High = "high", Slow = "slow", Fast = "fast", Custom = "custom";

    public static IReadOnlyList<string> Keys { get; } = [KeyXlr1, KeyXlr2];
    public static IReadOnlyList<string> Targets { get; } = [TargetMonitor, TargetAux];
    public static IReadOnlyList<string> Sensitivities { get; } = [Low, Medium, High];
    public static IReadOnlyList<string> Amounts { get; } = [Low, Medium, High];
    public static IReadOnlyList<string> Speeds { get; } = [Slow, Medium, Fast];

    private static readonly sbyte[] Thresholds = [-3, -12, -24];
    private static readonly byte[] Depths = [6, 12, 24];
    private static readonly (ushort Attack, ushort Release)[] Times = [(100, 2000), (80, 1200), (30, 200)];

    /// <summary>The USB playback pair a target streams on.</summary>
    public static int PairOf(string target) => target == TargetAux ? ProReturnPairs.Aux : ProReturnPairs.Monitor;

    /// <summary>The targets without repeats, in the API's order.</summary>
    public static IReadOnlyList<string> Ordered(IEnumerable<string> targets)
        => [.. Targets.Where(targets.Contains)];

    /// <summary>Why a setting cannot be written, or null when every value is one the unit takes.</summary>
    public static string? Valid(DuckingSettings s)
    {
        if (!Keys.Contains(s.Key)) return "key must be xlr1 or xlr2";
        if (s.Processing != Device) return "processing must be device, the one place ducking runs today";
        if (s.Targets.Any(t => !Targets.Contains(t)) || s.Targets.Distinct().Count() != s.Targets.Count)
            return "targets must be distinct entries of monitor and aux";
        if (!Sensitivities.Contains(s.Sensitivity)) return "sensitivity must be low, medium or high";
        if (!Amounts.Contains(s.Amount)) return "amount must be low, medium or high";
        if (!Speeds.Contains(s.Speed)) return "speed must be slow, medium or fast";
        return null;
    }

    /// <summary>
    /// Why the unit cannot run a setup, as a word a client can explain, or
    /// null when it can: the key must be XLR 1, the only key Wave Link was
    /// seen to write, and every target is one of the Pro's whole outputs.
    /// A setup the unit cannot run is written as ducking off.
    /// </summary>
    public static string? Unfit(DuckingSettings s) => s.Key != KeyXlr1 ? KeyNotSupported : null;

    /// <summary>What the unit is given for a setup: the setup itself, or ducking off when it cannot run it.</summary>
    public static DuckingSettings OnUnit(DuckingSettings s) => Unfit(s) is null ? s : s with { Enabled = false };

    /// <summary>The six preset bytes every entry takes for a setting.</summary>
    public static byte[] PresetBytes(DuckingSettings s)
    {
        sbyte threshold = Thresholds[IndexOf(Sensitivities, s.Sensitivity)];
        byte depth = Depths[IndexOf(Amounts, s.Amount)];
        (ushort attack, ushort release) = Times[IndexOf(Speeds, s.Speed)];
        return [(byte)threshold, depth, (byte)attack, (byte)(attack >> 8), (byte)release, (byte)(release >> 8)];
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++) if (names[i] == name) return i;
        throw new ArgumentException($"'{name}' is not one of {string.Join(", ", names)}");
    }

    /// <summary>
    /// Write a setting into the two blocks: the presets while it is on, the
    /// ducked flag of every entry, and the high bit of every pair field in
    /// the three mix sections. Everything else is left as it was.
    /// </summary>
    /// <exception cref="ArgumentException">A block has the wrong length or the setting a value the unit does not take.</exception>
    public static void Apply(Span<byte> block8, Span<byte> block1, DuckingSettings s)
    {
        if (block8.Length != Length) throw new ArgumentException($"block 0x0008 is {Length} bytes", nameof(block8));
        if (block1.Length != MixBlockLength) throw new ArgumentException($"block 0x0001 is {MixBlockLength} bytes", nameof(block1));
        if (Valid(s) is { } problem) throw new ArgumentException(problem, nameof(s));
        int pairs = WantedPairs(s);
        byte[]? preset = s.Enabled ? PresetBytes(s) : null;
        for (int e = 0; e < EntryCount; e++)
        {
            Span<byte> entry = block8.Slice(e * EntryLength, EntryLength);
            preset?.CopyTo(entry);
            bool wanted = e >= FirstPairEntry && (pairs & 1 << (e - FirstPairEntry)) != 0;
            if (wanted) entry[FlagsByte] |= DuckedBit;
            else if ((entry[FlagsByte] & UnknownBit) == 0) entry[FlagsByte] &= unchecked((byte)~DuckedBit);
        }
        foreach (int at in MixFieldOffsets)
        {
            int fields = block1[at] | block1[at + 1] << 8;
            for (int k = 0; k < PairCount; k++)
            {
                int high = 1 << (2 * k + 1);
                fields = (pairs & 1 << k) != 0 ? fields | high : fields & ~high;
            }
            block1[at] = (byte)fields;
            block1[at + 1] = (byte)(fields >> 8);
        }
    }

    /// <summary>The pairs a setting ducks, as a mask with bit k for pair k: none while it is off.</summary>
    public static int WantedPairs(DuckingSettings s)
        => s.Enabled ? s.Targets.Aggregate(0, (mask, t) => mask | 1 << PairOf(t)) : 0;

    /// <summary>What the two blocks hold, decoded; the 96 bytes of 0x0008 and the 108 of 0x0001.</summary>
    public static DuckingReadback Decode(ReadOnlySpan<byte> block8, ReadOnlySpan<byte> block1)
    {
        if (block8.Length != Length || block1.Length != MixBlockLength)
            throw new ArgumentException("block 0x0008 is 96 bytes and block 0x0001 108");
        var raw = new byte[Length + 2 * MixFieldOffsets.Count];
        block8.CopyTo(raw);
        for (int i = 0; i < MixFieldOffsets.Count; i++)
        {
            raw[Length + 2 * i] = block1[MixFieldOffsets[i]];
            raw[Length + 2 * i + 1] = block1[MixFieldOffsets[i] + 1];
        }
        return FromRaw(raw);
    }

    private static DuckingReadback FromRaw(byte[] raw)
    {
        string sensitivity = Same(raw, 0, 1, v => Array.IndexOf(Thresholds, (sbyte)v), Sensitivities);
        string amount = Same(raw, 1, 1, v => Array.IndexOf(Depths, (byte)v), Amounts);
        string speed = Same(raw, 2, 4, v => Array.FindIndex(Times, t => (t.Attack | t.Release << 16) == v), Speeds);
        var pairs = new List<int>();
        for (int k = 0; k < PairCount; k++)
            for (int i = 0; i < MixFieldOffsets.Count; i++)
            {
                int fields = raw[Length + 2 * i] | raw[Length + 2 * i + 1] << 8;
                if ((fields & 1 << (2 * k + 1)) != 0) { pairs.Add(k); break; }
            }
        var entries = new List<int>();
        for (int e = 0; e < EntryCount; e++)
            if ((raw[e * EntryLength + FlagsByte] & DuckedBit) != 0) entries.Add(e);
        return new DuckingReadback(sensitivity, amount, speed, pairs, entries) { Raw = Convert.ToHexString(raw) };
    }

    /// <summary>
    /// The preset a field holds in all twelve entries: the field's
    /// little-endian value at <paramref name="offset"/>, <paramref name="width"/>
    /// bytes wide, looked up by <paramref name="index"/>; <c>custom</c> when
    /// the entries disagree or no preset writes the value.
    /// </summary>
    private static string Same(byte[] block8, int offset, int width, Func<int, int> index, IReadOnlyList<string> names)
    {
        int first = 0;
        for (int e = 0; e < EntryCount; e++)
        {
            int value = 0;
            for (int b = width - 1; b >= 0; b--) value = value << 8 | block8[e * EntryLength + offset + b];
            if (e == 0) first = value;
            else if (value != first) return Custom;
        }
        return index(first) is int i and >= 0 ? names[i] : Custom;
    }

    /// <summary>
    /// Whether the unit, as last read, holds a setting: writing it would
    /// change no byte. False when the readback carries no raw bytes.
    /// </summary>
    public static bool Holds(DuckingReadback unit, DuckingSettings s)
    {
        if (unit.Raw.Length != 2 * (Length + 2 * MixFieldOffsets.Count) || Valid(s) is not null) return false;
        byte[] raw = Convert.FromHexString(unit.Raw);
        byte[] block8 = raw[..Length];
        var block1 = new byte[MixBlockLength];
        for (int i = 0; i < MixFieldOffsets.Count; i++)
        {
            block1[MixFieldOffsets[i]] = raw[Length + 2 * i];
            block1[MixFieldOffsets[i] + 1] = raw[Length + 2 * i + 1];
        }
        byte[] want8 = (byte[])block8.Clone(), want1 = (byte[])block1.Clone();
        Apply(want8, want1, s);
        return want8.AsSpan().SequenceEqual(block8) && want1.AsSpan().SequenceEqual(block1);
    }

    /// <summary>The bytes that differ between two copies of a block, as "offset old->new" pairs, for the journal.</summary>
    public static string Diff(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        var parts = new List<string>();
        for (int i = 0; i < Math.Min(before.Length, after.Length); i++)
            if (before[i] != after[i]) parts.Add($"{i} {before[i]:x2}->{after[i]:x2}");
        return parts.Count == 0 ? "no change" : string.Join(", ", parts);
    }
}

/// <summary>
/// What one ducking write did: for each block whether it was written, and
/// the bytes it changed. Neither written when the unit already held it.
/// </summary>
public sealed record DuckingWrite(bool Wrote8, string Change8, bool Wrote1, string Change1)
{
    public bool Wrote => Wrote8 || Wrote1;
}
