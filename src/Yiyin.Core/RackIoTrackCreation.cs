namespace LightDeck.Core;

public static class RackIoTrackCreation
{
    public static RackProject Prepare(RackProject current, RackProject ioDraft)
    {
        // I/O owns port bindings; the latest tracks, routing and plugin state win.
        var result = current.Copy();
        result.InputPorts = ioDraft.InputPorts.Select(p => p with { }).ToList();
        result.OutputPorts = ioDraft.OutputPorts.Select(p => p with { }).ToList();
        result.PlaybackTrackId = ioDraft.PlaybackTrackId;
        foreach (var track in result.Tracks)
            if (ioDraft.FindTrack(track.Id) is { } saved)
            {
                track.InputPortId = saved.InputPortId;
                track.HardwareOutputPortId = saved.HardwareOutputPortId;
            }

        var added = new List<RackTrack>();
        foreach (var port in result.OutputPorts.Where(p => current.OutputPorts.All(old => old.Id != p.Id)))
        {
            if (result.Tracks.Any(t => t.HardwareOutputPortId == port.Id)) continue;
            var track = RackTrack.Create(RackTrackKind.Output, port.Name);
            track.Channels = port.Channels;
            track.HardwareOutputPortId = port.Id;
            result.Tracks.Add(track); added.Add(track);
        }
        var firstOutput = result.Tracks.FirstOrDefault(t => t.Kind == RackTrackKind.Output)?.Id;
        foreach (var port in result.InputPorts.Where(p => current.InputPorts.All(old => old.Id != p.Id)))
        {
            if (result.Tracks.Any(t => t.InputPortId == port.Id)) continue;
            var track = RackTrack.Create(port.Channels == 1 ? RackTrackKind.Mono : RackTrackKind.Stereo, port.Name);
            track.InputPortId = port.Id;
            track.OutputTrackId = firstOutput;
            result.Tracks.Add(track); added.Add(track);
        }
        result.EnsureDisplayViews();
        if (result.DisplayViews.FirstOrDefault(v => v.Id == result.ActiveDisplayViewId) is { } view)
            foreach (var track in added) if (!view.TrackIds.Contains(track.Id)) view.TrackIds.Add(track.Id);
        result.SelectedTrackId ??= added.FirstOrDefault()?.Id;
        result.Validate();
        return result;
    }
}
