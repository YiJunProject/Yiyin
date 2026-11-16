namespace LightDeck.Core;

public sealed class RackSoloState
{
    private readonly HashSet<Guid> _muted;
    private RackSoloState(bool active, HashSet<Guid> muted) { IsActive = active; _muted = muted; }
    public bool IsActive { get; }
    public bool IsSoloMuted(Guid trackId) => _muted.Contains(trackId);
    public bool IsMuted(RackTrack track) => track.Mute || IsSoloMuted(track.Id);

    public static RackSoloState Evaluate(RackProject project)
    {
        var solos = project.Tracks.Where(t => t.Solo).Select(t => t.Id).ToHashSet();
        if (solos.Count == 0) return new(false, []);
        var tracks = project.Tracks.ToDictionary(t => t.Id);
        var downstream = tracks.Keys.ToDictionary(id => id, _ => new List<Guid>());
        var upstream = tracks.Keys.ToDictionary(id => id, _ => new List<Guid>());
        foreach (var track in project.Tracks)
        {
            void Connect(Guid target) { downstream[track.Id].Add(target); upstream[target].Add(track.Id); }
            if (track.MainOutputEnabled && track.MainOutputGainDb > -90 && track.OutputTrackId is { } output) Connect(output);
            foreach (var send in track.Sends.Where(s => s.Enabled && s.GainDb > -90)) Connect(send.TargetTrackId);
        }

        // Only explicit Solo opens contributors. A shared downstream bus or safe
        // effect must not reopen unrelated inputs feeding that same destination.
        var audible = new HashSet<Guid>(solos);
        var pending = new Queue<Guid>(solos);
        while (pending.TryDequeue(out var id))
            foreach (var source in upstream[id]) if (audible.Add(source)) pending.Enqueue(source);
        foreach (var track in project.Tracks.Where(t => t.SoloSafe)) audible.Add(track.Id);
        pending = new Queue<Guid>(audible);
        while (pending.TryDequeue(out var id))
            foreach (var target in downstream[id])
            {
                if (tracks[target].Kind == RackTrackKind.FX && !tracks[target].SoloSafe && !audible.Contains(target)) continue;
                if (audible.Add(target)) pending.Enqueue(target);
            }
        return new(true, tracks.Keys.Where(id => !audible.Contains(id)).ToHashSet());
    }
}
