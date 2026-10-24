using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace OpenXLR.UI;

internal sealed record InspectedNode(uint Id, string? Name, string? MediaClass, string? State, string? Error,
    int IncomingLinks, int OutgoingLinks, IReadOnlyDictionary<string, string> Properties);
internal sealed record InspectedLink(uint Id, uint? OutputNode, uint? OutputPort, uint? InputNode, uint? InputPort,
    string? State, string? Error, IReadOnlyDictionary<string, string> Format);
internal sealed record GraphObservation(string Code, uint Id, string Message);
internal sealed record AudioGraphReport(bool Available, string? Note, int? ObjectCount,
    int? NodeCount, int? PortCount, int? LinkCount, IReadOnlyDictionary<string, string> DefaultClock,
    IReadOnlyDictionary<string, string> ClockSettings, IReadOnlyList<InspectedNode> Nodes,
    IReadOnlyList<InspectedLink> Links, IReadOnlyList<GraphObservation> Observations);

/// <summary>Inspect the archived, redacted snapshot without querying or changing the live graph.</summary>
internal static class AudioGraphInspection
{
    internal const int MaximumBytes = 16 * 1024 * 1024;
    internal const int MaximumObjects = 16384;
    private static readonly string[] Defaults = ["default.clock.rate", "default.clock.allowed-rates",
        "default.clock.quantum", "default.clock.min-quantum", "default.clock.max-quantum",
        "default.clock.quantum-limit", "default.clock.quantum-floor", "clock.power-of-two-quantum"];
    private static readonly string[] Settings = ["clock.rate", "clock.allowed-rates", "clock.quantum",
        "clock.min-quantum", "clock.max-quantum", "clock.force-rate", "clock.force-quantum"];
    private static readonly string[] NodeProperties = ["audio.rate", "audio.channels", "node.rate", "node.latency",
        "node.force-rate", "node.force-quantum", "node.lock-rate", "node.lock-quantum", "clock.quantum-limit",
        "node.always-process", "node.pause-on-idle"];
    private static readonly string[] FormatProperties = ["mediaType", "mediaSubtype", "format", "rate", "channels", "position"];

    internal static AudioGraphReport Read(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            return Unavailable("The graph exceeds the inspection byte budget. The original dump is kept.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Unavailable("The dump is not a PipeWire object array.");
            if (document.RootElement.GetArrayLength() > MaximumObjects)
                return Unavailable("The graph exceeds the inspection object budget. The original dump is kept.");
            var objects = new Dictionary<uint, JsonElement>();
            PipeWireSnapshot.Apply(document.RootElement, objects);
            if (objects.Values.Any(item => Get(item, "type").ValueKind != JsonValueKind.String))
                return Unavailable("A registry object has no interface type. The graph cannot be classified reliably.");
            return Inspect(objects);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return Unavailable("The graph is incomplete or malformed. Check pw-dump.json and pw-dump-errors.txt.");
        }
    }

