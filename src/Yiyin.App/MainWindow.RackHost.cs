using System.IO;
using System.Text.Json;
using LightDeck.Core;
namespace LightDeck.App;
public sealed partial class MainWindow {
    private sealed class MainRackHost(MainWindow window) : IRackUiHost
    {
        private readonly RackDocument _ownerProject = window.Project;
        private readonly long _generation = window._rackGeneration;
        private bool IsCurrent(AudioRackService? service = null) => ReferenceEquals(window.Project, _ownerProject) && _generation == window._rackGeneration && !window._closing && !window._projectTransition && (service is null || ReferenceEquals(window._rackService, service));
        private void RequireCurrent(AudioRackService? service = null) { if (!IsCurrent(service)) throw new OperationCanceledException("机架所属工程已切换。"); }
        private void RequireRack(Guid identity) { RequireCurrent(); if (_ownerProject.RackId != identity) throw new InvalidOperationException("当前机架已切换，请重新打开原机架的编辑窗口。"); }
        private void RequireEditing() { RequireCurrent(); if (!IsEditing) throw new InvalidOperationException("演播模式不能修改结构、名称或设备；请先进入编辑模式。"); }
        public RackProject Project => IsCurrent(window._rackService) && window._rackService is { } service ? service.Project : _ownerProject.Rack;
        public Guid RackIdentity => _ownerProject.RackId;
        public string RackName => _ownerProject.RackName;
        public bool IsPluginReferenced(string key) => RackSnapshotCatalog.IsPluginReferenced(_ownerProject, key, Project);
        public RackLocalSettings Local => window.RackAppSettings.RackLocal;
        public RackUiStatus Status => window._rackService?.Status ?? new();
        public bool IsEditing => window._rackEditing && !window._closing && !window._projectTransition;
        public IReadOnlyList<RackPluginIdentity> Plugins => window.RackService.Plugins;
        public RackSnapshotService RackLibrary
        {
            get
            {
                RequireCurrent(); var service = window.RackService;
                return new(_ownerProject, new AudioRackSnapshotRuntime(service), window._rackCommit,
                    () =>
                    {
                        RequireCurrent(service);
                        if (!window.TrySaveProject(false)) throw new IOException("机架列表未保存，请检查磁盘或文件权限。");
                        window.RackSettingsStore.Save(window.RackAppSettings);
                        window.RackUiChanged?.Invoke(); window.UpdateProjectTitle();
                        return Task.CompletedTask;
                    }, () => RequireCurrent(service),
                    null, RequireEditing);
            }
        }
        public event Action? Changed { add => window.RackUiChanged += value; remove => window.RackUiChanged -= value; }
        public async Task ApplyProjectAsync(RackProject project)
        {
            RequireEditing(); var service = window.RackService; var identity = RackIdentity;
            project.Validate(); await window._rackCommit.WaitAsync();
            try
            {
                RequireCurrent(service); RequireRack(identity);
                var previous = service.Project.Copy();
                var recreate = project.Tracks.Where(t => previous.FindTrack(t.Id) is { } old && old.Channels != t.Channels)
                    .SelectMany(t => t.Inserts.Where(p => previous.FindTrack(t.Id)!.Inserts.Any(old => old.Id == p.Id && old.Identity.Key == p.Identity.Key))).Select(p => p.Id).ToArray();
                if (recreate.Length > 0)
                {
                    previous = await service.CapturePluginStatesAsync(recreate); RequireCurrent(service);
                    MergePluginStates(project, previous);
                }
                // Structural drafts do not own live controls; another window may
                // have moved a fader while state capture was in flight.
                RackProjectEdits.PreserveControls(project, service.Project);
                await CommitProjectAsync(service, project, previous);
            }
            finally { window._rackCommit.Release(); }
        }
        private async Task CommitProjectAsync(AudioRackService service, RackProject project, RackProject previous, RackLocalSettings? previousLocal = null)
        {
            RequireEditing();
            project.CaptureActiveDisplayView();
            project.Validate(); var local = previousLocal ?? RackWorkspaceControl.CopyLocal(Local);
            AudioRackService.GraphEdit? edit = null;
            try
            {
                
                edit = await service.BeginGraphEditAsync(project); RequireCurrent(service); _ownerProject.Rack = service.Project;
                var ids = RackSnapshotCatalog.GetReferencedTrackIds(_ownerProject);
                foreach (var id in Local.InputChannels.Keys.Where(id => !ids.Contains(id)).ToArray()) Local.InputChannels.Remove(id);
                foreach (var id in Local.OutputChannels.Keys.Where(id => !ids.Contains(id)).ToArray()) Local.OutputChannels.Remove(id);
                if (!window.TrySaveProject(false)) throw new IOException("工程未保存，请检查磁盘或文件权限。");
                RequireCurrent(service); window.RackSettingsStore.Save(window.RackAppSettings);
                await edit.CommitAsync(); edit = null; window.RackUiChanged?.Invoke();
            }
            catch
            {
                if (edit is not null)
                {
                    // Native retains the exact prior instances until persistence
                    // commits; rollback never reconstructs deleted plugins from
                    // stale project chunks.
                    try { await edit.DisposeAsync(); }
                    catch { if (IsCurrent(service)) await service.StopAsync(); }
                }
                if (IsCurrent(service))
                {
                    // Keep controls changed while native was loading, even if the
                    // structural change or either persistence write is rejected.
                    RackProjectEdits.PreserveControls(previous, service.Project);
                    service.ReplaceProject(previous);
                    _ownerProject.Rack = previous; Local.InputChannels = local.InputChannels; Local.OutputChannels = local.OutputChannels;
                    if (IsCurrent(service)) { window.TrySaveProject(false); try { window.RackSettingsStore.Save(window.RackAppSettings); } catch { } window.RackUiChanged?.Invoke(); }
                }
                throw;
            }
        }
        private async Task EditFreshProjectAsync(Action<RackProject> edit, IReadOnlyCollection<Guid>? sources = null)
        {
            RequireEditing(); var service = window.RackService; var identity = RackIdentity; await window._rackCommit.WaitAsync();
            try
            {
                RequireCurrent(service); RequireRack(identity); RequireEditing();
                var previous = sources is { Count: > 0 } ? await service.CapturePluginStatesAsync(sources) : service.Project.Copy();
                RequireCurrent(service); RequireEditing();
                var draft = previous.Copy(); edit(draft); draft.Validate();
                await CommitProjectAsync(service, draft, previous);
            }
            finally { window._rackCommit.Release(); }
        }
        public Task ReorderTrackAsync(Guid trackId, int destinationIndex) => EditFreshProjectAsync(p => RackProjectEdits.ReorderTrack(p, trackId, destinationIndex));
        public async Task CopyTrackAsync(Guid trackId)
        {
            RequireEditing(); var service = window.RackService; var identity = RackIdentity; await window._rackCommit.WaitAsync();
            try
            {
                RequireCurrent(service); RequireRack(identity); RequireEditing();
                var sources = service.Project.FindTrack(trackId)?.Inserts.Select(p => p.Id).ToArray() ?? throw new ArgumentException("轨道已不存在。");
                var previous = await service.CapturePluginStatesAsync(sources); RequireCurrent(service); RequireEditing();
                var draft = previous.Copy(); RackProjectEdits.CopyTrack(draft, trackId); draft.Validate();
                var oldLocal = RackWorkspaceControl.CopyLocal(Local); var copyId = draft.SelectedTrackId!.Value;
                // Legacy projects identify hardware maps by track ID. A copied
                // named-port reference already shares the intended logical port.
                if (Local.InputChannels.TryGetValue(trackId, out var input)) Local.InputChannels[copyId] = [.. input];
                if (Local.OutputChannels.TryGetValue(trackId, out var output)) Local.OutputChannels[copyId] = [.. output];
                await CommitProjectAsync(service, draft, previous, oldLocal);
            }
            finally { window._rackCommit.Release(); }
        }
        public Task TransferInsertAsync(Guid sourceTrackId, Guid insertId, Guid destinationTrackId, int destinationIndex) => EditFreshProjectAsync(p => RackProjectEdits.TransferInsert(p, sourceTrackId, insertId, destinationTrackId, destinationIndex), sourceTrackId == destinationTrackId ? null : new[] { insertId });
        public Task TransferSendAsync(Guid sourceTrackId, Guid sendId, Guid destinationTrackId, int destinationIndex) => EditFreshProjectAsync(p => RackProjectEdits.TransferSend(p, sourceTrackId, sendId, destinationTrackId, destinationIndex));
        public async Task AddPluginAsync(Guid targetTrackId, RackPluginIdentity plugin, int destinationIndex) { await EditFreshProjectAsync(p => RackProjectEdits.AddPlugin(p, targetTrackId, plugin, destinationIndex)); await RememberPluginAsync(plugin.Key); }
        public async Task AddPluginSendAsync(Guid sourceTrackId, RackPluginIdentity plugin) { await EditFreshProjectAsync(p => RackProjectEdits.AddPluginSend(p, sourceTrackId, plugin)); await RememberPluginAsync(plugin.Key); }
        private async Task RememberPluginAsync(string key)
        {
            try { var local = RackWorkspaceControl.CopyLocal(Local); local.RecentPlugins.Remove(key); local.RecentPlugins.Insert(0, key); local.RecentPlugins = local.RecentPlugins.Take(20).ToList(); await ApplyLocalAsync(local); }
            catch (Exception ex) { window.Notice("插件操作已完成；最近使用记录未保存：" + ex.Message); }
        }
        public async Task SetTrackVisibleAsync(Guid id, bool visible)
        {
            // Visibility changes the view only. The width-limit prompt also offers
            // this operation in performance mode without touching routing or audio.
            RequireCurrent(); var service = window.RackService; var identity = RackIdentity; await window._rackCommit.WaitAsync();
            try
            {
                RequireCurrent(service); RequireRack(identity); var previous = service.Project.FindTrack(id)?.Visible ?? throw new ArgumentException("轨道已不存在。");
                service.SetTrackVisible(id, visible); service.Project.CaptureActiveDisplayView(); _ownerProject.Rack = service.Project;
                if (!window.TrySaveProject(false)) { service.SetTrackVisible(id, previous); service.Project.CaptureActiveDisplayView(); _ownerProject.Rack = service.Project; window.TrySaveProject(false); window.UpdateProjectTitle(); window.RackUiChanged?.Invoke(); throw new IOException("轨道显示设置未保存。"); }
                window.RackUiChanged?.Invoke();
            }
            finally { window._rackCommit.Release(); }
        }
        public async Task ApplyDisplayViewsAsync(IReadOnlyList<RackDisplayView> views, Guid activeId)
        {
            RequireCurrent(); var service = window.RackService; var identity = RackIdentity; await window._rackCommit.WaitAsync();
            try
            {
                RequireCurrent(service); RequireRack(identity); var current = service.Project; current.EnsureDisplayViews();
                var previousViews = current.DisplayViews.Select(v => v.Copy()).ToList(); var previousId = current.ActiveDisplayViewId;
                var previousSelected = current.SelectedTrackId;
                var previousVisible = current.Tracks.ToDictionary(t => t.Id, t => t.Visible);
                var draft = current.Copy(); draft.DisplayViews = views.Select(v => v.Copy()).ToList();
                if (draft.DisplayViews.Count == 0) throw new ArgumentException("至少保留一个显示视图。");
                draft.ApplyDisplayView(activeId); draft.Validate();
                // View changes never capture plugin state or rebuild the audio graph.
                current.DisplayViews = draft.DisplayViews; current.ActiveDisplayViewId = activeId;
                foreach (var track in draft.Tracks) service.SetTrackVisible(track.Id, track.Visible);
                service.SelectTrack(draft.SelectedTrackId);
                _ownerProject.Rack = service.Project;
                if (!window.TrySaveProject(false))
                {
                    service.Project.DisplayViews = previousViews; service.Project.ActiveDisplayViewId = previousId;
                    foreach (var pair in previousVisible) service.SetTrackVisible(pair.Key, pair.Value);
                    service.SelectTrack(previousSelected);
                    _ownerProject.Rack = service.Project; window.TrySaveProject(false); window.UpdateProjectTitle(); window.RackUiChanged?.Invoke();
                    throw new IOException("显示视图未保存，已恢复原显示。");
                }
                window.RackUiChanged?.Invoke();
            }
            finally { window._rackCommit.Release(); }
        }
        public async Task ApplyIoAsync(RackProject project, RackLocalSettings local)
        {
            RequireEditing(); var identity = RackIdentity; project.Validate(); local.Validate();
            if (local.RoutePlayer && project.PlaybackTrackId is null) throw new ArgumentException("请选择播放器接入的立体声播放轨。");
            var service = window.RackService; await window._rackCommit.WaitAsync();
            try
            {
                RequireCurrent(service); RequireRack(identity); var previous = await service.CapturePluginStatesAsync(); RequireCurrent(service);
                var oldLocal = RackWorkspaceControl.CopyLocal(Local); var wasRunning = service.Status.IsRunning;
                var draft = RackIoTrackCreation.Prepare(previous, project);
                // Scans in another window may have completed after the draft opened.
                local.PluginLocations = new(Local.PluginLocations); local.PluginCatalog = Local.PluginCatalog.ToList(); local.ScanDirectories = [.. Local.ScanDirectories]; local.PluginFileStamps = new(Local.PluginFileStamps); local.InsertHeight = Local.InsertHeight; local.SendHeight = Local.SendHeight; local.OutputHeight = Local.OutputHeight; local.FaderHeight = Local.FaderHeight;
                local.PluginDefaultDirectoriesInitialized = Local.PluginDefaultDirectoriesInitialized; local.InitialPluginScanCompleted = Local.InitialPluginScanCompleted;
                local.RowAppearance = Local.RowAppearance.Copy(); local.CompactView = Local.CompactView; local.FavoritePlugins = [.. Local.FavoritePlugins]; local.RecentPlugins = [.. Local.RecentPlugins];
                try
                {
                    
                    RequireCurrent(service); RequireEditing(); await service.ResetConfigurationAsync(draft, local); RequireCurrent(service);
                    _ownerProject.Rack = service.Project; window.RackAppSettings.RackLocal = local;
                    if (!window.TrySaveProject(false)) throw new IOException("音频 I/O 工程配置未保存。");
                    window.RackSettingsStore.Save(window.RackAppSettings);
                    if (local.InitialPluginScanCompleted || local.PluginCatalog.Count > 0) await service.EnsureReadyAsync();
                    RequireCurrent(service); window.RackSettingsStore.Save(window.RackAppSettings); window.RackUiChanged?.Invoke();
                }
                catch
                {
                    if (IsCurrent(service))
                    {
                        _ownerProject.Rack = previous; window.RackAppSettings.RackLocal = oldLocal;
                        var restored = false;
                        try { await service.ResetConfigurationAsync(previous, oldLocal); restored = true; }
                        catch
                        {
                            if (IsCurrent(service))
                            {
                                try { await service.StopAsync(); } catch { }
                                if (IsCurrent(service)) { service.ReplaceProject(previous); if (!service.Status.IsRunning) service.UpdateLocal(oldLocal, preservePluginScan: true); oldLocal.WasRunning = false; }
                            }
                        }
                        RequireCurrent(service);
                        if (wasRunning && restored) { try { await service.StartAsync(); } catch { oldLocal.WasRunning = false; } }
                        if (IsCurrent(service)) { window.TrySaveProject(false); try { window.RackSettingsStore.Save(window.RackAppSettings); } catch { } window.RackUiChanged?.Invoke(); }
                    }
                    throw;
                }
            }
            finally { window._rackCommit.Release(); }
        }
        public async Task ApplyLocalAsync(RackLocalSettings local)
        {
            RequireCurrent(); var service = window.RackService;
            local = RackWorkspaceControl.CopyLocal(local);
            local.Validate(); await window._rackCommit.WaitAsync();
            try
            {
                RequireCurrent(service); var previous = window.RackAppSettings.RackLocal; var wasRunning = service.Status.IsRunning;
                local.PluginLocations = new(previous.PluginLocations); local.PluginCatalog = previous.PluginCatalog.ToList(); local.PluginFileStamps = new(previous.PluginFileStamps);
                local.PluginDefaultDirectoriesInitialized = previous.PluginDefaultDirectoriesInitialized; local.InitialPluginScanCompleted = previous.InitialPluginScanCompleted;
                // Directory edits belong to ApplyPluginManagementAsync. A stale
                // display/device draft must not erase defaults found by a scan.
                local.ScanDirectories = [.. previous.ScanDirectories];
                local.WasRunning = previous.WasRunning;
                if (!IsEditing)
                {
                    var allowed = RackWorkspaceControl.CopyLocal(previous);
                    allowed.InsertHeight = local.InsertHeight; allowed.SendHeight = local.SendHeight; allowed.OutputHeight = local.OutputHeight; allowed.FaderHeight = local.FaderHeight; allowed.CompactView = local.CompactView; allowed.RowAppearance = local.RowAppearance.Copy();
                    allowed.FavoritePlugins = [.. local.FavoritePlugins]; allowed.RecentPlugins = [.. local.RecentPlugins];
                    allowed.ShowRackInsert = local.ShowRackInsert; allowed.ShowRackSend = local.ShowRackSend; allowed.ShowRackOutput = local.ShowRackOutput;
                    allowed.ShowRackInspector = local.ShowRackInspector; allowed.ShowRackLibrary = local.ShowRackLibrary;
                    if (JsonSerializer.Serialize(allowed, ProjectStore.JsonOptions) != JsonSerializer.Serialize(local, ProjectStore.JsonOptions)) throw new InvalidOperationException("演播模式只能调整显示与常用插件，不能修改设备或插件目录。");
                }
                bool deviceChange = false;
                try
                {
                bool SameMap(Dictionary<Guid, int[]> left, Dictionary<Guid, int[]> right) => left.Count == right.Count && left.All(p => right.TryGetValue(p.Key, out var values) && p.Value.SequenceEqual(values));
                deviceChange = previous.DeviceApi != local.DeviceApi || previous.InputDeviceId != local.InputDeviceId || previous.OutputDeviceId != local.OutputDeviceId || previous.SampleRate != local.SampleRate || previous.BufferSize != local.BufferSize || previous.RoutePlayer != local.RoutePlayer || !SameMap(previous.InputChannels, local.InputChannels) || !SameMap(previous.OutputChannels, local.OutputChannels) || JsonSerializer.Serialize(previous.InputPorts) != JsonSerializer.Serialize(local.InputPorts) || JsonSerializer.Serialize(previous.OutputPorts) != JsonSerializer.Serialize(local.OutputPorts);
                if (deviceChange) { await service.StopAsync(); RequireCurrent(service);  RequireCurrent(service); local.WasRunning = false; }
                RequireCurrent(service); service.UpdateLocal(local, preservePluginScan: true); window.RackAppSettings.RackLocal = local; window.RackSettingsStore.Save(window.RackAppSettings);
                if (deviceChange) { if (local.InitialPluginScanCompleted || local.PluginCatalog.Count > 0) await service.EnsureReadyAsync(); RequireCurrent(service); window.RackSettingsStore.Save(window.RackAppSettings); }
                window.RackUiChanged?.Invoke();
                }
                catch
                {
                    if (IsCurrent(service))
                    {
                        window.RackAppSettings.RackLocal = previous;
                        if (deviceChange && !ReferenceEquals(service.Local, previous) && service.Status.IsRunning) await service.StopAsync();
                        if (!deviceChange) service.UpdateLocal(previous, preservePluginScan: true);
                        else if (!service.Status.IsRunning) { service.UpdateLocal(previous, preservePluginScan: true); if (wasRunning) try { await service.StartAsync(); } catch { previous.WasRunning = false; } }
                        try { window.RackSettingsStore.Save(window.RackAppSettings); } catch { }
                    }
                    throw;
                }
            }
            finally { window._rackCommit.Release(); }
        }
        public async Task ApplyPluginManagementAsync(RackLocalSettings local, IReadOnlyCollection<string> removedKeys)
        {
            RequireEditing(); var service = window.RackService; await window._rackCommit.WaitAsync();
            try
            {
                RequireCurrent(service); RequireEditing();
                if (service.Status.ScanProgress?.IsRunning == true) throw new InvalidOperationException("插件正在扫描，请等待完成后保存管理设置。");
                var previous = Local; var next = RackWorkspaceControl.CopyLocal(previous); next.ScanDirectories = [.. local.ScanDirectories];
                foreach (var key in removedKeys.Distinct(StringComparer.Ordinal))
                {
                    if (IsPluginReferenced(key)) throw new InvalidOperationException("当前或已保存机架仍在使用这个插件，请先移除对应轨道中的效果器。");
                    next.PluginCatalog.RemoveAll(p => p.Identity.Key == key); next.PluginLocations.Remove(key); next.FavoritePlugins.Remove(key); next.RecentPlugins.Remove(key);
                }
                next.Validate();
                try { service.UpdateLocal(next); window.RackAppSettings.RackLocal = next; window.RackSettingsStore.Save(window.RackAppSettings); window.RackUiChanged?.Invoke(); }
                catch { service.UpdateLocal(previous); window.RackAppSettings.RackLocal = previous; window.RackUiChanged?.Invoke(); throw; }
            }
            finally { window._rackCommit.Release(); }
        }
        public async Task SetRunningAsync(bool running)
        {
            RequireCurrent(); var service = window.RackService; var identity = RackIdentity; await window._rackCommit.WaitAsync();
            try { RequireCurrent(service); if (running) { RequireRack(identity); await service.StartAsync(); } else await service.StopAsync(); RequireCurrent(service); _ownerProject.Rack = service.Project; window.RackSettingsStore.Save(window.RackAppSettings); }
            finally { window._rackCommit.Release(); }
        }
        public async Task UpdateTrackAsync(RackTrack track)
        {
            RequireCurrent(); var service = window.RackService;
            if (!IsEditing)
            {
                var current = service.Project.FindTrack(track.Id) ?? throw new ArgumentException("轨道已不存在。");
                var allowed = current.Copy(); allowed.GainDb = track.GainDb; allowed.Pan = track.Pan; allowed.Mute = track.Mute; allowed.Solo = track.Solo; allowed.SoloSafe = track.SoloSafe; allowed.Monitor = track.Monitor; allowed.MainOutputEnabled = track.MainOutputEnabled; allowed.MainOutputGainDb = track.MainOutputGainDb;
                foreach (var send in allowed.Sends) if (track.Sends.FirstOrDefault(s => s.Id == send.Id) is { } edited) { send.GainDb = edited.GainDb; send.Enabled = edited.Enabled; send.PreFader = edited.PreFader; }
                foreach (var insert in allowed.Inserts) if (track.Inserts.FirstOrDefault(i => i.Id == insert.Id) is { } edited) { insert.Bypass = edited.Bypass; insert.Wet = edited.Wet; }
                if (JsonSerializer.Serialize(allowed, ProjectStore.JsonOptions) != JsonSerializer.Serialize(track, ProjectStore.JsonOptions)) throw new InvalidOperationException("演播模式不能修改名称、插件身份或结构。");
            }
            await service.UpdateTrackAsync(track); RequireCurrent(service); _ownerProject.Rack = service.Project; window.ScheduleRackSave(); window.RackUiChanged?.Invoke();
        }
        public async Task RenameTrackAsync(Guid id, string name)
        {
            RequireEditing(); var service = window.RackService; var identity = RackIdentity;
            await window._rackCommit.WaitAsync();
            try
            {
                RequireCurrent(service); RequireRack(identity);
                RequireEditing(); var previous = service.Project.FindTrack(id)?.Name ?? throw new ArgumentException("轨道已不存在。");
                service.RenameTrack(id, name); _ownerProject.Rack = service.Project;
                if (!window.TrySaveProject(false))
                {
                    service.RenameTrack(id, previous); _ownerProject.Rack = service.Project;
                    window.TrySaveProject(false); window.UpdateProjectTitle(); window.RackUiChanged?.Invoke();
                    throw new IOException("名称未保存，请检查磁盘或文件权限。");
                }
                window.RackUiChanged?.Invoke();
            }
            finally { window._rackCommit.Release(); }
        }
        public Task SelectTrackAsync(Guid? id)
        {
            RequireCurrent(); var service = window.RackService;
            service.SelectTrack(id); _ownerProject.Rack = service.Project;
            window.ScheduleRackSave(); window.RackUiChanged?.Invoke(); return Task.CompletedTask;
        }
        public async Task ScanPluginsAsync(CancellationToken token) { RequireEditing(); var service = window.RackService; await service.ScanPluginsAsync(token); RequireCurrent(service); window.RackSettingsStore.Save(window.RackAppSettings); }
        public async Task CheckPluginChangesAsync(CancellationToken token) { RequireEditing(); var service = window.RackService; await service.CheckPluginChangesAsync(token); RequireCurrent(service); window.RackSettingsStore.Save(window.RackAppSettings); }
        public async Task ShowPluginEditorAsync(Guid trackId, Guid insertId, nint owner) { RequireCurrent(); await window.RackService.ShowPluginEditorAsync(trackId, insertId, owner); RequireCurrent(); if (Project.FindTrack(trackId)?.Inserts.FirstOrDefault(p => p.Id == insertId) is { } plugin) await RememberPluginAsync(plugin.Identity.Key); }
        public async Task<IReadOnlyList<RackUiDevice>> EnumerateDevicesAsync(string? api = null) { RequireCurrent(); var service = window.RackService; var devices = await service.EnumerateDevicesAsync(api); RequireCurrent(service); return devices; }
        public async Task<RackUiDevice> ProbeDeviceAsync(string api, string deviceId) { RequireEditing(); var service = window.RackService; var device = await service.ProbeDeviceAsync(api, deviceId); RequireCurrent(service); return device; }
        public async Task ShowDriverControlPanelAsync(string deviceId) { RequireEditing(); var service = window.RackService; await service.ShowDriverControlPanelAsync(deviceId); RequireCurrent(service); }
    }
}
