using System;
using System.Collections.Generic;
using System.Linq;

#if OPENXLR_UI
namespace OpenXLR.UI;
#else
namespace OpenXLR.Core;
#endif

/// <summary>
/// Keeps Wine's USB driver out of the prefixes OpenXLR starts. With it,
/// winedevice.exe opens every USB device on the machine and holds it for the
/// life of the prefix: the interface OpenXLR drives, a Stream Deck, a device
/// passed through to a virtual machine. A bridged plugin never needs USB, its
/// audio reaches us through yabridge. Shared with the window, which opens
/// Wine's uninstaller in a plugin's prefix.
/// </summary>
internal static class WineDllOverrides
{
    internal const string Variable = "WINEDLLOVERRIDES";
    internal const string UsbDriver = "wineusb.sys";
    internal const string UsbOff = UsbDriver + "=d";

    /// <summary>
    /// The user's value with the USB driver switched off at the end. Wine
    /// reads the entries in order and a later one for the same module wins,
    /// so an entry of the user's own for the driver would lose to ours. That
    /// entry is a deliberate choice, and the one way back for a plugin that
    /// looks for a USB licence dongle, so a value that already names the
    /// driver is kept exactly as it is.
    /// </summary>
    internal static string WithUsbOff(string? inherited)
    {
        if (string.IsNullOrWhiteSpace(inherited)) return UsbOff;
        if (NamesUsbDriver(inherited)) return inherited;
        return inherited.TrimEnd().TrimEnd(';') + ";" + UsbOff;
    }

    /// <summary>
    /// Sets the variable in a child's environment overlay, from the overlay's
    /// own value or else the one this process inherited.
    /// </summary>
    internal static void Apply(IDictionary<string, string> environment)
        => environment[Variable] = WithUsbOff(environment.TryGetValue(Variable, out string? value)
            ? value : Environment.GetEnvironmentVariable(Variable));

    // Wine's syntax: entries split by ';', each a list of modules split by ','
    // before '=' and the load order after it. Names match without regard to
    // case and may carry a leading '*'; only ".dll" may be left off a name, so
    // "wineusb" alone does not name the driver.
    private static bool NamesUsbDriver(string value)
        => value.Split(';')
            .Select(entry => entry.Split('=', 2)[0])
            .SelectMany(names => names.Split(','))
            .Any(name => string.Equals(name.Trim().TrimStart('*'), UsbDriver, StringComparison.OrdinalIgnoreCase));
}
