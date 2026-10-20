using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>Which capture node feeds the hardware input channels, and mono capture ports, on a private PipeWire server.</summary>
public sealed partial class MonitorVolumeIntegrationTests
{
    private const string Xlr1 = "OpenXLR_ch_xlr1";
    private static MixerConfig HardwareInputConfig => new()
    {
        Channels = [new("xlr1", "Microphone") { InputPair = 0 }],
        Mixes = [new("monitor", "Monitor", MixKind.Monitor)],
    };

    private static uint WaveSource(PipeWireAdapter pw, string parent, string name)
    {
        if (!pw.ListDevices().Any(d => d.Name == parent)) pw.CreateNullSink(parent, "Fixture");
        uint module = pw.CreateVirtualMic(name, parent + ".monitor", name);
        WaitForRecovery(() => pw.ListDevices().Any(d => d.Name == name));
        return module;
    }

    [MonitorPipeWireFact]
    public void WithNoInterfaceDrivenALoneWaveXlrSourceStillFeedsTheInputs()
    {
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph(); using var mixer = new Mixer(pw);
        WaveSource(pw, "test_lone_parent", "test_Wave_XLR_lone");
        // No driven device, at most one interface attached: the 0.1.47 fallback.
        mixer.SetInputDeviceHint([], hardwareOutputRouting: false, modelFallback: true);
        mixer.Build(HardwareInputConfig);
        WaitForRecovery(() => { mixer.EnsureInputFeeds(); return IncomingRouteLinks(Xlr1).Length == 2; });
        Assert.Null(mixer.InputWarning);
        // A second interface attached and none driven: the feed goes, with a reason.
        mixer.SetInputDeviceHint([], hardwareOutputRouting: false, modelFallback: false);
        WaitForRecovery(() => IncomingRouteLinks(Xlr1).Length == 0);
        Assert.NotNull(mixer.InputWarning);
        mixer.EnsureInputFeeds();
        Assert.Empty(IncomingRouteLinks(Xlr1));
    }

    [MonitorPipeWireFact]
    public void TwoWaveSourcesAndNoDrivenUnitFeedNothing()
    {
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph(); using var mixer = new Mixer(pw);
        WaveSource(pw, "test_pair_parent", "test_Wave_XLR_unitA");
        WaveSource(pw, "test_pair_parent", "test_Wave_XLR_unitB");
        mixer.SetInputDeviceHint(null);
        mixer.Build(HardwareInputConfig);
        mixer.EnsureInputFeeds();
        Assert.Empty(IncomingRouteLinks(Xlr1)); Assert.NotNull(mixer.InputWarning);
        // The driven unit's own fragment picks its source.
        mixer.SetInputDeviceHint(["test_Wave_XLR_unitA", "Wave_XLR"], hardwareOutputRouting: false, modelFallback: false);
        WaitForRecovery(() => IncomingRouteLinks(Xlr1).Length == 2);
        Assert.Null(mixer.InputWarning);
        Assert.All(GraphLinksInto(Xlr1), link => Assert.StartsWith("test_Wave_XLR_unitA:", link.From));
    }

    [MonitorPipeWireFact]
    public void ASerialSpelledDifferentlyFindsTheLoneSourceByModelAndSaysSo()
    {
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        var notes = new List<string>();
        using var mixer = new Mixer(pw) { InputNote = notes.Add };
        WaveSource(pw, "test_spelling_parent", "test_Wave_XLR_unit_A-00");
        mixer.SetInputDeviceHint(["Wave_XLR_unitA-", "Wave_XLR"], hardwareOutputRouting: false, modelFallback: true);
        mixer.Build(HardwareInputConfig);
        WaitForRecovery(() => { mixer.EnsureInputFeeds(); return IncomingRouteLinks(Xlr1).Length == 2; });
        Assert.Contains(notes, n => n.Contains("Wave_XLR_unitA-", StringComparison.Ordinal) && n.Contains("test_Wave_XLR_unit_A-00", StringComparison.Ordinal));
        int noted = notes.Count;
        mixer.EnsureInputFeeds();
        Assert.Equal(noted, notes.Count);   // said once, not on every sweep
    }

