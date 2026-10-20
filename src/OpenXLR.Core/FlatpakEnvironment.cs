using System;

#if OPENXLR_UI
namespace OpenXLR.UI;
#else
namespace OpenXLR.Core;
#endif

/// <summary>The packaging identity, shared by the window and daemon.</summary>
internal static class FlatpakEnvironment
{
    internal const string AppId = "io.github.emaspa.openxlr";
    internal static bool IsFlatpak => Environment.GetEnvironmentVariable("FLATPAK_ID") == AppId;
}
