namespace LightDeck.Core;

public enum StudioOneImportSeverity { Information, Warning, Error }

public sealed record StudioOneImportIssue(StudioOneImportSeverity Severity, string Code, string Message,
    Guid? TrackId = null, Guid? PluginId = null);

// Indices are zero based. They are hints from the source computer, never an
// instruction to change the current device or automatically enable audio.
public sealed record StudioOneIoHint(Guid PortId, bool IsInput, string PortName, int Channels,
    string DeviceName, string DeviceType, IReadOnlyList<int> ChannelIndices,
    IReadOnlyList<string> ChannelNames);

public sealed record StudioOneTrackMapping(string SourceId, Guid TrackId, string SourceType, string Name);
public sealed record StudioOnePluginMapping(string SourceId, Guid TrackId, Guid PluginId, string PresetPath,
    string SourceFormat, bool StateImported);

public sealed class StudioOneImportResult
{
    public RackProject Project { get; init; } = new();
    public string Title { get; internal set; } = "";
    public string Generator { get; internal set; } = "";
    public string SourceSha256 { get; internal set; } = "";
    public int SourceSampleRate { get; internal set; }
    public List<StudioOneIoHint> IoHints { get; } = [];
    public List<StudioOneImportIssue> Issues { get; } = [];
    public List<StudioOneTrackMapping> TrackMappings { get; } = [];
    public List<StudioOnePluginMapping> PluginMappings { get; } = [];
    public bool CanImport => Project.Tracks.Count != 0 && !Issues.Any(i => i.Severity == StudioOneImportSeverity.Error);
}
