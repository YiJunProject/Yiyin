using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Shell;
using LightDeck.Core;
using Microsoft.Win32;

namespace LightDeck.App;

public sealed partial class MainWindow : Window
{
    private readonly string _dataDirectory, _sessionPath;
    private readonly SemaphoreSlim _rackCommit = new(1, 1);
    private AudioRackService? _rackService;
    private MainRackHost? _rackHost;
    private RackWorkspaceControl? _rackView;
    private AppSettings _rackAppSettings = new();
    private AppSettingsStore _rackSettingsStore;
    private DeckTheme _theme;
    private bool _rackEditing, _closing, _projectTransition, _exitReady, _saveInProgress;
    private long _rackGeneration;
    private int _notificationPending;
    private event Action? RackUiChanged;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly TextBlock _notice = new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(14, 4, 14, 8) };
    internal RackDocument Project { get; private set; }
    internal AppSettings RackAppSettings => _rackAppSettings;
    internal AppSettingsStore RackSettingsStore => _rackSettingsStore;
    public AudioRackService RackService => _rackService ??= CreateService();
    private IRackUiHost RackHost => _rackHost ??= new MainRackHost(this);

    public MainWindow(string dataDirectory, string? projectPath = null)
    {
        _dataDirectory = dataDirectory; _sessionPath = Path.Combine(dataDirectory, "current.yiyin");
        _rackSettingsStore = new(Path.Combine(dataDirectory, "app-settings.json"));
        try { _rackAppSettings = _rackSettingsStore.Load(); }
        catch (Exception e) { PreserveUnreadable(Path.Combine(dataDirectory, "app-settings.json")); Notice("本机设置未载入：" + e.Message); }
        var open = projectPath ?? (_rackAppSettings.ProjectPath is { } saved && File.Exists(saved) ? saved : _sessionPath);
        try { Project = File.Exists(open) ? ProjectStore.Load(open) : new(); }
        catch (Exception e) { if (projectPath is not null) throw new InvalidDataException("工程未载入，原文件保留：" + e.Message, e); if (open == _sessionPath) PreserveUnreadable(open); _rackAppSettings.ProjectPath = null; Project = new(); Notice("工程未载入，原文件保留：" + e.Message); }
        if (projectPath is not null) _rackAppSettings.ProjectPath = projectPath;
        _theme = DeckTheme.Find(_rackAppSettings.ThemeId); WorkspaceUiDensity.Mode = _rackAppSettings.Density;
        Width = Math.Clamp(_rackAppSettings.Width, 1000, Math.Max(1000, SystemParameters.WorkArea.Width));
        Height = Math.Clamp(_rackAppSettings.Height, 650, Math.Max(650, SystemParameters.WorkArea.Height));
        MinWidth = 1000; MinHeight = 650; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true; Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Yiyin;component/yiyin.ico"));
        if (_rackAppSettings.Maximized) WindowState = WindowState.Maximized;
        AutomationProperties.SetAutomationId(this, "yiyin-main");
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); TrySaveProject(false); };
        BuildShell(); UpdateProjectTitle();
        Loaded += async (_, _) =>
        {
            if (Environment.GetEnvironmentVariable("YIYIN_NO_AUDIO") == "1") return;
            try
            {
                var service = RackService;
                await service.CheckPluginChangesAsync(CancellationToken.None);
                if (_closing || !ReferenceEquals(service, _rackService)) return;
                _rackSettingsStore.Save(_rackAppSettings);
                if (_rackAppSettings.RackLocal.WasRunning) await service.EnsureReadyAsync();
            }
            catch (Exception e) { Notice("机架等待检查：" + e.Message); }
        };
        Closing += OnClosing;
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await RunAsync(() => SaveExplicitAsync(false)); }
        };
    }

    private AudioRackService CreateService()
    {
        var service = new AudioRackService(Project.Rack, _rackAppSettings.RackLocal, _dataDirectory);
        service.Changed += OnRackChanged; return service;
    }

    private void BuildShell()
    {
        if (_notice.Parent is Panel previous) previous.Children.Remove(_notice);
        _rackView?.Dispose(); _theme.Apply(this);
        var root = new DockPanel(); var header = new WrapPanel { Margin = new Thickness(12, 10, 12, 8) };
        WindowChrome.SetIsHitTestVisibleInChrome(header, true);
        var headerRow = new DockPanel();
        var caption = YideckWindowChrome.CaptionButtons(this); DockPanel.SetDock(caption, Dock.Right); headerRow.Children.Add(caption);
        headerRow.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "易音", FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 25, 0), VerticalAlignment = VerticalAlignment.Center });
        var projectMenu = new ContextMenu();
        AddMenu(projectMenu, "新建工程", () => NewProjectAsync()); AddMenu(projectMenu, "打开易音工程…", OpenProjectAsync);
        AddMenu(projectMenu, "保存工程  Ctrl+S", () => SaveExplicitAsync(false)); AddMenu(projectMenu, "另存为…", () => SaveExplicitAsync(true));
        projectMenu.Items.Add(new Separator()); AddMenu(projectMenu, "导入易控工程中的机架…", ImportYideckAsync);
        header.Children.Add(Command("工程 ▾", "yiyin-project", () => { projectMenu.PlacementTarget = header; projectMenu.IsOpen = true; }));
        header.Children.Add(Command("平板连接", "yiyin-tablet", ShowTablet));
        var themes = new ComboBox { Width = 126, ItemsSource = DeckTheme.All, SelectedItem = _theme, Margin = new Thickness(10, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(themes, "yiyin-theme");
        themes.SelectionChanged += (_, _) => { if (themes.SelectedItem is DeckTheme t && t.Id != _theme.Id) { _rackAppSettings.ThemeId = t.Id; _theme = t; RackSettingsStore.Save(_rackAppSettings); BuildShell(); } };
        header.Children.Add(themes);
        var settings = new ContextMenu();
        foreach (var (label, value) in new[] { ("紧凑布局", "compact"), ("标准布局", "standard"), ("触控布局", "touch") })
            AddMenu(settings, label, () => { WorkspaceUiDensity.Mode = value; _rackAppSettings.Density = value; RackSettingsStore.Save(_rackAppSettings); BuildShell(); return Task.CompletedTask; });
        AddMenu(settings, "使用说明", () => { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "使用说明.txt")) { UseShellExecute = true }); return Task.CompletedTask; });
        AddMenu(settings, "更新日志 / 关于", () => { ThemedMessageBox.Show(this, "易音 1.2.0 · 2026-11-20\n\n音频 I/O 可选仅声卡、专业声卡＋USB 或普通多 USB。主输出可分送到多个设备，运行中可试听同组设备的声道分配、关闭撤回或保存。\n\n1.1.0 · 2026-11-16\n\n同步易控 1.4.8 机架：独立轨道设置和插件库、多轨 Solo/Mute、FX Solo Safe、命名 I/O 自动建轨、数值滚轮与编辑、声像双击回中、中键横移、固定轨宽与模块分隔拖动。\n许可：仅个人非商业使用，禁止商用。\n\n1.0.1 · 2026-11-14\n\n修复：补齐最小化、最大化/还原与关闭按钮；顶部工程、平板、主题、设置可正常点击。\n修复：VST3 界面消息代理从首次连接建立，解决 Valhalla 等插件重复连接导致插入失败。\n\n1.0.0：从易控独立的纯音频机架。\n支持声卡 I/O、VST2/VST3、路由、机架库、Studio One 导入与平板演播控制。\n\n工程、配置与安装身份独立。第三方插件和驱动仍需自行安装。", "易音 · 更新日志"); return Task.CompletedTask; });
        header.Children.Add(Command("设置 ▾", "yiyin-settings", () => { settings.PlacementTarget = header; settings.IsOpen = true; }));
        DockPanel.SetDock(headerRow, Dock.Top); root.Children.Add(headerRow);
        DockPanel.SetDock(_notice, Dock.Bottom); _notice.Foreground = DeckTheme.Brush(_theme.Muted); root.Children.Add(_notice);
        _rackView = new(this, _theme, RackHost) { Margin = new Thickness(12, 0, 12, 4) };
        _rackView.SetWorkspaceActions(() => { _rackEditing = !_rackEditing; RackUiChanged?.Invoke(); BuildShell(); }, null, false);
        root.Children.Add(_rackView); Content = root;
    }

    private Button Command(string text, string id, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(13, 5, 13, 5), MinHeight = 34 };
        AutomationProperties.SetAutomationId(button, id); button.Click += (_, _) => action(); return button;
    }
    private void AddMenu(ContextMenu menu, string text, Func<Task> action)
    {
        var item = new MenuItem { Header = text }; item.Click += async (_, _) => await RunAsync(action); menu.Items.Add(item);
    }
    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); } catch (Exception e) { Notice(e.Message); ThemedMessageBox.Show(this, e.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Notice(string text) => _notice.Text = text;
    private void UpdateProjectTitle() => Title = $"易音 · {Project.Name} · {Project.RackName}";
    private void OnRackChanged()
    {
        if (_closing || Interlocked.Exchange(ref _notificationPending, 1) != 0) return;
        _ = Dispatcher.BeginInvoke(() => { Interlocked.Exchange(ref _notificationPending, 0); if (!_closing) RackUiChanged?.Invoke(); }, DispatcherPriority.Background);
    }
    private bool TrySaveProject(bool unused)
    {
        try { if (_rackService is not null) Project.Rack = _rackService.Project; ProjectStore.Save(_sessionPath, Project); if (_notice.Text.StartsWith("自动保存失败：")) Notice("自动恢复副本已保存。"); UpdateProjectTitle(); return true; }
        catch (Exception e) { Notice("自动保存失败：" + e.Message); return false; }
    }
    private void ScheduleRackSave() { _saveTimer.Stop(); _saveTimer.Start(); }
    private async Task CaptureAndSaveAsync(string? path)
    {
        await _rackCommit.WaitAsync();
        try
        {
            if (_rackService is not null)
            {
                var snapshot = await _rackService.CapturePluginStatesAsync();
                MergePluginStates(_rackService.Project, snapshot); Project.Rack = _rackService.Project;
            }
            if (path is not null) ProjectStore.Save(path, Project);
            ProjectStore.Save(_sessionPath, Project); RackSettingsStore.Save(_rackAppSettings);
        }
        finally { _rackCommit.Release(); }
    }
    private async Task SaveExplicitAsync(bool saveAs)
    {
        if (_saveInProgress) return;
        _saveInProgress = true;
        try
        {
            var path = saveAs ? null : _rackAppSettings.ProjectPath;
            if (path is null)
            {
                var dialog = new SaveFileDialog { Filter = "易音工程|*.yiyin", DefaultExt = ".yiyin", FileName = Project.Name + ".yiyin", AddExtension = true };
                if (dialog.ShowDialog(this) != true) return; path = dialog.FileName;
            }
            var oldName = Project.Name;
            Project.Name = Path.GetFileNameWithoutExtension(path);
            try { await CaptureAndSaveAsync(path); }
            catch { Project.Name = oldName; throw; }
            _rackAppSettings.ProjectPath = path; RackSettingsStore.Save(_rackAppSettings); UpdateProjectTitle(); Notice("已保存工程及当前可取得的插件状态。");
        }
        finally { _saveInProgress = false; }
    }
    private async Task<bool> ConfirmSwitchAsync()
    {
        var result = ThemedMessageBox.Show(this, "切换工程前，是否将当前工程保存到文件？\n自动恢复副本仍会保留。", "切换易音工程", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes) { await SaveExplicitAsync(false); if (_rackAppSettings.ProjectPath is null) return false; }
        await CaptureAndSaveAsync(null); return true;
    }
    private async Task NewProjectAsync() { if (await ConfirmSwitchAsync()) await SwitchProjectAsync(new(), null); }
    private async Task OpenProjectAsync()
    {
        var dialog = new OpenFileDialog { Filter = "易音工程|*.yiyin", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var document = ProjectStore.Load(dialog.FileName);
        if (await ConfirmSwitchAsync()) await SwitchProjectAsync(document, dialog.FileName);
    }
    internal async Task<bool> OpenExternalProjectAsync(string path)
    {
        try
        {
            if (_closing || _projectTransition) return false;
            var document = ProjectStore.Load(path);
            if (!await ConfirmSwitchAsync()) return false;
            await SwitchProjectAsync(document, path); return true;
        }
        catch (Exception e) { Notice("工程未打开：" + e.Message); return false; }
    }
    private async Task ImportYideckAsync()
    {
        var dialog = new OpenFileDialog { Filter = "易控工程|*.lightdeck", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var document = ProjectStore.ImportYideck(dialog.FileName, out var converted);
        var text = $"导入 {document.Rack.Tracks.Count} 条当前轨道和 {document.RackSnapshots.Count} 个保存机架。\n声卡映射使用易音本机设置，请重新核对。";
        if (converted > 0) text += $"\n{converted} 条内部音视频轨将转为无内部信号的 BUS；保留插件、发送和下游路由，需要你重新指定实际来源。";
        if (ThemedMessageBox.Show(this, text + "\n原易控文件保持原样。继续？", "导入机架", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (await ConfirmSwitchAsync()) { await SwitchProjectAsync(document, null); Notice(text.Replace('\n', ' ')); }
    }
    private async Task SwitchProjectAsync(RackDocument next, string? path)
    {
        _projectTransition = true; _rackGeneration++; _rackView?.Dispose(); _rackHost = null;
        try
        {
            if (_rackService is not null) { _rackService.Changed -= OnRackChanged; await _rackService.DisposeAsync(); _rackService = null; }
            Project = next; _rackAppSettings.ProjectPath = path; _rackAppSettings.RackLocal.WasRunning = false; _rackEditing = false;
            if (!TrySaveProject(false)) throw new IOException("新工程恢复副本未保存。");
            RackSettingsStore.Save(_rackAppSettings);
        }
        finally { _projectTransition = false; BuildShell(); UpdateProjectTitle(); }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exitReady) return; e.Cancel = true; if (_closing) return;
        if (ThemedMessageBox.Show(this, "保存恢复副本并退出易音？机架声音将停止。", "退出易音", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _closing = true; _saveTimer.Stop();
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            await CaptureAndSaveAsync(_rackAppSettings.ProjectPath);
            _rackAppSettings.Width = RestoreBounds.Width; _rackAppSettings.Height = RestoreBounds.Height; _rackAppSettings.Maximized = WindowState == WindowState.Maximized;
            RackSettingsStore.Save(_rackAppSettings);
            await StopTabletAsync(); _rackView?.Dispose();
            if (_rackService is not null) { _rackService.Changed -= OnRackChanged; await _rackService.DisposeAsync(); }
            _exitReady = true; Close();
        }
        catch (Exception error) { _closing = false; Notice("退出未完成：" + error.Message); ThemedMessageBox.Show(this, error.Message + "\n请处理后再次退出，上一份保存仍保留。", "易音退出未完成"); }
    }
    private static void PreserveUnreadable(string path)
    {
        if (File.Exists(path)) File.Copy(path, path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
    }
}
