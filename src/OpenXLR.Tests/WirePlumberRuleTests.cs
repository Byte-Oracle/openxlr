using System.Text.RegularExpressions;

namespace OpenXLR.Tests;

/// <summary>
/// The WirePlumber rules under packaging/: each capture-hold rule matches its
/// own device's capture node and nothing else attached next to it, and every
/// rule is installed by each package recipe and by the source install.
/// </summary>
public sealed class WirePlumberRuleTests
{
    private const string Dock = "50-xlr-dock-capture-hold.conf";
    private const string Mk1 = "52-openxlr-mk1-capture-hold.conf";
    private const string Wave3 = "53-openxlr-wave3-capture-hold.conf";

    // Node names as udev and the ALSA monitor build them: the product string
    // with spaces and colons turned into underscores, the serial, then the
    // interface and the profile's device name.
    public static TheoryData<string, string?> Nodes => new()
    {
        { "alsa_input.usb-Elgato_Systems_Elgato_XLR_Dock_0123456789-00.pro-input-0", Dock },
        { "alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_0123456789-00.pro-input-0", Mk1 },
        { "alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_0123456789-00.analog-stereo", Mk1 },
        { "alsa_input.usb-Elgato_Systems_Elgato_Wave_3_A011A53710KH3Y-00.mono-fallback", Wave3 },
        { "alsa_input.usb-Elgato_Systems_Elgato_Wave_3_A011A53710KH3Y-00.pro-input-0", Wave3 },
        { "alsa_output.usb-Elgato_Systems_Elgato_Wave_3_A011A53710KH3Y-00.analog-stereo", null },
        { "alsa_input.usb-Elgato_Systems_Elgato_Wave_1_A011A53710KH3Y-00.mono-fallback", null },
        { "alsa_input.usb-Elgato_Elgato_Wave_XLR_Pro_0123456789-00.pro-input-0", null },
        { "alsa_input.usb-Elgato_Elgato_Wave_XLR_Pro_0123456789-00.multichannel-input", null },
    };

    [Theory]
    [MemberData(nameof(Nodes))]
    public void EachCaptureHoldRuleMatchesOnlyItsDevice(string node, string? rule)
    {
        string[] matching = [.. new[] { Dock, Mk1, Wave3 }.Where(file => NodePattern(file).IsMatch(node))];
        Assert.Equal(rule is null ? [] : [rule], matching);
    }

    [Theory]
    [InlineData(Dock)]
    [InlineData(Mk1)]
    [InlineData(Wave3)]
    public void CaptureHoldRulesKeepTheSourceRunning(string file)
    {
        string text = File.ReadAllText(Path.Combine(Root(), "packaging", file));
        Assert.Contains("session.suspend-timeout-seconds = 0", text);
        Assert.Contains("node.pause-on-idle = false", text);
        Assert.Contains("node.always-process = true", text);
    }

    [Fact]
    public void EveryRuleIsInstalledEverywhere()
    {
        string root = Root();
        string[] rules = [.. Directory.GetFiles(Path.Combine(root, "packaging"), "5*.conf").Select(path => Path.GetFileName(path)).Order()];
        Assert.Contains(Wave3, rules);
        Assert.Equal(rules, OpenXLR.UI.FlatpakSetup.Rules.Order());
        string debian = File.ReadAllText(Path.Combine(root, "debian", "rules"));
        string spec = File.ReadAllText(Path.Combine(root, "packaging", "rpm", "openxlr.spec"));
        string nix = File.ReadAllText(Path.Combine(root, "packaging", "nix", "package.nix"));
        string source = File.ReadAllText(Path.Combine(root, "docs", "install-from-source.md"));
        foreach (string rule in rules)
        {
            Assert.Contains($"install -Dm644 packaging/{rule} \\", debian);
            Assert.Contains($"$(PKG)/usr/share/wireplumber/wireplumber.conf.d/{rule}", debian);
            Assert.Contains($"install -Dm644 packaging/{rule} \\", spec);
            Assert.Contains($"\n%{{_datadir}}/wireplumber/wireplumber.conf.d/{rule}\n", spec);
            Assert.Contains($"packaging/{rule}", nix);
            Assert.Contains($"cp packaging/{rule} ~/.config/wireplumber/wireplumber.conf.d/", source);
            Assert.Contains($"cmp -s packaging/{rule} ~/.config/wireplumber/wireplumber.conf.d/{rule} && rm --", source);
        }
    }

    /// <summary>The rule's node.name regex, unescaped from its SPA JSON string.</summary>
    private static Regex NodePattern(string file)
    {
        string text = File.ReadAllText(Path.Combine(Root(), "packaging", file));
        Match match = Regex.Match(text, "node\\.name = \"~((?:[^\"\\\\]|\\\\.)*)\"");
        Assert.True(match.Success, $"{file} has no node.name regex");
        return new Regex(Regex.Replace(match.Groups[1].Value, @"\\(.)", "$1"));
    }

    private static string Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "skins.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory.FullName;
    }
}