    [MonitorPipeWireFact]
    public void ASerialSpelledDifferentlyWithTwoUnitsOfTheModelFeedsNothing()
    {
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph(); using var mixer = new Mixer(pw);
        WaveSource(pw, "test_spelling_pair_parent", "test_Wave_XLR_unit_A-00");
        WaveSource(pw, "test_spelling_pair_parent", "test_Wave_XLR_unit_B-00");
        mixer.SetInputDeviceHint(["Wave_XLR_unitA-", "Wave_XLR"], hardwareOutputRouting: false, modelFallback: false);
        mixer.Build(HardwareInputConfig);
        mixer.EnsureInputFeeds();
        Assert.Empty(IncomingRouteLinks(Xlr1)); Assert.NotNull(mixer.InputWarning);
    }

    [MonitorPipeWireFact]
    public void ALongerSerialNeverSubstitutesForTheDrivenUnit()
    {
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph(); using var mixer = new Mixer(pw);
        uint selected = WaveSource(pw, "test_serial_parent", "test_Wave_XLR_unitA-00");
        WaveSource(pw, "test_serial_parent", "test_Wave_XLR_unitAB-00");
        // Two units of the model attached: the daemon passes the serial fragment alone.
        mixer.SetInputDeviceHint(["Wave_XLR_unitA-"], hardwareOutputRouting: false, modelFallback: false);
        mixer.Build(HardwareInputConfig);
        WaitForRecovery(() => IncomingRouteLinks(Xlr1).Length == 2);
        Assert.All(GraphLinksInto(Xlr1), link => Assert.StartsWith("test_Wave_XLR_unitA-00:", link.From));
        // The driven unit's source leaves; the other unit's microphone does not take its place.
        pw.UnloadModule(selected);
        WaitForRecovery(() => { mixer.EnsureInputFeeds(); return IncomingRouteLinks(Xlr1).Length == 0; });
    }

