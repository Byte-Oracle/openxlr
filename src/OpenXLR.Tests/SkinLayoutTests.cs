using Avalonia.Media;
using OpenXLR.UI;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

/// <summary>
/// The optional layout block of a skin: what it may say in this version, that
/// it is taken whole or not at all, and that a refused layout never costs the
/// skin its colours or its controls. Like the rest of the format, none of
/// this needs a rendering platform.
/// </summary>
public sealed class SkinLayoutTests
{
    private const string Palette = """
        "controls": { "fader": "console" },
        "tokens": { "Ox.Card.Background": "#123456" }
        """;

    private static readonly string[] Classic = ["inputs", "headphones", "monitor", "applications", "submixer"];

    private static SkinReadResult Read(string layout) =>
        SkinReader.Read("test", "{\"schema\":1,\"name\":\"Test\"," + Palette + ",\"layout\":" + layout + "}",
            SkinOrigin.BuiltIn, null);

    private static string Region(string id) => "{\"kind\":\"region\",\"region\":\"" + id + "\"}";

    private static string Stack(string orientation, IEnumerable<string> regions, string extra = "") =>
        "{\"version\":1,\"main\":{\"kind\":\"stack\",\"orientation\":\"" + orientation + "\"" + extra
        + ",\"children\":[" + string.Join(",", regions.Select(Region)) + "]}}";

    /// <summary>The palette and controls of <see cref="Palette"/>, whatever happened to the layout.</summary>
    private static void PaletteKept(SkinReadResult result)
    {
        SkinPackage package = Assert.IsType<SkinPackage>(result.Package);
        Assert.Equal(Color.Parse("#123456"), Assert.IsType<SkinSolid>(package.Tokens["Ox.Card.Background"]).Color);
        Assert.Equal("console", package.Controls["fader"]);
    }

    /// <summary>One complaint, naming the path, and the classic arrangement kept.</summary>
    private static void Refused(string layout, string path)
    {
        SkinReadResult result = Read(layout);
        PaletteKept(result);
        Assert.Null(result.Package!.Layout);
        string error = Assert.Single(result.Errors);
        Assert.StartsWith(path + ": ", error, StringComparison.Ordinal);
        Assert.EndsWith(SkinLayoutReader.Consequence, error, StringComparison.Ordinal);
    }

    [Fact]
    public void ASkinWithoutALayoutHasNoneAndTheClassicDefaultsDescribeTheWindow()
    {
        SkinReadResult result = SkinReader.Read("test", "{\"schema\":1," + Palette + "}", SkinOrigin.BuiltIn, null);
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Null(result.Package!.Layout);
        Assert.Null(SkinPackage.Default.Layout);

        SkinLayout classic = SkinLayoutDefaults.Classic;
        Assert.Equal(SkinFormat.LayoutVersion, classic.Version);
        Assert.Equal(SkinLayoutOrientation.Vertical, classic.Main.Orientation);
        Assert.Equal(8, classic.Main.Spacing);
        Assert.Equal(Classic, classic.Main.RegionOrder);
        Assert.Equal(Classic, SkinLayoutRegions.All);
    }

    [Fact]
    public void EveryShippedSkinKeepsTheClassicArrangement()
    {
        foreach (SkinEntry entry in SkinCatalog.BuiltIn())
            Assert.Null(entry.Package.Layout);
    }

    [Fact]
    public void AVerticalLayoutReadsToItsOrderAndSpacing()
    {
        string[] order = ["submixer", "inputs", "monitor", "headphones", "applications"];
        SkinReadResult result = Read(Stack("vertical", order, ",\"spacing\":12"));
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        SkinLayout layout = result.Package!.Layout!;
        Assert.Equal(1, layout.Version);
        Assert.Equal(SkinLayoutOrientation.Vertical, layout.Main.Orientation);
        Assert.Equal(12, layout.Main.Spacing);
        Assert.Equal(order, layout.Main.RegionOrder);
        Assert.Equal(new SkinLayoutRegion("submixer"), layout.Main.Children[0]);
        PaletteKept(result);
    }

