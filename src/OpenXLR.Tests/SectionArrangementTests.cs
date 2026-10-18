using OpenXLR.UI;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

/// <summary>
/// Where the regions stand: the legacy saved order for a skin without a
/// layout, the skin's own placement for one with, and the user's moves for
/// that skin on top as a permutation of the sections over the tree's places.
/// Pure rules; the window tests drive them on screen.
/// </summary>
public sealed class SectionArrangementTests
{
    private static readonly string[] Usual = ["InputsTile", "HeadphonesTile", "MonitorTile", "ApplicationsTile", "SubmixerTile"];

    private static SkinLayout Parse(string layout)
    {
        SkinReadResult result = SkinReader.Read("test", "{\"schema\":1,\"tokens\":{},\"layout\":" + layout + "}", SkinOrigin.BuiltIn, null);
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        return result.Package!.Layout!;
    }

    private static string Regions(params string[] ids) => string.Join(",", ids.Select(id => $"{{\"kind\":\"region\",\"region\":\"{id}\"}}"));

    private static readonly SkinLayout Row = Parse("{\"version\":1,\"main\":{\"kind\":\"stack\",\"id\":\"row\",\"orientation\":\"horizontal\","
        + "\"spacing\":12,\"children\":[" + Regions("submixer", "inputs", "monitor", "headphones", "applications") + "]}}");

    private static readonly string[] RowTiles = ["SubmixerTile", "InputsTile", "MonitorTile", "HeadphonesTile", "ApplicationsTile"];

    private static readonly SkinLayout Grid = Parse("""
        {"version":1,"main":{"kind":"grid","id":"desktop","columns":["auto",{"star":1}],"rows":["auto","auto","auto"],"children":[
          {"kind":"region","region":"header","row":0,"column":0,"columnSpan":2},
          {"kind":"region","region":"inputs","row":1,"column":0},
          {"kind":"stack","id":"right","orientation":"vertical","row":1,"column":1,"children":[
            {"kind":"region","region":"submixer"},{"kind":"region","region":"monitor"}]},
          {"kind":"region","region":"headphones","row":2,"column":0},
          {"kind":"region","region":"applications","row":2,"column":1}]},
         "regions":{"applications":{"visibility":"hidden"},"monitor":{"visibility":"hidden"}}}
        """);

    private static readonly SkinLayout Dock = Parse("""
        {"version":1,"main":{"kind":"dock","id":"d","children":[
          {"kind":"region","region":"inputs","dock":"left"},
          {"kind":"region","region":"monitor","dock":"bottom"},
          {"kind":"region","region":"headphones","dock":"left"},
          {"kind":"region","region":"applications","dock":"top"},
          {"kind":"region","region":"submixer","dock":"fill"}]}}
        """);

    [Fact]
    public void ASkinWithoutALayoutKeepsTheLegacyOrderInTheClassicColumn()
    {
        var settings = new UiSettings
        {
            SectionOrder = ["MonitorTile", "Gone"],
            SkinArrangements = [new SkinArrangement { Skin = "default", Version = 1, Slots = new Dictionary<string, string> { ["main:0"] = "submixer" } }],
        };
        SectionArrangement arrangement = SectionArrangement.Resolve("default", null, settings, out bool dropped);
        Assert.False(dropped);
        Assert.Equal(["MonitorTile", "InputsTile", "HeadphonesTile", "ApplicationsTile", "SubmixerTile"], arrangement.Order);
        Assert.Equal(Usual, arrangement.SkinOrder);
        Assert.True(arrangement.Tree.SameAs(SkinLayoutDefaults.Classic.Main));
        Assert.Equal(SkinLayoutOrientation.Vertical, arrangement.OrientationOf("inputs"));
        Assert.False(arrangement.HeaderInTree);
        Assert.Empty(arrangement.Hidden);
        Assert.Null(arrangement.Skin);
        Assert.Null(arrangement.Version);
        Assert.Equal(Usual, SectionArrangement.Resolve("default", null, new UiSettings()).Order);
    }

    [Fact]
    public void ASkinWithALayoutStartsFromItsOwnPlacementAndIgnoresTheLegacyOrder()
    {
        var settings = new UiSettings { SectionOrder = ["ApplicationsTile", "MonitorTile"] };
        SectionArrangement arrangement = SectionArrangement.Resolve("broadcast", Row, settings);
        Assert.Equal(RowTiles, arrangement.Order);
        Assert.Equal(RowTiles, arrangement.SkinOrder);
        Assert.Equal(SkinLayoutOrientation.Horizontal, arrangement.OrientationOf("monitor"));
        Assert.Equal("broadcast", arrangement.Skin);
        Assert.Equal(1, arrangement.Version);
    }

