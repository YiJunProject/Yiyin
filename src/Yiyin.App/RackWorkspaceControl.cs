using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LightDeck.Core;

namespace LightDeck.App;

public sealed partial class RackWorkspaceControl : Grid, IDisposable
{
    private readonly Window _owner;
    private readonly DeckTheme _theme;
    private readonly IRackUiHost _host;
    private readonly Grid _groups = new();
    private readonly ScrollViewer _mixer;
    private readonly StackPanel _inspector = new();
    private readonly Grid _library = new();
    private readonly ContentControl _sideContent = new();
    private bool _showLibrary;
    private bool _showInspector;
    private readonly Border _libraryDetail;
    private readonly Dictionary<string, CheckBox> _moduleChoices = [];
    private readonly Dictionary<Guid, List<(Button Mute, Button Solo)>> _soloButtons = [];
    private readonly Button _clearSolo;
    private readonly TextBlock _status, _error, _outputSignal;
    private readonly Button _run, _add, _devices, _plugins, _visibility, _compact;
    private readonly Grid _body = new();
    private readonly StackPanel _workspaceActions = new() { Orientation = Orientation.Horizontal };
    private Button? _editModeButton;
    private readonly Border _detail;
    private bool _refreshQueued;
    private readonly Dictionary<Guid, StripControls> _strips = [];
    private readonly Dictionary<Guid, SendControls> _sendControls = [];
    private readonly Dictionary<Guid, RackTrack> _pending = [];
    private readonly Dictionary<Guid, PendingControlEdits> _pendingEdits = [];
    private readonly HashSet<Guid> _writing = [];
    private readonly DispatcherTimer _meters;
    private bool _disposed, _syncing, _busy;
    private string _signature = "";

