using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// Elgato's full-speed units record silence when their playback stream
/// starts before capture, so the mixer links the interface's capture into
/// the XLR strip before it routes any mix into the interface's own sink.
/// A pw-link wrapper records the order in which the links are made.
/// </summary>
public sealed partial class MonitorVolumeIntegrationTests
{
    private const string Wave3Card = "usb-Elgato_Systems_Elgato_Wave_3_ORDERTEST-00";
    private const string Wave3Sink = "alsa_output." + Wave3Card + ".pro-output-0";
    private const string Wave3Source = "alsa_input." + Wave3Card + ".pro-input-0";
    private static readonly string[] Wave3Hints = ["Wave_3_ORDERTEST", "Elgato_Wave_3_"];

    [MonitorPipeWireFact]
    public void ADaemonMonitorOutputOnTheInterfaceIsLinkedAfterItsCapture()
    {
        using var links = new LinkLog();
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph(); using var mixer = new Mixer(pw);
        pw.CreateNullSink(Wave3Sink, "Wave 3 test output");
        WaitForInputPorts(Wave3Sink, 2);
        WaveSource(pw, "test_wave3_parent", Wave3Source);
        mixer.SetInputDeviceHint(Wave3Hints, hardwareOutputRouting: false, modelFallback: false);

        // monitorOutput in daemon.json or OPENXLR_MONITOR_OUTPUT names the unit.
        links.Start();
        mixer.Build(HardwareInputConfig, monitorOutputSink: Wave3Sink);

        links.AssertCaptureFirst();
        Assert.Equal(2, IncomingRouteLinks(Xlr1).Length);
        Assert.Equal(2, GraphLinksInto(Wave3Sink).Length);
    }

    [MonitorPipeWireFact]
    public void TheRouteIntoTheInterfaceWaitsForItsCaptureToAppear()
    {
        using var links = new LinkLog();
        // A clock that stands still: the wait never runs out in this test.
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph(); using var mixer = new Mixer(pw) { Clock = () => 0 };
        // The card's sink is published before its capture source, as after a
        // replug or a profile switch.
        pw.CreateNullSink(Wave3Sink, "Wave 3 test output");
        WaitForInputPorts(Wave3Sink, 2);
        mixer.SetInputDeviceHint(Wave3Hints, hardwareOutputRouting: false, modelFallback: false);
        links.Start();
        mixer.Build(HardwareInputConfig, monitorOutputSink: Wave3Sink);
        mixer.EnsureInputFeeds();
        mixer.EnsureMonitorRoutes();
        Assert.Empty(GraphLinksInto(Wave3Sink));

        WaveSource(pw, "test_wave3_late_parent", Wave3Source);
        // One sweep's order: the strips' feeds, then the monitor routes.
        WaitForRecovery(() =>
        {
            mixer.EnsureInputFeeds();
            mixer.EnsureMonitorRoutes();
            return IncomingRouteLinks(Xlr1).Length == 2 && GraphLinksInto(Wave3Sink).Length == 2;
        });
        links.AssertCaptureFirst();
    }

    [MonitorPipeWireFact]
    public void ACaptureThatNeverComesStopsHoldingTheOutputAfterTheGrace()
    {
        using var links = new LinkLog();
        long now = 0;
        var notes = new List<string>();
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw) { Clock = () => Volatile.Read(ref now), InputNote = notes.Add };
        pw.CreateNullSink(Wave3Sink, "Wave 3 test output");
        WaitForInputPorts(Wave3Sink, 2);
        mixer.SetInputDeviceHint(Wave3Hints, hardwareOutputRouting: false, modelFallback: false);
        mixer.Build(HardwareInputConfig);
        // The user picks the interface as the monitor output.
        mixer.SetMonitorOutputs([Wave3Sink]);
        mixer.EnsureMonitorRoutes();
        Assert.Empty(GraphLinksInto(Wave3Sink));
        Assert.Empty(notes);

        Volatile.Write(ref now, (long)Mixer.CaptureFirstGrace.TotalMilliseconds);
        WaitForRecovery(() => { mixer.EnsureMonitorRoutes(); return GraphLinksInto(Wave3Sink).Length == 2; });
        Assert.Contains(notes, note => note.Contains(Wave3Sink, StringComparison.Ordinal));
        int noted = notes.Count;
        mixer.EnsureMonitorRoutes();
        Assert.Equal(noted, notes.Count);   // said once, not on every sweep
        Assert.Empty(IncomingRouteLinks(Xlr1));
    }

    /// <summary>
    /// Puts a pw-link in front of the real one that appends every link it
    /// made, "output input", to a file, and takes it away again on dispose.
    /// </summary>
    private sealed class LinkLog : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-link-order-").FullName;
        private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
        private string Log => Path.Combine(_directory, "links");

        public LinkLog()
        {
            string real = (_path ?? "").Split(Path.PathSeparator)
                .Select(p => Path.GetFullPath(Path.Combine(p, "pw-link"))).First(File.Exists);
            ExecutableScript.Write(Path.Combine(_directory, "pw-link"), $$"""
                '{{real}}' "$@"
                status=$?
                if [ "$status" -eq 0 ] && [ "$#" -eq 2 ] && [ -e '{{Log}}.on' ]; then
                  case "$1" in -*) ;; *) echo "$1 $2" >> '{{Log}}' ;; esac
                fi
                exit "$status"
                """);
            Environment.SetEnvironmentVariable("PATH", _directory + Path.PathSeparator + _path);
        }

        /// <summary>Record from now on; links made by the fixture setup are left out.</summary>
        public void Start() => File.WriteAllText(Log + ".on", "");

        public void AssertCaptureFirst()
        {
            string[] made = File.Exists(Log) ? File.ReadAllLines(Log) : [];
            int capture = Array.FindIndex(made, l => l.StartsWith(Wave3Source + ":", StringComparison.Ordinal)
                && l.Contains(" " + Xlr1 + ":", StringComparison.Ordinal));
            int playback = Array.FindIndex(made, l => l.Contains(" " + Wave3Sink + ":", StringComparison.Ordinal));
            Assert.True(capture >= 0, "the capture was never linked:\n" + string.Join('\n', made));
            Assert.True(playback >= 0, "nothing was routed into the interface:\n" + string.Join('\n', made));
            Assert.True(capture < playback, "playback into the interface started before its capture:\n" + string.Join('\n', made));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _path);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
