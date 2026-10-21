using System.Text.Json.Nodes;

namespace OpenXLR.Tui;

/// <summary>
/// The hardware: the interface's microphone inputs with their processing,
/// what feeds XLR 1 and XLR 2, where each microphone's EQ runs, the
/// headphone outputs, the direct monitor blend and the USB Aux return. A control the active device does not
/// advertise is drawn faded rather than hidden, so the same page describes
/// every interface.
///
/// An input's group is headed by the channel that carries it, which is its
/// own strip until that strip is bound to another microphone. A binding
/// choice is a draft until Apply sends it, because each change mutes the
/// strip in the monitor mixes; stepping through the sources never rewires
/// the microphone on the way.
/// </summary>
internal sealed class InputsView : View
{
    private readonly RowList _list = new();
    private readonly Dictionary<string, (string? Source, int Port, bool Drive)> _drafts = [];
    // The request in flight, which is the link's own task, so a reply has
    // completed it by the time the link reports the change and a frame is
    // drawn. Driving a unit is a second request, sent once the binding is in.
    private Task<string?>? _applying;
    private string _applyingStrip = string.Empty;
    private string _applyingLabel = string.Empty;
    private string? _driveAfter;
    private bool _driving;

    public override string Title => "Inputs";

    public override string Keys => "Up/Down control  Space toggle  Left/Right or -/+ change  [/] fine  Enter apply";

    public override void Draw(Screen screen, Rect area, App app)
    {
        if (_applying is { IsCompleted: true } done)
        {
            string? error = done.IsCompletedSuccessfully ? done.Result : "The daemon did not answer";
            _applying = null;
            if (error is { Length: > 0 })
            {
                app.Say(_driving ? $"The strip is bound, but the interface was not driven: {error}" : error);
                _driveAfter = null;
            }
            else if (_driveAfter is { } unit)
            {
                _driveAfter = null;
                _driving = true;
                _applying = app.Link.Request("setWaveInterfaceEnabled", body =>
                {
                    body["device"] = unit;
                    body["value"] = true;
                });
            }
            else app.Say(_applyingLabel);
            // The binding is in once its own request succeeded, whatever driving the unit says.
            if (_driving || error is null) _drafts.Remove(_applyingStrip);
        }
        Snapshot? state = State(app);
        if (state is null)
        {
            screen.Text(area.X + 2, area.Y + 1, "Waiting for the daemon", app.Theme.TextMuted, app.Theme.Window);
            return;
        }
        _list.DrawGroups(screen, area, app.Theme, Build(app, state));
    }

    public override bool Handle(KeyPress key, App app)
    {
        Snapshot? state = State(app);
        return state is not null && _list.Handle(key, Build(app, state));
    }

