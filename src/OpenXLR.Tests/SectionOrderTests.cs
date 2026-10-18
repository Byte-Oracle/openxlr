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
    public void ASkinsArrangementRoundTripsAndANullOrBrokenOneReadsAsNone()
    {
        string home = Directory.CreateTempSubdirectory("openxlr-sections-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", home);
            string file = Path.Combine(home, "openxlr", "ui.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            File.WriteAllText(file, """{"skinArrangements":null,"skinLayoutsSeen":null}""");
            Assert.Empty(UiSettings.Load().SkinArrangements);
            Assert.Empty(UiSettings.Load().SkinLayoutsSeen);

            File.WriteAllText(file, """
                {"sectionOrder":["MonitorTile"],"skinArrangements":[
                  null,
                  {"skin":null,"version":1,"slots":{"main:0":"inputs"}},
                  {"skin":"","version":1,"slots":{"main:0":"inputs"}},
                  {"skin":"broadcast","version":1,"slots":null,"shown":null},
                  {"skin":"wide","version":1,"slots":{"row:0":"submixer","row:1":null,"row:4":"inputs"},"shown":["monitor",null,"monitor"]},
                  {"skin":"broadcast","version":1,"slots":{"desktop:1,0":"monitor"}}
                ],"skinLayoutsSeen":[null,{"skin":"wide","version":1},{"skin":"","version":1}]}
                """);
            UiSettings loaded = UiSettings.Load();
            Assert.Equal(["MonitorTile"], loaded.SectionOrder);
            Assert.Equal(["wide", "broadcast"], loaded.SkinArrangements.Select(e => e.Skin));
            Assert.Equal(new Dictionary<string, string> { ["row:0"] = "submixer", ["row:4"] = "inputs" },
                loaded.SkinArrangementFor("wide", 1)!.Slots);
            Assert.Equal(["monitor"], loaded.SkinArrangementFor("wide", 1)!.Shown);
            Assert.Equal(new Dictionary<string, string> { ["desktop:1,0"] = "monitor" }, loaded.SkinArrangementFor("broadcast", 1)!.Slots);
            Assert.Empty(loaded.SkinArrangementFor("broadcast", 1)!.Shown);
            Assert.Null(loaded.SkinArrangementFor("broadcast", 2));
            Assert.True(loaded.HasSeenSkinLayout("wide", 1));
            Assert.False(loaded.HasSeenSkinLayout("wide", 2));
            Assert.Single(loaded.SkinLayoutsSeen);

            UiSettings changed = loaded
                .WithSkinArrangement("broadcast", 1, new Dictionary<string, string> { ["desktop:1,0"] = "inputs", ["desktop:1,1"] = "monitor" }, ["applications"])
                .WithSkinArrangement("broadcast", 2, null, ["inputs"])
                .WithSkinLayoutSeen("broadcast", 1);
            Assert.Null(changed.Save());
            string text = File.ReadAllText(file);
            Assert.Contains("\"skinArrangements\"", text, StringComparison.Ordinal);
            Assert.Contains("\"desktop:1,0\": \"inputs\"", text, StringComparison.Ordinal);
            UiSettings again = UiSettings.Load();
            Assert.Equal(["MonitorTile"], again.SectionOrder);
            Assert.Equal("inputs", again.SkinArrangementFor("broadcast", 1)!.Slots["desktop:1,0"]);
            Assert.Equal(["applications"], again.SkinArrangementFor("broadcast", 1)!.Shown);
            Assert.Equal(["inputs"], again.SkinArrangementFor("broadcast", 2)!.Shown);
            Assert.True(again.HasSeenSkinLayout("broadcast", 1));

            // Nothing moved and nothing shown is no entry at all.
            Assert.Null(again.WithSkinArrangement("broadcast", 1, null, null).Save());
            Assert.Null(UiSettings.Load().SkinArrangementFor("broadcast", 1));
            Assert.Null(UiSettings.Load().WithSkinArrangement("broadcast", 2, new Dictionary<string, string>(), []).SkinArrangementFor("broadcast", 2));
            Assert.NotNull(UiSettings.Load().SkinArrangementFor("broadcast", 2));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(home, true);
        }
    }

    /// <summary>
    /// The per-skin section orders of the build before arrangements are not
    /// read, and go at the next save; every other unknown key is kept.
    /// </summary>
    [Fact]
    public void AnOldSkinSectionOrderIsIgnored()
    {
        string home = Directory.CreateTempSubdirectory("openxlr-sections-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", home);
            string file = Path.Combine(home, "openxlr", "ui.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, """
                {"sectionOrder":["SubmixerTile"],"skinSectionOrders":[{"skin":"row","version":1,"order":["monitor"]}],"fromNewer":true}
                """);
            UiSettings loaded = UiSettings.Load();
            Assert.Empty(loaded.SkinArrangements);
            Assert.Equal(["SubmixerTile"], loaded.SectionOrder);
            Assert.Equal(["fromNewer"], loaded.UnknownKeys!.Keys);
            Assert.Null(loaded.Save());
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("skinSectionOrders", text, StringComparison.Ordinal);
            Assert.Contains("\"fromNewer\": true", text, StringComparison.Ordinal);
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
        var one = new Dictionary<string, string> { ["main:0"] = "submixer" };
        for (int i = 0; i < UiSettings.MaxSkinArrangements + 5; i++)
            settings = settings.WithSkinArrangement("skin" + i, 1, one, null).WithSkinLayoutSeen("skin" + i, 1);
        Assert.Equal(UiSettings.MaxSkinArrangements, settings.SkinArrangements.Count);
        Assert.Equal(UiSettings.MaxSkinArrangements, settings.SkinLayoutsSeen.Count);
        Assert.Null(settings.SkinArrangementFor("skin0", 1));
        Assert.False(settings.HasSeenSkinLayout("skin0", 1));
        Assert.NotNull(settings.SkinArrangementFor("skin" + (UiSettings.MaxSkinArrangements + 4), 1));

        // Arranging an old one again makes it the most recent.
        settings = settings.WithSkinArrangement("skin5", 1, one, null).WithSkinArrangement("new", 1, one, null);
        Assert.NotNull(settings.SkinArrangementFor("skin5", 1));
        Assert.Null(settings.SkinArrangementFor("skin6", 1));

        var many = Enumerable.Range(0, 80).ToDictionary(i => "main:" + i, _ => "inputs");
        string[] shown = [.. Enumerable.Range(0, 10).Select(i => "region" + i)];
        SkinArrangement capped = settings.WithSkinArrangement("long", 1, many, shown).SkinArrangementFor("long", 1)!;
        Assert.Equal(OpenXLR.UI.Skinning.SkinFormat.MaxLayoutNodes, capped.Slots.Count);
        Assert.Equal(OpenXLR.UI.Skinning.SkinLayoutRegions.All.Count, capped.Shown.Count);
    }
}
