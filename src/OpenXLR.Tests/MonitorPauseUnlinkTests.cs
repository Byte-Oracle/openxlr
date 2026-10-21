using System.Reflection;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class MonitorPauseUnlinkTests
{
    private const string LinkedGraph = """
        [
          {"id":1,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"monitor"}}},
          {"id":2,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"speakers"}}},
          {"id":11,"type":"PipeWire:Interface:Port","info":{"props":{"node.id":"1","port.name":"monitor_FL"}}},
          {"id":21,"type":"PipeWire:Interface:Port","info":{"props":{"node.id":"2","port.name":"playback_FL"}}},
          {"id":30,"type":"PipeWire:Interface:Link","info":{"output-port-id":11,"input-port-id":21}}
        ]
        """;

    [Theory]
    [InlineData(LinkedGraph, false)]
    [InlineData("{", false)]
    [InlineData("unavailable", false)]
    [InlineData("[]", true)]
    public void ARefusedDisconnectUsesAFreshSnapshotInsteadOfCachedAbsence(string graph, bool removed)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-pause-confirm-").FullName;
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            File.WriteAllText(Path.Combine(directory, "graph"), "[]");
            ExecutableScript.Write(Path.Combine(directory, "pw-dump"), """
                dir=${0%/*}
                printf 'dump\n' >> "$dir/dumps"
                if [ -f "$dir/unavailable" ]; then exit 1; fi
                /bin/cat "$dir/graph"
                """);
            ExecutableScript.Write(Path.Combine(directory, "pw-link"), """
                dir=${0%/*}
                printf '%s\n' "$*" >> "$dir/calls"
                echo 'temporary disconnect refusal' >&2
                exit 1
                """);
            Environment.SetEnvironmentVariable("PATH", directory);
            var adapter = new PipeWireAdapter();
            Assert.Empty(adapter.OwnSinkLevels());
            File.WriteAllText(Path.Combine(directory, "graph"), graph);
            if (graph == "unavailable") File.WriteAllText(Path.Combine(directory, "unavailable"), "");

            Assert.Equal(removed, adapter.EnsureUnlinked(new PortLink([("monitor:monitor_FL", "speakers:playback_FL")])));
            Assert.Equal(["-d monitor:monitor_FL speakers:playback_FL"], File.ReadAllLines(Path.Combine(directory, "calls")));
            Assert.Equal(2, File.ReadAllLines(Path.Combine(directory, "dumps")).Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ARefusedDisconnectKeepsItsOwnerThroughSelectionChangesUntilItCanBeRemoved()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-pause-unlink-").FullName;
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            File.WriteAllText(Path.Combine(directory, "graph"), LinkedGraph);
            File.WriteAllText(Path.Combine(directory, "refuse"), "");
            ExecutableScript.Write(Path.Combine(directory, "pw-dump"), """
                dir=${0%/*}
                /bin/cat "$dir/graph"
                """);
            ExecutableScript.Write(Path.Combine(directory, "pw-link"), """
                dir=${0%/*}
                printf '%s\n' "$*" >> "$dir/calls"
                if [ -f "$dir/refuse" ]; then echo 'temporary disconnect refusal' >&2; exit 1; fi
                printf '[]' > "$dir/graph"
                """);
            Environment.SetEnvironmentVariable("PATH", directory);
            using var mixer = new Mixer(new PipeWireAdapter());
            typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, true);
            var routes = Field<Dictionary<string, PortLink>>("_monitorRoutes");
            routes["speakers"] = new PortLink([("monitor:monitor_FL", "speakers:playback_FL")]);
            Field<List<string>>("_monitorOutputs").Add("speakers");

            mixer.SetMonitorMuted(true);
            Assert.Single(routes);
            mixer.SetMonitorOutputs([]);
            Assert.Single(routes);
            Assert.Throws<InvalidOperationException>(() => mixer.SetMonitorMuted(false));
            Assert.True(mixer.MonitorMuted);
            Assert.Single(routes);
            File.Delete(Path.Combine(directory, "refuse"));

            Assert.True(mixer.EnsureMonitorRoutes());
            Assert.Empty(routes);
            string[] calls = File.ReadAllLines(Path.Combine(directory, "calls"));
            Assert.All(calls, call => Assert.Equal("-d monitor:monitor_FL speakers:playback_FL", call));
            Assert.False(mixer.EnsureMonitorRoutes());
            mixer.SetMonitorMuted(true);
            Assert.Equal(calls, File.ReadAllLines(Path.Combine(directory, "calls")));
            mixer.SetMonitorMuted(false);
            Assert.False(mixer.MonitorMuted);

            T Field<T>(string name) => (T)typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!;
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Directory.Delete(directory, true);
        }
    }
}
