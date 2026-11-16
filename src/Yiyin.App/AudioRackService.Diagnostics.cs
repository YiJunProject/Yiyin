using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LightDeck.Core;

namespace LightDeck.App;

public sealed partial class AudioRackService
{
    private readonly RackDiagnosticState _diagnostics = new();
}

// Observed only by managed control/status work, never by the native audio callback.
// Keep transient device IDs in memory solely to resolve friendly labels; never log them.
internal sealed class RackDiagnosticState(Func<long>? clock = null)
{
    private readonly object _gate = new();
    private readonly Func<long> _clock = clock ?? Stopwatch.GetTimestamp;
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _counters = new(), _pending = new();
    private bool _expectedRunning, _reset, _faulted;
    private string _errorCode = "", _actualFormat = "", _api = "wasapi";
    private long _nextCounterReport;

    internal static string FriendlyName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(c => char.IsControl(c) || c is '\\' or '/' or ':' or '@' or '=' or '{' or '}')
            || new[] { "password", "secret", "token", "serial", "credential", "authorization" }.Any(word => value.Contains(word, StringComparison.OrdinalIgnoreCase))) return "名称不可记录";
        return value;
    }
    internal static string Api(string value) => value is "asio" or "wasapi" ? value : "unknown";
    internal static string Operation(string value) => value is "ping" or "devices" or "probeDevice" or "driverControlPanel" or "configure" or "start" or "stop" or "shutdown"
        or "meters" or "status" or "setGraph" or "beginGraphEdit" or "commitGraphEdit" or "rollbackGraphEdit" or "clearGraph" or "setControls" or "pluginState" or "editor" or "attachMedia" or "detachMedia" or "offline" ? value : "other";

    internal static int? AsioErrorCode(string error)
    {
        foreach (var operation in new[] { "getChannels", "getSampleRate", "getBufferSize", "getChannelInfo", "controlPanel", "setSampleRate", "createBuffers", "getLatencies", "start" })
        {
            var prefix = "ASIO " + operation + " failed (";
            if (!error.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var end = error.IndexOf(')', prefix.Length);
            if (end > prefix.Length && end - prefix.Length <= 11 && int.TryParse(error.AsSpan(prefix.Length, end - prefix.Length), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code)) return code;
        }
        return null;
    }

    // Match only controlled native source prefixes; the remainder may contain a
    // driver-supplied message, plugin path, serial number or other private content.
    internal static string NativeCode(string? error) => error switch
    {
        null or "" => "none",
        var e when e.StartsWith("ASIO driver requested reset", StringComparison.Ordinal) || e == "ASIO driver requires reconfiguration" => "asio-reset-required",
        "ASIO selected sample rate is unsupported" or "ASIO driver did not apply the explicitly selected sample rate" => "asio-rate-rejected",
        "ASIO selected buffer size does not match driver constraints" => "asio-buffer-rejected",
        var e when e.StartsWith("ASIO initialization failed:", StringComparison.Ordinal) => "asio-initialization-failed",
        var e when e.StartsWith("Selected x64 ASIO driver", StringComparison.Ordinal) => "asio-driver-unavailable",
        var e when e.StartsWith("ASIO start failed (", StringComparison.Ordinal) => "asio-start-failed",
        var e when e.StartsWith("ASIO createBuffers failed (", StringComparison.Ordinal) => "asio-buffer-creation-failed",
        var e when e.StartsWith("ASIO setSampleRate failed (", StringComparison.Ordinal) => "asio-rate-rejected",
        var e when e.StartsWith("ASIO driver returned a null", StringComparison.Ordinal) => "asio-invalid-buffer",
        var e when e.StartsWith("Plugin requested I/O/latency restart", StringComparison.Ordinal) => "plugin-restart-required",
        var e when e.StartsWith("VST3 rejects requested mono/stereo bus arrangement", StringComparison.Ordinal) => "plugin-bus-arrangement-rejected",
        var e when e.StartsWith("snapshotFailed:", StringComparison.Ordinal) || e == "VST2 chunk state failed" => "plugin-current-state-failed",
        "Graph channel exceeds its selected endpoint range" => "graph-channel-range",
        "Graph hardware endpoints are unresolved; configure and reapply graph before start" or "Graph hardware endpoint is not configured" or "Each hardware graph node requires an explicit endpointId" => "graph-endpoint-unresolved",
        "Configured WASAPI endpoint unavailable" or "Configured WASAPI endpoint disappeared before start" or "An essential WASAPI endpoint stopped; all rack outputs stopped." => "wasapi-endpoint-unavailable",
        var e when e.StartsWith("WASAPI open failed:", StringComparison.Ordinal) || e.StartsWith("WASAPI endpoint open failed:", StringComparison.Ordinal) => "wasapi-open-failed",
        var e when e.StartsWith("WASAPI start failed:", StringComparison.Ordinal) || e.StartsWith("WASAPI endpoint start failed:", StringComparison.Ordinal) => "wasapi-start-failed",
        var e when e.StartsWith("capture bridge overflow at ", StringComparison.Ordinal) || e.StartsWith("capture bridge underflow at ", StringComparison.Ordinal)
            || e.StartsWith("output bridge overflow at ", StringComparison.Ordinal) || e.StartsWith("output bridge underflow at ", StringComparison.Ordinal) => "endpoint-buffer-failure",
        var e when e.StartsWith("WASAPI endpoint failure at ", StringComparison.Ordinal) => "wasapi-endpoint-unavailable",
        var e when e.StartsWith("unexpected endpoint callback shape at ", StringComparison.Ordinal) => "endpoint-callback-invalid",
        var e when e.StartsWith("Device needs larger block:", StringComparison.Ordinal) || e == "Endpoint needs a larger block than prepared graph" => "buffer-too-small",
        _ => "native-error-unknown"
    };
    internal static string Reason(string code) => code switch
    {
        "asio-reset-required" => "驱动要求重新配置；可能涉及重置、格式、延迟或回调停止，现有信号不足以认定物理掉线。",
        "asio-rate-rejected" => "驱动未接受明确选择的采样率。",
        "asio-buffer-rejected" or "asio-buffer-creation-failed" or "asio-invalid-buffer" or "buffer-too-small" => "缓冲配置或驱动缓冲无效；不能仅据此认定设备掉线。",
        "endpoint-buffer-failure" or "endpoint-callback-invalid" => "音频端点缓冲或回调形状异常，输出已由引擎保护停止；不等于已确认物理掉线。",
        "asio-driver-unavailable" => "所选驱动未注册、不可加载或其组件不可用；未自动切换设备。",
        "asio-initialization-failed" or "asio-start-failed" => "驱动初始化或启动失败；原生驱动的自由文本未写入日志。",
        "plugin-restart-required" => "效果器要求 I/O 或延迟重建，或处理失败；不是声卡掉线的充分证据。",
        "plugin-bus-arrangement-rejected" => "已找到 VST3 插件，但它未接受当前轨道的单声道／立体声配置。请检查插件版本与轨道声道；重新扫描不能解决声道不兼容。",
        "plugin-current-state-failed" => "未能在音频继续处理时取得效果器的当前完整状态，或读取期间检测到回调超时；本次操作未提交，原图保留。未使用旧状态或干声代替。该效果器的在线状态兼容性需要检查。",
        "graph-channel-range" => "工程中的物理通道超出机架上次准备的声卡通道范围。停止时仍可管理效果器；请在音频 I/O 中重新确认映射后手动启动。",
        "graph-endpoint-unresolved" => "工程的输入输出映射尚未匹配已准备的声卡端点。停止时仍可管理效果器；请在音频 I/O 中确认设备和通道后手动启动。",
        "engine-restart-required" => "引擎要求重新准备，但没有明确区分驱动、端点或效果器原因。",
        "wasapi-endpoint-unavailable" => "所选音频端点不可用或已停止，具体系统或硬件原因仍需进一步确认。",
        "wasapi-open-failed" or "wasapi-start-failed" => "所选音频端点打开或启动失败，未记录驱动自由文本。",
        "ipc-timeout" => "控制请求未在允许时间内完成；可能涉及驱动、效果器或通信阻塞，不能单凭超时认定设备掉线。",
        "ipc-cancelled" => "控制请求已取消，结果未知时已终止自有引擎以防迟到操作。",
        _ => "原因未知；现有引擎状态没有提供可安全确认的细分原因。"
    };

    public void StartRequested(RackLocalSettings local)
    {
        lock (_gate)
        {
            FlushCounters(); _expectedRunning = false; _faulted = false; _reset = false; _errorCode = ""; _actualFormat = ""; _api = Api(local.DeviceApi);
            _counters.Clear(); _pending.Clear(); _nextCounterReport = 0;
            DiagnosticLog.Write("机架", "rack-start-requested", DiagnosticLevel.Info, "请求启动机架", "按当前明确选择的设备与参数启动。",
                ("api", _api), ("requestedSampleRate", local.SampleRate), ("requestedBufferFrames", local.BufferSize));
        }
    }
    public void Configuration(RackLocalSettings local, IReadOnlyList<RackUiDevice> devices)
    {
        lock (_gate)
        {
            foreach (var device in devices) _names[device.Id] = FriendlyName(device.Name);
            DiagnosticLog.Write("机架", "rack-configuration-requested", DiagnosticLevel.Info, "机架设备配置请求", "设备名称来自本次枚举或所选驱动查询，不记录端点标识和硬件序列号。",
                ("api", Api(local.DeviceApi)), ("requestedOutputDevice", Name(local.OutputDeviceId)), ("requestedInputDevice", Name(local.InputDeviceId)),
                ("requestedSampleRate", local.SampleRate), ("requestedBufferFrames", local.BufferSize));
        }
    }
    private string Name(string id) => _names.GetValueOrDefault(id, "未确认名称");
    public void Started()
    {
        lock (_gate) { _expectedRunning = true; DiagnosticLog.Write("机架", "rack-started", DiagnosticLevel.Info, "机架启动请求已确认", "等待状态回报确认实际格式。", ("api", _api)); }
    }
    public void StopRequested(string reason)
    {
        lock (_gate)
        {
            _expectedRunning = false; FlushCounters();
            DiagnosticLog.Write("机架", "rack-stop-requested", DiagnosticLevel.Info, "主动停止机架", "这是主动停止请求，不作为设备掉线。", ("intent", reason));
        }
    }
    public void Stopped(string reason) => DiagnosticLog.Write("机架", "rack-stopped", DiagnosticLevel.Info, "机架停止已确认", "停止由明确的生命周期操作发起。", ("intent", reason));
    public void Fault(Exception exception)
    {
        lock (_gate)
        {
            if (_faulted) return;
            _faulted = true; _expectedRunning = false; FlushCounters();
            var code = exception.Data["rackDiagnosticCode"] as string;
            code = code is "asio-reset-required" or "asio-rate-rejected" or "asio-buffer-rejected" or "asio-initialization-failed" or "asio-driver-unavailable"
                or "asio-start-failed" or "asio-buffer-creation-failed" or "asio-invalid-buffer" or "plugin-restart-required" or "wasapi-endpoint-unavailable"
                or "wasapi-open-failed" or "wasapi-start-failed" or "buffer-too-small" or "endpoint-buffer-failure" or "endpoint-callback-invalid"
                or "native-error-unknown" or "ipc-timeout" or "ipc-cancelled" or "process-exit-unknown" ? code : "managed-failure";
            DiagnosticLog.Write("机架", "rack-failure", DiagnosticLevel.Error, "机架控制操作失败", Reason(code),
                [.. DiagnosticLog.ExceptionFacts(exception), ("causeCode", code), ("api", _api)]);
        }
    }
    public void Observe(JsonElement status)
    {
        lock (_gate)
        {
            var running = AudioRackService.Flag(status, "running"); var reset = AudioRackService.Flag(status, "driverResetRequired");
            var error = NativeCode(AudioRackService.Text(status, "error"));
            var api = Api(AudioRackService.Text(status, "api"));
            var rate = AudioRackService.Number(status, "sampleRate"); var buffer = AudioRackService.Number(status, "bufferFrames");
            var actualName = status.TryGetProperty("mediaClockOutputDeviceId", out var device) && device.ValueKind == JsonValueKind.String ? Name(device.GetString() ?? "") : "引擎未报告设备名称";
            var format = $"{api}|{rate}|{buffer}|{actualName}";
            if (format != _actualFormat)
            {
                _actualFormat = format;
                DiagnosticLog.Write("机架", "rack-actual-format", DiagnosticLevel.Info, "引擎报告实际音频格式", "设备名称由引擎配置的时钟端点映射；不代表额外硬件身份验证。",
                    ("api", api), ("actualDevice", actualName), ("actualSampleRate", rate), ("actualBufferFrames", buffer));
            }
            if (reset && !_reset) DiagnosticLog.Write("机架", "rack-driver-reset-required", DiagnosticLevel.Warning, "驱动要求重置机架", Reason("asio-reset-required"),
                ("api", api), ("running", running), ("notificationFlags", NestedCount(status, "asio", "notificationFlags")));
            _reset = reset;
            if (error != "none" && error != _errorCode) DiagnosticLog.Write("机架", "rack-native-status-error", DiagnosticLevel.Error, "引擎状态报告异常", Reason(error), ("causeCode", error), ("api", api));
            _errorCode = error;
            if (_expectedRunning && !running)
            {
                _expectedRunning = false;
                var cause = reset ? "asio-reset-required" : error != "none" ? error : AudioRackService.Flag(status, "restartRequired") ? "engine-restart-required" : "running-false-unknown";
                DiagnosticLog.Write("机架", "rack-unexpected-stop", DiagnosticLevel.Error, "机架未收到主动停止请求却已停止", Reason(cause), ("causeCode", cause), ("api", api));
            }
            Count("xruns", ReadCount(status, "xruns")); Count("deadlineMisses", ReadCount(status, "callbackDeadlineMisses"));
            Count("gateSkips", ReadCount(status, "gateSkipCount")); Count("mediaUnderruns", ReadCount(status, "mediaUnderruns"));
            Count("outputOverRangeSamples", NestedCount(status, "outputSignal", "overRangeSamples")); Count("outputNonFiniteSamples", NestedCount(status, "outputSignal", "nonFiniteSamples"));
            Count("integerOutputOverRangeSamples", NestedCount(status, "asio", "integerOutputOverRangeSamples")); Count("samplePositionDiscontinuities", NestedCount(status, "asio", "samplePositionDiscontinuities"));
            long under = 0, over = 0;
            if (status.TryGetProperty("endpoints", out var endpoints) && endpoints.ValueKind == JsonValueKind.Array)
                foreach (var endpoint in endpoints.EnumerateArray()) { under = Add(under, ReadCount(endpoint, "bridgeUnderflows")); over = Add(over, ReadCount(endpoint, "bridgeOverflows")); }
            Count("bridgeUnderflows", under); Count("bridgeOverflows", over);
            if (_pending.Count != 0 && _clock() >= _nextCounterReport) { FlushCounters(); _nextCounterReport = _clock() + Stopwatch.Frequency * 10; }
        }
    }
    private static long ReadCount(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? Math.Max(0, n) : 0;
    private static long NestedCount(JsonElement root, string key, string counter) => root.TryGetProperty(key, out var nested) ? ReadCount(nested, counter) : 0;
    private static long Add(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
    private void Count(string key, long current)
    {
        var previous = _counters.GetValueOrDefault(key); _counters[key] = current;
        if (current > previous) _pending[key] = Add(_pending.GetValueOrDefault(key), current - previous);
    }
    private void FlushCounters()
    {
        if (_pending.Count == 0) return;
        DiagnosticLog.Write("机架", "rack-buffer-counters", DiagnosticLevel.Warning, "机架缓冲或信号异常计数增加", "合并统计的增量；超限不等于设备掉线，回调超期也不能单独证明故障原因。",
            [.. _pending.Select(p => (p.Key, (object?)p.Value)), ("api", _api), ("aggregationSeconds", 10)]);
        _pending.Clear();
    }
}
