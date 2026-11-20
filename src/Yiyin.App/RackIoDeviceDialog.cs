using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.Json;
using LightDeck.Core;

namespace LightDeck.App;

internal sealed partial class RackDeviceDialog : DeckDialog
{
    private readonly IRackUiHost _host;
    private readonly RackDraftGuard _guard;
    private readonly RackProject _project;
    private readonly RackLocalSettings _local;
    private List<RackUiDevice> _devices;
    private readonly ComboBox _api = new(), _card = new(), _rate = new(), _buffer = new();
    private readonly Button _probe = new() { Content = "探测所选驱动" }, _panel = new() { Content = "驱动控制面板" };
    private Button _apply = null!, _cancel = null!;
    private readonly Grid _matrix = new();
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _actual = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _impact = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _clock = new();
    private readonly WrapPanel _groups = new(), _modes = new();
    private FrameworkElement _singleFields = null!, _multiFields = null!;
    private readonly HashSet<string> _selectedIds = new(StringComparer.Ordinal);
    private string _mode;
    private bool Multi => _mode != "single";
    private bool _input = true, _busy, _updating, _closed;
    private string _activeApi;
    private string _operationLabel = "正在读取驱动信息…";
    private Guid? _selection;
    private string _savedDraft = "";
    private Card? SelectedCard => _card.SelectedItem as Card;
    private List<RackIoPort> Ports => _input ? _project.InputPorts : _project.OutputPorts;
    private Dictionary<Guid, RackHardwarePort> Maps => _input ? _local.InputPorts : _local.OutputPorts;
    private static string CardKey(RackUiDevice device) => device.GroupId.Length > 0 ? device.GroupId : "endpoint:" + device.Id;

    public RackDeviceDialog(Window owner, DeckTheme theme, IRackUiHost host, IReadOnlyList<RackUiDevice> devices)
        : base(owner, theme, "易音 1.2.0 Windows 独立音频机架 · 音频 I/O", "", 1100)
    {
        _host = host; _guard = new(host); _devices = devices.ToList(); _project = host.Project.Copy(); _local = RackWorkspaceControl.CopyLocal(host.Local); _activeApi = RackLocalSettings.NormalizeDeviceApi(_local.DeviceApi);
        RackIoMigration.Upgrade(_project, _local);
        Width = Math.Min(1480, SystemParameters.WorkArea.Width - 60); MinWidth = 920; ResizeMode = ResizeMode.CanResizeWithGrip;
        _mode = _local.IoMode; _selectedIds.UnionWith(_local.SelectedEndpointIds);
        BuildDeviceControls();
        var driverActions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; foreach (var b in new[] { _probe, _panel }) { b.Margin = new Thickness(0, 0, 8, 0); driverActions.Children.Add(b); } Body.Children.Add(driverActions);
        AutomationProperties.SetAutomationId(_probe, "rack-device-probe"); AutomationProperties.SetAutomationId(_panel, "rack-device-control-panel"); _probe.Click += (_, _) => _ = ProbeAsync(); _panel.Click += (_, _) => _ = OpenPanelAsync();
        _summary.Foreground = DeckTheme.Brush(theme.Muted); _summary.Margin = new Thickness(0, 10, 0, 10); AutomationProperties.SetAutomationId(_summary, "rack-device-card-hint"); Body.Children.Add(_summary);
        
        _matrix.ColumnDefinitions.Add(new()); _matrix.ColumnDefinitions.Add(new()); Body.Children.Add(_matrix);
        _actual.Foreground = DeckTheme.Brush(theme.Muted); _actual.Margin = new Thickness(0, 8, 0, 0); AutomationProperties.SetAutomationId(_actual, "rack-device-actual"); Body.Children.Add(_actual);
        _impact.Margin = new Thickness(0, 8, 0, 0); _impact.Foreground = DeckTheme.Brush(theme.Text); AutomationProperties.SetAutomationId(_impact, "rack-device-apply-impact"); Body.Children.Add(_impact);
        _api.SelectionChanged += (_, _) => { if (!_updating && _api.SelectedValue is string api && api != _activeApi) _ = LoadApiAsync(api); };
        _card.SelectionChanged += (_, _) => { if (!_updating) { ClearError(); UpdateRates(true); Render(); } }; UpdateRates(); Render();
        _rate.SelectionChanged += (_, _) => { ClearError(); ReconcileStreamSelection(); }; _buffer.SelectionChanged += (_, _) => { ClearError(); ReconcileStreamSelection(); };
        _cancel = AddButton("关闭", Close, id: "rack-device-close");
        _apply = AddButton("保存配置", async () => await ApplyAsync(), true, "rack-device-save");
        Closing += OnDialogClosing;
        _host.Changed += HostChanged; Closed += (_, _) => { _closed = true; _host.Changed -= HostChanged; }; UpdateActionAvailability();
        _savedDraft = DraftState(); var registration = WorkspaceDraftRegistry.Register(this, "音频 I/O", () => !_closed && (_busy || DraftState() != _savedDraft), ApplyAsync, Close); Closed += (_, _) => registration.Dispose();
        if (_mode == "hybrid") Loaded += async (_, _) => { await ChangeModeAsync(_mode); if (!_edited) _savedDraft = DraftState(); };
    }

