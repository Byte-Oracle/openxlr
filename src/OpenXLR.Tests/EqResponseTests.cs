using System.Globalization;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The EQ's curve maths against native/tests/eq-reference.txt, which
/// tools/eq-reference.py computes from the Audio EQ Cookbook; the native
/// backend's measured response is held to the same table by
/// <c>make -C native test-eq</c>.
/// </summary>
public sealed class EqResponseTests
{
    private sealed record Row(int Rate, EqBand[] Bands, double[] Db);

    private static (double[] Frequencies, List<Row> Rows) Table()
    {
        double[]? frequencies = null;
        var rows = new List<Row>();
        foreach (string line in File.ReadLines(Path.Combine(MicEqTests.RepoRoot, "native", "tests", "eq-reference.txt")))
        {
            string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length == 0 || f[0] == "#") continue;
            if (f[0] == "f") { frequencies = [.. f.Skip(1).Select(Number)]; continue; }
            Assert.Equal("r", f[0]);
            var bands = new EqBand[4];
            for (int i = 0; i < 4; i++)
            {
                int at = 2 + 5 * i;
                bands[i] = new EqBand { Enabled = f[at] == "1", Type = f[at + 1], Frequency = Number(f[at + 2]), Gain = Number(f[at + 3]), Q = Number(f[at + 4]) };
            }
            rows.Add(new(int.Parse(f[1], CultureInfo.InvariantCulture), bands, [.. f.Skip(22).Select(Number)]));
        }
        Assert.NotNull(frequencies);
        return (frequencies!, rows);

        static double Number(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    [Fact]
    public void TheCurveEqualsTheCookbookTableAtBothRates()
    {
        (double[] frequencies, List<Row> rows) = Table();
        Assert.Equal(64, frequencies.Length);
        Assert.True(rows.Count >= 60);
        Assert.Equal([44100, 48000], rows.Select(r => r.Rate).Distinct().Order());
        Assert.Equal(MicEq.Types.Order(), rows.SelectMany(r => r.Bands.Where(b => b.Enabled)).Select(b => b.Type).Distinct().Order());
        foreach (Row row in rows)
        {
            Assert.Equal(64, row.Db.Length);
            var eq = new MicEq { Enabled = true, Bands = row.Bands };
            for (int i = 0; i < frequencies.Length; i++)
                Assert.True(Math.Abs(EqResponse.MagnitudeDb(eq, row.Rate, frequencies[i]) - row.Db[i]) < 0.01,
                    $"{row.Rate} Hz, {string.Join(", ", row.Bands.Where(b => b.Enabled))} at {frequencies[i]:0.##} Hz: {EqResponse.MagnitudeDb(eq, row.Rate, frequencies[i])} against {row.Db[i]}");
            // Off, the EQ does nothing; the bands still draw.
            Assert.Equal(0, EqResponse.MagnitudeDb(eq with { Enabled = false }, row.Rate, 1000));
            Assert.Equal(EqResponse.MagnitudeDb(eq, row.Rate, 1000), EqResponse.CascadeDb(row.Bands, row.Rate, 1000), 12);
        }
    }

    /// <summary>
    /// The high and low pass in the bilinear transform's tangent form, the
    /// way many EQs write them, against the cookbook's sine and cosine form,
    /// so the table does not rest on one way of writing the formulas.
    /// </summary>
    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void TheTangentFormOfThePassFiltersAgrees(int rate)
    {
        foreach (string type in new[] { MicEq.LowCut, MicEq.HighCut })
        foreach (double f0 in new[] { 20.0, 36, 120, 1000, 5000, 15049, 20000 })
        foreach (double q in new[] { 0.1, MicEq.DefaultQ, 2, 10 })
        {
            double k = Math.Tan(Math.PI * Math.Min(f0, 0.49 * rate) / rate);
            double norm = 1 / (1 + k / q + k * k);
            double a1 = 2 * (k * k - 1) * norm, a2 = (1 - k / q + k * k) * norm;
            EqCoefficients tangent = type == MicEq.HighCut
                ? new(k * k * norm, 2 * k * k * norm, k * k * norm, a1, a2)
                : new(norm, -2 * norm, norm, a1, a2);
            EqCoefficients cookbook = EqResponse.Coefficients(new EqBand { Type = type, Frequency = f0, Q = q }, rate);
            foreach (double f in new[] { 20.0, 100, 1000, 10000, 19000 })
                Assert.Equal(EqResponse.MagnitudeDb(tangent, rate, f), EqResponse.MagnitudeDb(cookbook, rate, f), 6);
        }
    }

    [Fact]
    public void TheCurvesHaveTheirTextbookLandmarks()
    {
        const int rate = 48000;
        double Db(string type, double f0, double gain, double q, double at)
            => EqResponse.MagnitudeDb(EqResponse.Coefficients(new EqBand { Type = type, Frequency = f0, Gain = gain, Q = q }, rate), rate, at);
        // A bell reaches its gain at its frequency.
        Assert.Equal(9.25, Db(MicEq.Peak, 1000, 9.25, 2, 1000), 6);
        Assert.Equal(-12, Db(MicEq.Peak, 310, -12, MicEq.DefaultQ, 310), 6);
        // A shelf is at half its gain at its frequency, and at its gain far away.
        Assert.Equal(6, Db(MicEq.LowShelf, 100, 12, MicEq.DefaultQ, 100), 6);
        Assert.Equal(12, Db(MicEq.LowShelf, 100, 12, MicEq.DefaultQ, 20) , 0);
        Assert.Equal(-3, Db(MicEq.HighShelf, 5000, -6, 3, 5000), 6);
        // A pass filter with Q 0.7071 is 3 dB down at its corner.
        Assert.Equal(-3.01, Db(MicEq.LowCut, 1000, 0, Math.Sqrt(0.5), 1000), 2);
        Assert.Equal(-3.01, Db(MicEq.HighCut, 1000, 0, Math.Sqrt(0.5), 1000), 2);
        // A notch is deep at its frequency, and flat away from it.
        Assert.True(Db(MicEq.Notch, 3000, 0, 10, 3000) < -100);
        Assert.Equal(0, Db(MicEq.Notch, 3000, 0, 10, 300), 2);
        // Gain means nothing to the gainless types.
        Assert.Equal(Db(MicEq.LowCut, 120, 0, 4, 100), Db(MicEq.LowCut, 120, 12, 4, 100), 12);
        Assert.Equal(Db(MicEq.Notch, 120, 0, 4, 100), Db(MicEq.Notch, 120, -12, 4, 100), 12);
        Assert.Throws<ArgumentException>(() => EqResponse.Coefficients(new EqBand { Type = "bandPass" }, rate));
    }
}
