using System.Text.Json;
using LightDeck.Core;

namespace LightDeck.App;

internal sealed record TabletRackControl(Guid TrackId, string Control, Guid? SendId, double? Value, bool? Enabled, Guid? PluginId = null);
internal sealed record TabletRackConnection(Guid Id, Guid TargetId, string Name, string Color, string Kind, double GainDb, bool Enabled, bool Main, bool PreFader);
internal sealed record TabletRackPlugin(Guid Id, string Name, bool Bypass, bool Available, string? Error);
internal readonly record struct TabletRackLayout(double InsertHeight, double SendHeight, double OutputHeight, double FaderHeight);
internal sealed record TabletRackTrack(Guid Id, string Name, string Color, string Kind, double GainDb, double Pan, bool Enabled,
    TabletRackConnection[] Connections, RackUiMeter Meter, string SourceLabel, string HardwareOutputLabel, TabletRackPlugin[] Plugins, double? PluginLatencyMs);
internal sealed record TabletRackState(bool ControlsEnabled, bool Editing, bool IsRunning, string Message, TabletRackTrack[] Tracks, RackUiOutputSignal? OutputSignal,
    TabletRackLayout Layout, string DeviceLabel, string Status, string DisplayViewName = "原显示布局", bool CompactView = false, string Density = "standard", RackRowAppearance? RowAppearance = null);

public sealed partial class MainWindow
{
    // Subscribing to the existing UI notifications does not construct or start an engine.
    internal event Action? TabletRackChanged { add => RackUiChanged += value; remove => RackUiChanged -= value; }
    internal RackProject TabletRackProject => _rackService?.Project ?? Project.Rack;
    internal RackLocalSettings TabletRackLocal => RackAppSettings.RackLocal;
    internal bool TabletRackEditing => _rackEditing;
    internal string TabletRackStructureIdentity()
    {
        var rack = TabletRackProject; var local = TabletRackLocal;
        return JsonSerializer.Serialize(new
        {
            Tracks = rack.Tracks.Select(t => new
            {
                t.Id, t.Name, t.Kind, t.Color, t.Visible, t.Channels, t.InputPortId, t.HardwareOutputPortId, t.OutputTrackId,
                Sends = t.Sends.Select(s => new { s.Id, s.TargetTrackId }).ToArray(),
                Plugins = t.Inserts.Select(p => new { p.Id, p.Identity.Key, p.Identity.Name }).ToArray()
            }),
            rack.InputPorts, rack.OutputPorts, LocalInputPorts = local.InputPorts, LocalOutputPorts = local.OutputPorts,
            local.RowAppearance, local.DeviceApi, local.OutputDeviceName, local.InsertHeight, local.SendHeight, local.OutputHeight, local.FaderHeight, local.CompactView,
            rack.ActiveDisplayViewId, DisplayViewName = rack.DisplayViews.FirstOrDefault(v => v.Id == rack.ActiveDisplayViewId)?.Name,
            Density = WorkspaceUiDensity.Mode
        });
    }

    internal TabletRackState ReadTabletRackState()
    {
        Dispatcher.VerifyAccess();
        var rack = TabletRackProject; var local = TabletRackLocal; var state = _rackService?.Status ?? new RackUiStatus();
        var enabled = Project.TabletAccess.AllowRack && !_closing && !_projectTransition && !_rackEditing && state.IsRunning && !state.IsBusy;
        string PhysicalChannels(RackTrack track, bool input)
        {
            var port = (input ? rack.InputPorts : rack.OutputPorts).FirstOrDefault(p => p.Id == (input ? track.InputPortId : track.HardwareOutputPortId));
            if (port is null) return "未分配" + (input ? "输入" : "输出");
            return (input ? local.InputPorts : local.OutputPorts).TryGetValue(port.Id, out var map)
                ? port.Name + " · " + map.DeviceName + " · " + string.Join(" / ", map.Channels.Select(c => c + 1))
                : port.Name + " · 本机硬件通道未分配";
        }
        TabletRackPlugin Plugin(RackPluginInstance plugin)
        {
            var error = state.PluginErrors?.GetValueOrDefault(plugin.Id);
            var catalog = local.PluginCatalog.FirstOrDefault(c => c.Identity.Key == plugin.Identity.Key);
            // Scanner/native exception details can contain local binary paths;
            // the tablet needs availability, while details stay on the computer.
            if (!string.IsNullOrWhiteSpace(error)) error = error == "插件文件缺失，请安装或重新扫描。" ? error : "插件加载或运行异常，请在电脑查看详细原因。";
            if (string.IsNullOrWhiteSpace(error) && catalog is { Available: false }) error = "插件不可用，请在电脑查看扫描结果。";
            if (string.IsNullOrWhiteSpace(error) && !local.PluginLocations.ContainsKey(plugin.Identity.Key)) error = "本机未登记插件位置";
            return new(plugin.Id, plugin.Identity.Name, plugin.Bypass, string.IsNullOrWhiteSpace(error), error);
        }
        // Match the desktop's three visual groups while preserving order within each group.
        var tracks = rack.Tracks.Where(t => t.Visible).OrderBy(t => t.Kind is RackTrackKind.Output ? 2 : t.Kind is RackTrackKind.Bus or RackTrackKind.FX ? 1 : 0).Select(t =>
        {
            var connections = new List<TabletRackConnection>();
            if (t.OutputTrackId is { } main && rack.FindTrack(main) is { } mainTarget)
                connections.Add(new(t.Id, main, mainTarget.Name, mainTarget.Color, mainTarget.Kind.ToString(), t.MainOutputGainDb, t.MainOutputEnabled, true, false));
            foreach (var send in t.Sends)
                if (rack.FindTrack(send.TargetTrackId) is { } target)
                    connections.Add(new(send.Id, target.Id, target.Name, target.Color, target.Kind.ToString(), send.GainDb, send.Enabled, false, send.PreFader));
            var latency = t.Inserts.Count == 0 ? 0d : state.PluginLatencyMilliseconds?.GetValueOrDefault(t.Id, double.NaN) ?? double.NaN;
            return new TabletRackTrack(t.Id, t.Name, t.Color, t.Kind.ToString(), t.GainDb, t.Pan,
                !t.Mute && (t.Kind is not (RackTrackKind.Mono or RackTrackKind.Stereo) || t.Monitor),
                connections.ToArray(), state.Meters?.GetValueOrDefault(t.Id) ?? new(),
                "来源 · " + (t.Kind is RackTrackKind.Playback ? "内部音视频" : t.Kind is RackTrackKind.Mono or RackTrackKind.Stereo ? PhysicalChannels(t, true) : "上游轨道"),
                t.Kind is RackTrackKind.Output ? PhysicalChannels(t, false) : "", t.Inserts.Select(Plugin).ToArray(),
                double.IsFinite(latency) && latency >= 0 ? latency : null);
        }).ToArray();
        var actual = state.ActualSampleRate > 0 && state.ActualBufferFrames > 0 ? $"{state.ActualSampleRate / 1000d:0.#} kHz · {state.ActualBufferFrames} 帧" : "实际采样 / 缓冲待连接";
        return new(enabled, _rackEditing, state.IsRunning,
            _projectTransition ? "电脑正在切换工程" : _rackEditing ? "电脑正在编辑机架，完成编辑后恢复平板控制" :
            !state.IsRunning ? "机架未运行，请在电脑检查并开启" : state.IsBusy ? "电脑正在更新机架" : "机架运行中 · 平板演播控制", tracks, state.OutputSignal,
            new(local.InsertHeight, local.SendHeight, local.OutputHeight, local.FaderHeight),
            local.DeviceApi.ToUpperInvariant() + " · " + (local.OutputDeviceName.Length > 0 ? local.OutputDeviceName : "未选择声卡"),
            (state.DriverResetRequired ? "需要重启驱动" : state.Message) + " · " + actual,
            rack.DisplayViews.FirstOrDefault(v => v.Id == rack.ActiveDisplayViewId)?.Name ?? "原显示布局", local.CompactView, WorkspaceUiDensity.Mode, local.RowAppearance.Copy());
    }