    [Fact]
    public void PlacesAreNamedByContainerAndIndexOrByContainerRowAndColumn()
    {
        Assert.Equal(["main:0", "main:1", "main:2", "main:3", "main:4"],
            LayoutSlot.Of(SkinLayoutDefaults.Classic.Main).Select(s => s.Id));

        IReadOnlyList<LayoutSlot> grid = LayoutSlot.Of(Grid.Main);
        Assert.Equal(["desktop:0,0", "desktop:1,0", "right:0", "right:1", "desktop:2,0", "desktop:2,1"], grid.Select(s => s.Id));
        Assert.Equal(["header", "inputs", "submixer", "monitor", "headphones", "applications"], grid.Select(s => s.SkinRegion));
        Assert.Equal(["desktop", "desktop", "right", "right", "desktop", "desktop"], grid.Select(s => s.Container.Id));

        IReadOnlyList<LayoutSlot> dock = LayoutSlot.Of(Dock.Main);
        Assert.Equal(["d:0", "d:1", "d:2", "d:3", "d:4"], dock.Select(s => s.Id));
        Assert.Equal(SkinLayoutEdge.Bottom, dock[1].Child.Edge);
    }

    [Fact]
    public void TwoSectionsTradePlacesAndTheHeaderStaysPut()
    {
        SectionArrangement arrangement = SectionArrangement.Resolve("grid", Grid, new UiSettings());
        Assert.True(arrangement.HeaderInTree);
        SectionArrangement swapped = arrangement.Swap("inputs", "submixer");
        Assert.Equal("submixer", swapped.Placement["desktop:1,0"]);
        Assert.Equal("inputs", swapped.Placement["right:0"]);
        Assert.Null(swapped.OrientationOf("submixer"));
        Assert.Equal(SkinLayoutOrientation.Vertical, swapped.OrientationOf("inputs"));
        Assert.Same(arrangement, arrangement.Swap("inputs", "inputs"));
        Assert.Same(arrangement, arrangement.Swap("inputs", "header"));
        Assert.Equal("header", swapped.Placement["desktop:0,0"]);
    }

    [Fact]
    public void ADropLandsBesideANeighbourInAStackAndTradesPlacesAnywhereElse()
    {
        SectionArrangement row = SectionArrangement.Resolve("row", Row, new UiSettings());
        // submixer, inputs, monitor, headphones, applications
        Assert.Equal(SectionDrop.Place, row.DropFor("inputs", "monitor"));
        Assert.Equal(SectionDrop.Swap, row.DropFor("submixer", "headphones"));
        // Next to its neighbour on the side it is already on: nothing moves.
        Assert.Same(row, row.Move("inputs", "monitor", after: false));
        Assert.Equal(["SubmixerTile", "MonitorTile", "InputsTile", "HeadphonesTile", "ApplicationsTile"],
            row.Move("inputs", "monitor", after: true).Order);
        Assert.Equal(["SubmixerTile", "MonitorTile", "InputsTile", "HeadphonesTile", "ApplicationsTile"],
            row.Move("monitor", "inputs", after: false).Order);
        // Two apart, either side: a swap.
        Assert.Equal(["HeadphonesTile", "InputsTile", "MonitorTile", "SubmixerTile", "ApplicationsTile"],
            row.Move("submixer", "headphones", after: false).Order);

        SectionArrangement grid = SectionArrangement.Resolve("grid", Grid, new UiSettings());
        Assert.Equal(SectionDrop.Swap, grid.DropFor("inputs", "headphones"));
        Assert.Equal(SectionDrop.Swap, grid.DropFor("inputs", "submixer"));
        Assert.Equal(SectionDrop.Place, grid.DropFor("submixer", "monitor"));

        // Without a skin layout a drop always lands beside, as it always has.
        SectionArrangement legacy = SectionArrangement.Resolve("default", null, new UiSettings());
        Assert.Equal(SectionDrop.Place, legacy.DropFor("submixer", "inputs"));
        Assert.Equal(["SubmixerTile", "InputsTile", "HeadphonesTile", "MonitorTile", "ApplicationsTile"],
            legacy.Move("submixer", "inputs", after: false).Order);
    }