    public RackWorkspaceControl(Window owner, DeckTheme theme, IRackUiHost host)
    {
        _owner = owner; _theme = theme; _host = host; _showLibrary = host.Local.ShowRackLibrary; _showInspector = host.Local.ShowRackInspector;
        PreviewMouseWheel += RackSliderWheel.HandlePreviewMouseWheel; _originalWindowMinimum = owner.MinHeight; _originalWindowMinimumWidth = owner.MinWidth; _originalWindowMaximumWidth = owner.MaxWidth;
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new()); RowDefinitions.Add(new() { Height = GridLength.Auto });
        var toolbarGrid = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_workspaceActions, Dock.Right); toolbarGrid.Children.Add(_workspaceActions);
        var toolbar = new WrapPanel(); toolbarGrid.Children.Add(toolbar);
        _add = Button("＋ 轨道", "rack-add-track", () => EditTrack(null), true);
        _devices = Button("音频 I/O", "rack-devices", () => Run(ShowDevicesAsync));
        _plugins = Button("插件管理", "rack-plugins", ShowPluginManager);
        _run = Button("开启机架", "rack-running", () => Run(() => _host.SetRunningAsync(!_host.Status.IsRunning)), true);
        _visibility = Button("显示视图", "rack-visibility", ShowViewMenu);
        _compact = Button("详细视图", "rack-view-density", ToggleCompactView);
        foreach (var button in new[] { _add, _devices }) { button.Margin = new Thickness(0, 0, 10, 4); toolbar.Children.Add(button); }
        toolbar.Children.Add(new Border { Width = 1, Height = 24, Background = DeckTheme.Brush(theme.Line), Margin = new Thickness(4, 0, 12, 0) });
        toolbar.Children.Add(Label("显示模块", 12, true));
        AddModuleChoice(toolbar, "插入", "insert");
        AddModuleChoice(toolbar, "发送", "send");
        AddModuleChoice(toolbar, "输出", "output");
        AddModuleChoice(toolbar, "轨道设置", "inspector");
        AddModuleChoice(toolbar, "插件库", "library");
        _clearSolo = Button("清除 Solo", "rack-clear-solo", () => { foreach (var track in _host.Project.Tracks.ToArray()) if ((_pending.GetValueOrDefault(track.Id) ?? track).Solo) Live(track.Id, t => t.Solo = false); });
        _clearSolo.Margin = new Thickness(12, 0, 8, 4); toolbar.Children.Add(_clearSolo);
        toolbar.Children.Add(Button("···", "rack-more", ShowMore)); Children.Add(toolbarGrid);
        var statusRow = new DockPanel { Margin = new Thickness(6, 0, 8, 4) };
        _outputSignal = Label("输出待采样", 10, true); DockPanel.SetDock(_outputSignal, Dock.Right); statusRow.Children.Add(_outputSignal);
        AutomationProperties.SetAutomationId(_outputSignal, "rack-final-output-signal");
        _status = Label("", 11, true); _status.TextTrimming = TextTrimming.CharacterEllipsis; AutomationProperties.SetAutomationId(_status, "rack-runtime-status"); statusRow.Children.Add(_status);
        var body = _body; body.ColumnDefinitions.Add(new()); body.ColumnDefinitions.Add(new() { Width = new GridLength(250) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(250) });
        var mixer = _mixer = new ScrollViewer { Content = _groups, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Padding = new Thickness(0, 0, 5, 0) };
        mixer.SizeChanged += (_, _) => ApplyModuleHeights();
        Loaded += (_, _) => ApplyModuleHeights();
        AutomationProperties.SetAutomationId(mixer, "rack-mixer"); body.Children.Add(mixer);
        EnableMixerPanning();
        AddWidthNotice(body);
        _sideContent.Content = new ScrollViewer { Content = _inspector, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _detail = CreateSidePanel("轨道设置", "inspector", _sideContent);
        Grid.SetColumn(_detail, 1); body.Children.Add(_detail);
        _libraryDetail = CreateSidePanel("插件库", "library", _library);
        Grid.SetColumn(_libraryDetail, 2); body.Children.Add(_libraryDetail);
        Grid.SetRow(body, 1); Children.Add(body);
        _error = Label("", 12); _error.Foreground = DeckTheme.Brush(theme.Light ? "#B13A32" : "#FFB39B"); _error.TextWrapping = TextWrapping.Wrap; _error.Margin = new Thickness(2, 7, 0, 0); var footer = new StackPanel(); footer.Children.Add(statusRow); footer.Children.Add(_error); Grid.SetRow(footer, 2); Children.Add(footer);
        SizeChanged += (_, _) => UpdateSideVisibility();
        EnableOutsideRemoval();
        host.Changed += HostChanged;
        _meters = new DispatcherTimer(TimeSpan.FromMilliseconds(1000d / 60), DispatcherPriority.Render, (_, _) => UpdateMeters(), Dispatcher); _meters.Start();
        Refresh();
    }
    internal IRackUiHost Host => _host;
    private TextBlock Label(string text, double size = 13, bool muted = false) => new() { Text = text, FontSize = size, Foreground = DeckTheme.Brush(muted ? _theme.Muted : _theme.Text), VerticalAlignment = VerticalAlignment.Center };
    private Button Button(string text, string id, Action action, bool primary = false)
    {
        var b = new Button { Content = text, MinHeight = WorkspaceUiDensity.ControlHeight, Padding = new Thickness(12, 6, 12, 6) }; AutomationProperties.SetAutomationId(b, id); AutomationProperties.SetName(b, text);
        if (primary) { b.Background = DeckTheme.Brush(_theme.Accent); b.Foreground = DeckTheme.Brush(_theme.AccentText); }
        b.Click += (_, _) => action(); return b;
    }
    private Border PanelFrame(UIElement child, double padding = 8) => new() { Child = child, Padding = new Thickness(padding), Background = _theme.Surface(_theme.Panel), BorderBrush = DeckTheme.Brush(_theme.Line), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(_theme.CardRadius) };
    private void HostChanged() { if (_disposed || _refreshQueued) return; _refreshQueued = true; _ = Dispatcher.BeginInvoke(() => { _refreshQueued = false; Refresh(); }, DispatcherPriority.Background); }
    private void Refresh()
    {
        if (_disposed) return;
        var signature = _host.Local.RowAppearance.Signature + ":" + _host.IsEditing + ":" + _host.Project.SelectedTrackId + ":" + _host.Local.RoutePlayer + ":" + string.Join("|", _host.Project.Tracks.Select(t => $"{t.Id}:{t.Name}:{t.Kind}:{t.Color}:{t.Channels}:{t.Visible}:{t.InputPortId}:{t.HardwareOutputPortId}:{t.OutputTrackId}:" + string.Join(',', t.Inserts.Select(p => $"{p.Id}/{p.Identity.Name}")) + ":" + string.Join(',', t.Sends.Select(s => $"{s.Id}/{s.TargetTrackId}")))) + JsonSerializer.Serialize(_host.Project.InputPorts) + JsonSerializer.Serialize(_host.Project.OutputPorts) + JsonSerializer.Serialize(_host.Local.InputPorts) + JsonSerializer.Serialize(_host.Local.OutputPorts);
        if (_signature != signature)
        {
            _signature = signature; _strips.Clear(); _soloButtons.Clear(); _sendControls.Clear(); _stripSendControls.Clear(); _mainOutputControls.Clear(); _pluginBypassControls.Clear(); _slotBypassControls.Clear(); _preFaderControls.Clear(); _insertLabels.Clear(); _insertButtons.Clear(); _moduleHeights.Clear(); _stripLayouts.Clear(); _groupHeadings.Clear(); _outputControls.Clear(); _groups.Children.Clear();
            _groups.ColumnDefinitions.Clear(); _groups.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); _groups.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); _groups.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); _groups.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            BuildGroup("输入", [RackTrackKind.Mono, RackTrackKind.Stereo, RackTrackKind.Playback], 0);
            BuildGroup("BUS", [RackTrackKind.Bus], 1);
            BuildGroup("FX", [RackTrackKind.FX], 2);
            BuildGroup("输出", [RackTrackKind.Output], 3);
            if (_groups.Children.Count == 0)
            {
                var empty = new StackPanel { MaxWidth = 330, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 26, 8, 10) };
                var message = Label(_host.Project.Tracks.Count > 0 ? "所有轨道已隐藏，声音与路由继续运行。" : "尚无机架轨道。", 13, true); message.TextWrapping = TextWrapping.Wrap; empty.Children.Add(message);
                if (_host.IsEditing) empty.Children.Add(_host.Project.Tracks.Count > 0 ? Button("显示轨道", "rack-show-hidden", ShowVisibility) : Button("添加轨道", "rack-empty-Mono", () => EditTrack(null, RackTrackKind.Mono)));
                Grid.SetColumnSpan(empty, 4); _groups.Children.Add(empty);
            }
            BuildInspector(); RefreshSidePanel();
        }
        _showInspector = _host.Local.ShowRackInspector; _showLibrary = _host.Local.ShowRackLibrary;
        UpdateSideVisibility(); ApplyModuleHeights(); BuildLibrary();
        UpdateLive();
    }
    private void BuildGroup(string name, RackTrackKind[] kinds, int column)
    {
        var selected = _host.Project.Tracks.Where(t => t.Visible && kinds.Contains(t.Kind)).ToArray();
        if (selected.Length == 0) return;
        var group = new DockPanel { Margin = new Thickness(0, 0, column == 3 ? 0 : 12, 0) };
        var color = column switch { 0 => "#8A6427", 1 => "#167F89", 2 => "#74428C", _ => "#93495B" };
        var heading = Label(name, 16); heading.Foreground = Brushes.White; heading.FontWeight = FontWeights.SemiBold; heading.HorizontalAlignment = HorizontalAlignment.Center;
        var banner = new Border { Child = heading, Background = DeckTheme.Brush(color), Padding = new Thickness(6, 8, 6, 8), Margin = new Thickness(0, 0, 5, 8), CornerRadius = new CornerRadius(4) };
        DockPanel.SetDock(banner, Dock.Top); group.Children.Add(banner); _groupHeadings.Add(banner);
        if (column != 3) { var separator = new Border { Width = 3, Background = DeckTheme.Brush(_theme.Accent), Margin = new Thickness(4, 0, 0, 0) }; DockPanel.SetDock(separator, Dock.Right); group.Children.Add(separator); }
        var tracks = new StackPanel { Orientation = Orientation.Horizontal }; group.Children.Add(tracks);
        foreach (var track in selected) tracks.Children.Add(BuildStrip(track));
        Grid.SetColumn(group, column); _groups.Children.Add(group);
    }
    private void UpdateLive()
    {
        if (_disposed) return; if (_editModeButton is not null) _editModeButton.Content = _host.IsEditing ? "完成编辑" : "编辑机架"; _syncing = true;
        try
        {
            var state = _host.Status; _run.Content = state.IsRunning ? "停止机架" : "开启机架"; _run.IsEnabled = !_busy && !state.IsBusy;
            var actual = state.ActualSampleRate > 0 && state.ActualBufferFrames > 0 ? $"{state.ActualSampleRate / 1000d:0.#} kHz · {state.ActualBufferFrames} 帧" : "实际采样 / 缓冲待连接";
            var device = _host.Local.OutputDeviceName.Length > 0 ? _host.Local.OutputDeviceName : "未选择声卡";
            _status.Text = $"{_host.RackName} · {_host.Local.DeviceApi.ToUpperInvariant()} · {device} · {(state.DriverResetRequired ? "需要重启驱动" : state.Message)} · {actual}";
            if (_busy) _status.Text = "正在处理机架操作… · " + _status.Text;
            _status.ToolTip = $"请求 {_host.Local.SampleRate} Hz / {_host.Local.BufferSize} 帧；输出延迟约 {state.LatencyMilliseconds:0.00} ms；回调占用 {state.CpuPercent:0.00}%（不是进程 CPU）。";
            if (state.ScanProgress is { } scan)
            {
                var scanText = scan.IsRunning ? $"{(_host.Local.InitialPluginScanCompleted ? "检查插件变化" : "首次扫描插件")} · {scan.Phase}" + (scan.Total > 0 ? $" {scan.Completed}/{scan.Total}" : "")
                    : scan.Phase == "完成" ? $"插件扫描完成 · {_host.Plugins.Count} 个可用" : "插件扫描" + scan.Phase;
                _status.Text = scanText + " · " + _status.Text;
                _status.ToolTip = scanText + (scan.CurrentFile.Length > 0 ? "\n" + scan.CurrentFile : "") + "\n" + _status.ToolTip;
            }
            _status.Foreground = DeckTheme.Brush(state.IsRunning && !state.DriverResetRequired ? (_theme.Light ? "#087C51" : "#46E1AA") : _theme.Muted);
            var signal = state.OutputSignal;
            var outputOver = signal is not null && (signal.OverRangeSamples > 0 || signal.NonFiniteSamples > 0 || signal.LastBlockPeak > 1 || signal.IntegerOutputOverRangeSamples > 0);
            _outputSignal.Text = signal is null || signal.FramesObserved <= 0 ? "输出待采样" : outputOver ? "最终输出超限" : "最终输出正常";
            _outputSignal.Foreground = DeckTheme.Brush(outputOver ? _theme.Light ? "#AD2338" : "#FF8C9C" : _theme.Muted);
            _outputSignal.ToolTip = signal is null ? "等待最终输出混合数据。" : $"所有输出轨汇入硬件通道后的浮点混合，超过 0 dBFS 可能削波。\n当前块峰值 {signal.LastBlockPeak:0.0000}；本次配置峰值 {signal.PeakSinceReset:0.0000}；超限样本 {signal.OverRangeSamples}；非有限值 {signal.NonFiniteSamples}。\n整数输出转换超限：{(signal.IntegerOutputOverRangeSamples is { } count ? count.ToString(CultureInfo.InvariantCulture) : "未提供")}。\n这不是声学测量或物理失真判断；未自动添加限幅器。";
            _add.IsEnabled = _devices.IsEnabled = _plugins.IsEnabled = _host.IsEditing && !_busy && !state.IsBusy;
            _visibility.IsEnabled = _compact.IsEnabled = !_busy && !state.IsBusy;
            _visibility.Content = "显示视图 ▾";
            _visibility.ToolTip = "当前：" + (_host.Project.DisplayViews.FirstOrDefault(v => v.Id == _host.Project.ActiveDisplayViewId)?.Name ?? "原显示布局") + "；只改变显示，不停止声音";
            _compact.Content = _host.Local.CompactView ? "精简视图" : "详细视图"; _compact.ToolTip = "点击切换精简 / 详细；保留已保存的模块高度";
            _add.Visibility = _host.IsEditing ? Visibility.Visible : Visibility.Collapsed;
            UpdateSoloButtons(); UpdateModuleChoices();
            foreach (var pair in _strips)
            {
                var track = _pending.GetValueOrDefault(pair.Key) ?? _host.Project.FindTrack(pair.Key); if (track is null) continue; var strip = pair.Value;
                if (!strip.Gain.IsMouseCaptureWithin && !strip.Gain.IsKeyboardFocusWithin) strip.Gain.Value = track.GainDb;
                if (!strip.Pan.IsMouseCaptureWithin && !strip.Pan.IsKeyboardFocusWithin) strip.Pan.Value = track.Pan;
                strip.Db.Text = track.GainDb <= -90 ? "−∞ dB" : $"{track.GainDb:0.0} dB"; strip.PanText.Text = "声像 · " + (Math.Abs(track.Pan) < .005 ? "C" : (track.Pan < 0 ? "L " : "R ") + Math.Abs(track.Pan * 100).ToString("0"));
                var enabled = TrackEnabled(track); PaintPower(strip.Enabled, enabled, "轨道");
                if (_mainOutputControls.TryGetValue(track.Id, out var mainOutput)) PaintPower(mainOutput, track.MainOutputEnabled, "主输出");
                var latency = track.Inserts.Count == 0 ? 0d : state.PluginLatencyMilliseconds?.GetValueOrDefault(track.Id, double.NaN) ?? double.NaN;
                strip.PluginLatency.Text = double.IsFinite(latency) && latency >= 0 ? $"插件延迟 {latency:0.00} ms" : "插件延迟 —";
            }
            foreach (var savedTrack in _host.Project.Tracks)
            foreach (var plugin in (_pending.GetValueOrDefault(savedTrack.Id) ?? savedTrack).Inserts)
            {
                UpdatePluginSlot(plugin, state);
                if (_pluginBypassControls.TryGetValue(plugin.Id, out var bypass)) { bypass.Content = plugin.Bypass ? "启用" : "旁通"; PaintToggle(bypass, plugin.Bypass); }
                if (_slotBypassControls.TryGetValue(plugin.Id, out var slotPower)) PaintPower(slotPower, !plugin.Bypass, "插件");
            }
            UpdateMainOutputs();
            foreach (var pair in _sendControls.Concat(_stripSendControls))
            {
                var control = pair.Value; var track = _pending.GetValueOrDefault(control.TrackId) ?? _host.Project.FindTrack(control.TrackId); var send = track?.Sends.FirstOrDefault(s => s.Id == pair.Key); if (send is null) continue;
                if (!control.Gain.IsMouseCaptureWithin && !control.Gain.IsKeyboardFocusWithin) control.Gain.Value = send.GainDb;
                control.Db.Text = $"{send.GainDb:0.0}"; PaintPower(control.Enabled, send.Enabled, "发送");
                if (control.TapLabel is { } tapLabel) tapLabel.Text = $"{_host.Project.FindTrack(send.TargetTrackId)?.Name ?? "未知"}\n{(send.PreFader ? "推子前 · 独立电平" : "推子后 · 跟随主音量")}";
                if (_preFaderControls.TryGetValue(send.Id, out var pre)) { pre.Content = send.PreFader ? "前" : "后"; pre.ToolTip = send.PreFader ? "推子前发送，点击改为推子后" : "推子后发送，点击改为推子前"; AutomationProperties.SetName(pre, pre.ToolTip.ToString()); PaintToggle(pre, send.PreFader); }
            }
        }
        finally { _syncing = false; }
    }

    private void ResetGain(Guid id)
    {
        if (_disposed || _busy || !_strips.TryGetValue(id, out var strip)) return;
        var wasSyncing = _syncing; _syncing = true; try { strip.Gain.Value = 0; } finally { _syncing = wasSyncing; }
        Live(id, track => track.GainDb = 0);
    }
    private void PaintToggle(Button button, bool active) { button.Background = DeckTheme.Brush(active ? _theme.Accent : _theme.Card); button.Foreground = DeckTheme.Brush(active ? _theme.AccentText : _theme.Text); }
    private void Live(Guid id, Action<RackTrack> edit)
    {
        if (_disposed || _busy) return;
        var before = _pending.GetValueOrDefault(id) ?? _host.Project.FindTrack(id); if (before is null) return;
        var draft = before.Copy(); edit(draft);
        if (!_pendingEdits.TryGetValue(id, out var edits)) _pendingEdits[id] = edits = new();
        edits.Capture(before, draft); _pending[id] = draft; UpdateLive(); if (!_writing.Add(id)) return;
        _ = WriteAsync();
        async Task WriteAsync()
        {
            try
            {
                while (!_disposed && _pendingEdits.Remove(id, out var changes))
                {
                    // Only local fields not yet submitted survive an await. Rebase
                    // them on current readback so another controller's pan/send is
                    // not replaced by the older optimistic whole-track snapshot.
                    var current = _host.Project.FindTrack(id)?.Copy(); if (current is null) break;
                    changes.Apply(current); _pending[id] = current;
                    await _host.UpdateTrackAsync(current);
                    if (!_pendingEdits.ContainsKey(id)) _pending.Remove(id);
                }
            }
            catch (Exception ex) { _error.Text = "轨道操作未完成：" + ex.Message; }
            finally { _pendingEdits.Remove(id); _pending.Remove(id); _writing.Remove(id); Refresh(); }
        }
    }
    private async void Run(Func<Task> action)
    {
        if (_disposed || _busy) return; _busy = true; _error.Text = ""; UpdateLive();
        try { await Dispatcher.Yield(DispatcherPriority.Background); await action(); } catch (Exception ex) { if (!_disposed) { _error.Text = ex.Message; _signature = ""; } }
        finally { _busy = false; if (!_disposed) Refresh(); }
    }
    private void OpenPlugin(Guid trackId, Guid insertId) => Run(() => _host.ShowPluginEditorAsync(trackId, insertId, new WindowInteropHelper(_owner).Handle));
    private static Brush ColorText(string color)
    {
        var c = (Color)ColorConverter.ConvertFromString(color); return DeckTheme.Brush((c.R * .299 + c.G * .587 + c.B * .114) >= 150 ? "#101820" : "#FFFFFF");
    }
    private string PhysicalChannels(Guid id, bool input)
    {
        var track = _host.Project.FindTrack(id); var portId = input ? track?.InputPortId : track?.HardwareOutputPortId;
        var port = (input ? _host.Project.InputPorts : _host.Project.OutputPorts).FirstOrDefault(p => p.Id == portId);
        if (port is null) return "未分配" + (input ? "输入" : "输出");
        return (input ? _host.Local.InputPorts : _host.Local.OutputPorts).TryGetValue(port.Id, out var map)
            ? port.Name + " · " + map.DeviceName + " · " + string.Join(" / ", map.Channels.Select(c => (c + 1).ToString(CultureInfo.InvariantCulture)))
            : port.Name + " · 本机硬件通道未分配";
    }
    internal static string KindLabel(RackTrackKind kind) => kind switch { RackTrackKind.Mono => "话筒 / 单声道", RackTrackKind.Stereo => "立体声输入", RackTrackKind.Playback => "内部音视频", RackTrackKind.Bus => "编组 BUS", RackTrackKind.FX => "效果 FX", _ => "硬件输出" };
    internal static RackLocalSettings CopyLocal(RackLocalSettings local) => JsonSerializer.Deserialize<RackLocalSettings>(JsonSerializer.Serialize(local, ProjectStore.JsonOptions), ProjectStore.JsonOptions)!;
    internal static void ThemeList(ListBox list, DeckTheme theme)
    {
        list.Background = DeckTheme.Brush(theme.Input); list.Foreground = DeckTheme.Brush(theme.Text); list.BorderBrush = DeckTheme.Brush(theme.Line); list.BorderThickness = new Thickness(1); list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var row = new Style(typeof(ListBoxItem)); row.Setters.Add(new Setter(Control.ForegroundProperty, DeckTheme.Brush(theme.Text))); row.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent)); row.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8)));
        var shell = new FrameworkElementFactory(typeof(Border)); shell.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Control.Background)) { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) }); shell.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding(nameof(Control.Padding)) { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) }); shell.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        row.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = shell }));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Control.BackgroundProperty, DeckTheme.Brush(theme.Card))); row.Triggers.Add(hover);
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true }; selected.Setters.Add(new Setter(Control.BackgroundProperty, DeckTheme.Brush(theme.Accent))); selected.Setters.Add(new Setter(Control.ForegroundProperty, DeckTheme.Brush(theme.AccentText))); row.Triggers.Add(selected); list.ItemContainerStyle = row;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; DisposeMixerPanning(); _libraryDebounce?.Stop(); _owner.LocationChanged -= OwnerLayoutChanged; RestoreWindowMinimum(); EndDragVisual(); _meters.Stop(); _host.Changed -= HostChanged; }
    private sealed record StripControls(Slider Gain, Slider Pan, Button Enabled, TextBlock Db, TextBlock PanText, RackMeterView Meter, TextBlock PluginLatency);
    private sealed record SendControls(Guid TrackId, Slider Gain, TextBlock Db, Button Enabled, TextBlock? TapLabel = null);
}