    internal void ValidateTabletRackControl(TabletRackControl command) => BuildTabletRackControl(command);
    private RackTrack BuildTabletRackControl(TabletRackControl command)
    {
        Dispatcher.VerifyAccess();
        if (!Project.TabletAccess.AllowRack || _closing || _projectTransition) throw new InvalidOperationException("电脑未开放机架，或工程正在切换。");
        if (_rackEditing) throw new InvalidOperationException("电脑正在编辑机架，请完成编辑后操作。");
        if (_rackService is not { Status.IsRunning: true, Status.IsBusy: false } service) throw new InvalidOperationException("机架未就绪，请在电脑检查运行状态。");
        var track = service.Project.FindTrack(command.TrackId)?.Copy() ?? throw new InvalidOperationException("机架轨道已不存在。");
        if (!track.Visible) throw new InvalidOperationException("此轨道未显示在平板机架中。");
        double Value(double minimum, double maximum)
        {
            if (command.Value is not { } value || !double.IsFinite(value) || value < minimum || value > maximum) throw new ArgumentException("机架数值超出有效范围。");
            return value;
        }
        bool Enabled() => command.Enabled ?? throw new ArgumentException("缺少机架开关状态。");
        RackSend Send() => track.Sends.FirstOrDefault(s => s.Id == command.SendId) ?? throw new InvalidOperationException("发送连接已改变，请等待同步。");
        switch (command.Control)
        {
            case "gain": track.GainDb = Value(-90, 12); break;
            case "pan": track.Pan = Value(-1, 1); break;
            case "enabled": var on = Enabled(); track.Mute = !on; if (track.Kind is RackTrackKind.Mono or RackTrackKind.Stereo) track.Monitor = on; break;
            case "mainGain": if (track.OutputTrackId is null) throw new InvalidOperationException("主输出已移除。"); track.MainOutputGainDb = Value(-90, 12); break;
            case "mainEnabled": if (track.OutputTrackId is null) throw new InvalidOperationException("主输出已移除。"); track.MainOutputEnabled = Enabled(); break;
            case "sendGain": Send().GainDb = Value(-90, 12); break;
            case "sendEnabled": Send().Enabled = Enabled(); break;
            case "sendPreFader": Send().PreFader = Enabled(); break;
            case "pluginBypass":
                var plugin = track.Inserts.FirstOrDefault(p => p.Id == command.PluginId) ?? throw new InvalidOperationException("插入效果器已改变，请等待同步。");
                plugin.Bypass = Enabled(); break;
            default: throw new ArgumentException("平板只允许机架演播控制。");
        }
        return track;
    }

    internal Task ApplyTabletRackControlAsync(TabletRackControl command, Func<bool> stillAuthorized)
    {
        if (!stillAuthorized()) throw new OperationCanceledException("机架操作已失效。");
        // Capture the current track at application time, not the first drag event.
        // MainRackHost retains generation checks, the native control queue and saving.
        return RackHost.UpdateTrackAsync(BuildTabletRackControl(command));
    }
}
