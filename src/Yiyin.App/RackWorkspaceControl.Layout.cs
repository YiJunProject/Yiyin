using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LightDeck.Core;

namespace LightDeck.App;

public sealed partial class RackWorkspaceControl
{
    private enum RackModule { Insert, Send, Output, Fader }
    private double InsertMinimumHeight => _host.Local.RowAppearance.ContentHeight("insert", 1);
    private double ConnectionMinimumHeight => Math.Max(_host.Local.RowAppearance.ContentHeight("send", 1), _host.Local.RowAppearance.ContentHeight("output", 1));
    private readonly List<(FrameworkElement Element, RackModule Kind)> _moduleHeights = [];
    private readonly Dictionary<RackModule, double> _modulePreview = [];
    public void SetWorkspaceActions(Action toggleEditing, Action? toggleDetached, bool detached)
    {
        _workspaceActions.Children.Clear();
        if (toggleDetached is not null) { var detach = Button(detached ? "收回" : "弹出", "rack-detach", toggleDetached); detach.Margin = new Thickness(4, 0, 4, 4); _workspaceActions.Children.Add(detach); }
        _editModeButton = Button(_host.IsEditing ? "完成编辑" : "编辑机架", "rack-edit-mode", toggleEditing, true); _editModeButton.Margin = new Thickness(4, 0, 0, 4); _workspaceActions.Children.Add(_editModeButton);
    }
    private void UpdateSideVisibility()
    {
        _detail.Visibility = _showInspector || _showAppearance ? Visibility.Visible : Visibility.Collapsed;
        _libraryDetail.Visibility = _showLibrary ? Visibility.Visible : Visibility.Collapsed;
        _body.ColumnDefinitions[1].Width = new GridLength(_showInspector || _showAppearance ? 250 : 0);
        _body.ColumnDefinitions[2].Width = new GridLength(_showLibrary ? 250 : 0);
    }
    private void ShowMore()
    {
        var menu = new ContextMenu();
        void Item(string title, string id, bool enabled, Action action) { var item = new MenuItem { Header = title, IsEnabled = enabled }; AutomationProperties.SetAutomationId(item, id); item.Click += (_, _) => action(); menu.Items.Add(item); }
        Item("显示视图", "rack-more-views", true, ShowViewMenu);
        Item("机架外观", "rack-appearance", true, ShowAppearance);
        Item(_host.Local.CompactView ? "详细视图" : "精简视图", "rack-view-density", true, ToggleCompactView);
        Item("插件扫描与管理", "rack-more-plugins", _host.IsEditing, ShowPluginManager);
        Item("导入 Studio One 工程…", "rack-import-studioone", _host.IsEditing && !_busy && _host.RackLibrary is not null, () => Run(ImportStudioOneAsync));
        Item("机架管理 · 切换 / 保留版本", "rack-library", _host.IsEditing && !_busy && _host.RackLibrary is not null, ShowRackLibrary);
        Item(_host.Status.IsRunning ? "停止机架（设备维护）" : "重新连接 / 开启机架", "rack-running", _host.IsEditing && !_busy, () => Run(() => _host.SetRunningAsync(!_host.Status.IsRunning)));
        Item("音频 I/O", "rack-more-devices", _host.IsEditing && !_busy, () => Run(ShowDevicesAsync));
        ContextMenu = menu; menu.PlacementTarget = this; menu.IsOpen = true;
    }
    private Brush TrackTint(string color)
    {
        var ink = (Color)ColorConverter.ConvertFromString(color); var basis = (Color)ColorConverter.ConvertFromString(_theme.Panel); var amount = _theme.Light ? .17 : .26;
        return new SolidColorBrush(Color.FromRgb((byte)(basis.R * (1 - amount) + ink.R * amount), (byte)(basis.G * (1 - amount) + ink.G * amount), (byte)(basis.B * (1 - amount) + ink.B * amount)));
    }
    private FrameworkElement BuildStrip(RackTrack track)
    {
        var strip = new Grid(); strip.RowDefinitions.Add(new() { Height = GridLength.Auto }); strip.RowDefinitions.Add(new()); strip.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var stack = new StackPanel(); strip.Children.Add(stack);
        var name = Button(track.Name, "rack-track-" + track.Id, () => { if (_host.IsEditing) Run(() => _host.SelectTrackAsync(track.Id)); });
        name.Background = DeckTheme.Brush(track.Color); name.Foreground = ColorText(track.Color); name.MinHeight = 42;
        name.Content = new TextBlock { Text = track.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold, FontSize = 14 }; name.ToolTip = track.Name; name.Padding = new Thickness(5, 6, 5, 6);
        var title = new Grid(); title.ColumnDefinitions.Add(new()); title.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); title.Children.Add(name);
        var enabled = Button("轨道开关", "rack-enable-" + track.Id, () => Live(track.Id, t => { var on = !TrackEnabled(t); t.Mute = !on; if (t.Kind is RackTrackKind.Mono or RackTrackKind.Stereo) t.Monitor = on; }));
        enabled.Padding = new Thickness(4); enabled.MinHeight = enabled.Height = WorkspaceUiDensity.IsTouch ? 44 : 34; enabled.Width = WorkspaceUiDensity.IsTouch ? 44 : 30; enabled.Margin = new Thickness(3, 0, 0, 0); enabled.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(enabled, 1); title.Children.Add(enabled); stack.Children.Add(title);
        MakeDragSource(name, () => new RackDrag(_host, RackDragKind.Track, track.Id));
        stack.Children.Add(BuildTrackSource(track));
        var inserts = BuildInsertArea(track); _moduleHeights.Add((inserts, RackModule.Insert)); stack.Children.Add(inserts);
        var sends = BuildSendArea(track); _moduleHeights.Add((sends, RackModule.Send)); stack.Children.Add(sends);
        var outputs = BuildOutputArea(track); _moduleHeights.Add((outputs, RackModule.Output)); stack.Children.Add(outputs);
        stack.Children.Add(ModuleHandle(RackModule.Fader, track.Id));
        var controls = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        var panBox = new StackPanel(); controls.Children.Add(panBox);
        var panText = Label("声像 · C", 10, true); panBox.Children.Add(EditableNumber(panText, "rack-pan-number-" + track.Id, () => ((_pending.GetValueOrDefault(track.Id) ?? _host.Project.FindTrack(track.Id))?.Pan ?? 0) * 100, value => Live(track.Id, t => t.Pan = value / 100), -100, 100, false));
        var pan = new RackPanSlider { Foreground = DeckTheme.Brush(_theme.Accent), Minimum = -1, Maximum = 1, Value = track.Pan, SmallChange = .01, LargeChange = .1, Margin = new Thickness(0, 2, 0, 0) }; AutomationProperties.SetAutomationId(pan, "rack-pan-" + track.Id); pan.ValueChanged += (_, _) => { if (!_syncing) Live(track.Id, t => t.Pan = pan.Value); }; panBox.Children.Add(pan); stack.Children.Add(controls);
        stack.Children.Add(SoloMuteButtons(track));
        var units = new Grid { Margin = new Thickness(1, 5, 1, 0) }; units.Children.Add(Label("增益 dB", 9, true)); var meterUnit = Label("dBFS", 9, true); meterUnit.HorizontalAlignment = HorizontalAlignment.Right; units.Children.Add(meterUnit); stack.Children.Add(units);
        var fader = new Grid { MinHeight = 180 }; _moduleHeights.Add((fader, RackModule.Fader)); Grid.SetRow(fader, 1); strip.Children.Add(fader); fader.ColumnDefinitions.Add(new() { Width = new GridLength(34) }); fader.ColumnDefinitions.Add(new() { Width = new GridLength(42) }); fader.ColumnDefinitions.Add(new());
        var gain = new RackGainSlider { Orientation = Orientation.Vertical, Minimum = RackDbScale.Minimum, Maximum = RackDbScale.Maximum, Value = Math.Clamp(track.GainDb, -90, 12), SmallChange = .1, LargeChange = 1, Template = RackDbScale.FaderTemplate() };
        AutomationProperties.SetAutomationId(gain, "rack-gain-" + track.Id); AutomationProperties.SetName(gain, track.Name + "增益 dB"); gain.ValueChanged += (_, _) => { if (!_syncing) Live(track.Id, t => t.GainDb = gain.Value); }; Grid.SetColumn(gain, 1); fader.Children.Add(gain);
        gain.ToolTip = "双击恢复 0 dB"; gain.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) => { if (e.ClickCount == 2) { ResetGain(track.Id); e.Handled = true; } }), true);
        var scale = new RackScaleView(DeckTheme.Brush(_theme.Text)); fader.Children.Add(scale);
        var meter = new RackMeterView { Margin = new Thickness(3, 0, 3, 0) }; Grid.SetColumn(meter, 2); AutomationProperties.SetAutomationId(meter, "rack-meter-" + track.Id); fader.Children.Add(meter);
        var footer = new StackPanel(); Grid.SetRow(footer, 2); strip.Children.Add(footer);
        var db = Label("0.0 dB", 12); db.HorizontalAlignment = HorizontalAlignment.Center; db.Margin = new Thickness(0, 2, 0, 4); footer.Children.Add(EditableNumber(db, "rack-gain-number-" + track.Id, () => CurrentGain(track.Id), value => Live(track.Id, t => t.GainDb = value)));
        AutomationProperties.SetAutomationId(db, "rack-gain-value-" + track.Id);
        var latency = Label("插件延迟 —", 10, true); latency.Margin = new Thickness(0, 3, 0, 2); latency.HorizontalAlignment = HorizontalAlignment.Center;
        latency.ToolTip = "本轨效果器链的处理延迟；不含路由 PDC 或声卡输出延迟。"; AutomationProperties.SetAutomationId(latency, "rack-plugin-latency-" + track.Id); footer.Children.Add(latency);
        var frame = PanelFrame(strip, 6); frame.Width = PreferredStripWidth; frame.MinWidth = MinimumStripWidth; frame.Background = TrackTint(track.Color); frame.BorderBrush = DeckTheme.Brush(_theme.Line); frame.BorderThickness = new Thickness(1); frame.Margin = new Thickness(0, 0, 5, 0); AutomationProperties.SetAutomationId(frame, "rack-strip-" + track.Id);
        _stripLayouts.Add(new(frame, stack, footer));
        name.PreviewMouseRightButtonDown += (_, e) => { if (_host.IsEditing) { e.Handled = true; RenameTrack(track.Id); } }; MakeDropTarget(frame, track.Id, RackDropKind.Track, 0);
        if (_host.IsEditing && _host.Project.SelectedTrackId == track.Id) { frame.BorderBrush = DeckTheme.Brush(_theme.Accent); frame.BorderThickness = new Thickness(2); }
        _strips[track.Id] = new(gain, pan, enabled, db, panText, meter, latency); return frame;
    }
    private double ModuleHeight(RackModule kind)
    {
        if (kind == RackModule.Fader) return MinimumFaderHeight;
        if (kind != RackModule.Fader && !ModuleVisible(kind.ToString().ToLowerInvariant())) return RackRowAppearance.HeaderHeight;
        var saved = kind switch { RackModule.Insert => _host.Local.InsertHeight, RackModule.Send => _host.Local.SendHeight, RackModule.Output => _host.Local.OutputHeight, _ => _host.Local.FaderHeight };
        if (_modulePreview.TryGetValue(kind, out var preview)) return preview;
        // Legacy zero heights predate drag-to-collapse. New collapsed sections
        // persist the visible header height, so old layouts retain their first row.
        if (saved <= 0) saved = kind == RackModule.Insert ? InsertMinimumHeight : ConnectionMinimumHeight;
        if (!_host.Local.CompactView) return saved;
        var visible = _host.Project.Tracks.Where(t => t.Visible);
        return kind switch
        {
            RackModule.Insert => visible.Any(t => t.Inserts.Count > 0) ? Math.Min(saved, InsertMinimumHeight) : 0,
            RackModule.Send => visible.Any(t => t.Sends.Any(s => _host.Project.FindTrack(s.TargetTrackId)?.Kind is RackTrackKind.Bus or RackTrackKind.FX)) ? Math.Min(saved, ConnectionMinimumHeight) : 0,
            RackModule.Output => Math.Min(saved, ConnectionMinimumHeight),
            _ => saved
        };
    }
    private static string ModuleName(RackModule kind) => kind switch { RackModule.Insert => "插入", RackModule.Send => "发送", RackModule.Output => "输出", _ => "推子" };
    private Thumb ModuleHandle(RackModule kind, Guid id)
    {
        var thumb = new Thumb { Height = 22, Margin = new Thickness(1, 2, 1, 2), Cursor = Cursors.SizeNS, Focusable = true };
        var shell = new FrameworkElementFactory(typeof(Border)); shell.SetValue(Border.BackgroundProperty, DeckTheme.Brush(_theme.Card)); shell.SetValue(Border.BorderBrushProperty, DeckTheme.Brush(_theme.Muted)); shell.SetValue(Border.BorderThicknessProperty, new Thickness(0, 1, 0, 1)); shell.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        var grip = new FrameworkElementFactory(typeof(TextBlock)); grip.SetValue(TextBlock.TextProperty, kind == RackModule.Fader ? ModuleName(kind) : ""); grip.SetValue(TextBlock.FontSizeProperty, 13d); grip.SetValue(TextBlock.ForegroundProperty, DeckTheme.Brush(_theme.Text)); grip.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center); grip.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center); shell.AppendChild(grip);
        thumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = shell };
        var above = kind switch { RackModule.Send => RackModule.Insert, RackModule.Output => RackModule.Send, _ => RackModule.Output };
        thumb.ToolTip = "拖动整条分隔栏，调整上方" + ModuleName(above) + (above == RackModule.Output ? "区域；至少保留一行输出" : "区域；可完全收起内容，标题条始终保留");
        AutomationProperties.SetName(thumb, thumb.ToolTip.ToString()); AutomationProperties.SetAutomationId(thumb, "rack-resize-" + kind.ToString().ToLowerInvariant() + "-" + id);
        double? startHeight = null;
        double startThumbY = 0;
        thumb.DragStarted += (_, _) =>
        {
            if (!ModuleVisible(above.ToString().ToLowerInvariant())) return;
            // Capture displayed sizes once, so compressed preferences do not move
            // other boundaries while this pointer controls only the preceding area.
            foreach (var section in new[] { RackModule.Insert, RackModule.Send, RackModule.Output })
                _modulePreview[section] = _effectiveHeights.GetValueOrDefault(section, ModuleHeight(section));
            startHeight = _modulePreview[above];
            startThumbY = thumb.TranslatePoint(new Point(), _owner).Y;
        };
        thumb.DragDelta += (_, e) =>
        {
            if (startHeight is not { } initial) return;
            var height = initial + e.VerticalChange + thumb.TranslatePoint(new Point(), _owner).Y - startThumbY;
            var minimum = above == RackModule.Output ? ConnectionMinimumHeight : RackRowAppearance.HeaderHeight;
            var others = new[] { RackModule.Insert, RackModule.Send, RackModule.Output }.Where(k => k != above).Sum(ModuleHeight);
            var available = _groups.Height - FixedStripHeight() - MinimumFaderHeight - others;
            var maximum = Math.Max(minimum, Math.Min(above == RackModule.Output ? 720 : 360, available));
            _modulePreview[above] = Math.Clamp(height, minimum, maximum);
            ApplyModuleHeights();
        };
        thumb.DragCompleted += async (_, e) =>
        {
            if (startHeight is null) return;
            startHeight = null;
            if (e.Canceled) { _modulePreview.Clear(); ApplyModuleHeights(); return; }
            var draft = CopyLocal(_host.Local); draft.CompactView = false;
            if (draft.ShowRackInsert) draft.InsertHeight = ModuleHeight(RackModule.Insert);
            if (draft.ShowRackSend) draft.SendHeight = ModuleHeight(RackModule.Send);
            if (draft.ShowRackOutput) draft.OutputHeight = ModuleHeight(RackModule.Output);
            try { _error.Text = ""; await _host.ApplyLocalAsync(draft); } catch (Exception ex) { _error.Text = "区域高度未保存：" + ex.Message; }
            finally { _modulePreview.Clear(); ApplyModuleHeights(); }
        }; return thumb;
    }
    private void ApplyModuleHeights()
    {
        UpdateResponsiveLayout();
        foreach (var module in _moduleHeights)
        {
            var height = _effectiveHeights.GetValueOrDefault(module.Kind, ModuleHeight(module.Kind));
            if (module.Kind == RackModule.Fader) { module.Element.MinHeight = height; continue; }
            module.Element.Visibility = Visibility.Visible;
            module.Element.Height = height;
        }
    }
    private void UpdateMeters()
    {
        if (_disposed || !IsVisible) return;
        var viewport = _body.Children.OfType<ScrollViewer>().FirstOrDefault(); if (viewport is null) return;
        var samples = _host.Status.Meters;
        foreach (var pair in _strips)
        {
            var meter = pair.Value.Meter; if (!meter.IsVisible || meter.ActualHeight <= 0) continue;
            var point = meter.TranslatePoint(new Point(), viewport); if (point.X + meter.ActualWidth < 0 || point.X > viewport.ActualWidth || point.Y + meter.ActualHeight < 0 || point.Y > viewport.ActualHeight) continue;
            meter.Update(samples?.GetValueOrDefault(pair.Key) ?? SilentMeter);
        }
    }
    private static readonly RackUiMeter SilentMeter = new();
}
