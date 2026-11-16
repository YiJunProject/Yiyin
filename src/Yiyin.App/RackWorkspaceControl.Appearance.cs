using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using LightDeck.Core;

namespace LightDeck.App;

public sealed partial class RackWorkspaceControl
{
    private bool _showAppearance;
    private StackPanel? _appearancePanel;
    private void ShowAppearance()
    {
        _showAppearance = !_showAppearance;
        if (_showAppearance) _appearancePanel = BuildAppearancePanel();
        RefreshSidePanel(); UpdateSideVisibility();
    }
    private StackPanel BuildAppearancePanel()
    {
        var panel = new StackPanel(); panel.Children.Add(Label("机架外观", 18));
        panel.Children.Add(Label("条目厚度 · 字体自动适应", 12, true));
        foreach (var section in new[] { "insert", "send", "output" })
        {
            var name = section == "insert" ? "插入" : section == "send" ? "发送" : "输出";
            panel.Children.Add(Label(name + "条厚度", 12));
            var grid = new Grid { Margin = new(0, 6, 0, 14) }; grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new(52) });
            var slider = new Slider { Minimum = 24, Maximum = 72, Value = _host.Local.RowAppearance.Height(section), TickFrequency = 1, IsSnapToTickEnabled = true, MinHeight = 28 };
            var number = new TextBox { Text = slider.Value.ToString("0", CultureInfo.CurrentCulture), Margin = new(6, 0, 0, 0), Padding = new(3), MinWidth = 0, MinHeight = 28, FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center }; Grid.SetColumn(number, 1); grid.Children.Add(slider); grid.Children.Add(number); panel.Children.Add(grid);
            AutomationProperties.SetAutomationId(slider, "rack-row-height-" + section); AutomationProperties.SetAutomationId(number, "rack-row-height-number-" + section);
            void Commit()
            {
                if (!double.TryParse(number.Text, out var value) || !double.IsFinite(value) || value < 24 || value > 72) { number.Text = slider.Value.ToString("0"); return; }
                slider.Value = value;
                if (Math.Abs(_host.Local.RowAppearance.Height(section) - value) < .001) return;
                Run(async () => { var local = CopyLocal(_host.Local); local.RowAppearance.SetHeight(section, value); await _host.ApplyLocalAsync(local); });
            }
            slider.ValueChanged += (_, _) => number.Text = slider.Value.ToString("0");
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, e) => { if (!e.Canceled) Commit(); }));
            slider.PreviewMouseLeftButtonUp += (_, _) => Commit(); slider.PreviewKeyUp += (_, _) => Commit();
            number.LostKeyboardFocus += (_, _) => Commit(); number.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        }
        var presets = new WrapPanel();
        foreach (var (label, preset) in new[] { ("紧凑", "compact"), ("标准", "standard"), ("触摸", "touch") })
            presets.Children.Add(Button(label, "rack-row-preset-" + preset, () => Run(async () => { var local = CopyLocal(_host.Local); local.RowAppearance = RackRowAppearance.Preset(preset); await _host.ApplyLocalAsync(local); _appearancePanel = BuildAppearancePanel(); RefreshSidePanel(); })));
        panel.Children.Add(presets);
        var tighten = Button("一键收紧区域", "rack-tighten-regions", () => Run(TightenAppearanceAsync)); tighten.Margin = new(0, 12, 0, 8); panel.Children.Add(tighten);
        var hint = Label("条厚和区域高度独立。按内容收紧整排插入、发送、输出区域；不改变声音或推子数值。主控组件默认沿用此样式。", 11, true); hint.TextWrapping = TextWrapping.Wrap; panel.Children.Add(hint);
        return panel;
    }
    private Task TightenAppearanceAsync()
    {
        var local = CopyLocal(_host.Local); var tracks = _host.Project.Tracks.Where(t => t.Visible).ToArray(); var rows = local.RowAppearance;
        int Count(RackTrack t, bool output) => (output && t.OutputTrackId.HasValue ? 1 : 0) + t.Sends.Count(s => _host.Project.FindTrack(s.TargetTrackId) is { } target && (output ? target.Kind == RackTrackKind.Output : target.Kind is RackTrackKind.Bus or RackTrackKind.FX));
        var inserts = tracks.Select(t => t.Inserts.Count).DefaultIfEmpty().Max(); var sends = tracks.Select(t => Count(t, false)).DefaultIfEmpty().Max(); var outputs = tracks.Select(t => Count(t, true)).DefaultIfEmpty().Max();
        local.InsertHeight = inserts == 0 ? 0 : Math.Min(360, rows.ContentHeight("insert", inserts));
        local.SendHeight = sends == 0 ? 0 : Math.Min(360, rows.ContentHeight("send", sends));
        local.OutputHeight = Math.Clamp(rows.ContentHeight("output", outputs), 48, 720); local.CompactView = false;
        return _host.ApplyLocalAsync(local);
    }
}
