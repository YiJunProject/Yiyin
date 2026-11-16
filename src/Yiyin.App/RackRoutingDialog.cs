using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using LightDeck.Core;

namespace LightDeck.App;

internal sealed class RackRoutingDialog : DeckDialog
{
    private readonly IRackUiHost _host;
    private readonly RackDraftGuard _guard;
    private readonly RackProject _draft;
    private readonly Grid _workspace = new();
    private readonly StackPanel _editor = new();
    private readonly ScrollViewer _diagram = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly List<Action> _readFields = [];
    private Guid? _selected;
    private bool _matrix, _applying;
    private readonly Button _apply;
    public RackRoutingDialog(Window owner, DeckTheme theme, IRackUiHost host, Guid? selected) : base(owner, theme, "路由配置", "实线为主输出，虚线为额外发送。修改先保留在此窗口，点击应用后更新机架；取消不改变实际声音连接。", 1000)
    {
        _host = host; _guard = new(host); _draft = host.Project.Copy(); _selected = selected ?? _draft.Tracks.FirstOrDefault()?.Id;
        Width = Math.Min(1400, SystemParameters.WorkArea.Width - 60); MinWidth = 900; Height = Math.Min(840, SystemParameters.WorkArea.Height - 60); SizeToContent = SizeToContent.Manual; ResizeMode = ResizeMode.CanResizeWithGrip;
        var tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        Button Tab(string text, bool matrix) { var b = new Button { Content = text, MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) }; b.Click += (_, _) => { if (!ReadFields()) return; _matrix = matrix; Render(); }; tabs.Children.Add(b); return b; }
        AutomationProperties.SetAutomationId(Tab("直观连接", false), "rack-routing-graph"); AutomationProperties.SetAutomationId(Tab("路由矩阵", true), "rack-routing-matrix"); Body.Children.Add(tabs);
        _workspace.ColumnDefinitions.Add(new()); _workspace.ColumnDefinitions.Add(new() { Width = new GridLength(290) });
        _workspace.Children.Add(_diagram); var right = new ScrollViewer { Content = _editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(14, 0, 0, 0) }; Grid.SetColumn(right, 1); _workspace.Children.Add(right); Body.Children.Add(_workspace);
        SizeChanged += (_, _) => _workspace.Height = Math.Max(300, ActualHeight - 240);
        AddButton("取消修改", Close, id: "rack-routing-cancel"); _apply = AddButton("应用路由", () => _ = ApplyAsync(), true, "rack-routing-apply");
        Closing += (_, e) => { if (_applying) e.Cancel = true; }; Render();
    }
    private TextBlock Text(string value, double size = 12) => new() { Text = value, FontSize = size, Foreground = DeckTheme.Brush(Theme.Text), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private void Render()
    {
        _readFields.Clear(); _editor.Children.Clear(); if (_matrix) BuildMatrix(); else BuildGraph(); BuildEditor();
    }
    private bool ReadFields()
    {
        try { foreach (var read in _readFields) read(); Error.Visibility = Visibility.Collapsed; return true; }
        catch (Exception ex) { Invalid(ex.Message); return false; }
    }
    private async Task ApplyAsync()
    {
        if (_applying || !ReadFields()) return;
        try { _guard.RequireCurrent(); _draft.Validate(); _applying = true; _apply.IsEnabled = false; await _host.ApplyProjectAsync(_draft.Copy()); _applying = false; DialogResult = true; }
        catch (Exception ex) { Invalid("路由未应用：" + ex.Message); }
        finally { _applying = false; _apply.IsEnabled = true; }
    }
    private void BuildGraph()
    {
        var canvas = new Canvas { Width = 790, Height = Math.Max(440, _draft.Tracks.GroupBy(t => Group(t.Kind)).Select(g => g.Count()).DefaultIfEmpty(0).Max() * 88 + 65) };
        for (var column = 0; column < 3; column++) { var title = Text(new[] { "输入与播放", "编组 BUS / 效果 FX", "硬件输出" }[column], 14); title.FontWeight = FontWeights.SemiBold; Canvas.SetLeft(title, column * 270 + 8); Canvas.SetTop(title, 10); canvas.Children.Add(title); }
        var points = new Dictionary<Guid, Point>(); var counts = new int[3];
        foreach (var track in _draft.Tracks) { var group = Group(track.Kind); points[track.Id] = new Point(8 + group * 270, 48 + counts[group]++ * 88); }
        foreach (var track in _draft.Tracks)
        {
            if (track.OutputTrackId is { } target) Edge(track, target, false);
            foreach (var send in track.Sends.Where(s => s.Enabled)) Edge(track, send.TargetTrackId, true);
        }
        foreach (var track in _draft.Tracks)
        {
            var stack = new StackPanel(); stack.Children.Add(Text(track.Name, 14)); stack.Children.Add(Text(RackWorkspaceControl.KindLabel(track.Kind), 10));
            var button = new Button { Content = stack, Width = 190, Height = 65, HorizontalContentAlignment = HorizontalAlignment.Left, BorderBrush = DeckTheme.Brush(track.Id == _selected ? Theme.Accent : track.Color), BorderThickness = new Thickness(track.Id == _selected ? 2 : 1), ToolTip = track.Name };
            AutomationProperties.SetAutomationId(button, "rack-route-node-" + track.Id); button.Click += (_, _) => { if (!ReadFields()) return; _selected = track.Id; Render(); };
            Canvas.SetLeft(button, points[track.Id].X); Canvas.SetTop(button, points[track.Id].Y); canvas.Children.Add(button);
        }
        _diagram.Content = canvas;
        void Edge(RackTrack from, Guid target, bool send)
        {
            if (!points.TryGetValue(target, out var to)) return; var start = points[from.Id] + new Vector(190, 32); var end = to + new Vector(0, 32);
            var bend = Math.Max(45, Math.Abs(end.X - start.X) * .45); var geometry = new PathGeometry([new PathFigure(start, [new BezierSegment(start + new Vector(bend, 0), end - new Vector(bend, 0), end, true)], false)]);
            var path = new System.Windows.Shapes.Path { Data = geometry, Stroke = DeckTheme.Brush(from.Color), StrokeThickness = 2, IsHitTestVisible = false }; if (send) path.StrokeDashArray = new DoubleCollection([4, 3]); canvas.Children.Add(path);
            var arrow = new Polygon { Points = new PointCollection([end, end + new Vector(-8, -4), end + new Vector(-8, 4)]), Fill = DeckTheme.Brush(from.Color), IsHitTestVisible = false }; canvas.Children.Add(arrow);
        }
    }
    private static int Group(RackTrackKind kind) => kind is RackTrackKind.Mono or RackTrackKind.Stereo or RackTrackKind.Playback ? 0 : kind is RackTrackKind.Bus or RackTrackKind.FX ? 1 : 2;
    private void BuildMatrix()
    {
        var sources = _draft.Tracks.Where(t => t.Kind != RackTrackKind.Output).ToArray(); var targets = _draft.Tracks.Where(t => t.Kind is RackTrackKind.Bus or RackTrackKind.FX or RackTrackKind.Output).ToArray();
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(150) }); foreach (var _ in targets) grid.ColumnDefinitions.Add(new() { Width = new GridLength(145) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); for (var r = 0; r < sources.Length; r++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Add(Text("来源 → 目标", 13), 0, 0); for (var c = 0; c < targets.Length; c++) Add(Text(targets[c].Name, 12), 0, c + 1);
        for (var r = 0; r < sources.Length; r++)
        {
            var source = sources[r]; var pick = new Button { Content = source.Name, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(7), ToolTip = source.Name }; pick.Click += (_, _) => { if (ReadFields()) { _selected = source.Id; Render(); } }; Add(pick, r + 1, 0);
            for (var c = 0; c < targets.Length; c++)
            {
                var target = targets[c]; var send = source.Sends.FirstOrDefault(s => s.TargetTrackId == target.Id && s.Enabled); var main = source.OutputTrackId == target.Id;
                var selected = send is null ? main ? 1 : 0 : main ? send.PreFader ? 4 : 5 : send.PreFader ? 2 : 3;
                var combo = new ComboBox { ItemsSource = new[] { "—", "主输出", "推前发送", "推后发送", "主＋推前", "主＋推后" }, SelectedIndex = selected, IsEnabled = source.Id != target.Id };
                AutomationProperties.SetAutomationId(combo, $"rack-route-cell-{source.Id}-{target.Id}");
                combo.SelectionChanged += (_, _) =>
                {
                    if (!ReadFields()) return; var value = combo.SelectedIndex;
                    if (value is 1 or 4 or 5) source.OutputTrackId = target.Id; else if (source.OutputTrackId == target.Id) source.OutputTrackId = null;
                    var old = source.Sends.FirstOrDefault(s => s.TargetTrackId == target.Id); source.Sends.RemoveAll(s => s.TargetTrackId == target.Id);
                    if (value >= 2) source.Sends.Add(new RackSend { Id = old?.Id ?? Guid.NewGuid(), TargetTrackId = target.Id, GainDb = old?.GainDb ?? -12, PreFader = value is 2 or 4 });
                    Render();
                }; Add(combo, r + 1, c + 1);
            }
        }
        _diagram.Content = grid;
        void Add(FrameworkElement element, int row, int column) { element.Margin = new Thickness(3); Grid.SetRow(element, row); Grid.SetColumn(element, column); grid.Children.Add(element); }
    }
    private void BuildEditor()
    {
        var track = _selected is { } id ? _draft.FindTrack(id) : null; if (track is null) { _editor.Children.Add(Text("先添加或选择轨道。")); return; }
        _editor.Children.Add(Text("当前连接 · " + track.Name, 17));
        if (track.Kind == RackTrackKind.Output) { _editor.Children.Add(Text("输出轨的硬件声道在“音频设备”中分配，不回送其他轨道。")); return; }
        _editor.Children.Add(Label("主输出"));
        var targets = _draft.Tracks.Where(t => t.Id != track.Id && t.Kind is RackTrackKind.Bus or RackTrackKind.FX or RackTrackKind.Output).ToArray();
        var choices = new[] { new RouteChoice(null, "未分配") }.Concat(targets.Select(t => new RouteChoice(t.Id, t.Name))).ToArray();
        var main = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(RouteChoice.Name), SelectedValuePath = nameof(RouteChoice.Id), SelectedValue = track.OutputTrackId };
        if (track.OutputTrackId is null) main.SelectedIndex = 0; AutomationProperties.SetAutomationId(main, "rack-route-main"); _editor.Children.Add(main);
        _readFields.Add(() => track.OutputTrackId = (main.SelectedItem as RouteChoice)?.Id);
        _editor.Children.Add(Label("额外发送"));
        foreach (var target in targets)
        {
            var existing = track.Sends.FirstOrDefault(s => s.TargetTrackId == target.Id); var box = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var enabled = new CheckBox { Content = target.Name, IsChecked = existing?.Enabled == true }; AutomationProperties.SetAutomationId(enabled, "rack-send-enable-" + target.Id); box.Children.Add(enabled);
            var amount = new TextBox { Text = (existing?.GainDb ?? -12).ToString("0.0", CultureInfo.InvariantCulture), ToolTip = "发送增益 -90 至 +24 dB" }; AutomationProperties.SetAutomationId(amount, "rack-send-gain-" + target.Id); box.Children.Add(amount);
            var pre = new ComboBox { ItemsSource = new[] { "推子后 · 跟随主音量", "推子前 · 独立电平" }, SelectedIndex = existing is null || existing.PreFader ? 1 : 0, Margin = new Thickness(0, 4, 0, 0) }; AutomationProperties.SetAutomationId(pre, "rack-send-pre-" + target.Id); box.Children.Add(pre); _editor.Children.Add(box);
            _readFields.Add(() =>
            {
                if (!double.TryParse(amount.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gain) || !double.IsFinite(gain) || gain is < -90 or > 24) throw new ArgumentException("发送增益需为 -90 至 +24 dB。");
                var old = track.Sends.FirstOrDefault(s => s.TargetTrackId == target.Id); track.Sends.RemoveAll(s => s.TargetTrackId == target.Id);
                if (enabled.IsChecked == true) track.Sends.Add(new RackSend { Id = old?.Id ?? Guid.NewGuid(), TargetTrackId = target.Id, GainDb = gain, PreFader = pre.SelectedIndex == 1 });
            });
        }
    }
    private sealed record RouteChoice(Guid? Id, string Name);
}
