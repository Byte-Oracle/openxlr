using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Skinning;
using OpenXLR.UI.Views;

namespace OpenXLR.Tests;

public sealed partial class SkinWindowTests
{
    /// <summary>
    /// A skin may leave the binding out of an input row and of a matrix row
    /// head. The strips keep every other part, a skin that keeps it shows the
    /// chip again, and the binding stays reachable from Options either way.
    /// </summary>
    private static void ASkinCanLeaveTheBindingOut()
    {
        string data = Path.Combine(Path.GetTempPath(), "openxlr-skinbinding-" + Guid.NewGuid());
        string? oldHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? oldDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(data, "none"));
            foreach ((string id, string hidden) in new[] { ("rack-no-binding", "[\"binding\"]"), ("rack-binding", "[]") })
            {
                string folder = Path.Combine(data, "openxlr", "skins", id);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "skin.json"), """
                    {"schema":1,"name":"NAME","tokens":{},"layout":{"version":1,"regions":{
                      "inputs":{"template":"input-rack","strip":{"hidden":HIDDEN}},
                      "submixer":{"template":"mix-matrix","matrix":{"rowHead":{"hidden":HIDDEN}}}}}}
                    """.Replace("NAME", id).Replace("HIDDEN", hidden));
            }
            var model = (MainViewModel)window.DataContext!;
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse("""
                {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"}],
                 "channels":[{"id":"xlr1","name":"Mic 1","hardware":true,"levels":{"monitor":1},
                              "binding":{"kind":"source","source":"usb.mic","pair":0,"monoChannel":0},"bindingConnected":true},
                             {"id":"xlr2","name":"XLR 2","hardware":true,"levels":{"monitor":1},"binding":{"kind":"unit","pair":1}},
                             {"id":"music","name":"Music","levels":{"monitor":1},"volume":1}],
                 "inserts":{}}
                """)]);
            window.Show();
            foreach ((string id, bool shown) in new[] { ("rack-no-binding", false), ("rack-binding", true) })
            {
                SkinEntry entry = Assert.Single(SkinCatalog.Discover(), e => e.Id == id);
                Assert.Empty(entry.Errors);
                Assert.Empty(SkinService.Apply(entry));
                Layout(window, 1040, 1200);
                InputRackViewModel rack = Assert.IsType<InputRackViewModel>(window.InputRackModel);
                MixMatrixViewModel matrix = Assert.IsType<MixMatrixViewModel>(window.MixMatrix);
                foreach (InputStripViewModel strip in rack.Strips.Where(s => s.Channel.IsBindable))
                {
                    Assert.Equal(shown, strip.Has(SkinStripParts.Binding));
                    Assert.True(strip.Has(SkinStripParts.Meter), id);
                    Assert.True(strip.Has(SkinStripParts.Effects), id);
                }
                Assert.Equal(shown, window.GetVisualDescendants().OfType<BindingChip>()
                    .Any(c => c.IsEffectivelyVisible && c.GetVisualAncestors().OfType<InputRackView>().Any()));
                foreach (MatrixRowViewModel row in matrix.Rows.Where(r => r.Channel.IsBindable))
                {
                    Assert.Equal(shown, row.ShowsBinding);
                    Border head = SkinLayoutWindowTests.RowHead(window, row.Channel.Id);
                    Assert.Equal(shown, head.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "MatrixBindingLine").IsEffectivelyVisible);
                    Assert.Equal(shown, head.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MatrixBinding").IsEffectivelyVisible);
                    Assert.True(head.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == row.Channel.Name), id);
                }
                Capture(window, $"skin-{id}");
            }
        }
        finally
        {
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            window.Close();
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldHome);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", oldDirs);
            try { Directory.Delete(data, recursive: true); } catch (IOException) { }
        }
    }
}