    [Fact]
    public void TheArrowsFindTheNextSectionThatWayInTheSameContainer()
    {
        SectionArrangement grid = SectionArrangement.Resolve("grid", Grid, new UiSettings());
        bool Any(string _) => true;
        Assert.Equal("headphones", grid.Neighbour("inputs", 0, 1, Any));
        Assert.Equal("applications", grid.Neighbour("headphones", 1, 0, Any));
        Assert.Equal("inputs", grid.Neighbour("headphones", 0, -1, Any));
        Assert.Null(grid.Neighbour("inputs", 0, -1, Any)); // the header is not a section
        Assert.Null(grid.Neighbour("inputs", 1, 0, Any)); // the stack beside it is another container
        Assert.Null(grid.Neighbour("inputs", -1, 0, Any));
        Assert.Equal("monitor", grid.Neighbour("submixer", 0, 1, Any));
        Assert.Equal("monitor", grid.Neighbour("submixer", 1, 0, Any));
        Assert.Equal("submixer", grid.Neighbour("monitor", -1, 0, Any));
        // A section the window is not showing is passed over.
        Assert.Null(grid.Neighbour("inputs", 0, 1, r => r != "headphones"));

        SectionArrangement dock = SectionArrangement.Resolve("dock", Dock, new UiSettings());
        Assert.Equal("monitor", dock.Neighbour("inputs", 1, 0, Any));
        Assert.Equal("headphones", dock.Neighbour("inputs", 0, 1, r => r != "monitor"));
        Assert.Null(dock.Neighbour("inputs", 0, -1, Any));
    }

    [Fact]
    public void TheUsersMovesForThatSkinGoOnTopAndOnlyTheMovedPlacesAreSaved()
    {
        SectionArrangement grid = SectionArrangement.Resolve("grid", Grid, new UiSettings());
        SectionArrangement moved = grid.Swap("inputs", "headphones").Show("applications");
        UiSettings saved = moved.Save(new UiSettings { SectionOrder = ["MonitorTile"] });
        SkinArrangement entry = saved.SkinArrangementFor("grid", 1)!;
        Assert.Equal(new Dictionary<string, string> { ["desktop:1,0"] = "headphones", ["desktop:2,0"] = "inputs" }, entry.Slots);
        Assert.Equal(["applications"], entry.Shown);
        Assert.Equal(["MonitorTile"], saved.SectionOrder);

        SectionArrangement again = SectionArrangement.Resolve("grid", Grid, saved, out bool dropped);
        Assert.False(dropped);
        Assert.Equal("headphones", again.Placement["desktop:1,0"]);
        Assert.Equal(["monitor"], again.Hidden);
        Assert.True(again.SameStructure(moved));
        Assert.True(again.SameOrigin(grid));

        // An arrangement made under another layout version is not this one's, and is not dropped.
        Assert.Equal(grid.Order, SectionArrangement.Resolve("grid", Grid with { Version = 2 }, saved, out dropped).Order);
        Assert.False(dropped);
    }

    [Fact]
    public void AnArrangementThatNoLongerFitsTheTreeIsDroppedWhole()
    {
        SkinArrangement Entry(Dictionary<string, string> slots, params string[] shown) =>
            new() { Skin = "grid", Version = 1, Slots = slots, Shown = shown };
        foreach (SkinArrangement stale in new[]
        {
            // A place the tree does not have, beside one it does.
            Entry(new() { ["desktop:1,0"] = "headphones", ["desktop:2,0"] = "inputs", ["left:0"] = "monitor" }),
            Entry(new() { ["desktop:1,0"] = "mixer" }),
            Entry(new() { ["desktop:0,0"] = "inputs", ["desktop:1,0"] = "header" }),
            Entry(new() { ["desktop:1,0"] = "submixer" }),
            Entry(new(), "inputs"),
            Entry(new(), "nothing"),
        })
        {
            SectionArrangement arrangement = SectionArrangement.Resolve("grid", Grid, new UiSettings { SkinArrangements = [stale] }, out bool dropped);
            Assert.True(dropped, string.Join(",", stale.Slots.Select(p => p.Key + "=" + p.Value)) + string.Join(",", stale.Shown));
            Assert.Equal(arrangement.SkinOrder, arrangement.Order);
            Assert.Equal(["monitor", "applications"], arrangement.Hidden);
        }
    }

