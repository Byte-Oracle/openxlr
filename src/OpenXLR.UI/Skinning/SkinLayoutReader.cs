using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace OpenXLR.UI.Skinning;

/// <summary>
/// Reads the optional <c>layout</c> block of a skin. Like the rest of the
/// reader it touches no rendering platform, so every rule is an ordinary
/// unit test.
///
/// The block is read by hand into typed records: each object's properties
/// are matched against a fixed list, each value against its own type and
/// bounds, and nothing names a type, a binding, a resource or a file. A
/// layout is taken whole or not at all. The first problem rejects the block,
/// names the JSON path it found it at, and leaves the skin's tokens and
/// controls alone, so the window keeps the skin's colours in the classic
/// arrangement.
/// </summary>
public static class SkinLayoutReader
{
    /// <summary>What follows every complaint, so a reader knows what the window did about it.</summary>
    internal const string Consequence = "The skin's layout is ignored and the classic arrangement is used.";

    private sealed class LayoutException(string path, string message) : Exception(message)
    {
        public string Path { get; } = path;
    }

    private const string Stack = "stack";
    private const string Region = "region";

    /// <summary>
    /// The layout this block describes, or null when it breaks a rule; the
    /// reason, with its path, is then added to <paramref name="errors"/>.
    /// </summary>
    public static SkinLayout? Read(JsonElement element, List<string> errors)
    {
        try
        {
            return ReadLayout(element);
        }
        catch (LayoutException ex)
        {
            errors.Add($"{ex.Path}: {ex.Message} {Consequence}");
            return null;
        }
    }

    private static SkinLayout ReadLayout(JsonElement element)
    {
        const string path = "layout";
        Dictionary<string, JsonElement> fields = Fields(element, path, "the layout");

        // The version first: a newer layout is expected to carry properties
        // this one does not know, and the version is what to tell the user.
        if (!fields.TryGetValue("version", out JsonElement versionElement))
            throw new LayoutException(path, $"the layout has no \"version\". Add \"version\": {SkinFormat.LayoutVersion}.");
        if (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out int version))
            throw new LayoutException(path + ".version", "expected a whole number.");
        if (version > SkinFormat.LayoutVersion)
            throw new LayoutException(path + ".version",
                $"layout version {version} is newer than this OpenXLR reads, which is {SkinFormat.LayoutVersion}. Update OpenXLR to use it.");
        if (version != SkinFormat.LayoutVersion)
            throw new LayoutException(path + ".version", $"layout version {version} is not a version this format ever had.");

