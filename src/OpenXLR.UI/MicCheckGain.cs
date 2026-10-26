using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenXLR.UI;

/// <summary>
/// The levels Mic check's Level step works to, in dBFS. The band is the one
/// the window draws under the meter (its columns are 42, 12 and 6 dB of the
/// 60 dB scale), and Suggest gain aims inside it.
/// </summary>
public static class MicCheckLevels
{
    /// <summary>The bottom of the meter's scale: the daemon sends anything quieter as this.</summary>
    public const double MeterFloorDb = -60;

    /// <summary>The band speech should peak in.</summary>
    public const double BandLowDb = -18, BandHighDb = -6;

    /// <summary>
    /// Where Suggest gain puts the loud stretches of speech: the level the
    /// meter's 300 ms RMS stays under for 95 percent of the measurement.
    /// Speech peaks sit several dB above it, so at this level they land in
    /// the band, and the loudest is held to <see cref="BandHighDb"/>.
    /// </summary>
    public const double TargetSpeechDb = -20;

    /// <summary>The share of RMS readings the speech level is above.</summary>
    public const double SpeechPercentile = 0.95;

    /// <summary>A speech level this low is too close to the meter's floor to tell a voice from silence.</summary>
    public const double NoVoiceDb = -54;

    /// <summary>A peak reading this high is taken as a clip, with the daemon's clip flag.</summary>
    public const double ClipDb = -0.5;

    /// <summary>How long Suggest gain listens.</summary>
    public static readonly TimeSpan MeasureTime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The fewest readings a measurement is judged on: two seconds of the
    /// daemon's 15 Hz meters. Fewer means the meter did not run.
    /// </summary>
    public const int MinReadings = 30;

    /// <summary>A change smaller than this is not worth making: the gain is kept.</summary>
    public const int ToleranceDb = 2;

    /// <summary>A meter reading on the 0 to 1 scale, as dBFS.</summary>
    public static double FromMeter(double scale) => Math.Clamp(scale, 0, 1) * 60 + MeterFloorDb;
}

/// <summary>One reading of the meter before the chain, in dBFS, and whether the daemon flagged a clip.</summary>
public readonly record struct LevelReading(double RmsDb, double PeakDb, bool Clip);

/// <summary>What Suggest gain made of a measurement.</summary>
public enum GainOutcome
{
    /// <summary>The meter sent too few readings to judge.</summary>
    TooFewReadings,
    /// <summary>The level stayed too near the meter's floor to tell a voice from silence.</summary>
    NoVoice,
    /// <summary>The gain already puts speech in the band: keep it.</summary>
    Keep,
    /// <summary>A new gain inside the unit's range puts speech in the band.</summary>
    Change,
    /// <summary>Even the unit's highest gain leaves speech under the target.</summary>
    AtMaximum,
    /// <summary>Even the unit's lowest gain leaves speech over the target.</summary>
    AtMinimum,
    /// <summary>The microphone has no gain OpenXLR sets: the change it needs is said, not made.</summary>
    Elsewhere,
}

/// <summary>
/// A measurement and the gain it suggests. <see cref="SpeechDb"/> is the
/// 95th percentile of the meter's RMS readings, <see cref="PeakDb"/> the
/// highest sample peak, and <see cref="ChangeDb"/> what the level needs:
/// enough to bring the speech level to the target, and no more than keeps
/// the highest peak at the band's top. <see cref="ToDb"/> is that change on
/// the unit's whole-dB gain, inside its range.
/// </summary>
public sealed record GainSuggestion(GainOutcome Outcome, int Readings, double SpeechDb, double PeakDb, bool Clipped,
    double ChangeDb, int? FromDb, int? ToDb)
{
    /// <summary>The suggestion changes the unit's gain.</summary>
    public bool ChangesGain => FromDb is int from && ToDb is int to && from != to;

    /// <summary>
    /// Judge a measurement. <paramref name="gainDb"/> is the unit's gain while
    /// it was taken, or null when OpenXLR sets no gain for the microphone;
    /// the unit's gain runs from 0 to <paramref name="maxDb"/> in whole dB.
    /// </summary>
    public static GainSuggestion From(IReadOnlyList<LevelReading> readings, int? gainDb, int maxDb)
    {
        if (readings.Count < MicCheckLevels.MinReadings)
            return new(GainOutcome.TooFewReadings, readings.Count, MicCheckLevels.MeterFloorDb, MicCheckLevels.MeterFloorDb, false, 0, gainDb, gainDb);
        double[] rms = [.. readings.Select(r => r.RmsDb).Order()];
        int rank = Math.Clamp((int)Math.Ceiling(MicCheckLevels.SpeechPercentile * rms.Length) - 1, 0, rms.Length - 1);
        double speech = rms[rank];
        double peak = readings.Max(r => r.PeakDb);
        // The clip flag holds for a second, so one already set when the
        // measurement starts may be older than it; only a flag that rises
        // during it counts, beside a peak reading at full scale.
        bool clipped = peak >= MicCheckLevels.ClipDb
            || readings.Zip(readings.Skip(1)).Any(pair => !pair.First.Clip && pair.Second.Clip);
        if (speech <= MicCheckLevels.NoVoiceDb && !clipped)
            return new(GainOutcome.NoVoice, readings.Count, speech, peak, false, 0, gainDb, gainDb);

        double change = Math.Min(MicCheckLevels.TargetSpeechDb - speech, MicCheckLevels.BandHighDb - peak);
        if (gainDb is not int from)
            return new(Math.Abs(change) < MicCheckLevels.ToleranceDb && !clipped ? GainOutcome.Keep : GainOutcome.Elsewhere,
                readings.Count, speech, peak, clipped, change, null, null);

        // Whole dB, rounded down where rounding up would lift the highest
        // peak over the band's top.
        double wanted = from + change;
        int to = (int)Math.Round(wanted, MidpointRounding.AwayFromZero);
        if (peak + (to - from) > MicCheckLevels.BandHighDb) to = (int)Math.Floor(wanted);
        to = Math.Clamp(to, 0, maxDb);
        GainOutcome outcome = wanted > maxDb + 0.5 ? GainOutcome.AtMaximum
            : wanted < -0.5 ? GainOutcome.AtMinimum
            : Math.Abs(to - from) < MicCheckLevels.ToleranceDb && !clipped ? GainOutcome.Keep
            : GainOutcome.Change;
        if (outcome == GainOutcome.Keep) to = from;
        return new(outcome, readings.Count, speech, peak, clipped, change, from, to);
    }

    /// <summary>How far the level stays from the target at the gain suggested, in dB: under it at the maximum, over it at the minimum.</summary>
    public double ShortfallDb => FromDb is int from && ToDb is int to ? Math.Abs(from + ChangeDb - to) : 0;
}