    /// <summary>
    /// A primary switch from a Pro to a one-input unit without Aux In and
    /// back. XLR 1 moves to the other unit's microphone, so its monitor sends
    /// come up muted by the console rule. Aux In and XLR 2 have no pair on the
    /// one-input unit: nothing new is patched into them either way, so their
    /// monitor sends keep what the user set. A capture input on either unit's
    /// node keeps its own source through both switches, so its sends stay
    /// as they were too.
    /// </summary>
    [MonitorPipeWireFact]
    public void APrimarySwitchToAUnitWithoutAuxInLeavesAuxInsMonitorSendAlone()
    {
        const string pro = "alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_Pro_SWITCHPRO-00.pro-input-0";
        const string dock = "alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_Dock_MK.2_SWITCHDOCK-00.analog-stereo";
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph(); using var mixer = new Mixer(pw);
        List<uint> modules = [];
        try
        {
            modules.Add(LoadModule("module-null-sink", "sink_name=switch_pro_parent", "channels=6", "sink_properties=device.description=Switch-pro-parent"));
            modules.Add(LoadModule("module-null-sink", "sink_name=switch_dock_parent", "channels=2", "sink_properties=device.description=Switch-dock-parent"));
            WaitForRecovery(() => ProcessRunner.Run("pactl", ["list", "sources", "short"]).StdoutText is var listed
                && listed.Contains("switch_pro_parent.monitor", StringComparison.Ordinal) && listed.Contains("switch_dock_parent.monitor", StringComparison.Ordinal));
            modules.Add(LoadModule("module-remap-source", $"source_name={pro}", "master=switch_pro_parent.monitor", "channels=6", "source_properties=device.description=Switch-pro"));
            modules.Add(LoadModule("module-remap-source", $"source_name={dock}", "master=switch_dock_parent.monitor", "channels=2", "source_properties=device.description=Switch-dock"));
            WaitForRecovery(() => pw.ListDevices().Count(d => d.Name is pro or dock) == 2);

            mixer.SetInputJacks(2, true);
            mixer.SetInputDeviceHint(["Wave_XLR_Pro_SWITCHPRO-"], hardwareOutputRouting: false, modelFallback: false);
            mixer.Build(MixerConfig.Default());
            mixer.CreateCaptureChannel("Pro input 2", pro, 0, _ => null, monoChannel: 2);
            mixer.CreateCaptureChannel("Dock input", dock, 0, _ => null, monoChannel: 0);
            string[] captures = [.. mixer.Snapshot().Channels.Where(c => c.CaptureSource is pro or dock).Select(c => c.Id)];
            Assert.Equal(2, captures.Length);
            string[] all = ["xlr1", "xlr2", "aux", .. captures];
            WaitForRecovery(() => { mixer.EnsureInputFeeds(); return all.All(c => IncomingRouteLinks($"OpenXLR_ch_{c}").Length == 2); });
            foreach (string channel in all) mixer.SetChannelMuted(channel, "monitor", false);
            bool Muted(string channel) => mixer.IsChannelMutedIn(channel, "monitor");

            // The daemon hands the mixer the dock's jacks first, then its node.
            mixer.SetInputJacks(1, false);
            mixer.SetInputDeviceHint(["Wave_XLR_Dock_MK.2_SWITCHDOCK-"], hardwareOutputRouting: false, modelFallback: false);
            WaitForRecovery(() => { mixer.EnsureInputFeeds(); return GraphLinksInto("OpenXLR_ch_xlr1").Any(l => l.From.StartsWith(dock + ":", StringComparison.Ordinal)); });
            Assert.True(Muted("xlr1"));
            Assert.False(Muted("aux"));
            Assert.False(Muted("xlr2"));
            Assert.Empty(IncomingRouteLinks("OpenXLR_ch_aux"));
            Assert.All(captures, c => Assert.False(Muted(c)));

            // And back to the Pro: Aux In plays again, its send as the user left it.
            mixer.SetChannelMuted("xlr1", "monitor", false);
            mixer.SetInputJacks(2, true);
            mixer.SetInputDeviceHint(["Wave_XLR_Pro_SWITCHPRO-"], hardwareOutputRouting: false, modelFallback: false);
            WaitForRecovery(() => { mixer.EnsureInputFeeds(); return IncomingRouteLinks("OpenXLR_ch_aux").Length == 2; });
            Assert.True(Muted("xlr1"));
            Assert.False(Muted("aux"));
            Assert.False(Muted("xlr2"));
            Assert.All(captures, c => Assert.False(Muted(c)));
            Assert.All(captures, c => Assert.Equal(2, IncomingRouteLinks($"OpenXLR_ch_{c}").Length));
            Assert.DoesNotContain("aux|monitor", mixer.ExportSettings().ChannelMuted);
        }
        finally
        {
            mixer.TearDown();
            foreach (uint module in Enumerable.Reverse(modules)) UnloadModule(module);
        }
    }

    [MonitorPipeWireFact]
    public void SelectedMonoPortsFeedBothSidesAndNeverFallBackToAnotherPort()
    {
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("test_wave_capture", "Wave fixture");
        pw.CreateVirtualMic("test_wave_source", "test_wave_capture.monitor", "Wave source");
        WaitForRecovery(() => pw.ListDevices().Any(d => d.Name == "test_wave_source"));
        mixer.Build(new MixerConfig { Channels = [new("system", "System")], Mixes = [new("monitor", "Monitor", MixKind.Monitor)] });
        mixer.CreateCaptureChannel("Left", "test_wave_source", 0, _ => null, monoChannel: 0);
        mixer.CreateCaptureChannel("Right", "test_wave_source", 0, _ => null, monoChannel: 1);
        mixer.CreateCaptureChannel("Missing", "test_wave_source", 0, _ => null, monoChannel: 63);
        WaitForRecovery(() => { mixer.EnsureInputFeeds(); return mixer.Snapshot().Channels.Count(c => c.CaptureConnected) == 2; });
        var left = GraphLinksInto("OpenXLR_ch_left");
        var right = GraphLinksInto("OpenXLR_ch_right");
        Assert.Equal(2, left.Length); Assert.Equal(2, right.Length);
        Assert.Single(left.Select(l => l.From).Distinct()); Assert.Single(right.Select(l => l.From).Distinct());
        Assert.NotEqual(left[0].From, right[0].From);
        Assert.Empty(IncomingRouteLinks("OpenXLR_ch_missing"));
        MixerConfig restored = MixerConfig.FromSettings(mixer.ExportSettings());
        Assert.Equal(1, restored.Channels.Single(c => c.Id == "right").CaptureMonoChannel);
        Assert.Equal(1, mixer.Snapshot().Channels.Single(c => c.Id == "right").CaptureMonoChannel);
        Assert.All(mixer.Snapshot().Channels.Where(c => c.CaptureSource is not null), c => Assert.Contains("monitor", c.MutedIn));
    }

