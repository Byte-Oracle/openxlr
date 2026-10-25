using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class FilterStateIntegrationTests
{
    [MonitorPipeWireFact]
    public void InternalFiltersAreNotPublishedAsDesktopDevices()
    {
        var pw = new PipeWireAdapter();
        try
        {
            FilterHandle mic = pw.CreateMicFilter("visibility", 120, false);
            FilterHandle delay = pw.CreateMixDelay("visibility");
            // Created afterwards, these selectable devices also establish
            // that the Pulse server has seen the graph's new endpoints.
            pw.CreateNullSink("visibility_output", "Test output");
            pw.CreateVirtualMic("OpenXLR_visibility_mic", "visibility_output.monitor", "OpenXLR Mic Effects");
            foreach (string kind in new[] { "sources", "sinks" })
            {
                ProcessResult result = ProcessRunner.Run("pactl", ["-f", "json", "list", kind]);
                Assert.True(result.Ok, result.Stderr);
                using JsonDocument devices = JsonDocument.Parse(result.Stdout);
                string[] names = [.. devices.RootElement.EnumerateArray().Select(d => d.GetProperty("name").GetString()!)];
                Assert.Contains(kind == "sources" ? "OpenXLR_visibility_mic" : "visibility_output", names);
                foreach (string node in new[] { mic.SinkName, mic.SourceName, delay.SinkName, delay.SourceName })
                    Assert.DoesNotContain(names, name => name == node || name == node + ".monitor");
            }
        }
        finally { pw.TearDown(); }
    }

    [MonitorPipeWireFact]
    public void TheSweepPutsBackAStageStreamTheDesktopTurnedDown()
    {
        var pw = new PipeWireAdapter();
        try
        {
            using var mixer = new Mixer(pw);
            mixer.Build(new MixerConfig { Channels = [], Mixes = [new("monitor", "Monitor", MixKind.Monitor)] });
            FilterHandle mic = pw.CreateMicFilter("held", 120, false);
            Assert.True(SpinWait.SpinUntil(() => pw.OwnStageStreams().Count(s => s.Name.StartsWith(mic.SinkName[..^3], StringComparison.Ordinal)) == 2,
                TimeSpan.FromSeconds(5)));
            OwnStageStream input = pw.OwnStageStreams().Single(s => s.Name == mic.SinkName);
            OwnStageStream output = pw.OwnStageStreams().Single(s => s.Name == mic.SourceName);
            Assert.True(input.Input);
            Assert.False(output.Input);

            // What a desktop applet does to a stream it lists as an application.
            foreach ((string command, string index, string value) in new[]
            {
                ("set-sink-input-volume", output.Serial.ToString(), "40%"),
                ("set-source-output-mute", input.Serial.ToString(), "1"),
            })
            {
                ProcessResult result = ProcessRunner.Run("pactl", [command, index, value]);
                Assert.True(result.Ok, result.Stderr);
            }
            Assert.True(SpinWait.SpinUntil(() => pw.OwnStageStreams().Count(s => s.Muted || s.Volume < .99) == 2, TimeSpan.FromSeconds(5)));

            IReadOnlyList<string> restored = mixer.EnsureOwnSinkLevels();
            Assert.Contains(mic.SinkName, restored);
            Assert.Contains(mic.SourceName, restored);
            Assert.True(SpinWait.SpinUntil(() => pw.OwnStageStreams().All(s => !s.Muted && s.Volume > .99), TimeSpan.FromSeconds(5)));
            Assert.DoesNotContain(mic.SinkName, mixer.EnsureOwnSinkLevels());
        }
        finally { pw.TearDown(); }
    }

    [MonitorPipeWireFact]
    public void InternalMicFiltersIgnoreSavedDesktopMuteAndGain()
    {
        var pw = new PipeWireAdapter();
        try
        {
            // The private runner seeds both halves with a saved mute and 12.5% gain.
            var filter = pw.CreateMicFilter("restore_test", 120, false);
            try
            {
                var result = ProcessRunner.Run("python3", ["-c", """
                    import math, struct, subprocess, sys, tempfile
                    rate = 48000
                    signal = b''.join(struct.pack('<f', .25 * math.sin(2 * math.pi * 1000 * i / rate)) for i in range(rate * 3))
                    args = ['--format=f32', '--rate=48000', '--channels=1']
                    if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
                    with tempfile.TemporaryFile() as capture:
                        record = subprocess.Popen(['pw-cat', '--record', *args, '--properties={ node.link-group = openxlr-test-record }', '--target', sys.argv[2], '-'], stdout=capture, stderr=subprocess.PIPE)
                        play = None
                        try:
                            play = subprocess.Popen(['pw-cat', '--playback', *args, '--properties={ node.link-group = openxlr-test-playback }', '--target', sys.argv[1], '-'], stdin=subprocess.PIPE, stderr=subprocess.PIPE)
                            _, errors = play.communicate(signal, timeout=10)
                            assert play.returncode == 0, errors.decode()
                            record.terminate(); record.communicate(timeout=5)
                            capture.seek(0); raw = capture.read()
                            values = struct.unpack('<' + 'f' * (len(raw) // 4), raw)
                            tail = values[-rate:]
                            rms = math.sqrt(sum(v*v for v in tail) / max(1, len(tail)))
                            assert .15 < rms < .20, f'Internal filter restored mute or gain: RMS={rms}'
                        finally:
                            for p in (play, record):
                                if p is not None:
                                    if p.poll() is None: p.kill()
                                    p.wait()
                    """, filter.SinkName, filter.SourceName], TimeSpan.FromSeconds(20));
                Assert.True(result.Ok, result.StdoutText + result.Stderr);
                using var graph = PipeWireSnapshot.Parse(ProcessRunner.Run("pw-dump", [], TimeSpan.FromSeconds(5)).Stdout);
                foreach (string name in new[] { filter.SinkName, filter.SourceName })
                {
                    var node = graph.RootElement.EnumerateArray().Single(n =>
                        n.TryGetProperty("info", out var info) && info.TryGetProperty("props", out var props) &&
                        props.TryGetProperty("node.name", out var value) && value.GetString() == name);
                    var props = node.GetProperty("info").GetProperty("props");
                    Assert.True(props.GetProperty("node.hidden").GetBoolean());
                    Assert.False(props.GetProperty("state.restore-props").GetBoolean());
                }
            }
            finally { pw.StopFilter(filter); }
        }
        finally { pw.TearDown(); }
    }
}
