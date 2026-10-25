using OpenXLR.UI.Localization;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>A mix's insert chain in its own window: add, reorder, bypass, remove, open controls.</summary>
public partial class MixInsertsWindow : Window
{
    public MixInsertsWindow()
    {
        InitializeComponent();
    }

    private MicCheckWindow? _micCheck;
    private void OnMicCheck(object? sender, RoutedEventArgs e)
    {
        if (Chain is not { CanMicCheck: true } chain) return;
        if (_micCheck is not null) { _micCheck.Activate(); return; }
        _micCheck = new MicCheckWindow { DataContext = chain.MicCheck };
        _micCheck.Closed += (_, _) => _micCheck = null;
        _micCheck.Show(this);
    }

    private InsertsViewModel? Chain => DataContext as InsertsViewModel;

    private EffectWorkflowWindow? _workflow;

    private void OnWorkflow(object? sender, RoutedEventArgs e)
    {
        if (Chain is null) return;
        if (_workflow is not null) { _workflow.Activate(); return; }
        _workflow = new EffectWorkflowWindow { DataContext = Chain };
        _workflow.Closed += (_, _) => _workflow = null;
        _workflow.Show(this);
    }

    private void OnCopyInsert(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel insert) insert.Owner.CopyEffects(insert);
    }

    private async void OnRenameInsert(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not InsertViewModel insert) return;
        string? name = await Dialogs.NameAsync(this, Localizer.Format("RenameEffectTitle", insert.Label), insert.Label,
            Localizer.Text("RenameEffectHint"), 256);
        if (name is not null && name != insert.Label) await insert.Owner.RenameEffectAsync(insert, name);
    }

    private async void OnAddInsert(object? sender, RoutedEventArgs e)
    {
        if (Chain is null) return;
        var picker = new PluginPickerWindow { DataContext = Chain };
        PluginChoice? choice = await picker.ShowDialog<PluginChoice?>(this);
        if (choice is not null) Chain.Add(choice);
    }

    private void OnInsertControls(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel ins) InsertWindows.OpenControls(this, ins);
    }

    private void OnInsertUp(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel ins) ins.Owner.Move(ins, -1);
    }

    private void OnInsertDown(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel ins) ins.Owner.Move(ins, +1);
    }

    private void OnInsertRemove(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel ins) ins.Owner.Remove(ins);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