    [MonitorPipeWireFact]
    public async Task DisablingAnAdditionalUnitRemovesTheChannelsMadeFromItsSource()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-wave-disable-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        try
        {
            new DaemonSettings { Submixer = true }.Save();
            new MixerSettings { UserChannels = [new("system", "System")], UserMixes = [] }.Save();
            WaveSource(pw, "test_disable_parent", "test_XLR_Dock_dockA-00");
            WaveSource(pw, "test_disable_parent", "test_headset_source");
            var primary = new Unit("Wave XLR", 0x007d, "unitP");
            var dock = new Unit("XLR Dock", 0x00a6, "dockA");
            IReadOnlyList<IAudioDevice> attached = [primary, dock];
            var config = new ConfigurationBuilder().Build();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => [primary]);
            devices.SweepOnce();
            using var lifetime = new TestLifetime();
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            using var interfaces = new WaveInterfaces(devices, NullLogger<DeviceManager>.Instance, config, () => attached);
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime, interfaces);
            await mixer.StartAsync(CancellationToken.None);
            await interfaces.StartAsync(CancellationToken.None);
            try
            {
                WaitForRecovery(() => interfaces.Snapshot().Count == 2);
                async Task Ok(string command) => Assert.True((await hub.ExecuteForApiAsync(command)).Ok, command);
                IReadOnlyList<string> Captures() => [.. mixer.Snapshot()!.Channels.Where(c => c.CaptureSource is not null).Select(c => c.CaptureSource!)];
                string id = dock.Info.InstanceId;

                await Ok($$"""{"cmd":"setWaveInterfaceEnabled","device":"{{id}}","value":true}""");
                WaitForRecovery(() => interfaces.Snapshot().Any(u => u.Id == id && u.Connected));
                await Ok("""{"cmd":"createCaptureChannel","name":"Dock mic","source":"test_XLR_Dock_dockA-00","capturePair":0,"captureMonoChannel":0}""");
                await Ok("""{"cmd":"createCaptureChannel","name":"Headset","source":"test_headset_source","capturePair":0}""");
                Assert.Equal(2, Captures().Count);

                // Unplugged but still enabled: the channel stays, silent.
                attached = [primary];
                WaitForRecovery(() => interfaces.Snapshot().Any(u => u.Id == id && !u.Connected));
                Assert.Equal(2, Captures().Count);
                attached = [primary, dock];
                WaitForRecovery(() => interfaces.Snapshot().Any(u => u.Id == id && u.Connected));

                // Disabled: its channel goes, and the headset channel stays.
                string dockChannel = mixer.Snapshot()!.Channels.Single(c => c.CaptureSource == "test_XLR_Dock_dockA-00").Id;
                await Ok($$"""{"cmd":"setWaveInterfaceEnabled","device":"{{id}}","value":false}""");
                Assert.Equal(["test_headset_source"], Captures());
                Assert.DoesNotContain(MixerSettings.Load(MixerSettings.DefaultPath, out _)!.UserChannels!, c => c.CaptureSource == "test_XLR_Dock_dockA-00");
                Assert.Empty(IncomingRouteLinks("OpenXLR_ch_" + dockChannel));
            }
            finally
            {
                await interfaces.StopAsync(CancellationToken.None);
                await mixer.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            pw.TearDown();
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A strip bound to a second unit's capture node while the first unit is
    /// the primary: the binding records the unit, the strip carries that
    /// unit's controls once it is driven as an additional interface, and it
    /// follows the unit's node when a card profile switch renames it, there
    /// and back, with its audio. A source that is not a supported unit's is
    /// never followed to another name.
    /// </summary>
    [MonitorPipeWireFact]
    public async Task AStripBoundToASecondUnitFollowsItsRenamedNodeAndCarriesItsControlsWhileDriven()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-driven-source-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        const string card = "usb-Elgato_Systems_Elgato_Wave_3_w3A-00";
        const string mono = "alsa_input." + card + ".mono-fallback", stereo = "alsa_input." + card + ".analog-stereo";
        const string cam = "alsa_input.usb-046d_Test_Cam_CAM1-02.analog-stereo", camRenamed = "alsa_input.usb-046d_Test_Cam_CAM1-02.mono-fallback";
        using var stop = new CancellationTokenSource();
        Task<ProcessResult>? generator = null;
        try
        {
            new DaemonSettings { Submixer = true }.Save();
            pw.CreateNullSink("primary_signal", "Wave:3 capsule");
            uint node = pw.CreateVirtualMic(mono, "primary_signal.monitor", "Elgato Wave:3 Mono");
            uint camNode = pw.CreateVirtualMic(cam, "primary_signal.monitor", "Test webcam");
            WaitForRecovery(() => pw.ListDevices().Any(d => d.Name == mono) && pw.ListDevices().Any(d => d.Name == cam));
            // The primary first in the table's order, the Wave:3 attached beside it.
            var primary = new Unit("Wave XLR", 0x007d, "unitP");
            var wave3 = new Unit("Wave:3", 0x0070, "w3A", xlrJacks: false);
            IReadOnlyList<IAudioDevice> attached = [new Unit("Wave XLR", 0x007d, "unitP"), new Unit("Wave:3", 0x0070, "w3A", xlrJacks: false)];
            var config = new ConfigurationBuilder().Build();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => [primary, wave3]);
            devices.SweepOnce();
            Assert.True(primary.Connected);
            Assert.False(wave3.Connected);
            using var lifetime = new TestLifetime();
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            using var interfaces = new WaveInterfaces(devices, NullLogger<DeviceManager>.Instance, config, () => attached);
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime, interfaces);
            await mixer.StartAsync(CancellationToken.None);
            await interfaces.StartAsync(CancellationToken.None);
            generator = PrimarySignal(stop.Token);
            try
            {
                WaitForRecovery(() => interfaces.Snapshot().Count == 2 && mixer.Built);
                string id = wave3.Info.InstanceId;
                async Task Ok(string command) => Assert.True((await hub.ExecuteForApiAsync(command)).Ok, command);
                ChannelStatus Strip(string strip) => mixer.Snapshot()!.Channels.Single(c => c.Id == strip);
                void FedFrom(string strip, string source) => WaitForRecovery(() =>
                {
                    mixer.SweepOnce();
                    (string From, string To)[] links = GraphLinksInto("OpenXLR_ch_" + strip);
                    return links.Length == 2 && links.All(l => l.From.StartsWith(source + ":", StringComparison.Ordinal)) && Strip(strip).BindingConnected;
                });

                // The Wave:3's node is a source like any other, and the binding
                // remembers whose it is; the unit is not driven yet.
                await Ok($$"""{"cmd":"setInputBinding","channel":"xlr2","source":"{{mono}}","captureMonoChannel":0}""");
                Assert.Equal(new ChannelBinding("source", mono, 0, 0, id), Strip("xlr2").Binding);
                Assert.Equal(id, MixerSettings.Load()!.InputBindings!["xlr2"].Unit);
                Assert.Equal("Mic 2", Strip("xlr2").Name);   // a capsule, not a jack
                FedFrom("xlr2", mono);
                StripSound("xlr2", .1);
                Assert.Null(Strip("xlr2").UnitInput);
                Assert.Equal(new UnitInputStatus(primary.Info.InstanceId, 1, Primary: true), Strip("xlr1").UnitInput);

                // Driven as an additional interface: its input is on the strip.
                await Ok($$"""{"cmd":"setWaveInterfaceEnabled","device":"{{id}}","value":true}""");
                WaitForRecovery(() => Strip("xlr2").UnitInput == new UnitInputStatus(id, 1, Primary: false));
                Assert.Equal(new UnitInputStatus(primary.Info.InstanceId, 1, Primary: true), Strip("xlr1").UnitInput);
                Assert.Contains(hub.Snapshot().WaveInterfaces!, u => u.Id == id && u.Connected && !u.Active);

                // A card profile switch renames the node: the strip follows the
                // card, keeps its binding as saved, and stays the unit's.
                pw.UnloadModule(node);
                WaitForRecovery(() => { mixer.SweepOnce(); return !Strip("xlr2").BindingConnected; });
                node = pw.CreateVirtualMic(stereo, "primary_signal.monitor", "Elgato Wave:3 Analog Stereo");
                FedFrom("xlr2", stereo);
                StripSound("xlr2", .1);
                Assert.Equal(mono, Strip("xlr2").Binding.Source);
                Assert.Null(Strip("xlr2").BindingWarning);
                Assert.Equal(new UnitInputStatus(id, 1, Primary: false), Strip("xlr2").UnitInput);
                Assert.Equal(mono, MixerSettings.Load()!.InputBindings!["xlr2"].Source);
                // And back: the exact name wins again.
                pw.UnloadModule(node);
                node = pw.CreateVirtualMic(mono, "primary_signal.monitor", "Elgato Wave:3 Mono");
                FedFrom("xlr2", mono);
                StripSound("xlr2", .1);

                // A webcam is followed by its exact name only.
                await Ok($$"""{"cmd":"setInputBinding","channel":"xlr1","source":"{{cam}}","captureMonoChannel":0}""");
                Assert.Null(Strip("xlr1").Binding.Unit);
                FedFrom("xlr1", cam);
                pw.UnloadModule(camNode);
                camNode = pw.CreateVirtualMic(camRenamed, "primary_signal.monitor", "Test webcam mono");
                WaitForRecovery(() => pw.ListDevices().Any(d => d.Name == camRenamed));
                // Three whole passes that see the renamed node. A call made
                // while the daemon's own timer pass runs does nothing, and that
                // pass may have read the devices before the camera went.
                for (int ran = 0; ran < 3;) if (mixer.SweepOnce()) ran++;
                Assert.False(Strip("xlr1").BindingConnected);
                Assert.Empty(IncomingRouteLinks("OpenXLR_ch_xlr1"));
                Assert.Contains("not connected", Strip("xlr1").BindingWarning);
                FedFrom("xlr2", mono);   // the other strip never flinched

                // No longer driven: the binding stays, the controls go.
                await Ok($$"""{"cmd":"setWaveInterfaceEnabled","device":"{{id}}","value":false}""");
                WaitForRecovery(() => Strip("xlr2").UnitInput is null);
                Assert.Equal(mono, Strip("xlr2").Binding.Source);
                FedFrom("xlr2", mono);
            }
            finally
            {
                stop.Cancel();
                await interfaces.StopAsync(CancellationToken.None);
                await mixer.StopAsync(CancellationToken.None);
                devices.SuspendSession();
            }
        }
        finally
        {
            stop.Cancel();
            if (generator is not null) await generator;
            pw.TearDown();
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class Unit(string model, ushort product, string serial, bool xlrJacks = true) : IAudioDevice
    {
        public DeviceInfo Info { get; } = new("Elgato", model, 0x0fd9, product) { Location = new(1, (byte)(product & 0x7f), "1-" + serial, serial) };
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, Mute = true, RetainsSettings = true, XlrJacks = xlrJacks };
        public bool Connected { get; private set; }
        public DeviceState ReadState() => new() { GainDb = 30 };
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public void Dispose() => Connected = false;
        public void SetGainDb(int db) { } public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { }
        public void SetVoiceTuneStrength(int value) { } public void SetHpVolumeDb(double db) { }
        public void SetLowImpedance(bool on) { } public void SetCrossfade(int value) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { }
        public void SetCompressor(bool on) { }
    }

    private sealed class TestLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stop.Token;
        public CancellationToken ApplicationStopped => _stop.Token;
        public void StopApplication() => _stop.Cancel();
        public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
    }

    private static (string From, string To)[] GraphLinksInto(string node)
    {
        using var graph = PipeWireSnapshot.Parse(ProcessRunner.Run("pw-dump", []).Stdout);
        return [.. PipeWireAdapter.ParseGraphLinks(graph.RootElement.EnumerateArray())
            .Where(link => link.To.StartsWith(node + ":", StringComparison.Ordinal))];
    }
}
