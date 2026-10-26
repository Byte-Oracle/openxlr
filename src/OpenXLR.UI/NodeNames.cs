using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace OpenXLR.UI;

/// <summary>
/// A readable name for an audio device the UI has no description for, read
/// out of its PipeWire node name. Used only for a selected output or a bound
/// source that is away and was never seen with its description.
/// </summary>
internal static partial class NodeNames
{
    /// <summary>
    /// The product an ALSA node names: <c>alsa_output.usb-SteelSeries_Arctis_Nova_Elite-00.analog-stereo</c>
    /// reads "SteelSeries Arctis Nova Elite". A USB node loses its bus, its
    /// interface index, its card profile and a trailing serial number, and a
    /// vendor written twice is written once. A PCI node names only a slot,
    /// so an HDMI output reads "HDMI" with PipeWire's port number. Any other
    /// name, a Bluetooth node included, comes back as it is.
    /// </summary>
    public static string Readable(string name)
    {
        string rest = name.StartsWith("alsa_output.", StringComparison.Ordinal) ? name["alsa_output.".Length..]
            : name.StartsWith("alsa_input.", StringComparison.Ordinal) ? name["alsa_input.".Length..]
            : "";
        // A Wave XLR Pro jack (#hp1) is labelled by the daemon, never guessed.
        if (rest.Length == 0 || rest.Contains('#')) return name;
        if (rest.StartsWith("pci-", StringComparison.Ordinal))
            return Hdmi().Match(rest) is { Success: true } hdmi
                ? hdmi.Groups["extra"].Success ? $"HDMI {int.Parse(hdmi.Groups["extra"].Value) + 1}" : "HDMI"
                : name;
        if (Usb().Match(rest) is not { Success: true } usb) return name;
        List<string> words = [.. usb.Groups["device"].Value.Split('_', StringSplitOptions.RemoveEmptyEntries)];
        if (words.Count > 1 && Serial(words[^1])) words.RemoveAt(words.Count - 1);
        // The vendor written again after a word or two (Elgato_Elgato, Elgato_Systems_Elgato) is written once.
        int again = words.FindIndex(1, Math.Min(2, words.Count - 1), w => string.Equals(w, words[0], StringComparison.OrdinalIgnoreCase));
        if (again > 0 && again < words.Count - 1) words.RemoveRange(0, again);
        return words.Count > 0 ? string.Join(' ', words) : name;
    }

    // A serial number is one long run of letters and digits, four of them
    // digits at least; a model name such as MK.2 or AT2020USB is shorter or
    // carries punctuation.
    private static bool Serial(string word)
        => word.Length >= 10 && word.All(char.IsAsciiLetterOrDigit) && word.Count(char.IsAsciiDigit) >= 4;

    // usb-<device>-<interface index>.<profile>; the device may contain dots
    // (MK.2), the profile never does (analog-stereo, pro-output-0, HiFi__Speaker__sink).
    [GeneratedRegex(@"^usb-(?<device>.+)-\d{2}\.[A-Za-z0-9_-]+$")]
    private static partial Regex Usb();

    // pci-<slot>.<function>.hdmi-stereo or hdmi-surround, with -extraN on the later ports.
    [GeneratedRegex(@"^pci-[0-9a-f_]+\.\d+\.hdmi-[a-z0-9]+(-extra(?<extra>\d+))?$")]
    private static partial Regex Hdmi();
}