    [Fact]
    public void AHorizontalLayoutReadsWithTheClassicSpacingWhenItSetsNone()
    {
        SkinReadResult result = Read(Stack("horizontal", Classic));
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        SkinLayoutStack main = result.Package!.Layout!.Main;
        Assert.Equal(SkinLayoutOrientation.Horizontal, main.Orientation);
        Assert.Equal(SkinLayoutDefaults.Spacing, main.Spacing);
        Assert.Equal(Classic, main.RegionOrder);
        Assert.False(main.SameAs(SkinLayoutDefaults.Classic.Main));
        Assert.True(main.SameAs(main with { Children = [.. main.Children] }));
    }

    [Fact]
    public void ALayoutWithOnlyAVersionTakesTheClassicTree()
    {
        SkinReadResult result = Read("{\"version\":1}");
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.True(result.Package!.Layout!.Main.SameAs(SkinLayoutDefaults.Classic.Main));
    }

    [Fact]
    public void TheSpacingBoundsAreTheDocumentedOnes()
    {
        foreach (double spacing in new[] { SkinFormat.MinLayoutSpacing, SkinFormat.MaxLayoutSpacing, 2.5 })
        {
            SkinReadResult result = Read(Stack("vertical", Classic, ",\"spacing\":"
                + spacing.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.True(result.Ok, string.Join("; ", result.Errors));
            Assert.Equal(spacing, result.Package!.Layout!.Main.Spacing);
        }
        Refused(Stack("vertical", Classic, ",\"spacing\":-1"), "layout.main.spacing");
        Refused(Stack("vertical", Classic, ",\"spacing\":64.5"), "layout.main.spacing");
        Refused(Stack("vertical", Classic, ",\"spacing\":1e999"), "layout.main.spacing");
        Refused(Stack("vertical", Classic, ",\"spacing\":\"8\""), "layout.main.spacing");
    }

    [Fact]
    public void AnUnknownOrMissingVersionIsRefused()
    {
        Refused(Stack("vertical", Classic).Replace("\"version\":1", "\"version\":2"), "layout.version");
        Refused(Stack("vertical", Classic).Replace("\"version\":1", "\"version\":0"), "layout.version");
        Refused(Stack("vertical", Classic).Replace("\"version\":1", "\"version\":1.5"), "layout.version");
        Refused(Stack("vertical", Classic).Replace("\"version\":1", "\"version\":\"1\""), "layout.version");
        Refused(Stack("vertical", Classic).Replace("\"version\":1,", ""), "layout");

        // A newer version is said as such, before any property it adds.
        SkinReadResult future = Read("{\"version\":2,\"regions\":{}}");
        Assert.Contains("newer than this OpenXLR reads", Assert.Single(future.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void ALayoutThatIsNotAnObjectIsRefused()
    {
        Refused("[]", "layout");
        Refused("\"classic\"", "layout");
        Refused("{\"version\":1,\"main\":[]}", "layout.main");
    }

    [Fact]
    public void AnUnknownKindIsRefused()
    {
        Refused(Stack("vertical", Classic).Replace("\"kind\":\"stack\"", "\"kind\":\"grid\""), "layout.main.kind");
        Refused(Stack("vertical", Classic).Replace("\"kind\":\"stack\",", ""), "layout.main");
        Refused(Stack("vertical", Classic).Replace("\"kind\":\"stack\"", "\"kind\":\"region\""), "layout.main.kind");
        string dock = Stack("vertical", Classic).Replace("{\"kind\":\"region\",\"region\":\"monitor\"}",
            "{\"kind\":\"dock\",\"region\":\"monitor\"}");
        Refused(dock, "layout.main.children[2].kind");
    }

    [Fact]
    public void AnUnknownOrMissingPropertyIsRefused()
    {
        Refused(Stack("vertical", Classic).Replace("{\"version\":1,", "{\"version\":1,\"regions\":{},"), "layout.regions");
        Refused(Stack("vertical", Classic, ",\"id\":\"desktop\""), "layout.main.id");
        Refused(Stack("vertical", Classic).Replace("\"region\":\"inputs\"}", "\"region\":\"inputs\",\"row\":0}"),
            "layout.main.children[0].row");
        Refused(Stack("vertical", Classic).Replace("\"orientation\":\"vertical\",", ""), "layout.main");
        Refused(Stack("diagonal", Classic), "layout.main.orientation");
        Refused("{\"version\":1,\"main\":{\"kind\":\"stack\",\"orientation\":\"vertical\"}}", "layout.main");
        Refused(Stack("vertical", Classic).Replace("{\"kind\":\"region\",\"region\":\"inputs\"}", "{\"kind\":\"region\"}"),
            "layout.main.children[0]");
    }

    [Fact]
    public void ARepeatedKeyIsRefusedWhereverItIs()
    {
        Refused(Stack("vertical", Classic).Replace("{\"version\":1,", "{\"version\":1,\"version\":1,"), "layout.version");
        Refused(Stack("vertical", Classic, ",\"orientation\":\"horizontal\""), "layout.main.orientation");
        Refused(Stack("vertical", Classic).Replace("\"region\":\"inputs\"}", "\"region\":\"inputs\",\"region\":\"monitor\"}"),
            "layout.main.children[0].region");

        SkinReadResult twice = SkinReader.Read("test", "{\"schema\":1," + Palette + ",\"layout\":{\"version\":1},\"layout\":{\"version\":1}}",
            SkinOrigin.BuiltIn, null);
        PaletteKept(twice);
        Assert.Null(twice.Package!.Layout);
        Assert.StartsWith("layout: ", Assert.Single(twice.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRegionIsPlacedExactlyOnce()
    {
        Refused(Stack("vertical", ["inputs", "headphones", "monitor", "applications"]), "layout.main.children");
        Refused(Stack("vertical", [.. Classic, "inputs"]), "layout.main.children[5].region");
        Refused(Stack("vertical", ["inputs", "headphones", "monitor", "applications", "mixer"]), "layout.main.children[4].region");
        Refused(Stack("vertical", ["header", .. Classic]), "layout.main.children[0].region");
        Refused(Stack("vertical", []), "layout.main.children");
        Refused(Stack("vertical", Classic).Replace("\"region\":\"inputs\"", "\"region\":3"), "layout.main.children[0].region");
    }

    [Fact]
    public void AContainerHoldsAtMostSixteenChildrenAndNothingNests()
    {
        string seventeen = "{\"version\":1,\"main\":{\"kind\":\"stack\",\"orientation\":\"vertical\",\"children\":["
            + string.Join(",", Enumerable.Repeat(Region("inputs"), SkinFormat.MaxLayoutChildren + 1)) + "]}}";
        Refused(seventeen, "layout.main.children");
        Assert.Contains($"at most {SkinFormat.MaxLayoutChildren} children", Assert.Single(Read(seventeen).Errors),
            StringComparison.Ordinal);

        string nested = Stack("vertical", Classic).Replace(Region("submixer"),
            "{\"kind\":\"stack\",\"orientation\":\"horizontal\",\"children\":[" + Region("submixer") + "]}");
        Refused(nested, "layout.main.children[4].kind");
        Refused(Stack("vertical", Classic).Replace(Region("monitor"), "\"monitor\""), "layout.main.children[2]");
    }

    [Fact]
    public void AFaultInTheLayoutLeavesTheOtherComplaintsAsTheyWere()
    {
        SkinReadResult result = SkinReader.Read("test",
            "{\"schema\":1,\"tokens\":{\"Ox.Card.Background\":\"#123456\",\"Ox.Nonsense\":\"#fff\"},"
            + "\"layout\":{\"version\":1,\"main\":{\"kind\":\"grid\"}}}", SkinOrigin.BuiltIn, null);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.StartsWith("\"Ox.Nonsense\"", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.StartsWith("layout.main.kind: ", StringComparison.Ordinal));
        Assert.Single(result.Package!.Tokens);
    }

    [Fact]
    public void AControlCharacterInAPropertyNameIsNotRepeatedInTheMessage()
    {
        SkinReadResult result = Read(Stack("vertical", Classic, ",\"b\\u0007ad\":1"));
        string error = Assert.Single(result.Errors);
        Assert.StartsWith("layout.main.bad: ", error, StringComparison.Ordinal);
        Assert.DoesNotContain(error, char.IsControl);
    }
}
