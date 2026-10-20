namespace OpenXLR.Core.Mixing;

/// <summary>One band's normalised biquad coefficients: a0 is 1.</summary>
public readonly record struct EqCoefficients(double B0, double B1, double B2, double A1, double A2);

/// <summary>
/// The microphone EQ's curve: each band's coefficients and the magnitude of
/// the cascade at any frequency, in double precision. Written from Robert
/// Bristow-Johnson's Audio EQ Cookbook (https://www.w3.org/TR/audio-eq-cookbook/),
/// whose curves Wave Link's EQ uses. The native helper's <c>eq</c> backend
/// computes the same coefficients, and tools/eq-reference.py writes the
/// table both are tested against. The window draws its curve from here, so
/// what is drawn is what runs.
/// </summary>
public static class EqResponse
{
    /// <summary>
    /// A band's coefficients at <paramref name="rate"/>. The shelves take
    /// the same Q as the bell. Shelves and the bell keep a 2 Hz floor and the
    /// linear gain a 1e-15 floor, which the daemon's ranges never reach; a
    /// frequency above 0.49 of the rate is held there.
    /// </summary>
    public static EqCoefficients Coefficients(EqBand band, double rate)
    {
        double frequency = Math.Min(band.Frequency, 0.49 * rate);
        if (band.Type is MicEq.LowShelf or MicEq.HighShelf or MicEq.Peak) frequency = Math.Max(frequency, 2);
        double w0 = 2 * Math.PI * frequency / rate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        double alpha = sin / (2 * band.Q);
        double a = Math.Sqrt(Math.Max(Math.Pow(10, band.Gain / 20), 1e-15));   // 10^(dB/40)
        double root = 2 * Math.Sqrt(a) * alpha;
        (double b0, double b1, double b2, double a0, double a1, double a2) = band.Type switch
        {
            MicEq.LowCut => ((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha),
            MicEq.HighCut => ((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha),
            MicEq.Notch => (1, -2 * cos, 1, 1 + alpha, -2 * cos, 1 - alpha),
            MicEq.LowShelf => (
                a * ((a + 1) - (a - 1) * cos + root),
                2 * a * ((a - 1) - (a + 1) * cos),
                a * ((a + 1) - (a - 1) * cos - root),
                (a + 1) + (a - 1) * cos + root,
                -2 * ((a - 1) + (a + 1) * cos),
                (a + 1) + (a - 1) * cos - root),
            MicEq.HighShelf => (
                a * ((a + 1) + (a - 1) * cos + root),
                -2 * a * ((a - 1) + (a + 1) * cos),
                a * ((a + 1) + (a - 1) * cos - root),
                (a + 1) - (a - 1) * cos + root,
                2 * ((a - 1) - (a + 1) * cos),
                (a + 1) - (a - 1) * cos - root),
            MicEq.Peak => (1 + alpha * a, -2 * cos, 1 - alpha * a, 1 + alpha / a, -2 * cos, 1 - alpha / a),
            _ => throw new ArgumentException($"Unknown EQ band type '{band.Type}'."),
        };
        return new(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>The magnitude of one set of coefficients at <paramref name="frequency"/>, in dB.</summary>
    public static double MagnitudeDb(EqCoefficients c, double rate, double frequency)
    {
        double w = 2 * Math.PI * frequency / rate;
        // H(e^jw) with z^-1 = cos w - j sin w.
        double c1 = Math.Cos(w), s1 = -Math.Sin(w), c2 = Math.Cos(2 * w), s2 = -Math.Sin(2 * w);
        double nr = c.B0 + c.B1 * c1 + c.B2 * c2, ni = c.B1 * s1 + c.B2 * s2;
        double dr = 1 + c.A1 * c1 + c.A2 * c2, di = c.A1 * s1 + c.A2 * s2;
        return 10 * Math.Log10((nr * nr + ni * ni) / (dr * dr + di * di));
    }

    /// <summary>The cascade of the bands that are on, whatever the EQ's own switch says; for drawing.</summary>
    public static double CascadeDb(IEnumerable<EqBand> bands, double rate, double frequency)
        => bands.Where(b => b.Enabled).Sum(b => MagnitudeDb(Coefficients(b, rate), rate, frequency));

    /// <summary>What the EQ does to <paramref name="frequency"/>: 0 dB while it is off.</summary>
    public static double MagnitudeDb(MicEq eq, double rate, double frequency)
        => eq.Enabled ? CascadeDb(eq.Bands, rate, frequency) : 0;
}
