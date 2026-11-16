using LightDeck.Core;

namespace LightDeck.App;

// Commands operate on a fresh, captured draft owned by MainRackHost. Validation
// precedes publication, so rejected copies never leave partial tracks or sends.
internal static class RackProjectEdits
{
    private static RackTrack Track(RackProject project, Guid id) => project.FindTrack(id) ?? throw new ArgumentException("轨道已不存在。");
    private static void Move<T>(List<T> list, T item, int index)
    { list.Remove(item); list.Insert(Math.Clamp(index, 0, list.Count), item); }
    public static void ReorderTrack(RackProject project, Guid id, int index) => Move(project.Tracks, Track(project, id), index);
    public static void CopyTrack(RackProject project, Guid id)
    {
        var original = Track(project, id); var copy = original.Copy(true); copy.Name = original.Name.Length > 197 ? original.Name[..197] + " 副本" : original.Name + " 副本";
        project.Tracks.Insert(project.Tracks.IndexOf(original) + 1, copy); project.SelectedTrackId = copy.Id;
    }
    public static void TransferInsert(RackProject project, Guid sourceId, Guid insertId, Guid destinationId, int index)
    {
        var source = Track(project, sourceId); var target = Track(project, destinationId);
        var insert = source.Inserts.FirstOrDefault(i => i.Id == insertId) ?? throw new ArgumentException("效果器已不存在。");
        if (source.Id == target.Id) Move(source.Inserts, insert, index);
        else target.Inserts.Insert(Math.Clamp(index, 0, target.Inserts.Count), insert.Copy(true));
    }
    public static void TransferSend(RackProject project, Guid sourceId, Guid sendId, Guid destinationId, int index)
    {
        var source = Track(project, sourceId); var target = Track(project, destinationId);
        var send = source.Sends.FirstOrDefault(i => i.Id == sendId) ?? throw new ArgumentException("发送已不存在。");
        if (source.Id == target.Id) Move(source.Sends, send, index);
        else target.Sends.Insert(Math.Clamp(index, 0, target.Sends.Count), send with { Id = Guid.NewGuid() });
    }
    public static void AddPlugin(RackProject project, Guid trackId, RackPluginIdentity plugin, int index)
    {
        RackValidation.ValidateIdentity(plugin); var target = Track(project, trackId);
        target.Inserts.Insert(Math.Clamp(index, 0, target.Inserts.Count), new() { Identity = plugin with { } });
    }
    public static void AddPluginSend(RackProject project, Guid sourceId, RackPluginIdentity plugin)
    {
        RackValidation.ValidateIdentity(plugin); var source = Track(project, sourceId);
        if (source.Kind == RackTrackKind.Output) throw new ArgumentException("输出轨不能添加发送。");
        var name = string.IsNullOrWhiteSpace(plugin.Name) ? "新效果" : plugin.Name;
        var fx = RackTrack.Create(RackTrackKind.FX, name.Length > 197 ? name[..197] + " FX" : name + " FX");
        fx.OutputTrackId = source.OutputTrackId ?? project.Tracks.FirstOrDefault(t => t.Kind == RackTrackKind.Output)?.Id;
        fx.Inserts.Add(new() { Identity = plugin with { } });
        project.Tracks.Insert(project.Tracks.IndexOf(source) + 1, fx); source.Sends.Add(new() { TargetTrackId = fx.Id }); project.SelectedTrackId = fx.Id;
    }
    public static void PreserveControls(RackProject target, RackProject current)
    {
        foreach (var track in target.Tracks)
        {
            if (current.FindTrack(track.Id) is not { } live) continue;
            track.GainDb = live.GainDb; track.Pan = live.Pan; track.Mute = live.Mute; track.Solo = live.Solo; track.SoloSafe = live.SoloSafe; track.Monitor = live.Monitor; track.MainOutputEnabled = live.MainOutputEnabled; track.MainOutputGainDb = live.MainOutputGainDb;
            track.Name = live.Name; track.Visible = live.Visible;
            foreach (var send in track.Sends)
                if (live.Sends.FirstOrDefault(s => s.Id == send.Id) is { } saved) { send.GainDb = saved.GainDb; send.Enabled = saved.Enabled; send.PreFader = saved.PreFader; }
            foreach (var insert in track.Inserts)
                if (live.Inserts.FirstOrDefault(i => i.Id == insert.Id && i.Identity.Key == insert.Identity.Key) is { } saved) { insert.Bypass = saved.Bypass; insert.Wet = saved.Wet; }
        }
    }
}
