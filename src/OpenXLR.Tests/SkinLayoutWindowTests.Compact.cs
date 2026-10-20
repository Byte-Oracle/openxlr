using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// MONITOR as a toolbar and APPLICATIONS as a list: in a narrow side column,
/// where the toolbar stands in its two rows, and along the bottom of a dock,
/// where it is one row, at 640, 1040 and 2560 pixels, with nothing clipped
/// or overlapping; every selected output and its feed reachable from the
/// toolbar's picker; the list's channel picker assigning; and the way back
/// to the panel and the chips with the same controls and application objects.
/// </summary>
internal static partial class SkinLayoutWindowTests
{
    /// <summary>A narrow left column with the application list and the toolbar, the other sections beside it.</summary>
    private const string SideSkin = """
        {"schema":1,"name":"Side","tokens":{"Ox.Card.Background":"#24262c"},"layout":{"version":1,
         "main":{"kind":"grid","id":"desktop","columns":[{"size":280},{"star":1}],"rows":["auto","auto"],
          "children":[
           {"kind":"region","region":"header","row":0,"column":0,"columnSpan":2},
           {"kind":"stack","id":"side","orientation":"vertical","row":1,"column":0,"children":[
             {"kind":"region","region":"applications"},{"kind":"region","region":"monitor"}]},
           {"kind":"stack","id":"centre","orientation":"vertical","row":1,"column":1,"children":[
             {"kind":"region","region":"inputs"},{"kind":"region","region":"headphones"},{"kind":"region","region":"submixer"}]}]},
         "regions":{"monitor":{"template":"output-toolbar"},"applications":{"template":"application-list"}}}}
        """;

    /// <summary>The toolbar along the bottom of a dock, the list down its right side.</summary>
    private const string FooterSkin = """
        {"schema":1,"name":"Footer","tokens":{"Ox.Card.Background":"#262428"},"layout":{"version":1,
         "main":{"kind":"dock","id":"desktop","children":[
           {"kind":"region","region":"monitor","dock":"bottom"},
           {"kind":"region","region":"applications","dock":"right"},
           {"kind":"stack","id":"centre","orientation":"vertical","dock":"fill","children":[
             {"kind":"region","region":"inputs"},{"kind":"region","region":"headphones"},{"kind":"region","region":"submixer"}]}]},
         "regions":{"monitor":{"template":"output-toolbar"},"applications":{"template":"application-list","width":300}}}}
        """;

    private static readonly string[] Outputs = ["speakers", "headset", "hdmi"];

