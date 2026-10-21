using System.Globalization;
using System.Reflection;
using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class MonitorVolumeSyncTests
{
    private sealed class Sinks : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("openxlr-sinks-").FullName;
        private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
        public PipeWireAdapter Adapter { get; } = new();
        public Mixer Mixer { get; }
        public string[] Writes => File.ReadAllLines(Path.Combine(_dir, "writes"));
        public int Dumps => File.ReadAllLines(Path.Combine(_dir, "reads")).Length;
        public long Now { get; set; }

        public Sinks()
        {
            ExecutableScript.Write(Path.Combine(_dir, "pw-dump"), """
                dir=${0%/*}
                echo read >> "$dir/reads"
                if [ -f "$dir/graph-unavailable" ]; then exit 1; fi
                /bin/cat "$dir/dump.json"
                """);
            ExecutableScript.Write(Path.Combine(_dir, "pw-link"), "exit 0\n");
            ExecutableScript.Write(Path.Combine(_dir, "pactl"), """
                dir=${0%/*}
                echo "$*" >> "$dir/writes"
                if [ "$1" = get-sink-mute ]; then
                    /bin/cat "$dir/mute"
                    exit
                fi
                if [ "$1" = set-sink-mute ]; then
                    if [ -f "$dir/fail" ]; then
                        left=$(/bin/cat "$dir/fail")
                        if [ "$left" -gt 0 ]; then
                            printf '%s\n' "$((left - 1))" > "$dir/fail"
                            echo 'transient mute failure' >&2
                            exit 1
                        fi
                    fi
                    flip() {
                        current=$(/bin/cat "$dir/mute")
                        case "$current" in 'Mute: yes') printf 'Mute: no\n' > "$dir/mute" ;; *) printf 'Mute: yes\n' > "$dir/mute" ;; esac
                    }
                    if [ -f "$dir/press-before-write" ]; then
                        /bin/rm "$dir/press-before-write"
                        flip
                    fi
                    case "$3" in 0) printf 'Mute: no\n' > "$dir/mute" ;; 1) printf 'Mute: yes\n' > "$dir/mute" ;; toggle) flip ;; esac
                fi
                """);
            Environment.SetEnvironmentVariable("PATH", _dir);
            Adapter.SetSinkMuted("OpenXLR_mix_monitor", false);
            File.WriteAllText(Path.Combine(_dir, "writes"), "");
            Mixer = new(Adapter) { Clock = () => Now };
            typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer, true);
            typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer,
                new MixerConfig { Channels = [], Mixes = [new("monitor", "Monitor", MixKind.Monitor)] });
        }

        public void ApplicationChannel() => typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(Mixer, new MixerConfig { Channels = [new("system", "System")], Mixes = [new("monitor", "Monitor", MixKind.Monitor)] });

        public void SelectExternalOutput() => ((List<string>)typeof(Mixer).GetField("_monitorOutputs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Mixer)!).Add("external");

        public void DirectMute(bool muted) => File.WriteAllText(Path.Combine(_dir, "mute"), muted ? "Mute: yes\n" : "Mute: no\n");
        public void FailMuteWrites(int count) => File.WriteAllText(Path.Combine(_dir, "fail"), count.ToString(CultureInfo.InvariantCulture));
        public void PressDuringNextMuteWrite() => File.WriteAllText(Path.Combine(_dir, "press-before-write"), "");
        public void GraphUnavailable(bool unavailable)
        {
            string path = Path.Combine(_dir, "graph-unavailable");
            if (unavailable) File.WriteAllText(path, "");
            else File.Delete(path);
            typeof(PipeWireAdapter).GetMethod("InvalidateDump", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Adapter, null);
        }

        public void Dump(double volume, bool muted = false, bool internalSink = false, bool? externalMuted = null, int externalSerial = 1)
        {
            string amplitude = Math.Pow(volume, 3).ToString("R", CultureInfo.InvariantCulture);
            string monitor = $$$$"""
                {"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"OpenXLR_mix_monitor","media.class":"Audio/Sink"},
                "params":{"Props":[{"channelVolumes":[{{{{amplitude}}}}],"mute":{{{{JsonSerializer.Serialize(muted)}}}}}]}}}
                """;
            string channel = """
                {"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"OpenXLR_ch_system","media.class":"Audio/Sink"},
                "params":{"Props":[{"channelVolumes":[0.125],"mute":true}]}}}
                """;
            string external = externalMuted is bool value ? "," + JsonSerializer.Serialize(new
            {
                type = "PipeWire:Interface:Node", info = new
                {
                    props = new Dictionary<string, string> { ["node.name"] = "external", ["media.class"] = "Audio/Sink",
                        ["object.serial"] = externalSerial.ToString(CultureInfo.InvariantCulture) },
                    @params = new { Props = new[] { new { channelVolumes = new[] { 1.0 }, mute = value } } },
                },
            }) : "";
            File.WriteAllText(Path.Combine(_dir, "dump.json"), "[" + monitor + (internalSink ? "," + channel : "") + external + "]");
            typeof(PipeWireAdapter).GetMethod("InvalidateDump", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Adapter, null);
        }

        public void Dispose()
        {
            try { Mixer.Dispose(); }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", _path);
                Directory.Delete(_dir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(0.665, 0.66)]
    [InlineData(0.675, 0.68)]
    [InlineData(1.205, 1.2)]
    public void RoundedReadbackDoesNotChangeTheStoredMaster(double stored, double readback)
    {
        using var sinks = new Sinks();
        sinks.Mixer.SetMixVolume("monitor", stored);
        sinks.Dump(readback);

        Assert.False(sinks.Mixer.SyncMonitorVolumes());

        Assert.Equal(stored, Assert.Single(sinks.Mixer.Snapshot().Mixes).Volume);
    }

    [Theory]
    [InlineData(0.65, 0.65)]
    [InlineData(1.2, 1.2)]
    [InlineData(1.6, 1.5)]
    public void DesktopChangesAreReadAndBoostIsClamped(double desktop, double expected)
    {
        using var sinks = new Sinks();
        sinks.Mixer.SetMixVolume("monitor", 0.665);
        sinks.Dump(desktop, muted: true);

        Assert.True(sinks.Mixer.SyncMonitorVolumes());

        var mix = Assert.Single(sinks.Mixer.Snapshot().Mixes);
        Assert.Equal(expected, mix.Volume);
        Assert.True(mix.Muted);
        if (desktop > 1.5) Assert.Contains("set-sink-volume OpenXLR_mix_monitor 150%", sinks.Writes);
    }

    [Fact]
    public void SinkRepairAndMonitorReadbackShareOneDump()
    {
        using var sinks = new Sinks();
        sinks.Dump(0.6, internalSink: true);

        Assert.True(sinks.Mixer.SyncOwnSinkLevels(out var restored));

        Assert.Equal(["OpenXLR_ch_system"], restored);
        Assert.Equal(0.6, Assert.Single(sinks.Mixer.Snapshot().Mixes).Volume);
        Assert.Equal(1, sinks.Dumps);
        Assert.Contains("set-sink-volume OpenXLR_ch_system 100%", sinks.Writes);
        Assert.Contains("set-sink-mute OpenXLR_ch_system 0", sinks.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AUiWriteInvalidatesTheDumpBeforeTheNextSweep(bool mute)
    {
        using var sinks = new Sinks();
        sinks.Dump(0.6);
        Assert.True(sinks.Mixer.SyncOwnSinkLevels(out _));
        if (mute) sinks.Mixer.SetMixMuted("monitor", true);
        else sinks.Mixer.SetMixVolume("monitor", 0.8);
        sinks.Dump(mute ? 0.6 : 0.8, muted: mute);

        Assert.False(sinks.Mixer.SyncOwnSinkLevels(out _));

        Assert.Equal(2, sinks.Dumps);
        var mix = Assert.Single(sinks.Mixer.Snapshot().Mixes);
        Assert.Equal(mute ? 0.6 : 0.8, mix.Volume);
        Assert.Equal(mute, mix.Muted);
    }

    [Fact]
    public void AStaleApplicationMuteEchoDoesNotBecomeAnotherKeyPress()
    {
        using var sinks = new Sinks();
        sinks.ApplicationChannel();
        sinks.DirectMute(true);
        sinks.Dump(0.6, internalSink: true);

        Assert.True(sinks.Mixer.SyncOwnSinkLevels(out _));
        Assert.True(sinks.Mixer.MonitorMuted);
        Assert.False(sinks.Mixer.SyncOwnSinkLevels(out _));
        Assert.True(sinks.Mixer.MonitorMuted);
    }

    [Fact]
    public void LinuxCanStillEditAnIndividualMixMuteWhileMonitoringIsPaused()
    {
        using var sinks = new Sinks();
        sinks.Mixer.SetMonitorMuted(true);
        sinks.Dump(0.6, muted: true);
        Assert.True(sinks.Mixer.SyncOwnSinkLevels(out _));
        Assert.True(Assert.Single(sinks.Mixer.Snapshot().Mixes).Muted);
        sinks.Dump(0.6, muted: false);
        Assert.True(sinks.Mixer.SyncOwnSinkLevels(out _));
        Assert.False(Assert.Single(sinks.Mixer.Snapshot().Mixes).Muted);
        Assert.True(sinks.Mixer.MonitorMuted);
    }

    [Fact]
    public void ARefusedNativePulseBacksOffAndRecoversWithoutForgettingTheNextKey()
    {
        using var sinks = new Sinks();
        sinks.SelectExternalOutput();
        sinks.Dump(0.6, externalMuted: false);
        sinks.DirectMute(false);
        sinks.Mixer.SyncDeviceVolumes();
        sinks.Dump(0.6, externalMuted: true);
        sinks.DirectMute(true);
        sinks.FailMuteWrites(4);

        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        Assert.False(sinks.Mixer.MonitorMuted); // No pulse is counted before its consume is acknowledged.
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        int beforeBackoff = sinks.Writes.Count(w => w.StartsWith("set-sink-mute external ", StringComparison.Ordinal));
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        Assert.Equal(beforeBackoff, sinks.Writes.Count(w => w.StartsWith("set-sink-mute external ", StringComparison.Ordinal)));
        sinks.Now = 1_000;
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        sinks.Now = 3_000;
        Assert.True(sinks.Mixer.SyncDeviceVolumes());
        Assert.False(sinks.Adapter.GetSinkMuted("external"));
        Assert.True(sinks.Mixer.MonitorMuted);
        Assert.False(sinks.Mixer.SyncDeviceVolumes());

        sinks.DirectMute(true);
        Assert.True(sinks.Mixer.SyncDeviceVolumes());
        Assert.False(sinks.Mixer.MonitorMuted);
        Assert.False(sinks.Adapter.GetSinkMuted("external"));
    }

    [Fact]
    public void AStaleOutputMuteEchoCannotCancelARefusedNativePulse()
    {
        using var sinks = new Sinks();
        sinks.SelectExternalOutput();
        sinks.Dump(0.6, externalMuted: false);
        sinks.DirectMute(false);
        sinks.Mixer.SyncDeviceVolumes();
        sinks.Dump(0.6, externalMuted: true);
        sinks.DirectMute(true);
        sinks.FailMuteWrites(4);
        Assert.False(sinks.Mixer.SyncDeviceVolumes());

        sinks.Dump(0.6, externalMuted: false);
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        sinks.Dump(0.6, externalMuted: true);
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        Assert.False(sinks.Mixer.MonitorMuted);
        Assert.True(sinks.Adapter.GetSinkMuted("external"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AReconnectedOutputRestoresItsPriorMuteWithoutBecomingAKeyPress(bool paused, bool sawRemoval)
    {
        using var sinks = new Sinks();
        sinks.SelectExternalOutput();
        sinks.Dump(0.6, externalMuted: true);
        sinks.DirectMute(true);
        sinks.Mixer.SyncDeviceVolumes();
        sinks.Mixer.SetMonitorMuted(paused);

        if (sawRemoval)
        {
            sinks.Dump(0.6); // The selected device has gone away.
            sinks.Mixer.SyncDeviceVolumes();
        }
        sinks.Dump(0.6, externalMuted: false, externalSerial: 2); // A new node starts unmuted.
        sinks.DirectMute(false);
        sinks.Mixer.SyncDeviceVolumes();

        Assert.Equal(paused, sinks.Mixer.MonitorMuted);
        Assert.True(sinks.Adapter.GetSinkMuted("external"));
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        Assert.Equal(paused, sinks.Mixer.MonitorMuted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANewOutputConfirmsItsBaselineBeforeTheNextNativeMuteKey(bool muted)
    {
        using var sinks = new Sinks();
        sinks.SelectExternalOutput();
        sinks.Dump(0.6, externalMuted: !muted); // Discovery can precede the native mute echo.
        sinks.DirectMute(muted);
        sinks.Mixer.SyncDeviceVolumes();

        sinks.Dump(0.6, externalMuted: !muted);
        sinks.DirectMute(!muted);
        Assert.True(sinks.Mixer.SyncDeviceVolumes());
        Assert.True(sinks.Mixer.MonitorMuted);
        Assert.Equal(muted, sinks.Adapter.GetSinkMuted("external"));
    }

    [Fact]
    public void AnApplicationPulseAlsoConfirmsPhysicalFlagsWhoseRegistryEchoIsLate()
    {
        using var sinks = new Sinks();
        sinks.ApplicationChannel();
        sinks.SelectExternalOutput();
        sinks.DirectMute(true);
        sinks.Dump(0.6, externalMuted: true);
        sinks.Mixer.SyncDeviceVolumes();

        // The internal mute is already visible, but the physical output's
        // registry still shows its baseline. Repairing the internal sink
        // leaves the fake native flag false, as the physical toggle did.
        sinks.Dump(0.6, internalSink: true, externalMuted: true);
        Assert.True(sinks.Mixer.SyncOwnSinkLevels(out _));
        Assert.True(sinks.Mixer.MonitorMuted);
        sinks.Mixer.SyncDeviceVolumes();
        Assert.True(sinks.Adapter.GetSinkMuted("external"));

        sinks.Dump(0.6, externalMuted: false); // The delayed physical echo arrives.
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        Assert.True(sinks.Mixer.MonitorMuted);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ASecondNativeKeyDuringConsumptionIsNotOverwritten(bool application, bool originalMute)
    {
        using var sinks = new Sinks();
        if (application) sinks.ApplicationChannel();
        else
        {
            sinks.SelectExternalOutput();
            sinks.DirectMute(originalMute);
            sinks.Dump(1, externalMuted: originalMute);
            sinks.Mixer.SyncDeviceVolumes();
        }
        sinks.DirectMute(!originalMute);
        sinks.Dump(1, internalSink: application, externalMuted: application ? null : !originalMute);
        sinks.PressDuringNextMuteWrite();
        Sweep();
        Assert.True(sinks.Mixer.MonitorMuted);

        bool flag = sinks.Adapter.GetSinkMuted(application ? "OpenXLR_ch_system" : "external");
        sinks.Dump(1, internalSink: application && flag, externalMuted: application ? null : flag);
        Sweep();
        Assert.False(sinks.Mixer.MonitorMuted);
        Assert.Equal(originalMute, sinks.Adapter.GetSinkMuted(application ? "OpenXLR_ch_system" : "external"));

        void Sweep()
        {
            if (application) sinks.Mixer.SyncOwnSinkLevels(out _);
            sinks.Mixer.SyncDeviceVolumes();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASecondNativeKeyCancelsAnUnacknowledgedPulseInsteadOfLeavingMonitoringPaused(bool application)
    {
        using var sinks = new Sinks();
        if (application) sinks.ApplicationChannel();
        else sinks.SelectExternalOutput();
        sinks.DirectMute(false);
        sinks.Dump(1, externalMuted: application ? null : false);
        sinks.Mixer.SyncDeviceVolumes();
        sinks.DirectMute(true);
        sinks.Dump(1, internalSink: application, externalMuted: application ? null : true);
        sinks.FailMuteWrites(4);
        Sweep();
        sinks.DirectMute(false); // Second key before any consume succeeded.
        sinks.Dump(1, externalMuted: application ? null : false);
        Sweep();
        Assert.False(sinks.Mixer.MonitorMuted);
        Assert.False(sinks.Adapter.GetSinkMuted(application ? "OpenXLR_ch_system" : "external"));

        void Sweep()
        {
            if (application) sinks.Mixer.SyncOwnSinkLevels(out _);
            sinks.Mixer.SyncDeviceVolumes();
        }
    }

    [Fact]
    public void AGraphFailureDoesNotSuppressTheNextPhysicalKeyAsPartOfAnOldApplicationPulse()
    {
        using var sinks = new Sinks();
        sinks.ApplicationChannel();
        sinks.SelectExternalOutput();
        sinks.DirectMute(false);
        sinks.Dump(1, externalMuted: false);
        sinks.Mixer.SyncDeviceVolumes();
        sinks.DirectMute(true);
        sinks.Dump(1, internalSink: true, externalMuted: false);
        Assert.True(sinks.Mixer.SyncOwnSinkLevels(out _));
        sinks.GraphUnavailable(true);
        Assert.Throws<InvalidOperationException>(() => sinks.Mixer.SyncDeviceVolumes());
        sinks.GraphUnavailable(false);

        sinks.DirectMute(true);
        sinks.Dump(1, externalMuted: true);
        Assert.True(sinks.Mixer.SyncDeviceVolumes());
        Assert.False(sinks.Mixer.MonitorMuted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFirstNativeKeyAfterSelectingAnOutputDoesNotWaitForTheFirstSweep(bool originalMute)
    {
        using var sinks = new Sinks();
        sinks.DirectMute(originalMute);
        sinks.Dump(1, externalMuted: originalMute);
        sinks.Mixer.SetMonitorOutputs(["external"]);
        Assert.Equal(["external"], sinks.Mixer.MonitorOutputs);

        sinks.DirectMute(!originalMute);
        sinks.Dump(1, externalMuted: !originalMute);
        sinks.Mixer.SyncDeviceVolumes();

        Assert.True(sinks.Mixer.MonitorMuted);
        Assert.Equal(originalMute, sinks.Adapter.GetSinkMuted("external"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SelectionRemembersAnAddressableSinkBeforeItsFirstRegistryObservation(bool originalMute, bool unavailableGraph)
    {
        using var sinks = new Sinks();
        sinks.DirectMute(originalMute);
        sinks.Dump(1); // Native commands can reach a sink before its Props arrive.
        if (unavailableGraph) sinks.GraphUnavailable(true);
        sinks.Mixer.SetMonitorOutputs(["external"]);
        if (unavailableGraph) sinks.GraphUnavailable(false);
        sinks.Mixer.SyncDeviceVolumes(); // Still no registry identity, so this is not a disconnect.

        sinks.DirectMute(!originalMute);
        sinks.Dump(1, externalMuted: !originalMute);
        sinks.Mixer.SyncDeviceVolumes();

        Assert.True(sinks.Mixer.MonitorMuted);
        Assert.Equal(originalMute, sinks.Adapter.GetSinkMuted("external"));
    }

    [Fact]
    public void ReselectingAnOutputEstablishesItsNewBaselineWithoutAnInterveningSweep()
    {
        using var sinks = new Sinks();
        sinks.DirectMute(false);
        sinks.Dump(1, externalMuted: false);
        sinks.Mixer.SetMonitorOutputs(["external"]);
        sinks.Mixer.SyncDeviceVolumes();
        sinks.Mixer.SetMonitorOutputs([]);
        sinks.DirectMute(true);
        sinks.Dump(1, externalMuted: true);
        sinks.Mixer.SetMonitorOutputs(["external"]);

        sinks.DirectMute(false);
        sinks.Dump(1, externalMuted: false);
        sinks.Mixer.SyncDeviceVolumes();

        Assert.True(sinks.Mixer.MonitorMuted);
        Assert.True(sinks.Adapter.GetSinkMuted("external"));
    }

    [Fact]
    public void RelinkingTheSameSelectionKeepsAnUnconsumedNativeKey()
    {
        using var sinks = new Sinks();
        sinks.DirectMute(false);
        sinks.Dump(1, externalMuted: false);
        sinks.Mixer.SetMonitorOutputs(["external"]);

        sinks.DirectMute(true);
        sinks.Dump(1, externalMuted: true);
        sinks.Mixer.SetMonitorOutputs(["external"]);
        sinks.Mixer.SyncDeviceVolumes();

        Assert.True(sinks.Mixer.MonitorMuted);
        Assert.False(sinks.Adapter.GetSinkMuted("external"));
    }

    [Fact]
    public void AnUnavailableGraphDoesNotRejectSelectionOrPreventLaterMuteDiscovery()
    {
        using var sinks = new Sinks();
        sinks.DirectMute(false);
        sinks.Dump(1, externalMuted: false);
        sinks.GraphUnavailable(true);
        sinks.Mixer.SetMonitorOutputs(["external"]);
        Assert.Equal(["external"], sinks.Mixer.MonitorOutputs);
        sinks.GraphUnavailable(false);
        sinks.Mixer.SyncDeviceVolumes();

        sinks.DirectMute(true);
        sinks.Dump(1, externalMuted: true);
        sinks.Mixer.SyncDeviceVolumes();

        Assert.True(sinks.Mixer.MonitorMuted);
        Assert.False(sinks.Adapter.GetSinkMuted("external"));
    }
}
