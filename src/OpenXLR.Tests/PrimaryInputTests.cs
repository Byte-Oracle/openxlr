using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class PrimaryInputTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData(" ", 0)]
    [InlineData("OpenXLR_chat", 0)]
    [InlineData("speakers.monitor", 0)]
    [InlineData("mic\nother", 0)]
    [InlineData("mic", -1)]
    [InlineData("mic", 64)]
    public void InvalidPrimaryBindingsAreRejectedByBothCommandAndMixer(string source, int channel)
    {
        using var mixer = new Mixer();
        Assert.NotNull(CommandValidation.Check(new Command
            { Cmd = "setPrimaryInput", Source = source, CaptureMonoChannel = channel }, mixer, _ => null));
        Assert.Throws<ArgumentException>(() => mixer.SetPrimaryInput(new(source, channel)));
        Assert.Null(mixer.PrimaryInput);
    }

    [Fact]
    public void SelectionUsesTheExistingCaptureBoundsAndNullRestoresAutomaticMode()
    {
        using var mixer = new Mixer();
        Assert.Null(CommandValidation.Check(new() { Cmd = "setPrimaryInput" }, mixer, _ => null));
        Assert.Null(CommandValidation.Check(new() { Cmd = "setPrimaryInput", Source = "mic", CaptureMonoChannel = 63 }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "setPrimaryInput", Source = "mic", CapturePair = 1 }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "setPrimaryInput", CaptureMonoChannel = 1 }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "setPrimaryInput", Source = new string('a', 257) }, mixer, _ => null));
        mixer.SetPrimaryInput(new("mic", 63));
        Assert.Equal(new("mic", 63), mixer.ExportSettings().PrimaryInput);
        Assert.False(mixer.SetHardwareMicMonitor(true));
        mixer.SetPrimaryInput(null);
        Assert.Null(mixer.ExportSettings().PrimaryInput);
    }

    [Fact]
    public void SavedBindingRoundTripsAndMalformedEntriesProduceAWarning()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-primary-settings-").FullName;
        string path = Path.Combine(dir, "mixer.json");
        try
        {
            Assert.Null(new MixerSettings { PrimaryInput = new("mic", 3) }.Save(path));
            Assert.Equal(new("mic", 3), MixerSettings.Load(path)!.PrimaryInput);
            File.WriteAllText(path, """{"primaryInput":{"source":"OpenXLR_chat","channel":0}}""");
            Assert.Null(MixerSettings.Load(path, out string? warning)!.PrimaryInput);
            Assert.Contains("primaryInput", warning);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task GenericStateHidesHardwareControlsAndKeepsSoftwareProcessing()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var model = new MainViewModel(client);
        void Apply(string state) => typeof(MainViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [JsonNode.Parse(state)!]);
        Apply("""{"connected":true,"capabilities":{"xlrInputs":2,"hpOutputs":2,"lowCut":true,"phantom":true,"clipGuard":true,"auxInput":true}}""");
        Assert.True(model.CapPhantom);
        Apply("""{"connected":false,"devices":[],"mixer":{"primaryInput":{"source":"test.mic","channel":1},"primaryInputConnected":true}}""");
        Assert.True(model.GenericMicrophone);
        Assert.False(model.CapPhantom);
        Assert.False(model.CapClipGuard);
        Assert.False(model.HasXlr2);
        Assert.False(model.HasHp2);
        Assert.False(model.CapAuxInput);
        Assert.True(model.ShowSoftLowCut);
        Assert.True(model.ShowSoftClipGuard);
        Assert.False(model.ShowGainLock);
        Assert.False(model.ShowResetDefaults);
        Assert.Contains("test.mic", model.StatusLine);
        model.Channels.Add(new(client, "xlr1", "Microphone", []) { IsHardware = true });
        using var rack = new InputRackViewModel(model);
        using var matrix = new MixMatrixViewModel(model);
        Assert.DoesNotContain(SkinStripParts.Gain, rack.Strips.Single().Spec.Parts);
        Assert.DoesNotContain(SkinStripParts.Gain, matrix.Rows.Single().Spec.Parts);
        Assert.DoesNotContain(SkinStripParts.Mute, matrix.Rows.Single().Spec.Parts);
        Assert.Contains(SkinStripParts.Meter, matrix.Rows.Single().Spec.Parts);
        var flow = FlowGraph.From(model);
        Assert.Contains(flow.Nodes, n => n.Key == "capture:xlr1" && n.Label == "test.mic" && n.Active);
        Assert.DoesNotContain(flow.Nodes, n => n.Key == "hw:xlr1");
        Apply("""{"connected":true,"capabilities":{"xlrInputs":2,"hpOutputs":2,"phantom":true,"clipGuard":true},"mixer":{}}""");
        Assert.False(model.GenericMicrophone);
        Assert.True(model.CapPhantom);
        Assert.True(model.HasXlr2);
        Assert.False(model.ShowSoftClipGuard);
        Assert.Contains(SkinStripParts.Gain, rack.Strips.Single().Spec.Parts);
        Assert.Contains(SkinStripParts.Gain, matrix.Rows.Single().Spec.Parts);
        Assert.Contains(SkinStripParts.Mute, matrix.Rows.Single().Spec.Parts);
    }
    private static DeviceInfo Unit(string model, ushort pid, string? serial, string port = "1-2")
        => new("Elgato", model, 0x0fd9, pid) { Location = new UsbLocation(1, 2, port, serial) };

    [Fact]
    public void NodesOfAttachedSupportedUnitsNameTheirUnit()
    {
        DeviceInfo pro = Unit("Wave XLR Pro", 0x00b4, "AAY3H5481241GS");
        DeviceInfo wave = Unit("Wave XLR", 0x007d, "SER1", "1-3");
        DeviceInfo wave3 = Unit("Wave:3", 0x0070, "A011A53710KH3Y", "1-4");
        IReadOnlyList<DeviceInfo> attached = [pro, wave, wave3];
        Assert.Equal((pro.InstanceId, "Elgato Wave XLR Pro"),
            InterfaceNodes.Owner("alsa_input.usb-Elgato_Elgato_Wave_XLR_Pro_AAY3H5481241GS-00.pro-input-0", attached));
        // "Wave_XLR" is inside "Wave_XLR_Pro": the Pro's serial is the longer match.
        Assert.Equal(pro.InstanceId, InterfaceNodes.Owner("alsa_output.usb-Elgato_Elgato_Wave_XLR_Pro_AAY3H5481241GS-00.HiFi__Headphones__sink", attached)?.Id);
        Assert.Equal(wave.InstanceId, InterfaceNodes.Owner("alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_SER1-00.mono-fallback", attached)?.Id);
        Assert.Equal(wave3.InstanceId, InterfaceNodes.Owner("alsa_input.usb-Elgato_Systems_Elgato_Wave_3_A011A53710KH3Y-00.pro-input-0", attached)?.Id);
        Assert.Null(InterfaceNodes.Owner("alsa_input.usb-046d_Logi_4K_Stream_Edition_C971DF30-03.analog-stereo", attached));
        Assert.Null(InterfaceNodes.Owner("Wave_XLR_virtual_source", attached)); // only an ALSA card belongs to a unit
        Assert.Null(InterfaceNodes.Owner("alsa_input.usb-Elgato_Elgato_Wave_XLR_Pro_AAY3H5481241GS-00.pro-input-0", []));

        // Two units of one model without serials cannot be told apart: the node is theirs, under the model id.
        IReadOnlyList<DeviceInfo> twins = [Unit("Wave XLR", 0x007d, null, "1-5"), Unit("Wave XLR", 0x007d, null, "1-6")];
        Assert.Equal("0fd9:007d", InterfaceNodes.Owner("alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR-00.mono-fallback", twins)?.Id);
    }

    [Theory]
    [InlineData("""{"audio.channels":1,"audio.position":"[ MONO ]"}""", 1, "MONO")]
    [InlineData("""{"audio.channels":2,"audio.position":"[ FL, FR ]"}""", 2, "FL,FR")]
    [InlineData("""{"audio.position":"FL,FR"}""", 2, "FL,FR")]
    [InlineData("""{"audio.channels":3,"audio.position":["AUX0","AUX1","AUX2"]}""", 3, "AUX0,AUX1,AUX2")]
    [InlineData("""{"audio.channels":4,"audio.position":"[ FL, FR ]"}""", 4, null)]
    [InlineData("""{"audio.channels":2}""", 2, null)]
    [InlineData("""{"audio.channels":0,"audio.position":"[ ]"}""", null, null)]
    [InlineData("""{}""", null, null)]
    public void SourcePortLayoutIsReadFromNodeProperties(string props, int? channels, string? positions)
    {
        using var doc = JsonDocument.Parse(props);
        (int? count, string[]? names) = PipeWireAdapter.PortLayout(doc.RootElement);
        Assert.Equal(channels, count);
        Assert.Equal(positions, names is null ? null : string.Join(',', names));
    }

    [Fact]
    public void PortChoicesAreNamedAndCappedAtTheChannelCount()
    {
        Assert.Equal(["1 (MONO)"], OptionsWindow.PortChoices(new("wave3", "Wave:3", false, true, 1, "MONO")).Select(p => p.Label));
        Assert.Equal(["1 (FL)", "2 (FR)"], OptionsWindow.PortChoices(new("cam", "Webcam", false, true, 2, "FL,FR")).Select(p => p.Label));
        Assert.Equal("3 (AUX2)", OptionsWindow.PortChoices(new("pro", "Interface", false, true, 18,
            string.Join(',', Enumerable.Range(0, 18).Select(i => $"AUX{i}"))))[2].Label);
        Assert.Equal(["1", "2"], OptionsWindow.PortChoices(new("plain", "Plain", false, true, 2)).Select(p => p.Label));
        // Unknown layout: every port the command accepts. A saved port past the count stays selectable.
        Assert.Equal(64, OptionsWindow.PortChoices(null).Count);
        Assert.Equal([0, 1, 4], OptionsWindow.PortChoices(new("cam", "Webcam", false, true, 2, "FL,FR"), keep: 4).Select(p => p.Index));
    }

    [Fact]
    public async Task DeviceEntriesCarryPortsAndTheOwningUnit()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var model = new MainViewModel(client);
        typeof(MainViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [JsonNode.Parse("""
            {"connected":false,"mixer":{},"devices":[
              {"name":"cam","description":"Webcam","kind":1,"isOwn":false,"isPhysical":true,"channels":2,"positions":["FL","FR"]},
              {"name":"pro","description":"Wave XLR Pro","kind":1,"isOwn":false,"isPhysical":true,"channels":18,"drivenUnit":"0fd9:00b4@abc"}]}
            """)!]);
        Assert.Equal(new AudioDeviceItem("cam", "Webcam", false, true, 2, "FL,FR"), model.Inputs[0]);
        Assert.Equal("FR", model.Inputs[0].PositionOf(1));
        Assert.Equal("0fd9:00b4@abc", model.Inputs[1].DrivenUnit);
    }
}
