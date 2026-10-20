using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace OpenXLR.UI;

/// <summary>
/// The EQ editors the window keeps open: one per microphone, reused while
/// open, so a second click raises the editor rather than stacking another.
/// </summary>
public static class EqWindows
{
    private static readonly Dictionary<MicEqViewModel, EqWindow> Open = new();

    public static EqWindow Show(Window owner, MicEqViewModel eq)
    {
        if (Open.TryGetValue(eq, out EqWindow? open))
        {
            // A window's owner cannot change while it is shown: reopen under the window that asked.
            if (open.Owner == owner)
            {
                if (!open.IsVisible) open.Show(owner);
                open.Activate();
                return open;
            }
            open.Close();
        }
        var window = new EqWindow { DataContext = eq };
        window.Closed += (_, _) => { if (Open.TryGetValue(eq, out EqWindow? kept) && kept == window) Open.Remove(eq); };
        Open[eq] = window;
        window.Show(owner);
        return window;
    }

    /// <summary>The editor open for this EQ, or null.</summary>
    public static EqWindow? Find(MicEqViewModel eq) => Open.GetValueOrDefault(eq);

    /// <summary>Close the editor of an EQ the daemon no longer reports.</summary>
    internal static void Close(MicEqViewModel eq)
    {
        if (Open.TryGetValue(eq, out EqWindow? window)) window.Close();
    }

    /// <summary>
    /// What it takes to open again, under another window, every editor
    /// <paramref name="owner"/> holds open now, at the same place: an owned
    /// window closes with its owner, as the plugin windows do.
    /// </summary>
    internal static IReadOnlyList<Action<Window>> Handover(Window owner)
    {
        var reopen = new List<Action<Window>>();
        foreach ((MicEqViewModel eq, EqWindow window) in Open.ToArray())
        {
            if (window.Owner != owner || !window.IsVisible) continue;
            PixelPoint place = window.Position;
            reopen.Add(next => Show(next, eq).Position = place);
        }
        return reopen;
    }
}
