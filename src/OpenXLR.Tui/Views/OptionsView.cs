namespace OpenXLR.Tui;

/// <summary>
/// The appearance and what the terminal knows about the connection. The skin
/// list is the same one the window shows, read from the same folders, and the
/// choice is written back to the same file, so picking Gruvbox here is
/// picking it in the window too. Material's mode is the same shared choice.
///
/// The primary microphone comes first: XLR 1 follows the Elgato interface, or
/// takes one mono port of any other capture source, as in the window's
/// Options. A choice is a draft until Apply sends it, so stepping through the
/// sources never rewires the microphone on the way.
/// </summary>
internal sealed class OptionsView : View
{
    private static readonly string[] Modes = [AppearanceModes.System, AppearanceModes.Light, AppearanceModes.Dark];
    private static readonly string[] ModeNames = ["System", "Light", "Dark"];
    private readonly RowList _list = new();
    private IReadOnlyList<SkinEntry> _skins = SkinCatalog.Scan();
    private bool _drafted;
    private string? _draftSource;
    private int _draftPort = 1;
    private Task<string?>? _applying;
    private string _applyingLabel = string.Empty;

    public override string Title => "Options";

    public override string Keys => "Left/Right mode or source  Enter use skin or apply  R reload skins";

    public override void Draw(Screen screen, Rect area, App app)
    {
        if (_applying is { IsCompleted: true } done)
        {
            string? error = done.IsCompletedSuccessfully ? done.Result : "The daemon did not answer";
            _applying = null;
            if (error is { Length: > 0 }) app.Say(error);
            else
            {
                _drafted = false;
                app.Say(_applyingLabel);
            }
        }
        _list.Draw(screen, area, app.Theme, Build(app), labelWidth: 26);
    }

    public override bool Handle(KeyPress key, App app)
    {
        if (key.Key == Key.Char && key.Char == 'R')
        {
            _skins = SkinCatalog.Scan();
            app.Say($"{_skins.Count} skins");
            return true;
        }
        return _list.Handle(key, Build(app));
    }

    private List<Row> Build(App app)
    {
        List<Row> rows = [];
        if (State(app) is { } current) PrimaryInput(rows, app, current);
        rows.AddRange(
        [
            new HeadingRow("Appearance"),
            new ChoiceRow("Material mode", ModeNames, Array.IndexOf(Modes, app.AppearanceMode), index =>
            {
                app.UseAppearance(Modes[index]);
                string chosen = $"Material mode {ModeNames[index]}";
                if (!UiSettingsFile.WriteAppearanceMode(Modes[index]))
                    app.Say($"{chosen} is on for this run, the saved choice could not be written");
                else if (app.Theme.Id != "default")
                    app.Say($"{chosen} is saved; it applies when Material is the skin");
                else app.Say($"{chosen} is the appearance here and in the window");
            }),
        ]);
        foreach (SkinEntry skin in _skins) rows.Add(new SkinRow(skin, app));

        rows.Add(new HeadingRow("Daemon"));
        Snapshot? state = State(app);
        rows.Add(new TextRow("Connection", app.Link.Status));
        rows.Add(new TextRow("Version", state?.DaemonVersion is { Length: > 0 } version ? version : "unknown"));
        if (state?.Device is { } device)
            rows.Add(new TextRow("Interface", $"{device.Vendor} {device.Model} ({device.UsbId})"));
        if (state?.Warning is { Length: > 0 } warning) rows.Add(new TextRow("Warning", warning));
        if (state?.Mixer.LayoutWarning is { Length: > 0 } layout) rows.Add(new TextRow("Layout", layout));
        rows.Add(new ActionRow("Fresh state", "refresh", () =>
        {
            app.Link.Send("getState");
            app.Say("Asked for a state");
        }));

        return rows;
    }

