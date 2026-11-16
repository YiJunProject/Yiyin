using System.IO;
using LightDeck.Core;

namespace LightDeck.App;

/// <summary>The same owned engine captures the old rack and installs the next one stopped.</summary>
public interface IRackSnapshotRuntime
{
    RackProject Project { get; }
    bool IsRunning { get; }
    Task SwitchStoppedAsync(RackProject next, Action<RackProject> capturedPrevious, CancellationToken cancellationToken = default);
    Task RestoreAsync(RackProject previous, bool resumePrevious, CancellationToken cancellationToken = default);
}

public sealed class AudioRackSnapshotRuntime(AudioRackService service) : IRackSnapshotRuntime
{
    public RackProject Project => service.Project;
    public bool IsRunning => service.Status.IsRunning;
    public Task SwitchStoppedAsync(RackProject next, Action<RackProject> capturedPrevious, CancellationToken cancellationToken = default)
        => service.SwitchSnapshotStoppedAsync(next, capturedPrevious, cancellationToken);
    public async Task RestoreAsync(RackProject previous, bool resumePrevious, CancellationToken cancellationToken = default)
    {
        try
        {
            await service.SwitchSnapshotStoppedAsync(previous, null, cancellationToken);
            if (resumePrevious) await service.StartAsync();
        }
        catch
        {
            try { await service.StopAsync(); } catch { }
            service.ReplaceProject(previous);
            throw;
        }
    }
}

/// <summary>Rack catalog transactions share the workspace's structural commit gate.
/// No local device settings or plugin-location maps are copied into portable snapshots.</summary>
public sealed class RackSnapshotService
{
    private readonly RackDocument _project;
    private readonly IRackSnapshotRuntime _runtime;
    private readonly SemaphoreSlim _commitGate;
    private readonly Func<Task> _persist;
    private readonly Action _requireCurrent;
    private readonly Func<Task>? _beforeSwitch;
    private readonly Action? _requireEditing;

