using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenXLR.UI;

/// <summary>
/// The sizes the mixer's own window draws the submixer at, as kept in
/// ui.json under <c>mixerScale</c>: <see cref="Fit"/>, or a whole percentage
/// of the size the skin and the control sizing give it, from
/// <see cref="MinimumPercent"/> to <see cref="MaximumPercent"/>. The picker
/// lists <see cref="Choices"/> and a custom size for any other percentage in
/// that range. Nothing smaller than the skin's own size is offered: a smaller
/// mixer is the compact density or a smaller window, which scrolls.
/// </summary>
public static class MixerScale
{
    /// <summary>The largest size at which the whole panel shows without scrolling.</summary>
    public const string Fit = "fit";

    /// <summary>The skin's own size, and the choice when none is saved.</summary>
    public const string Default = "100";

    /// <summary>The fixed choices, in the order the picker lists them; a custom size follows them.</summary>
    public static readonly IReadOnlyList<string> Choices = [Fit, "100", "125", "150", "175", "200"];

    /// <summary>The smallest size, in percent: the skin's own.</summary>
    public const int MinimumPercent = 100;

    /// <summary>The largest size, in percent, typed or found by <see cref="Fit"/>.</summary>
    public const int MaximumPercent = 300;

    /// <summary>The most <see cref="Fit"/> enlarges, in percent.</summary>
    public const int FitMaximumPercent = MaximumPercent;

    /// <summary>The steps <see cref="Fit"/> searches in, in percent, and the custom size's spinner steps.</summary>
    public const int FitStepPercent = 5;

    /// <summary>
    /// The choice as it is written: <see cref="Fit"/>, or a whole percentage
    /// in range in plain digits, leading zeros dropped. Null for anything
    /// else, a percentage out of range included: the picker never writes one,
    /// so it was damaged or written by hand, and the skin's own size is the
    /// one known to suit the panel.
    /// </summary>
    public static string? Known(string? choice) =>
        choice == Fit ? Fit : Percent(choice) is int percent ? percent.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>The whole percentage a choice draws at; null for <see cref="Fit"/> and anything <see cref="Known"/> refuses.</summary>
    public static int? Percent(string? choice) =>
        int.TryParse(choice, NumberStyles.None, CultureInfo.InvariantCulture, out int percent)
            && percent is >= MinimumPercent and <= MaximumPercent ? percent : null;

    /// <summary>True when the choice is one the picker lists by itself, false for a custom size.</summary>
    public static bool Listed(string? choice) => Known(choice) is { } known && Choices.Contains(known, StringComparer.Ordinal);

    /// <summary>A typed size as a whole percentage, rounded and held to the range.</summary>
    public static int Clamp(decimal percent) =>
        (int)Math.Clamp(Math.Round(percent, MidpointRounding.AwayFromZero), MinimumPercent, MaximumPercent);

    /// <summary>The factor a choice draws at; <see cref="Fit"/> and anything unknown give 1.</summary>
    public static double Factor(string? choice) => Percent(choice) is int percent ? percent / 100.0 : 1;

    /// <summary>
    /// The largest scale from 1 to <see cref="FitMaximumPercent"/>, in steps
    /// of <see cref="FitStepPercent"/>, at which <paramref name="fits"/> holds;
    /// 1 when none above it does. A larger scale only ever needs more room,
    /// so the answer is searched by halves and <paramref name="fits"/> runs a
    /// handful of times, never for 1 itself.
    /// </summary>
    public static double LargestFit(Func<double, bool> fits)
    {
        int low = 0, high = (FitMaximumPercent - MinimumPercent) / FitStepPercent;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (fits(Percent(middle))) low = middle;
            else high = middle - 1;
        }
        return Percent(low);

        static double Percent(int step) => (MinimumPercent + step * FitStepPercent) / 100.0;
    }
}