    private string DraftState() => JsonSerializer.Serialize(new { _project.InputPorts, _project.OutputPorts, TrackPorts = _project.Tracks.Select(t => new { t.Id, t.InputPortId, t.HardwareOutputPortId }), InputMaps = _local.InputPorts, OutputMaps = _local.OutputPorts, _local.OutputMirrors, Mode = _mode, Devices = _selectedIds.Order(), Clock = (_clock.SelectedItem as RackUiDevice)?.Id, Api = _activeApi, Card = SelectedCard?.Id, Rate = _rate.SelectedItem, Buffer = _buffer.SelectedItem }, ProjectStore.JsonOptions);
    private async Task<bool> ApplyAsync()
    {
        if (_busy) return false; _busy = true; UpdateActionAvailability();
        try
        {
            await _previewWork; _guard.RequireCurrent(); var settings = PrepareSettings();
            await _host.ApplyIoAsync(_project, settings);
            _previewTouched = false; _savedDraft = DraftState(); _busy = false; _allowClose = true; DialogResult = true; return true;
        }
        catch (Exception ex)
        {
            var error = await RestorePreviewAsync(); Invalid("音频 I/O 未保存：" + ex.Message + error); return false;
        }
        finally { _busy = false; UpdateActionAvailability(); }
    }

