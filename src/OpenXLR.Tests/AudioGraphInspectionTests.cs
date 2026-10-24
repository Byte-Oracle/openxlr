using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class AudioGraphInspectionTests
{
    private const string Fixture = """
        [
          {"id":0,"type":"PipeWire:Interface:Core","info":{"props":{"default.clock.rate":48000,"default.clock.quantum":1024}}},
          {"id":1,"type":"PipeWire:Interface:Node","info":{"state":"running","props":{"node.name":"OpenXLR_xlr1","media.class":"Audio/Source","node.rate":"1/48000","node.latency":"256/48000"}}},
          {"id":2,"type":"PipeWire:Interface:Node","info":{"state":"running","props":{"node.name":"alsa_output.fixture","media.class":"Audio/Sink","audio.rate":16000}}},
          {"id":11,"type":"PipeWire:Interface:Port","info":{"direction":"output","props":{"node.id":"1"}}},
          {"id":12,"type":"PipeWire:Interface:Port","info":{"direction":"input","props":{"node.id":2}}},
          {"id":30,"type":"PipeWire:Interface:Link","info":{"output-node-id":1,"output-port-id":11,"input-node-id":2,"input-port-id":12,"state":"active","format":{"mediaType":"audio","mediaSubtype":"raw","format":"F32LE","rate":48000,"channels":1}}},
          {"id":4,"type":"PipeWire:Interface:Metadata","props":{"metadata.name":"settings"},"metadata":[{"subject":0,"key":"clock.rate","value":48000},{"subject":0,"key":"clock.force-quantum","value":0},{"subject":1,"key":"clock.quantum","value":64},{"subject":0,"key":"private-key","value":"PRIVATE-MARKER"}]}
        ]
        """;

    private static JsonArray Graph() => JsonNode.Parse(Fixture)!.AsArray();
    private static JsonNode Object(JsonArray graph, int id) => graph.Single(o => o!["id"]!.GetValue<int>() == id)!;

    [Fact]
    public void InventoryKeepsDefaultsSettingsFormatsAndDirectionalLinkCountsSeparate()
    {
        var report = AudioGraphInspection.Read(Fixture);
        Assert.True(report.Available);
        Assert.Equal(7, report.ObjectCount);
        Assert.Equal(2, report.NodeCount);
        Assert.Equal(2, report.PortCount);
        Assert.Equal(1, report.LinkCount);
        Assert.Empty(report.Observations);
        Assert.Equal("48000", report.DefaultClock["default.clock.rate"]);
        Assert.Equal("0", report.ClockSettings["clock.force-quantum"]);
        Assert.DoesNotContain("clock.quantum", report.ClockSettings.Keys);
        Assert.DoesNotContain("PRIVATE-MARKER", JsonSerializer.Serialize(report));
        var source = Assert.Single(report.Nodes, n => n.Id == 1);
        Assert.Equal(0, source.IncomingLinks);
        Assert.Equal(1, source.OutgoingLinks);
        Assert.Equal("256/48000", source.Properties["node.latency"]);
        Assert.Equal("16000", Assert.Single(report.Nodes, n => n.Id == 2).Properties["audio.rate"]);
        Assert.Equal("48000", Assert.Single(report.Links).Format["rate"]);
        Assert.Contains("not a signal-quality test", AudioGraphInspection.ToText(report));
    }

    [Fact]
    public void ReportedNodeAndLinkErrorsArePreservedWithoutDeclaringACause()
    {
        var graph = Graph();
        Object(graph, 1)["info"]!["state"] = "error";
        Object(graph, 1)["info"]!["error"] = "device unavailable";
        Object(graph, 30)["info"]!["state"] = "error";
        Object(graph, 30)["info"]!["error"] = "format negotiation failed";
        var report = AudioGraphInspection.Read(graph.ToJsonString());
        Assert.Equal(["link-error", "node-error"], report.Observations.Select(o => o.Code));
        Assert.Equal("device unavailable", Assert.Single(report.Nodes, n => n.Id == 1).Error);
        Assert.Equal("format negotiation failed", Assert.Single(report.Links).Error);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("42")]
    [InlineData("[\"error\"]")]
    [InlineData("{}")]
    public void UnexpectedTextFieldTypesStayUnknownRatherThanBecomingErrors(string value)
    {
        var graph = Graph();
        Object(graph, 1)["info"]!["error"] = JsonNode.Parse(value);
        Object(graph, 30)["info"]!["error"] = JsonNode.Parse(value);
        var report = AudioGraphInspection.Read(graph.ToJsonString());
        Assert.Null(Assert.Single(report.Nodes, n => n.Id == 1).Error);
        Assert.Null(Assert.Single(report.Links).Error);
        Assert.Empty(report.Observations);
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("suspended")]
    public void RestingNodesAndDifferentRatePropertiesAreNotDiagnosedAsBroken(string state)
    {
        var graph = Graph();
        Object(graph, 1)["info"]!["state"] = state;
        Object(graph, 30)["info"]!["state"] = "paused";
        Assert.Empty(AudioGraphInspection.Read(graph.ToJsonString()).Observations);
    }

    [Fact]
    public void MissingEndpointsRemainSnapshotObservationsRatherThanAutomaticRepairs()
    {
        var graph = Graph();
        graph.Remove(Object(graph, 2));
        graph.Remove(Object(graph, 12));
        var report = AudioGraphInspection.Read(graph.ToJsonString());
        Assert.Contains(report.Observations, o => o.Code == "missing-node" && o.Id == 30);
        Assert.Contains(report.Observations, o => o.Code == "missing-port" && o.Id == 30);
        Assert.All(report.Observations, o => Assert.Contains("snapshot", o.Message));
    }

    [Fact]
    public void WrongPortOwnersAndDirectionsAreIdentified()
    {
        var graph = Graph();
        Object(graph, 12)["info"]!["direction"] = "output";
        Object(graph, 11)["info"]!["props"]!["node.id"] = 2;
        var report = AudioGraphInspection.Read(graph.ToJsonString());
        Assert.Equal(2, report.Observations.Count(o => o.Code == "endpoint-mismatch"));
    }

    [Fact]
    public void LegacyPropertyEndpointsAndDirectionsAreUnderstood()
    {
        var graph = Graph();
        var info = Object(graph, 30)["info"]!.AsObject();
        foreach (string key in new[] { "output-node-id", "input-node-id", "output-port-id", "input-port-id" }) info.Remove(key);
        info["props"] = JsonNode.Parse("""{"link.output.node":"1","link.output.port":"11","link.input.node":"2","link.input.port":"12"}""");
        Object(graph, 11)["info"]!.AsObject().Remove("direction");
        Object(graph, 11)["info"]!["props"]!["port.direction"] = "out";
        Assert.Empty(AudioGraphInspection.Read(graph.ToJsonString()).Observations);
    }

    [Fact]
    public void IncompletePortAndNodeMetadataStaysExplicitlyUnknown()
    {
        var graph = Graph();
        Object(graph, 11)["info"]!["props"]!.AsObject().Remove("node.id");
        Object(graph, 12)["info"]!.AsObject().Remove("direction");
        Object(graph, 2).AsObject().Remove("info");
        var report = AudioGraphInspection.Read(graph.ToJsonString());
        Assert.True(report.Available);
        Assert.Equal(2, report.Observations.Count(o => o.Code == "incomplete-port"));
        Assert.Contains(report.Observations, o => o.Code == "incomplete-node" && o.Id == 2);
        Assert.Null(Assert.Single(report.Nodes, n => n.Id == 2).State);
    }

    [Fact]
    public void DeletedAndUnsupportedClockValuesDoNotKeepAnEarlierSetting()
    {
        var graph = Graph();
        var metadata = Object(graph, 4)["metadata"]!.AsArray();
        metadata.Add(JsonNode.Parse("""{"subject":0,"key":"clock.force-quantum","value":null}"""));
        metadata.Add(JsonNode.Parse("""{"subject":0,"key":"clock.rate","value":[{"credential":"PRIVATE-MARKER"}]}"""));
        var report = AudioGraphInspection.Read(graph.ToJsonString());
        Assert.DoesNotContain("clock.force-quantum", report.ClockSettings.Keys);
        Assert.DoesNotContain("clock.rate", report.ClockSettings.Keys);
        Assert.DoesNotContain("PRIVATE-MARKER", JsonSerializer.Serialize(report));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("4294967296")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("\"../1\"")]
    public void InvalidEndpointIdsStayUnknown(string token)
    {
        var graph = Graph();
        Object(graph, 30)["info"]!["input-node-id"] = JsonNode.Parse(token);
        var report = AudioGraphInspection.Read(graph.ToJsonString());
        Assert.True(report.Available);
        Assert.Null(Assert.Single(report.Links).InputNode);
        Assert.Contains(report.Observations, o => o.Code == "incomplete-endpoint");
        Assert.Contains(" -> unknown:12;", AudioGraphInspection.ToText(report));
    }

    [Fact]
    public void UnlinkedStreamsAreOnlyFlaggedWhenRunningAndInTheirAudioDirection()
    {
        var graph = Graph();
        Object(graph, 1)["info"]!["props"]!["media.class"] = "Stream/Input/Audio";
        var report = AudioGraphInspection.Read(graph.ToJsonString());
        Assert.Contains(report.Observations, o => o.Code == "unlinked-running-stream" && o.Id == 1);
        Object(graph, 1)["info"]!["state"] = "idle";
        Assert.Empty(AudioGraphInspection.Read(graph.ToJsonString()).Observations);
        graph.Remove(Object(graph, 30));
        report = AudioGraphInspection.Read(graph.ToJsonString());
        Assert.Equal("isolated-openxlr-node", Assert.Single(report.Observations).Code);
    }

    [Fact]
    public void RegistryUpdatesAndBothRemovalShapesUseTheExistingSnapshotRules()
    {
        string raw = Fixture + "\n" + """
            [{"id":1,"type":"PipeWire:Interface:Node","info":{"state":"error","props":{"node.name":"updated"}}},
             {"id":11,"info":null},{"id":12,"props":null}]
            """;
        string normalized = Diagnostics.GraphForArchive(raw);
        using var document = JsonDocument.Parse(normalized);
        Assert.Equal(5, document.RootElement.GetArrayLength());
        var report = AudioGraphInspection.Read(normalized);
        Assert.True(report.Available);
        Assert.Equal("updated", Assert.Single(report.Nodes, n => n.Id == 1).Name);
        Assert.Equal(0, report.PortCount);
        Assert.Equal(2, report.Observations.Count(o => o.Code == "missing-port"));
        Assert.Contains(report.Observations, o => o.Code == "node-error");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":1}]")]
    [InlineData("[{\"id\":1,\"type\":null}]")]
    [InlineData("[{\"id\":-1}]")]
    [InlineData("[{\"id\":4294967296}]")]
    [InlineData("[{\"id\":1,\"id\":2}]")]
    [InlineData("[{\"id\":1}")]
    public void MalformedGraphsAreUnavailableRatherThanEmptyHealthyGraphs(string json)
    {
        var report = AudioGraphInspection.Read(json);
        Assert.False(report.Available);
        Assert.Null(report.NodeCount);
        Assert.NotNull(report.Note);
        Assert.Empty(report.Nodes);
        Assert.Contains(report.Note, AudioGraphInspection.ToText(report));
    }

    [Theory]
    [InlineData("[] [")]
    [InlineData("[] {}")]
    public void InterruptedUpdateStreamsAreKeptForTheExistingErrorSidecar(string json)
        => Assert.Equal(json, Diagnostics.GraphForArchive(json));

    [Fact]
    public void AnEmptyGraphIsAValidZeroCountAndCyclesNeedNoRecursiveTraversal()
    {
        Assert.Equal(0, AudioGraphInspection.Read("[]").NodeCount);
        var graph = Graph();
        Object(graph, 30)["info"]!["input-node-id"] = 1;
        Object(graph, 12)["info"]!["props"]!["node.id"] = 1;
        var report = AudioGraphInspection.Read(graph.ToJsonString());
        Assert.True(report.Available);
        Assert.Empty(report.Observations);
    }

    [Fact]
    public void LargeGraphsHaveExplicitBudgetsAndTextCannotInjectTerminalControlLines()
    {
        Assert.False(AudioGraphInspection.Read(new string(' ', AudioGraphInspection.MaximumBytes + 1)).Available);
        var objects = Enumerable.Range(0, AudioGraphInspection.MaximumObjects + 1).Select(i => new { id = i });
        Assert.False(AudioGraphInspection.Read(JsonSerializer.Serialize(objects)).Available);
        var graph = Graph();
        Object(graph, 1)["info"]!["props"]!["node.name"] = "fixture\nINJECTED\u001b[31m";
        string report = AudioGraphInspection.ToText(AudioGraphInspection.Read(graph.ToJsonString()));
        Assert.DoesNotContain("fixture\nINJECTED", report);
        Assert.DoesNotContain('\u001b', report);
        Assert.Contains("\\nINJECTED", report);
    }

    [Fact]
    public void LongPropertyValuesAreBoundedAndUnrecognizedPropertiesAreNotCopied()
    {
        var graph = Graph();
        var props = Object(graph, 1)["info"]!["props"]!;
        props["node.latency"] = new string('x', 2048);
        props["application.process.args"] = "PRIVATE-MARKER";
        var report = AudioGraphInspection.Read(graph.ToJsonString());
        Assert.Equal(515, Assert.Single(report.Nodes, n => n.Id == 1).Properties["node.latency"].Length);
        Assert.DoesNotContain("PRIVATE-MARKER", JsonSerializer.Serialize(report));
    }
}
