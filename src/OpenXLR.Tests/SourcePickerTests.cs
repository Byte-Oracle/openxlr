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

/// <summary>
/// What the binding dialog knows about a capture source: which supported
/// unit owns its node, how many ports it has and what PipeWire calls them.
/// </summary>
[Collection("xdg-config")]
public sealed class SourcePickerTests
{
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
    public void HardwareSourcesAreNamedWithoutTheirAlsaProfile()
    {
        string[] names = PipeWireAdapter.ShortHardwareNames([
            ("Elgato Wave XLR Dock MK.2 Analog Stereo", "Analog Stereo"),
            ("Ryzen HD Audio Controller Stereo analogico", "Stereo analogico"),
            ("Wave XLR Pro mic (raw, driven by OpenXLR)", "Pro"),
            ("Webcam Analog Stereo", "Analog Stereo"),
            ("Webcam Digital Stereo (IEC958)", "Digital Stereo (IEC958)"),
            ("Katana Analog Stereo", null),
            ("Analog Stereo", "Analog Stereo"),
        ]);
        Assert.Equal([
            "Elgato Wave XLR Dock MK.2",
            "Ryzen HD Audio Controller",
            "Wave XLR Pro mic (raw, driven by OpenXLR)",
            "Webcam Analog Stereo",
            "Webcam Digital Stereo (IEC958)",
            "Katana Analog Stereo",
            "Analog Stereo",
        ], names);
    }

    [Fact]
    public void PortChoicesAreNamedAndCappedAtTheChannelCount()
    {
        Assert.Equal(["1 (MONO)"], InputBindingDialog.PortChoices(new("wave3", "Wave:3", false, true, 1, "MONO")).Select(p => p.Label));
        Assert.Equal(["1 (FL)", "2 (FR)"], InputBindingDialog.PortChoices(new("cam", "Webcam", false, true, 2, "FL,FR")).Select(p => p.Label));
        Assert.Equal("3 (AUX2)", InputBindingDialog.PortChoices(new("pro", "Interface", false, true, 18,
            string.Join(',', Enumerable.Range(0, 18).Select(i => $"AUX{i}"))))[2].Label);
        Assert.Equal(["1", "2"], InputBindingDialog.PortChoices(new("plain", "Plain", false, true, 2)).Select(p => p.Label));
        // Unknown layout: every port the command accepts. A saved port past the count stays selectable.
        Assert.Equal(64, InputBindingDialog.PortChoices(null).Count);
        Assert.Equal([0, 1, 4], InputBindingDialog.PortChoices(new("cam", "Webcam", false, true, 2, "FL,FR"), keep: 4).Select(p => p.Index));
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