    private static void CheckCompactTemplates(string dir)
    {
        Write(dir, "side", SideSkin);
        Write(dir, "footer", FooterSkin);
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        try
        {
            var model = (MainViewModel)window.DataContext!;
            const string mixer = """
                {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},{"id":"monitor2","name":"Monitor B","kind":"monitor"},
                          {"id":"stream","name":"Stream","kind":"virtualMic"}],
                 "channels":[{"id":"browser","name":"Browser","levels":{"monitor":0.5}},{"id":"game","name":"Game"},
                             {"id":"voicechat","name":"Voice Chat"}],
                 "monitorOutputs":["speakers","headset"],"monitorFeeds":{"headset":"monitor2"},"outputVolume":0.6}
                """;
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse(mixer)]);
            typeof(MainViewModel).GetMethod("ApplyDevices", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse("""
                [{"kind":0,"name":"speakers","description":"Desk speakers with a long description from the driver"},
                 {"kind":0,"name":"headset","description":"USB headset"},{"kind":0,"name":"hdmi","description":"HDMI display"}]
                """), JsonNode.Parse(mixer)]);
            ChannelChoice[] choices = [new("browser", "Browser"), new("game", "Game"), new("voicechat", "Voice Chat with a long name")];
            foreach (string name in new[] { "Google Chrome with a very long window title", "Discord", "Steam" })
            {
                var app = new AppStreamViewModel(new DaemonClient("ws://127.0.0.1:1/ws"), name, name, choices);
                app.ApplyFromDaemon(name == "Steam" ? "game" : "browser", true, true);
                model.Apps.Add(app);
                model.ActiveApps.Add(app);
            }
            AppStreamViewModel[] apps = [.. model.ActiveApps];
            Assert.Equal(Outputs, model.MonitorOutputs.Select(o => o.Name));
            window.Show();
            Layout(window, 1040, 900);
            Border Card(string tile) => (Border)window.FindControl<Expander>(tile)!.Parent!;
            Border[] cards = [.. Usual.Select(Card)];
            var scroll = window.FindControl<ScrollViewer>("SectionScroll")!;
            var controls = window.FindControl<Grid>("MonitorControls")!;
            var picker = window.FindControl<DropDownButton>("MonitorDestination")!;
            var volume = window.FindControl<Slider>("OutputVolumeSlider")!;
            var readout = window.FindControl<TextBlock>("MonitorVolumeReadout")!;
            Assert.Equal(SkinLayoutTemplates.OutputPanel, window.MonitorTemplate);
            Assert.True(window.FindControl<TextBlock>("MonitorDevicesLabel")!.IsVisible);
            Assert.NotEmpty(ApplicationChips(window));

            foreach (string skin in new[] { "side", "footer" })
            {
                Wear(skin);
                foreach (double width in new[] { 640d, 1040, 2560 })
                {
                    Layout(window, width, 1200);
                    string where = $"{skin} at {width}";
                    Assert.Equal(SkinLayoutTemplates.OutputToolbar, window.MonitorTemplate);
                    Border[] shown = [.. cards.Where(c => c.IsVisible)];
                    Assert.Equal(5, shown.Length);
                    AssertNoOverlap(shown);
                    foreach (Border card in shown) AssertControlsInside(card, scroll);

                    // The toolbar: no labels, the picker, the volume and its readout apart and inside the section.
                    Assert.False(window.FindControl<TextBlock>("MonitorDevicesLabel")!.IsVisible, where);
                    Assert.False(window.FindControl<TextBlock>("MonitorVolumeLabel")!.IsVisible, where);
                    Assert.Equal(OpenXLR.UI.Localization.Localizer.Text("Devices"), AutomationProperties.GetName(picker));
                    Assert.Equal(OpenXLR.UI.Localization.Localizer.Text("Volume"), AutomationProperties.GetName(volume));
                    foreach (Control part in new Control[] { picker, volume, readout }) AssertInside(part, Card("MonitorTile"));
                    AssertNoOverlap([picker, volume, readout]);
                    bool stacked = controls.Bounds.Width < SkinLayoutDefaults.MonitorStackWidth;
                    double pickerTop = picker.TranslatePoint(default, controls)!.Value.Y;
                    double volumeTop = volume.TranslatePoint(default, controls)!.Value.Y;
                    Assert.True(stacked ? volumeTop > pickerTop + picker.Bounds.Height - 0.5 : Math.Abs(volumeTop - pickerTop) < picker.Bounds.Height,
                        $"the toolbar is {controls.Bounds.Width} wide and {(stacked ? "not stacked" : "stacked")} {where}");
                    // In the 280 pixel column the toolbar is always in its two rows.
                    if (skin == "side")
                    {
                        Assert.True(stacked, where);
                        Assert.Equal(280, Card("MonitorTile").Bounds.Width, 0.5);
                        Assert.Equal(280, Card("ApplicationsTile").Bounds.Width, 0.5);
                    }
                    else Assert.Equal(300, Card("ApplicationsTile").Bounds.Width, 0.5);
                    Assert.True(volume.Bounds.Width >= 100, $"the volume is {volume.Bounds.Width} wide {where}");

                    // The list: one line per application, its parts apart and inside the line, the name shortened, not clipped.
                    Border[] lines = ApplicationLines(window);
                    Assert.Equal(apps.Length, lines.Length);
                    Assert.Empty(ApplicationChips(window));
                    AssertNoOverlap(lines);
                    for (int i = 1; i < lines.Length; i++)
                        Assert.True(lines[i].TranslatePoint(default, window)!.Value.Y > lines[i - 1].TranslatePoint(default, window)!.Value.Y, where);
                    foreach (Border line in lines)
                    {
                        AssertInside(line, Card("ApplicationsTile"));
                        Control[] parts = [.. ((Grid)line.Child!).Children.Where(c => c.IsVisible)];
                        AssertNoOverlap(parts);
                        foreach (Control part in parts) AssertInside(part, line);
                        TextBlock name = parts.OfType<TextBlock>().Single();
                        Assert.True(name.Bounds.Width > 20, $"a name is {name.Bounds.Width} wide {where}");
                    }
                    Capture(window, $"skin-layout-compact-{skin}-{width}");
                }
            }

            // Every output stays reachable from the toolbar's picker, with each selected output's feed.
            Layout(window, 1040, 1200);
            picker.Flyout!.ShowAt(picker);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var list = (ItemsControl)((Flyout)picker.Flyout).Content!;
            CheckBox[] boxes = [.. list.GetVisualDescendants().OfType<CheckBox>()];
            Assert.Equal(Outputs, boxes.Select(b => ((MonitorOutputItem)b.DataContext!).Name));
            Assert.All(boxes, b => Assert.True(b.IsEffectivelyVisible));
            Assert.Equal([true, true, false], boxes.Select(b => b.IsChecked == true));
            ComboBox[] feeds = [.. list.GetVisualDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible)];
            Assert.Equal(2, feeds.Length);
            Assert.Equal("monitor2", ((MixOption)feeds[1].SelectedItem!).Id);
            boxes[2].IsChecked = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(model.MonitorOutputs[2].IsSelected);
            Assert.Equal(3, list.GetVisualDescendants().OfType<ComboBox>().Count(c => c.IsEffectivelyVisible));
            picker.Flyout.Hide();
            volume.Value = 0.42;
            Assert.Equal(0.42, model.OutputVolume);

            // The list's picker assigns the application, as a chip's does.
            ComboBox assign = ApplicationLines(window).Single(l => l.DataContext == apps[1])
                .GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ApplicationChannel");
            Assert.Equal(OpenXLR.UI.Localization.Localizer.Format("ApplicationChannelName", "Discord"), AutomationProperties.GetName(assign));
            Assert.Equal(choices.Select(c => c.Name), assign.Items.Cast<ChannelChoice>().Select(c => c.Name));
            assign.SelectedItem = assign.Items.Cast<ChannelChoice>().Single(c => c.Id == "voicechat");
            Assert.Equal("voicechat", apps[1].SelectedChannel!.Id);

            // Back to the panel and the chips: the same controls and the same applications.
            Wear(SkinPackage.DefaultId);
            Layout(window, 1040, 900);
            Assert.Equal(SkinLayoutTemplates.OutputPanel, window.MonitorTemplate);
            Assert.True(window.FindControl<TextBlock>("MonitorDevicesLabel")!.IsVisible);
            Assert.Same(picker, window.FindControl<DropDownButton>("MonitorDestination"));
            Assert.Same(controls, picker.Parent);
            Assert.Empty(ApplicationLines(window));
            Assert.Equal(apps.Length, ApplicationChips(window).Length);
            Assert.Equal(apps, model.ActiveApps);
            Assert.Equal("voicechat", apps[1].SelectedChannel!.Id);
            Assert.Equal(1, Grid.GetColumn(picker));
        }
        finally
        {
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    private static Border[] ApplicationLines(Window window) =>
        [.. window.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "ApplicationLine" && b.IsEffectivelyVisible)];

    private static Border[] ApplicationChips(Window window) =>
        [.. window.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "ApplicationChip" && b.IsEffectivelyVisible)];

    private static Border[] ApplicationTiles(Window window) =>
        [.. window.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "ApplicationTile" && b.IsEffectivelyVisible)];
}
