using System.IO;
using System.Text.Json.Nodes;
using LightDeck.Core;

namespace LightDeck.App;

public sealed partial class AudioRackService
{
    private readonly Dictionary<Guid, (string Key, string Path)> _loadedPluginLocations = [];

    // Controls never need binary paths or serialized plugin state. This is also
    // used to carry edits made in another view while a graph was preparing.
    private static JsonObject BuildControls(RackProject project)
    {
        var solo = RackSoloState.Evaluate(project);
        var nodes = new JsonArray(); var edges = new JsonArray();
        foreach (var track in project.Tracks)
        {
            var plugins = new JsonArray();
            foreach (var insert in track.Inserts) plugins.Add(new JsonObject { ["instanceId"] = insert.Id.ToString(), ["wet"] = insert.Wet, ["bypass"] = insert.Bypass });
            nodes.Add(new JsonObject { ["id"] = track.Id.ToString(), ["gain"] = Gain(track.GainDb), ["pan"] = track.Pan, ["mute"] = solo.IsMuted(track), ["monitor"] = track.Monitor, ["plugins"] = plugins });
            if (track.OutputTrackId is not null) edges.Add(new JsonObject { ["id"] = track.Id + ":main", ["source"] = "post", ["gain"] = track.MainOutputEnabled ? Gain(track.MainOutputGainDb) : 0 });
            foreach (var send in track.Sends) edges.Add(new JsonObject { ["id"] = send.Id.ToString(), ["source"] = send.PreFader ? "pre" : "post", ["gain"] = send.Enabled ? Gain(send.GainDb) : 0 });
        }
        return new() { ["nodes"] = nodes, ["edges"] = edges };
    }

    private void RememberLoadedPlugins(RackProject project)
    {
        if (!_graphPrepared) _loadedPluginLocations.Clear();
        var ids = project.Tracks.SelectMany(t => t.Inserts).Select(p => p.Id).ToHashSet();
        foreach (var id in _loadedPluginLocations.Keys.Where(id => !ids.Contains(id)).ToArray()) _loadedPluginLocations.Remove(id);
        foreach (var plugin in project.Tracks.SelectMany(t => t.Inserts))
            if ((!_loadedPluginLocations.TryGetValue(plugin.Id, out var old) || old.Key != plugin.Identity.Key) && Local.PluginLocations.TryGetValue(plugin.Identity.Key, out var path))
                _loadedPluginLocations[plugin.Id] = (plugin.Identity.Key, path);
    }

    // Holds the structural gate until BOTH project and local persistence have
    // succeeded. Audio callbacks keep running; live controls queue independently.
    internal async Task<GraphEdit> BeginGraphEditAsync(RackProject project)
    {
        project.Validate(); await _graphGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        var previous = Project; var oldLocations = new Dictionary<Guid, (string Key, string Path)>(_loadedPluginLocations);
        long? token = null;
        try
        {
            long version; lock (_controlGate) version = _controlVersion;
            if (_engine is { IsAlive: true } && (_graphPrepared || Status.IsRunning))
            {
                var result = await CommandAsync("beginGraphEdit", new() { ["graph"] = BuildGraph(project) }).ConfigureAwait(false);
                token = result.GetProperty("transaction").GetInt64();
                RememberLoadedPlugins(project);
            }
            lock (_controlGate)
            {
                if (_pendingControls is not null || version != _controlVersion)
                {
                    foreach (var target in project.Tracks)
                        if (Project.FindTrack(target.Id) is { } current) MergeControls(target, current);
                    _pendingControls = project;
                }
                Project = project;
            }
            RefreshPluginErrors(); Notify();
            return new GraphEdit(this, previous, oldLocations, token);
        }
        catch
        {
            // Build failure is atomic in native: the old graph was never retired.
            if (token is { } value)
                try { await CommandAsync("rollbackGraphEdit", new() { ["transaction"] = value }).ConfigureAwait(false); } catch { }
            _graphGate.Release(); throw;
        }
    }

    internal sealed class GraphEdit(AudioRackService owner, RackProject previous,
        Dictionary<Guid, (string Key, string Path)> locations, long? token) : IAsyncDisposable
    {
        private bool _finished;
        public async Task CommitAsync()
        {
            if (_finished) throw new InvalidOperationException("机架编辑事务已结束。");
            if (token is { } value)
            {
                JsonObject controls; lock (owner._controlGate) controls = BuildControls(owner.Project);
                controls["transaction"] = value;
                var result = await owner.CommandAsync("commitGraphEdit", controls).ConfigureAwait(false);
                owner.ObserveHardwareReadiness(result);
            }
            _finished = true; owner._graphGate.Release();
        }
        public async ValueTask DisposeAsync()
        {
            if (_finished) return;
            try
            {
                JsonObject controls;
                lock (owner._controlGate)
                {
                    RackProjectEdits.PreserveControls(previous, owner.Project);
                    controls = BuildControls(previous);
                    owner.Project = previous;
                    owner._pendingControls = previous;
                    owner._controlVersion++;
                }
                if (token is { } value)
                {
                    controls["transaction"] = value;
                    await owner.CommandAsync("rollbackGraphEdit", controls).ConfigureAwait(false);
                }
                owner._loadedPluginLocations.Clear();
                foreach (var item in locations) owner._loadedPluginLocations[item.Key] = item.Value;
                owner.RefreshPluginErrors(); owner.Notify();
            }
            finally { _finished = true; owner._graphGate.Release(); }
        }
    }
}