    [Fact]
    public void HiddenSectionsComeBackOneByOneAndResetHidesThemAgain()
    {
        SectionArrangement grid = SectionArrangement.Resolve("grid", Grid, new UiSettings());
        Assert.Equal(["applications", "monitor"], grid.SkinHidden.Order());
        Assert.Equal(["monitor", "applications"], grid.Hidden);
        SectionArrangement shown = grid.Show("monitor");
        Assert.Equal(["applications"], shown.Hidden);
        Assert.Same(shown, shown.Show("monitor"));
        Assert.Same(shown, shown.Show("inputs"));
        Assert.False(shown.SameStructure(grid));
        SectionArrangement restored = shown.Swap("inputs", "headphones").Restored();
        Assert.True(restored.SameStructure(grid));
    }

    [Fact]
    public void SavingAndResettingTouchOnlyTheOrderThatOwnsTheArrangement()
    {
        var other = new SkinArrangement { Skin = "other", Version = 1, Slots = new Dictionary<string, string> { ["a:0"] = "inputs" } };
        var settings = new UiSettings { SectionOrder = ["MonitorTile"], SkinArrangements = [other] };

        SectionArrangement skinned = SectionArrangement.Resolve("broadcast", Row, settings);
        UiSettings moved = skinned.Swap("submixer", "inputs").Save(settings);
        Assert.Equal(["MonitorTile"], moved.SectionOrder);
        Assert.Equal(new Dictionary<string, string> { ["row:0"] = "inputs", ["row:1"] = "submixer" },
            moved.SkinArrangementFor("broadcast", 1)!.Slots);
        Assert.Equal(other.Slots, moved.SkinArrangementFor("other", 1)!.Slots);
        Assert.Equal("InputsTile", SectionArrangement.Resolve("broadcast", Row, moved).Order[0]);

        // Moving everything back saves no entry at all.
        Assert.Null(skinned.Save(moved).SkinArrangementFor("broadcast", 1));

        UiSettings reset = skinned.Reset(moved);
        Assert.Null(reset.SkinArrangementFor("broadcast", 1));
        Assert.Equal(other.Slots, reset.SkinArrangementFor("other", 1)!.Slots);
        Assert.Equal(["MonitorTile"], reset.SectionOrder);
        Assert.Equal(RowTiles, SectionArrangement.Resolve("broadcast", Row, reset).Order);

        SectionArrangement legacy = SectionArrangement.Resolve("default", null, moved);
        UiSettings legacyMoved = legacy.Move("submixer", "inputs", after: false).Save(moved);
        Assert.Equal(["MonitorTile", "SubmixerTile", "InputsTile", "HeadphonesTile", "ApplicationsTile"], legacyMoved.SectionOrder);
        Assert.Equal(moved.SkinArrangements, legacyMoved.SkinArrangements);
        Assert.Equal(Usual, legacy.Reset(legacyMoved).SectionOrder);
        Assert.Equal(moved.SkinArrangements, legacy.Reset(legacyMoved).SkinArrangements);
    }

    [Fact]
    public void TheStructureComparisonCoversTheTreeThePlacesAndTheOptions()
    {
        SectionArrangement a = SectionArrangement.Resolve("broadcast", Row, new UiSettings());
        Assert.True(a.SameStructure(a with { Skin = "another" }));
        Assert.False(a.SameStructure(a.Swap("inputs", "monitor")));
        SkinLayoutStack stack = (SkinLayoutStack)Row.Main;
        Assert.False(a.SameStructure(SectionArrangement.Resolve("broadcast", Row with { Main = stack with { Spacing = 8 } }, new UiSettings())));
        Assert.False(a.SameStructure(SectionArrangement.Resolve("broadcast",
            Row with { Main = stack with { Orientation = SkinLayoutOrientation.Vertical } }, new UiSettings())));
        Assert.False(a.SameStructure(SectionArrangement.Resolve("broadcast",
            Row with { Header = new SkinHeaderOptions(["about"], []) }, new UiSettings())));
        Assert.False(a.SameStructure(SectionArrangement.Resolve("broadcast", null, new UiSettings())));
    }

    [Fact]
    public void EverySectionHasOneTileAndBack()
    {
        Assert.Equal(Usual, SectionArrangement.Tiles);
        foreach (string region in SkinLayoutRegions.Sections)
            Assert.Equal(region, SectionArrangement.RegionFor(SectionArrangement.TileFor(region)));
        Assert.Null(SectionArrangement.RegionFor("Gone"));
        Assert.Throws<ArgumentException>(() => SectionArrangement.TileFor("header"));
    }
}
