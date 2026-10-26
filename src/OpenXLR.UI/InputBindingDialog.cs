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
/// The dialog that binds XLR 1, XLR 2 or a capture input, opened from its chip,
/// from Options and from the header. Hardware strips list the interface's
/// own jack first; capture inputs list its source alongside every other
/// external source, with a port chooser sized from the source. Replacing a strip
/// that has a connected interface behind it offers to keep the interface's
/// microphone as a capture channel, and choosing another supported unit that
/// is not driven offers to drive it as an additional interface, so its
/// controls can be shown on the strip. Each is a command the dialog sends
/// after the binding and reports on its own.
/// </summary>
internal static class InputBindingDialog
{
    /// <summary>A mono port or stereo pair, numbered from 1 and named by its channel positions when PipeWire gives them.</summary>
    internal sealed record PortChoice(int Index, string Label, bool Stereo = false)
    {
        public int Pair => Stereo ? Index : 0;
        public int? MonoChannel => Stereo ? null : Index;
    }

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

    /// <summary>Named stereo pairs and mono ports, including a saved choice on an absent port.</summary>
    internal static List<PortChoice> CapturePortChoices(AudioDeviceItem? device, int pair = 0, int? mono = null)
    {
        List<PortChoice> ports = PortChoices(device, mono);
        int count = Math.Clamp(device?.Channels ?? 64, 1, 64);
        var pairs = Enumerable.Range(0, Math.Max(1, count / 2)).ToList();
        if (mono is null && !pairs.Contains(pair)) pairs.Add(pair);
        return [.. pairs.Select(i => new PortChoice(i,
            Localizer.Format("CaptureStereoPorts", i * 2 + 1, i * 2 + 2,
                device?.PositionOf(i * 2) is { Length: > 0 } left && device.PositionOf(i * 2 + 1) is { Length: > 0 } right
                    ? $" ({left}, {right})" : ""), Stereo: true)),
            .. ports.Select(p => p with { Label = Localizer.Format("CaptureMonoPort", p.Label) })];
    }

    internal static ComboBox PortPicker(string name)
        => new() { Name = name, HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<PortChoice>((choice, _) =>
                new TextBlock { Text = choice?.Label, TextTrimming = TextTrimming.CharacterEllipsis }) };

