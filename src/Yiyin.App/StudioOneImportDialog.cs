using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LightDeck.Core;
using Microsoft.Win32;

namespace LightDeck.App;

internal sealed class StudioOneImportDialog : DeckDialog
{
    private readonly StudioOneImportResult _result;
    private readonly RackSnapshotService _library;
    private readonly string _report;
    private readonly TextBox _name;
    private readonly CheckBox _ack;
    private readonly Button _import, _keep;
    private readonly TextBlock _status;
    private bool _busy;
    private Guid? _added;
    internal bool Switched { get; private set; }

    internal StudioOneImportDialog(Window owner, DeckTheme theme, StudioOneImportResult result, RackLocalSettings local, RackSnapshotService library)
        : base(owner, theme, "导入 Studio One 工程", "先查看转换结果，再生成独立机架。切换会停止当前机架；原机架完整保留，可随时切回。", 960)
    {
        _result = result; _library = library; _report = StudioOneImportPresentation.BuildReport(result, local);
        UseEditorLayout();  AutomationProperties.SetAutomationId(this, "studioone-import-dialog");
        _name = Field("新机架名称", result.Title.Length > 80 ? result.Title[..80] : result.Title, "studioone-rack-name"); _name.MaxLength = 80;
        var plugins = result.Project.Tracks.SelectMany(t => t.Inserts).ToArray();
        var summary = new TextBlock { Text = $"{result.Generator}  ·  {result.Project.Tracks.Count} 条轨道  ·  {plugins.Length} 个插件  ·  {result.Project.InputPorts.Count} 输入 / {result.Project.OutputPorts.Count} 输出\n插件状态已读取 {result.PluginMappings.Count(p => p.StateImported)} 个；本机可匹配 {plugins.Count(p => StudioOneImportPresentation.Available(p, local))} 个。", TextWrapping = TextWrapping.Wrap, Foreground = DeckTheme.Brush(theme.Text), FontSize = 14, Margin = new Thickness(0, 14, 0, 10), LineHeight = 24 };
        AutomationProperties.SetAutomationId(summary, "studioone-import-summary"); Body.Children.Add(summary);
        var tabs = new TabControl { Height = 300, Background = theme.Surface(theme.Panel), BorderBrush = DeckTheme.Brush(theme.Line), Foreground = DeckTheme.Brush(theme.Text) };
        AutomationProperties.SetAutomationId(tabs, "studioone-preview-tabs");
        var issues = result.Issues.Select(i => $"• {i.Message}").ToList();
        foreach (var track in result.Project.Tracks)
        foreach (var plugin in track.Inserts.Where(p => !StudioOneImportPresentation.Available(p, local)))
            issues.Add($"• {track.Name} / {plugin.Identity.Name}：本机未找到已扫描且可用的同 ID 插件。导入后保留红色插槽；安装并扫描匹配插件，或替换 / 移除该插槽后才能启动（仅旁通仍不能启动）。");
        AddTab(tabs, "需要处理", string.Join("\n\n", issues.Prepend("原始 I/O 名称与声道关系已读取；切换后在“音频 I/O”绑定本机声卡。不会把其他电脑的通道号自动套到当前设备。")));
        AddTab(tabs, "轨道与完整报告", _report);
        Body.Children.Add(tabs);
        _ack = new CheckBox { Content = new TextBlock { Text = "我已核对差异。切换后核对 I/O，处理全部缺失插件后才能启动；旁通不能跳过缺失插件。", TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 14, 0, 8), Foreground = DeckTheme.Brush(theme.Text) };
        AutomationProperties.SetAutomationId(_ack, "studioone-acknowledge"); Body.Children.Add(_ack);
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = DeckTheme.Brush(theme.Accent), Margin = new Thickness(0, 4, 0, 4) }; AutomationProperties.SetAutomationId(_status, "studioone-import-status"); Body.Children.Add(_status);
        AddButton("导出报告…", ExportReport, id: "studioone-export-report");
        AddButton("取消", Close, id: "studioone-import-cancel");
        _keep = AddButton("仅加入工程", () => _ = ImportAsync(false), id: "studioone-import-keep");
        _import = AddButton("加入并切换", () => _ = ImportAsync(true), true, "studioone-import-switch");
        _ack.Checked += (_, _) => UpdateButtons(); _ack.Unchecked += (_, _) => UpdateButtons(); UpdateButtons();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        if (!result.CanImport) Invalid("此工程含无法安全转换的结构，暂不能导入。请查看报告中的错误。");
    }
    private void AddTab(TabControl tabs, string title, string text)
    {
        var box = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12), BorderThickness = new Thickness(0), Background = Theme.Surface(Theme.Panel), Foreground = DeckTheme.Brush(Theme.Text), FontSize = 13 };
        tabs.Items.Add(new TabItem { Header = title, Content = box });
    }
    private void UpdateButtons() { _import.IsEnabled = _keep.IsEnabled = !_busy && _result.CanImport && _ack.IsChecked == true; _name.IsEnabled = !_busy && _added is null; _ack.IsEnabled = !_busy; }
    internal async Task ImportAsync(bool switchNow)
    {
        if (_busy || !_result.CanImport || _ack.IsChecked != true) return;
        _busy = true; UpdateButtons(); Error.Visibility = Visibility.Collapsed;
        try
        {
            if (_added is null)
            {
                var rack = _result.Project.Copy();
                rack.ImportNotes = _report.Length <= 16000 ? _report : _report[..16000] + "\n报告过长，此处仅保留前部；完整内容请使用导出报告。";
                _added = await _library.AddImportedAsync(_name.Text, rack);
            }
            _status.Text = "新机架已加入当前工程；原机架保留。正式文件仍需“保存工程”。";
            if (switchNow) { await _library.SwitchAsync(_added.Value); Switched = true; }
            _busy = false; DialogResult = true;
        }
        catch (Exception ex) { Invalid((_added is not null ? "新机架已保留，尚未完成切换。可重试或到机架管理中查看。\n" : "导入未完成，原机架不变。\n") + ex.Message); }
        finally { _busy = false; UpdateButtons(); }
    }
    private void ExportReport()
    {
        var picker = new SaveFileDialog { Title = "导出 Studio One 转换报告", Filter = "文本报告|*.txt", FileName = "StudioOne-导入报告.txt" };
        if (picker.ShowDialog(this) != true) return;
        try { File.WriteAllText(picker.FileName, _report); _status.Text = "报告已导出。"; } catch (Exception ex) { Invalid("报告未导出：" + ex.Message); }
    }
}