    private List<Row> Build(App app, Snapshot state)
    {
        List<Row> rows = [];
        DaemonLink link = app.Link;
        int inputs = Math.Max(1, state.Count("xlrInputs"));
        const string off = "not on this device";

        for (int input = 1; input <= inputs; input++)
        {
            if (state.Carrier(input) is { CaptureSource: not null, Mono: true }) continue;
            rows.Add(new HeadingRow(state.InputHeading(input)) { Dial = true });
            PrimaryInput(rows, link, state, input);
        }

        AdditionalInputs(rows, link, state);
        Bindings(rows, app, state);
        Equalisers(rows, link, state);

        // The software low cut and ClipGuard run in the mixer, so they act on
        // any microphone on XLR 1; the gain lock guards the interface's gain.
        rows.Add(new HeadingRow("Software"));
        rows.Add(new ToggleRow("Gain lock", state.Flag("gainLocked"),
            value => Set(link, "gainLock", value)) { Enabled = state.Can("gain"), DisabledText = off });
        string[] lowCuts = ["off", "80 Hz", "120 Hz"];
        int lowCutAt = state.Mixer.LowCutHz switch { 80 => 1, 120 => 2, _ => 0 };
        rows.Add(new ChoiceRow("Software low cut", lowCuts, lowCutAt, index =>
            link.Send("setLowCutHz", body => body["value"] = index switch { 1 => 80, 2 => 120, _ => 0 })));
        rows.Add(new ToggleRow("Software ClipGuard", state.Mixer.SoftClipGuard,
            value => link.Send("setSoftClipGuard", body => body["value"] = value))
        { Enabled = state.Mixer.SoftClipGuardAvailable, Note = state.Mixer.SoftClipGuardAvailable ? null : "needs swh-plugins" });

        rows.Add(new HeadingRow("Headphones"));
        rows.Add(new NumberRow("Phones 1", Percent(state.Number("hpVolumeDb")), 0, 100, 5, Widgets.Percent0,
            value => Set(link, "hpVolumeDb", Decibels(value))) { Enabled = state.Can("hpVolume"), DisabledText = off });
        if (state.Count("hpOutputs") > 1)
            rows.Add(new NumberRow("Phones 2", Percent(state.Number("hp2VolumeDb")), 0, 100, 5, Widgets.Percent0,
                value => Set(link, "hp2VolumeDb", Decibels(value))) { Enabled = state.Can("hpVolume") });
        rows.Add(new NumberRow("Mic to PC blend", state.Number("crossfade"), 0, 200, 10,
            value => value <= 0 ? "mic only" : value >= 200 ? "PC only" : $"{value:0}",
            value => Set(link, "crossfade", (int)Math.Round(value))) { Enabled = state.Can("crossfade"), DisabledText = off });
        rows.Add(new ToggleRow("Low impedance", state.Flag("lowImpedance"),
            value => Set(link, "lowImpedance", value)) { Enabled = state.Can("lowImpedance"), DisabledText = off });

        if (state.Can("outputRouting"))
        {
            rows.Add(new HeadingRow("Hardware outputs"));
            rows.Add(new ToggleRow("Phones 1 out", state.Flag("outHp1"), value => Set(link, "outHp1", value)));
            rows.Add(new ToggleRow("Phones 2 out", state.Flag("outHp2"), value => Set(link, "outHp2", value))
            { Enabled = state.Count("hpOutputs") > 1 });
            rows.Add(new ToggleRow("USB Aux out", state.Flag("outUsbAux"), value => Set(link, "outUsbAux", value)));
            rows.Add(new ToggleRow("Line out", state.Flag("outLineOut"), value => Set(link, "outLineOut", value)));
        }

        if (state.Can("auxInput"))
        {
            rows.Add(new HeadingRow("USB Aux in"));
            rows.Add(new NumberRow("Level", state.Number("auxLevelDb"), -60, 0, 2,
                value => $"{value:0} dB", value => Set(link, "auxLevelDb", value)));
            rows.Add(new ToggleRow("Level lock", state.Flag("auxLevelLock"),
                value => Set(link, "auxLevelLock", value)));
            rows.Add(new ToggleRow("Aux port feed", state.Mixer.AuxPortEnabled,
                value => link.Send("setAuxPortEnabled", body => body["value"] = value)));
        }

        return rows;
    }

    private static void PrimaryInput(List<Row> rows, DaemonLink link, Snapshot state, int input, string? device = null)
    {
        string suffix = input == 1 ? string.Empty : "2";
        int gainMax = state.Count("gainMaxDb") is int max and > 0 ? max : 80;
        const string off = "not on this device";
        void Write(string control, JsonNode value) => link.Send(device is null ? "set" : "setWaveControl", body =>
        {
            if (device is not null) body["device"] = device;
            body["control"] = control;
            body["value"] = value;
        });
        rows.Add(new NumberRow($"Gain", state.Number($"gain{suffix}Db"), 0, gainMax, 1,
            value => $"{value:0} dB", value => Write($"gain{suffix}", (int)Math.Round(value)))
        {
            Enabled = state.Can("gain"), FineStep = 1, DisabledText = off,
            Note = state.Flag("gainLocked") ? "locked" : null,
        });
        rows.Add(new ToggleRow("Mute", state.Flag($"mute{suffix}"),
            value => Write($"mute{suffix}", value)) { Enabled = state.Can("mute"), DisabledText = off });
        rows.Add(new ToggleRow("Low cut", state.Flag($"lowCut{suffix}"),
            value => Write($"lowCut{suffix}", value)) { Enabled = state.Can("lowCut"), DisabledText = off });
        rows.Add(new ToggleRow("Expander", state.Flag($"expander{suffix}"),
            value => Write($"expander{suffix}", value)) { Enabled = state.Can("expander"), DisabledText = off });
        rows.Add(new ToggleRow("Voice tune", state.Flag($"voiceTune{suffix}"),
            value => Write($"voiceTune{suffix}", value)) { Enabled = state.Can("voiceTune"), DisabledText = off });
        rows.Add(new NumberRow("VT strength", state.Number($"voiceTuneStrength{suffix}"), 0, 100, 5,
            Widgets.Percent0, value => Write($"voiceTuneStrength{suffix}", (int)Math.Round(value)))
        { Enabled = state.Can("voiceTune"), DisabledText = off });
        rows.Add(new ToggleRow("Phantom power", state.Flag($"phantom{suffix}"),
            value => Write($"phantom{suffix}", value))
        {
            Enabled = state.Can("phantom"), DisabledText = off,
            Note = state.Flag($"phantomSettling{suffix}") ? "settling" : null,
        });
        rows.Add(new ToggleRow("ClipGuard", state.Flag($"clipGuard{suffix}"),
            value => Write($"clipGuard{suffix}", value)) { Enabled = state.Can("clipGuard"), DisabledText = off });
        rows.Add(new ToggleRow("Compressor", state.Flag($"compressor{suffix}"),
            value => Write($"compressor{suffix}", value)) { Enabled = state.Can("compressor"), DisabledText = off });
    }

