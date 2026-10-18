using OpenXLR.UI;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

/// <summary>
/// Whose order the sections take: the legacy saved order for a skin without
/// a layout, the skin's own order for one with, and the user's moves for that
/// skin on top. Pure rules; the window test drives them on screen.
/// </summary>
public sealed class SectionArrangementTests
{
    private static readonly string[] Usual = ["InputsTile", "HeadphonesTile", "MonitorTile", "ApplicationsTile", "SubmixerTile"];

    private static SkinLayout Layout(SkinLayoutOrientation orientation, double spacing, params string[] regions) =>
        new(1, new SkinLayoutStack(orientation, spacing, [.. regions.Select(r => new SkinLayoutRegion(r))]));

    private static readonly SkinLayout Row = Layout(SkinLayoutOrientation.Horizontal, 12,
        "submixer", "inputs", "monitor", "headphones", "applications");

    private static readonly string[] RowTiles = ["SubmixerTile", "InputsTile", "MonitorTile", "HeadphonesTile", "ApplicationsTile"];

    [Fact]
    public void ASkinWithoutALayoutKeepsTheLegacyOrderInAColumn()
    {
        var settings = new UiSettings
        {
            SectionOrder = ["MonitorTile", "Gone"],
            SkinSectionOrders = [new SkinSectionOrder { Skin = "default", Version = 1, Order = ["submixer"] }],
        };
        SectionArrangement arrangement = SectionArrangement.Resolve("default", null, settings);
        Assert.Equal(["MonitorTile", "InputsTile", "HeadphonesTile", "ApplicationsTile", "SubmixerTile"], arrangement.Order);
        Assert.Equal(Usual, arrangement.SkinOrder);
        Assert.Equal(SkinLayoutOrientation.Vertical, arrangement.Orientation);
        Assert.Equal(SkinLayoutDefaults.Spacing, arrangement.Spacing);
        Assert.Null(arrangement.Skin);
        Assert.Null(arrangement.Version);
        Assert.Equal(Usual, SectionArrangement.Resolve("default", null, new UiSettings()).Order);
    }

    [Fact]
    public void ASkinWithALayoutStartsFromItsOwnOrderAndIgnoresTheLegacyOne()
    {
        var settings = new UiSettings { SectionOrder = ["ApplicationsTile", "MonitorTile"] };
        SectionArrangement arrangement = SectionArrangement.Resolve("broadcast", Row, settings);
        Assert.Equal(RowTiles, arrangement.Order);
        Assert.Equal(RowTiles, arrangement.SkinOrder);
        Assert.Equal(SkinLayoutOrientation.Horizontal, arrangement.Orientation);
        Assert.Equal(12, arrangement.Spacing);
        Assert.Equal("broadcast", arrangement.Skin);
        Assert.Equal(1, arrangement.Version);
    }

    [Fact]
    public void TheUsersMovesForThatSkinGoOnTopAndAreMadeWholeAgainstTheSkinsOrder()
    {
        var settings = new UiSettings
        {
            SectionOrder = ["ApplicationsTile"],
            SkinSectionOrders =
            [
                new SkinSectionOrder { Skin = "other", Version = 1, Order = ["headphones"] },
                new SkinSectionOrder { Skin = "broadcast", Version = 1, Order = ["applications", "gone", "applications", "monitor"] },
            ],
        };
        SectionArrangement arrangement = SectionArrangement.Resolve("broadcast", Row, settings);
        // Unknown and repeated regions go; the ones it leaves out follow in the skin's order.
        Assert.Equal(["ApplicationsTile", "MonitorTile", "SubmixerTile", "InputsTile", "HeadphonesTile"], arrangement.Order);
        Assert.Equal(RowTiles, arrangement.SkinOrder);

        // An arrangement made under another layout version is not this one's.
        SkinLayout second = Row with { Version = 2 };
        Assert.Equal(RowTiles, SectionArrangement.Resolve("broadcast", second, settings).Order);
    }

    [Fact]
    public void SavingAndResettingTouchOnlyTheOrderThatOwnsTheArrangement()
    {
        var settings = new UiSettings
        {
            SectionOrder = ["MonitorTile"],
            SkinSectionOrders = [new SkinSectionOrder { Skin = "other", Version = 1, Order = ["headphones"] }],
        };

        SectionArrangement skinned = SectionArrangement.Resolve("broadcast", Row, settings);
        UiSettings moved = skinned.Save(settings, ["InputsTile", "SubmixerTile", "MonitorTile", "HeadphonesTile", "ApplicationsTile"]);
        Assert.Equal(["MonitorTile"], moved.SectionOrder);
        Assert.Equal(["inputs", "submixer", "monitor", "headphones", "applications"], moved.SkinSectionOrderFor("broadcast", 1)!.Order);
        Assert.Equal(["headphones"], moved.SkinSectionOrderFor("other", 1)!.Order);
        Assert.Equal("InputsTile", SectionArrangement.Resolve("broadcast", Row, moved).Order[0]);

        UiSettings reset = skinned.Reset(moved);
        Assert.Null(reset.SkinSectionOrderFor("broadcast", 1));
        Assert.Equal(["headphones"], reset.SkinSectionOrderFor("other", 1)!.Order);
        Assert.Equal(["MonitorTile"], reset.SectionOrder);
        Assert.Equal(RowTiles, SectionArrangement.Resolve("broadcast", Row, reset).Order);

        SectionArrangement legacy = SectionArrangement.Resolve("default", null, moved);
        UiSettings legacyMoved = legacy.Save(moved, ["SubmixerTile", "InputsTile", "HeadphonesTile", "MonitorTile", "ApplicationsTile"]);
        Assert.Equal(["SubmixerTile", "InputsTile", "HeadphonesTile", "MonitorTile", "ApplicationsTile"], legacyMoved.SectionOrder);
        Assert.Equal(moved.SkinSectionOrders, legacyMoved.SkinSectionOrders);
        Assert.Equal(Usual, legacy.Reset(legacyMoved).SectionOrder);
        Assert.Equal(moved.SkinSectionOrders, legacy.Reset(legacyMoved).SkinSectionOrders);
    }

    [Fact]
    public void TheStructureComparisonIsOrderDirectionAndGap()
    {
        SectionArrangement a = SectionArrangement.Resolve("broadcast", Row, new UiSettings());
        Assert.True(a.SameStructure(a with { Order = [.. a.Order], Skin = "another" }));
        Assert.False(a.SameStructure(a with { Orientation = SkinLayoutOrientation.Vertical }));
        Assert.False(a.SameStructure(a with { Spacing = 8 }));
        Assert.False(a.SameStructure(a with { Order = [.. a.Order.Reverse()] }));
    }

    [Fact]
    public void EveryRegionHasOneTileAndBack()
    {
        Assert.Equal(Usual, SectionArrangement.Tiles);
        foreach (string region in SkinLayoutRegions.All)
            Assert.Equal(region, SectionArrangement.RegionFor(SectionArrangement.TileFor(region)));
        Assert.Null(SectionArrangement.RegionFor("Gone"));
        Assert.Throws<ArgumentException>(() => SectionArrangement.TileFor("header"));
    }
}
