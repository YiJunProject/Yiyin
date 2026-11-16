using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LightDeck.Core;

namespace LightDeck.App;

public sealed partial class RackWorkspaceControl
{
    private void BuildInspector()
    {
        _inspector.Children.Clear(); _sendControls.Clear();
        foreach (var buttons in _soloButtons.Values) if (buttons.Count > 1) buttons.RemoveRange(1, buttons.Count - 1);
        var track = _host.Project.SelectedTrackId is { } id ? _host.Project.FindTrack(id) : null;
        if (track is null) { var empty = Label("选择一条轨道，查看输入、效果和发送。", 13, true); empty.TextWrapping = TextWrapping.Wrap; _inspector.Children.Add(empty); return; }
        var title = Label(track.Name, 16); title.FontWeight = FontWeights.SemiBold; title.TextWrapping = TextWrapping.Wrap; title.Foreground = ColorText(track.Color);
        _inspector.Children.Add(new Border { Background = DeckTheme.Brush(track.Color), Padding = new Thickness(10), CornerRadius = new CornerRadius(4), Child = title, Margin = new Thickness(0, 0, 0, 12) });
        void Field(string label, FrameworkElement control)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) }; row.ColumnDefinitions.Add(new() { Width = new GridLength(65) }); row.ColumnDefinitions.Add(new());
            row.Children.Add(Label(label, 12)); Grid.SetColumn(control, 1); row.Children.Add(control); _inspector.Children.Add(row);
        }
        var name = new TextBox { Text = track.Name, IsReadOnly = !_host.IsEditing, Padding = new Thickness(6, 4, 6, 4), MinWidth = 0 };
        AutomationProperties.SetAutomationId(name, "rack-inspector-name");
        void SaveName() { var value = name.Text.Trim(); if (_host.IsEditing && value.Length > 0 && value != track.Name) Run(() => _host.RenameTrackAsync(track.Id, value)); }
        name.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { SaveName(); e.Handled = true; } else if (e.Key == System.Windows.Input.Key.Escape) { name.Text = track.Name; e.Handled = true; } };
        name.LostKeyboardFocus += (_, _) => SaveName(); Field("轨道", name);
        Field("类型", Label(KindLabel(track.Kind), 12, true));
        Field(track.Kind == RackTrackKind.Output ? "硬件" : "输入源", BuildTrackSource(track));
        if (track.Kind != RackTrackKind.Output)
        {
            var choices = _host.Project.Tracks.Where(t => t.Id != track.Id && t.Kind is RackTrackKind.Bus or RackTrackKind.FX or RackTrackKind.Output).Select(t => new RackRouteChoice(t.Id, t.Name));
            Field("主输出", RouteChoice("", "rack-inspector-main", track.OutputTrackId, choices, target => Run(() => MutateProjectAsync(p => p.FindTrack(track.Id)!.OutputTrackId = target))));
        }
        var color = Button("颜色  ▾", "rack-track-edit", () => EditTrack(_host.Project.FindTrack(track.Id)));
        color.IsEnabled = _host.IsEditing; color.Background = DeckTheme.Brush(track.Color); color.Foreground = ColorText(track.Color); Field("轨道颜色", color);
        _inspector.Children.Add(SoloMuteButtons(track, true));
        var legend = Label("M 静音    S 独听", 11, true); legend.Margin = new Thickness(0, 0, 0, 14); _inspector.Children.Add(legend);
        _inspector.Children.Add(new Border { Height = 1, Background = DeckTheme.Brush(_theme.Line), Margin = new Thickness(0, 4, 0, 6) });
        InspectorHeading("路由示意");
        void Tile(string text, string color)
        {
            var label = Label(text, 13); label.TextWrapping = TextWrapping.Wrap; label.HorizontalAlignment = HorizontalAlignment.Center;
            _inspector.Children.Add(new Border { Child = label, Padding = new Thickness(8), BorderBrush = DeckTheme.Brush(color), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Margin = new Thickness(18, 0, 18, 0) });
        }
        if (track.Kind is RackTrackKind.Mono or RackTrackKind.Stereo)
        {
            Tile(_host.Project.InputPorts.FirstOrDefault(p => p.Id == track.InputPortId)?.Name ?? "未分配输入", _theme.Line);
            var arrow = Label("↓", 20, true); arrow.HorizontalAlignment = HorizontalAlignment.Center; _inspector.Children.Add(arrow);
        }
        var seen = new HashSet<Guid>(); var node = track;
        while (node is not null && seen.Add(node.Id))
        {
            Tile(node.Name, node.Color);
            if (node.OutputTrackId is not { } next) break;
            var arrow = Label("↓", 20, true); arrow.HorizontalAlignment = HorizontalAlignment.Center; _inspector.Children.Add(arrow);
            node = _host.Project.FindTrack(next);
        }
        if (_host.IsEditing)
        {
            var actions = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
            void Action(string text, string key, Action action) { var button = Button(text, "rack-track-" + key, action); button.Padding = new Thickness(6, 3, 6, 3); button.Margin = new Thickness(0, 0, 4, 4); actions.Children.Add(button); }
            Action("复制", "copy", () => Run(() => CopyTrackAsync(track.Id)));
            Action("左移", "left", () => Run(() => MoveTrackAsync(track.Id, -1))); Action("右移", "right", () => Run(() => MoveTrackAsync(track.Id, 1)));
            Action("删除", "delete", () => Run(() => DeleteTrackAsync(track.Id))); _inspector.Children.Add(actions);
        }
    }

    private void InspectorHeading(string text) { var label = Label(text, 15); label.FontWeight = FontWeights.SemiBold; label.Margin = new Thickness(0, 20, 0, 10); _inspector.Children.Add(label); }
    private Task MutateProjectAsync(Action<RackProject> edit)
    {
        if (!_host.IsEditing) throw new InvalidOperationException("演播模式禁止结构编辑。");
        var draft = _host.Project.Copy(); edit(draft); draft.Validate(); return _host.ApplyProjectAsync(draft);
    }
    private void EditTrack(RackTrack? track, RackTrackKind kind = RackTrackKind.Mono)
    {
        if (!_host.IsEditing || _busy) return;
        var guard = new RackDraftGuard(_host); var dialog = new RackTrackDialog(_owner, _theme, track ?? RackTrack.Create(kind, "新轨道"), track is null);
        if (dialog.ShowDialog() != true) return;
        Run(() => { guard.RequireCurrent(); return MutateProjectAsync(project => { if (track is null) project.Tracks.Add(dialog.Result); else { var index = project.Tracks.FindIndex(t => t.Id == track.Id); if (index < 0) throw new InvalidOperationException("轨道已删除。"); project.Tracks[index] = dialog.Result; } project.SelectedTrackId = dialog.Result.Id; }); });
    }
    private void RenameTrack(Guid id)
    {
        if (_disposed || _busy || !_host.IsEditing || _host.Project.FindTrack(id) is not { } track) return;
        var guard = new RackDraftGuard(_host); new RackRenameDialog(_owner, _theme, track.Name, name => { guard.RequireCurrent(); return _host.RenameTrackAsync(id, name); }).ShowDialog(); Refresh();
    }
    private Task CopyTrackAsync(Guid id) => _host.CopyTrackAsync(id);
    private Task MoveTrackAsync(Guid id, int direction) => _host.ReorderTrackAsync(id, Math.Clamp(_host.Project.Tracks.FindIndex(t => t.Id == id) + direction, 0, _host.Project.Tracks.Count - 1));
    private async Task DeleteTrackAsync(Guid id)
    {
        var guard = new RackDraftGuard(_host); var track = _host.Project.FindTrack(id); if (track is null) return;
        if (ThemedMessageBox.Show(_owner, "删除「" + track.Name + "」及它的效果器？其他轨道通往它的主输出和发送也会移除。", "删除机架轨道", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        guard.RequireCurrent(); await MutateProjectAsync(p => p.RemoveTrack(id));
    }
    private void AddPlugin(Guid trackId)
    {
        if (!_host.IsEditing || _busy) return;
        Run(async () => { await _host.SelectTrackAsync(trackId); _showLibrary = true; _librarySignature = ""; BuildLibrary(); RefreshSidePanel(); });
    }
    private void EditPlugin(Guid trackId, Guid pluginId)
    {
        if (_busy) return;
        var plugin = _host.Project.FindTrack(trackId)?.Inserts.FirstOrDefault(p => p.Id == pluginId); if (plugin is null) return;
        var guard = new RackDraftGuard(_host); var dialog = new RackPluginSettingsDialog(_owner, _theme, plugin, _host.IsEditing);
        if (dialog.ShowDialog() != true) return;
        Run(async () =>
        {
            guard.RequireCurrent();
            if (dialog.Remove) { await MutateProjectAsync(project => project.FindTrack(trackId)!.Inserts.RemoveAll(p => p.Id == pluginId)); return; }
            var track = _host.Project.FindTrack(trackId)?.Copy(); var current = track?.Inserts.FirstOrDefault(p => p.Id == pluginId); if (track is null || current is null) return;
            current.Wet = dialog.Result.Wet; current.Bypass = dialog.Result.Bypass; await _host.UpdateTrackAsync(track);
            guard.RequireCurrent(); var index = _host.Project.FindTrack(trackId)?.Inserts.FindIndex(p => p.Id == pluginId) ?? -1;
            if (index >= 0 && dialog.Move != 0) await _host.TransferInsertAsync(trackId, pluginId, trackId, Math.Clamp(index + dialog.Move, 0, track.Inserts.Count - 1));
        });
    }
    private void ShowRouting() => ShowRouting(_host.Project.SelectedTrackId);
    private void ShowRouting(Guid? id)
    {
        if (!_host.IsEditing || _busy) return;
        var dialog = new RackRoutingDialog(_owner, _theme, _host, id); dialog.ShowDialog(); Refresh();
    }
    private async Task ShowDevicesAsync()
    {
        if (!_host.IsEditing) return;
        var guard = new RackDraftGuard(_host); var devices = await _host.EnumerateDevicesAsync(); if (_disposed) return; guard.RequireCurrent();
        var dialog = new RackDeviceDialog(_owner, _theme, _host, devices); dialog.ShowDialog(); _signature = "";
    }
    private void ShowPluginManager() { if (_busy || !_host.IsEditing) return; new RackPluginManagerDialog(_owner, _theme, _host).ShowDialog(); Refresh(); }
}

internal sealed class RackRenameDialog : DeckDialog
{
    private bool _saving;
    public RackRenameDialog(Window owner, DeckTheme theme, string current, Func<string, Task> save) : base(owner, theme, "重命名机架轨道", "只修改工程中的轨道名称，机架继续运行。", 480)
    {
        var name = Field("轨道名称", current, "rack-rename-name"); name.MaxLength = 200;
        AddButton("取消", Close); Button apply = null!; apply = AddButton("保存名称", () => _ = SaveAsync(), true, "rack-rename-save");
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        async Task SaveAsync()
        {
            if (_saving) return;
            if (string.IsNullOrWhiteSpace(name.Text)) { Invalid("请输入轨道名称。"); return; }
            _saving = true; apply.IsEnabled = false;
            try { await save(name.Text.Trim()); _saving = false; DialogResult = true; }
            catch (Exception ex) { Invalid("名称未保存：" + ex.Message); }
            finally { _saving = false; apply.IsEnabled = true; }
        }
    }
}

internal sealed class RackTrackDialog : DeckDialog
{
    public RackTrack Result { get; private set; }
    public RackTrackDialog(Window owner, DeckTheme theme, RackTrack source, bool create) : base(owner, theme, create ? "添加机架轨道" : "编辑机架轨道", "轨道名称、类型与颜色保存在工程。输入和输出设备只保存在本机。", 500)
    {
        Result = source.Copy(); var name = Field("轨道名称", source.Name, "rack-track-name"); name.MaxLength = 200;
        Body.Children.Add(Label("轨道类型")); var kind = new ComboBox { ItemsSource = Enum.GetValues<RackTrackKind>().Where(k => k != RackTrackKind.Playback).Select(k => new RackKindChoice(k, RackWorkspaceControl.KindLabel(k))).ToArray(), DisplayMemberPath = nameof(RackKindChoice.Label), SelectedValuePath = nameof(RackKindChoice.Kind), SelectedValue = source.Kind, IsEnabled = create }; AutomationProperties.SetAutomationId(kind, "rack-track-kind"); Body.Children.Add(kind);
        Body.Children.Add(Label("颜色")); var color = new HsbColorPicker(theme, source.Color); Body.Children.Add(color);
        AddButton("取消", Close); AddButton("保存", () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || !color.IsValid) { Invalid("请输入名称与有效颜色。"); return; }
            var selected = (RackTrackKind)kind.SelectedValue; Result.Name = name.Text.Trim(); Result.Kind = selected; Result.Channels = create ? selected == RackTrackKind.Mono ? 1 : 2 : source.Channels; Result.Color = color.ColorHex;
            DialogResult = true;
        }, true, "rack-track-save");
    }
    private sealed record RackKindChoice(RackTrackKind Kind, string Label);
}
internal sealed class RackPluginChoiceDialog : DeckDialog
{
    public RackPluginIdentity? Selected { get; private set; }
    public RackPluginChoiceDialog(Window owner, DeckTheme theme, IReadOnlyList<RackPluginIdentity> plugins) : base(owner, theme, "添加效果器", "从本机已扫描的插件中选择。缺少插件时先打开插件管理并扫描。", 560)
    {
        var filter = new RackPluginFilter(theme, "rack-plugin-choice"); filter.SetPlugins(plugins); Body.Children.Add(filter);
        var list = new ListBox { ItemsSource = plugins, DisplayMemberPath = nameof(RackPluginIdentity.Name), MinHeight = 180, MaxHeight = 380 }; RackWorkspaceControl.ThemeList(list, theme); AutomationProperties.SetAutomationId(list, "rack-plugin-choice"); Body.Children.Add(list);
        var count = new TextBlock { Text = $"显示 {plugins.Count} 个插件", Foreground = DeckTheme.Brush(theme.Muted), Margin = new Thickness(0, 6, 0, 0) }; Body.Children.Add(count);
        filter.Changed += () => { var selected = list.SelectedItem as RackPluginIdentity; var filtered = plugins.Where(filter.Matches).ToArray(); list.ItemsSource = filtered; list.SelectedItem = filtered.FirstOrDefault(p => p.Key == selected?.Key); count.Text = $"显示 {filtered.Length} / {plugins.Count} 个插件"; };
        AddButton("取消", Close); AddButton("添加", () => { if (list.SelectedItem is not RackPluginIdentity selected) { Invalid("请选择一个插件。"); return; } Selected = selected; DialogResult = true; }, true, "rack-plugin-add");
    }
}
internal sealed class RackPluginSettingsDialog : DeckDialog
{
    public RackPluginInstance Result { get; }
    public bool Remove { get; private set; }
    public int Move { get; private set; }
    public RackPluginSettingsDialog(Window owner, DeckTheme theme, RackPluginInstance plugin, bool allowStructure = true) : base(owner, theme, plugin.Identity.Name, allowStructure ? "干湿比与旁通保存在工程，顺序决定效果器处理顺序。" : "演播可调整干湿比与旁通；添加、移除和排序请进入编辑。", 480)
    {
        Result = plugin.Copy(); var wet = Field("湿声比例 0–100%", (plugin.Wet * 100).ToString("0.#", CultureInfo.InvariantCulture), "rack-plugin-wet");
        var bypass = new CheckBox { Content = "旁通效果器", IsChecked = plugin.Bypass, Margin = new Thickness(0, 12, 0, 0) }; AutomationProperties.SetAutomationId(bypass, "rack-plugin-setting-bypass"); Body.Children.Add(bypass);
        var order = new ComboBox { ItemsSource = new[] { "保留顺序", "向前一位", "向后一位" }, SelectedIndex = 0, Visibility = allowStructure ? Visibility.Visible : Visibility.Collapsed }; AutomationProperties.SetAutomationId(order, "rack-plugin-order"); if (allowStructure) Body.Children.Add(Label("顺序")); Body.Children.Add(order);
        var remove = new CheckBox { Content = "移除此效果器", Margin = new Thickness(0, 14, 0, 0), Visibility = allowStructure ? Visibility.Visible : Visibility.Collapsed }; AutomationProperties.SetAutomationId(remove, "rack-plugin-remove"); Body.Children.Add(remove);
        AddButton("取消", Close); AddButton("应用", () => { if (!double.TryParse(wet.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value is < 0 or > 100) { Invalid("干湿比需为 0–100%。"); return; } Result.Wet = value / 100; Result.Bypass = bypass.IsChecked == true; Remove = remove.IsChecked == true; Move = order.SelectedIndex == 1 ? -1 : order.SelectedIndex == 2 ? 1 : 0; DialogResult = true; }, true, "rack-plugin-apply");
    }
}
