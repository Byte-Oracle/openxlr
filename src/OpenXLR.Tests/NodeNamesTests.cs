using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class NodeNamesTests
{
    [Theory]
    [InlineData("alsa_output.usb-SteelSeries_Arctis_Nova_Elite-00.analog-stereo", "SteelSeries Arctis Nova Elite")]
    [InlineData("alsa_output.usb-Elgato_Elgato_Wave_XLR_Dock_MK.2_AB2JA536121KPI-00.analog-stereo", "Elgato Wave XLR Dock MK.2")]
    [InlineData("alsa_output.usb-Elgato_Systems_Elgato_Wave_XLR_Pro-00.pro-output-0", "Elgato Wave XLR Pro")]
    [InlineData("alsa_output.usb-Generic_USB_Audio_201405280001-00.iec958-stereo", "Generic USB Audio")]
    [InlineData("alsa_output.usb-Generic_USB_Audio-00.HiFi__Speaker__sink", "Generic USB Audio")]
    [InlineData("alsa_input.usb-Audio_Technica_AT2020USB_-00.mono-fallback", "Audio Technica AT2020USB")]
    [InlineData("alsa_input.usb-Elgato_Systems_Elgato_Wave_3_BS12345678-01.mono-fallback", "Elgato Wave 3")]
    [InlineData("alsa_output.pci-0000_01_00.1.hdmi-stereo-extra2", "HDMI 3")]
    [InlineData("alsa_output.pci-0000_01_00.1.hdmi-stereo", "HDMI")]
    public void AnAlsaNodeReadsAsTheProductItNames(string node, string readable)
        => Assert.Equal(readable, NodeNames.Readable(node));

    [Theory]
    [InlineData("bluez_output.AA_BB_CC_DD_EE_FF.1")]
    [InlineData("alsa_output.pci-0000_00_1f.3.analog-stereo")]
    [InlineData("alsa_output.usb-Elgato_Systems_Elgato_Wave_XLR_Pro-00.pro-output-0#hp1")]
    [InlineData("alsa_output.usb-Elgato_Wave_XLR_Dock_MK.2")]
    [InlineData("alsa_output.")]
    [InlineData("OpenXLR_mix_monitor")]
    [InlineData("")]
    public void ANameThatDoesNotParseComesBackAsItIs(string node)
        => Assert.Equal(node, NodeNames.Readable(node));
}
