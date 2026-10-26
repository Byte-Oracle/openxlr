using System;
using System.Collections.Generic;
using System.Linq;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>
/// A starting point for a speaking voice in Mic check's Shape step: the
/// microphone EQ's four bands, with the EQ switched on. Applying one is a
/// single <c>setEq</c>, so it is heard at once and undone in one step.
/// </summary>
public sealed record MicCheckPreset(string Id, MicEq Eq)
{
    /// <summary>The preset's name in the UI's language.</summary>
    public string Name => Id switch
    {
        "warm" => Localizer.Text("MicCheckPresetWarm"),
        "broadcast" => Localizer.Text("MicCheckPresetBroadcast"),
        _ => Localizer.Text("MicCheckPresetClear"),
    };

    /// <summary>What the preset does to a voice, for its tooltip.</summary>
    public string Hint => Id switch
    {
        "warm" => Localizer.Text("MicCheckPresetWarmTip"),
        "broadcast" => Localizer.Text("MicCheckPresetBroadcastTip"),
        _ => Localizer.Text("MicCheckPresetClearTip"),
    };

    /// <summary>The preset's EQ held to the ranges the daemon reports, which the shipped values are inside.</summary>
    public MicEq For(EqLimits limits) => MicEq.Snap(Eq with
    {
        Bands = [.. Eq.Bands.Select(b => b with
        {
            Frequency = Math.Clamp(b.Frequency, limits.Frequency.Min, limits.Frequency.Max),
            Gain = Math.Clamp(b.Gain, limits.Gain.Min, limits.Gain.Max),
            Q = Math.Clamp(b.Q, limits.Q.Min, limits.Q.Max),
        })],
    });
}

/// <summary>
/// The presets Mic check offers. These are OpenXLR's own values for speech,
/// written down here and nowhere else in code; the manual's table is checked
/// against them. Each one keeps the low end out with the first band and
/// shapes the voice with the other three, gently enough to be a starting
/// point rather than a sound.
/// </summary>
public static class MicCheckPresets
{
    private static EqBand Band(string type, double hz, double db = 0, double? q = null)
        => new() { Enabled = true, Type = type, Frequency = hz, Gain = db, Q = q ?? MicEq.DefaultQ };

    private static MicEq Eq(params EqBand[] bands) => MicEq.Snap(new MicEq { Enabled = true, Bands = bands });

    /// <summary>Rumble out below 80 Hz, a little less boxiness, a small lift for presence and air.</summary>
    public static MicCheckPreset Clear { get; } = new("clear", Eq(
        Band(MicEq.LowCut, 80),
        Band(MicEq.Peak, 300, -2, 1.0),
        Band(MicEq.Peak, 3500, 2, 1.0),
        Band(MicEq.HighShelf, 10000, 1)));

    /// <summary>A lower low cut, more body under 200 Hz and a softer top.</summary>
    public static MicCheckPreset Warm { get; } = new("warm", Eq(
        Band(MicEq.LowCut, 60),
        Band(MicEq.LowShelf, 200, 2),
        Band(MicEq.Peak, 3000, 1, 1.0),
        Band(MicEq.HighShelf, 8000, -2)));

    /// <summary>A tight low end, less mud around 400 Hz and a forward presence lift.</summary>
    public static MicCheckPreset Broadcast { get; } = new("broadcast", Eq(
        Band(MicEq.LowCut, 100),
        Band(MicEq.Peak, 400, -3, 1.4),
        Band(MicEq.Peak, 4000, 3, 0.8),
        Band(MicEq.HighShelf, 12000, 2)));

    /// <summary>The presets in the order the Shape step shows them.</summary>
    public static IReadOnlyList<MicCheckPreset> All { get; } = [Clear, Warm, Broadcast];
}
