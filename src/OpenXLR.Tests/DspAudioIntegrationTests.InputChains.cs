using System.Globalization;
using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// Input chains rebuilt one at a time, on a private PipeWire server: XLR 1
/// and XLR 2 on a fake unit, each with the latency fixture, and a recall
/// that builds only what it changed. A chain is the same running chain while
/// its nodes and links keep their serials. Each case runs twice: with plain
/// settings, where the fixture runs in PipeWire's filter chain (or in the
/// native host when that PipeWire refuses LV2, as on the CI runner), and with
/// latency compensation on, which always puts it in the native host.
/// </summary>
public sealed partial class DspAudioIntegrationTests
{
    private const string ChainFixture = "urn:openxlr:test:latency";

    private static InsertDefinition Fixture(string id, double delay)
        => new() { Id = id, Kind = "lv2", Plugin = ChainFixture, Params = new() { ["delay"] = delay } };

    [DspPipeWireFact]
    public Task ChangingXlr2sChainKeepsXlr1sChainRunning() => OnTheUnit(mixer =>
    {
        mixer.SetInserts("xlr1", [Fixture("one", 100)]);
        mixer.SetInserts("xlr2", [Fixture("two", 200)]);
        ChainGraph xlr1 = SettledChain("xlr1");
        ChainGraph xlr2 = SettledChain("xlr2");

        // A new plugin on XLR 2 builds XLR 2's chain again and nothing else.
        mixer.SetInserts("xlr2", [Fixture("two", 200), Fixture("three", 300)]);
        ChainGraph rebuilt = SettledChain("xlr2");
        Assert.Empty(rebuilt.Nodes.Keys.Intersect(xlr2.Nodes.Keys));
        Assert.Equal(xlr1, SettledChain("xlr1"));

        // Bypass is a structure change too, for XLR 2 alone.
        mixer.SetInsertBypass("xlr2", "three", true);
        ChainGraph bypassed = SettledChain("xlr2");
        Assert.Empty(bypassed.Nodes.Keys.Intersect(rebuilt.Nodes.Keys));
        Assert.Equal(xlr1, SettledChain("xlr1"));

        // A control moves live: no chain is built for it.
        mixer.SetInsertParam("xlr2", "two", "delay", 250);
        AssertControl(mixer, "xlr2", "two", 250);
        Assert.Equal(bypassed, SettledChain("xlr2"));
        Assert.Equal(xlr1, SettledChain("xlr1"));

        // A chain that dies is built again alone by the sweep.
        KillChainHolder(bypassed);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            mixer.EnsureFilterRoutes();
            ChainGraph now = Chain("xlr2");
            return now.Nodes.Count > 0 && !now.Nodes.Keys.Intersect(bypassed.Nodes.Keys).Any();
        }, TimeSpan.FromSeconds(8)), "The dead XLR 2 chain was not built again.");
        SettledChain("xlr2");
        Assert.Equal(xlr1, SettledChain("xlr1"));
        AssertControl(mixer, "xlr2", "two", 250);
    });

    [DspPipeWireFact]
    public Task ARecallBuildsOnlyTheChainsWhosePluginsChanged() => OnTheUnit(mixer =>
    {
        mixer.SetInserts("xlr1", [Fixture("one", 100)]);
        mixer.SetInserts("xlr2", [Fixture("two", 200)]);
        mixer.SetInserts("mix:chat", [Fixture("chat", 300)]);
        MixerScene scene = mixer.ExportScene();
        ChainGraph xlr1 = SettledChain("xlr1"), xlr2 = SettledChain("xlr2"), chat = SettledChain("chat", "OpenXLR_ins_");
        AssertControl(mixer, "xlr1", "one", 100);
        AssertControl(mixer, "mix:chat", "chat", 300);

        // A profile whose chains match keeps every chain, recalled twice.
        mixer.ApplyScene(scene);
        mixer.ApplyScene(scene);
        Assert.Equal(xlr1, SettledChain("xlr1"));
        Assert.Equal(xlr2, SettledChain("xlr2"));
        Assert.Equal(chat, SettledChain("chat", "OpenXLR_ins_"));
        // So does the saved mixer read back at startup.
        mixer.ApplySettings(mixer.ExportSettings());
        Assert.Equal(xlr1, SettledChain("xlr1"));
        Assert.Equal(chat, SettledChain("chat", "OpenXLR_ins_"));

        // Only control values differ: they reach the running chains.
        MixerScene values = scene with
        {
            Inserts = new()
            {
                ["xlr1"] = [Fixture("one", 150)],
                ["xlr2"] = [Fixture("two", 200)],
                ["mix:chat"] = [Fixture("chat", 350)],
            },
        };
        mixer.ApplyScene(values);
        AssertControl(mixer, "xlr1", "one", 150);
        AssertControl(mixer, "mix:chat", "chat", 350);
        Assert.Equal(xlr1, SettledChain("xlr1"));
        Assert.Equal(xlr2, SettledChain("xlr2"));
        Assert.Equal(chat, SettledChain("chat", "OpenXLR_ins_"));
        Assert.Equal(150, Assert.Single(mixer.Snapshot().Inserts["xlr1"]).Insert.Params["delay"]);

        // A profile without XLR 2 leaves its chain alone; one with a
        // different XLR 2 chain builds XLR 2's alone.
        mixer.ApplyScene(values with { Inserts = new() { ["xlr1"] = [Fixture("one", 150)], ["mix:chat"] = [Fixture("chat", 350)] } });
        Assert.Equal(xlr2, SettledChain("xlr2"));
        mixer.ApplyScene(values with
        {
            Inserts = new()
            {
                ["xlr1"] = [Fixture("one", 150)],
                ["xlr2"] = [Fixture("two", 200), Fixture("more", 10)],
                ["mix:chat"] = [Fixture("chat", 350)],
            },
        });
        Assert.Empty(SettledChain("xlr2").Nodes.Keys.Intersect(xlr2.Nodes.Keys));
        Assert.Equal(xlr1, SettledChain("xlr1"));
        Assert.Equal(chat, SettledChain("chat", "OpenXLR_ins_"));

        // A mix the profile does not name has no inserts, as before.
        mixer.ApplyScene(values with { Inserts = new() { ["xlr1"] = [Fixture("one", 150)] } });
        Assert.True(SpinWait.SpinUntil(() => Chain("chat", "OpenXLR_ins_").Nodes.Count == 0, TimeSpan.FromSeconds(3)));
        Assert.False(mixer.Snapshot().Inserts.ContainsKey("mix:chat"));
        Assert.Equal(xlr1, SettledChain("xlr1"));
        Assert.Equal(2, mixer.Snapshot().Inserts["xlr2"].Count);
    });

    /// <summary>
    /// Run a case on XLR 1 and XLR 2 of the fake unit, with its signal
    /// playing and XLR 1 sent to Chat so every chain processes audio: once
    /// with plain settings and once with latency compensation on.
    /// </summary>
    private static async Task OnTheUnit(Action<Mixer> test)
    {
        Assert.True(PluginCatalog.Find("lv2", ChainFixture) is not null,
            "Build native/tests/latency.lv2 (make -C native test-lv2) and add native/tests to LV2_PATH.");
        Assert.True(NativePluginHost.HostInstalled, "Build with -p:EnableNativeLv2Host=true so the native host sits beside the tests.");
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        (uint parent, uint unit) = MonitorVolumeIntegrationTests.LoadBindUnit(pw);
        using var stop = new CancellationTokenSource();
        Task<ProcessResult> signal = MonitorVolumeIntegrationTests.BindUnitSignal(stop.Token);
        try
        {
            foreach (bool compensate in new[] { false, true })
            {
                using var mixer = new Mixer(pw);
                mixer.SetInputDeviceHint(["Wave_XLR_BINDUNIT-"], hardwareOutputRouting: false, modelFallback: false);
                mixer.Build(MixerConfig.Default());
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    mixer.EnsureInputFeeds();
                    return mixer.Snapshot().Channels.Where(c => c.Id is "xlr1" or "xlr2").All(c => c.BindingConnected);
                }, TimeSpan.FromSeconds(5)), "XLR 1 and XLR 2 did not find the test unit.");
                mixer.SetLevel("xlr1", "chat", 1);
                mixer.SetChannelMuted("xlr1", "chat", false);
                mixer.SetMixLatencyCompensation(compensate);
                test(mixer);
            }
        }
        finally
        {
            stop.Cancel();
            await signal;
            MonitorVolumeIntegrationTests.UnloadModule(unit);
            MonitorVolumeIntegrationTests.UnloadModule(parent);
        }
    }

    /// <summary>
    /// Wait until the fixture's delay control in the running chain reads
    /// <paramref name="delay"/>. PipeWire's filter chain shows it in
    /// pw-dump; the native host shows nothing there, but the fixture
    /// reports its delay as its latency, which the host reads back.
    /// </summary>
    private static void AssertControl(Mixer mixer, string chainKey, string insertId, double delay)
    {
        (string key, string prefix) = chainKey.StartsWith("mix:", StringComparison.Ordinal)
            ? (chainKey[4..], "OpenXLR_ins_") : (chainKey, "OpenXLR_lc_");
        int rate = PipeWireAdapter.ParseGraphSampleRate(ProcessRunner.Run("pw-metadata", ["-n", "settings"]).StdoutText);
        string seen = "";
        Assert.True(SpinWait.SpinUntil(() =>
        {
            if (Chain(key, prefix).Nodes.Keys.Any(n => n.Contains("_stage_", StringComparison.Ordinal)))
            {
                double? ms = mixer.Snapshot().Inserts.GetValueOrDefault(chainKey)?
                    .FirstOrDefault(i => i.Insert.Id == insertId)?.LatencyMilliseconds;
                seen = $"native host latency {ms} ms";
                return ms is double reported && Math.Abs(reported - delay * 1000 / rate) < 1e-3;
            }
            double? value = ChainControl(key, "i0:delay", prefix);
            seen = $"filter chain i0:delay {value}";
            return value == delay;
        }, TimeSpan.FromSeconds(8)), $"{chainKey} {insertId} did not reach delay {delay}: {seen}");
    }

    /// <summary>A chain's nodes by name with their serials, and the serials of every link to or from them.</summary>
    private sealed record ChainGraph(IReadOnlyDictionary<string, string> Nodes, IReadOnlyList<string> Links, IReadOnlyList<int> Clients)
    {
        public bool Equals(ChainGraph? other) => other is not null
            && Nodes.OrderBy(n => n.Key).SequenceEqual(other.Nodes.OrderBy(n => n.Key))
            && Links.SequenceEqual(other.Links);

        public override int GetHashCode() => Nodes.Count;

        public override string ToString()
            => string.Join(", ", Nodes.Select(n => $"{n.Key}#{n.Value}")) + " links " + string.Join(",", Links);
    }

    /// <summary>The chain of <paramref name="key"/>, once its links are all up.</summary>
    private static ChainGraph SettledChain(string key, string prefix = "OpenXLR_lc_")
    {
        ChainGraph graph = Chain(key, prefix);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            ChainGraph now = Chain(key, prefix);
            bool settled = now.Nodes.Count > 0 && now.Links.Count >= 2 && now.Equals(graph);
            graph = now;
            return settled;
        }, TimeSpan.FromSeconds(5)), $"The {key} chain did not settle: {graph}");
        return graph;
    }

    private static ChainGraph Chain(string key, string prefix = "OpenXLR_lc_")
    {
        using JsonDocument dump = Dump();
        var nodes = new Dictionary<string, string>();
        var ids = new HashSet<int>();
        var clients = new List<int>();
        foreach (JsonElement item in dump.RootElement.EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "PipeWire:Interface:Node"
                || !item.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Object
                || !info.TryGetProperty("props", out JsonElement props)
                || !props.TryGetProperty("node.name", out JsonElement name)
                || name.GetString() is not string node || !node.StartsWith($"{prefix}{key}_", StringComparison.Ordinal)) continue;
            nodes[node] = props.GetProperty("object.serial").ToString();
            ids.Add(item.GetProperty("id").GetInt32());
            if (props.TryGetProperty("client.id", out JsonElement client)) clients.Add(client.GetInt32());
        }
        var links = new List<string>();
        foreach (JsonElement item in dump.RootElement.EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "PipeWire:Interface:Link"
                || !item.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Object) continue;
            if (ids.Contains(info.GetProperty("output-node-id").GetInt32()) || ids.Contains(info.GetProperty("input-node-id").GetInt32()))
                links.Add(info.GetProperty("props").GetProperty("object.serial").ToString());
        }
        links.Sort(StringComparer.Ordinal);
        return new(nodes, links, clients);
    }

    /// <summary>A plugin control as PipeWire reports it on the chain's input half, or null.</summary>
    private static double? ChainControl(string key, string control, string prefix = "OpenXLR_lc_")
    {
        using JsonDocument dump = Dump();
        foreach (JsonElement item in dump.RootElement.EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "PipeWire:Interface:Node"
                || !item.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Object
                || info.GetProperty("props").TryGetProperty("node.name", out JsonElement name) is false
                || name.GetString() is not string node
                || !node.StartsWith($"{prefix}{key}_", StringComparison.Ordinal) || !node.EndsWith("_in", StringComparison.Ordinal)
                || !info.TryGetProperty("params", out JsonElement parameters)
                || !parameters.TryGetProperty("Props", out JsonElement all)) continue;
            foreach (JsonElement props in all.EnumerateArray())
            {
                if (!props.TryGetProperty("params", out JsonElement pairs)) continue;
                JsonElement[] flat = [.. pairs.EnumerateArray()];
                for (int i = 0; i + 1 < flat.Length; i += 2)
                    if (flat[i].ValueKind == JsonValueKind.String && flat[i].GetString() == control) return flat[i + 1].GetDouble();
            }
        }
        return null;
    }

    /// <summary>
    /// End the process that holds a chain, pw-cli for a filter chain or the
    /// native host, after checking that this test process started it, so
    /// nothing outside the test is touched.
    /// </summary>
    private static void KillChainHolder(ChainGraph chain)
    {
        using JsonDocument dump = Dump();
        int clientId = chain.Clients.Distinct().Single();
        JsonElement client = dump.RootElement.EnumerateArray().Single(item =>
            item.GetProperty("type").GetString() == "PipeWire:Interface:Client" && item.GetProperty("id").GetInt32() == clientId);
        int pid = int.Parse(client.GetProperty("info").GetProperty("props").GetProperty("application.process.id").ToString(),
            CultureInfo.InvariantCulture);
        string stat = File.ReadAllText($"/proc/{pid}/stat");
        string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        Assert.Equal(Environment.ProcessId, int.Parse(fields[1], CultureInfo.InvariantCulture));
        string command = File.ReadAllText($"/proc/{pid}/comm").Trim();
        Assert.True(command is "pw-cli" || command.StartsWith("openxlr-lv2-hos", StringComparison.Ordinal), command);
        using var holder = System.Diagnostics.Process.GetProcessById(pid);
        holder.Kill();
        holder.WaitForExit(2000);
    }

    private static JsonDocument Dump()
    {
        ProcessResult dump = ProcessRunner.Run("pw-dump", [], TimeSpan.FromSeconds(5));
        Assert.True(dump.Ok, dump.Stderr);
        return PipeWireSnapshot.Parse(dump.Stdout);
    }
}
