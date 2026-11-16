using LightDeck.Core;

namespace LightDeck.App;

public sealed record RackUiMeter(double PeakLeft = 0, double PeakRight = 0, bool Clipped = false, double RmsLeft = 0, double RmsRight = 0);
public sealed record RackPluginScanProgress(bool IsRunning = false, bool IsIncremental = false, string Phase = "", int Completed = 0, int Total = 0, string CurrentFile = "", bool Deferred = false);
public sealed record RackUiOutputSignal(double LastBlockPeak, double PeakSinceReset, long OverRangeSamples, long NonFiniteSamples, long FramesObserved, long? IntegerOutputOverRangeSamples = null);
public sealed record RackUiStatus(bool IsRunning = false, bool IsBusy = false, string Message = "机架已停止", double CpuPercent = 0,
    double LatencyMilliseconds = 0, IReadOnlyDictionary<Guid, RackUiMeter>? Meters = null, int ActualSampleRate = 0, int ActualBufferFrames = 0,
    IReadOnlyDictionary<Guid, double>? PluginLatencyMilliseconds = null, string Api = "wasapi", bool DriverResetRequired = false,
    IReadOnlyDictionary<Guid, string>? PluginErrors = null, RackPluginScanProgress? ScanProgress = null, RackUiOutputSignal? OutputSignal = null);
public sealed record RackUiDevice(string Id, string Name, string Api, int InputChannels, int OutputChannels, IReadOnlyList<int>? SampleRates = null, int PreferredSampleRate = 0,
    string GroupId = "", string GroupName = "", string Manufacturer = "", bool Available = true, bool ProbeRequired = false,
    IReadOnlyList<string>? InputChannelNames = null, IReadOnlyList<string>? OutputChannelNames = null, int CurrentSampleRate = 0,
    int BufferMinFrames = -1, int BufferMaxFrames = -1, int BufferPreferredFrames = -1, int BufferGranularity = -1,
    int InputLatencyFrames = -1, int OutputLatencyFrames = -1)
{
    public override string ToString() => Name;
}

// The view owns only temporary edits. Project changes and native lifetime are
// committed by the host so rebuilding or detaching a view never stops audio.
public interface IRackUiHost
{
    RackProject Project { get; }
    Guid RackIdentity => Guid.Empty;
    string RackName => "当前机架";
    bool IsPluginReferenced(string key) => Project.Tracks.Any(t => t.Inserts.Any(p => p.Identity.Key == key));
    RackLocalSettings Local { get; }
    RackUiStatus Status { get; }
    bool IsEditing { get; }
    IReadOnlyList<RackPluginIdentity> Plugins { get; }
    RackSnapshotService? RackLibrary => null;
    event Action? Changed;
    Task ApplyProjectAsync(RackProject project);
    Task ApplyLocalAsync(RackLocalSettings local);
    Task ApplyPluginManagementAsync(RackLocalSettings local, IReadOnlyCollection<string> removedKeys)
    {
        var next = RackWorkspaceControl.CopyLocal(Local); next.ScanDirectories = [.. local.ScanDirectories];
        foreach (var key in removedKeys)
        {
            if (IsPluginReferenced(key)) throw new InvalidOperationException("当前或已保存机架仍在使用这个插件，请先移除对应效果器。");
            next.PluginCatalog.RemoveAll(p => p.Identity.Key == key); next.PluginLocations.Remove(key); next.FavoritePlugins.Remove(key); next.RecentPlugins.Remove(key);
        }
        return ApplyLocalAsync(next);
    }
    Task SetRunningAsync(bool running);
    Task UpdateTrackAsync(RackTrack track);
    Task RenameTrackAsync(Guid id, string name);
    Task ReorderTrackAsync(Guid trackId, int destinationIndex);
    Task CopyTrackAsync(Guid trackId);
    Task TransferInsertAsync(Guid sourceTrackId, Guid insertId, Guid destinationTrackId, int destinationIndex);
    Task TransferSendAsync(Guid sourceTrackId, Guid sendId, Guid destinationTrackId, int destinationIndex);
    Task AddPluginAsync(Guid targetTrackId, RackPluginIdentity plugin, int destinationIndex);
    Task AddPluginSendAsync(Guid sourceTrackId, RackPluginIdentity plugin);
    Task SetTrackVisibleAsync(Guid id, bool visible);
    Task ApplyDisplayViewsAsync(IReadOnlyList<RackDisplayView> views, Guid activeId)
    {
        var project = Project.Copy(); project.DisplayViews = views.Select(v => v.Copy()).ToList(); project.ApplyDisplayView(activeId); return ApplyProjectAsync(project);
    }
    Task ApplyIoAsync(RackProject project, RackLocalSettings local);
    Task SelectTrackAsync(Guid? id);
    Task ScanPluginsAsync(CancellationToken token);
    Task CheckPluginChangesAsync(CancellationToken token);
    Task ShowPluginEditorAsync(Guid trackId, Guid insertId, nint owner);
    Task<IReadOnlyList<RackUiDevice>> EnumerateDevicesAsync(string? api = null);
    Task<RackUiDevice> ProbeDeviceAsync(string api, string deviceId);
    Task ShowDriverControlPanelAsync(string deviceId);
}

internal sealed class RackDraftGuard(IRackUiHost host)
{
    private readonly Guid _identity = host.RackIdentity;
    public bool IsCurrent => host.RackIdentity == _identity;
    public void RequireCurrent()
    {
        if (!IsCurrent) throw new InvalidOperationException("当前机架已切换；此窗口仍是原机架的草稿，请关闭后重新打开，未应用修改不会写入新机架。");
    }
}
