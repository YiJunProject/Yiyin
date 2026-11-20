namespace LightDeck.Core;

public sealed partial class RackLocalSettings
{
    public string IoMode { get; set; } = "single";
    public List<string> SelectedEndpointIds { get; set; } = [];
    public Dictionary<Guid, List<RackHardwarePort>> OutputMirrors { get; set; } = [];

    public IEnumerable<RackHardwarePort> OutputTargets(Guid portId)
    {
        if (OutputPorts.TryGetValue(portId, out var first)) yield return first;
        if (IoMode != "single" && OutputMirrors.TryGetValue(portId, out var mirrors))
            foreach (var mirror in mirrors) yield return mirror;
    }

    public bool UsesSameStreams(RackLocalSettings other) =>
        IoMode == other.IoMode && DeviceApi == other.DeviceApi &&
        OutputDeviceId == other.OutputDeviceId && SampleRate == other.SampleRate &&
        BufferSize == other.BufferSize &&
        SelectedEndpointIds.Count == other.SelectedEndpointIds.Count &&
        SelectedEndpointIds.ToHashSet(StringComparer.Ordinal).SetEquals(other.SelectedEndpointIds);

    private void ValidateHardwareRoutes()
    {
        if (IoMode is not ("single" or "hybrid" or "multi") ||
            IoMode == "hybrid" && DeviceApi != "asio" || IoMode == "multi" && DeviceApi != "wasapi")
            throw new ArgumentException("机架模式与主驱动类型不匹配。");
        if (SelectedEndpointIds is null || SelectedEndpointIds.Any(string.IsNullOrWhiteSpace) ||
            SelectedEndpointIds.Distinct(StringComparer.Ordinal).Count() != SelectedEndpointIds.Count ||
            OutputMirrors is null)
            throw new ArgumentException("机架设备选择无效。");

        void CheckPort(Guid port, RackHardwarePort map)
        {
            if (port == Guid.Empty || map is null || string.IsNullOrWhiteSpace(map.DeviceId) ||
                map.Channels is not { Length: >= 1 and <= 2 } channels ||
                channels.Any(c => c < (IoMode == "single" ? 0 : -1) || c > 255) ||
                channels.All(c => c == -1) || channels.Distinct().Count() != channels.Length)
                throw new ArgumentException("音频 I/O 硬件映射无效。");
        }
        foreach (var (port, map) in InputPorts.Concat(OutputPorts)) CheckPort(port, map);
        foreach (var (port, mirrors) in OutputMirrors)
        {
            if (mirrors is null || !OutputPorts.ContainsKey(port))
                throw new ArgumentException("输出的附加映射缺少主映射。");
            foreach (var map in mirrors) CheckPort(port, map);
        }
    }
}
