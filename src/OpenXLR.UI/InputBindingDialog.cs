using OpenXLR.UI.Localization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using OpenXLR.UI.Layout;

namespace OpenXLR.UI;

/// <summary>
/// The one dialog that binds XLR 1 or XLR 2, opened from the strip's chip,
/// from Options and from the header. It lists the interface's own jack, then
/// every capture source that is not one of OpenXLR's nodes or a supported
/// interface's, with a port chooser sized from the source. Replacing a strip
/// that has a connected interface behind it offers to keep the interface's
/// microphone as a capture channel, which is a second command the dialog
/// sends after the binding and reports on its own.
/// </summary>
internal static class InputBindingDialog
{
    /// <summary>One port of a capture source, numbered from 1 and named by its channel position when PipeWire gives one.</summary>
    internal sealed record PortChoice(int Index, string Label);

    /// <summary>
    /// The ports a source offers: one per channel when PipeWire reports the
    /// count, else the 64 the command accepts. A saved port past the count
    /// stays listed, so opening the dialog never changes the binding by itself.
    /// </summary>
    internal static List<PortChoice> PortChoices(AudioDeviceItem? device, int? keep = null)
    {
        int count = Math.Clamp(device?.Channels ?? 64, 1, 64);
        var ports = Enumerable.Range(0, count).Select(i => new PortChoice(i,
            device?.PositionOf(i) is { Length: > 0 } position
                ? Localizer.Format("InputBindingPortName", i + 1, position)
                : (i + 1).ToString(CultureInfo.CurrentCulture))).ToList();
        if (keep is int saved && saved >= count && saved < 64)
            ports.Add(new(saved, (saved + 1).ToString(CultureInfo.CurrentCulture)));
        return ports;
    }

    /// <summary>
    /// Open the dialog for a strip over <paramref name="origin"/>'s window. The
    /// window's model is found from the control's own data, so a chip in the
    /// main window, the popped-out mixer or Options opens the same dialog.
    /// </summary>
    internal static Task OpenFrom(Control origin, string channelId)
    {
        if (TopLevel.GetTopLevel(origin) is not Window owner || ModelOf(origin) is not { } main) return Task.CompletedTask;
        return ShowAsync(owner, main, channelId);
    }

    /// <summary>The window's model, from the nearest data that leads to it.</summary>
    internal static MainViewModel? ModelOf(Control origin)
    {
        foreach (Visual visual in origin.GetSelfAndVisualAncestors())
            if (visual is StyledElement { DataContext: { } data })
                switch (data)
                {
                    case MainViewModel main: return main;
                    case OptionsViewModel options: return options.Main;
                    case InputStripViewModel strip: return strip.Main;
                    case MatrixRowViewModel row: return row.Main;
                    case InputRackViewModel rack: return rack.Main;
                    case MixMatrixViewModel matrix: return matrix.Main;
                }
        return null;
    }

    internal static async Task ShowAsync(Window owner, MainViewModel main, string channelId)
    {
        if (Build(main, channelId) is { } dialog) await dialog.ShowDialog(owner);
    }