    /// <summary>
    /// What feeds XLR 1 and XLR 2: the interface's own input or one port of
    /// another capture source, each with its port, Apply and the strip's
    /// connection. The sources are the ones the window offers, which leaves
    /// out an attached supported interface's nodes for now.
    /// </summary>
    private void Bindings(List<Row> rows, App app, Snapshot state)
    {
        List<ChannelEntry> strips = [.. state.Mixer.Channels.Where(channel => channel.Bindable)];
        if (strips.Count == 0) return;
        rows.Add(new HeadingRow("Bindings"));
        List<DeviceEntry> sources = state.BindingChoices;
        foreach (ChannelEntry strip in strips)
        {
            string? bound = state.BoundSource(strip);
            int boundPort = (strip.Binding?.MonoChannel ?? 0) + 1;
            // The interface's own input: an XLR jack, or a USB microphone's capsule.
            List<(string? Name, string Label)> choices = [(null, state.UnitInputLabel(strip))];
            foreach (DeviceEntry source in sources)
            {
                string label = source.Description is { Length: > 0 } description ? description : source.Name;
                // Two microphones of one model share a description; the node name tells them apart.
                if (sources.Count(other => other.Description == source.Description) > 1) label = $"{label} ({source.Name})";
                choices.Add((source.Name, label));
            }
            // A saved source that is unplugged, or that the list leaves out, stays chosen, as the daemon keeps it.
            if (bound is not null && !choices.Any(choice => choice.Name == bound))
                choices.Add((bound, $"{bound} (missing)"));

            (string? chosen, int port, bool drive) = _drafts.TryGetValue(strip.Id, out var draft) ? draft : (bound, boundPort, true);
            int at = choices.FindIndex(choice => choice.Name == chosen);
            if (at < 0) (at, chosen, port) = (0, null, 1);   // a drafted source that has since gone
            DeviceEntry? device = sources.FirstOrDefault(source => source.Name == chosen);
            int ports = device?.Channels is int count and > 0 ? Math.Min(count, 64) : 64;
            port = Math.Clamp(port, 1, ports);
            string id = strip.Id;
            string name = strip.Name;
            rows.Add(new ChoiceRow(name, [.. choices.Select(choice => choice.Label)], at,
                index => _drafts[id] = (choices[index].Name, 1, drive)));
            rows.Add(new NumberRow($"{name} port", port, 1, ports, 1, value => PortLabel(device, (int)value),
                value => _drafts[id] = (chosen, (int)value, drive))
            { Enabled = chosen is not null, Integer = true, FineStep = 1, DisabledText = "the interface takes no port" });
            if (chosen?.StartsWith("bluez_input.", StringComparison.Ordinal) == true)
                rows.Add(new TextRow("Bluetooth", "stays in headset mode while bound"));
            // Another supported unit that is not driven yet: drive it as an
            // additional interface too, on by default, since otherwise its
            // gain is out of reach.
            WaveInterfaceEntry? undriven = state.UndrivenUnit(chosen);
            if (undriven is not null)
                rows.Add(new ToggleRow("Also drive unit", drive, value => _drafts[id] = (chosen, port, value))
                { Note = undriven.Name });
            bool changed = chosen != bound || chosen is not null && port != boundPort;
            string done = chosen is null ? $"{name} takes {choices[0].Label}" : $"{name} takes {choices[at].Label}, port {port}";
            string? driveUnit = undriven is not null && drive ? undriven.Id : null;
            rows.Add(new ActionRow($"Apply to {name}", _applying is null ? "apply" : "sending",
                () => Apply(app, id, chosen, port, driveUnit, done))
            { Note = changed ? "not applied" : null });
            rows.Add(new TextRow($"{name} feed", strip.BindingWarning is { Length: > 0 } warning ? warning
                : strip.BindingConnected ? $"linked, {state.BindingLabel(strip)}" : "not linked"));
        }
    }

