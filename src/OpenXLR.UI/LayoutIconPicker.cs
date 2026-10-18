using OpenXLR.UI.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace OpenXLR.UI;

/// <summary>
/// The icon choice offered where a channel or mix is created and in its
/// Appearance dialog: each entry is drawn beside its name. The order is the
/// default for the item's kind, no icon, the built-in set, the worn skin's
/// own icons, then the legacy text glyphs under a heading, so a choice an
/// older layout made can still be kept. It is offered whatever skin is worn,
/// because the icon belongs to the layout.
/// </summary>
public static class LayoutIconPicker
{
    /// <summary>Fill <paramref name="box"/> with the choices and select <paramref name="current"/>.</summary>
    public static void Fill(ComboBox box, string current, string defaultIcon)
    {
        ArgumentNullException.ThrowIfNull(box);
        box.Items.Clear();
        box.Items.Add(Entry("", defaultIcon, Localizer.Format("IconDefaultNamed", defaultIcon)));
        box.Items.Add(Entry(LayoutIcons.None, defaultIcon, Localizer.Text("IconNone")));
        var names = new List<string>(LayoutIcons.BuiltIn.Select(i => i.Name));
        names.AddRange(LayoutIconRegistry.SkinIcons.Keys.Where(n => LayoutIcons.Find(n) is null && n != LayoutIcons.None)
            .Order(StringComparer.Ordinal));
        // A name this window cannot draw (a skin icon of a skin not worn) stays selectable.
        if (LayoutIcons.IsName(current) && current != LayoutIcons.None && !names.Contains(current)) names.Add(current);
        foreach (string name in names) box.Items.Add(Entry(name, defaultIcon, name));
        box.Items.Add(new ComboBoxItem
        {
            Content = new TextBlock { Text = Localizer.Text("IconTextHeading"), Classes = { "hint" } },
            IsEnabled = false, Focusable = false,
        });
        foreach (string glyph in LayoutIcons.Glyphs) box.Items.Add(Entry(glyph, defaultIcon, glyph));
        Select(box, current);
    }

    /// <summary>The stored value of the selected entry; empty for the default.</summary>
    public static string Chosen(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    /// <summary>Change the default the first entry names and draws, keeping the selection.</summary>
    public static void SetDefault(ComboBox box, string defaultIcon)
    {
        string chosen = Chosen(box);
        Fill(box, chosen, defaultIcon);
    }

    private static void Select(ComboBox box, string current) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == current)
            ?? box.Items.OfType<ComboBoxItem>().First();

    private static ComboBoxItem Entry(string value, string defaultIcon, string label)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var icon = new LayoutIconBlock
        {
            Icon = value, Fallback = defaultIcon, MinWidth = 20, Margin = new Avalonia.Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = Avalonia.Media.TextAlignment.Center,
        };
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        var item = new ComboBoxItem { Content = grid, Tag = value };
        AutomationProperties.SetName(item, label);
        return item;
    }
}
