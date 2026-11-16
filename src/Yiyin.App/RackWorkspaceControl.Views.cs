using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Text.Json;
using LightDeck.Core;

namespace LightDeck.App;

public sealed partial class RackWorkspaceControl
{
    private void ToggleCompactView() => Run(async () => { var local = CopyLocal(_host.Local); local.CompactView = !local.CompactView; await _host.ApplyLocalAsync(local); });
    private void ShowViewMenu()
    {
        var guard = new RackDraftGuard(_host); var project = _host.Project.Copy(); project.EnsureDisplayViews();
        var menu = new ContextMenu();
        foreach (var view in project.DisplayViews)
        {
            var item = new MenuItem { Header = view.Name + " · " + view.TrackIds.Count + " 轨", IsCheckable = true, IsChecked = view.Id == project.ActiveDisplayViewId };
            AutomationProperties.SetAutomationId(item, "rack-view-select-" + view.Id);
            item.Click += (_, _) => Run(() => { guard.RequireCurrent(); return _host.ApplyDisplayViewsAsync(project.DisplayViews, view.Id); }); menu.Items.Add(item);
        }
        menu.Items.Add(new Separator()); var manage = new MenuItem { Header = "管理显示视图…" }; manage.Click += (_, _) => ShowVisibility(); menu.Items.Add(manage);
        _visibility.ContextMenu = menu; menu.PlacementTarget = this; menu.IsOpen = true;
    }
}

internal sealed class RackVisibilityDialog : DeckDialog
{
    private readonly RackProject _draft;
    private readonly ComboBox _views = new() { DisplayMemberPath = nameof(RackDisplayView.Name) };
    private readonly TextBox _name = new() { MaxLength = 80 };
    private readonly StackPanel _tracks = new();
    private readonly Button _remove;
    private bool _rendering, _saving;
    private RackDisplayView Selected => (RackDisplayView)_views.SelectedItem;
    public RackVisibilityDialog(Window owner, DeckTheme theme, IRackUiHost host)
        : base(owner, theme, "显示视图 · 轨道可见性", "演出可切换已保存视图。只改变显示，声音、插件和路由继续；应用后同步平板。", 570)
    {
        var guard = new RackDraftGuard(host); _draft = host.Project.Copy(); _draft.EnsureDisplayViews();
        Body.Children.Add(Label("选择显示视图")); AutomationProperties.SetAutomationId(_views, "rack-display-views"); Body.Children.Add(_views);
        Body.Children.Add(Label("视图名称")); AutomationProperties.SetAutomationId(_name, "rack-display-view-name"); Body.Children.Add(_name);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        var add = new Button { Content = "＋ 复制为新视图", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) }; actions.Children.Add(add);
        _remove = new Button { Content = "删除此视图", Padding = new Thickness(10, 6, 10, 6) }; actions.Children.Add(_remove); Body.Children.Add(actions);
        AutomationProperties.SetAutomationId(add, "rack-display-view-add"); AutomationProperties.SetAutomationId(_remove, "rack-display-view-delete");
        Body.Children.Add(new ScrollViewer { Content = _tracks, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _views.SelectionChanged += (_, _) => { if (!_rendering && _views.SelectedItem is RackDisplayView) RenderTracks(); };
        _name.TextChanged += (_, _) => { if (!_rendering && _views.SelectedItem is RackDisplayView view) view.Name = _name.Text; };
        _name.LostKeyboardFocus += (_, _) => _views.Items.Refresh();
        add.Click += (_, _) => { if (_draft.DisplayViews.Count >= 64) { Invalid("最多保存 64 个显示视图。"); return; } var view = new RackDisplayView { Name = "新显示视图", TrackIds = [.. Selected.TrackIds] }; _draft.DisplayViews.Add(view); RenderViews(view.Id); _name.Focus(); _name.SelectAll(); };
        _remove.Click += (_, _) => { if (_draft.DisplayViews.Count <= 1) return; _draft.DisplayViews.Remove(Selected); RenderViews(_draft.DisplayViews[0].Id); };
        AddButton("取消", Close, false, "rack-display-view-cancel");
        Button apply = null!; apply = AddButton("应用并关闭", async () => { await ApplyAsync(); }, true, "rack-visibility-close");
        async Task<bool> ApplyAsync()
        {
            if (_saving) return false;
            try { guard.RequireCurrent(); _draft.ApplyDisplayView(Selected.Id); _draft.Validate(); _saving = true; Body.IsEnabled = apply.IsEnabled = false; await host.ApplyDisplayViewsAsync(_draft.DisplayViews, Selected.Id); _saving = false; DialogResult = true; return true; }
            catch (Exception ex) { Invalid("显示视图未保存：" + ex.Message); return false; }
            finally { _saving = false; Body.IsEnabled = apply.IsEnabled = true; }
        }
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
        RenderViews(_draft.ActiveDisplayViewId!.Value);
        string State() => JsonSerializer.Serialize(new { Views = _draft.DisplayViews, Selected = Selected.Id });
        var baseline = State(); var registration = WorkspaceDraftRegistry.Register(this, "机架显示视图", () => _saving || State() != baseline, ApplyAsync, Close); Closed += (_, _) => registration.Dispose();
    }
    private void RenderViews(Guid selected)
    {
        _rendering = true; _views.ItemsSource = null; _views.ItemsSource = _draft.DisplayViews; _views.SelectedItem = _draft.DisplayViews.First(v => v.Id == selected); _rendering = false; RenderTracks();
    }
    private void RenderTracks()
    {
        _rendering = true; _name.Text = Selected.Name; _tracks.Children.Clear(); _remove.IsEnabled = _draft.DisplayViews.Count > 1;
        foreach (var track in _draft.Tracks)
        {
            var view = Selected; var check = new CheckBox { Content = track.Name + " · " + RackWorkspaceControl.KindLabel(track.Kind), IsChecked = view.TrackIds.Contains(track.Id), Margin = new Thickness(0, 6, 0, 6) };
            AutomationProperties.SetAutomationId(check, "rack-visible-" + track.Id); check.Click += (_, _) => { if (check.IsChecked == true) { if (!view.TrackIds.Contains(track.Id)) view.TrackIds.Add(track.Id); } else view.TrackIds.Remove(track.Id); }; _tracks.Children.Add(check);
        }
        _rendering = false;
    }
}