    private static AudioGraphReport Inspect(Dictionary<uint, JsonElement> objects)
    {
        var nodes = objects.Where(p => Text(p.Value, "type") == "PipeWire:Interface:Node").ToDictionary();
        var ports = objects.Where(p => Text(p.Value, "type") == "PipeWire:Interface:Port").ToDictionary();
        var links = new List<InspectedLink>();
        var observations = new List<GraphObservation>();
        var incoming = new Dictionary<uint, int>();
        var outgoing = new Dictionary<uint, int>();
        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (id, item) in objects.OrderBy(p => p.Key))
        {
            var info = Get(item, "info");
            string? type = Text(item, "type");
            if (type == "PipeWire:Interface:Core")
                foreach (var pair in Selected(Get(info, "props"), Defaults)) defaults[pair.Key] = pair.Value;
            if (type == "PipeWire:Interface:Metadata" && Text(Get(item, "props"), "metadata.name") == "settings"
                && Get(item, "metadata") is var metadata && metadata.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in metadata.EnumerateArray())
                    if (Id(entry, "subject") == 0 && Text(entry, "key") is string key && Settings.Contains(key))
                    {
                        if (Value(Get(entry, "value")) is string value) settings[key] = value;
                        else settings.Remove(key);
                    }
            }
            if (type != "PipeWire:Interface:Link") continue;
            var props = Get(info, "props");
            uint? from = Id(info, "output-node-id") ?? Id(props, "link.output.node");
            uint? to = Id(info, "input-node-id") ?? Id(props, "link.input.node");
            uint? fromPort = Id(info, "output-port-id") ?? Id(props, "link.output.port");
            uint? toPort = Id(info, "input-port-id") ?? Id(props, "link.input.port");
            string? state = Text(info, "state"), error = Text(info, "error");
            links.Add(new(id, from, fromPort, to, toPort, state, error, Selected(Get(info, "format"), FormatProperties)));
            if (from is uint output) outgoing[output] = outgoing.GetValueOrDefault(output) + 1;
            if (to is uint input) incoming[input] = incoming.GetValueOrDefault(input) + 1;
            if (state == "error" || !string.IsNullOrWhiteSpace(error))
                observations.Add(new("link-error", id, "PipeWire reported an error for this link."));
            Endpoint(from, fromPort, "output", id);
            Endpoint(to, toPort, "input", id);
        }

        var inspected = new List<InspectedNode>();
        foreach (var (id, item) in nodes.OrderBy(p => p.Key))
        {
            var info = Get(item, "info");
            var props = Get(info, "props");
            string? name = Text(props, "node.name"), media = Text(props, "media.class");
            if (info.ValueKind != JsonValueKind.Object)
                observations.Add(new("incomplete-node", id, "The node's information is absent from this snapshot."));
            string? state = Text(info, "state"), error = Text(info, "error");
            int ins = incoming.GetValueOrDefault(id), outs = outgoing.GetValueOrDefault(id);
            inspected.Add(new(id, name, media, state, error, ins, outs, Selected(props, NodeProperties)));
            if (state == "error" || !string.IsNullOrWhiteSpace(error))
                observations.Add(new("node-error", id, "PipeWire reported an error for this node."));
            if (ins == 0 && outs == 0 && name?.StartsWith("OpenXLR_", StringComparison.Ordinal) == true)
                observations.Add(new("isolated-openxlr-node", id, "This OpenXLR-named node has no observed links. It may be unused or disconnected."));
            if (state == "running" && (media == "Stream/Input/Audio" && ins == 0 || media == "Stream/Output/Audio" && outs == 0))
                observations.Add(new("unlinked-running-stream", id, "A running stream has no link in its audio direction in this snapshot."));
        }
        return new(true, null, objects.Count, nodes.Count, ports.Count, links.Count, defaults, settings, inspected, links, observations);

        void Endpoint(uint? node, uint? port, string direction, uint link)
        {
            if (node is null || port is null)
                observations.Add(new("incomplete-endpoint", link, $"The link's {direction} endpoint is not fully described."));
            if (node is uint n && !nodes.ContainsKey(n))
                observations.Add(new("missing-node", link, $"The {direction} node is absent from this snapshot. The graph may have changed during collection."));
            if (port is not uint p) return;
            if (!ports.TryGetValue(p, out var endpoint))
            {
                observations.Add(new("missing-port", link, $"The {direction} port is absent from this snapshot. The graph may have changed during collection."));
                return;
            }
            var portInfo = Get(endpoint, "info");
            var portProps = Get(portInfo, "props");
            string? actual = Text(portInfo, "direction") ?? Text(portProps, "port.direction");
            actual = actual switch { "in" => "input", "out" => "output", _ => actual };
            uint? owner = Id(portProps, "node.id");
            if (owner is null || actual is not ("input" or "output"))
                observations.Add(new("incomplete-port", link, $"The {direction} port's owner or direction is unavailable."));
            if ((owner is uint ownerId && node is uint expected && ownerId != expected)
                || ((actual is "input" or "output") && actual != direction))
                observations.Add(new("endpoint-mismatch", link, $"The {direction} port's owner or direction disagrees with the link."));
        }
    }

    internal static string ToText(AudioGraphReport report)
    {
        var text = new StringBuilder("OpenXLR audio graph inspection\nSource: pw-dump.json\n");
        text.AppendLine("Observed links do not prove clean audio. Idle and suspended nodes can be normal.");
        text.AppendLine("The graph can change during collection. This report repairs nothing.");
        if (!report.Available) return text.AppendLine(report.Note).ToString();
        text.AppendLine($"Objects: {report.ObjectCount}; nodes: {report.NodeCount}; ports: {report.PortCount}; links: {report.LinkCount}");
        foreach (var pair in report.DefaultClock) text.AppendLine($"Default {pair.Key}: {Quote(pair.Value)}");
        foreach (var pair in report.ClockSettings) text.AppendLine($"Setting {pair.Key}: {Quote(pair.Value)}");
        text.AppendLine("Observations:");
        if (report.Observations.Count == 0) text.AppendLine("No listed anomaly was observed; this is not a signal-quality test.");
        foreach (var finding in report.Observations) text.AppendLine($"{finding.Code} [{finding.Id}]: {finding.Message}");
        text.AppendLine("Nodes:");
        foreach (var node in report.Nodes)
        {
            text.AppendLine($"{node.Id}: {Quote(node.Name)}; class={Quote(node.MediaClass)}; state={Quote(node.State)}; incoming={node.IncomingLinks}; outgoing={node.OutgoingLinks}; error={Quote(node.Error)}");
            foreach (var pair in node.Properties) text.AppendLine($"  {pair.Key}: {Quote(pair.Value)}");
        }
        text.AppendLine("Links:");
        foreach (var link in report.Links)
        {
            text.AppendLine($"{link.Id}: {Number(link.OutputNode)}:{Number(link.OutputPort)} -> {Number(link.InputNode)}:{Number(link.InputPort)}; state={Quote(link.State)}; error={Quote(link.Error)}");
            foreach (var pair in link.Format) text.AppendLine($"  {pair.Key}: {Quote(pair.Value)}");
        }
        return text.ToString();
    }

    private static string Quote(string? value) => value is null ? "unknown" : JsonSerializer.Serialize(value);
    private static string Number(uint? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
    private static AudioGraphReport Unavailable(string note) => new(false, note, null, null, null, null,
        new Dictionary<string, string>(), new Dictionary<string, string>(), [], [], []);
    private static JsonElement Get(JsonElement value, string key)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var found) ? found : default;
    private static string? Text(JsonElement value, string key)
    {
        var found = Get(value, key);
        return found.ValueKind == JsonValueKind.String ? Value(found) : null;
    }
    private static string? Value(JsonElement value)
    {
        string? text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            JsonValueKind.Array when value.GetArrayLength() <= 32 && value.EnumerateArray().All(v =>
                v.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null) => value.GetRawText(),
            _ => null,
        };
        return text?.Length > 512 ? text[..512] + "..." : text;
    }
    private static uint? Id(JsonElement value, string key)
    {
        var found = Get(value, key);
        if (found.ValueKind == JsonValueKind.Number && found.TryGetUInt32(out uint id)) return id;
        return found.ValueKind == JsonValueKind.String
            && uint.TryParse(found.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out id) ? id : null;
    }
    private static Dictionary<string, string> Selected(JsonElement properties, string[] keys)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in keys)
            if (Value(Get(properties, key)) is string text) result[key] = text;
        return result;
    }
}
