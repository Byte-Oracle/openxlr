using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using OpenXLR.UI.Views;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>Tab names, origin, capture state and controls fit the main and management surfaces.</summary>
internal static class BrowserTabsWindowTests
{
    internal static void Check()
    {
        FieldInfo culture = typeof(Localizer).GetField("_culture", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previous = culture.GetValue(null);
        try
        {
            foreach (string language in new[] { "en", "de", Localizer.Language }.Distinct(StringComparer.Ordinal))
            foreach (bool touch in new[] { false, true })
            {
                culture.SetValue(null, CultureInfo.GetCultureInfo(language));
                SkinService.ApplyControlSizing(touch);
                CheckAt(language, touch);
            }
        }
        finally
        {
            culture.SetValue(null, previous);
            SkinService.ApplyControlSizing(false);
        }
    }

    private static void CheckAt(string language, bool touch)
    {
        var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var main = new MainWindow(client);
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        var model = (MainViewModel)main.DataContext!;
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.DaemonConnected))!.SetValue(model, true);
        BrowserTabViewModelTests.Report(model, true, new JsonArray(
            BrowserTabViewModelTests.Tab(title: "A long browser tab title with a video, a playlist and more details that must remain within the row", channel: "music", remember: true, capturing: true),
            BrowserTabViewModelTests.Tab(id: 8, title: "", channel: null)));
        foreach (string name in new[] { "InputsTile", "HeadphonesTile", "MonitorTile", "SubmixerTile" })
            main.FindControl<Expander>(name)!.IsExpanded = false;
        var apps = new AppsWindow { DataContext = model };
        var column = new Window { Content = new BrowserTabsView { DataContext = model } };
        try
        {
            main.Show();
            Layout(main, 640, 1000);
            CheckRows(main, main.GetVisualDescendants().OfType<BrowserTabsView>().Single());
            Capture(main, $"browser-tabs-main-{language}-{(touch ? "touch" : "standard")}-640");

            apps.Show();
            Layout(apps, 620, 700);
            CheckRows(apps, apps.GetVisualDescendants().OfType<BrowserTabsView>().Single());
            Capture(apps, $"browser-tabs-manage-{language}-{(touch ? "touch" : "standard")}");

            // A skin can put APPLICATIONS in a narrow column. Controls wrap below the tab name.
            column.Show();
            Layout(column, 260, 1000);
            CheckRows(column, (BrowserTabsView)column.Content!);
            Capture(column, $"browser-tabs-column-{language}-{(touch ? "touch" : "standard")}-260");
        }
        finally
        {
            apps.Close(); column.Close(); main.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void CheckRows(Window window, BrowserTabsView view)
    {
        Border[] rows = [.. view.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "BrowserTabRow")];
        Assert.Equal(2, rows.Length);
        AssertNoOverlap(rows);
        foreach (Border row in rows)
        {
            AssertInside(row, window);
            Control[] controls = [.. row.GetVisualDescendants().OfType<Control>().Where(c => c.Name is
                "BrowserTabTitle" or "BrowserTabSite" or "BrowserTabChannel" or "BrowserTabRemember" or "BrowserTabForgetWebsite" or "BrowserTabStatus")];
            Assert.Equal(6, controls.Length);
            AssertNoOverlap(controls);
            foreach (Control control in controls) AssertInside(control, row);
            ComboBox picker = controls.OfType<ComboBox>().Single();
            Assert.True(picker.Bounds.Width >= 180);
            Assert.True(picker.IsEnabled);
            var title = (TextBlock)controls.Single(c => c.Name == "BrowserTabTitle");
            Assert.Equal(((BrowserTabViewModel)row.DataContext!).Title, ToolTip.GetTip(title));
        }
    }
}
