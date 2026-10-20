namespace OpenXLR.Core.Devices;

/// <summary>
/// One band of a unit's own EQ in the unit's raw form: what its settings
/// block holds, byte for byte. <see cref="Type"/> is 0 high pass, 1 low
/// shelf, 2 bell, 3 notch, 4 high shelf, 5 low pass, the order of
/// <c>MicEq.Types</c>. <see cref="Gain"/> is in quarter decibels and
/// <see cref="Q"/> is round(64 log10 Q).
/// </summary>
public sealed record DeviceEqBand(bool Enabled, int Type, int Gain, int Frequency, int Q);

/// <summary>
/// A unit's EQ in its raw form: the on switch and four bands. With
/// <see cref="Bands"/> null it names the switch alone, which is what
/// releasing a unit's EQ writes: the bands stay as they are.
/// </summary>
public sealed record DeviceEq(bool Enabled, IReadOnlyList<DeviceEqBand>? Bands)
{
    /// <summary>The switch off and nothing else: the bands the unit holds are kept.</summary>
    public static DeviceEq Off { get; } = new(false, null);

    /// <summary>Whether this names the switch alone.</summary>
    public bool SwitchOnly => Bands is null;

    public bool Equals(DeviceEq? other)
        => other is not null && Enabled == other.Enabled
            && (ReferenceEquals(Bands, other.Bands) || Bands is not null && other.Bands is not null && Bands.SequenceEqual(other.Bands));

    public override int GetHashCode() => HashCode.Combine(Enabled, Bands?.Count ?? -1);

    /// <summary>
    /// Whether the fields this value names match what a unit holds: the
    /// switch, and the bands when it names them. Compared in the raw form,
    /// so the one Q byte no grid point reaches cannot hide a difference.
    /// </summary>
    public bool HeldBy(DeviceEq held)
        => Enabled == held.Enabled && (Bands is null || held.Bands is not null && Bands.SequenceEqual(held.Bands));

    public override string ToString() => Bands is null ? $"switch {(Enabled ? "on" : "off")}"
        : $"{(Enabled ? "on" : "off")}; " + string.Join("; ", Bands.Select((b, i) =>
            $"band {i + 1} {(b.Enabled ? "on" : "off")} type {b.Type} gain {b.Gain / 4.0:0.##} dB {b.Frequency} Hz q {b.Q}"));
}

/// <summary>
/// Whether a unit runs the microphone EQ on its own DSP, and when it does
/// not though its model can, why: <see cref="Reason"/> is a word a client
/// can explain (<c>firmwareOld</c>, <c>firmwareUnknown</c>,
/// <c>unsupportedBlock</c>) and <see cref="Note"/> the sentence the daemon
/// gives. Null on a model without a hardware EQ.
/// </summary>
public sealed record DeviceEqSupport(bool Ready, string? Reason = null, string? Note = null)
{
    public const string FirmwareOld = "firmwareOld";
    public const string FirmwareUnknown = "firmwareUnknown";
    public const string UnsupportedBlock = "unsupportedBlock";
}

/// <summary>
/// The four-band EQ in one 38-byte input struct of the Wave FX settings
/// block (block <c>0x0004</c>), as the Wave XLR MK.2 and the XLR Dock MK.2
/// hold it from firmware 1.0.15, and as Wave Link 3.5 writes it:
///
///   byte 2, bit 0          the EQ's on switch
///   bytes 18, 23, 28, 33   flags: bit 0 band on, bits 1 to 3 the type, bits 4 to 7 kept
///   bytes 19, 24, 29, 34   gain, int8, quarter dB
///   bytes 20 and 21, ...   frequency, uint16 little endian, Hz
///   bytes 22, 27, 32, 37   Q, int8, round(64 log10 Q)
///
/// Every other byte and bit belongs to another control (gain, the flag
/// byte at 1, ClipGuard at byte 2 bit 2, voice tune strength, unmapped live
/// parameters) and is never touched here. The Wave XLR Pro's block holds
/// two structs of the same shape at 0 and 38, so the codec takes a span.
/// </summary>
public static class WaveFxEq
{
    /// <summary>The length of one input struct.</summary>
    public const int StructLength = 38;
    public const int SwitchOffset = 2;
    public const byte SwitchMask = 0x01;
    public const int FirstBand = 18;
    public const int BandStride = 5;
    public const int Bands = 4;
    private const byte BandOnMask = 0x01;
    private const byte TypeMask = 0x0E;
    private const byte OwnedFlagBits = BandOnMask | TypeMask;

