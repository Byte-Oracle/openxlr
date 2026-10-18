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

    private static string Region(string id, string extra = "") => "{\"kind\":\"region\",\"region\":\"" + id + "\"" + extra + "}";

    private static string Stack(string orientation, IEnumerable<string> regions, string extra = "") =>
        "{\"version\":1,\"main\":" + StackNode("main", orientation, regions.Select(r => Region(r)), extra) + "}";

    private static string StackNode(string id, string orientation, IEnumerable<string> children, string extra = "") =>
        "{\"kind\":\"stack\",\"id\":\"" + id + "\",\"orientation\":\"" + orientation + "\"" + extra
        + ",\"children\":[" + string.Join(",", children) + "]}";

    private static string WithMain(string node) => "{\"version\":1,\"main\":" + node + "}";

    /// <summary>
    /// A two-column grid: the header across the top, INPUTS and the
    /// submixer side by side, the other three under them.
    /// </summary>
    private const string GridLayout = """
        {"version":1,"main":{"kind":"grid","id":"desktop","spacing":12,
          "columns":[{"size":280},{"star":2}],
          "rows":["auto",{"star":1},"auto","auto"],
          "children":[
            {"kind":"region","region":"header","row":0,"column":0,"columnSpan":2},
            {"kind":"region","region":"inputs","row":1,"column":0},
            {"kind":"region","region":"submixer","row":1,"column":1},
            {"kind":"region","region":"headphones","row":2,"column":0},
            {"kind":"region","region":"monitor","row":2,"column":1},
            {"kind":"region","region":"applications","row":3,"column":0,"columnSpan":2}]}}
        """;

    private const string DockLayout = """
        {"version":1,"main":{"kind":"dock","id":"desktop","spacing":10,"children":[
          {"kind":"region","region":"header","dock":"bottom"},
          {"kind":"region","region":"inputs","dock":"left"},
          {"kind":"stack","id":"side","orientation":"vertical","dock":"right","children":[
            {"kind":"region","region":"headphones"},{"kind":"region","region":"monitor"}]},
          {"kind":"region","region":"applications","dock":"top"},
          {"kind":"region","region":"submixer","dock":"fill"}]}}
        """;

    /// <summary>The palette and controls of <see cref="Palette"/>, whatever happened to the layout.</summary>
    private static void PaletteKept(SkinReadResult result)
    {
        SkinPackage package = Assert.IsType<SkinPackage>(result.Package);
        Assert.Equal(Color.Parse("#123456"), Assert.IsType<SkinSolid>(package.Tokens["Ox.Card.Background"]).Color);
        Assert.Equal("console", package.Controls["fader"]);
    }

    /// <summary>One complaint, naming the path, and the classic arrangement kept.</summary>
    private static string Refused(string layout, string path)
    {
        SkinReadResult result = Read(layout);
        PaletteKept(result);
        Assert.Null(result.Package!.Layout);
        string error = Assert.Single(result.Errors);
        Assert.StartsWith(path + ": ", error, StringComparison.Ordinal);
        Assert.EndsWith(SkinLayoutReader.Consequence, error, StringComparison.Ordinal);
        return error;
    }

    private static SkinLayout Taken(string layout)
    {
        SkinReadResult result = Read(layout);
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        PaletteKept(result);
        return Assert.IsType<SkinLayout>(result.Package!.Layout);
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
        SkinLayoutStack main = Assert.IsType<SkinLayoutStack>(classic.Main);
        Assert.Equal(SkinLayoutDefaults.ClassicId, main.Id);
        Assert.Equal(SkinLayoutOrientation.Vertical, main.Orientation);
        Assert.Equal(8, main.Spacing);
        Assert.Equal(Classic, main.RegionOrder);
        Assert.False(classic.PlacesHeader);
        Assert.Equal(Classic, SkinLayoutRegions.Sections);
        Assert.Equal(["header", .. Classic], SkinLayoutRegions.All);
        Assert.Equal(SkinHeaderActions.All, classic.Header.Row);
        Assert.Empty(classic.Header.Overflow);
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
        SkinLayout layout = Taken(Stack("vertical", order, ",\"spacing\":12"));
        Assert.Equal(1, layout.Version);
        SkinLayoutStack main = Assert.IsType<SkinLayoutStack>(layout.Main);
        Assert.Equal("main", main.Id);
        Assert.Equal(SkinLayoutOrientation.Vertical, main.Orientation);
        Assert.Equal(12, main.Spacing);
        Assert.Equal(order, main.RegionOrder);
        Assert.Equal(new SkinLayoutRegion("submixer"), main.Children[0].Node);
    }

    [Fact]
    public void AHorizontalLayoutReadsWithTheClassicSpacingWhenItSetsNone()
    {
        SkinLayoutStack main = Assert.IsType<SkinLayoutStack>(Taken(Stack("horizontal", Classic)).Main);
        Assert.Equal(SkinLayoutOrientation.Horizontal, main.Orientation);
        Assert.Equal(SkinLayoutDefaults.Spacing, main.Spacing);
        Assert.Equal(Classic, main.RegionOrder);
        Assert.False(main.SameAs(SkinLayoutDefaults.Classic.Main));
        Assert.True(main.SameAs(main with { Children = [.. main.Children] }));
    }

    /// <summary>
    /// The milestone 1 shape, a stack of the five sections with no header in
    /// it, still reads; the header then heads the window above the tree.
    /// </summary>
    [Fact]
    public void ATreeWithoutTheHeaderLeavesTheHeaderAboveIt()
    {
        SkinLayout layout = Taken(Stack("horizontal", Classic));
        Assert.False(layout.PlacesHeader);
        Assert.DoesNotContain("header", layout.Main.Regions());
        Assert.True(Taken(GridLayout).PlacesHeader);
    }

    [Fact]
    public void ALayoutWithOnlyAVersionTakesTheClassicTree()
    {
        SkinLayout layout = Taken("{\"version\":1}");
        Assert.True(layout.Main.SameAs(SkinLayoutDefaults.Classic.Main));
        Assert.Empty(layout.Regions);
        Assert.Same(SkinHeaderOptions.Default, layout.Header);
    }

    [Fact]
    public void AGridReadsToItsTracksCellsAndSpans()
    {
        SkinLayoutGrid grid = Assert.IsType<SkinLayoutGrid>(Taken(GridLayout).Main);
        Assert.Equal("desktop", grid.Id);
        Assert.Equal(12, grid.Spacing);
        Assert.Equal([new SkinLayoutTrack(SkinTrackUnit.Pixel, 280), new SkinLayoutTrack(SkinTrackUnit.Star, 2)], grid.Columns);
        Assert.Equal([SkinLayoutTrack.Auto, new SkinLayoutTrack(SkinTrackUnit.Star, 1), SkinLayoutTrack.Auto, SkinLayoutTrack.Auto], grid.Rows);
        SkinLayoutChild header = grid.Children[0];
        Assert.Equal(new SkinLayoutRegion("header"), header.Node);
        Assert.Equal((0, 0, 1, 2), (header.Row, header.Column, header.RowSpan, header.ColumnSpan));
        SkinLayoutChild monitor = grid.Children.Single(c => c.Node == new SkinLayoutRegion("monitor"));
        Assert.Equal((2, 1, 1, 1), (monitor.Row, monitor.Column, monitor.RowSpan, monitor.ColumnSpan));
        // Placement, and focus, goes by row and then by column.
        Assert.Equal(["header", "inputs", "submixer", "headphones", "monitor", "applications"], grid.RegionOrder);
    }

    [Fact]
    public void ADockReadsToItsEdgesInOrderWithANestedStack()
    {
        SkinLayout layout = Taken(DockLayout);
        SkinLayoutDock dock = Assert.IsType<SkinLayoutDock>(layout.Main);
        Assert.Equal(10, dock.Spacing);
        Assert.Equal([SkinLayoutEdge.Bottom, SkinLayoutEdge.Left, SkinLayoutEdge.Right, SkinLayoutEdge.Top, SkinLayoutEdge.Fill],
            dock.Children.Select(c => c.Edge));
        SkinLayoutStack side = Assert.IsType<SkinLayoutStack>(dock.Children[2].Node);
        Assert.Equal("side", side.Id);
        Assert.Equal(["headphones", "monitor"], side.RegionOrder);
        Assert.Equal(["header", "inputs", "headphones", "monitor", "applications", "submixer"], layout.Main.Regions());
        Assert.Equal(["desktop", "side"], layout.Main.Containers().Select(c => c.Id));
    }

    [Fact]
    public void ContainersNestFourDeep()
    {
        string deep = WithMain(StackNode("a", "vertical", [
            Region("header"),
            StackNode("b", "horizontal", [
                Region("inputs"),
                "{\"kind\":\"grid\",\"id\":\"c\",\"rows\":[\"auto\",\"auto\"],\"columns\":[\"auto\"],\"children\":["
                + Region("headphones", ",\"row\":0,\"column\":0")
                + ",{\"kind\":\"dock\",\"id\":\"d\",\"row\":1,\"column\":0,\"children\":["
                + Region("monitor", ",\"dock\":\"top\"") + "," + Region("applications", ",\"dock\":\"fill\"") + "]}]}"]),
            Region("submixer")]));
        SkinLayout layout = Taken(deep);
        Assert.Equal(["a", "b", "c", "d"], layout.Main.Containers().Select(c => c.Id));

        string tooDeep = deep.Replace(Region("applications", ",\"dock\":\"fill\""),
            StackNode("e", "vertical", [Region("applications")], ",\"dock\":\"fill\""));
        string error = Refused(tooDeep, "layout.main.children[1].children[1].children[1].children[1]");
        Assert.Contains($"at most {SkinFormat.MaxLayoutDepth} deep", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RegionOptionsReadToTheirValues()
    {
        SkinLayout layout = Taken("""
            {"version":1,"regions":{
              "applications":{"visibility":"hidden"},
              "headphones":{"visibility":"collapsed","height":180},
              "inputs":{"width":720.5},
              "header":{"actions":["flow","profiles"],"overflow":["restart-daemon","about"]}}}
            """);
        Assert.Equal(SkinRegionVisibility.Hidden, layout.OptionsFor("applications").Visibility);
        Assert.Equal(new SkinRegionOptions(SkinRegionVisibility.Collapsed, null, 180), layout.OptionsFor("headphones"));
        Assert.Equal(720.5, layout.OptionsFor("inputs").Width);
        Assert.Same(SkinRegionOptions.Default, layout.OptionsFor("submixer"));
        Assert.Equal(["flow", "profiles"], layout.Header.Actions);
        Assert.Equal(["restart-daemon", "about"], layout.Header.Overflow);
        // The row: the listed actions, then the ones named nowhere in their usual order.
        Assert.Equal(["flow", "profiles", "arrange", "version"], layout.Header.Row);
        Assert.True(layout.Main.SameAs(SkinLayoutDefaults.Classic.Main));
        Assert.NotEqual(SkinLayoutDefaults.Classic.Description(), layout.Description());
    }

    [Fact]
    public void TheSpacingBoundsAreTheDocumentedOnes()
    {
        foreach (double spacing in new[] { SkinFormat.MinLayoutSpacing, SkinFormat.MaxLayoutSpacing, 2.5 })
            Assert.Equal(spacing, Taken(Stack("vertical", Classic, ",\"spacing\":"
                + spacing.ToString(System.Globalization.CultureInfo.InvariantCulture))).Main.Spacing);
        Refused(Stack("vertical", Classic, ",\"spacing\":-1"), "layout.main.spacing");
        Refused(Stack("vertical", Classic, ",\"spacing\":64.5"), "layout.main.spacing");
        Refused(Stack("vertical", Classic, ",\"spacing\":1e999"), "layout.main.spacing");
        Refused(Stack("vertical", Classic, ",\"spacing\":\"8\""), "layout.main.spacing");
        Refused(GridLayout.Replace("\"spacing\":12", "\"spacing\":65"), "layout.main.spacing");
        Refused(DockLayout.Replace("\"spacing\":10", "\"spacing\":-0.5"), "layout.main.spacing");
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
        SkinReadResult future = Read("{\"version\":2,\"mixer\":{}}");
        Assert.Contains("newer than this OpenXLR reads", Assert.Single(future.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void ALayoutThatIsNotAnObjectIsRefused()
    {
        Refused("[]", "layout");
        Refused("\"classic\"", "layout");
        Refused("{\"version\":1,\"main\":[]}", "layout.main");
        Refused("{\"version\":1,\"regions\":[]}", "layout.regions");
        Refused("{\"version\":1,\"regions\":{\"inputs\":\"hidden\"}}", "layout.regions.inputs");
    }

    [Fact]
    public void AnUnknownKindIsRefused()
    {
        Refused(Stack("vertical", Classic).Replace("\"kind\":\"stack\"", "\"kind\":\"canvas\""), "layout.main.kind");
        Refused(Stack("vertical", Classic).Replace("\"kind\":\"stack\",", ""), "layout.main");
        Refused(Stack("vertical", Classic).Replace("\"kind\":\"stack\"", "\"kind\":\"region\""), "layout.main.kind");
        Refused(Stack("vertical", Classic).Replace("\"kind\":\"stack\"", "\"kind\":7"), "layout.main.kind");
        string widget = Stack("vertical", Classic).Replace(Region("monitor"), "{\"kind\":\"button\",\"region\":\"monitor\"}");
        Refused(widget, "layout.main.children[2].kind");
    }

    [Fact]
    public void AMissingOrBadOrRepeatedIdIsRefused()
    {
        Refused(Stack("vertical", Classic).Replace("\"id\":\"main\",", ""), "layout.main");
        foreach (string id in new[] { "", "Main", "has space", "under_score", "ünïcode", new string('a', SkinFormat.MaxLayoutIdLength + 1) })
            Refused(Stack("vertical", Classic).Replace("\"id\":\"main\"", "\"id\":\"" + id + "\""), "layout.main.id");
        Refused(Stack("vertical", Classic).Replace("\"id\":\"main\"", "\"id\":3"), "layout.main.id");
        Taken(Stack("vertical", Classic).Replace("\"id\":\"main\"", "\"id\":\"" + new string('a', SkinFormat.MaxLayoutIdLength) + "\""));
        Taken(Stack("vertical", Classic).Replace("\"id\":\"main\"", "\"id\":\"left-2\""));

        string twice = DockLayout.Replace("\"id\":\"side\"", "\"id\":\"desktop\"");
        Assert.Contains("already the id of layout.main", Refused(twice, "layout.main.children[2].id"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownOrMissingPropertyIsRefused()
    {
        Refused(Stack("vertical", Classic).Replace("{\"version\":1,", "{\"version\":1,\"mixer\":{},"), "layout.mixer");
        Refused(Stack("vertical", Classic, ",\"rows\":[\"auto\"]"), "layout.main.rows");
        Refused(Stack("vertical", Classic).Replace(Region("inputs"), Region("inputs", ",\"row\":0")),
            "layout.main.children[0].row");
        Refused(Stack("vertical", Classic).Replace(Region("inputs"), Region("inputs", ",\"dock\":\"top\"")),
            "layout.main.children[0].dock");
        Refused(Stack("vertical", Classic).Replace(Region("inputs"), Region("inputs", ",\"binding\":\"Gain\"")),
            "layout.main.children[0].binding");
        Refused(Stack("vertical", Classic).Replace("\"orientation\":\"vertical\",", ""), "layout.main");
        Refused(Stack("diagonal", Classic), "layout.main.orientation");
        Refused("{\"version\":1,\"main\":{\"kind\":\"stack\",\"id\":\"main\",\"orientation\":\"vertical\"}}", "layout.main");
        Refused(Stack("vertical", Classic).Replace(Region("inputs"), "{\"kind\":\"region\"}"), "layout.main.children[0]");
        // A dock child takes an edge, not a cell; a grid child a cell, not an edge.
        Refused(DockLayout.Replace("\"dock\":\"left\"", "\"row\":0"), "layout.main.children[1].row");
        Refused(GridLayout.Replace("\"row\":1,\"column\":0}", "\"row\":1,\"column\":0,\"dock\":\"left\"}"),
            "layout.main.children[1].dock");
        Refused(GridLayout.Replace("\"columns\"", "\"cols\""), "layout.main.cols");
    }

    [Fact]
    public void ARepeatedKeyIsRefusedWhereverItIs()
    {
        Refused(Stack("vertical", Classic).Replace("{\"version\":1,", "{\"version\":1,\"version\":1,"), "layout.version");
        Refused(Stack("vertical", Classic, ",\"orientation\":\"horizontal\""), "layout.main.orientation");
        Refused(Stack("vertical", Classic).Replace("\"region\":\"inputs\"}", "\"region\":\"inputs\",\"region\":\"monitor\"}"),
            "layout.main.children[0].region");
        Refused("{\"version\":1,\"regions\":{\"inputs\":{},\"inputs\":{}}}", "layout.regions.inputs");

        SkinReadResult twice = SkinReader.Read("test", "{\"schema\":1," + Palette + ",\"layout\":{\"version\":1},\"layout\":{\"version\":1}}",
            SkinOrigin.BuiltIn, null);
        PaletteKept(twice);
        Assert.Null(twice.Package!.Layout);
        Assert.StartsWith("layout: ", Assert.Single(twice.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void EverySectionIsPlacedExactlyOnceAndTheHeaderAtMostOnce()
    {
        Refused(Stack("vertical", ["inputs", "headphones", "monitor", "applications"]), "layout.main.children");
        Refused(Stack("vertical", [.. Classic, "inputs"]), "layout.main.children[5].region");
        Refused(Stack("vertical", ["inputs", "headphones", "monitor", "applications", "mixer"]), "layout.main.children[4].region");
        Refused(Stack("vertical", ["header", .. Classic, "header"]), "layout.main.children[6].region");
        Refused(Stack("vertical", []), "layout.main.children");
        Refused(Stack("vertical", Classic).Replace("\"region\":\"inputs\"", "\"region\":3"), "layout.main.children[0].region");
        Assert.True(Taken(Stack("vertical", ["header", .. Classic])).PlacesHeader);
        // A section left out of a nested tree is still left out.
        Refused(DockLayout.Replace(",{\"kind\":\"region\",\"region\":\"monitor\"}", ""), "layout.main.children");
    }

    [Fact]
    public void ABadGridTrackIsRefused()
    {
        foreach ((string track, string path) in new[]
        {
            ("\"fill\"", "layout.main.columns[0]"),
            ("3", "layout.main.columns[0]"),
            ("{}", "layout.main.columns[0]"),
            ("{\"star\":1,\"size\":100}", "layout.main.columns[0]"),
            ("{\"star\":0.05}", "layout.main.columns[0].star"),
            ("{\"star\":10.5}", "layout.main.columns[0].star"),
            ("{\"star\":\"1\"}", "layout.main.columns[0].star"),
            ("{\"size\":39}", "layout.main.columns[0].size"),
            ("{\"size\":2001}", "layout.main.columns[0].size"),
            ("{\"size\":1e999}", "layout.main.columns[0].size"),
            ("{\"min\":10}", "layout.main.columns[0].min"),
        })
            Refused(GridLayout.Replace("{\"size\":280}", track), path);
        foreach (string track in new[] { "{\"star\":0.1}", "{\"star\":10}", "{\"size\":40}", "{\"size\":2000}", "\"auto\"" })
            Taken(GridLayout.Replace("{\"size\":280}", track));

        Refused(GridLayout.Replace("\"columns\":[{\"size\":280},{\"star\":2}]", "\"columns\":[]"), "layout.main.columns");
        Refused(GridLayout.Replace("\"columns\":[{\"size\":280},{\"star\":2}]", "\"columns\":\"auto\""), "layout.main.columns");
        Refused(GridLayout.Replace("\"rows\":[\"auto\",{\"star\":1},\"auto\",\"auto\"],", ""), "layout.main");
        string nine = "\"columns\":[" + string.Join(",", Enumerable.Repeat("\"auto\"", SkinFormat.MaxLayoutTracks + 1)) + "]";
        string error = Refused(GridLayout.Replace("\"columns\":[{\"size\":280},{\"star\":2}]", nine), "layout.main.columns");
        Assert.Contains($"1 to {SkinFormat.MaxLayoutTracks}", error, StringComparison.Ordinal);
        string eight = "\"columns\":[" + string.Join(",", Enumerable.Repeat("\"auto\"", SkinFormat.MaxLayoutTracks)) + "]";
        Taken(GridLayout.Replace("\"columns\":[{\"size\":280},{\"star\":2}]", eight));
    }

    [Fact]
    public void AGridChildStaysInsideTheGridAndOffItsNeighbours()
    {
        Refused(GridLayout.Replace("\"row\":3,\"column\":0", "\"row\":4,\"column\":0"), "layout.main.children[5].row");
        Refused(GridLayout.Replace("\"row\":1,\"column\":0}", "\"row\":1,\"column\":2}"), "layout.main.children[1].column");
        Refused(GridLayout.Replace("\"row\":1,\"column\":0}", "\"row\":-1,\"column\":0}"), "layout.main.children[1].row");
        Refused(GridLayout.Replace("\"row\":1,\"column\":0}", "\"row\":1.5,\"column\":0}"), "layout.main.children[1].row");
        Refused(GridLayout.Replace("\"row\":1,\"column\":0}", "\"column\":0}"), "layout.main.children[1]");
        // A span past the last track, or of none.
        Refused(GridLayout.Replace("\"row\":0,\"column\":0,\"columnSpan\":2", "\"row\":0,\"column\":0,\"columnSpan\":3"),
            "layout.main.children[0].columnSpan");
        Refused(GridLayout.Replace("\"row\":0,\"column\":0,\"columnSpan\":2", "\"row\":0,\"column\":1,\"columnSpan\":2"),
            "layout.main.children[0].columnSpan");
        Refused(GridLayout.Replace("\"row\":0,\"column\":0,\"columnSpan\":2", "\"row\":0,\"column\":0,\"columnSpan\":0"),
            "layout.main.children[0].columnSpan");
        Refused(GridLayout.Replace("\"row\":3,\"column\":0,\"columnSpan\":2", "\"row\":3,\"column\":0,\"rowSpan\":2"),
            "layout.main.children[5].rowSpan");

        // Two children over one cell; the second is the one named.
        string overlap = GridLayout.Replace("\"region\":\"inputs\",\"row\":1,\"column\":0", "\"region\":\"inputs\",\"row\":1,\"column\":0,\"rowSpan\":2");
        string error = Refused(overlap, "layout.main.children[3]");
        Assert.Contains("row 2, column 0", error, StringComparison.Ordinal);
        Assert.Contains("layout.main.children[1]", error, StringComparison.Ordinal);
        Refused(GridLayout.Replace("\"row\":2,\"column\":1", "\"row\":0,\"column\":1"), "layout.main.children[4]");

        // A cell left empty is allowed.
        Taken(GridLayout.Replace("\"rows\":[\"auto\",{\"star\":1},\"auto\",\"auto\"]", "\"rows\":[\"auto\",{\"star\":1},\"auto\",\"auto\",\"auto\"]"));
    }

    [Fact]
    public void ADockFillsWithItsLastChildAlone()
    {
        Refused(DockLayout.Replace("\"dock\":\"fill\"", "\"dock\":\"top\""), "layout.main.children[4].dock");
        Refused(DockLayout.Replace("\"dock\":\"left\"", "\"dock\":\"fill\""), "layout.main.children[1].dock");
        Refused(DockLayout.Replace("\"dock\":\"left\"", "\"dock\":\"middle\""), "layout.main.children[1].dock");
        Refused(DockLayout.Replace("\"dock\":\"left\"", "\"dock\":true"), "layout.main.children[1].dock");
        Refused(DockLayout.Replace(",\"dock\":\"left\"", ""), "layout.main.children[1]");
        Refused(WithMain("{\"kind\":\"dock\",\"id\":\"d\",\"children\":[]}"), "layout.main.children");
    }

    [Fact]
    public void AContainerHoldsAtMostSixteenChildrenAndATreeSixtyFourNodes()
    {
        string seventeen = WithMain(StackNode("main", "vertical", Enumerable.Repeat(Region("inputs"), SkinFormat.MaxLayoutChildren + 1)));
        string error = Refused(seventeen, "layout.main.children");
        Assert.Contains($"at most {SkinFormat.MaxLayoutChildren} children", error, StringComparison.Ordinal);

        // Empty stacks are allowed, so a tree of them reaches the node bound:
        // main, then four stacks of fifteen.
        int count = 0;
        string Empty() => StackNode("s" + count++, "vertical", []);
        string Row() => StackNode("r" + count++, "vertical", Enumerable.Range(0, 15).Select(_ => Empty()));
        string big = WithMain(StackNode("main", "vertical", Enumerable.Range(0, 4).Select(_ => Row())));
        string nodes = Refused(big, "layout.main.children[3].children[14]");
        Assert.Contains($"at most {SkinFormat.MaxLayoutNodes} nodes", nodes, StringComparison.Ordinal);
    }

    [Fact]
    public void ABadRegionOptionIsRefused()
    {
        Refused("{\"version\":1,\"regions\":{\"mixer\":{}}}", "layout.regions.mixer");
        Refused("{\"version\":1,\"regions\":{\"inputs\":{\"template\":\"rack\"}}}", "layout.regions.inputs.template");
        Refused("{\"version\":1,\"regions\":{\"inputs\":{\"actions\":[]}}}", "layout.regions.inputs.actions");
        Refused("{\"version\":1,\"regions\":{\"header\":{\"visibility\":\"hidden\"}}}", "layout.regions.header.visibility");
        Refused("{\"version\":1,\"regions\":{\"header\":{\"width\":400}}}", "layout.regions.header.width");
        Refused("{\"version\":1,\"regions\":{\"inputs\":{\"visibility\":\"gone\"}}}", "layout.regions.inputs.visibility");
        foreach (string size in new[] { "119", "4001", "1e999", "\"300\"" })
        {
            Refused("{\"version\":1,\"regions\":{\"inputs\":{\"width\":" + size + "}}}", "layout.regions.inputs.width");
            Refused("{\"version\":1,\"regions\":{\"monitor\":{\"height\":" + size + "}}}", "layout.regions.monitor.height");
        }
        Taken("{\"version\":1,\"regions\":{\"inputs\":{\"width\":120,\"height\":4000}}}");
    }

    [Fact]
    public void ABadHeaderActionIsRefused()
    {
        string Header(string body) => "{\"version\":1,\"regions\":{\"header\":{" + body + "}}}";
        Refused(Header("\"actions\":[\"settings\"]"), "layout.regions.header.actions[0]");
        Refused(Header("\"actions\":[3]"), "layout.regions.header.actions[0]");
        Refused(Header("\"actions\":\"flow\""), "layout.regions.header.actions");
        Assert.Contains("Options is always first",
            Refused(Header("\"actions\":[\"flow\",\"options\"]"), "layout.regions.header.actions[1]"), StringComparison.Ordinal);
        Refused(Header("\"overflow\":[\"options\"]"), "layout.regions.header.overflow[0]");
        Refused(Header("\"actions\":[\"flow\",\"flow\"]"), "layout.regions.header.actions[1]");
        Refused(Header("\"actions\":[\"flow\"],\"overflow\":[\"about\",\"flow\"]"), "layout.regions.header.overflow[1]");
        SkinLayout every = Taken(Header("\"overflow\":[" + string.Join(",", SkinHeaderActions.All.Select(a => $"\"{a}\"")) + "]"));
        Assert.Empty(every.Header.Row);
        Assert.Equal(SkinHeaderActions.All, every.Header.Overflow);
    }

    [Fact]
    public void AFaultInTheLayoutLeavesTheOtherComplaintsAsTheyWere()
    {
        SkinReadResult result = SkinReader.Read("test",
            "{\"schema\":1,\"tokens\":{\"Ox.Card.Background\":\"#123456\",\"Ox.Nonsense\":\"#fff\"},"
            + "\"layout\":{\"version\":1,\"main\":{\"kind\":\"canvas\"}}}", SkinOrigin.BuiltIn, null);
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