    internal static void FillCapturePorts(ComboBox picker, AudioDeviceItem? device, int pair = 0, int? mono = null)
    {
        List<PortChoice> ports = CapturePortChoices(device, pair, mono);
        picker.ItemsSource = ports;
        picker.SelectedItem = ports.First(p => p.Pair == pair && p.MonoChannel == mono);
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

    /// <summary>The binding dialog, not yet shown; null when the channel takes no capture source.</summary>
    internal static Window? Build(MainViewModel main, string channelId)
    {
        if (main.Channels.FirstOrDefault(c => (c.IsBindable || c.CaptureSource is not null) && c.Id == channelId) is not { } strip) return null;
        bool capture = strip.CaptureSource is not null;
        int input = channelId == "xlr2" ? 2 : 1;
        string? current = capture ? strip.Binding?.Source ?? strip.CaptureSource : strip.IsSourceBound ? strip.Binding!.Source : null;

        // The primary interface's own nodes are its jacks, the first entry;
        // another supported unit's are offered like any source.
        string? primary = main.WaveInterfaces.FirstOrDefault(w => w.Active)?.Id;
        var choices = main.Inputs.Where(i => !i.IsOwn && (capture || i.DrivenUnit is null || i.DrivenUnit != primary))
            .Select(i => new DeviceChoice(i.Name, i.Description)).ToList();
        // The interface's own input: an XLR jack, or a USB microphone's capsule.
        if (!capture)
            choices.Insert(0, new(null, main.UnitXlrJacks ? Localizer.Format("InputBindingInterfaceJack", input)
                : Localizer.Format("InputBindingUnitCapsule", main.UnitName)));
        // A unit's node renamed since the binding (driven, or its card profile
        // switched) is the source the strip plays from now.
        if (current is not null && !choices.Any(c => c.Name == current) && strip.Binding?.Unit is { } boundUnit
            && main.Inputs.Where(i => !i.IsOwn && i.DrivenUnit == boundUnit).ToArray() is [var renamed])
            current = renamed.Name;
        if (current is not null && !choices.Any(c => c.Name == current))
            choices.Add(new(current, Localizer.Format("InputBindingSourceMissing", NodeNames.Readable(current))));

        var source = new ComboBox { Name = "InputBindingSource", ItemsSource = choices,
            SelectedItem = choices.First(c => c.Name == current), HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<DeviceChoice>((choice, _) =>
                new TextBlock { Text = choice?.Label, TextTrimming = TextTrimming.CharacterEllipsis }) };
        var port = PortPicker("InputBindingPort");
        Avalonia.Automation.AutomationProperties.SetName(source, Localizer.Format("InputBindingSourceName", strip.Name));
        Avalonia.Automation.AutomationProperties.SetName(port, Localizer.Text("InputBindingPort"));
        AudioDeviceItem? DeviceOf(DeviceChoice? choice) => choice?.Name is { } name ? main.Inputs.FirstOrDefault(i => i.Name == name) : null;
        void ListPorts(bool keep)
        {
            AudioDeviceItem? device = DeviceOf(source.SelectedItem as DeviceChoice);
            if (capture)
            {
                FillCapturePorts(port, device, keep ? strip.Binding?.Pair ?? strip.CapturePair : 0,
                    keep ? strip.Binding?.MonoChannel ?? strip.CaptureMonoChannel
                        : (strip.Binding?.MonoChannel ?? strip.CaptureMonoChannel) is not null ? 0 : null);
                return;
            }
            int? saved = keep && current is not null ? strip.Binding!.Port : null;
            List<PortChoice> ports = PortChoices(device, saved);
            port.ItemsSource = ports;
            port.SelectedItem = ports.FirstOrDefault(p => p.Index == (saved ?? 0)) ?? ports[0];
        }
        ListPorts(true);

        // The interface's own microphone, kept as an ordinary capture channel on
        // the unit's node at this strip's port, as the additional interfaces'
        // Add input channel makes one. Offered only while the strip is on the
        // interface and the unit's node is known, and only once.
        (string Node, string Unit)? unitNode = !capture && strip.IsUnitBound ? UnitNodeOf(main, strip) : null;
        int unitPort = (input - 1) * 2;
        bool kept = unitNode is { } known && main.Channels.Any(c => c.CaptureSource == known.Node
            && c.Binding is { } b && (b.MonoChannel is int mono ? mono == unitPort : b.Pair == input - 1));
        var keep = new CheckBox { Name = "KeepUnitMicrophone", Content = Localizer.Text("KeepInterfaceMicrophone"),
            IsChecked = main.DeviceConnected, IsVisible = false };
        // Another supported unit, attached but not driven: drive it as an
        // additional interface, ticked, since otherwise its gain is out of reach.
        var drive = new CheckBox { Name = "DriveInterface", Content = Localizer.Text("AlsoDriveThisInterface"), IsChecked = true, IsVisible = false };
        WaveInterfaceViewModel? UndrivenUnit(string? name)
            => name is not null && main.Inputs.FirstOrDefault(i => i.Name == name)?.DrivenUnit is { } id
                && main.WaveInterfaces.FirstOrDefault(w => w.Id == id) is { Active: false, Enabled: false, CanEnable: true } unit ? unit : null;
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
                    new TextBlock { Text = capture ? Localizer.Format("CaptureBindingHint", strip.Name) : Localizer.Format("InputBindingHint", strip.Name),
                        TextWrapping = TextWrapping.Wrap, Classes = { "hint" } },
                    source, bluetooth, new TextBlock { Text = Localizer.Text("InputBindingPort") }, port, keep, drive, note,
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
            drive.IsVisible = UndrivenUnit(chosen) is not null;
            drive.IsEnabled = !saving && !done;
            bluetooth.IsVisible = chosen?.StartsWith("bluez_input.", StringComparison.Ordinal) == true;
            save.IsEnabled = !saving && !done && (chosen is null || port.SelectedItem is PortChoice);
        }
        // A different source starts at its first port or pair, keeping the input's width.
        source.SelectionChanged += (_, _) => { ListPorts(false); RefreshControls(); };
        port.SelectionChanged += (_, _) => RefreshControls();
        RefreshControls();
        save.Click += async (_, _) =>
        {
            if (!save.IsEnabled || source.SelectedItem is not DeviceChoice choice) return;
            int chosenPort = choice.Name is null ? 0 : ((PortChoice)port.SelectedItem!).Index;
            bool keepUnit = keep.IsVisible && keep.IsChecked == true;
            WaveInterfaceViewModel? driveUnit = drive.IsVisible && drive.IsChecked == true ? UndrivenUnit(choice.Name) : null;
            saving = true;
            note.Text = "";
            RefreshControls();
            PortChoice? selected = port.SelectedItem as PortChoice;
            string? error = capture
                ? await main.SetCaptureSource(strip.Id, choice.Name!, selected!.Pair, selected.MonoChannel)
                : await main.SetInputBinding(strip.Id, choice.Name, chosenPort);
            if (error is not null) { note.Text = error; saving = false; RefreshControls(); return; }
            // The strip is bound; what follows is reported on its own.
            List<string> failures = [];
            if (keepUnit && unitNode is { } node)
            {
                string name = Localizer.Format("InterfaceInputChannelName", node.Unit, input);
                if (name.Length > 60) name = name[..60];
                if (await main.CreateCaptureChannel(name, node.Node, 0, unitPort) is { } failed)
                    failures.Add(Localizer.Format("InputBindingKeepFailed", strip.Name, failed));
            }
            if (driveUnit is not null && await main.SetWaveInterfaceEnabled(driveUnit.Id, true) is { } refused)
                failures.Add(Localizer.Format("InputBindingDriveFailed", strip.Name, driveUnit.Name, refused));
            if (failures.Count == 0) { dialog.Close(); return; }
            note.Text = string.Join("\n", failures);
            saving = false; done = true;
            cancel.Content = Localizer.Text("Close");
            RefreshControls();
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
