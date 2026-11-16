using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using LightDeck.Core;

namespace LightDeck.App;

public sealed partial class RackWorkspaceControl
{
    private DockPanel Module(string title, string id, UIElement content, Action? add = null, bool expanded = false, RackModule? kind = null, Guid trackId = default)
    {
        var box = new DockPanel { Margin = new Thickness(0, 2, 0, 2), LastChildFill = true, ClipToBounds = true };
        var head = new Grid { Height = RackRowAppearance.HeaderHeight, ClipToBounds = true };
        if (kind is RackModule.Send or RackModule.Output)
        {
            var handle = ModuleHandle(kind.Value, trackId); handle.Margin = new Thickness(0); handle.Height = RackRowAppearance.HeaderHeight;
            head.Children.Add(handle);
        }
        var caption = new DockPanel { IsHitTestVisible = false }; head.Children.Add(caption);
        var heading = Label(title, 13); heading.Margin = new Thickness(26, 0, 26, 0); heading.TextAlignment = TextAlignment.Center; caption.Children.Add(heading);
        var fold = Button("▾", id + "-toggle", () => { if (kind is { } section) ToggleModule(section.ToString().ToLowerInvariant()); });
        fold.Width = 22; fold.MinHeight = 22; fold.Height = RackRowAppearance.HeaderHeight; fold.Padding = new Thickness(0); fold.HorizontalAlignment = HorizontalAlignment.Left;
        fold.BorderThickness = new Thickness(0); fold.Background = Brushes.Transparent; head.Children.Add(fold);
        if (add is not null && _host.IsEditing)
        {
            var button = Button("＋", id + "-add", add); button.MinHeight = button.Height = 23; button.Width = 26; button.Padding = new Thickness(0);
            button.HorizontalAlignment = HorizontalAlignment.Right; button.ToolTip = "添加" + title; head.Children.Add(button);
        }
        DockPanel.SetDock(head, Dock.Top); box.Children.Add(head);
        var scroll = new ScrollViewer { Content = content, CanContentScroll = true, VerticalScrollBarVisibility = expanded ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = expanded ? Brushes.Transparent : _theme.Light ? new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(36, 0, 0, 0)) };
        AutomationProperties.SetAutomationId(scroll, id); box.Children.Add(scroll);
        if (kind is { } k)
        {
            void VisibilityChanged() { var shown = ModuleVisible(k.ToString().ToLowerInvariant()); var visibility = shown ? Visibility.Visible : Visibility.Collapsed; if (scroll.Visibility != visibility) scroll.Visibility = visibility; var caption = shown ? "▾" : "▸"; if (!Equals(fold.Content, caption)) fold.Content = caption; }
            box.LayoutUpdated += (_, _) => VisibilityChanged(); VisibilityChanged();
        }
        return box;
    }
    private FrameworkElement BuildInsertArea(RackTrack track)
    {
        var rows = new StackPanel();
        for (var i = 0; i < track.Inserts.Count; i++)
        {
            var plugin = track.Inserts[i]; var button = Button(plugin.Identity.Name, "rack-insert-" + plugin.Id, () => OpenPlugin(track.Id, plugin.Id));
            var pitch = _host.Local.RowAppearance.InsertItemHeight; var name = RackCompactRow.Text(plugin.Identity.Name, pitch, DeckTheme.Brush(_theme.Text)); button.Content = name; button.MinHeight = WorkspaceUiDensity.IsTouch ? 44 : 27; button.Padding = new Thickness(5, 2, 5, 2); button.Margin = new Thickness(2, 1, 2, 2);
            button.Focusable = true; button.Cursor = System.Windows.Input.Cursors.Hand; _insertLabels[plugin.Id] = name; _insertButtons[plugin.Id] = button;
            button.PreviewMouseRightButtonDown += (_, e) => { e.Handled = true; ShowInsertMenu(button, track.Id, plugin.Id); };
            var power = Button("插件开关", "rack-slot-bypass-" + plugin.Id, () => Live(track.Id, t => { if (t.Inserts.FirstOrDefault(p => p.Id == plugin.Id) is { } current) current.Bypass = !current.Bypass; }));
            var slot = new RackCompactRow(pitch, DeckTheme.Brush(track.Color), _theme, button, power); _slotBypassControls[plugin.Id] = power;

            MakeDragSource(button, () => new RackDrag(_host, RackDragKind.Insert, track.Id, plugin.Id)); MakeDropTarget(button, track.Id, RackDropKind.Insert, i, true); rows.Children.Add(slot);
        }
        if (rows.Children.Count == 0) rows.Children.Add(Label(_host.IsEditing ? "拖入插件 / 点＋添加" : "无插入效果器", 10, true));
        var box = Module("插入", "rack-insert-area-" + track.Id, rows, () => AddPlugin(track.Id), kind: RackModule.Insert, trackId: track.Id);
        MakeDropTarget(box, track.Id, RackDropKind.Insert, track.Inserts.Count); return box;
    }
    private void UpdatePluginSlot(RackPluginInstance plugin, RackUiStatus status)
    {
        if (!_insertLabels.TryGetValue(plugin.Id, out var label) || !_insertButtons.TryGetValue(plugin.Id, out var button)) return;
        var error = status.PluginErrors?.GetValueOrDefault(plugin.Id);
        var catalog = _host.Local.PluginCatalog.FirstOrDefault(c => c.Identity.Key == plugin.Identity.Key);
        if (string.IsNullOrWhiteSpace(error) && catalog is { Available: false }) error = catalog.Error.Length > 0 ? catalog.Error : "插件不可用";
        if (string.IsNullOrWhiteSpace(error) && !_host.Local.PluginLocations.ContainsKey(plugin.Identity.Key)) error = "本机未登记插件位置";
        var bad = !string.IsNullOrWhiteSpace(error);
        var caption = (bad ? "不可用 · " : plugin.Bypass ? "旁通 · " : "") + plugin.Identity.Name;
        if (label.Text != caption) label.Text = caption;
        if (button.Tag is not bool prior || prior != bad)
        {
            button.Tag = bad; button.Background = DeckTheme.Brush(bad ? _theme.Light ? "#FAD9DE" : "#672A39" : _theme.Card); button.Foreground = DeckTheme.Brush(bad ? _theme.Light ? "#9E1932" : "#FFC8D1" : _theme.Text);
        }
        button.ToolTip = bad ? plugin.Identity.Name + "\n" + error : plugin.Identity.Name + "\n点击打开 / 前置插件窗口；右键操作" + (_host.IsEditing ? "；Ctrl 拖到机架空白处移除" : "");
        AutomationProperties.SetHelpText(button, button.ToolTip.ToString());
    }
    private void ShowInsertMenu(Button anchor, Guid trackId, Guid pluginId)
    {
        var plugin = _host.Project.FindTrack(trackId)?.Inserts.FirstOrDefault(p => p.Id == pluginId); if (plugin is null) return; var menu = new ContextMenu();
        void Item(string title, string action, Action callback) { var item = new MenuItem { Header = title }; AutomationProperties.SetAutomationId(item, "rack-insert-menu-" + action + "-" + pluginId); item.Click += (_, _) => callback(); menu.Items.Add(item); }
        Item("打开 / 前置插件窗口", "open", () => OpenPlugin(trackId, pluginId));
        Item(plugin.Bypass ? "启用插件" : "旁通插件", "bypass", () => Live(trackId, t => { if (t.Inserts.FirstOrDefault(p => p.Id == pluginId) is { } current) current.Bypass = !current.Bypass; }));
        Item("干湿 / 旁通设置", "settings", () => EditPlugin(trackId, pluginId));
        if (_host.IsEditing) Item("移除此插件", "remove", () => Run(() => MutateProjectAsync(p => p.FindTrack(trackId)!.Inserts.RemoveAll(i => i.Id == pluginId))));
        anchor.ContextMenu = menu; menu.PlacementTarget = anchor; menu.IsOpen = true;
    }
    private FrameworkElement BuildSendArea(RackTrack track) => BuildConnectionArea(track, false);
    private FrameworkElement BuildOutputArea(RackTrack track) => BuildConnectionArea(track, true);
    private FrameworkElement BuildConnectionArea(RackTrack track, bool output)
    {
        var rows = new StackPanel();
        bool Match(Guid target) => _host.Project.FindTrack(target) is { } t && (output ? t.Kind == RackTrackKind.Output : t.Kind is RackTrackKind.Bus or RackTrackKind.FX);
        if (output && track.OutputTrackId is { } main && _host.Project.FindTrack(main) is not null) rows.Children.Add(ConnectionRow(track, null, main, track.MainOutputGainDb, track.MainOutputEnabled));
        for (var i = 0; i < track.Sends.Count; i++)
        {
            var send = track.Sends[i]; if (!Match(send.TargetTrackId)) continue;
            var row = ConnectionRow(track, send, send.TargetTrackId, send.GainDb, send.Enabled); MakeDropTarget(row, track.Id, output ? RackDropKind.Output : RackDropKind.Send, i, true); rows.Children.Add(row);
        }
        if (rows.Children.Count == 0)
        {
            var summary = track.Kind == RackTrackKind.Output ? output ? PhysicalChannels(track.Id, false) : "硬件输出不回送" : output && track.OutputTrackId is { } parent && _host.Project.FindTrack(parent) is { Kind: RackTrackKind.Bus or RackTrackKind.FX } target ? "经 " + target.Name + " 输出" : output ? "无直接输出" : "无 Bus / FX 发送";
            var empty = Label(summary, 10, true); empty.TextWrapping = TextWrapping.Wrap; empty.Margin = new Thickness(4, 5, 4, 0); rows.Children.Add(empty);
        }
        var id = output ? "rack-output-area-" : "rack-send-area-";
        var box = Module(output ? track.Kind == RackTrackKind.Output ? "硬件输出" : "输出" : "发送", id + track.Id, rows, track.Kind == RackTrackKind.Output ? null : () => ShowTargetMenu(track.Id, output), kind: output ? RackModule.Output : RackModule.Send, trackId: track.Id);
        if (track.Kind != RackTrackKind.Output) MakeDropTarget(box, track.Id, output ? RackDropKind.Output : RackDropKind.Send, track.Sends.Count);
        return box;
    }
    private Border ConnectionRow(RackTrack track, RackSend? send, Guid targetId, double gainValue, bool on)
    {
        var target = _host.Project.FindTrack(targetId)!; var main = send is null; var id = main ? track.Id : send!.Id;
        var prefix = main ? "rack-main-output-" : "rack-strip-send-";
        var compact = main || target.Kind == RackTrackKind.Output;
        var slotHeight = _host.Local.RowAppearance.Height(compact ? "output" : "send");
        Button? pre = null;
        if (send is not null)
        {
            pre = Button(send.PreFader ? "前" : "后", "rack-send-pre-" + id, () => Live(track.Id, t => { if (t.Sends.FirstOrDefault(s => s.Id == id) is { } current) current.PreFader = !current.PreFader; })); pre.Width = slotHeight; pre.MinHeight = pre.Height = slotHeight; pre.Padding = new Thickness(0); pre.FontSize = 10; pre.Margin = new Thickness(0, 0, 3, 0);  _preFaderControls[id] = pre;
        }
        var power = Button("连接开关", prefix + "enable-" + id, () => Live(track.Id, t => { if (main) t.MainOutputEnabled = !t.MainOutputEnabled; else if (t.Sends.FirstOrDefault(s => s.Id == id) is { } current) current.Enabled = !current.Enabled; })); power.Width = slotHeight; power.MinHeight = power.Height = slotHeight; power.Padding = new Thickness(compact ? 2 : 3); 
        var name = RackCompactRow.Text(target.Name, slotHeight, DeckTheme.Brush(_theme.Text)); name.Foreground = DeckTheme.Brush(_theme.Text); name.TextTrimming = TextTrimming.CharacterEllipsis;
        var targetButton = Button(target.Name, "rack-send-target-" + id, () => ShowTargetPlugins(targetId)); targetButton.Content = name; targetButton.MinHeight = targetButton.Height = slotHeight; targetButton.Padding = new Thickness(0); targetButton.Background = Brushes.Transparent; targetButton.BorderThickness = new Thickness(0); targetButton.ToolTip = (main ? "主去向 → " : "附加连接 → ") + target.Name + "\n点击调整目标已有插件；右键移除此连接"; 
        if (send is not null) MakeDragSource(name, () => new RackDrag(_host, RackDragKind.Send, track.Id, id));
        var db = RackCompactRow.Text($"{gainValue:0.0}", slotHeight, DeckTheme.Brush(_theme.Text)); db.Foreground = name.Foreground; db.TextAlignment = TextAlignment.Right;
        var number = EditableNumber(db, prefix + "number-" + id, () => { var current = _pending.GetValueOrDefault(track.Id) ?? _host.Project.FindTrack(track.Id); return main ? current?.MainOutputGainDb ?? 0 : current?.Sends.FirstOrDefault(s => s.Id == id)?.GainDb ?? 0; }, value => Live(track.Id, t => { if (main) t.MainOutputGainDb = value; else if (t.Sends.FirstOrDefault(s => s.Id == id) is { } current) current.GainDb = value; }));
        var gain = new Slider { Minimum = -90, Maximum = 12, Value = Math.Clamp(gainValue, -90, 12), SmallChange = .1, LargeChange = 1, Height = WorkspaceUiDensity.IsTouch ? 44 : 26, Margin = new Thickness(0, 0, 2, compact ? 0 : 2) }; AutomationProperties.SetAutomationId(gain, prefix + "gain-" + id);
        gain.ToolTip = target.Name + "连接增益 dB"; gain.ValueChanged += (_, _) => { if (!_syncing) Live(track.Id, t => { if (main) t.MainOutputGainDb = gain.Value; else if (t.Sends.FirstOrDefault(s => s.Id == id) is { } current) current.GainDb = gain.Value; }); }; gain.IsMoveToPointEnabled = true; gain.PreviewMouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { gain.Value = 0; e.Handled = true; } };
        var controls = new SendControls(track.Id, gain, db, power); if (main) { _outputControls[track.Id] = controls; _mainOutputControls[track.Id] = power; } else _stripSendControls[id] = controls;
        FrameworkElement prefixElement = pre is not null ? pre : RackCompactRow.Text("主", slotHeight, DeckTheme.Brush(_theme.Text));
        var frame = new RackCompactRow(slotHeight, DeckTheme.Brush(target.Color), _theme, targetButton, power, prefixElement, number, gain); AutomationProperties.SetAutomationId(frame, "rack-connection-" + id);
        frame.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (!_host.IsEditing) return; e.Handled = true; var menu = new ContextMenu();
            var remove = new MenuItem { Header = "移除此连接" }; remove.Click += (_, _) => Run(() => MutateProjectAsync(p => { var t = p.FindTrack(track.Id)!; if (main) t.OutputTrackId = null; else t.Sends.RemoveAll(s => s.Id == id); })); menu.Items.Add(remove); frame.ContextMenu = menu; menu.PlacementTarget = frame; menu.IsOpen = true;
        }; return frame;
    }
    private void ShowTargetPlugins(Guid targetId)
    {
        var target = _host.Project.FindTrack(targetId); if (target is null) return; var menu = new ContextMenu();
        foreach (var plugin in target.Inserts)
        {
            var item = new MenuItem { Header = plugin.Identity.Name }; AutomationProperties.SetAutomationId(item, "rack-target-plugin-" + plugin.Id);
            var open = new MenuItem { Header = "打开 / 前置插件窗口" }; AutomationProperties.SetAutomationId(open, "rack-target-open-" + plugin.Id); open.Click += (_, _) => OpenPlugin(targetId, plugin.Id); item.Items.Add(open);
            var bypass = new MenuItem { Header = plugin.Bypass ? "启用插件" : "旁通插件" }; AutomationProperties.SetAutomationId(bypass, "rack-target-bypass-" + plugin.Id); bypass.Click += (_, _) => Live(targetId, t => { if (t.Inserts.FirstOrDefault(p => p.Id == plugin.Id) is { } current) current.Bypass = !current.Bypass; }); item.Items.Add(bypass);
            var settings = new MenuItem { Header = "干湿 / 旁通设置" }; settings.Click += (_, _) => EditPlugin(targetId, plugin.Id); item.Items.Add(settings); menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "目标轨道没有插入插件", IsEnabled = false });
        ContextMenu = menu; menu.PlacementTarget = this; menu.IsOpen = true;
    }
    private void UpdateMainOutputs()
    {
        foreach (var pair in _outputControls)
        {
            var track = _pending.GetValueOrDefault(pair.Key) ?? _host.Project.FindTrack(pair.Key); if (track is null) continue;
            if (!pair.Value.Gain.IsMouseCaptureWithin && !pair.Value.Gain.IsKeyboardFocusWithin) pair.Value.Gain.Value = track.MainOutputGainDb;
            pair.Value.Db.Text = $"{track.MainOutputGainDb:0.0}"; PaintPower(pair.Value.Enabled, track.MainOutputEnabled, "输出连接");
        }
    }
    private void ShowTargetMenu(Guid trackId, bool output)
    {
        if (!_host.IsEditing || _busy || _host.Project.FindTrack(trackId) is not { } source) return;
        var menu = new ContextMenu();
        var mainMenu = new MenuItem { Header = "更改主去向（替换）" }; var extraMenu = new MenuItem { Header = "增加附加输出（保留现有连接）" };
        if (output) { menu.Items.Add(mainMenu); menu.Items.Add(extraMenu); }
        foreach (var target in _host.Project.Tracks.Where(t => t.Id != trackId && (output ? t.Kind is RackTrackKind.Output or RackTrackKind.Bus : t.Kind is RackTrackKind.Bus or RackTrackKind.FX)))
        {
            void Add(bool main, ItemsControl parent)
            {
                void Apply(RackProject project) { var track = project.FindTrack(trackId)!; if (main) track.OutputTrackId = target.Id; else track.Sends.Add(new RackSend { TargetTrackId = target.Id }); }
                var enabled = source.OutputTrackId != target.Id && source.Sends.All(s => s.TargetTrackId != target.Id); var error = "";
                if (enabled) try { var proposed = _host.Project.Copy(); Apply(proposed); proposed.Validate(); } catch (Exception ex) { enabled = false; error = ex.Message; }
                var item = new MenuItem { Header = target.Name, IsEnabled = enabled, ToolTip = error.Length > 0 ? error : main ? "替换「" + (_host.Project.FindTrack(source.OutputTrackId ?? Guid.Empty)?.Name ?? "未分配") + "」，其他独立输出保持。" : "增加一条独立连接，保留主去向。" };
                AutomationProperties.SetAutomationId(item, (main ? "rack-main-target-" : "rack-add-target-") + target.Id); item.Click += (_, _) => Run(() => MutateProjectAsync(Apply)); parent.Items.Add(item);
            }
            if (output) { Add(true, mainMenu); if (target.Kind == RackTrackKind.Output) Add(false, extraMenu); } else Add(false, menu);
        }
        if (output) { mainMenu.IsEnabled = mainMenu.Items.Count > 0; extraMenu.IsEnabled = extraMenu.Items.Count > 0; }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "请先添加 Bus / FX 轨道", IsEnabled = false });
        ContextMenu = menu; menu.PlacementTarget = this; menu.IsOpen = true;
    }
    private FrameworkElement BuildTrackSource(RackTrack track)
    {
        var holder = new Border { Height = 40, Margin = new Thickness(0, 3, 0, 1) };
        if (_host.IsEditing && track.Kind is RackTrackKind.Mono or RackTrackKind.Stereo)
            holder.Child = RouteChoice("来源", "rack-input-port-" + track.Id, track.InputPortId, _host.Project.InputPorts.Where(p => p.Channels == track.Channels).Select(p => new RackRouteChoice(p.Id, p.Name)), id => Run(() => MutateProjectAsync(p => p.FindTrack(track.Id)!.InputPortId = id)));
        else if (_host.IsEditing && track.Kind == RackTrackKind.Output)
            holder.Child = RouteChoice("硬件", "rack-output-port-" + track.Id, track.HardwareOutputPortId, _host.Project.OutputPorts.Where(p => p.Channels == track.Channels).Select(p => new RackRouteChoice(p.Id, p.Name)), id => Run(() => MutateProjectAsync(p => p.FindTrack(track.Id)!.HardwareOutputPortId = id)));
        else { var text = Label(track.Kind switch { RackTrackKind.Playback => "来源 · 内部音视频", RackTrackKind.Mono or RackTrackKind.Stereo => "来源 · " + (_host.Project.InputPorts.FirstOrDefault(p => p.Id == track.InputPortId)?.Name ?? "未分配"), _ => "来源 · 上游轨道" }, 10, true); text.TextTrimming = TextTrimming.CharacterEllipsis; text.ToolTip = text.Text; holder.Child = text; }
        return holder;
    }
}
