using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenXLR.UI.Skinning;

/// <summary>Which way a stack lays its children out.</summary>
public enum SkinLayoutOrientation
{
    /// <summary>One above the other, the classic window.</summary>
    Vertical,
    /// <summary>Side by side, in one row that scrolls when the window is narrower.</summary>
    Horizontal,
}

/// <summary>
/// The regions a skin's layout may place. Each id names one part of the
/// window the application owns and builds; a skin places it and never
/// supplies it.
/// </summary>
public static class SkinLayoutRegions
{
    public const string Inputs = "inputs";
    public const string Headphones = "headphones";
    public const string Monitor = "monitor";
    public const string Applications = "applications";
    public const string Submixer = "submixer";

    /// <summary>Every region layout version 1 places, in the classic order.</summary>
    public static IReadOnlyList<string> All { get; } = [Inputs, Headphones, Monitor, Applications, Submixer];

    public static bool Exists(string id) => All.Contains(id, StringComparer.Ordinal);
}

/// <summary>A region placed in a container: one of <see cref="SkinLayoutRegions.All"/>.</summary>
public sealed record SkinLayoutRegion(string Id);

/// <summary>
/// A stack of regions. In layout version 1 this is the whole tree: the five
/// sections, each once, in one direction.
/// </summary>
/// <param name="Spacing">The gap between children, in device-independent pixels.</param>
public sealed record SkinLayoutStack(
    SkinLayoutOrientation Orientation,
    double Spacing,
    IReadOnlyList<SkinLayoutRegion> Children)
{
    /// <summary>The region ids in the order the stack places them.</summary>
    public IEnumerable<string> RegionOrder => Children.Select(c => c.Id);

    /// <summary>Equal in what the window shows: direction, gap and order.</summary>
    public bool SameAs(SkinLayoutStack other) =>
        Orientation == other.Orientation && Spacing.Equals(other.Spacing)
        && RegionOrder.SequenceEqual(other.RegionOrder, StringComparer.Ordinal);
}

/// <summary>
/// A skin's validated desktop layout. Every value has passed
/// <see cref="SkinLayoutReader"/>; nothing in it names a type, a binding, a
/// resource or a file.
/// </summary>
/// <param name="Version">The layout format version the skin was written for.</param>
/// <param name="Main">The main window's sections.</param>
public sealed record SkinLayout(int Version, SkinLayoutStack Main);
