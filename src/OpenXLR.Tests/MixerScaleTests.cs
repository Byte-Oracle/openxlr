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
        Assert.All(MixerScale.Choices, choice => Assert.True(MixerScale.Listed(choice)));
        Assert.Null(MixerScale.Percent(MixerScale.Fit));
        // Out of range, not a whole number in plain digits, or not a size at all: the skin's own size.
        foreach (string? other in new[] { null, "", "0", "90", "99", "301", "1000", "99999999999", "-150", "+150", " 150", "150 ",
                     "FIT", "Fit", "custom", "150%", "1.5", "137.5", "1e2", "١٥٠" })
        {
            Assert.Null(MixerScale.Known(other));
            Assert.Null(MixerScale.Percent(other));
            Assert.False(MixerScale.Listed(other));
            Assert.Equal(1, MixerScale.Factor(other));
        }
    }

    [Theory]
    [InlineData("100", 100, true)]
    [InlineData("101", 101, false)]
    [InlineData("130", 130, false)]
    [InlineData("137", 137, false)]
    [InlineData("150", 150, true)]
    [InlineData("0150", 150, true)]
    [InlineData("250", 250, false)]
    [InlineData("300", 300, false)]
    public void AnyWholePercentInRangeIsASizeAndOnlyTheFixedOnesAreListed(string choice, int percent, bool listed)
    {
        Assert.Equal(percent.ToString(System.Globalization.CultureInfo.InvariantCulture), MixerScale.Known(choice));
        Assert.Equal(percent, MixerScale.Percent(choice));
        Assert.Equal(percent / 100.0, MixerScale.Factor(choice), 10);
        Assert.Equal(listed, MixerScale.Listed(choice));
    }

    [Theory]
    [InlineData(-5, 100)]
    [InlineData(0, 100)]
    [InlineData(1, 100)]
    [InlineData(99.4, 100)]
    [InlineData(100, 100)]
    [InlineData(137, 137)]
    [InlineData(137.4, 137)]
    [InlineData(137.5, 138)]
    [InlineData(300, 300)]
    [InlineData(300.4, 300)]
    [InlineData(1370, 300)]
    public void ATypedSizeIsRoundedToAWholePercentAndHeldToTheRange(double typed, int expected)
    {
        Assert.Equal(expected, MixerScale.Clamp((decimal)typed));
        // The box goes as far as Fit does.
        Assert.Equal(MixerScale.FitMaximumPercent, MixerScale.MaximumPercent);
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
            foreach (string text in new[] { "{}", """{"mixerScale":null}""", """{"mixerScale":150}""", """{"mixerScale":"90"}""",
                         """{"mixerScale":"301"}""", """{"mixerScale":"137.5"}""", """{"mixerScale":"130%"}""",
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
            // A custom size is kept as its percentage and read back as one.
            foreach (string custom in new[] { "101", "137", "300" })
            {
                Assert.Null((UiSettings.Load() with { MixerScale = custom }).Save());
                Assert.Contains($"\"mixerScale\": \"{custom}\"", File.ReadAllText(file), StringComparison.Ordinal);
                Assert.Equal(custom, UiSettings.Load().MixerScale);
            }
            File.WriteAllText(file, """{"mixerScale":"0137"}""");
            Assert.Equal("137", UiSettings.Load().MixerScale);
            // A value the picker and the box never offer is not written.
            foreach (string never in new[] { "90", "301", "137.5", "custom" })
            {
                Assert.Null((UiSettings.Load() with { MixerScale = never }).Save());
                Assert.Contains("\"mixerScale\": null", File.ReadAllText(file), StringComparison.Ordinal);
                Assert.Null(UiSettings.Load().MixerScale);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(home, true);
        }
    }
}
