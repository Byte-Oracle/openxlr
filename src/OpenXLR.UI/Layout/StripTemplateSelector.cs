using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI.Layout;

/// <summary>
/// Picks one of the application's compiled templates for a strip, a bank's
/// master, an input row or a matrix row head by an explicit key, which is how a skin's template
/// name reaches markup the application owns. Avalonia has no
/// DataTemplateSelector; this is the small lookup in its place.
///
/// It does not recycle a control built for one item for the next: a
/// presenter that is handed another mix builds the template again, so no
/// fader is ever bound to the old mix's range and the new mix's level at once.
/// </summary>
public sealed class StripTemplateSelector : IDataTemplate
{
    /// <summary>The templates by key: a strip or master template name, or an input strip kind.</summary>
    [Content]
    public Dictionary<string, IDataTemplate> Templates { get; } = new(StringComparer.Ordinal);

    public bool Match(object? data) => Key(data) is { } key && Templates.ContainsKey(key);

    public Control? Build(object? param) =>
        Key(param) is { } key && Templates.TryGetValue(key, out IDataTemplate? template) ? template.Build(param) : null;

    /// <summary>The key a template is chosen by for an item.</summary>
    internal static string? Key(object? data) => data switch
    {
        BankCellViewModel cell => cell.Bank.Presentation.Options.Strip.Template,
        BankMasterViewModel master => master.Bank.Presentation.Options.Master.Template,
        InputStripViewModel strip => KindKey(strip.Kind),
        MatrixRowViewModel row => KindKey(row.Kind),
        _ => null,
    };

    /// <summary>The key of an input strip's or a matrix row head's template: what the channel is.</summary>
    private static string KindKey(InputStripKind kind) => kind switch
    {
        InputStripKind.Xlr1 => "xlr1",
        InputStripKind.Xlr2 => "xlr2",
        InputStripKind.AuxIn => "aux",
        InputStripKind.LineIn => "line",
        _ => "channel",
    };
}
