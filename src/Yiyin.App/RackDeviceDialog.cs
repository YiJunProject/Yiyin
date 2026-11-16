using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LightDeck.Core;
using Microsoft.Win32;

namespace LightDeck.App;

internal sealed class RackPluginManagerDialog : DeckDialog
{
    private readonly IRackUiHost _host;
    private readonly RackLocalSettings _draft;
    private readonly HashSet<string> _removedRegistrations = new(StringComparer.Ordinal);
    private readonly ListBox _directories = new() { MinHeight = 85, MaxHeight = 145 };
    private readonly ListBox _catalog = new() { MinHeight = 140, MaxHeight = 260 };
    private readonly TextBlock _progress;
    private readonly RackPluginFilter _filter;
    private CancellationTokenSource? _scan;
    private bool _busy, _observedRunningScan;
    private string _savedDirectories = "";
    private readonly List<Button> _scanActions = [];
    private readonly Button _save;
    public RackPluginManagerDialog(Window owner, DeckTheme theme, IRackUiHost host) : base(owner, theme, "插件扫描与管理", "扫描会先保存目录；移除登记在保存管理设置后生效，关闭会放弃未保存的移除。不删除磁盘插件文件。", 700)
    {
        _host = host; _draft = RackWorkspaceControl.CopyLocal(host.Local);
        RackWorkspaceControl.ThemeList(_directories, theme); RackWorkspaceControl.ThemeList(_catalog, theme);
        Body.Children.Add(Label("扫描目录")); Body.Children.Add(_directories); var actions = new WrapPanel();
        AddAction("添加目录", () => { var picker = new OpenFolderDialog { Title = "选择插件扫描目录" }; if (picker.ShowDialog(this) == true && !_draft.ScanDirectories.Contains(picker.FolderName, StringComparer.OrdinalIgnoreCase)) { _draft.ScanDirectories.Add(picker.FolderName); Refresh(); } });
        AddAction("移除目录", () => { if (_directories.SelectedItem is string path) { _draft.ScanDirectories.Remove(path); Refresh(); } });
        AddAction("检查变化", () => _ = ScanAsync(true)); AddAction("完整扫描", () => _ = ScanAsync(false)); AddAction("取消扫描", () => _scan?.Cancel()); Body.Children.Add(actions);
        _progress = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = DeckTheme.Brush(theme.Muted) }; AutomationProperties.SetAutomationId(_progress, "rack-plugin-scan-progress"); Body.Children.Add(_progress);
        Body.Children.Add(Label("本机插件")); _filter = new RackPluginFilter(theme, "rack-plugin-manager"); _filter.Changed += FilterCatalog; Body.Children.Add(_filter); AutomationProperties.SetAutomationId(_catalog, "rack-plugin-manager-list"); Body.Children.Add(_catalog);
        var remove = new Button { Content = "移除所选插件登记", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), IsEnabled = host.IsEditing };
        AutomationProperties.SetAutomationId(remove, "rack-plugin-registration-remove");
        remove.Click += (_, _) =>
        {
            if (_catalog.SelectedItem is not PluginEntry entry) return;
            if (_host.IsPluginReferenced(entry.Value.Identity.Key)) { Invalid("当前或已保存机架仍在使用这个插件，请先移除对应轨道中的效果器。"); return; }
            _removedRegistrations.Add(entry.Value.Identity.Key); _draft.PluginCatalog.RemoveAll(p => p.Identity.Key == entry.Value.Identity.Key); _draft.PluginLocations.Remove(entry.Value.Identity.Key); Refresh();
        }; Body.Children.Add(remove);
        AddButton("关闭", Close, false, "rack-plugin-manager-close"); _save = AddButton("保存管理设置", () => _ = SaveAsync(), true, "rack-plugin-manager-save");
        _host.Changed += ScanChanged; Closed += (_, _) => _host.Changed -= ScanChanged;
        Closing += (_, e) => { if (_busy) { _scan?.Cancel(); e.Cancel = true; _progress.Text = "正在取消扫描，请稍候…"; } }; Refresh(); ScanChanged();
        _savedDirectories = JsonSerializer.Serialize(_draft.ScanDirectories); var registration = WorkspaceDraftRegistry.Register(this, "插件扫描与管理", () => _busy || _removedRegistrations.Count > 0 || JsonSerializer.Serialize(_draft.ScanDirectories) != _savedDirectories, SaveAsync, Close); Closed += (_, _) => registration.Dispose();
        void AddAction(string name, Action action) { var b = new Button { Content = name, Margin = new Thickness(0, 8, 6, 0), Padding = new Thickness(10, 5, 10, 5) }; AutomationProperties.SetAutomationId(b, name switch { "检查变化" => "rack-plugin-scan-changes", "完整扫描" => "rack-plugin-scan-full", "取消扫描" => "rack-plugin-scan-cancel", _ => "rack-plugin-scan-directory-" + _scanActions.Count }); b.Click += (_, _) => { if (!_busy || name == "取消扫描") action(); }; _scanActions.Add(b); actions.Children.Add(b); }
    }
    private void ScanChanged()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(ScanChanged); return; }
        var scanning = _busy || _host.Status.ScanProgress?.IsRunning == true;
        _save.IsEnabled = _host.IsEditing && !scanning;
        foreach (var button in _scanActions) button.IsEnabled = button.Content?.ToString() == "取消扫描" ? _scan is not null : _host.IsEditing && !scanning;
        if (_host.Status.ScanProgress is { IsRunning: true } progress)
        {
            _observedRunningScan = true;
            _progress.Text = $"{progress.Phase} · {progress.Completed} / {progress.Total}" + (progress.Deferred ? " · 后台等待" : "");
        }
        else if (_observedRunningScan && !_busy)
        {
            _observedRunningScan = false;
            // A manager opened during the automatic scan must display the new
            // inventory without overwriting unsaved directory/removal choices.
            if (JsonSerializer.Serialize(_draft.ScanDirectories) == _savedDirectories)
            {
                _draft.ScanDirectories = [.. _host.Local.ScanDirectories];
                _savedDirectories = JsonSerializer.Serialize(_draft.ScanDirectories);
            }
            _draft.PluginCatalog = _host.Local.PluginCatalog.Where(p => !_removedRegistrations.Contains(p.Identity.Key)).ToList();
            _draft.PluginLocations = _host.Local.PluginLocations.Where(p => !_removedRegistrations.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            _progress.Text = _host.Status.ScanProgress?.Phase == "完成" ? "扫描完成，共 " + _host.Local.PluginCatalog.Count + " 个登记项。" : "扫描" + _host.Status.ScanProgress?.Phase;
            Refresh();
        }
    }
    private void Refresh()
    {
        _directories.ItemsSource = _draft.ScanDirectories.ToArray(); _filter.SetPlugins(_draft.PluginCatalog.Select(p => p.Identity)); FilterCatalog();
    }
    private void FilterCatalog()
    {
        var selected = (_catalog.SelectedItem as PluginEntry)?.Value.Identity.Key; var visible = _draft.PluginCatalog.Where(p => _filter.Matches(p.Identity)).Select(p => new PluginEntry(p)).ToArray(); _catalog.ItemsSource = visible; _catalog.SelectedItem = visible.FirstOrDefault(p => p.Value.Identity.Key == selected);
    }
    private async Task ScanAsync(bool incremental)
    {
        if (_busy || _host.Status.ScanProgress?.IsRunning == true) return; _busy = true; _scan = new(); _progress.Text = incremental ? "正在检查插件文件变化…" : "正在完整扫描插件…"; ScanChanged();
        try
        {
            await _host.ApplyPluginManagementAsync(_draft, []); _savedDirectories = JsonSerializer.Serialize(_draft.ScanDirectories); if (incremental) await _host.CheckPluginChangesAsync(_scan.Token); else await _host.ScanPluginsAsync(_scan.Token);
            _draft.PluginCatalog = _host.Local.PluginCatalog.Where(p => !_removedRegistrations.Contains(p.Identity.Key)).ToList(); _draft.PluginLocations = _host.Local.PluginLocations.Where(p => !_removedRegistrations.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value); _progress.Text = "扫描完成，共 " + _host.Local.PluginCatalog.Count + " 个登记项。"; Refresh();
        }
        catch (OperationCanceledException) { _progress.Text = "扫描已取消。"; }
        catch (Exception ex) { Invalid("扫描未完成：" + ex.Message); }
        finally { _busy = false; _scan.Dispose(); _scan = null; ScanChanged(); }
    }
    private async Task<bool> SaveAsync()
    {
        if (_busy || _host.Status.ScanProgress?.IsRunning == true) return false; _busy = true;
        try { await _host.ApplyPluginManagementAsync(_draft, _removedRegistrations); _savedDirectories = JsonSerializer.Serialize(_draft.ScanDirectories); _removedRegistrations.Clear(); _busy = false; DialogResult = true; return true; }
        catch (Exception ex) { Invalid("插件设置未保存：" + ex.Message); return false; }
        finally { _busy = false; }
    }
    private sealed record PluginEntry(RackPluginCatalogEntry Value)
    {
        public override string ToString() => $"{Value.Identity.Name} · {Value.Identity.Format} · {Value.Identity.Vendor}" + (Value.Available ? "" : " · 不可用：" + Value.Error);
    }
}
