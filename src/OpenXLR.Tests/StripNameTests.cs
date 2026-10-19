using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// What a microphone strip is called and which icon it wears before the user
/// picks one. XLR 1 and XLR 2 keep their names only while a real XLR jack
/// feeds them; a strip bound to another source, or on a Wave:3's or
/// Wave:1's built-in capsule, is Mic 1 or Mic 2. The ids never change.
/// </summary>
[Collection("xdg-config")]
public sealed class StripNameTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-strip-names-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public StripNameTests() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_directory, true);
    }

    private static (string Name, string Icon) Strip(Mixer mixer, string id)
    {
        ChannelStatus channel = mixer.Snapshot().Channels.Single(c => c.Id == id);
        return (channel.Name, channel.Appearance.DefaultIcon);
    }

    [Fact]
    public void TheNameAndIconFollowTheBindingAndTheIdsStay()
    {
        using var mixer = new Mixer();
        Assert.Equal(("XLR 1", "mic"), Strip(mixer, "xlr1"));
        Assert.Equal(("XLR 2", "mic"), Strip(mixer, "xlr2"));

        mixer.SetInputBinding("xlr1", new("alsa_input.usb-046d_Logi_4K_Stream_Edition-03.analog-stereo", 0, 0));
        mixer.SetInputBinding("xlr2", new("bluez_input.12_34_56_78_9A_BC.1", 0, 0));
        Assert.Equal(("Mic 1", "microphone"), Strip(mixer, "xlr1"));
        Assert.Equal(("Mic 2", "headset"), Strip(mixer, "xlr2"));
        Assert.Equal(("Aux In", "jack"), Strip(mixer, "aux"));
        Assert.Equal(["xlr1", "xlr2", "aux"], mixer.Snapshot().Channels.Where(c => c.Hardware).Select(c => c.Id));

        mixer.SetInputBinding("xlr1", null);
        Assert.Equal(("XLR 1", "mic"), Strip(mixer, "xlr1"));
        Assert.Equal(("Mic 2", "headset"), Strip(mixer, "xlr2"));
        mixer.SetInputBinding("xlr2", null);
        Assert.Equal(("XLR 2", "mic"), Strip(mixer, "xlr2"));

        // A capsule rather than a jack: Mic 1 on the unit itself, kept while no unit is driven.
        Assert.True(mixer.SetUnitXlrJacks(false));
        Assert.Equal(("Mic 1", "microphone"), Strip(mixer, "xlr1"));
        Assert.False(mixer.SetUnitXlrJacks(null));
        Assert.Equal(("Mic 1", "microphone"), Strip(mixer, "xlr1"));
        Assert.True(mixer.SetUnitXlrJacks(true));
        Assert.Equal(("XLR 1", "mic"), Strip(mixer, "xlr1"));
        // A chosen icon still wins over the default; the default stays reported.
        Assert.False(mixer.SetUnitXlrJacks(true));
    }

    public static TheoryData<string, bool> Models => new()
    {
        { "Wave XLR Pro", true }, { "Wave XLR", true }, { "Wave XLR MK.2", true },
        { "XLR Dock", true }, { "XLR Dock MK.2", true }, { "Wave:3", false }, { "Wave:1", false },
    };

    private static DeviceCapabilities CapabilitiesOf(string model) => model switch
    {
        "Wave XLR Pro" => new WaveXlrProDevice().Capabilities,
        "Wave XLR" => new WaveXlrMk1Device().Capabilities,
        "Wave XLR MK.2" => new WaveXlrMk2Device().Capabilities,
        "XLR Dock" => new XlrDockDevice().Capabilities,
        "XLR Dock MK.2" => new XlrDockMk2Device().Capabilities,
        "Wave:3" => new Wave3Device(new Wave3DeviceTests.FakeUsb()).Capabilities,
        _ => new Wave1Device(new Wave3DeviceTests.FakeUsb()).Capabilities,
    };

    [Theory]
    [MemberData(nameof(Models))]
    public void OnlyARealXlrJackKeepsTheXlrNameOnTheUnitItself(string model, bool xlr)
    {
        DeviceCapabilities capabilities = CapabilitiesOf(model);
        Assert.Equal(xlr, capabilities.XlrJacks);
        var unit = new Unit(model, capabilities);
        using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [unit]);
        using var service = new MixerService(NullLogger<MixerService>.Instance, new ConfigurationBuilder().Build(), devices);
        devices.SweepOnce();
        Assert.True(unit.Connected);
        typeof(MixerService).GetMethod("UpdateInputDeviceHint", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, null);
        var mixer = (Mixer)typeof(MixerService).GetField("_mixer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        Assert.Equal(xlr ? ("XLR 1", "mic") : ("Mic 1", "microphone"), Strip(mixer, "xlr1"));
        // The state carries the capability for the clients that label the unit's input.
        Assert.Equal(xlr, devices.Snapshot().Capabilities!.XlrJacks);
        // Another source on the strip is Mic 1 whatever the unit is.
        mixer.SetInputBinding("xlr1", new("alsa_input.usb-cam", 0, 0));
        Assert.Equal(("Mic 1", "microphone"), Strip(mixer, "xlr1"));
        devices.SuspendSession();
    }

    /// <summary>
    /// A strip bound to another supported unit is named by what feeds it, as
    /// the unit's own strips are: one of an XLR Dock's or a Wave XLR's jacks
    /// keeps the strip's XLR name and icon, a Wave:3's capsule is Mic 1 or
    /// Mic 2. The answer is kept by model through an unplug in this run.
    /// </summary>
    [Fact]
    public void AStripBoundToAnotherUnitsJackKeepsItsXlrName()
    {
        const string dock = "0fd9:00a6@00000000000000d0", wave3 = "0fd9:0070@00000000000000e0";
        using var mixer = new Mixer();
        mixer.SetDrivenUnit("0fd9:00b4@00000000000000f0");
        mixer.SetInputBinding("xlr1", new("alsa_input.usb-Elgato_Systems_Elgato_XLR_Dock_D-00.analog-stereo", 0, 0) { Unit = dock });
        mixer.SetInputBinding("xlr2", new("alsa_input.usb-Elgato_Systems_Elgato_Wave_3_W-00.mono-fallback", 0, 0) { Unit = wave3 });
        // Before the daemon has said what the units are, a bound strip is a Mic.
        Assert.Equal(("Mic 1", "microphone"), Strip(mixer, "xlr1"));
        Assert.True(mixer.SetAttachedUnits([new(dock, [], 1, XlrJacks: true, Driven: false), new(wave3, [], 1, XlrJacks: false, Driven: false)]));
        Assert.Equal(("XLR 1", "mic"), Strip(mixer, "xlr1"));
        Assert.Equal(("Mic 2", "microphone"), Strip(mixer, "xlr2"));
        // Unplugged: the model's answer stays for the run.
        Assert.True(mixer.SetAttachedUnits([]));
        Assert.Equal(("XLR 1", "mic"), Strip(mixer, "xlr1"));
        // The same jack bound to the other strip is XLR 2.
        mixer.SetInputBinding("xlr1", null);
        mixer.SetInputBinding("xlr2", new("alsa_input.usb-Elgato_Systems_Elgato_XLR_Dock_D-00.analog-stereo", 0, 0) { Unit = dock });
        Assert.Equal(("XLR 2", "mic"), Strip(mixer, "xlr2"));
    }

    private sealed class Unit(string model, DeviceCapabilities capabilities) : IAudioDevice
    {
        public DeviceInfo Info { get; } = new("Elgato", model, 0x0fd9, 0x0070);
        public DeviceCapabilities Capabilities { get; } = capabilities;
        public bool Connected { get; private set; }
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public void Dispose() => Connected = false;
        public DeviceState ReadState() => new() { GainDb = 30 };
        public void SetGainDb(int db) { } public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }
}
