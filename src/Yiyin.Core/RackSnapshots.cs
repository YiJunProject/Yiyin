namespace LightDeck.Core;

/// <summary>An inactive rack stored in the show. Hardware maps remain in RackLocalSettings.</summary>
public sealed class RackSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "已保存机架";
    public RackProject Rack { get; set; } = new();
    public RackSnapshot Copy() => new() { Id = Id, Name = Name, Rack = Rack.Copy() };
}

public sealed record RackSnapshotInfo(Guid Id, string Name, bool IsActive, int TrackCount, int PluginCount, int InputPortCount, int OutputPortCount);

public static class RackSnapshotCatalog
{
    public static void Validate(Guid activeId, string activeName, RackProject active, IReadOnlyList<RackSnapshot> snapshots)
    {
        if (activeId == Guid.Empty) throw new InvalidDataException("当前机架标识无效。");
        ValidateName(activeName);
        if (active is null || snapshots is null) throw new InvalidDataException("工程机架或已保存机架列表缺失。");
        active.Validate();
        var ids = new HashSet<Guid> { activeId };
        foreach (var snapshot in snapshots)
        {
            if (snapshot is null || snapshot.Id == Guid.Empty || !ids.Add(snapshot.Id) || snapshot.Rack is null)
                throw new InvalidDataException("已保存机架标识重复或内容无效。");
            ValidateName(snapshot.Name); snapshot.Rack.Validate();
        }
    }

    public static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl)) throw new ArgumentException("机架名称需为 1–100 个可见字符。");
        return name.Trim();
    }

    public static List<RackSnapshot> Copy(IReadOnlyList<RackSnapshot> snapshots) => snapshots.Select(snapshot => snapshot.Copy()).ToList();

    public static IReadOnlyList<RackSnapshotInfo> Describe(RackDocument project, RackProject? active = null)
    {
        static RackSnapshotInfo Info(Guid id, string name, RackProject rack, bool current) => new(id, name, current,
            rack.Tracks.Count, rack.Tracks.Sum(track => track.Inserts.Count), rack.InputPorts.Count, rack.OutputPorts.Count);
        return new[] { Info(project.RackId, project.RackName, active ?? project.Rack, true) }
            .Concat(project.RackSnapshots.Select(snapshot => Info(snapshot.Id, snapshot.Name, snapshot.Rack, false))).ToArray();
    }

    public static HashSet<Guid> GetReferencedTrackIds(RackDocument project) => project.Rack.Tracks.Select(track => track.Id)
        .Concat(project.RackSnapshots.SelectMany(snapshot => snapshot.Rack.Tracks).Select(track => track.Id)).ToHashSet();

    public static bool IsPluginReferenced(RackDocument project, string key, RackProject? active = null) =>
        (active ?? project.Rack).Tracks.Concat(project.RackSnapshots.SelectMany(snapshot => snapshot.Rack.Tracks))
            .Any(track => track.Inserts.Any(plugin => plugin.Identity.Key == key));
}
