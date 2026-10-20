using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    /// <summary>
    /// The session manager marks a stream that was linked to the default
    /// output as following the default (target.node -1 in the default
    /// metadata) and moves it when the default changes. A meter on the
    /// channel or mix that is the default output has to stay on it.
    /// </summary>
    [MonitorPipeWireFact]
    public void AMeterStaysOnItsSinkWhenTheDefaultOutputChanges()
    {
        var pw = new PipeWireAdapter();
        using var meters = new MeterReader();
        try
        {
            pw.CreateNullSink("test_meter_held", "Metered output");
            pw.CreateNullSink("test_meter_other", "Other output");
            Assert.True(SpinWait.SpinUntil(() =>
            {
                pw.SetDefaultSink("test_meter_held");
                return pw.GetDefaultSink() == "test_meter_held";
            }, TimeSpan.FromSeconds(5)), "the metered sink became the default output");

            meters.Add("held", "test_meter_held");
            Assert.True(SpinWait.SpinUntil(() => MeterFeeds("test_meter_held").SequenceEqual(["test_meter_held"]),
                TimeSpan.FromSeconds(5)), "the meter reads its sink");

            pw.SetDefaultSink("test_meter_other");
            Assert.True(SpinWait.SpinUntil(() => pw.GetDefaultSink() == "test_meter_other", TimeSpan.FromSeconds(5)));
            // The session manager moves a following stream within
            // milliseconds of the change; give it far longer than that.
            Assert.False(SpinWait.SpinUntil(() => !MeterFeeds("test_meter_held").SequenceEqual(["test_meter_held"]),
                TimeSpan.FromSeconds(2)), "the meter moved to " + string.Join(", ", MeterFeeds("test_meter_held")));
        }
        finally { pw.TearDown(); }
    }

    /// <summary>The nodes linked into the meter stream that targets <paramref name="sink"/>.</summary>
    private static string[] MeterFeeds(string sink)
    {
        ProcessResult dump = ProcessRunner.Run("pw-dump", []);
        Assert.True(dump.Ok, dump.Stderr);
        using JsonDocument graph = PipeWireSnapshot.Parse(dump.Stdout);
        var names = new Dictionary<int, string>();
        var meters = new HashSet<int>();
        foreach (JsonElement item in graph.RootElement.EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "PipeWire:Interface:Node"
                || !item.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Object
                || !info.TryGetProperty("props", out JsonElement props)) continue;
            int id = item.GetProperty("id").GetInt32();
            if (props.TryGetProperty("node.name", out JsonElement name)) names[id] = name.GetString() ?? "";
            if (props.TryGetProperty("application.name", out JsonElement app) && app.GetString() == "parec"
                && props.TryGetProperty("target.object", out JsonElement target) && target.GetString() == sink)
                meters.Add(id);
        }
        var feeds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in graph.RootElement.EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "PipeWire:Interface:Link"
                || !item.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Object) continue;
            if (meters.Contains(info.GetProperty("input-node-id").GetInt32()))
                feeds.Add(names.GetValueOrDefault(info.GetProperty("output-node-id").GetInt32(), "?"));
        }
        return [.. feeds];
    }
}
