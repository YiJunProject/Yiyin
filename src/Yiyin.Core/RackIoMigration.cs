namespace LightDeck.Core;

public sealed record RackHardwarePort
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public int[] Channels { get; set; } = [];
    public RackHardwarePort Copy() => this with { Channels = [.. Channels] };
}

public static class RackIoMigration
{
    // Called on editor drafts. Opening or cancelling the editor never alters the live graph.
    public static void Upgrade(RackProject project, RackLocalSettings local)
    {
        foreach (var track in project.Tracks)
        {
            bool input = track.Kind is RackTrackKind.Mono or RackTrackKind.Stereo;
            if (!input && track.Kind != RackTrackKind.Output) continue;
            if (input ? track.InputPortId is not null : track.HardwareOutputPortId is not null) continue;
            var old = input ? local.InputChannels : local.OutputChannels;
            if (!old.TryGetValue(track.Id, out var channels)) continue;
            var deviceId = input ? local.InputDeviceId : local.OutputDeviceId;
            if (string.IsNullOrWhiteSpace(deviceId)) continue;
            var ports = input ? project.InputPorts : project.OutputPorts;
            var maps = input ? local.InputPorts : local.OutputPorts;
            var same = ports.FirstOrDefault(p => p.Channels == track.Channels && maps.TryGetValue(p.Id, out var hardware) && hardware.DeviceId == deviceId && hardware.Channels.SequenceEqual(channels));
            if (same is null)
            {
                same = new() { Name = track.Name, Channels = track.Channels }; ports.Add(same);
                maps[same.Id] = new() { DeviceId = deviceId, DeviceName = input ? local.InputDeviceName : local.OutputDeviceName, Channels = [.. channels] };
            }
            if (input) track.InputPortId = same.Id; else track.HardwareOutputPortId = same.Id;
            old.Remove(track.Id);
        }
        project.Validate(); local.Validate();
    }
}
