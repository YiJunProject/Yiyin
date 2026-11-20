using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using LightDeck.Core;

namespace LightDeck.App;

public sealed partial class AudioRackService
{
    internal static IReadOnlyList<RackUiEndpointStatus> ParseEndpoints(JsonElement status)
    {
        if (!status.TryGetProperty("endpoints", out var endpoints) || endpoints.ValueKind != JsonValueKind.Array) return [];
        return endpoints.EnumerateArray().Select(e => new RackUiEndpointStatus(Text(e, "deviceId"), Flag(e, "input"),
            Flag(e, "clock"), Flag(e, "muted"), (int)Number(e, "sampleRate"), Number(e, "driftPpm"), Text(e, "error"))).ToArray();
    }

    private void SelectMultiEndpoints(IReadOnlyList<RackUiDevice> devices,
        Dictionary<string, int> inputs, Dictionary<string, int> outputs)
    {
        var clock = devices.FirstOrDefault(d => d.Id == Local.OutputDeviceId && d.Available);
        if (clock is null || clock.OutputChannels == 0 || clock.ProbeRequired ||
            !Local.SelectedEndpointIds.Contains(clock.Id) || clock.Api != Local.DeviceApi)
            throw new IOException("请选择可用的主输出时钟，并将它加入当前设备。");
        if (Local.SampleRate is not (44100 or 48000) ||
            (clock.Api == "asio" ? clock.SampleRates?.Contains(Local.SampleRate) != true : clock.PreferredSampleRate != Local.SampleRate))
            throw new IOException("主时钟须支持所选的 44100 或 48000 Hz。");
        if (clock.Api == "asio") ValidateAsioBuffer(clock, Local.BufferSize);

        foreach (var id in Local.SelectedEndpointIds)
        {
            var device = devices.FirstOrDefault(d => d.Id == id);
            if (id != clock.Id && (device?.Api == "asio" || id.StartsWith("asio:", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("辅助设备须为 Windows 音频端点，一个机架仅使用一个 ASIO 驱动。");
            if (device is not { Available: true, ProbeRequired: false }) continue;
            if (device.InputChannels > 0) inputs.Add(id, device.InputChannels);
            if (device.OutputChannels > 0) outputs.Add(id, device.OutputChannels);
        }
        void IncludeMapping(RackHardwarePort map, int width, bool input, string name)
        {
            if (map.Channels.Length != width) throw new IOException($"“{name}”的物理通道数量不匹配。");
            if (!Local.SelectedEndpointIds.Contains(map.DeviceId)) return;
            var prepared = input ? inputs : outputs;
            int required = map.Channels.Max() + 1;
            if (devices.Any(d => d.Id == map.DeviceId && d.Available))
            {
                if (required > prepared.GetValueOrDefault(map.DeviceId))
                    throw new IOException($"“{name}”的通道超出所选声卡范围。");
            }
            else if (required > 0)
            {
                // An absent auxiliary still has an explicit saved direction and
                // width. Native keeps this stream silent until it is available.
                prepared[map.DeviceId] = Math.Max(required, prepared.GetValueOrDefault(map.DeviceId));
            }
        }
        foreach (var port in Project.InputPorts)
            if (Local.InputPorts.TryGetValue(port.Id, out var map)) IncludeMapping(map, port.Channels, true, port.Name);
        foreach (var port in Project.OutputPorts)
            foreach (var map in Local.OutputTargets(port.Id)) IncludeMapping(map, port.Channels, false, port.Name);
        foreach (var track in Project.Tracks.Where(t => t.InputPortId is null && t.HardwareOutputPortId is null))
        {
            var hardware = HardwareFor(track, Local);
            if (hardware.Channels is not null)
                IncludeMapping(new() { DeviceId = hardware.Endpoint, Channels = hardware.Channels }, track.Channels, track.Kind != RackTrackKind.Output, track.Name);
        }
    }

    private static JsonObject RoutingNode(RackProject project, RackTrack track, RackLocalSettings local)
    {
        string kind = track.Kind switch
        {
            RackTrackKind.Mono or RackTrackKind.Stereo => "input", RackTrackKind.Output => "output",
            RackTrackKind.Playback when project.PlaybackTrackId == track.Id => "media",
            RackTrackKind.FX => "fx", _ => "bus"
        };
        var node = new JsonObject { ["id"] = track.Id.ToString(), ["kind"] = kind, ["channels"] = track.Channels };
        var primary = HardwareFor(track, local);
        if (local.IoMode == "single" || kind is not ("input" or "output"))
        {
            node["endpointId"] = primary.Endpoint;
            node["deviceChannels"] = JsonSerializer.SerializeToNode(primary.Channels ?? Enumerable.Range(0, track.Channels).ToArray());
            return node;
        }
        var targets = new JsonArray();
        void Route(string deviceId, int[] channels)
        {
            if (local.SelectedEndpointIds.Contains(deviceId))
                targets.Add(new JsonObject { ["endpointId"] = deviceId, ["deviceChannels"] = JsonSerializer.SerializeToNode(channels) });
        }
        if (kind == "output" && track.HardwareOutputPortId is { } id)
        {
            foreach (var target in local.OutputTargets(id)) Route(target.DeviceId, target.Channels);
        }
        else if (primary.Channels is { } channels) Route(primary.Endpoint, channels);
        node["hardwareRoutes"] = targets;
        return node;
    }

    public bool CanPreviewHardwareRoutes(RackLocalSettings local) =>
        _graphPrepared && Status.IsRunning && local.IoMode != "single" && local.UsesSameStreams(Local);

    public bool CanApplyHardwareRoutes(RackProject project, RackLocalSettings local)
    {
        if (!CanPreviewHardwareRoutes(local) || local.RoutePlayer != Local.RoutePlayer ||
            project.PlaybackTrackId != Project.PlaybackTrackId || project.Tracks.Count != Project.Tracks.Count) return false;
        return project.Tracks.All(t => Project.FindTrack(t.Id) is { } old && old.Kind == t.Kind &&
            old.Channels == t.Channels && old.OutputTrackId == t.OutputTrackId &&
            old.Inserts.Select(p => (p.Id, p.Identity.Key)).SequenceEqual(t.Inserts.Select(p => (p.Id, p.Identity.Key))) &&
            old.Sends.Select(s => (s.Id, s.TargetTrackId)).SequenceEqual(t.Sends.Select(s => (s.Id, s.TargetTrackId))));
    }

    public async Task PreviewHardwareRoutesAsync(RackProject project, RackLocalSettings local)
    {
        await _graphGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (CanPreviewHardwareRoutes(local)) await SendHardwareRoutesAsync(project, local).ConfigureAwait(false);
        }
        finally { _graphGate.Release(); }
    }

    private Task<JsonElement> SendHardwareRoutesAsync(RackProject project, RackLocalSettings local)
    {
        var nodes = new JsonArray();
        foreach (var live in Project.Tracks.Where(t => t.Kind is RackTrackKind.Mono or RackTrackKind.Stereo or RackTrackKind.Output))
            nodes.Add(RoutingNode(project, project.FindTrack(live.Id) ?? live, local));
        return _engine!.RequestAsync("setHardwareRoutes", new() { ["nodes"] = nodes }, _lifetime.Token);
    }
}
