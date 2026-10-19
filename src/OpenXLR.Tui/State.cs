using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenXLR.Tui;

/// <summary>
/// A channel's or mix's icon, colour and hidden flag from the layout editor.
/// Presentation only; the strip's sends and routing do not depend on it.
/// </summary>
internal sealed record AppearanceEntry
{
    public string Icon { get; init; } = string.Empty;
    public string? Colour { get; init; }
    public bool Hidden { get; init; }

    /// <summary>The built-in icon the daemon chose for the item's kind.</summary>
    public string DefaultIcon { get; init; } = string.Empty;

    /// <summary>The colour to draw the name in, or null for the theme's.</summary>
    public Rgb? Accent => Rgb.TryParse(Colour);

    /// <summary>
    /// The name with the icon in front of it, when there is one. A terminal
    /// draws text, so a named icon is drawn as its text character; a skin's
    /// own icon name falls back to the built-in icon of that name or the default.
    /// </summary>
    public string Label(string name) => LayoutIcons.TextFor(Icon, DefaultIcon) is { Length: > 0 } icon ? $"{icon} {name}" : name;
}

/// <summary>One mix master: a monitor mix, a virtual microphone or the Aux port.</summary>
internal sealed record MixEntry
{
    public AppearanceEntry Appearance { get; init; } = new();
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public double Volume { get; init; } = 1;
    public bool Muted { get; init; }
    public string Kind { get; init; } = "virtualMic";
    public bool? Editable { get; init; }
    public bool IsEditable => Editable ?? Kind == "virtualMic";

    /// <summary>A monitor mix goes to 150%, everything else to 100%.</summary>
    public double Ceiling => Kind == "monitor" ? 1.5 : 1.0;
}

/// <summary>One channel strip and its send into every mix.</summary>
internal sealed record ChannelEntry
{
    public AppearanceEntry Appearance { get; init; } = new();
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public Dictionary<string, double> Levels { get; init; } = [];
    public List<string> MutedIn { get; init; } = [];

    /// <summary>
    /// The channel's own volume, which scales every send it makes; null for a
    /// hardware input, and for every channel of a daemon that has none.
    /// </summary>
    public double? Volume { get; init; }
    public bool Hardware { get; init; }
    public string? CaptureSource { get; init; }
    public int CapturePair { get; init; }
    public bool CaptureConnected { get; init; }

    /// <summary>
    /// False when the active device has no jack behind this channel, which is
    /// XLR 2 and Aux In on everything but the Wave XLR Pro. The daemon keeps
    /// sending the strip so its levels survive a device change.
    /// </summary>
    public bool Present { get; init; } = true;

    /// <summary>
    /// True for the XLR microphone inputs, which are mono. The daemon still
    /// sends a pair for every channel and repeats the one reading in both
    /// sides of it, so a stereo meter here would draw the same bar twice.
    /// </summary>
    public bool Mono => Id is "xlr1" or "xlr2";

    public double Level(string mix) => Levels.TryGetValue(mix, out double value) ? value : 0;

    public bool IsMuted(string mix) => MutedIn.Contains(mix);

    /// <summary>
    /// Step the channel volume by <paramref name="by"/>, within 0 to 1. Only an
    /// application or capture channel has one; any other selection says so.
    /// </summary>
    public static void StepVolume(App app, ChannelEntry? channel, double by)
    {
        if (channel?.Volume is not double volume)
        {
            app.Say("Only an application or capture channel has a channel volume");
            return;
        }
        app.Link.Send("setChannelVolume", body =>
        {
            body["channel"] = channel.Id;
            body["value"] = Math.Round(Math.Clamp(volume + by, 0, 1), 2);
        });
    }
}

/// <summary>A live playback stream the daemon has placed on a channel.</summary>
internal sealed record StreamEntry
{
    public long Id { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Identity { get; init; } = string.Empty;
    public string? ChannelId { get; init; }
    public bool Active { get; init; }
    public bool Running { get; init; }
}

/// <summary>One plugin in a chain, with the status the daemon reports for it.</summary>
internal sealed record InsertEntry
{
    public InsertBody Insert { get; init; } = new();
    public string? Error { get; init; }
    public bool NativeHostRunning { get; init; }
    public bool NativeUiBlocked { get; init; }
    public string? NativeUiBlockReason { get; init; }
}

internal sealed record InsertBody
{
    public string Id { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Plugin { get; init; } = string.Empty;
    public string? Label { get; init; }
    public bool Bypass { get; init; }
    public bool NativeHost { get; init; }

    /// <summary>The plugin's control values, carried back untouched whenever the chain is rewritten.</summary>
    public Dictionary<string, double> Params { get; init; } = [];
}

/// <summary>A PipeWire sink or source the daemon can see.</summary>
internal sealed record DeviceEntry
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Kind { get; init; }
    public bool IsOwn { get; init; }
    public bool IsPhysical { get; init; }
    public double? Volume { get; init; }
    public bool? Muted { get; init; }

    /// <summary>Kind 0 is a sink in the daemon's own listing.</summary>
    public bool IsSink => Kind == 0;
}

/// <summary>
/// The capture source and its zero-based mono port feeding XLR 1 in generic
/// microphone mode. The daemon leaves the field out in automatic mode.
/// </summary>
internal sealed record PrimaryInputEntry
{
    public string Source { get; init; } = string.Empty;
    public int Channel { get; init; }
}

/// <summary>An interface attached to the machine, whether or not it is the active one.</summary>
internal sealed record DetectedDevice
{
    public string UsbId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Active { get; init; }
}

internal sealed record DeviceDescriptor
{
    public string Vendor { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string UsbId { get; init; } = string.Empty;
    public string? Note { get; init; }
}

internal sealed record MixerSnapshot
{
    public List<MixEntry> Mixes { get; init; } = [];
    public List<ChannelEntry> Channels { get; init; } = [];