    /// <summary>The XLR 1 source: what it is now, the draft choice, its port, and Apply.</summary>
    private void PrimaryInput(List<Row> rows, App app, Snapshot state)
    {
        PrimaryInputEntry? bound = state.Mixer.PrimaryInput is { Source.Length: > 0 } input ? input : null;
        rows.Add(new HeadingRow("Primary microphone"));
        rows.Add(new TextRow("XLR 1", state.GenericLabel is { } generic
            ? $"generic, {generic}{(state.Mixer.PrimaryInputConnected ? string.Empty : ", not linked")}"
            : "automatic, the Elgato interface"));

        List<DeviceEntry> sources = state.PrimaryInputChoices;
        List<(string? Name, string Label)> choices = [(null, "Automatic Elgato interface")];
        foreach (DeviceEntry source in sources)
        {
            string label = source.Description is { Length: > 0 } description ? description : source.Name;
            // Two microphones of one model share a description; the node name tells them apart.
            if (sources.Count(other => other.Description == source.Description) > 1) label = $"{label} ({source.Name})";
            choices.Add((source.Name, label));
        }
        // A saved source that is unplugged stays chosen, as the daemon keeps it.
        if (bound is not null && !choices.Any(choice => choice.Name == bound.Source))
            choices.Add((bound.Source, $"{bound.Source} (missing)"));

        string? chosen = _drafted ? _draftSource : bound?.Source;
        int port = _drafted ? _draftPort : (bound?.Channel ?? 0) + 1;
        int at = choices.FindIndex(choice => choice.Name == chosen);
        if (at < 0) (at, chosen) = (0, null);   // a drafted source that has since gone
        rows.Add(new ChoiceRow("Source", [.. choices.Select(choice => choice.Label)], at,
            index => Draft(choices[index].Name, port)));
        rows.Add(new NumberRow("Port", port, 1, 64, 1, value => $"{value:0}", value => Draft(chosen, (int)value))
        { Enabled = chosen is not null, Integer = true, FineStep = 1, DisabledText = "automatic takes no port" });
        bool changed = chosen != bound?.Source || chosen is not null && port != bound!.Channel + 1;
        rows.Add(new ActionRow("Apply", _applying is null ? "apply" : "sending", () => Apply(app, chosen, port, choices[at].Label))
        { Note = changed ? "not applied" : null });
    }

    private void Draft(string? source, int port)
    {
        _drafted = true;
        _draftSource = source;
        _draftPort = Math.Clamp(port, 1, 64);
    }

    private void Apply(App app, string? source, int port, string label)
    {
        if (_applying is not null) return;
        _applyingLabel = source is null ? "XLR 1 follows the Elgato interface" : $"XLR 1 is {label}, port {port}";
        _applying = app.Link.Request("setPrimaryInput", body =>
        {
            body["source"] = source;
            if (source is not null) body["captureMonoChannel"] = port - 1;
        });
    }

    /// <summary>One skin in the picker.</summary>
    private sealed class SkinRow(SkinEntry skin, App app) : Row(skin.Name)
    {
        public override void DrawValue(Screen screen, int x, int y, int width, Theme theme, Rgb back, bool focused)
        {
            bool chosen = app.Theme.Id == skin.Id;
            Widgets.Lamp(screen, x, y, chosen, theme, back);
            string note = skin.Description is { Length: > 0 } text ? text : skin.Id;
            screen.Text(x + 2, y, note, chosen ? theme.TextDetail : theme.TextMuted, back, bold: focused,
                maxWidth: Math.Max(4, width - 4));
        }

        public override bool Handle(KeyPress key)
        {
            if (key.Key != Key.Enter && !(key.Key == Key.Char && key.Char == ' ')) return false;
            Theme theme = skin.Id == "default"
                ? Theme.Material
                : skin.Read() is { } json ? Theme.FromJson(json, skin.Id, skin.Name) : Theme.Material;
            app.UseTheme(theme);
            app.Say(UiSettingsFile.WriteSkin(skin.Id)
                ? $"{skin.Name} is the appearance here and in the window"
                : $"{skin.Name} is on for this run, the saved choice could not be written");
            return true;
        }
    }
}