    /// <summary>The ranges a band must decode into, the ranges the EQ itself takes.</summary>
    public const int MaxType = 5, MinGain = -48, MaxGain = 48, MinFrequency = 20, MaxFrequency = 20000, MinQ = -64, MaxQ = 64;

    /// <summary>Read the EQ from an input struct, raw, whatever the values. Null when the struct is too short.</summary>
    public static DeviceEq? Decode(ReadOnlySpan<byte> input)
    {
        if (input.Length < StructLength) return null;
        var bands = new DeviceEqBand[Bands];
        for (int i = 0; i < Bands; i++)
        {
            int at = FirstBand + i * BandStride;
            byte flags = input[at];
            bands[i] = new DeviceEqBand((flags & BandOnMask) != 0, (flags & TypeMask) >> 1, (sbyte)input[at + 1],
                input[at + 2] | input[at + 3] << 8, (sbyte)input[at + 4]);
        }
        return new DeviceEq((input[SwitchOffset] & SwitchMask) != 0, bands);
    }

    /// <summary>
    /// Whether every band holds a value the EQ takes: a known type, a
    /// frequency from 20 to 20000 Hz, a gain from -12 to +12 dB and a Q
    /// from 0.1 to 10. A unit whose block does not is not driven.
    /// </summary>
    public static bool Decodes(DeviceEq? eq)
        => eq?.Bands is { Count: Bands } bands && bands.All(b => b.Type is >= 0 and <= MaxType
            && b.Frequency is >= MinFrequency and <= MaxFrequency && b.Gain is >= MinGain and <= MaxGain && b.Q is >= MinQ and <= MaxQ);

    /// <summary>
    /// Write an EQ into an input struct: the switch, and the bands when the
    /// value names them. Flags bits 4 to 7 and every byte the EQ does not
    /// own keep what the struct held.
    /// </summary>
    /// <exception cref="ArgumentException">The struct is short or a value is outside the EQ's ranges.</exception>
    public static void Encode(Span<byte> input, DeviceEq eq)
    {
        if (input.Length < StructLength) throw new ArgumentException($"an input struct is {StructLength} bytes", nameof(input));
        if (eq.Bands is { } bands && (bands.Count != Bands || !Decodes(eq)))
            throw new ArgumentException("the EQ holds a value outside the unit's ranges", nameof(eq));
        input[SwitchOffset] = eq.Enabled ? (byte)(input[SwitchOffset] | SwitchMask) : (byte)(input[SwitchOffset] & ~SwitchMask);
        if (eq.Bands is null) return;
        for (int i = 0; i < Bands; i++)
        {
            DeviceEqBand band = eq.Bands[i];
            int at = FirstBand + i * BandStride;
            input[at] = (byte)(input[at] & ~OwnedFlagBits | band.Type << 1 | (band.Enabled ? BandOnMask : 0));
            input[at + 1] = (byte)(sbyte)band.Gain;
            input[at + 2] = (byte)(band.Frequency & 0xFF);
            input[at + 3] = (byte)(band.Frequency >> 8);
            input[at + 4] = (byte)(sbyte)band.Q;
        }
    }

    /// <summary>
    /// A firmware version in block <c>0x0000</c> bytes 5 to 7 (major, minor,
    /// patch), or null when the block is too short to hold one.
    /// </summary>
    public static Version? FirmwareVersion(ReadOnlySpan<byte> identity)
        => identity.Length >= 8 ? new Version(identity[5], identity[6], identity[7]) : null;

    /// <summary>The first firmware these units run the EQ on: 1.0.15, measured on both models.</summary>
    public static Version EqFirmware { get; } = new(1, 0, 15);
}
