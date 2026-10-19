using OpenXLR.Core.Devices;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// Finding one interface's PipeWire nodes by a name fragment. A card can
/// publish several nodes (the Wave XLR Pro's UCM split has one per jack), so
/// matches are grouped by card: nodes of one card are one interface, nodes of
/// two cards are two units that the fragment cannot tell apart.
/// </summary>
public static class InterfaceNodes
{
    /// <summary>
    /// The card part of a node name: for an ALSA node the text between the
    /// "alsa_input." or "alsa_output." prefix and the next dot
    /// ("usb-Elgato_Systems_Elgato_Wave_XLR_SERIAL-00"); any other node is
    /// its own card.
    /// </summary>
    public static string CardKey(string nodeName)
    {
        int start = 0;
        if (nodeName.StartsWith("alsa_", StringComparison.Ordinal) && nodeName.IndexOf('.') is int dot and > 0)
            start = dot + 1;
        int end = nodeName.IndexOf('.', start);
        return end < 0 ? nodeName[start..] : nodeName[start..end];
    }

    /// <summary>
    /// The attached supported unit whose card publishes an ALSA node, as its
    /// instance id and display name, or null for any other node. The card part
    /// of the name (see <see cref="CardKey"/>) is matched against each unit's
    /// fragments among the attached units, and the longest fragment wins, so a
    /// unit's serial beats another unit's model name. Two units whose best
    /// fragments are equal, the same model without serials, cannot be told
    /// apart: the node is still theirs, under the model id.
    /// </summary>
    public static (string Id, string Name)? Owner(string nodeName, IReadOnlyList<DeviceInfo> attached)
    {
        if (!nodeName.StartsWith("alsa_", StringComparison.Ordinal)) return null;
        string card = CardKey(nodeName);
        DeviceInfo? best = null;
        int bestLength = 0;
        bool tie = false;
        foreach (DeviceInfo unit in attached)
        {
            int length = unit.FragmentsAmong(attached)
                .Where(f => f.Length > 0 && card.Contains(f, StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Length).DefaultIfEmpty(0).Max();
            if (length == 0 || length < bestLength) continue;
            tie = length == bestLength && best is not null && best.InstanceId != unit.InstanceId;
            if (length > bestLength) { best = unit; bestLength = length; }
        }
        if (best is null) return null;
        return (tie ? $"{best.VendorId:x4}:{best.ProductId:x4}" : best.InstanceId, best.DisplayName);
    }

    /// <summary>
    /// The nodes, in their given order, of the one card that the first
    /// fragment naming any node names. <paramref name="matched"/> is that
    /// fragment. When it names nodes of more than one card the result is
    /// empty and <paramref name="ambiguous"/> is true; a later, broader
    /// fragment would only name more of them, so none is tried.
    /// </summary>
    public static List<AudioNode> Pick(IEnumerable<AudioNode> nodes, IReadOnlyList<string> fragments,
        out string? matched, out bool ambiguous)
    {
        matched = null;
        ambiguous = false;
        List<AudioNode> all = [.. nodes];
        foreach (string fragment in fragments)
        {
            if (fragment.Length == 0) continue;
            List<AudioNode> found = [.. all.Where(n => n.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))];
            if (found.Count == 0) continue;
            matched = fragment;
            if (found.Select(n => CardKey(n.Name)).Distinct(StringComparer.Ordinal).Skip(1).Any())
            {
                ambiguous = true;
                return [];
            }
            return found;
        }
        return [];
    }
}
