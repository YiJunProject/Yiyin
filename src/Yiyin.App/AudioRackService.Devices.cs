using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using LightDeck.Core;

namespace LightDeck.App;

public sealed partial class AudioRackService
{
    private readonly ConcurrentDictionary<string, RackUiDevice> _deviceProbes = new(StringComparer.OrdinalIgnoreCase);
    private static string ProbeKey(string api, string id) => api + ":" + id;

    public async Task<IReadOnlyList<RackUiDevice>> EnumerateDevicesAsync(string? api = null)
    {
        api = RackLocalSettings.NormalizeDeviceApi(api ?? Local.DeviceApi);
        var result = await CommandAsync("devices", new() { ["api"] = api }).ConfigureAwait(false);
        // ASIO enumeration is registry-only. Merely opening this list never loads
        // any driver; cached metadata comes only from a selected-driver query.
        return result.GetProperty("devices").EnumerateArray().Select(ReadDevice).Where(d => !string.IsNullOrEmpty(d.Id) && d.Api == api)
            .Select(d => d.ProbeRequired && _deviceProbes.TryGetValue(ProbeKey(api, d.Id), out var probed) ? probed with { Available = d.Available } : d).ToArray();
    }

    public async Task<RackUiDevice> ProbeDeviceAsync(string api, string deviceId)
    {
        api = RackLocalSettings.NormalizeDeviceApi(api);
        await _graphGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (Status.IsRunning)
            {
                if (api == Local.DeviceApi && deviceId == Local.OutputDeviceId && _deviceProbes.TryGetValue(ProbeKey(api, deviceId), out var cached)) return cached;
                throw new InvalidOperationException("请先停止机架，再查询其他 ASIO 驱动；运行中的驱动只显示已确认参数。");
            }
            return await ProbeDeviceUnderGateAsync(api, deviceId).ConfigureAwait(false);
        }
        finally { _graphGate.Release(); }
    }

    private async Task<RackUiDevice> ProbeDeviceUnderGateAsync(string api, string deviceId)
    {
        if (api != "asio" || string.IsNullOrWhiteSpace(deviceId) || !deviceId.StartsWith("asio:", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择已注册的 ASIO 驱动。");
        ObjectDisposedException.ThrowIf(_disposed, this);
        _deviceProbes.TryRemove(ProbeKey(api, deviceId), out _);
        // Each selected driver runs in its own short-lived process. A faulty driver
        // cannot replace or initialize the rack's currently prepared native graph.
        await using var probe = new RackEngineProcess(NativePath("Yideck.Audio.Engine.exe"), "probe");
        var result = await probe.RequestAsync("probeDevice", new() { ["api"] = api, ["deviceId"] = deviceId }, _lifetime.Token).ConfigureAwait(false);
        var device = ReadDevice(result.GetProperty("device"));
        if (device.Id != deviceId || device.Api != api || !device.Available || device.ProbeRequired || device.OutputChannels <= 0) throw new IOException("驱动查询未返回所选 ASIO 驱动的有效通道信息。");
        _deviceProbes[ProbeKey(api, deviceId)] = device;
        return device;
    }

    public async Task ShowDriverControlPanelAsync(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || !deviceId.StartsWith("asio:", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择 ASIO 驱动后打开控制面板。");
        await _graphGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (Status.IsRunning) throw new InvalidOperationException("请先停止机架，再打开 ASIO 驱动控制面板。");
            _deviceProbes.TryRemove(ProbeKey("asio", deviceId), out _);
            await using var panel = new RackEngineProcess(NativePath("Yideck.Audio.Engine.exe"), "driver-panel");
            await panel.RequestAsync("driverControlPanel", new() { ["api"] = "asio", ["deviceId"] = deviceId }, _lifetime.Token).ConfigureAwait(false);
        }
        finally { _graphGate.Release(); }
    }

    internal static RackUiDevice ReadDevice(JsonElement d)
    {
        int[] Ints(string name) => d.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Select(v => v.GetInt32()).ToArray() : [];
        string[] Names(string name) => d.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Select(v => v.GetString() ?? "").ToArray() : [];
        return new(Text(d, "stableId", Text(d, "id")), Text(d, "name"), RackLocalSettings.NormalizeDeviceApi(Text(d, "api", "wasapi")), (int)Number(d, "inputChannels"), (int)Number(d, "outputChannels"),
            Ints("sampleRates"), (int)Number(d, "preferredSampleRate"), Text(d, "groupId"), Text(d, "groupName"), Text(d, "manufacturer"), !d.TryGetProperty("available", out var available) || available.ValueKind != JsonValueKind.False,
            Flag(d, "probeRequired"), Names("inputChannelNames"), Names("outputChannelNames"), (int)Number(d, "currentSampleRate"),
            (int)Number(d, "bufferMinFrames", -1), (int)Number(d, "bufferMaxFrames", -1), (int)Number(d, "bufferPreferredFrames", -1), (int)Number(d, "bufferGranularity", -1),
            (int)Number(d, "inputLatencyFrames", -1), (int)Number(d, "outputLatencyFrames", -1));
    }

    internal static void ValidateAsioBuffer(RackUiDevice driver, int frames)
    {
        var min = driver.BufferMinFrames; var max = driver.BufferMaxFrames; var step = driver.BufferGranularity;
        if (min <= 0 || max < min || frames < min || frames > max || step < -1 ||
            step == -1 && (frames & (frames - 1)) != 0 || step == 0 && (min != max || frames != min) || step > 0 && (frames - min) % step != 0)
            throw new IOException("缓冲帧数不符合所选 ASIO 驱动报告的范围或步进，请重新查询驱动并选择。");
    }
}
