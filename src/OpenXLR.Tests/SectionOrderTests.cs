using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>The saved section order as the window reads and changes it.</summary>
[Collection("xdg-config")]
public sealed class SectionOrderTests
{
    private static readonly string[] Usual = ["InputsTile", "HeadphonesTile", "MonitorTile", "ApplicationsTile", "SubmixerTile"];

    [Fact]
    public void ASavedOrderIsMadeWholeAndKeepsOnlyKnownSections()
    {
        Assert.Equal(Usual, MainWindow.CompleteSectionOrder(null));
        Assert.Equal(Usual, MainWindow.CompleteSectionOrder([]));
        Assert.Equal(["SubmixerTile", "MonitorTile", "InputsTile", "HeadphonesTile", "ApplicationsTile"],
            MainWindow.CompleteSectionOrder(["SubmixerTile", "Gone", "MonitorTile", "SubmixerTile"]));
    }

    [Fact]
    public void ADropLandsBeforeOrAfterItsTarget()
    {
        Assert.Equal(["SubmixerTile", "InputsTile", "HeadphonesTile", "MonitorTile", "ApplicationsTile"],
            MainWindow.PlaceSection(Usual, "SubmixerTile", "InputsTile", after: false));
        Assert.Equal(["HeadphonesTile", "MonitorTile", "InputsTile", "ApplicationsTile", "SubmixerTile"],
            MainWindow.PlaceSection(Usual, "InputsTile", "MonitorTile", after: true));
        Assert.Equal(Usual, MainWindow.PlaceSection(Usual, "InputsTile", "InputsTile", after: true));
        Assert.Equal(Usual, MainWindow.PlaceSection(Usual, "Gone", "InputsTile", after: true));
    }

    [Fact]
    public void ANullOrderInTheFileReadsAsNone()
    {
        string home = Directory.CreateTempSubdirectory("openxlr-sections-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", home);
            Directory.CreateDirectory(Path.Combine(home, "openxlr"));
            File.WriteAllText(Path.Combine(home, "openxlr", "ui.json"), """{"sectionOrder":null,"collapsedSections":null}""");
            UiSettings settings = UiSettings.Load();
            Assert.Empty(settings.SectionOrder);
            Assert.Empty(settings.CollapsedSections);
            Assert.Null((settings with { SectionOrder = ["SubmixerTile"] }).Save());
            Assert.Equal(["SubmixerTile"], UiSettings.Load().SectionOrder);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(home, true);
        }
    }

    [Fact]
    public void ASkinsSectionOrderRoundTripsAndANullOrBrokenOneReadsAsNone()
    {
        string home = Directory.CreateTempSubdirectory("openxlr-sections-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", home);
            string file = Path.Combine(home, "openxlr", "ui.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            File.WriteAllText(file, """{"skinSectionOrders":null}""");
            Assert.Empty(UiSettings.Load().SkinSectionOrders);

            File.WriteAllText(file, """
                {"sectionOrder":["MonitorTile"],"skinSectionOrders":[
                  null,
                  {"skin":null,"version":1,"order":["inputs"]},
                  {"skin":"","version":1,"order":["inputs"]},
                  {"skin":"broadcast","version":1,"order":null},
                  {"skin":"wide","version":1,"order":["submixer",null,"inputs"]},
                  {"skin":"broadcast","version":1,"order":["monitor"]}
                ]}
                """);
            UiSettings loaded = UiSettings.Load();
            Assert.Equal(["MonitorTile"], loaded.SectionOrder);
            Assert.Equal(["wide", "broadcast"], loaded.SkinSectionOrders.Select(e => e.Skin));
            Assert.Equal(["submixer", "inputs"], loaded.SkinSectionOrderFor("wide", 1)!.Order);
            Assert.Equal(["monitor"], loaded.SkinSectionOrderFor("broadcast", 1)!.Order);
            Assert.Null(loaded.SkinSectionOrderFor("broadcast", 2));

            UiSettings changed = loaded.WithSkinSectionOrder("broadcast", 1, ["submixer", "inputs"])
                .WithSkinSectionOrder("broadcast", 2, ["inputs"]);
            Assert.Null(changed.Save());
            UiSettings again = UiSettings.Load();
            Assert.Equal(["MonitorTile"], again.SectionOrder);
            Assert.Equal(["submixer", "inputs"], again.SkinSectionOrderFor("broadcast", 1)!.Order);
            Assert.Equal(["inputs"], again.SkinSectionOrderFor("broadcast", 2)!.Order);
            Assert.Contains("\"skinSectionOrders\"", File.ReadAllText(file), StringComparison.Ordinal);

            Assert.Null(again.WithSkinSectionOrder("broadcast", 1, null).Save());
            Assert.Null(UiSettings.Load().SkinSectionOrderFor("broadcast", 1));
            Assert.NotNull(UiSettings.Load().SkinSectionOrderFor("broadcast", 2));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(home, true);
        }
    }

    [Fact]
    public void OnlyTheMostRecentSkinArrangementsAreKept()
    {
        var settings = new UiSettings();
        for (int i = 0; i < UiSettings.MaxSkinSectionOrders + 5; i++)
            settings = settings.WithSkinSectionOrder("skin" + i, 1, ["submixer"]);
        Assert.Equal(UiSettings.MaxSkinSectionOrders, settings.SkinSectionOrders.Count);
        Assert.Null(settings.SkinSectionOrderFor("skin0", 1));
        Assert.NotNull(settings.SkinSectionOrderFor("skin" + (UiSettings.MaxSkinSectionOrders + 4), 1));

        // Arranging an old one again makes it the most recent.
        settings = settings.WithSkinSectionOrder("skin5", 1, ["inputs"]).WithSkinSectionOrder("new", 1, ["inputs"]);
        Assert.NotNull(settings.SkinSectionOrderFor("skin5", 1));
        Assert.Null(settings.SkinSectionOrderFor("skin6", 1));

        string[] tooLong = [.. Enumerable.Repeat("inputs", 40)];
        Assert.Equal(OpenXLR.UI.Skinning.SkinFormat.MaxLayoutChildren,
            settings.WithSkinSectionOrder("long", 1, tooLong).SkinSectionOrderFor("long", 1)!.Order.Count);
    }
}
