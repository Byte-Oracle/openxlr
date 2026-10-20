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

        var input = MixLinks().First(pair => pair.From.StartsWith("OpenXLR_mix_chat:", StringComparison.Ordinal)
            && pair.To.StartsWith(stage + ":", StringComparison.Ordinal));
        BreakAndHealMixLink(pw, mixer, input.From, input.To);
        var output = MixLinks().First(pair => pair.From.StartsWith(stage + ":", StringComparison.Ordinal)
            && pair.To.StartsWith("OpenXLR_post_chat:", StringComparison.Ordinal));
        BreakAndHealMixLink(pw, mixer, output.From, output.To);

        Assert.Equal(identity, MixNodeSerial(stage));
        Assert.True(Assert.Single(mixer.Snapshot().Inserts["mix:chat"]).NativeHostRunning);
        Assert.False(mixer.EnsureFilterRoutes());
        CaptureLevel(0.1, "OpenXLR_mix_chat", "OpenXLR_post_chat");
    }

    [MonitorPipeWireFact]
    public void AnInitiallyRefusedPostLinkRetriesWithoutDroppingTheSurvivingSide()
    {
        string folder = Directory.CreateTempSubdirectory("openxlr-post-retry-").FullName;
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        string realLink = previousPath!.Split(Path.PathSeparator).Select(p => Path.Combine(p, "pw-link")).First(File.Exists);
        string marker = Path.Combine(folder, "reject");
        File.WriteAllText(marker, "1");
        File.WriteAllText(Path.Combine(folder, "real"), realLink);
        ExecutableScript.Write(Path.Combine(folder, "pw-link"), """
            #!/bin/sh
            folder=$(dirname "$0")
            if [ "$#" -eq 2 ] && [ "$1" = 'OpenXLR_mix_chat:monitor_FR' ] && [ "$2" = 'OpenXLR_post_chat:playback_FR' ] && [ -f "$folder/reject" ]; then
                echo 'transient test refusal' >&2
                exit 1
            fi
            exec "$(cat "$folder/real")" "$@"
            """);
        Environment.SetEnvironmentVariable("PATH", folder + Path.PathSeparator + previousPath);
        try
        {
            var pw = new PipeWireAdapter();
            using var registry = pw.WatchGraph();
            using var mixer = new Mixer(pw);
            mixer.Build(new MixerConfig { Channels = [], Mixes = [new("chat", "Chat", MixKind.VirtualMic)] });
            const string left = "OpenXLR_mix_chat:monitor_FL", leftTo = "OpenXLR_post_chat:playback_FL";
            const string right = "OpenXLR_mix_chat:monitor_FR", rightTo = "OpenXLR_post_chat:playback_FR";
            Assert.True(SpinWait.SpinUntil(() => MixLinks().Contains((left, leftTo)), TimeSpan.FromSeconds(3)));
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