    private void SetCards(string? retainId = null)
    {
        var devices = _devices.Where(d => RackLocalSettings.NormalizeDeviceApi(d.Api) == _activeApi).ToArray();
        var cards = devices.GroupBy(CardKey).Select(g => new Card(g.Key, g.Select(d => d.GroupName).FirstOrDefault(n => n.Length > 0) ?? g.First().Name, g.ToArray())).ToList(); cards.Insert(0, new("", _activeApi == "asio" ? "请选择 ASIO 驱动" : "请选择声卡", []));
        var saved = _local.DeviceApi == _activeApi ? _local.OutputDeviceId.Length > 0 ? devices.FirstOrDefault(d => d.Id == _local.OutputDeviceId) : devices.FirstOrDefault(d => _local.OutputPorts.Values.Any(p => p.DeviceId == d.Id)) : null;
        var selected = retainId is not null ? cards.FirstOrDefault(c => c.Id == retainId) : saved is null ? null : cards.FirstOrDefault(c => c.Id == CardKey(saved));
        if (selected is null && _local.DeviceApi == _activeApi && _local.OutputDeviceId.Length > 0) { selected = new("missing:" + _local.OutputDeviceId, (_local.OutputDeviceName.Length > 0 ? _local.OutputDeviceName : "已保存声卡") + "（当前不可用）", []); cards.Add(selected); }
        _updating = true; try { _card.ItemsSource = cards; _card.SelectedItem = selected ?? cards[0]; } finally { _updating = false; }
    }
    private async Task LoadApiAsync(string api)
    {
        await HardwareActionAsync(async () => { var devices = await _host.EnumerateDevicesAsync(api); _activeApi = api; _devices = devices.ToList(); SetCards(); UpdateRates(true); Render(); }, "正在读取已注册音频设备…");
        if (!Equals(_api.SelectedValue, _activeApi)) { _updating = true; try { _api.SelectedValue = _activeApi; } finally { _updating = false; } }
    }
    private async Task ProbeAsync()
    {
        if (_host.Status.IsRunning || _activeApi != "asio" || DriverDevice is not { Available: true } device) return;
        var cardId = SelectedCard?.Id ?? "";
        await HardwareActionAsync(async () =>
        {
            InvalidateDriver(device, cardId); var result = await _host.ProbeDeviceAsync("asio", device.Id); _devices.RemoveAll(d => d.Id == result.Id); _devices.Add(result); SetCards(cardId); RefreshDeviceControls(); UpdateRates(true); Render();
        }, "正在隔离进程中探测所选驱动…");
    }
    private async Task OpenPanelAsync()
    {
        if (_host.Status.IsRunning || _activeApi != "asio" || DriverDevice is not { Available: true } device) return;
        var cardId = SelectedCard?.Id ?? "";
        await HardwareActionAsync(async () =>
        {
            InvalidateDriver(device, cardId); await _host.ShowDriverControlPanelAsync(device.Id);
        }, "驱动控制面板正在打开，请在该面板完成设置后关闭。");
    }
    private void InvalidateDriver(RackUiDevice device, string cardId)
    {
        _devices.RemoveAll(d => d.Id == device.Id); _devices.Add(device with { ProbeRequired = true, InputChannels = 0, OutputChannels = 0, SampleRates = [], InputChannelNames = [], OutputChannelNames = [] });
        SetCards(cardId); RefreshDeviceControls(); UpdateRates(); Render();
    }
    private async Task HardwareActionAsync(Func<Task> action, string label)
    {
        if (_busy) return; _busy = true; _operationLabel = label; Error.Visibility = Visibility.Collapsed; UpdateActionAvailability();
        try { _guard.RequireCurrent(); await action(); } catch (Exception ex) { Invalid("驱动操作未完成：" + ex.Message); }
        finally { _busy = false; UpdateActionAvailability(); }
    }
    private void HostChanged() { if (!_closed && !Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(UpdateActionAvailability); }
    private void UpdateActionAvailability()
    {
        if (_closed) return; var state = _host.Status; Body.IsEnabled = !_busy && _guard.IsCurrent;
        var driver = _activeApi == "asio"; _probe.Visibility = _panel.Visibility = driver ? Visibility.Visible : Visibility.Collapsed;
        _probe.IsEnabled = _panel.IsEnabled = driver && !_busy && !state.IsRunning && !state.IsBusy && DriverDevice?.Available == true;
        if (_apply is not null) _apply.IsEnabled = !_busy && _guard.IsCurrent; if (_cancel is not null) _cancel.IsEnabled = !_busy;
        if (!_guard.IsCurrent) Invalid("当前机架已切换，请关闭此窗口并重新打开音频 I/O。");
        _actual.Text = _busy ? _operationLabel : state.DriverResetRequired ? "驱动请求重新初始化：请停止机架后重新开启。"
            : state.IsRunning ? $"当前实际 {state.Api.ToUpperInvariant()} · {state.ActualSampleRate} Hz / {state.ActualBufferFrames} 帧。运行中禁止探测或打开 ASIO 控制面板。"
            : Multi ? "主时钟输出驱动机架处理；其他设备的实际延迟取决于驱动与缓冲设置。" : driver ? "ASIO 列表只读取注册项；点击探测才初始化所选驱动，不开始播放。驱动控制面板关闭后请重新探测。" : "同一张声卡可映射多个 Windows 端点；映射端点需使用同一采样率。";
        if (Multi && state.IsRunning && state.Endpoints is { Count: > 0 } endpoints)
        {
            string EndpointText(RackUiEndpointStatus ep)
            {
                var d = _devices.FirstOrDefault(d => d.Id == ep.DeviceId);
                var role = ep.Clock ? "主时钟" : ep.Input ? "输入" : "输出";
                return $"{d?.Name ?? ep.DeviceId}（{role}）" + (ep.Muted ? " 已静音 " + ep.Error : $" {ep.SampleRate} Hz" + (ep.Clock ? "" : $" · {ep.DriftPpm:+0.0;-0.0;0} ppm"));
            }
            _actual.Text += "\n" + string.Join("　", endpoints.Select(EndpointText));
        }
        UpdateImpact();
    }
    private void UpdateRates(bool newDevice = false)
    {
        var old = _rate.SelectedItem is int rate && !newDevice ? rate : _local.SampleRate;
        if (Multi) { UpdateMultiRates(old, newDevice); return; }
        var inputIds = _project.InputPorts.Select(p => p.Id).ToHashSet(); var outputIds = _project.OutputPorts.Select(p => p.Id).ToHashSet();
        var mappedIds = _local.InputPorts.Where(p => inputIds.Contains(p.Key)).Concat(_local.OutputPorts.Where(p => outputIds.Contains(p.Key))).Select(p => p.Value.DeviceId).ToHashSet();
        var endpoints = (SelectedCard?.Devices ?? []).Where(d => d.Available && !d.ProbeRequired && mappedIds.Contains(d.Id)).ToArray();
        if (endpoints.Length == 0) endpoints = (SelectedCard?.Devices ?? []).Where(d => d.Available && !d.ProbeRequired && d.OutputChannels > 0).ToArray();
        var rates = new[] { 44100, 48000 }.Where(r => endpoints.Length > 0 && endpoints.All(d => d.SampleRates?.Contains(r) == true)).ToArray();
        var driver = _activeApi == "asio" ? endpoints.FirstOrDefault() : null;
        if (newDevice && driver is not null) old = driver.CurrentSampleRate > 0 && rates.Contains(driver.CurrentSampleRate) ? driver.CurrentSampleRate : driver.PreferredSampleRate;
        _rate.ItemsSource = rates; _rate.SelectedItem = rates.Contains(old) ? old : rates.Cast<int?>().FirstOrDefault();
        var oldBuffer = !newDevice && _buffer.SelectedItem is int frames ? frames : _local.BufferSize;
        var buffers = _activeApi == "asio" ? driver is null ? [] : AsioBufferChoices(driver) : new[] { 64, 128, 256, 512, 1024, 2048 }.Append(_local.BufferSize).Distinct().Order().ToArray();
        if (newDevice && driver is not null) oldBuffer = driver.BufferPreferredFrames;
        _buffer.ItemsSource = buffers; _buffer.SelectedItem = buffers.Contains(oldBuffer) ? oldBuffer : _activeApi == "asio" ? null : buffers.Cast<int?>().FirstOrDefault();
        if (SelectedCard?.Devices.Count > 0)
        {
            var unprobed = SelectedCard.Devices.Any(d => d.ProbeRequired); var ids = SelectedCard.Devices.Select(d => d.Id).ToHashSet(); var unmatched = mappedIds.Count(id => !ids.Contains(id));
            _summary.Text = _activeApi == "asio" ? unprobed ? $"{SelectedCard.Name} · 尚未探测，请点击「探测所选驱动」读取真实声道、采样率和缓冲范围。" : $"{SelectedCard.Name} · 当前 {driver?.CurrentSampleRate ?? 0} Hz；驱动报告输入延迟 {Latency(driver?.InputLatencyFrames)} / 输出延迟 {Latency(driver?.OutputLatencyFrames)}。缓冲 {driver?.BufferMinFrames}–{driver?.BufferMaxFrames} 帧，首选 {driver?.BufferPreferredFrames} 帧；实际值以运行回读为准。"
                : $"{SelectedCard.Name} · 显示 Windows 中开放的硬件/虚拟端口，只列出映射端口共同支持的采样率，不更改 Windows 设置。";
            if (unmatched > 0) _summary.Text += $" {unmatched} 个原硬件端点未匹配，原映射保留，请重新点选。";
            if (_activeApi == "asio" && !unprobed && buffers.Length == 0) _summary.Text += " 驱动没有报告可用缓冲范围，不能应用。";
            else if (_activeApi == "asio" && !unprobed && _buffer.SelectedItem is null) _summary.Text += " 原缓冲值不在驱动范围内，请明确选择新的请求值。";
        }
        else _summary.Text = "未选择可用声卡。请连接设备后重新打开此窗口；不会自动替换已保存的设备。";
        UpdateActionAvailability();
    }
    private static string Latency(int? value) => value is >= 0 ? value + " 帧" : "未知";
    internal static int[] AsioBufferChoices(RackUiDevice driver) => driver.BufferMinFrames <= 0 || driver.BufferMaxFrames < driver.BufferMinFrames || driver.BufferGranularity < -1 ? [] : Enumerable.Range(8, 8192 - 8 + 1).Where(frames => frames >= driver.BufferMinFrames && frames <= driver.BufferMaxFrames && (driver.BufferGranularity == -1 ? (frames & (frames - 1)) == 0 : driver.BufferGranularity > 0 ? (frames - driver.BufferMinFrames) % driver.BufferGranularity == 0 : driver.BufferMinFrames == driver.BufferMaxFrames && frames == driver.BufferMinFrames)).ToArray();

    private void ClearError() { Error.Visibility = Visibility.Collapsed; UpdateImpact(); }
    private void UpdateImpact()
    {
        _impact.Text = Multi && MatchesRunningStreams()
            ? "勾选声道可即时试听。保存配置保留当前分配，关闭恢复上次保存的分配。"
            : "选择设备并分配输入、输出声道后保存配置。新增输入轨的监听需手动开启。";
    }
    private void AddPort(int channels)
    {
        if (Ports.Count >= 256) { Invalid("每类最多 256 个通道。"); return; }
        var port = new RackIoPort { Channels = channels, Name = (_input ? "输入 " : "输出 ") + (Ports.Count + 1) }; Ports.Add(port); _selection = port.Id; ClearError(); Render();
    }
    private void RemovePort()
    {
        var port = Ports.FirstOrDefault(p => p.Id == _selection); if (port is null) { Invalid("请先点击要移除的通道名称。"); return; }
        var used = _project.Tracks.Where(t => (_input ? t.InputPortId : t.HardwareOutputPortId) == port.Id).ToArray();
        if (ThemedMessageBox.Show(this, $"移除“{port.Name}”？" + (used.Length == 0 ? "" : $"\n将清除 {used.Length} 条轨道对此通道的选择；轨道本身保留。"), "移除音频 I/O", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        foreach (var track in used) { if (_input) track.InputPortId = null; else track.HardwareOutputPortId = null; }
        Ports.Remove(port); Maps.Remove(port.Id); if (!_input) _local.OutputMirrors.Remove(port.Id); _selection = null; ClearError(); UpdateRates(); Render();
    }
    private void SelectCell(RackIoPort port, RackUiDevice device, int channel)
    {
        _selection = port.Id; ClearError();
        if (Multi) { SelectMultiCell(port, device, channel); Render(); QueuePreview(); return; }
        var selected = Maps.TryGetValue(port.Id, out var old) && old.DeviceId == device.Id ? old.Channels.ToList() : [];
        if (selected.Contains(channel)) selected.Remove(channel);
        else if (port.Channels == 1) selected = [channel];
        else { if (selected.Count == 2) selected.RemoveAt(1); selected.Add(channel); }
        if (selected.Count == 0) Maps.Remove(port.Id); else Maps[port.Id] = new() { DeviceId = device.Id, DeviceName = device.Name, Channels = selected.ToArray() };
        UpdateRates(); Render();
    }
    private RackLocalSettings PrepareSettings()
    {
        var settings = RackWorkspaceControl.CopyLocal(_local);
        settings.IoMode = _mode;
        if (Multi) PrepareMultiSettings(settings); else PrepareSingleSettings(settings);
        settings.RoutePlayer = false;
        _project.PlaybackTrackId = null;
        _project.Validate(); settings.Validate(); return settings;
    }
    private void PrepareSingleSettings(RackLocalSettings settings)
    {
        var card = SelectedCard ?? throw new ArgumentException("请选择声卡。");
        if (card.Devices.Count == 0 || card.Devices.Any(d => !d.Available)) throw new ArgumentException("所选声卡当前不可用。");
        if (card.Devices.Any(d => d.ProbeRequired)) throw new ArgumentException("请先停止机架并探测所选 ASIO 驱动，确认真实声道和缓冲范围。");
        foreach (var input in new[] { true, false })
        {
            var ports = input ? _project.InputPorts : _project.OutputPorts; var maps = input ? _local.InputPorts : _local.OutputPorts;
            foreach (var port in ports)
            {
                port.Name = port.Name.Trim();
                if (!maps.TryGetValue(port.Id, out var map)) continue;
                var endpoint = card.Devices.FirstOrDefault(d => d.Id == map.DeviceId) ?? throw new ArgumentException($"“{port.Name}”的硬件端点不属于当前可用声卡，请重新映射。");
                if (map.Channels.Length != port.Channels || map.Channels.Any(c => c < 0 || c >= (input ? endpoint.InputChannels : endpoint.OutputChannels))) throw new ArgumentException($"请为“{port.Name}”点选完整的 {port.Channels} 个硬件声道。");
            }
        }
        var outputIds = _project.OutputPorts.Select(p => p.Id).ToHashSet();
        var usedOutput = _local.OutputPorts.Where(p => outputIds.Contains(p.Key)).Select(p => p.Value.DeviceId).Distinct().ToArray();
        var output = card.Devices.FirstOrDefault(d => d.Id == _local.OutputDeviceId && usedOutput.Contains(d.Id)) ?? card.Devices.FirstOrDefault(d => usedOutput.Contains(d.Id) && d.OutputChannels > 0);
        if (output is null) throw new ArgumentException("请在右侧输出矩阵至少添加并映射一个硬件输出。");
        var inputIds = _project.InputPorts.Select(p => p.Id).ToHashSet();
        var inputDevice = card.Devices.FirstOrDefault(d => _local.InputPorts.Any(p => inputIds.Contains(p.Key) && p.Value.DeviceId == d.Id));
        settings.SelectedEndpointIds.Clear(); settings.OutputMirrors.Clear();
        settings.DeviceApi = output.Api; settings.OutputDeviceId = output.Id; settings.OutputDeviceName = output.Name;
        settings.InputDeviceId = inputDevice?.Id ?? ""; settings.InputDeviceName = inputDevice?.Name ?? "";
        if (_rate.SelectedItem is not int rate || _buffer.SelectedItem is not int buffer) throw new ArgumentException(_activeApi == "asio" ? "请选择驱动明确支持的采样率和请求缓冲帧数；约束未知时请重新探测。" : "所选端点需具有共同采样率，请先在 Windows 声音设置中设置相同格式。");
        if (_activeApi == "asio" && !AsioBufferChoices(output).Contains(buffer)) throw new ArgumentException("请求缓冲帧数不符合 ASIO 驱动约束。");
        settings.SampleRate = rate; settings.BufferSize = buffer;
    }
    private sealed record ApiChoice(string Id, string Name);
    private sealed record Card(string Id, string Name, IReadOnlyList<RackUiDevice> Devices) { public override string ToString() => Name; }
}