    /// <summary>The dialog for XLR 1 or XLR 2, not yet shown; null when the mixer has no such strip.</summary>
    internal static Window? Build(MainViewModel main, string channelId)
    {
        if (main.Channels.FirstOrDefault(c => c.IsBindable && c.Id == channelId) is not { } strip) return null;
        int input = channelId == "xlr2" ? 2 : 1;
        string? current = strip.IsSourceBound ? strip.Binding!.Source : null;

        // A supported interface's own nodes are left out until the window can
        // draw that unit's controls on the strip; the daemon refuses them.
        var choices = main.Inputs.Where(i => !i.IsOwn && i.DrivenUnit is null)
            .Select(i => new DeviceChoice(i.Name, i.Description)).ToList();
        // The interface's own input: an XLR jack, or a USB microphone's capsule.
        choices.Insert(0, new(null, main.UnitXlrJacks ? Localizer.Format("InputBindingInterfaceJack", input)
            : Localizer.Format("InputBindingUnitCapsule", main.UnitName)));
        if (current is not null && !choices.Any(c => c.Name == current))
            choices.Add(new(current, Localizer.Format("InputBindingSourceMissing", current)));

        var source = new ComboBox { Name = "InputBindingSource", ItemsSource = choices,
            SelectedItem = choices.First(c => c.Name == current), HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<DeviceChoice>((choice, _) =>
                new TextBlock { Text = choice?.Label, TextTrimming = TextTrimming.CharacterEllipsis }) };
        var port = new ComboBox { Name = "InputBindingPort", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<PortChoice>((choice, _) =>
                new TextBlock { Text = choice?.Label, TextTrimming = TextTrimming.CharacterEllipsis }) };
        Avalonia.Automation.AutomationProperties.SetName(source, Localizer.Format("InputBindingSourceName", strip.Name));
        Avalonia.Automation.AutomationProperties.SetName(port, Localizer.Text("InputBindingPort"));
        AudioDeviceItem? DeviceOf(DeviceChoice? choice) => choice?.Name is { } name ? main.Inputs.FirstOrDefault(i => i.Name == name) : null;
        void ListPorts(int? keep)
        {
            List<PortChoice> ports = PortChoices(DeviceOf(source.SelectedItem as DeviceChoice), keep);
            port.ItemsSource = ports;
            port.SelectedItem = ports.FirstOrDefault(p => p.Index == (keep ?? 0)) ?? ports[0];
        }
        ListPorts(current is null ? null : strip.Binding!.Port);

        // The interface's own microphone, kept as an ordinary capture channel on
        // the unit's node at this strip's port, as the additional interfaces'
        // Add input channel makes one. Offered only while the strip is on the
        // interface and the unit's node is known, and only once.
        (string Node, string Unit)? unitNode = strip.IsUnitBound ? UnitNodeOf(main, strip) : null;
        int unitPort = (input - 1) * 2;
        bool kept = unitNode is { } known && main.Channels.Any(c => c.CaptureSource == known.Node
            && c.Binding is { } b && (b.MonoChannel is int mono ? mono == unitPort : b.Pair == input - 1));
        var keep = new CheckBox { Name = "KeepUnitMicrophone", Content = Localizer.Text("KeepInterfaceMicrophone"),
            IsChecked = main.DeviceConnected, IsVisible = false };
        var bluetooth = new TextBlock { Name = "InputBindingBluetooth", Text = Localizer.Text("InputBindingBluetoothHint"),
            TextWrapping = TextWrapping.Wrap, Classes = { "hint" }, IsVisible = false };
        var save = new Button { Name = "ApplyInputBinding", Content = Localizer.Text("Save"), IsDefault = true };
        var cancel = new Button { Name = "CancelInputBinding", Content = Localizer.Text("Cancel"), IsCancel = true };
        var note = new TextBlock { Name = "InputBindingError", TextWrapping = TextWrapping.Wrap, Classes = { "warn" } };
        var dialog = new Window
        {
            Title = Localizer.Format("InputBindingTitle", strip.Name), Width = 480, Height = 420, MinWidth = 360, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Classes = { "dialog" },
            Content = new ScrollViewer { Content = new StackPanel
            {
                Margin = new Thickness(18), Spacing = 10,
                Children =
                {
                    new TextBlock { Text = Localizer.Format("InputBindingHint", strip.Name), TextWrapping = TextWrapping.Wrap, Classes = { "hint" } },
                    source, bluetooth, new TextBlock { Text = Localizer.Text("InputBindingPort") }, port, keep, note,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, save } },
                },
            } },
        };
        bool saving = false, done = false;
        void RefreshControls()
        {
            string? chosen = (source.SelectedItem as DeviceChoice)?.Name;
            source.IsEnabled = !saving && !done;
            port.IsEnabled = !saving && !done && chosen is not null;
            keep.IsVisible = chosen is not null && unitNode is not null && !kept;
            keep.IsEnabled = !saving && !done;
            bluetooth.IsVisible = chosen?.StartsWith("bluez_input.", StringComparison.Ordinal) == true;
            save.IsEnabled = !saving && !done && (chosen is null || port.SelectedItem is PortChoice);
        }
        // A different source starts at its first port, the usual microphone.
        source.SelectionChanged += (_, _) => { ListPorts(null); RefreshControls(); };
        port.SelectionChanged += (_, _) => RefreshControls();
        RefreshControls();
        save.Click += async (_, _) =>
        {
            if (!save.IsEnabled || source.SelectedItem is not DeviceChoice choice) return;
            int chosenPort = choice.Name is null ? 0 : ((PortChoice)port.SelectedItem!).Index;
            bool keepUnit = keep.IsVisible && keep.IsChecked == true;
            saving = true;
            note.Text = "";
            RefreshControls();
            string? error = await main.SetInputBinding(strip.Id, choice.Name, chosenPort);
            if (error is null && keepUnit && unitNode is { } node)
            {
                string name = Localizer.Format("InterfaceInputChannelName", node.Unit, input);
                if (name.Length > 60) name = name[..60];
                if (await main.CreateCaptureChannel(name, node.Node, 0, unitPort) is { } failed)
                {
                    // The strip is bound; only the capture channel is missing.
                    note.Text = Localizer.Format("InputBindingKeepFailed", strip.Name, failed);
                    saving = false; done = true;
                    cancel.Content = Localizer.Text("Close");
                    RefreshControls();
                    return;
                }
            }
            if (error is null) dialog.Close();
            else { note.Text = error; saving = false; RefreshControls(); }
        };
        cancel.Click += (_, _) => dialog.Close();
        return dialog;
    }

    /// <summary>
    /// The primary unit's capture node and name: the node its own sources
    /// match by the unit's capture hint, or the one node the daemon says
    /// belongs to it. Null when that is not one node.
    /// </summary>
    private static (string Node, string Unit)? UnitNodeOf(MainViewModel main, ChannelViewModel strip)
    {
        WaveInterfaceViewModel? unit = main.WaveInterfaces.FirstOrDefault(w => w.Active)
            ?? main.WaveInterfaces.FirstOrDefault(w => w.Id == strip.UnitInput?.Unit);
        if (unit is null) return null;
        string name = unit.Name.Length > 0 ? unit.Name : main.DeviceName;
        if (unit.Sources.Count == 1) return (unit.Sources[0].Name, name);
        AudioDeviceItem[] owned = [.. main.Inputs.Where(i => i.DrivenUnit == unit.Id)];
        return owned.Length == 1 ? (owned[0].Name, name) : null;
    }
}