    private List<ChannelEntry>? _shown;

    /// <summary>
    /// The channels worth drawing: every one the active device can feed. A
    /// snapshot is replaced rather than edited, so this is worked out once.
    /// </summary>
    [JsonIgnore]
    public List<ChannelEntry> Shown => _shown ??= [.. Channels.Where(channel => channel.Present)];

    private List<ChannelEntry>? _strips;

    /// <summary>
    /// The channels the mixer tab draws a strip for: the shown ones less
    /// those hidden in the layout editor. The matrix still lists a hidden
    /// channel, so its sends stay within reach.
    /// </summary>
    [JsonIgnore]
    public List<ChannelEntry> Strips => _strips ??= [.. Shown.Where(channel => !channel.Appearance.Hidden)];

    private List<MixEntry>? _masters;

    /// <summary>
    /// The mixes the mixer tab draws a master and sends for: every mix less
    /// those hidden in the layout editor. The matrix still lists a hidden
    /// mix, so its master and the sends into it stay within reach.
    /// </summary>
    [JsonIgnore]
    public List<MixEntry> Masters => _masters ??= [.. Mixes.Where(mix => !mix.Appearance.Hidden)];
    public List<string> MonitorOutputs { get; init; } = [];
    public Dictionary<string, string> MonitorFeeds { get; init; } = [];
    public double? OutputVolume { get; init; }
    public bool AuxPortEnabled { get; init; }
    public int LowCutHz { get; init; }
    public bool SoftClipGuard { get; init; }
    public bool SoftClipGuardAvailable { get; init; }
    public string? EnforcedDefaultSink { get; init; }
    public string? EnforcedDefaultSource { get; init; }
    public List<StreamEntry> Streams { get; init; } = [];
    public Dictionary<string, List<InsertEntry>> Inserts { get; init; } = [];
    public string? LayoutWarning { get; init; }

    /// <summary>The generic microphone binding, or null while XLR 1 follows the Elgato interface.</summary>
    public PrimaryInputEntry? PrimaryInput { get; init; }

    /// <summary>True when the generic microphone's route into XLR 1 exists.</summary>
    public bool PrimaryInputConnected { get; init; }
}

/// <summary>
/// A state message, kept as one immutable snapshot the drawing code reads
/// without a lock. The hardware controls and the capabilities stay as raw JSON
/// values: they are a long list of flags that grows with every device, and the
/// terminal only ever asks for one by name.
/// </summary>
internal sealed record Snapshot
{
    public string DaemonVersion { get; init; } = string.Empty;
    public bool Connected { get; init; }
    public string? Warning { get; init; }
    public DeviceDescriptor? Device { get; init; }
    public Dictionary<string, JsonElement> Capabilities { get; init; } = [];
    public Dictionary<string, JsonElement> State { get; init; } = [];
    public MixerSnapshot Mixer { get; init; } = new();
    public List<DeviceEntry> Devices { get; init; } = [];
    public List<string> Profiles { get; init; } = [];
    public string? ActiveProfile { get; init; }
    public string? RecallOnConnect { get; init; }
    public List<DetectedDevice> Detected { get; init; } = [];

    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static Snapshot? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<Snapshot>(json, Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// True in generic microphone mode: XLR 1 is fed by a PipeWire source the
    /// user picked and the daemon drives no Elgato unit, so <see cref="Connected"/>
    /// is false and there are no capabilities, while the mixer runs as usual.
    /// </summary>
    [JsonIgnore]
    public bool Generic => Mixer.PrimaryInput is { Source.Length: > 0 };

    /// <summary>The generic microphone as a person reads it: its description and its port, numbered from 1.</summary>
    [JsonIgnore]
    public string? GenericLabel => Mixer.PrimaryInput is { Source.Length: > 0 } input
        ? $"{SourceName(input.Source)}, port {input.Channel + 1}"
        : null;

    /// <summary>A source's description from the device list, or its node name when it is not listed.</summary>
    public string SourceName(string source) =>
        Devices.FirstOrDefault(device => !device.IsSink && device.Name == source) is { Description.Length: > 0 } listed
            ? listed.Description
            : source;

    /// <summary>
    /// The capture sources XLR 1 can take, as the window lists them: every
    /// source but OpenXLR's own nodes, and none the daemon would refuse
    /// (an OpenXLR name, a monitor tap, a control character, over 256 characters).
    /// </summary>
    [JsonIgnore]
    public List<DeviceEntry> PrimaryInputChoices =>
        [.. Devices.Where(device => !device.IsSink && !device.IsOwn && Selectable(device.Name))];

    private static bool Selectable(string name) =>
        name.Length is > 0 and <= 256 && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl)
        && !name.StartsWith("OpenXLR", StringComparison.Ordinal) && !name.EndsWith(".monitor", StringComparison.Ordinal);

    public bool Can(string capability) =>
        Capabilities.TryGetValue(capability, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    public int Count(string capability) =>
        Capabilities.TryGetValue(capability, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    public bool Flag(string control) =>
        State.TryGetValue(control, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    public double Number(string control) =>
        State.TryGetValue(control, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;
}
