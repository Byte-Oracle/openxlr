using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using OpenXLR.UI.Views;

namespace OpenXLR.Tests;

public sealed partial class SkinWindowTests
{
    /// <summary>
    /// The Aux In and Line In readouts wear the skin in every place they are
    /// drawn, the input rack, the mix matrix and the classic INPUTS panel:
    /// their lettering takes the skin's value colour, a screen reader reads
    /// what they show, and a moving volume fader turns them to the volume.
    /// </summary>
    private static void TheInputReadoutsWearTheSkin()
    {
        string data = Path.Combine(Path.GetTempPath(), "openxlr-skinreadout-" + Guid.NewGuid());
        string? oldHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? oldDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        Color value = Color.Parse("#12ab34");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(data, "none"));
            foreach ((string id, string layout) in new[]
                     {
                         ("readouts", ""","layout":{"version":1,"regions":{"inputs":{"template":"input-rack"},"submixer":{"template":"mix-matrix"}}}"""),
                         ("readouts-classic", ""),
                     })
            {
                string folder = Path.Combine(data, "openxlr", "skins", id);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "skin.json"),
                    "{\"schema\":1,\"name\":\"" + id + "\",\"tokens\":{\"Ox.Text.Primary\":\"#12ab34\"}" + layout + "}");
            }
            var model = (MainViewModel)window.DataContext!;
            WindowLayoutTests.StopConnecting(window);
            model.CapAuxInput = true;
            model.CapLineInput = true;
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse("""
                {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"}],
                 "channels":[{"id":"xlr1","name":"XLR 1","hardware":true,"levels":{"monitor":1},"volume":1},
                             {"id":"aux","name":"Aux In","hardware":true,"levels":{"monitor":1},"volume":0.8},
                             {"id":"line","name":"Line In","hardware":true,"levels":{"monitor":1},"volume":0.6},
                             {"id":"music","name":"Music","levels":{"monitor":1},"volume":1}],
                 "inserts":{}}
                """)]);
            model.RefreshBindings();
            model.AuxLevelDb = -12;
            model.LineInGainDb = 20;
            window.Show();

            SkinEntry entry = Assert.Single(SkinCatalog.Discover(), e => e.Id == "readouts");
            Assert.Empty(entry.Errors);
            Assert.Empty(SkinService.Apply(entry));
            Layout(window, 1040, 1200);
            Assert.IsType<InputRackViewModel>(window.InputRackModel);
            Assert.IsType<MixMatrixViewModel>(window.MixMatrix);
            var rack = window.FindControl<InputRackView>("InputRack")!;
            foreach ((string id, string gain) in new[] { ("aux", "-12 dB"), ("line", "20 dB") })
            {
                ChannelViewModel channel = model.Channels.Single(c => c.Id == id);
                var hold = new ManualHold();
                channel.StartReadoutHold = hold.Start;
                Border strip = rack.GetVisualDescendants().OfType<Border>()
                    .Single(b => b.Name == "InputStrip" && b.DataContext is InputStripViewModel s && s.Channel.Id == id);
                Border head = SkinLayoutWindowTests.RowHead(window, id);
                TextBlock[] readouts =
                [
                    strip.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RackGainReadout"),
                    head.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "MatrixReadout"),
                ];
                foreach (TextBlock readout in readouts) Wears(readout, gain, channel.ReadoutName, $"{id} {readout.Name}");
                Assert.Equal(Localizer.Text(id == "aux" ? "USBAuxIn" : "LineInGain"), AutomationProperties.GetName(readouts[0]));

                // The rack's volume fader moves: both readouts read the volume, and the gain again after the hold.
                Slider volume = strip.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "RackVolume")
                    .GetVisualDescendants().OfType<Slider>().Single();
                FaderInput.SetIsAdjusting(volume, true);
                Pump(window);
                foreach (TextBlock readout in readouts) Wears(readout, channel.VolumeText, Localizer.Format("ChannelVolumeName", channel.Name), $"{id} {readout.Name} moving");
                FaderInput.SetIsAdjusting(volume, false);
                Pump(window);
                Assert.Equal(channel.VolumeText, readouts[1].Text);
                Assert.Single(hold.Handles).Elapse();
                Pump(window);
                foreach (TextBlock readout in readouts) Wears(readout, gain, channel.ReadoutName, $"{id} {readout.Name} after the hold");

                // The matrix's own volume fader does the same.
                Slider matrixVolume = head.GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "MatrixVolume");
                FaderInput.SetIsAdjusting(matrixVolume, true);
                Pump(window);
                Assert.Equal(channel.VolumeText, readouts[1].Text);
                FaderInput.SetIsAdjusting(matrixVolume, false);
                hold.Handles[1].Elapse();
                Pump(window);
                Assert.Equal(gain, readouts[1].Text);
            }
            // Line In draws its gain and no lock; Aux In keeps its lock.
            Border line = rack.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Name == "InputStrip" && b.DataContext is InputStripViewModel s && s.Channel.Id == "line");
            Assert.DoesNotContain(line.GetVisualDescendants().OfType<ToggleButton>(), t => t.IsEffectivelyVisible);
            Assert.Contains(line.GetVisualDescendants().OfType<Slider>(),
                s => s.IsEffectivelyVisible && AutomationProperties.GetName(s) == Localizer.Text("LineInGain"));

            // The classic INPUTS panel, under the same value colour.
            SkinEntry classic = Assert.Single(SkinCatalog.Discover(), e => e.Id == "readouts-classic");
            Assert.Empty(classic.Errors);
            Assert.Empty(SkinService.Apply(classic));
            Assert.Null(window.InputRackModel);
            Layout(window, 1040, 1200);
            Assert.True(window.FindControl<Grid>("LineInRow")!.IsEffectivelyVisible);
            Wears(window.FindControl<TextBlock>("AuxInReadout")!, "-12 dB", Localizer.Text("USBAuxIn"), "classic aux");
            Wears(window.FindControl<TextBlock>("LineInReadout")!, "20 dB", Localizer.Text("LineInGain"), "classic line");
        }
        finally
        {
            window.Close();
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldHome);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", oldDirs);
            try { Directory.Delete(data, recursive: true); } catch (IOException) { }
        }

        void Wears(TextBlock readout, string text, string name, string where)
        {
            Assert.True(readout.IsEffectivelyVisible, where);
            Assert.Equal(text, readout.Text);
            Assert.Equal(name, AutomationProperties.GetName(readout));
            Assert.Equal(name, ToolTip.GetTip(readout));
            Assert.Equal(value, Assert.IsAssignableFrom<ISolidColorBrush>(readout.Foreground).Color);
        }
    }
}
