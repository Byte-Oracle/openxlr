using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>
/// The window's half of application icons: the freedesktop icon theme
/// lookup against a fake tree of themes, and the desktop's theme read from
/// its settings files.
/// </summary>
[Collection("xdg-config")]
public sealed class IconThemeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("openxlr-icon-theme-").FullName;
    private string User => Path.Combine(_root, "home/icons");
    private string Shared => Path.Combine(_root, "usr/icons");
    private string Pixmaps => Path.Combine(_root, "usr/pixmaps");

    public IconThemeTests()
    {
        Theme(Shared, "Papirus", "hicolor", ("16x16/apps", "Size=16\nType=Fixed"), ("22x22/apps", "Size=22\nType=Fixed"),
            ("48x48/apps", "Size=48\nType=Fixed"), ("64x64/apps", "Size=64\nType=Fixed"));
        Theme(Shared, "Papirus-Dark", "Papirus,breeze", ("16x16/apps", "Size=16\nType=Fixed"));
        Theme(Shared, "breeze", "hicolor", ("apps/48", "Size=48\nType=Scalable\nMinSize=16\nMaxSize=256"));
        Theme(Shared, "hicolor", "", ("16x16/apps", "Size=16\nType=Fixed"), ("32x32/apps", "Size=32\nType=Fixed"),
            ("256x256/apps", "Size=256\nType=Fixed"), ("scalable/apps", "Size=128\nType=Scalable\nMinSize=8\nMaxSize=512"));
        // The user's hicolor holds no index of its own: the shared one describes it.
        Directory.CreateDirectory(Path.Combine(User, "hicolor/256x256/apps"));
        Directory.CreateDirectory(Pixmaps);

        Icon(Shared, "Papirus/22x22/apps/steam.svg");
        Icon(Shared, "Papirus/16x16/apps/steam.svg");
        Icon(Shared, "Papirus/48x48/apps/steam.svg");
        Icon(Shared, "hicolor/32x32/apps/steam.png");
        Icon(Shared, "breeze/apps/48/discord.svg");
        Icon(Shared, "hicolor/16x16/apps/signal-desktop.png");
        Icon(Shared, "hicolor/32x32/apps/signal-desktop.png");
        Icon(Shared, "hicolor/256x256/apps/signal-desktop.png");
        Icon(User, "hicolor/256x256/apps/io.github.tobagin.karere.png");
        Icon(Shared, "hicolor/scalable/apps/io.github.tobagin.karere.svg");
        Icon(Pixmaps, "legacy.png");
        Icon(Pixmaps, "legacy.xpm");
        Icon(_root, "secret.png");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Theme(string baseDir, string name, string inherits, params (string Dir, string Keys)[] dirs)
    {
        string root = Path.Combine(baseDir, name);
        Directory.CreateDirectory(root);
        string index = $"[Icon Theme]\nName={name}\nInherits={inherits}\nDirectories={string.Join(',', dirs.Select(d => d.Dir))}\n";
        foreach ((string dir, string keys) in dirs)
        {
            Directory.CreateDirectory(Path.Combine(root, dir));
            index += $"\n[{dir}]\nContext=Applications\n{keys}\n";
        }
        File.WriteAllText(Path.Combine(root, "index.theme"), index);
    }

    private static void Icon(string baseDir, string path)
    {
        string file = Path.Combine(baseDir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
    }

    private IconThemeLookup Lookup(string? theme) => new([User, Shared], [Pixmaps], theme);

    private string Rel(string file) => Path.GetRelativePath(_root, file);

    [Fact]
    public void TheDesktopsThemeAndWhatItInheritsComeBeforeHicolor()
    {
        IReadOnlyList<string> steam = Lookup("Papirus-Dark").Find("steam", 18);
        // Papirus-Dark has no steam of its own; Papirus, which it inherits, does,
        // made for 22 before 16, then the larger 48, then hicolor's.
        Assert.Equal(["usr/icons/Papirus/22x22/apps/steam.svg", "usr/icons/Papirus/16x16/apps/steam.svg",
            "usr/icons/Papirus/48x48/apps/steam.svg", "usr/icons/hicolor/32x32/apps/steam.png"], steam.Select(Rel));
        // Papirus-Dark's second parent, then hicolor.
        Assert.Equal("usr/icons/breeze/apps/48/discord.svg", Rel(Lookup("Papirus-Dark").Find("discord", 18)[0]));
        Assert.Equal("usr/icons/hicolor/32x32/apps/steam.png", Rel(Lookup(null).Find("steam", 18)[0]));
        Assert.Equal("usr/icons/hicolor/32x32/apps/steam.png", Rel(Lookup("NotInstalled").Find("steam", 18)[0]));
    }

    [Theory]
    [InlineData(16, "16x16")]
    [InlineData(18, "32x32")]   // larger scales down cleanly
    [InlineData(28, "32x32")]
    [InlineData(64, "256x256")]
    [InlineData(400, "256x256")]
    public void ASizeNearTheDrawnSizeComesFirst(int size, string dir)
        => Assert.Equal($"usr/icons/hicolor/{dir}/apps/signal-desktop.png", Rel(Lookup(null).Find("signal-desktop", size)[0]));

    [Fact]
    public void EveryBaseDirectoryOfAThemeIsSearchedAndAScalableIconMatchesAnySize()
    {
        IReadOnlyList<string> karere = Lookup(null).Find("io.github.tobagin.karere", 18);
        Assert.Equal(["usr/icons/hicolor/scalable/apps/io.github.tobagin.karere.svg",
            "home/icons/hicolor/256x256/apps/io.github.tobagin.karere.png"], karere.Select(Rel));
    }

    [Fact]
    public void PixmapsAndPathsAreTheLastResortAndOnlyPngAndSvgCount()
    {
        Assert.Equal(["usr/pixmaps/legacy.png"], Lookup("Papirus").Find("legacy", 18).Select(Rel));
        Assert.Equal(["usr/pixmaps/legacy.png"], Lookup("Papirus").Find("legacy.png", 18).Select(Rel));
        string file = Path.Combine(Pixmaps, "legacy.png");
        Assert.Equal([file], Lookup(null).Find(file, 18));
        Assert.Empty(Lookup(null).Find(Path.Combine(Pixmaps, "legacy.xpm"), 18));
        Assert.Empty(Lookup(null).Find(Path.Combine(Pixmaps, "missing.png"), 18));
        Assert.Empty(Lookup(null).Find("missing", 18));
        Assert.Empty(Lookup(null).Find(null, 18));
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("../../secret")]
    [InlineData("hicolor/32x32/apps/steam")]
    [InlineData("..")]
    public void ANameIsOneSegmentAndNeverReachesOutside(string name)
        => Assert.Empty(Lookup(null).Find(name, 18));

    [Fact]
    public void AnInheritanceLoopEnds()
    {
        Theme(Shared, "Loop-A", "Loop-B", ("16x16/apps", "Size=16\nType=Fixed"));
        Theme(Shared, "Loop-B", "Loop-A", ("16x16/apps", "Size=16\nType=Fixed"));
        Assert.Equal("usr/icons/hicolor/32x32/apps/steam.png", Rel(Lookup("Loop-A").Find("steam", 18)[0]));
    }

    [Fact]
    public void TheDesktopsThemeIsReadFromItsSettings()
    {
        string config = Path.Combine(_root, "config");
        string[] keys = ["XDG_CONFIG_HOME", "XDG_CONFIG_DIRS", "XDG_CURRENT_DESKTOP"];
        string?[] saved = [.. keys.Select(Environment.GetEnvironmentVariable)];
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
            Environment.SetEnvironmentVariable("XDG_CONFIG_DIRS", Path.Combine(_root, "etc"));
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "KDE");
            Assert.Equal("breeze", IconThemeLookup.DesktopTheme());   // Plasma's own default

            Directory.CreateDirectory(Path.Combine(config, "gtk-3.0"));
            File.WriteAllText(Path.Combine(config, "gtk-3.0/settings.ini"), "[Settings]\ngtk-icon-theme-name=Adwaita\n");
            File.WriteAllText(Path.Combine(config, "kdeglobals"), "[General]\nTheme=wrong\n\n[Icons]\nTheme=Papirus-Dark\n");
            Assert.Equal("Papirus-Dark", IconThemeLookup.DesktopTheme());

            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "GNOME");
            Assert.Equal("Adwaita", IconThemeLookup.DesktopTheme());
            File.WriteAllText(Path.Combine(config, "gtk-3.0/settings.ini"), "[Settings]\ngtk-icon-theme-name=../../x\n");
            Assert.Equal("Papirus-Dark", IconThemeLookup.DesktopTheme());
        }
        finally
        {
            for (int i = 0; i < keys.Length; i++) Environment.SetEnvironmentVariable(keys[i], saved[i]);
        }
    }

    [Fact]
    public void TheSearchFollowsXdgAndAddsFlatpakAndSnap()
    {
        string?[] saved = [Environment.GetEnvironmentVariable("XDG_DATA_HOME"), Environment.GetEnvironmentVariable("XDG_DATA_DIRS")];
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "home"));
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "usr"));
            IReadOnlyList<string> bases = IconThemeLookup.BaseDirectories();
            Assert.Equal([User, Shared], bases.Skip(1).Take(2));
            Assert.EndsWith("/.icons", bases[0]);
            Assert.Contains("/var/lib/flatpak/exports/share/icons", bases);
            Assert.Contains("/var/lib/snapd/desktop/icons", bases);
            Assert.Contains(bases, b => b.EndsWith("/.local/share/flatpak/exports/share/icons", StringComparison.Ordinal));
            Assert.Equal([Pixmaps, "/usr/share/pixmaps"], IconThemeLookup.PixmapDirectories());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", saved[0]);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", saved[1]);
        }
    }
}