    public RackSnapshotService(RackDocument project, IRackSnapshotRuntime runtime, SemaphoreSlim commitGate,
        Func<Task> persist, Action requireCurrent, Func<Task>? beforeSwitch = null, Action? requireEditing = null)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(runtime); ArgumentNullException.ThrowIfNull(commitGate);
        ArgumentNullException.ThrowIfNull(persist); ArgumentNullException.ThrowIfNull(requireCurrent);
        _project = project; _runtime = runtime; _commitGate = commitGate; _persist = persist; _requireCurrent = requireCurrent; _beforeSwitch = beforeSwitch; _requireEditing = requireEditing;
        Validate();
    }

    public IReadOnlyList<RackSnapshotInfo> GetRacks() { _requireCurrent(); return RackSnapshotCatalog.Describe(_project, _runtime.Project); }
    public RackProject Preview(Guid id)
    {
        _requireCurrent();
        return id == _project.RackId ? _runtime.Project.Copy() : Snapshot(id).Rack.Copy();
    }

    public async Task<Guid> AddImportedAsync(string name, RackProject rack, CancellationToken cancellationToken = default)
    {
        name = RackSnapshotCatalog.ValidateName(name); ArgumentNullException.ThrowIfNull(rack); rack.Validate();
        // Capture the import draft before waiting so the caller cannot alter accepted content.
        var imported = new RackSnapshot { Name = name, Rack = rack.Copy() };
        await _commitGate.WaitAsync(cancellationToken);
        try
        {
            _requireCurrent(); _requireEditing?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            var previous = _project.RackSnapshots;
            try
            {
                _project.RackSnapshots = [.. previous, imported]; Validate();
                await _persist(); _requireCurrent(); return imported.Id;
            }
            catch (Exception failure)
            {
                _project.RackSnapshots = previous;
                await PersistRollbackAsync(failure); throw;
            }
        }
        finally { _commitGate.Release(); }
    }

    public async Task SwitchAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _commitGate.WaitAsync(cancellationToken);
        try
        {
            _requireCurrent(); _requireEditing?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            if (id == _project.RackId) return;
            var selected = Snapshot(id); var next = selected.Rack.Copy(); next.Validate();
            var previousId = _project.RackId; var previousName = _project.RackName; var previousSnapshots = _project.RackSnapshots;
            var previousRack = _runtime.Project.Copy(); var wasRunning = _runtime.IsRunning; var captured = false;
            if (_beforeSwitch is not null) await _beforeSwitch();
            _requireCurrent(); _requireEditing?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _runtime.SwitchStoppedAsync(next, value =>
                {
                    _requireCurrent(); _requireEditing?.Invoke(); previousRack = value; previousRack.Validate(); captured = true;
                }, cancellationToken);
                _requireCurrent(); _requireEditing?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
                // Reuse the selected list position for the previous rack. Every inactive rack
                // stays directly accessible, without accumulating duplicate backup copies.
                _project.RackSnapshots = previousSnapshots.Select(snapshot => snapshot.Id == id
                    ? new RackSnapshot { Id = previousId, Name = previousName, Rack = previousRack }
                    : snapshot).ToList();
                _project.RackId = selected.Id; _project.RackName = selected.Name; _project.Rack = _runtime.Project;
                Validate(); await _persist(); _requireCurrent();
            }
            catch (Exception failure)
            {
                if (!captured) throw;
                _project.RackId = previousId; _project.RackName = previousName; _project.RackSnapshots = previousSnapshots;
                _project.Rack = previousRack;
                if (!IsCurrent()) throw;
                Exception? restoreFailure = null;
                if (captured)
                {
                    try { await _runtime.RestoreAsync(previousRack, wasRunning, CancellationToken.None); _project.Rack = _runtime.Project; }
                    catch (Exception error) { restoreFailure = error; }
                }
                await PersistRollbackAsync(failure, restoreFailure);
                throw;
            }
        }
        finally { _commitGate.Release(); }
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _commitGate.WaitAsync(cancellationToken);
        try
        {
            _requireCurrent(); _requireEditing?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            if (id == _project.RackId) throw new InvalidOperationException("当前正在使用的机架不能移除，请先切换到其他机架。");
            _ = Snapshot(id); var previous = _project.RackSnapshots;
            try { _project.RackSnapshots = previous.Where(snapshot => snapshot.Id != id).ToList(); Validate(); await _persist(); _requireCurrent(); }
            catch (Exception failure) { _project.RackSnapshots = previous; await PersistRollbackAsync(failure); throw; }
        }
        finally { _commitGate.Release(); }
    }

    public async Task RenameAsync(Guid id, string name, CancellationToken cancellationToken = default)
    {
        name = RackSnapshotCatalog.ValidateName(name); await _commitGate.WaitAsync(cancellationToken);
        try
        {
            _requireCurrent(); _requireEditing?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            var active = id == _project.RackId; var snapshot = active ? null : Snapshot(id);
            var previous = active ? _project.RackName : snapshot!.Name;
            if (name == previous) return;
            try
            {
                if (active) _project.RackName = name; else snapshot!.Name = name;
                Validate(); await _persist(); _requireCurrent();
            }
            catch (Exception failure)
            {
                if (active) _project.RackName = previous; else snapshot!.Name = previous;
                await PersistRollbackAsync(failure); throw;
            }
        }
        finally { _commitGate.Release(); }
    }

    private RackSnapshot Snapshot(Guid id) => _project.RackSnapshots.SingleOrDefault(snapshot => snapshot.Id == id)
        ?? throw new ArgumentException("已保存机架不存在或已移除。");
    private void Validate() => RackSnapshotCatalog.Validate(_project.RackId, _project.RackName, _project.Rack, _project.RackSnapshots);
    private bool IsCurrent() { try { _requireCurrent(); return true; } catch (OperationCanceledException) { return false; } }
    private async Task PersistRollbackAsync(Exception original, Exception? runtimeFailure = null)
    {
        if (!IsCurrent()) return;
        Exception? saveFailure = null;
        try { await _persist(); } catch (Exception error) { saveFailure = error; }
        if (runtimeFailure is not null || saveFailure is not null)
        {
            var failures = new List<Exception> { original }; if (runtimeFailure is not null) failures.Add(runtimeFailure); if (saveFailure is not null) failures.Add(saveFailure);
            throw new IOException(runtimeFailure is not null
                ? "机架切换未完成，原机架内容已保留，但音频运行恢复失败；请检查设备后手动启动。"
                : "机架修改未完成，内存中的原内容已恢复，但恢复保存失败；请检查磁盘后重新保存。", new AggregateException(failures));
        }
    }
}
