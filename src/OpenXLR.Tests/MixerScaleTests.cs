using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>The size of the mixer's own window: the choices, the Fit search and the saved choice.</summary>
[Collection("xdg-config")]
public sealed class MixerScaleTests
{
    [Fact]
    public void ChoicesReadAsTheirFactorAndAnythingElseAsTheSkinsOwnSize()
    {
        Assert.Equal([MixerScale.Fit, "100", "125", "150", "175", "200"], MixerScale.Choices);
        Assert.Equal(1, MixerScale.Factor(null));
        Assert.Equal(1, MixerScale.Factor(MixerScale.Fit));
        Assert.Equal(1, MixerScale.Factor("100"));
        Assert.Equal(1.25, MixerScale.Factor("125"));
        Assert.Equal(2, MixerScale.Factor("200"));
        foreach (string? other in new[] { null, "", "90", "130", "FIT", "150%", "1.5" })
        {
            Assert.Null(MixerScale.Known(other));
            Assert.Equal(1, MixerScale.Factor(other));
        }
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.04, 1.0)]
    [InlineData(1.05, 1.05)]
    [InlineData(1.37, 1.35)]
    [InlineData(2.0, 2.0)]
    [InlineData(2.99, 2.95)]
    [InlineData(10.0, 3.0)]
    public void FitIsTheLargestStepThatFitsAndNeverUnderTheSkinsOwnSize(double limit, double expected)
    {
        var tried = new List<double>();
        double found = MixerScale.LargestFit(scale =>
        {
            tried.Add(scale);
            return scale <= limit + 1e-9;
        });
        Assert.Equal(expected, found, 10);
        // A handful of tries, never the floor itself, every one a whole step.
        Assert.InRange(tried.Count, 1, 6);
        Assert.DoesNotContain(1.0, tried);
        Assert.All(tried, scale => Assert.Equal(Math.Round(scale * 100 / MixerScale.FitStepPercent),
            scale * 100 / MixerScale.FitStepPercent, 9));
        // Nothing fits above 1: the floor, which a window too small scrolls from.
        Assert.Equal(1, MixerScale.LargestFit(_ => false));
    }

    [Fact]
    public void TheSavedSizeRoundTripsAndAnythingBrokenReadsAsNone()
    {
        string home = Directory.CreateTempSubdirectory("openxlr-mixer-scale-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", home);
            string file = Path.Combine(home, "openxlr", "ui.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            Assert.Null(new UiSettings().MixerScale);
            foreach (string text in new[] { "{}", """{"mixerScale":null}""", """{"mixerScale":150}""", """{"mixerScale":"130"}""",
                         """{"mixerScale":"Fit"}""", """{"mixerScale":["fit"]}""", """{"mixerScale":{"scale":"fit"}}""" })
            {
                File.WriteAllText(file, text);
                UiSettings read = UiSettings.Load(out string? problem);
                Assert.Null(problem);
                Assert.Null(read.MixerScale);
            }
            // A damaged value costs only itself.
            File.WriteAllText(file, """{"mixerScale":2,"sectionOrder":["MonitorTile"]}""");
            Assert.Equal(["MonitorTile"], UiSettings.Load().SectionOrder);

            foreach (string choice in MixerScale.Choices)
            {
                Assert.Null((UiSettings.Load() with { MixerScale = choice }).Save());
                Assert.Contains($"\"mixerScale\": \"{choice}\"", File.ReadAllText(file), StringComparison.Ordinal);
                Assert.Equal(choice, UiSettings.Load().MixerScale);
            }
            // A value no picker offers is not written.
            Assert.Null((UiSettings.Load() with { MixerScale = "130" }).Save());
            Assert.Contains("\"mixerScale\": null", File.ReadAllText(file), StringComparison.Ordinal);
            Assert.Null(UiSettings.Load().MixerScale);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(home, true);
        }
    }
}
