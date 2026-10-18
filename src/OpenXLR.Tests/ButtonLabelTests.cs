using System.Text.Json.Nodes;
using System.Xml.Linq;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class ButtonLabelTests
{
    [Fact]
    public void ButtonLabelsDoNotContainEllipses()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "OpenXLR.UI"))) root = root.Parent;
        Assert.NotNull(root);
        string ui = Path.Combine(root.FullName, "src", "OpenXLR.UI");
        foreach (string path in Directory.EnumerateFiles(ui, "*.axaml"))
        {
            var document = XDocument.Load(path);
            foreach (var button in document.Descendants().Where(e => e.Name.LocalName.EndsWith("Button", StringComparison.Ordinal)))
            {
                if (button.Attribute("Content")?.Value is string content)
                    AssertPlain(content, Path.GetFileName(path));
                foreach (var text in button.Descendants().Where(e => e.Name.LocalName == "TextBlock"))
                    if (text.Attribute("Text")?.Value is string label) AssertPlain(label, Path.GetFileName(path));
            }
        }
    }

    [Fact]
    public void DynamicInsertButtonLabelsHaveNoEllipsis()
    {
        var inserts = new InsertsViewModel(new DaemonClient(), "xlr1");
        Assert.Equal("Inserts", inserts.ButtonText);
        inserts.Items.Add(new InsertViewModel(inserts, "test", "test-plugin", "Test"));
        Assert.Equal("Inserts (1)", inserts.ButtonText);
    }

    /// <summary>
    /// The chain button wears the chain's state: nothing while the chain is
    /// empty, processing while any plugin is in the path and sound, off when
    /// the chain holds plugins and every one is bypassed or failed, and it
    /// follows a bypass flipped from the strip.
    /// </summary>
    [Fact]
    public void TheChainButtonWearsTheChainState()
    {
        var inserts = new InsertsViewModel(new DaemonClient(), "xlr1");
        var raised = new List<string>();
        inserts.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);
        Assert.False(inserts.HasItems);
        Assert.False(inserts.IsProcessing);
        Assert.False(inserts.IsOff);

        static JsonObject Insert(string id, bool bypass) => new()
        {
            ["insert"] = new JsonObject { ["id"] = id, ["plugin"] = "urn:test:" + id, ["label"] = id, ["bypass"] = bypass },
        };
        inserts.Apply(new JsonArray(Insert("a", false), Insert("b", true)));
        Assert.True(inserts.HasItems);
        Assert.True(inserts.IsProcessing);
        Assert.False(inserts.IsOff);
        Assert.Contains(nameof(InsertsViewModel.IsProcessing), raised);

        inserts.Apply(new JsonArray(Insert("a", true), Insert("b", true)));
        Assert.False(inserts.IsProcessing);
        Assert.True(inserts.IsOff);

        var failed = new JsonArray(Insert("a", false));
        failed[0]!["error"] = "did not load";
        inserts.Apply(failed);
        Assert.False(inserts.IsProcessing);
        Assert.True(inserts.IsOff);

        inserts.Apply(new JsonArray(Insert("a", false)));
        raised.Clear();
        inserts.Items[0].Bypass = true;
        Assert.True(inserts.IsOff);
        Assert.Contains(nameof(InsertsViewModel.IsOff), raised);

        inserts.Apply(null);
        Assert.False(inserts.HasItems);
        Assert.False(inserts.IsProcessing);
        Assert.False(inserts.IsOff);
    }

    private static void AssertPlain(string text, string file)
        => Assert.True(!text.Contains('…') && !text.Contains("...", StringComparison.Ordinal),
            $"{file}: button label '{text}' contains an ellipsis.");
}
