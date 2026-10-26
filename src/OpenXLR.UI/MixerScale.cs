using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenXLR.UI;

/// <summary>
/// The sizes the mixer's own window draws the submixer at, as kept in
/// ui.json under <c>mixerScale</c>: <see cref="Fit"/>, or a percentage of
/// the size the skin and the control sizing give it. Nothing smaller than
/// the skin's own size is offered: a smaller mixer is the compact density
/// or a smaller window, which scrolls.
/// </summary>
public static class MixerScale
{
    /// <summary>The largest size at which the whole panel shows without scrolling.</summary>
    public const string Fit = "fit";

    /// <summary>The skin's own size, and the choice when none is saved.</summary>
    public const string Default = "100";

    /// <summary>Every choice, in the order the picker lists them.</summary>
    public static readonly IReadOnlyList<string> Choices = [Fit, "100", "125", "150", "175", "200"];

    /// <summary>The most <see cref="Fit"/> enlarges, in percent.</summary>
    public const int FitMaximumPercent = 300;

    /// <summary>The steps <see cref="Fit"/> searches in, in percent.</summary>
    public const int FitStepPercent = 5;

    /// <summary>The choice itself when it is one of <see cref="Choices"/>, otherwise null.</summary>
    public static string? Known(string? choice) => choice is not null && Choices.Contains(choice, StringComparer.Ordinal) ? choice : null;

    /// <summary>The factor a fixed choice draws at; <see cref="Fit"/> and anything unknown give 1.</summary>
    public static double Factor(string? choice) =>
        Known(choice) is { } known && known != Fit ? int.Parse(known, CultureInfo.InvariantCulture) / 100.0 : 1;

    /// <summary>
    /// The largest scale from 1 to <see cref="FitMaximumPercent"/>, in steps
    /// of <see cref="FitStepPercent"/>, at which <paramref name="fits"/> holds;
    /// 1 when none above it does. A larger scale only ever needs more room,
    /// so the answer is searched by halves and <paramref name="fits"/> runs a
    /// handful of times, never for 1 itself.
    /// </summary>
    public static double LargestFit(Func<double, bool> fits)
    {
        int low = 0, high = (FitMaximumPercent - 100) / FitStepPercent;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (fits(Percent(middle))) low = middle;
            else high = middle - 1;
        }
        return Percent(low);

        static double Percent(int step) => (100 + step * FitStepPercent) / 100.0;
    }
}
