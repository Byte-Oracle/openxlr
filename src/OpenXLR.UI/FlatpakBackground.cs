using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace OpenXLR.UI;

internal static class FlatpakBackground
{
    internal const string Interface = "org.freedesktop.portal.Background";

    internal static async Task ConfigureAsync(bool audio, bool window, CancellationToken cancel = default)
    {
        using var connection = new DBusConnection(DesktopBus.SessionAddress());
        await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), cancel);
        await ConfigureAsync(new DesktopBus(connection), audio, window, cancel);
    }

    internal static async Task ConfigureAsync(DesktopBus bus, bool audio, bool window, CancellationToken cancel)
    {
        bool enabled = audio || window;
        var result = await bus.Request("RequestBackground", "sa{sv}", token => (ref MessageWriter w) =>
        {
            w.WriteString("");
            w.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["handle_token"] = token,
                ["reason"] = Localization.Localizer.Text("FlatpakBackgroundReason"),
                ["autostart"] = enabled,
                ["commandline"] = VariantValue.Array(window ? new[] { "openxlr" } : new[] { "openxlr", "--background" }),
                ["dbus-activatable"] = false,
            });
        }, cancel, Interface);
        if (!result.TryGetValue("background", out var background) || !background.GetBool()
            || !result.TryGetValue("autostart", out var autostart) || autostart.GetBool() != enabled)
            throw new InvalidOperationException(Localization.Localizer.Text("FlatpakBackgroundRefused"));
    }
}