        Only(fields, path, "the layout", "version", "main");
        SkinLayoutStack main = fields.TryGetValue("main", out JsonElement mainElement)
            ? ReadMain(mainElement, path + ".main")
            : SkinLayoutDefaults.Classic.Main;
        return new SkinLayout(version, main);
    }

    private static SkinLayoutStack ReadMain(JsonElement element, string path)
    {
        Dictionary<string, JsonElement> fields = Fields(element, path, "\"main\"");
        string kind = Kind(fields, path);
        if (kind == Region)
            throw new LayoutException(path + ".kind", "\"main\" is a \"stack\" of regions, not a region of its own.");
        if (kind != Stack) throw UnknownKind(path, kind);
        Only(fields, path, "a stack", "kind", "orientation", "spacing", "children");

        if (!fields.TryGetValue("orientation", out JsonElement orientationElement))
            throw new LayoutException(path, "a stack needs an \"orientation\" of \"vertical\" or \"horizontal\".");
        SkinLayoutOrientation orientation = (orientationElement.ValueKind == JsonValueKind.String
            ? orientationElement.GetString() : null) switch
        {
            "vertical" => SkinLayoutOrientation.Vertical,
            "horizontal" => SkinLayoutOrientation.Horizontal,
            _ => throw new LayoutException(path + ".orientation", "expected \"vertical\" or \"horizontal\"."),
        };

        double spacing = SkinLayoutDefaults.Spacing;
        if (fields.TryGetValue("spacing", out JsonElement spacingElement))
        {
            if (spacingElement.ValueKind != JsonValueKind.Number)
                throw new LayoutException(path + ".spacing", "expected a number.");
            if (!spacingElement.TryGetDouble(out spacing) || !double.IsFinite(spacing))
                throw new LayoutException(path + ".spacing", "expected a finite number.");
            if (spacing is < SkinFormat.MinLayoutSpacing or > SkinFormat.MaxLayoutSpacing)
                throw new LayoutException(path + ".spacing",
                    $"{spacing.ToString(CultureInfo.InvariantCulture)} is outside the allowed range "
                    + $"{SkinFormat.MinLayoutSpacing} to {SkinFormat.MaxLayoutSpacing}.");
        }

        if (!fields.TryGetValue("children", out JsonElement childrenElement))
            throw new LayoutException(path, "a stack needs a \"children\" array.");
        if (childrenElement.ValueKind != JsonValueKind.Array)
            throw new LayoutException(path + ".children", "expected an array of regions.");
        if (childrenElement.GetArrayLength() > SkinFormat.MaxLayoutChildren)
            throw new LayoutException(path + ".children",
                $"a container holds at most {SkinFormat.MaxLayoutChildren} children.");

        var children = new List<SkinLayoutRegion>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement child in childrenElement.EnumerateArray())
        {
            SkinLayoutRegion region = ReadRegion(child, $"{path}.children[{index}]");
            if (!placed.Add(region.Id))
                throw new LayoutException($"{path}.children[{index}].region",
                    $"\"{region.Id}\" is placed twice; each region appears once.");
            children.Add(region);
            index++;
        }
        string[] missing = [.. SkinLayoutRegions.All.Where(id => !placed.Contains(id))];
        if (missing.Length > 0)
            throw new LayoutException(path + ".children",
                $"{Quoted(missing)} {(missing.Length == 1 ? "is" : "are")} not placed; "
                + "the main stack places each of " + Quoted(SkinLayoutRegions.All) + " once.");
        return new SkinLayoutStack(orientation, spacing, children);
    }

    private static SkinLayoutRegion ReadRegion(JsonElement element, string path)
    {
        Dictionary<string, JsonElement> fields = Fields(element, path, "a child");
        string kind = Kind(fields, path);
        if (kind == Stack)
            throw new LayoutException(path + ".kind",
                $"layout version {SkinFormat.LayoutVersion} has one stack, \"main\"; a container cannot sit inside it.");
        if (kind != Region) throw UnknownKind(path, kind);
        Only(fields, path, "a region", "kind", "region");

        if (!fields.TryGetValue("region", out JsonElement regionElement))
            throw new LayoutException(path, "a region needs a \"region\" id.");
        if (regionElement.ValueKind != JsonValueKind.String)
            throw new LayoutException(path + ".region", "expected a region id such as \"inputs\".");
        string id = regionElement.GetString()!;
        if (!SkinLayoutRegions.Exists(id))
            throw new LayoutException(path + ".region",
                $"\"{Printable(id)}\" is not a region this OpenXLR has; use one of {Quoted(SkinLayoutRegions.All)}.");
        return new SkinLayoutRegion(id);
    }

    /// <summary>An object's properties by name. A name given twice has no single meaning, so it is refused.</summary>
    private static Dictionary<string, JsonElement> Fields(JsonElement element, string path, string what)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new LayoutException(path, $"{what} must be a JSON object.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!fields.TryAdd(property.Name, property.Value))
                throw new LayoutException($"{path}.{Printable(property.Name)}", "is given twice.");
        return fields;
    }

    /// <summary>Refuse any property the object's kind does not have.</summary>
    private static void Only(Dictionary<string, JsonElement> fields, string path, string what, params string[] allowed)
    {
        foreach (string name in fields.Keys)
            if (!allowed.Contains(name, StringComparer.Ordinal))
                throw new LayoutException($"{path}.{Printable(name)}",
                    $"{what} has no such property; it takes {Quoted(allowed)}.");
    }

    private static string Kind(Dictionary<string, JsonElement> fields, string path)
    {
        if (!fields.TryGetValue("kind", out JsonElement kind))
            throw new LayoutException(path, "every node needs a \"kind\".");
        if (kind.ValueKind != JsonValueKind.String)
            throw new LayoutException(path + ".kind", "expected \"stack\" or \"region\".");
        return kind.GetString()!;
    }

    private static LayoutException UnknownKind(string path, string kind) =>
        new(path + ".kind", $"\"{Printable(kind)}\" is not a kind this OpenXLR has; use \"stack\" or \"region\".");

    private static string Quoted(IEnumerable<string> names)
    {
        string[] quoted = [.. names.Select(n => $"\"{n}\"")];
        return quoted.Length <= 1 ? string.Concat(quoted)
            : string.Join(", ", quoted[..^1]) + " and " + quoted[^1];
    }

    /// <summary>A name from the file, short and without control characters, for a message.</summary>
    private static string Printable(string text)
    {
        string clean = new([.. text.Where(c => !char.IsControl(c))]);
        return clean.Length > 64 ? clean[..64] : clean;
    }
}
