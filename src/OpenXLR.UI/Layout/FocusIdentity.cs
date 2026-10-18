using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace OpenXLR.UI.Layout;

/// <summary>
/// What a focused control stands for, by meaning rather than by instance: its
/// role (a fader, a mute, a picker, a handle, or any other kind of control)
/// and whose it is (a channel id, a mix id, an application, or a view model
/// that has no id). A skin switch may build a template's controls again, so
/// the control that had focus can be gone afterwards while another one for
/// the same channel and mix stands in its place; this finds that one.
/// </summary>
/// <param name="Role">"fader", "mute", "picker", "handle", or the control's type name.</param>
/// <param name="Name">The control's own name, for the window's named controls bound to the window's model.</param>
/// <param name="Model">The view model behind a control that has no id of its own, by reference.</param>
internal sealed record FocusIdentity(string Role, string? Channel, string? Mix, string? Application, string? Name, object? Model)
{
    /// <summary>The identity of a control, or null for one that cannot take focus.</summary>
    internal static FocusIdentity? Of(Control? control)
    {
        if (control is null || !control.Focusable) return null;
        string role = control switch
        {
            Slider => "fader",
            ToggleButton toggle when toggle.Classes.Contains("mute") => "mute",
            ComboBox or DropDownButton => "picker",
            Button button when button.Classes.Contains("reorderHandle") => "handle",
            _ => control.GetType().Name,
        };
        // A name tells apart the controls of one model: the window's own, or
        // the switches of one input strip. Copies under a data template share
        // theirs, and their model says whose each one is.
        string? name = control.Name;
        return control.DataContext switch
        {
            BankCellViewModel cell => new(role, cell.Channel.Id, cell.Bank.Mix?.Id, null, name, null),
            BankMasterViewModel master => new(role, null, master.Mix.Id, null, name, null),
            MixBankViewModel bank => new(role, null, bank.Mix?.Id, null, name, null),
            SendViewModel send => new(role, send.ChannelId, send.MixId, null, name, null),
            MixViewModel mix => new(role, null, mix.Id, null, name, null),
            ChannelViewModel channel => new(role, channel.Id, null, null, name, null),
            InputStripViewModel strip => new(role, strip.Channel.Id, null, null, name, null),
            AppStreamViewModel app => new(role, null, null, app.Identity, name, null),
            MainViewModel => new(role, null, null, null, name, null),
            var other => new(role, null, null, null, name, other),
        };
    }

    /// <summary>
    /// Focus the control in <paramref name="root"/> that stands for this
    /// identity: the same role for the same channel, mix or application, or,
    /// when the mix is gone, the same role for the same channel. Only a
    /// control that can take focus and is shown counts. The control focused,
    /// or null when there was none to focus.
    /// </summary>
    internal Control? Restore(TopLevel root)
    {
        Control[] candidates = [.. root.GetVisualDescendants().OfType<Control>()
            .Where(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled)];
        Control? match = candidates.FirstOrDefault(c => Of(c) == this);
        if (match is null && Channel is not null && Mix is not null)
            match = candidates.FirstOrDefault(c => Of(c) is { } other && other.Role == Role && other.Channel == Channel
                && other.Mix is not null);
        return match?.Focus(NavigationMethod.Unspecified) == true ? match : null;
    }
}
