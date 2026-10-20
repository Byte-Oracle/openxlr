namespace OpenXLR.Core.Mixing;

/// <summary>
/// How a channel or a mix is drawn: an icon (see <see cref="LayoutIcons"/>),
/// an optional colour, and whether its strip or column is left out of the
/// full mixer. Presentation only: stable ids, routing order and every audio
/// connection are the same whatever these hold.
/// </summary>
public sealed record LayoutAppearance(string Icon = "", string? Colour = null, bool Hidden = false)
{
    public static LayoutAppearance Default { get; } = new();

    /// <summary>The legacy text glyphs a layout may still store; see <see cref="LayoutIcons"/> for the rest.</summary>
    public static IReadOnlyList<string> Icons => LayoutIcons.Glyphs;

    /// <summary>Every channel and mix the layout can hold, the editable ones and the six structural ones, and the two headphone outputs.</summary>
    public const int MaxEntries = MixerConfig.MaxApplicationChannels + MixerConfig.MaxUserMixes + 6 + 2;

    /// <summary>The hardware outputs that take an icon and a colour: the two headphone jacks.</summary>
    public static IReadOnlyList<string> Outputs { get; } = ["phones1", "phones2"];

    /// <summary>The icon a headphone output wears before the user picks one.</summary>
    public const string OutputDefaultIcon = "headset";

    /// <summary>An icon of a valid shape (<see cref="LayoutIcons.IsValidChoice"/>) and a null or #RRGGBB colour.</summary>
    public static bool IsValid(LayoutAppearance? value) => value is not null &&
        LayoutIcons.IsValidChoice(value.Icon) &&
        (value.Colour is null || value.Colour.Length == 7 && value.Colour[0] == '#' &&
            value.Colour.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0);

    /// <summary>
    /// A saved entry: a <c>channel:</c> or <c>mix:</c> key of bounded length,
    /// or <c>output:phones1</c> or <c>output:phones2</c>, and a valid value.
    /// A channel or a mix can be hidden, which only leaves it out of the full
    /// mixer: its audio, outputs and sends are the same. A headphone output is
    /// hardware and never hidden.
    /// </summary>
    internal static bool IsValidEntry(string? key, LayoutAppearance? value) =>
        key is { Length: <= 44 } && IsValid(value) &&
        (key.StartsWith("channel:", StringComparison.Ordinal) && key.Length > 8 ||
         key.StartsWith("mix:", StringComparison.Ordinal) && key.Length > 4 ||
         key.StartsWith("output:", StringComparison.Ordinal) && Outputs.Contains(key[7..]) && !value!.Hidden);
}

/// <summary>
/// An appearance as the state publishes it: the stored values and the icon
/// the item wears when the stored one is empty or unknown to a client.
/// </summary>
/// <param name="DefaultIcon">A built-in icon name chosen by the item's kind; never empty.</param>
public sealed record LayoutAppearanceState(string Icon, string? Colour, bool Hidden, string DefaultIcon)
{
    public static LayoutAppearanceState From(LayoutAppearance value, string defaultIcon) =>
        new(value.Icon, value.Colour, value.Hidden, defaultIcon);
}

public sealed partial record ChannelStatus
{
    /// <summary>The channel's icon, colour and hidden flag, with its default icon.</summary>
    public LayoutAppearanceState Appearance { get; init; } = LayoutAppearanceState.From(LayoutAppearance.Default, "apps");
}

public sealed partial record MixStatus
{
    /// <summary>The mix's icon, colour and hidden flag, with its default icon.</summary>
    public LayoutAppearanceState Appearance { get; init; } = LayoutAppearanceState.From(LayoutAppearance.Default, "podcast");
}

public sealed partial class Mixer
{
    private Dictionary<string, LayoutAppearance> _appearance = [];

    private Dictionary<string, LayoutAppearance> ExportAppearanceLocked() => _appearance
        .Where(p => AppearanceTargetExists(p.Key) && p.Value != LayoutAppearance.Default).ToDictionary();

    private void RestoreAppearanceLocked(Dictionary<string, LayoutAppearance>? saved) =>
        _appearance = (saved ?? []).Where(p => AppearanceTargetExists(p.Key) && LayoutAppearance.IsValidEntry(p.Key, p.Value))
            .Take(LayoutAppearance.MaxEntries).ToDictionary();

    private bool AppearanceTargetExists(string key) =>
        key.StartsWith("output:", StringComparison.Ordinal) && LayoutAppearance.Outputs.Contains(key[7..]) ||
        key.StartsWith("channel:", StringComparison.Ordinal) && _config.Channels.Any(c => c.Id == key[8..]) ||
        key.StartsWith("mix:", StringComparison.Ordinal) && _config.Mixes.Any(m => m.Id == key[4..]);

    private LayoutAppearance AppearanceLocked(string key) => _appearance.GetValueOrDefault(key) ?? LayoutAppearance.Default;

    /// <summary>The state with each channel and mix carrying its appearance.</summary>
    private MixerState WithAppearanceLocked(MixerState state) => state with
    {
        Channels = [.. state.Channels.Select(c => c with { Appearance = LayoutAppearanceState.From(
            AppearanceLocked("channel:" + c.Id), LayoutDefaultIcons.ForChannel(c.Id, c.Hardware, c.CaptureSource,
                // A strip bound to another unit's XLR jack wears the jack's icon.
                c.Hardware && c.Binding.Kind == "source" && !OnXlrJackLocked(c.Id) ? c.Binding.Source : null,
                c.Hardware && c.Binding.Kind == "source" || _unitXlrJacks, c.CaptureMonoChannel)) })],
        Mixes = [.. state.Mixes.Select(m => m with { Appearance = LayoutAppearanceState.From(
            AppearanceLocked("mix:" + m.Id), LayoutDefaultIcons.ForMix(m.Id, m.Kind)) })],
        OutputAppearance = LayoutAppearance.Outputs.ToDictionary(id => id,
            id => LayoutAppearanceState.From(AppearanceLocked("output:" + id), LayoutAppearance.OutputDefaultIcon)),
    };

    /// <summary>
    /// Replace one channel's or mix's appearance and save it. No PipeWire
    /// node or link changes; a failed save restores the previous appearance.
    /// </summary>
    /// <param name="key"><c>channel:&lt;id&gt;</c>, <c>mix:&lt;id&gt;</c>, <c>output:phones1</c> or <c>output:phones2</c>.</param>
    public void SetLayoutAppearance(string key, LayoutAppearance value, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            if (!AppearanceTargetExists(key)) throw new InvalidOperationException("unknown channel, mix or output");
            if (!LayoutAppearance.IsValidEntry(key, value)) throw new InvalidOperationException("invalid icon, colour or hidden flag");
            var previous = _appearance;
            _appearance = new(previous) { [key] = value };
            try { PersistLocked(persist); }
            catch { _appearance = previous; throw; }
        }
    }

    /// <summary>
    /// Give an item that is being created its icon, ahead of the save that
    /// creates it; the caller restores the previous table when that save fails.
    /// </summary>
    private void SetCreatedIconLocked(string key, string? icon)
    {
        if (!string.IsNullOrEmpty(icon)) _appearance = new(_appearance) { [key] = new LayoutAppearance(icon) };
    }
}
