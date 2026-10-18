using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace OpenXLR.UI;

/// <summary>
/// The insert windows the UI keeps open: one controls window per insert
/// and one chain window per mix, reused while open so a second click
/// raises the existing window instead of stacking another.
/// </summary>
public static class InsertWindows
{
    // Keyed by the insert itself: a slot id can come back holding another
    // plugin, and two chains can hold the same id.
    private static readonly Dictionary<InsertViewModel, InsertControlsWindow> Controls = new();
    private static readonly Dictionary<string, MixInsertsWindow> Chains = new();

    public static void OpenControls(Window owner, InsertViewModel insert)
    {
        if (Controls.TryGetValue(insert, out InsertControlsWindow? open) && Reuse(open, owner, insert)) return;
        var w = new InsertControlsWindow { DataContext = insert };
        w.Closed += (_, _) => { if (Controls.TryGetValue(insert, out InsertControlsWindow? kept) && kept == w) Controls.Remove(insert); };
        Controls[insert] = w;
        w.Show(owner);
    }

    /// <summary>Close the controls of an insert that left its chain.</summary>
    internal static void CloseControls(InsertViewModel insert)
    {
        if (Controls.TryGetValue(insert, out InsertControlsWindow? window)) window.Close();
    }

    /// <summary>Close a deleted mix's chain without affecting a replacement with the same id.</summary>
    internal static void CloseChain(InsertsViewModel chain, string key)
    {
        if (Chains.TryGetValue(key, out MixInsertsWindow? window) && ReferenceEquals(window.DataContext, chain))
            window.Close();
    }

    public static void OpenChain(Window owner, InsertsViewModel chain, string key)
    {
        if (Chains.TryGetValue(key, out MixInsertsWindow? open) && Reuse(open, owner, chain)) return;
        var w = new MixInsertsWindow { DataContext = chain };
        w.Closed += (_, _) => { if (Chains.TryGetValue(key, out MixInsertsWindow? kept) && kept == w) Chains.Remove(key); };
        Chains[key] = w;
        w.Show(owner);
    }

    /// <summary>
    /// What it takes to open again, under another window, every plugin
    /// window <paramref name="owner"/> holds open now. An owned window closes
    /// with its owner, and a window's owner cannot change while it is shown,
    /// so a window that is about to close hands its plugin windows on this
    /// way: each one opens again under the new owner, at the same place, on
    /// the same chain or insert. One whose insert has left its chain since
    /// does not come back.
    /// </summary>
    internal static IReadOnlyList<Action<Window>> Handover(Window owner)
    {
        var reopen = new List<Action<Window>>();
        foreach ((InsertViewModel insert, InsertControlsWindow window) in Controls)
        {
            if (window.Owner != owner || !window.IsVisible) continue;
            PixelPoint place = window.Position;
            reopen.Add(next =>
            {
                if (!insert.Owner.Items.Contains(insert)) return;
                OpenControls(next, insert);
                Controls[insert].Position = place;
            });
        }
        foreach ((string key, MixInsertsWindow window) in Chains)
        {
            if (window.Owner != owner || !window.IsVisible || window.DataContext is not InsertsViewModel chain) continue;
            PixelPoint place = window.Position;
            reopen.Add(next =>
            {
                OpenChain(next, chain, key);
                Chains[key].Position = place;
            });
        }
        return reopen;
    }

    private static bool Reuse(Window window, Window owner, object model)
    {
        // A window's owner cannot move with the mixer panel. Reopen its
        // controls under the window that asked for them. A deleted mix's id
        // can also return with a new chain, so its old model must not survive.
        if (window.Owner != owner || !ReferenceEquals(window.DataContext, model))
        {
            window.Close();
            return false;
        }
        if (!window.IsVisible) window.Show(owner);
        window.Activate();
        return true;
    }
}
