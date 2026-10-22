using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using System.Text.Json;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void VirtualMicrophoneLinksRecoverWithoutAnyInsertChain()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(new MixerConfig { Channels = [], Mixes = [new("chat", "Chat", MixKind.VirtualMic)] });

        foreach (string side in new[] { "FL", "FR" })
            BreakAndHealMixLink(pw, mixer, "OpenXLR_mix_chat:monitor_" + side, "OpenXLR_post_chat:playback_" + side);
        Assert.False(mixer.EnsureFilterRoutes());
        CaptureLevel(0.1, "OpenXLR_mix_chat", "OpenXLR_post_chat");
    }

    [DspPipeWireFact]
    public void MixInsertAndPostLinksRecoverWithoutRestartingThePlugin()
    {
        const string plugin = "urn:openxlr:test:latency";
        Assert.NotNull(PluginCatalog.Find("lv2", plugin));
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(new MixerConfig { Channels = [], Mixes = [new("chat", "Chat", MixKind.VirtualMic)] });
        mixer.SetMixLatencyCompensation(true);
        mixer.SetInserts("mix:chat", [new() { Id = "test", Kind = "lv2", Plugin = plugin }]);
        InsertStatus running = Assert.Single(mixer.Snapshot().Inserts["mix:chat"]);
        Assert.True(running.NativeHostRunning, running.ToString());
        string stage = pw.DumpNodes().Single(n => n.Name.StartsWith("OpenXLR_ins_chat_", StringComparison.Ordinal)
            && n.Name.EndsWith("_stage_0", StringComparison.Ordinal)).Name;
        string identity = MixNodeSerial(stage);
        // The host reports the plugin's latency once audio runs, and the
        // stage's ports appear after the build returns. Each of those makes
        // one later sweep report a change, so the links are broken only
        // once the graph has settled.
        double latency = WaitForSettledMix(mixer, "mix:chat");

        var input = MixLinks().First(pair => pair.From.StartsWith("OpenXLR_mix_chat:", StringComparison.Ordinal)
            && pair.To.StartsWith(stage + ":", StringComparison.Ordinal));
        BreakAndHealMixLink(pw, mixer, input.From, input.To);
        var output = MixLinks().First(pair => pair.From.StartsWith(stage + ":", StringComparison.Ordinal)
            && pair.To.StartsWith("OpenXLR_post_chat:", StringComparison.Ordinal));
        BreakAndHealMixLink(pw, mixer, output.From, output.To);

        Assert.Equal(identity, MixNodeSerial(stage));
        InsertStatus healed = Assert.Single(mixer.Snapshot().Inserts["mix:chat"]);
        Assert.True(healed.NativeHostRunning);
        Assert.Equal(latency, healed.LatencyMilliseconds);
        Assert.False(mixer.EnsureFilterRoutes());
        CaptureLevel(0.1, "OpenXLR_mix_chat", "OpenXLR_post_chat");
    }

    [MonitorPipeWireFact]
    public void AnInitiallyRefusedPostLinkRetriesWithoutDroppingTheSurvivingSide()
    {
        string folder = Directory.CreateTempSubdirectory("openxlr-post-retry-").FullName;
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            string realLink = (previousPath ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p, "pw-link")).First(File.Exists);
            string marker = Path.Combine(folder, "reject");
            File.WriteAllText(marker, "1");
            File.WriteAllText(Path.Combine(folder, "real"), realLink);
            ExecutableScript.Write(Path.Combine(folder, "pw-link"), """
                #!/bin/sh
                folder=$(dirname "$0")
                if [ "$#" -eq 2 ] && [ "$1" = 'OpenXLR_mix_chat:monitor_FR' ] && [ "$2" = 'OpenXLR_post_chat:playback_FR' ] && [ -f "$folder/reject" ]; then
                    touch "$folder/refused"
                    echo 'transient test refusal' >&2
                    exit 1
                fi
                exec "$(cat "$folder/real")" "$@"
                """);
            Environment.SetEnvironmentVariable("PATH", folder + Path.PathSeparator + previousPath);
            var pw = new PipeWireAdapter();
            using var registry = pw.WatchGraph();
            using var mixer = new Mixer(pw);
            mixer.Build(new MixerConfig { Channels = [], Mixes = [new("chat", "Chat", MixKind.VirtualMic)] });
            const string left = "OpenXLR_mix_chat:monitor_FL", leftTo = "OpenXLR_post_chat:playback_FL";
            const string right = "OpenXLR_mix_chat:monitor_FR", rightTo = "OpenXLR_post_chat:playback_FR";
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureFilterRoutes();
                return MixLinks().Contains((left, leftTo)) && File.Exists(Path.Combine(folder, "refused"));
            }, TimeSpan.FromSeconds(3)));
            Assert.DoesNotContain((right, rightTo), MixLinks());
            mixer.EnsureFilterRoutes();
            Assert.Contains((left, leftTo), MixLinks());
            File.Delete(marker);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureFilterRoutes();
                return MixLinks().Contains((right, rightTo));
            }, TimeSpan.FromSeconds(4)), "The initially refused stereo side did not recover.");
            Assert.Contains((left, leftTo), MixLinks());
            Assert.False(mixer.EnsureFilterRoutes());
            CaptureLevel(0.1, "OpenXLR_mix_chat", "OpenXLR_post_chat");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Directory.Delete(folder, true);
        }
    }

    /// <summary>
    /// Sweep until the insert on <paramref name="chain"/> has reported its
    /// latency and a sweep finds nothing left to change; the figure it settled on.
    /// </summary>
    private static double WaitForSettledMix(Mixer mixer, string chain)
    {
        double? latency = null;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            // Read before the sweep, so a report that lands during it is
            // still seen by the sweep that has to come after.
            latency = Assert.Single(mixer.Snapshot().Inserts[chain]).LatencyMilliseconds;
            return !mixer.EnsureFilterRoutes() && latency is not null;
        }, TimeSpan.FromSeconds(8)), "The insert chain did not settle: " + JsonSerializer.Serialize(mixer.Snapshot()));
        return latency!.Value;
    }

    private static void BreakAndHealMixLink(PipeWireAdapter pw, Mixer mixer, string from, string to)
    {
        pw.Run("pw-link", "-d", from, to);
        Assert.True(SpinWait.SpinUntil(() => !MixLinks().Contains((from, to)), TimeSpan.FromSeconds(3)));
        Assert.True(SpinWait.SpinUntil(() =>
        {
            mixer.EnsureFilterRoutes();
            return MixLinks().Contains((from, to));
        }, TimeSpan.FromSeconds(4)), "A lost internal mix link did not recover.");
    }

    private static HashSet<(string From, string To)> MixLinks()
    {
        using var graph = MixGraph();
        return PipeWireAdapter.ParseGraphLinks(graph.RootElement.EnumerateArray());
    }

    private static string MixNodeSerial(string name)
    {
        using var graph = MixGraph();
        JsonElement node = graph.RootElement.EnumerateArray().Single(item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "PipeWire:Interface:Node"
            && item.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Object
            && props.TryGetProperty("node.name", out var value) && value.GetString() == name);
        return node.GetProperty("info").GetProperty("props").GetProperty("object.serial").ToString();
    }

    private static JsonDocument MixGraph()
    {
        ProcessResult dump = ProcessRunner.Run("pw-dump", []);
        Assert.True(dump.Ok, dump.Stderr);
        return PipeWireSnapshot.Parse(dump.Stdout);
    }
}
