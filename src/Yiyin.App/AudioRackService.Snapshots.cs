using LightDeck.Core;

namespace LightDeck.App;

public sealed partial class AudioRackService
{
    // A rack identity change cannot reuse the old hardware graph or merge its controls by
    // track ID. Capture the outgoing rack and replace the configuration under one graph gate.
    internal async Task SwitchSnapshotStoppedAsync(RackProject next, Action<RackProject>? capturedPrevious, CancellationToken cancellationToken)
    {
        next.Validate();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _graphGate.WaitAsync(request.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SetBusy("正在切换机架…");
            if (_graphPrepared && _engine is { IsAlive: true } editorEngine)
                foreach (var plugin in Project.Tracks.SelectMany(track => track.Inserts))
                    await editorEngine.RequestAsync("editor", new() { ["instanceId"] = plugin.Id.ToString(), ["show"] = false }, request.Token).ConfigureAwait(false);
            RackProject? captured = null;
            if (capturedPrevious is not null)
            {
                captured = await CaptureStatesUnderGateAsync().ConfigureAwait(false);
                request.Token.ThrowIfCancellationRequested(); capturedPrevious(captured);
            }
            _diagnostics.StopRequested("rack-switch");
            if (_engine is { IsAlive: true } engine)
            {
                await engine.RequestAsync("stop", cancellation: request.Token).ConfigureAwait(false);
                if (_graphPrepared) await engine.RequestAsync("clearGraph", cancellation: request.Token).ConfigureAwait(false);
            }
            request.Token.ThrowIfCancellationRequested();
            lock (_controlGate)
            {
                if (captured is not null)
                {
                    RackProjectEdits.PreserveControls(captured, Project);
                    foreach (var track in captured.Tracks)
                        if (Project.FindTrack(track.Id) is { } current) { track.Name = current.Name; track.Visible = current.Visible; }
                    captured.SelectedTrackId = Project.SelectedTrackId;
                }
                Project = next; _pendingControls = null; _controlVersion++;
            }
            _graphPrepared = false; Local.WasRunning = false; _pluginLoadErrors.Clear(); RefreshPluginErrors();
            _diagnostics.Stopped("rack-switch");
            Status = Status with { IsRunning = false, IsBusy = false, Message = "机架已切换并停止；请确认音频 I/O 后手动启动。" }; Notify();
        }
        catch
        {
            Status = Status with { IsBusy = false }; Notify(); throw;
        }
        finally { _graphGate.Release(); }
    }
}
