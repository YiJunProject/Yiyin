using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LightDeck.Core;

namespace LightDeck.App;

internal sealed class RackLibraryDialog : DeckDialog
{
    private readonly RackSnapshotService _library;
    private readonly ListBox _list;
    private readonly TextBox _details;
    private readonly Button _switch, _remove, _rename;
    private bool _busy;
    internal RackLibraryDialog(Window owner, DeckTheme theme, RackSnapshotService library)
        : base(owner, theme, "机架管理", "只运行当前机架。切换会保存当前插件参数并停止音频；其他机架保留在工程内。保存工程文件后，这些机架也会一并保存。", 920)
    {
        _library = library; UseEditorLayout(); AutomationProperties.SetAutomationId(this, "rack-library-dialog");
        _list = new ListBox { Height = 150, DisplayMemberPath = nameof(RackItem.Label), Background = theme.Surface(theme.Panel), Foreground = DeckTheme.Brush(theme.Text), BorderBrush = DeckTheme.Brush(theme.Line) };
        AutomationProperties.SetAutomationId(_list, "rack-library-list"); Body.Children.Add(_list);
        _details = new TextBox { Height = 250, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = theme.Surface(theme.Panel), Foreground = DeckTheme.Brush(theme.Text), Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(10) };
        AutomationProperties.SetAutomationId(_details, "rack-library-preview"); Body.Children.Add(_details);
        _rename = AddButton("重命名", Rename, id: "rack-library-rename");
        _remove = AddButton("移除所选", Remove, id: "rack-library-remove");
        AddButton("关闭", Close, id: "rack-library-close");
        _switch = AddButton("切换到所选…", Switch, true, "rack-library-switch");
        _list.SelectionChanged += (_, _) => Preview();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        RefreshList();
    }
    private RackSnapshotInfo? Selected => (_list.SelectedItem as RackItem)?.Info;
    private void RefreshList(Guid? selection = null)
    {
        var items = _library.GetRacks().Select(i => new RackItem(i)).ToList(); _list.ItemsSource = items;
        _list.SelectedItem = items.FirstOrDefault(i => i.Info.Id == selection) ?? items.FirstOrDefault(i => i.Info.IsActive); Preview();
    }
    private void Preview()
    {
        var selected = Selected; _switch.IsEnabled = _remove.IsEnabled = !_busy && selected is { IsActive: false }; _rename.IsEnabled = !_busy && selected is not null;
        if (selected is null) { _details.Text = ""; return; }
        var rack = _library.Preview(selected.Id);
        _details.Text = $"{selected.Name} · {selected.TrackCount} 条轨道 · {selected.PluginCount} 个插件\n" + string.Join("\n", rack.Tracks.Select(t => $"{t.Name} · {t.Kind} · {t.Inserts.Count} 插入 / {t.Sends.Count} 发送 · {(t.Visible ? "显示" : "隐藏")}")) + (rack.ImportNotes.Length == 0 ? "" : "\n\n" + rack.ImportNotes);
    }
    private void Switch()
    {
        if (Selected is not { IsActive: false } selected || _busy) return;
        var confirm = new RackActionConfirmation(this, Theme, "切换机架", $"切换到“{selected.Name}”？当前机架及插件参数将保留；声音和送入机架的播放器会停止。切换后核对音频 I/O，再手动开启。", "保留当前并切换");
        if (confirm.ShowDialog() == true) Run(() => _library.SwitchAsync(selected.Id), selected.Id);
    }
    private void Remove()
    {
        if (Selected is not { IsActive: false } selected || _busy) return;
        if (new RackActionConfirmation(this, Theme, "移除保存的机架", $"从当前工程移除“{selected.Name}”？不会删除源 .song 文件，也不会改变当前运行机架。", "移除机架").ShowDialog() == true)
            Run(() => _library.RemoveAsync(selected.Id));
    }
    private void Rename()
    {
        if (Selected is not { } selected || _busy) return;
        var dialog = new RackLibraryRenameDialog(this, Theme, selected.Name);
        if (dialog.ShowDialog() == true) Run(() => _library.RenameAsync(selected.Id, dialog.Result), selected.Id);
    }
    private async void Run(Func<Task> action, Guid? selection = null)
    {
        if (_busy) return; _busy = true; Error.Visibility = Visibility.Collapsed; _list.IsEnabled = false; Preview();
        try { await action(); } catch (Exception ex) { Invalid(ex.Message); }
        finally { _busy = false; _list.IsEnabled = true; try { RefreshList(selection); } catch (Exception ex) { Invalid(ex.Message); } }
    }
    private sealed record RackItem(RackSnapshotInfo Info) { public string Label => $"{(Info.IsActive ? "● 当前" : "○ 已保留")}  {Info.Name}    {Info.TrackCount} 轨道 · {Info.PluginCount} 插件"; }
}

internal sealed class RackActionConfirmation : DeckDialog
{
    internal RackActionConfirmation(Window owner, DeckTheme theme, string title, string message, string action) : base(owner, theme, title, message, 620)
    { AddButton("取消", Close); AddButton(action, () => DialogResult = true, true, "rack-confirm-action"); }
}

internal sealed class RackLibraryRenameDialog : DeckDialog
{
    private readonly TextBox _name;
    internal string Result => _name.Text.Trim();
    internal RackLibraryRenameDialog(Window owner, DeckTheme theme, string name) : base(owner, theme, "机架名称", "只修改机架名称，保持声音和路由参数。")
    {
        _name = Field("名称", name, "rack-rename-value"); _name.MaxLength = 80;
        AddButton("取消", Close); AddButton("应用修改", () => { try { RackSnapshotCatalog.ValidateName(Result); DialogResult = true; } catch (Exception ex) { Invalid(ex.Message); } }, true);
    }
}