    /// <summary>
    /// Each microphone's EQ: what runs it, and where a unit that runs the EQ
    /// itself carries the microphone, the choice between the unit and this
    /// computer, which setEqProcessing sends. The bands are edited in the window.
    /// </summary>
    private static void Equalisers(List<Row> rows, DaemonLink link, Snapshot state)
    {
        List<ChannelEntry> microphones = [.. state.Mixer.Shown.Where(channel => channel.Eq is not null)];
        if (microphones.Count == 0) return;
        rows.Add(new HeadingRow("Microphone EQ"));
        string[] places = ["on the unit", "in software"];
        foreach (ChannelEntry channel in microphones)
        {
            EqEntry eq = channel.Eq!;
            string id = channel.Id;
            rows.Add(new TextRow($"{channel.Name} EQ", eq.Status));
            if (!eq.Offered) continue;
            rows.Add(new ChoiceRow($"{channel.Name} EQ runs", places, eq.Processing == "software" ? 1 : 0,
                index => link.Send("setEqProcessing", body =>
                {
                    body["channel"] = id;
                    body["value"] = index == 1 ? "software" : "device";
                })));
            if (eq.Why is { Length: > 0 } why) rows.Add(new TextRow($"{channel.Name} unit", why));
        }
    }

    /// <summary>A port numbered from 1, with the channel PipeWire names for it when it reports one.</summary>
    private static string PortLabel(DeviceEntry? device, int port) =>
        device?.Positions is { } positions && port >= 1 && port <= positions.Count && device.Channels == positions.Count
            ? $"{port} ({positions[port - 1]})" : $"{port}";

    private void Apply(App app, string strip, string? source, int port, string? driveUnit, string label)
    {
        if (_applying is not null) return;
        _applyingStrip = strip;
        _applyingLabel = label;
        _driveAfter = driveUnit;
        _driving = false;
        _applying = app.Link.Request("setInputBinding", body =>
        {
            body["channel"] = strip;
            body["source"] = source;
            if (source is not null) body["captureMonoChannel"] = port - 1;
        });
    }

    /// <summary>
    /// Capture microphones with their source and connection, and the controls
    /// of each carried unit, written with setWaveControl.
    /// </summary>
    private static void AdditionalInputs(List<Row> rows, DaemonLink link, Snapshot state)
    {
        foreach (ChannelEntry channel in state.Mixer.Channels)
        {
            bool capture = channel.CaptureSource is not null && channel.Mono;
            if (capture)
            {
                rows.Add(new HeadingRow(channel.Name));
                string source = state.BoundSource(channel)!;
                rows.Add(new TextRow("Source", $"{state.SourceName(source)}, port {(channel.Binding?.MonoChannel ?? 0) + 1}"));
                rows.Add(new TextRow("Connection", channel.BindingWarning is { Length: > 0 } warning ? warning
                    : channel.CaptureConnected ? "linked" : "not linked"));
                if (channel.UnitInput is { Primary: true } primary)
                {
                    rows.Add(new HeadingRow($"{state.Device?.Model ?? "Interface"} input {primary.Input} on {channel.Name}") { Dial = state.Can("gain") });
                    PrimaryInput(rows, link, state, primary.Input, primary.Unit);
                }
            }
            if (channel.UnitInput is not { Primary: false } carried || state.Additional(carried.Unit) is not { Driven: true } unit)
                continue;
            string suffix = carried.Input == 2 ? "2" : string.Empty;
            UnitControl Control(string name) => new(unit.Id, name + suffix);
            int gainMax = unit.Count("gainMaxDb") is int max and > 0 ? max : 80;
            rows.Add(new HeadingRow($"{unit.Name} input {carried.Input} on {channel.Name}") { Dial = unit.Can("gain") });
            if (unit.Can("gain"))
                rows.Add(new NumberRow("Gain", unit.Number($"gain{suffix}Db"), 0, gainMax, 1, value => $"{value:0} dB",
                    value => link.Send("setWaveControl", body =>
                    {
                        body["device"] = unit.Id;
                        body["control"] = $"gain{suffix}";
                        body["value"] = (int)Math.Round(value);
                    }))
                { FineStep = 1, Note = unit.Flag("gainLocked") ? "locked" : null });
            foreach ((string label, string name) in new[] { ("Mute", "mute"), ("Low cut", "lowCut"), ("ClipGuard", "clipGuard"), ("Phantom power", "phantom") })
                if (unit.Can(name))
                    rows.Add(new ToggleRow(label, unit.Flag(name + suffix), value => Snapshot.Send(link, Control(name), value)));
        }
    }

    /// <summary>The headphone scale the window shows: -60 dB is 0% and 0 dB is 100%.</summary>
    private static double Percent(double decibels) => Math.Clamp((60.0 + decibels) / 60.0 * 100.0, 0, 100);

    private static double Decibels(double percent) => -60.0 + Math.Clamp(percent, 0, 100) * 0.6;

    private static void Set(DaemonLink link, string control, bool value) =>
        link.Send("set", body => { body["control"] = control; body["value"] = value; });

    private static void Set(DaemonLink link, string control, int value) =>
        link.Send("set", body => { body["control"] = control; body["value"] = value; });

    private static void Set(DaemonLink link, string control, double value) =>
        link.Send("set", body => { body["control"] = control; body["value"] = Math.Round(value, 2); });
}
