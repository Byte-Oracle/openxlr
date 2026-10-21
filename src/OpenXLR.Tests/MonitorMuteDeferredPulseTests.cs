using System.Reflection;
using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class MonitorMuteDeferredPulseTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void APhysicalConsumeDeferredFromABlanketPulseDoesNotBecomeANewKey(bool secondKey, bool identityVerification)
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.SetNative("OpenXLR_ch_system", true);
        fixture.SetNative("external", true);
        fixture.DeferNextPhysicalPulse(identityVerification);
        fixture.Publish(identityVerification ? 2 : 1);

        fixture.Mixer.SyncOwnSinkLevels(out _);
        fixture.Mixer.SyncDeviceVolumes();
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.True(fixture.Native("external"));
        Assert.False(fixture.Native("OpenXLR_ch_system"));

        if (secondKey) fixture.SetNative("external", false);
        fixture.Publish(identityVerification ? 2 : 1);
        fixture.Mixer.SyncOwnSinkLevels(out _);
        fixture.Mixer.SyncDeviceVolumes();

        Assert.Equal(!secondKey, fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("external"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AnApplicationConsumeDeferredFromAnotherApplicationDoesNotBecomeANewKey(bool secondKey, bool readFailure)
    {
        using var fixture = new IndependentSinks();
        fixture.SetNative("OpenXLR_ch_system", true);
        fixture.SetNative("OpenXLR_ch_browser", true);
        fixture.DeferNextPulse("OpenXLR_ch_browser", readFailure);
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("OpenXLR_ch_system"));
        Assert.True(fixture.Native("OpenXLR_ch_browser"));

        if (secondKey) fixture.SetNative("OpenXLR_ch_browser", false);
        fixture.Sweep();
        Assert.Equal(!secondKey, fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("OpenXLR_ch_browser"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnApplicationConsumeDeferredWhileAPhysicalSinkCountsDoesNotBecomeANewKey(bool secondKey)
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.SetNative("OpenXLR_ch_system", true);
        fixture.SetNative("external", true);
        fixture.DeferNextPulse("OpenXLR_ch_system");
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.True(fixture.Native("OpenXLR_ch_system"));
        Assert.False(fixture.Native("external"));

        if (secondKey) fixture.SetNative("OpenXLR_ch_system", false);
        fixture.Sweep();
        Assert.Equal(!secondKey, fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("OpenXLR_ch_system"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredPhysicalAcknowledgementsKeepANewerExplicitMonitorState(bool muted)
    {
        using var fixture = new IndependentSinks();
        fixture.DeferBlanketPhysicalPulse();
        fixture.Mixer.SetMonitorMuted(muted);
        fixture.Sweep();
        Assert.Equal(muted, fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("external"));

        fixture.SetNative("external", true);
        fixture.Sweep();
        Assert.Equal(!muted, fixture.Mixer.MonitorMuted);
    }

    [Fact]
    public void DeferredPhysicalAcknowledgementsKeepANewerAtomicApiToggle()
    {
        using var fixture = new IndependentSinks();
        fixture.DeferBlanketPhysicalPulse();
        fixture.Mixer.ToggleMonitorMuted();
        fixture.Sweep();
        Assert.False(fixture.Mixer.MonitorMuted);
        fixture.SetNative("external", true);
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredPhysicalAcknowledgementsDoNotCarryAcrossSelectionOrReplacement(bool replacement)
    {
        using var fixture = new IndependentSinks();
        fixture.DeferBlanketPhysicalPulse();
        if (replacement)
        {
            fixture.ReplaceExternal();
            fixture.Sweep(2);
            Assert.True(fixture.Mixer.MonitorMuted);
            Assert.False(fixture.Native("external"));
            fixture.SetNative("external", true);
            fixture.Sweep(2);
        }
        else
        {
            fixture.Mixer.SetMonitorOutputs([]);
            fixture.Mixer.SetMonitorOutputs(["external"]);
            fixture.SetNative("external", false);
            fixture.Sweep();
        }
        Assert.False(fixture.Mixer.MonitorMuted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultipleDeferredPhysicalOutputsKeepOneBlanketAction(bool secondKey)
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external", "external2"]);
        fixture.SetNative("OpenXLR_ch_system", true);
        foreach (string name in new[] { "external", "external2" })
        {
            fixture.SetNative(name, true);
            fixture.DeferNextPulse(name);
        }
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        if (secondKey)
            foreach (string name in new[] { "external", "external2" }) fixture.SetNative(name, false);
        fixture.Sweep();
        Assert.Equal(!secondKey, fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("external"));
        Assert.False(fixture.Native("external2"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AFailedIdentityReadConfirmsNativeParityDespiteALateRegistryMute(bool registryMuted, bool nativeMuted)
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.SetNative("OpenXLR_ch_system", true);
        fixture.SetNative("external", nativeMuted);
        fixture.DeferNextPhysicalPulse(identityVerification: true);
        fixture.Sweep(2, registryMuted);
        Assert.True(fixture.Mixer.MonitorMuted);

        fixture.Sweep(2);
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("external"));
    }

    [Fact]
    public void AFailedApplicationReadDoesNotCreditAnAlreadyConsumedStaleRegistryFlag()
    {
        using var fixture = new IndependentSinks();
        fixture.SetNative("OpenXLR_ch_browser", true);
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        Assert.False(fixture.Native("OpenXLR_ch_browser"));

        fixture.SetNative("OpenXLR_ch_system", true);
        fixture.DeferNextPulse("OpenXLR_ch_browser", readFailure: true);
        fixture.Sweep(browserRegistryMuted: true);
        Assert.False(fixture.Mixer.MonitorMuted);
        fixture.Sweep();
        Assert.False(fixture.Mixer.MonitorMuted);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void APhysicalBlanketPulseKeepsMixedOriginalMixMutesWhenEveryApplicationIsDeferred(bool firstMuted, bool readFailure)
    {
        using var fixture = new IndependentSinks();
        fixture.Mixer.SetMonitorOutputs(["external"]);
        fixture.Mixer.SetMixMuted("monitor", firstMuted);
        fixture.Mixer.SetMixMuted("monitor_b", !firstMuted);
        foreach (string name in new[] { "OpenXLR_ch_system", "OpenXLR_ch_browser" })
        {
            fixture.SetNative(name, true);
            fixture.DeferNextPulse(name, readFailure);
        }
        fixture.SetNative("external", true);
        fixture.SetNative("OpenXLR_mix_monitor", true);
        fixture.SetNative("OpenXLR_mix_monitor_b", true);
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        CheckMixes();
        fixture.Sweep();
        Assert.True(fixture.Mixer.MonitorMuted);
        CheckMixes();
        fixture.Mixer.ToggleMonitorMuted();
        fixture.Sweep();
        Assert.False(fixture.Mixer.MonitorMuted);
        CheckMixes();

        void CheckMixes()
        {
            Assert.Equal([firstMuted ? "monitor" : "monitor_b"], fixture.Mixer.ExportSettings().MixMuted);
            Assert.Equal(firstMuted, fixture.Native("OpenXLR_mix_monitor"));
            Assert.Equal(!firstMuted, fixture.Native("OpenXLR_mix_monitor_b"));
        }
    }

    private sealed class IndependentSinks : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-deferred-pulse-").FullName;
        private readonly string? _previousPath = Environment.GetEnvironmentVariable("PATH");
        private readonly PipeWireAdapter _adapter = new();
        public Mixer Mixer { get; }

        public IndependentSinks()
        {
            foreach (string name in Names) SetNative(name, false);
            File.WriteAllText(Path.Combine(_directory, "native-serial"), "1");
            ExecutableScript.Write(Path.Combine(_directory, "pw-dump"), """
                dir=${0%/*}
                /bin/cat "$dir/graph"
                """);
            ExecutableScript.Write(Path.Combine(_directory, "pw-link"), "exit 0\n");
            ExecutableScript.Write(Path.Combine(_directory, "pactl"), """
                dir=${0%/*}
                if [ "$1" = --format=json ]; then
                    if [ -f "$dir/fail-native" ]; then /bin/rm "$dir/fail-native"; exit 1; fi
                    mute=$(/bin/cat "$dir/external")
                    mute2=$(/bin/cat "$dir/external2")
                    serial=$(/bin/cat "$dir/native-serial")
                    system=$(/bin/cat "$dir/OpenXLR_ch_system")
                    browser=$(/bin/cat "$dir/OpenXLR_ch_browser")
                    printf '[{"index":1,"name":"external","mute":%s,"properties":{"object.serial":"%s"}},{"index":2,"name":"external2","mute":%s,"properties":{"object.serial":"2"}},{"index":3,"name":"OpenXLR_ch_system","mute":%s,"properties":{"object.serial":"3"}},{"index":4,"name":"OpenXLR_ch_browser","mute":%s,"properties":{"object.serial":"4"}}]' "$mute" "$serial" "$mute2" "$system" "$browser"
                    exit
                fi
                case "$1" in
                  get-sink-volume) printf 'Volume: front-left: 65536 / 100%% / 0.00 dB\n' ;;
                  get-sink-mute)
                    if [ -f "$dir/fail-read-$2" ]; then /bin/rm "$dir/fail-read-$2"; exit 1; fi
                    mute=$(/bin/cat "$dir/$2")
                    if [ "$mute" = true ]; then echo 'Mute: yes'; else echo 'Mute: no'; fi ;;
                  set-sink-mute)
                    if [ -f "$dir/fail-$2" ]; then
                        /bin/rm "$dir/fail-$2"
                        echo 'temporary native consume failure' >&2
                        exit 1
                    fi
                    case "$3" in
                      0) printf false > "$dir/$2" ;;
                      1) printf true > "$dir/$2" ;;
                      toggle)
                        mute=$(/bin/cat "$dir/$2")
                        if [ "$mute" = true ]; then printf false > "$dir/$2"; else printf true > "$dir/$2"; fi ;;
                    esac ;;
                esac
                """);
            Environment.SetEnvironmentVariable("PATH", _directory);
            Mixer = new(_adapter);
            typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer, true);
            typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer,
                new MixerConfig { Channels = [new("system", "System"), new("browser", "Browser")], Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("monitor_b", "Monitor B", MixKind.Monitor)] });
            Publish();
        }

        public void SetNative(string name, bool muted) => File.WriteAllText(Path.Combine(_directory, name), muted ? "true" : "false");
        public bool Native(string name) => File.ReadAllText(Path.Combine(_directory, name)) == "true";
        private static readonly string[] Names = ["external", "external2", "OpenXLR_ch_system", "OpenXLR_ch_browser", "OpenXLR_mix_monitor", "OpenXLR_mix_monitor_b"];
        public void DeferNextPhysicalPulse(bool identityVerification) => File.WriteAllText(Path.Combine(_directory, identityVerification ? "fail-native" : "fail-external"), "");
        public void DeferNextPulse(string name, bool readFailure = false) => File.WriteAllText(Path.Combine(_directory, $"fail-{(readFailure ? "read-" : "")}{name}"), "");
        public void ReplaceExternal() => File.WriteAllText(Path.Combine(_directory, "native-serial"), "2");
        public void Sweep(int externalRegistrySerial = 1, bool? externalRegistryMuted = null, bool? browserRegistryMuted = null)
        {
            Publish(externalRegistrySerial, externalRegistryMuted, browserRegistryMuted);
            Mixer.SyncOwnSinkLevels(out _);
            Mixer.SyncDeviceVolumes();
        }
        public void DeferBlanketPhysicalPulse()
        {
            Mixer.SetMonitorOutputs(["external"]);
            SetNative("OpenXLR_ch_system", true);
            SetNative("external", true);
            DeferNextPulse("external");
            Sweep();
            Assert.True(Mixer.MonitorMuted);
            Assert.True(Native("external"));
        }

        public void Publish(int externalRegistrySerial = 1, bool? externalRegistryMuted = null, bool? browserRegistryMuted = null)
        {
            File.WriteAllBytes(Path.Combine(_directory, "graph"), JsonSerializer.SerializeToUtf8Bytes(Names.Select((name, index) => new
            {
                id = index + 1, type = "PipeWire:Interface:Node", info = new
                {
                    props = new Dictionary<string, string> { ["node.name"] = name, ["media.class"] = "Audio/Sink", ["object.serial"] = (name == "external" ? externalRegistrySerial : index + 1).ToString() },
                    @params = new { Props = new[] { new { channelVolumes = new[] { 1.0 }, mute = name == "external" ? externalRegistryMuted ?? Native(name)
                        : name == "OpenXLR_ch_browser" ? browserRegistryMuted ?? Native(name) : Native(name) } } },
                },
            })));
            typeof(PipeWireAdapter).GetMethod("InvalidateDump", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_adapter, null);
        }

        public void Dispose()
        {
            try { Mixer.Dispose(); }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", _previousPath);
                Directory.Delete(_directory, true);
            }
        }
    }
}
