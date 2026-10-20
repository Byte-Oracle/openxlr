using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class TestEnvironmentTests
{
    [Fact]
    public void DefaultSettingsAndCachesBelongToTheTestProcess()
    {
        foreach (string variable in new[] { "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME", "WINEPREFIX" })
            Assert.StartsWith(TestEnvironment.Root + Path.DirectorySeparatorChar, Environment.GetEnvironmentVariable(variable));
        Assert.StartsWith(TestEnvironment.Root + Path.DirectorySeparatorChar, OpenXlrPaths.ConfigFile("daemon.json"));
        Assert.StartsWith(TestEnvironment.Root + Path.DirectorySeparatorChar, PluginScanLogStore.DefaultDirectory);
    }

    [Fact]
    public void DefaultNativeCatalogueCannotSearchTheDesktopsPlugins()
    {
        foreach (string directory in ClapCatalog.SearchPath().Concat(Vst3Catalog.SearchPath()))
            Assert.StartsWith(TestEnvironment.Root + Path.DirectorySeparatorChar, directory);
        Assert.Empty(ClapCatalog.SearchPath().SelectMany(ClapCatalog.Bundles));
        Assert.Empty(Vst3Catalog.SearchPath().SelectMany(Vst3Catalog.Bundles));
    }
}
