using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LightDeck.Core;

namespace LightDeck.App;

/// <summary>One owned native engine per project, shared by both workspace windows.
/// No plugin binary is ever loaded into the lighting process.</summary>
public sealed partial class AudioRackService : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1), _graphGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _localDirectory;
    private readonly string? _nativeDirectory;
    private RackEngineProcess? _engine;
    private Task? _poll;
    private bool _disposed, _graphPrepared;
    private readonly object _controlGate = new();
    private Task? _controlFlush;
    private RackProject? _pendingControls;
    private long _controlVersion;
    private readonly ConcurrentDictionary<Guid, string> _pluginLoadErrors = new();
    public RackProject Project { get; private set; }
    public RackLocalSettings Local { get; private set; }
    public RackUiStatus Status { get; private set; } = new();
    private object? _catalogSource;
    private IReadOnlyList<RackPluginIdentity> _catalogView = Array.Empty<RackPluginIdentity>();
    public IReadOnlyList<RackPluginIdentity> Plugins
    {
        get
        {
            lock (_controlGate)
            {
                // Scan/management publish replacement collections. Meter refreshes
                // must not sort the complete inventory or allocate a new view.
                if (!ReferenceEquals(_catalogSource, Local.PluginCatalog))
                {
                    _catalogView = Local.PluginCatalog.Where(x => x.Available).Select(x => x.Identity).DistinctBy(x => x.Key).OrderBy(x => x.Vendor).ThenBy(x => x.Name).ToArray();
                    _catalogSource = Local.PluginCatalog;
                }
                return _catalogView;
            }
        }
    }
    public event Action? Changed;
    public async Task<bool> EnsureReadyAsync()
    {
        if (Status.IsRunning) return true;
        if (string.IsNullOrWhiteSpace(Local.OutputDeviceId) || !Project.Tracks.Any(t => t.Kind == RackTrackKind.Output)) return false;
        await StartAsync().ConfigureAwait(false); return true;
    }
    public AudioRackService(RackProject project, RackLocalSettings local, string localDirectory)
        : this(project, local, localDirectory, null) { }
    internal AudioRackService(RackProject project, RackLocalSettings local, string localDirectory, string? nativeDirectory)
    { Project = project; Local = local; _localDirectory = Path.GetFullPath(localDirectory); _nativeDirectory = nativeDirectory is null ? null : Path.GetFullPath(nativeDirectory); RefreshPluginErrors(); }
    private string NativePath(string name) => _nativeDirectory is null ? FindNative(name) : Path.Combine(_nativeDirectory, name);
    public void ReplaceProject(RackProject project) { project.Validate(); Project = project; Notify(); }
    public void UpdateLocal(RackLocalSettings local, bool preservePluginScan = false)
    {
        local.Validate();
        lock (_controlGate)
        {
            if (Status.IsRunning && (local.DeviceApi != Local.DeviceApi || local.InputDeviceId != Local.InputDeviceId || local.OutputDeviceId != Local.OutputDeviceId || local.SampleRate != Local.SampleRate || local.BufferSize != Local.BufferSize ||
                JsonSerializer.Serialize(local.InputChannels) != JsonSerializer.Serialize(Local.InputChannels) || JsonSerializer.Serialize(local.OutputChannels) != JsonSerializer.Serialize(Local.OutputChannels) ||
                JsonSerializer.Serialize(local.InputPorts) != JsonSerializer.Serialize(Local.InputPorts) || JsonSerializer.Serialize(local.OutputPorts) != JsonSerializer.Serialize(Local.OutputPorts)))
                throw new InvalidOperationException("请先停止机架，再更改音频设备与通道。");
            if (preservePluginScan) PreservePluginScanState(local);
            Local = local;
        }
        Notify();
    }
    // Caller holds _controlGate, shared with scan snapshot/publication. Device
    // drafts and rollback snapshots do not own the current plugin inventory.
    private void PreservePluginScanState(RackLocalSettings local)
    {
        if (ReferenceEquals(local, Local)) return;
        local.ScanDirectories = [.. Local.ScanDirectories];
        local.PluginCatalog = Local.PluginCatalog.ToList();
        local.PluginLocations = new(Local.PluginLocations);
        local.PluginFileStamps = new(Local.PluginFileStamps);
        local.PluginDefaultDirectoriesInitialized = Local.PluginDefaultDirectoriesInitialized;
        local.InitialPluginScanCompleted = Local.InitialPluginScanCompleted;
    }
    public async Task InitializeAsync()
    {
        await _lifecycle.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_engine is { IsAlive: true }) return;
            if (_graphPrepared) throw new IOException("音频引擎已丢失当前效果器状态。请先停止机架，再明确点击启动以使用已保存参数恢复。");
            if (_engine is not null) await _engine.DisposeAsync().ConfigureAwait(false);
            var engine = new RackEngineProcess(NativePath("Yideck.Audio.Engine.exe"));
            engine.Failed += Fail;
            try { await engine.RequestAsync("ping", cancellation: _lifetime.Token).ConfigureAwait(false); }
            catch { await engine.DisposeAsync(); throw; }
            _engine = engine;
            _poll ??= PollAsync();
        }
        finally { _lifecycle.Release(); }
    }
    internal static string FindNative(string name)
    {
        foreach (var relative in new[] { "audio-native", "native-audio/bin" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, relative, name);
            if (File.Exists(path)) return path;
        }
        // Development uses the same built binary; installed builds only match the first path.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "native-audio", "bin", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("音频机架组件缺失，请重新安装易音。", name);
    }
    internal async Task<JsonElement> CommandAsync(string op, JsonObject? fields = null, CancellationToken ct = default)
    { await InitializeAsync().ConfigureAwait(false); return await _engine!.RequestAsync(op, fields, ct).ConfigureAwait(false); }
    internal async Task DetachMediaAsync(long generation)
    {
        // Cleanup must never launch a replacement engine or replay controls after a fault.
        if (_engine is not { IsAlive: true } engine) return;
        try { await engine.RequestAsync("detachMedia", new() { ["generation"] = generation }, _lifetime.Token).ConfigureAwait(false); }
        catch (Exception) when (_disposed || !engine.IsAlive) { }
    }

    private async Task ConfigureAsync()
    {
        var devices = await EnumerateDevicesAsync(Local.DeviceApi).ConfigureAwait(false);
        if (Local.DeviceApi == "asio")
        {
            if (string.IsNullOrWhiteSpace(Local.OutputDeviceId)) throw new InvalidOperationException("请先选择 ASIO 驱动。");
            var selected = devices.SingleOrDefault(d => d.Id == Local.OutputDeviceId) ?? throw new IOException("上次的 ASIO 驱动未注册或不可用，请重新选择；不会改用其他驱动。");
            selected = await ProbeDeviceUnderGateAsync("asio", selected.Id).ConfigureAwait(false);
            devices = devices.Select(d => d.Id == selected.Id ? selected : d).ToArray();
        }
        var configuration = BuildConfiguration(devices);
        _diagnostics.Configuration(Local, devices);
        await CommandAsync("configure", configuration).ConfigureAwait(false);
    }
    internal JsonObject BuildConfiguration(IReadOnlyList<RackUiDevice> devices)
    {
        Local.Validate(); Project.Validate();
        if (string.IsNullOrWhiteSpace(Local.OutputDeviceId)) throw new InvalidOperationException("请先选择机架声卡和输出通道。");
        var inputs = new Dictionary<string, int>(); var outputs = new Dictionary<string, int>();
        void Include(string endpoint, int[] channels, bool input, string name)
        {
            var device = devices.SingleOrDefault(d => d.Id == endpoint) ?? throw new IOException($"“{name}”的声卡端点不可用；不会改用系统默认设备。");
            if (!device.Available || device.ProbeRequired) throw new IOException($"请先查询“{name}”的驱动通道与缓冲参数。");
            if (RackLocalSettings.NormalizeDeviceApi(device.Api) != Local.DeviceApi) throw new IOException("一个机架不能混用 WASAPI 与 ASIO 设备。");
            var available = input ? device.InputChannels : device.OutputChannels;
            if (channels.Length == 0 || channels.Any(c => c < 0 || c >= available)) throw new IOException($"“{name}”的通道超出所选声卡范围。");
            var destination = input ? inputs : outputs;
            destination[endpoint] = Math.Max(destination.GetValueOrDefault(endpoint), channels.Max() + 1);
        }
        foreach (var port in Project.InputPorts)
            if (Local.InputPorts.TryGetValue(port.Id, out var hardware))
            { if (hardware.Channels.Length != port.Channels) throw new IOException($"输入“{port.Name}”的物理通道数量不匹配。"); Include(hardware.DeviceId, hardware.Channels, true, port.Name); }
        foreach (var port in Project.OutputPorts)
            if (Local.OutputPorts.TryGetValue(port.Id, out var hardware))
            { if (hardware.Channels.Length != port.Channels) throw new IOException($"输出“{port.Name}”的物理通道数量不匹配。"); Include(hardware.DeviceId, hardware.Channels, false, port.Name); }
        foreach (var track in Project.Tracks)
        {
            if (track.Kind is not (RackTrackKind.Output or RackTrackKind.Mono or RackTrackKind.Stereo)) continue;
            var mapped = HardwareFor(track);
            if (mapped.Channels is null || mapped.Channels.Length != track.Channels) throw new IOException($"轨道“{track.Name}”尚未配置 {track.Channels} 个物理通道。");
            Include(mapped.Endpoint, mapped.Channels, track.Kind != RackTrackKind.Output, track.Name);
        }
        if (!outputs.ContainsKey(Local.OutputDeviceId)) throw new IOException("时钟输出端点未分配到工程的物理输出，请在音频 I/O 中明确选择。");
        var chosen = inputs.Keys.Concat(outputs.Keys).Distinct().Select(id => devices.Single(d => d.Id == id)).ToArray();
        if (Local.DeviceApi == "asio")
        {
            if (outputs.Count != 1 || inputs.Count > 1 || chosen.Length != 1 || !Local.OutputDeviceId.StartsWith("asio:", StringComparison.OrdinalIgnoreCase)) throw new IOException("ASIO 机架必须使用同一个驱动的输入和输出，不能混用多个驱动。");
            var driver = chosen[0];
            if (Local.SampleRate is not (44100 or 48000) || driver.SampleRates?.Contains(Local.SampleRate) != true) throw new IOException("机架当前支持驱动确认的 44100 或 48000 Hz；请选择已报告的采样率。");
            ValidateAsioBuffer(driver, Local.BufferSize);
        }
        else
        {
        if (chosen.Length > 1 && (chosen.Any(d => string.IsNullOrWhiteSpace(d.GroupId)) || chosen.Select(d => d.GroupId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)) throw new IOException("所有输入输出端点必须来自同一张声卡；设备分组无法确认时不自动混用。");
        if (Local.SampleRate is not (44100 or 48000) || chosen.Any(d => d.PreferredSampleRate != 0 && d.PreferredSampleRate != Local.SampleRate)) throw new IOException("所有声卡端点的当前共享采样率必须一致并为 44100 或 48000 Hz；不会自动重采样物理输入输出。");
        }
        if (inputs.Count + outputs.Count > 32 || inputs.Values.Sum() > 256 || outputs.Values.Sum() > 256) throw new IOException("机架最多使用 32 个设备流，每方向不超过 256 个通道。");
        JsonArray Endpoints(Dictionary<string, int> values) => new(values.Select(p => (JsonNode)new JsonObject { ["deviceId"] = p.Key, ["channels"] = p.Value }).ToArray());
        return new() { ["api"] = Local.DeviceApi, ["inputEndpoints"] = Endpoints(inputs), ["outputEndpoints"] = Endpoints(outputs), ["clockOutputDeviceId"] = Local.OutputDeviceId,
            ["sampleRate"] = Local.SampleRate, ["bufferFrames"] = Local.BufferSize };
    }
    private (string Endpoint, int[]? Channels) HardwareFor(RackTrack track)
    {
        if (track.Kind is not (RackTrackKind.Output or RackTrackKind.Mono or RackTrackKind.Stereo)) return ("", null);
        bool output = track.Kind == RackTrackKind.Output;
        var port = output ? track.HardwareOutputPortId : track.InputPortId;
        if (port is { } id) return (output ? Local.OutputPorts : Local.InputPorts).TryGetValue(id, out var map) ? (map.DeviceId, map.Channels) : ("", null);
        (output ? Local.OutputChannels : Local.InputChannels).TryGetValue(track.Id, out var channels);
        return (output ? Local.OutputDeviceId : Local.InputDeviceId, channels);
    }
    public async Task StartAsync()
    {
        await _graphGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (Status.IsRunning) return;
            _diagnostics.StartRequested(Local);
            SetBusy("正在准备声卡与效果器…");
            if (_engine is not { IsAlive: true }) _graphPrepared = false;
            if (_graphPrepared)
            {
                // A plugin editor may already have created a graph at the old block
                // format. Preserve its stopped-state parameters before reconfiguring.
                var captured = await CaptureStatesUnderGateAsync().ConfigureAwait(false);
                await _engine!.RequestAsync("clearGraph", cancellation: _lifetime.Token).ConfigureAwait(false);
                Project = captured; _graphPrepared = false;
            }
            await ConfigureAsync().ConfigureAwait(false);
            await SendGraphAsync(Project).ConfigureAwait(false);
            var result = await CommandAsync("start").ConfigureAwait(false);
            _diagnostics.Started();
            Local.WasRunning = true; Status = Status with { IsRunning = true, IsBusy = false, Api = Local.DeviceApi, Message = "机架运行中 · " + Local.DeviceApi.ToUpperInvariant() }; Notify();
        }
        catch (Exception ex) { Local.WasRunning = false; Fail(ex); throw; }
        finally { _graphGate.Release(); }
    }
    public async Task StopAsync()
    {
        await _graphGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _diagnostics.StopRequested("user-request");
            if (_engine is { IsAlive: true }) await _engine.RequestAsync("stop").ConfigureAwait(false);
            _diagnostics.Stopped("user-request");
            Local.WasRunning = false; Status = Status with { IsRunning = false, IsBusy = false, Message = "机架已停止" }; Notify();
        }
        catch (Exception ex) { _diagnostics.Fault(ex); throw; }
        finally { _graphGate.Release(); }
    }
    public async Task ResetConfigurationAsync(RackProject project, RackLocalSettings local)
    {
        project.Validate(); local.Validate(); await _graphGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            _diagnostics.StopRequested("configuration-change");
            if (_engine is { IsAlive: true })
            {
                await _engine.RequestAsync("stop", cancellation: _lifetime.Token).ConfigureAwait(false);
                if (_graphPrepared) await _engine.RequestAsync("clearGraph", cancellation: _lifetime.Token).ConfigureAwait(false);
            }
            lock (_controlGate) { RackProjectEdits.PreserveControls(project, Project); PreservePluginScanState(local); Project = project; Local = local; _pendingControls = null; _controlVersion++; }
            _graphPrepared = false; Local.WasRunning = false;
            _diagnostics.Stopped("configuration-change");
            Status = Status with { IsRunning = false, IsBusy = false, Message = "设备配置已保存，请开启机架。" }; Notify();
        }
        finally { _graphGate.Release(); }
    }
    public async Task ApplyGraphAsync(RackProject project, bool prepareNativeGraph = false)
    {
        project.Validate(); await _graphGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            long version; lock (_controlGate) version = _controlVersion;
            // Scanning/enumeration can leave the process alive without a loaded rack.
            // Editing that stopped configuration must not require every remaining
            // plugin to load; otherwise missing slots cannot be removed one by one.
            if (_engine is { IsAlive: true } && (prepareNativeGraph || _graphPrepared || Status.IsRunning))
                await SendGraphAsync(project).ConfigureAwait(false);
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
        }
        finally { _graphGate.Release(); }
    }
    private async Task<JsonElement> SendGraphAsync(RackProject project)
    {
        try
        {
            var result = await CommandAsync("setGraph", new() { ["graph"] = BuildGraph(project) }).ConfigureAwait(false);
            RememberLoadedPlugins(project);
            foreach (var plugin in project.Tracks.SelectMany(t => t.Inserts)) _pluginLoadErrors.TryRemove(plugin.Id, out _);
            _graphPrepared = true; ObserveHardwareReadiness(result); RefreshPluginErrors(); return result;
        }
        catch (Exception ex)
        {
            if (ex.Data["instanceId"] is string id && Guid.TryParse(id, out var instance)) _pluginLoadErrors[instance] = ex.Message;
            RefreshPluginErrors(); Notify(); throw;
        }
    }
    internal JsonObject BuildGraph(RackProject project)
    {
        var solo = RackSoloState.Evaluate(project);
        var nodes = new JsonArray(); var edges = new JsonArray();
        var existing = Project.Tracks.SelectMany(t => t.Inserts.Select(p => (p.Id, p.Identity.Key, t.Channels))).ToDictionary(p => p.Id);
        foreach (var track in project.Tracks)
        {
            var plugins = new JsonArray();
            foreach (var plugin in track.Inserts)
            {
                // A retained instance can still be removed/reordered when its
                // binary disappears. Only a NEW instance needs a loadable file.
                var retained = _graphPrepared && _engine is { IsAlive: true } && _loadedPluginLocations.TryGetValue(plugin.Id, out var loaded) && loaded.Key == plugin.Identity.Key;
                var path = retained ? _loadedPluginLocations[plugin.Id].Path : Local.PluginLocations.GetValueOrDefault(plugin.Identity.Key);
                if (path is null || (!retained && !(File.Exists(path) || Directory.Exists(path))))
                {
                    var error = new IOException($"缺少效果器“{plugin.Identity.Name}”；请重新扫描并安装已授权的插件。"); error.Data["instanceId"] = plugin.Id.ToString(); throw error;
                }
                JsonNode? state = null;
                var reused = retained && existing.TryGetValue(plugin.Id, out var old) && old.Key == plugin.Identity.Key && old.Channels == track.Channels;
                // Reused instances already hold current state. Sending their old
                // opaque blobs adds JSON work and must never restore them.
                if (!reused && plugin.StateBase64.Length != 0) state = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(plugin.StateBase64))) ?? throw new InvalidDataException("效果器保存状态为空。");
                plugins.Add(new JsonObject { ["instanceId"] = plugin.Id.ToString(), ["format"] = plugin.Identity.Format.ToLowerInvariant(),
                    ["classId"] = plugin.Identity.ClassId, ["path"] = path, ["bypass"] = plugin.Bypass, ["wet"] = plugin.Wet, ["state"] = state });
            }
            var hardware = HardwareFor(track);
            nodes.Add(new JsonObject { ["id"] = track.Id.ToString(), ["kind"] = track.Kind switch { RackTrackKind.Mono or RackTrackKind.Stereo => "input", RackTrackKind.Playback => project.PlaybackTrackId == track.Id ? "media" : "bus", RackTrackKind.Output => "output", RackTrackKind.FX => "fx", _ => "bus" },
                ["channels"] = track.Channels, ["endpointId"] = hardware.Endpoint, ["deviceChannels"] = JsonSerializer.SerializeToNode(hardware.Channels ?? Enumerable.Range(0, track.Channels).ToArray()),
                ["gain"] = Gain(track.GainDb), ["pan"] = track.Pan, ["mute"] = solo.IsMuted(track), ["monitor"] = track.Monitor, ["plugins"] = plugins });
            void Edge(string id, Guid target, string source, double gain) => edges.Add(new JsonObject { ["id"] = id, ["from"] = track.Id.ToString(), ["to"] = target.ToString(), ["source"] = source, ["gain"] = gain,
                ["fromChannel"] = 0, ["toChannel"] = 0, ["channels"] = Math.Min(track.Channels, project.FindTrack(target)!.Channels) });
            if (track.OutputTrackId is { } output) Edge(track.Id + ":main", output, "post", track.MainOutputEnabled ? Gain(track.MainOutputGainDb) : 0);
            foreach (var send in track.Sends) Edge(send.Id.ToString(), send.TargetTrackId, send.PreFader ? "pre" : "post", send.Enabled ? Gain(send.GainDb) : 0);
        }
        return new() { ["nodes"] = nodes, ["edges"] = edges };
    }
    private static double Gain(double db) => db <= -90 ? 0 : Math.Pow(10, db / 20);
    public Task UpdateTrackAsync(RackTrack track)
    {
        lock (_controlGate)
        {
            var copy = Project.Copy(); var index = copy.Tracks.FindIndex(t => t.Id == track.Id);
            if (index < 0) throw new ArgumentException("轨道已不存在。");
            MergeControls(copy.Tracks[index], track); copy.Validate(); Project = copy; _pendingControls = copy; _controlVersion++;
            return _controlFlush ??= Task.Run(FlushControlsAsync);
        }
    }
    public void RenameTrack(Guid id, string name)
    {
        lock (_controlGate)
        {
            var copy = Project.Copy();
            var track = copy.FindTrack(id) ?? throw new ArgumentException("轨道已不存在。");
            track.Name = name.Trim(); copy.Validate(); Project = copy;
            // A label change has no audio graph or native controls to submit.
            if (_pendingControls?.FindTrack(id) is { } pending) pending.Name = track.Name;
        }
        Notify();
    }
    public void SetTrackVisible(Guid id, bool visible)
    {
        lock (_controlGate)
        {
            var copy = Project.Copy(); var track = copy.FindTrack(id) ?? throw new ArgumentException("轨道已不存在。");
            track.Visible = visible; Project = copy;
            if (_pendingControls?.FindTrack(id) is { } pending) pending.Visible = visible;
        }
        Notify();
    }
    public void SelectTrack(Guid? id)
    {
        lock (_controlGate)
        {
            if (id is not null && Project.FindTrack(id.Value) is null) return;
            var copy = Project.Copy(); copy.SelectedTrackId = id; Project = copy;
            if (_pendingControls is not null) _pendingControls.SelectedTrackId = id;
        }
        Notify();
    }
    private static void MergeControls(RackTrack target, RackTrack source)
    {
        target.GainDb = source.GainDb; target.Pan = source.Pan; target.Mute = source.Mute; target.Solo = source.Solo; target.SoloSafe = source.SoloSafe; target.Monitor = source.Monitor; target.MainOutputEnabled = source.MainOutputEnabled; target.MainOutputGainDb = source.MainOutputGainDb;
        foreach (var send in target.Sends)
            if (source.Sends.FirstOrDefault(s => s.Id == send.Id) is { } current) { send.GainDb = current.GainDb; send.Enabled = current.Enabled; send.PreFader = current.PreFader; }
        foreach (var insert in target.Inserts)
            if (source.Inserts.FirstOrDefault(i => i.Id == insert.Id && i.Identity.Key == insert.Identity.Key) is { } current) { insert.Bypass = current.Bypass; insert.Wet = current.Wet; }
    }
    private async Task FlushControlsAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(16, _lifetime.Token).ConfigureAwait(false);
                await _graphGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    RackProject? latest; lock (_controlGate) { latest = _pendingControls; _pendingControls = null; }
                    if (latest is not null && _engine is { IsAlive: true } && _graphPrepared)
                    {
                        await _engine.RequestAsync("setControls", BuildControls(latest), _lifetime.Token).ConfigureAwait(false);
                    }
                }
                finally { _graphGate.Release(); }
                lock (_controlGate) { if (_pendingControls is not null) continue; _controlFlush = null; break; }
            }
            Notify();
        }
        catch { lock (_controlGate) _controlFlush = null; throw; }
    }
    public Task<RackProject> CapturePluginStatesAsync() => CapturePluginStatesAsync(null);
    public async Task<RackProject> CapturePluginStatesAsync(IReadOnlyCollection<Guid>? sourceIds)
    {
        await _graphGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try { return await CaptureStatesUnderGateAsync(sourceIds).ConfigureAwait(false); }
        finally { _graphGate.Release(); }
    }
    private async Task<RackProject> CaptureStatesUnderGateAsync(IReadOnlyCollection<Guid>? sourceIds = null)
    {
        var snapshot = Project.Copy();
        if (!_graphPrepared) return snapshot;
        if (_engine is not { IsAlive: true }) throw new IOException("机架引擎已结束，无法获取最后的效果器参数；上一份有效保存仍保留。");
        foreach (var plugin in snapshot.Tracks.SelectMany(t => t.Inserts))
        {
            if (sourceIds is not null && !sourceIds.Contains(plugin.Id)) continue;
            var state = await _engine.RequestAsync("pluginState", new() { ["instanceId"] = plugin.Id.ToString() }, _lifetime.Token).ConfigureAwait(false);
            plugin.StateBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(state.GetRawText()));
        }
        lock (_controlGate)
            foreach (var target in snapshot.Tracks)
                if (Project.FindTrack(target.Id) is { } current) { MergeControls(target, current); target.Name = current.Name; target.Visible = current.Visible; }
        snapshot.Validate(); return snapshot;
    }
    public async Task ShowPluginEditorAsync(Guid trackId, Guid insertId, nint owner)
    {
        await _graphGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (Project.FindTrack(trackId)?.Inserts.All(i => i.Id != insertId) != false) throw new ArgumentException("效果器已不存在。");
            if (!_graphPrepared) { await InitializeAsync(); await SendGraphAsync(Project); }
            _engine!.AllowEditorForeground();
            await CommandAsync("editor", new() { ["instanceId"] = insertId.ToString(), ["show"] = true, ["owner"] = owner.ToInt64(), ["ownerWindowHandle"] = unchecked((ulong)owner.ToInt64()) });
        }
        finally { _graphGate.Release(); }
    }
    public async Task HidePluginEditorsAsync()
    {
        await _graphGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (!_graphPrepared || _engine is not { IsAlive: true } engine) return;
            foreach (var plugin in Project.Tracks.SelectMany(t => t.Inserts)) await engine.RequestAsync("editor", new() { ["instanceId"] = plugin.Id.ToString(), ["show"] = false }, _lifetime.Token).ConfigureAwait(false);
        }
        finally { _graphGate.Release(); }
    }
    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / 60));
        var statusAt = 0L;
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (_engine is not { IsAlive: true } engine || Status.IsBusy) continue;
                try
                {
                    var meterResult = await engine.TryRequestAsync("meters", _lifetime.Token).ConfigureAwait(false);
                    if (meterResult is not { } m) continue;
                    var meters = new Dictionary<Guid, RackUiMeter>();
                    if (m.TryGetProperty("meters", out var items)) foreach (var item in items.EnumerateArray())
                        if (Guid.TryParse(Text(item, "id"), out var id)) meters[id] = new(Number(item, "peakLeft"), Number(item, "peakRight"), Flag(item, "clipped"), Number(item, "rmsLeft"), Number(item, "rmsRight"));
                    Status = Status with { Meters = meters };
                    if (Stopwatch.GetTimestamp() < statusAt) { Notify(); continue; }
                    var statusResult = await engine.TryRequestAsync("status", _lifetime.Token).ConfigureAwait(false);
                    if (statusResult is not { } s) { Notify(); continue; }
                    statusAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
                    _diagnostics.Observe(s);
                    var pluginLatency = new Dictionary<Guid, double>();
                    var sampleRate = Number(s, "sampleRate");
                    if (sampleRate > 0 && double.IsFinite(sampleRate) && s.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
                        foreach (var track in tracks.EnumerateArray())
                            if (Guid.TryParse(Text(track, "id"), out var trackId) && track.TryGetProperty("pluginLatencySamples", out var samples) &&
                                samples.ValueKind == JsonValueKind.Number && samples.TryGetDouble(out var frames) && frames >= 0 && double.IsFinite(frames))
                                pluginLatency[trackId] = frames * 1000 / sampleRate;
                    Status = Status with { IsRunning = Flag(s, "running"), Api = Text(s, "api", Local.DeviceApi), DriverResetRequired = Flag(s, "driverResetRequired"), CpuPercent = Number(s, "cpuPercent"),
                        LatencyMilliseconds = Number(s, "deviceLatencyFrames") * 1000 / Math.Max(1, Number(s, "sampleRate", Local.SampleRate)), Meters = meters,
                        ActualSampleRate = (int)Number(s, "sampleRate"), ActualBufferFrames = (int)Number(s, "bufferFrames"), PluginLatencyMilliseconds = pluginLatency, OutputSignal = ParseOutputSignal(s) };
                    if (!string.IsNullOrWhiteSpace(Text(s, "error")))
                        Status = Status with { Message = Text(s, "error") };
                    else if (Flag(s, "restartRequired"))
                        Status = Status with { Message = "效果器延迟或声道布局已变化，请停止并重新启动机架后使用。" };
                    else ObserveHardwareReadiness(s);
                    Notify();
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                catch (Exception ex) { Fail(ex); }
            }
        }
        catch (OperationCanceledException) { }
    }
    private void SetBusy(string message) { Status = Status with { IsBusy = true, Message = message }; Notify(); }
    internal static RackUiOutputSignal? ParseOutputSignal(JsonElement status)
    {
        if (!status.TryGetProperty("outputSignal", out var signal) || signal.ValueKind != JsonValueKind.Object) return null;
        long Count(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.TryGetInt64(out var count) ? count : 0;
        long? integerClipped = status.TryGetProperty("asio", out var asio) && asio.ValueKind == JsonValueKind.Object && asio.TryGetProperty("integerOutputOverRangeSamples", out var counter) && counter.TryGetInt64(out var clipped) ? clipped : null;
        return new(Number(signal, "lastBlockPeak"), Number(signal, "peakSinceReset"), Count(signal, "overRangeSamples"), Count(signal, "nonFiniteSamples"), Count(signal, "framesObserved"), integerClipped);
    }
    private void Fail(Exception ex) { if (!_disposed) _diagnostics.Fault(ex); Local.WasRunning = false; Status = Status with { IsRunning = false, IsBusy = false, Meters = null, Message = "机架输出已停止：" + ex.Message }; Notify(); }
    private void Notify() { foreach (var handler in Changed?.GetInvocationList() ?? []) try { ((Action)handler)(); } catch { /* Closing view cannot terminate audio. */ } }
    internal static string Text(JsonElement e, string key, string fallback = "") => e.TryGetProperty(key, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : v.ToString() : fallback;
    internal static double Number(JsonElement e, string key, double fallback = 0) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : fallback;
    internal static bool Flag(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        _diagnostics.StopRequested("service-dispose"); _engine?.BeginShutdown(); _lifetime.Cancel();
        if (_poll is not null) await _poll.ConfigureAwait(false);
        if (_controlFlush is not null) try { await _controlFlush.ConfigureAwait(false); } catch (Exception) { }
        if (_engine is not null) await _engine.DisposeAsync().ConfigureAwait(false);
        _diagnostics.Stopped("service-dispose");
        _lifetime.Dispose();
    }
}

internal sealed class RackEngineProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<long, string> _pendingOperations = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Task _reader, _errors;
    private long _sequence; private bool _disposed;
    private int _closing, _termination, _failureReported;
    private readonly string _purpose;
    public bool IsAlive => !_disposed && !_process.HasExited;
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    public void AllowEditorForeground() { if (IsAlive) _ = AllowSetForegroundWindow(_process.Id); }
    public event Action<Exception>? Failed;
    public RackEngineProcess(string executable, string purpose = "rack")
    {
        _purpose = purpose is "probe" or "driver-panel" ? purpose : "rack";
        _process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
        try { if (!_process.Start()) throw new IOException("无法启动音频引擎。"); }
        catch (Exception ex) { DiagnosticLog.Write("机架 IPC", "rack-process-start-failed", DiagnosticLevel.Error, "音频引擎进程未启动", "启动失败；未记录可执行文件路径或异常原文。", [.. DiagnosticLog.ExceptionFacts(ex), ("purpose", _purpose)]); _process.Dispose(); throw; }
        DiagnosticLog.Write("机架 IPC", "rack-process-started", DiagnosticLevel.Info, "音频引擎进程已创建", "仅表示自有子进程创建成功，不代表声卡已经运行。", ("purpose", _purpose), ("processId", _process.Id));
        _reader = ReadAsync(); _errors = DrainErrorsAsync();
    }
    public async Task<JsonElement> RequestAsync(string op, JsonObject? fields = null, CancellationToken cancellation = default)
    {
        if (!IsAlive) throw new IOException("音频引擎已关闭。");
        // The native control thread is sequential. Start timeout only when this request
        // actually owns the lane, so a short status request cannot kill a slow snapshot.
        try { await _write.WaitAsync(cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) { LogCancellation(op, "cancelled-before-send", false); throw; }
        return await RequestEnteredAsync(op, fields, cancellation).ConfigureAwait(false);
    }
    public async Task<JsonElement?> TryRequestAsync(string op, CancellationToken cancellation)
    {
        if (!IsAlive || !await _write.WaitAsync(0, cancellation).ConfigureAwait(false)) return null;
        return await RequestEnteredAsync(op, null, cancellation).ConfigureAwait(false);
    }
    private async Task<JsonElement> RequestEnteredAsync(string op, JsonObject? fields, CancellationToken cancellation)
    {
        var id = Interlocked.Increment(ref _sequence); var source = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.TryAdd(id, source);
        _pendingOperations[id] = RackDiagnosticState.Operation(op);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        // Some driver control panels are modal until the user closes them. The
        // owning service lifetime still cancels and kills this isolated process.
        if (op != "driverControlPanel") timeout.CancelAfter(TimeSpan.FromSeconds(op is "setGraph" or "beginGraphEdit" or "pluginState" or "editor" or "probeDevice" ? 45 : 8));
        try
        {
            var request = fields?.DeepClone().AsObject() ?? new(); request["id"] = id; request["op"] = op;
            await _process.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), timeout.Token).ConfigureAwait(false); await _process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            return await source.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Unknown outcome cannot leave a late graph/start command running after cancel.
            var cancelled = cancellation.IsCancellationRequested;
            Interlocked.CompareExchange(ref _termination, cancelled ? 1 : 2, 0);
            LogCancellation(op, cancelled ? "caller-cancelled" : "response-timeout", true);
            KillOwned(); var error = new IOException($"音频引擎 {op} 已取消或超时，输出已停止。");
            error.Data["rackDiagnosticCode"] = cancelled ? "ipc-cancelled" : "ipc-timeout"; throw error;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            if (ex.Data["rackDiagnosticCode"] is null && Volatile.Read(ref _closing) == 0)
                DiagnosticLog.Write("机架 IPC", "rack-ipc-transport-error", DiagnosticLevel.Error, "音频引擎通信未完成", "管道或进程通信失败，原因不能直接归为 ASIO 掉线。", [.. DiagnosticLog.ExceptionFacts(ex), ("operation", RackDiagnosticState.Operation(op)), ("purpose", _purpose)]);
            throw;
        }
        finally { _pending.TryRemove(id, out _); _pendingOperations.TryRemove(id, out _); _write.Release(); }
    }
    private async Task ReadAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                using var json = JsonDocument.Parse(line); var root = json.RootElement;
                if (!root.TryGetProperty("id", out var id) || !_pending.TryGetValue(id.GetInt64(), out var source)) continue;
                if (!AudioRackService.Flag(root, "ok"))
                {
                    var code = RackDiagnosticState.NativeCode(AudioRackService.Text(root, "error"));
                    var error = new IOException(code is "plugin-bus-arrangement-rejected" or "plugin-current-state-failed" or "graph-channel-range" or "graph-endpoint-unresolved" ? RackDiagnosticState.Reason(code) : AudioRackService.Text(root, "error", "音频引擎请求失败。"));
                    error.Data["rackDiagnosticCode"] = code;
                    DiagnosticLog.Write("机架 IPC", "rack-native-request-failed", DiagnosticLevel.Error, "原生音频引擎拒绝请求", RackDiagnosticState.Reason(code),
                        ("causeCode", code), ("purpose", _purpose), ("operation", _pendingOperations.GetValueOrDefault(id.GetInt64(), "other")),
                        ("asioErrorCode", RackDiagnosticState.AsioErrorCode(AudioRackService.Text(root, "error"))));
                    if (root.TryGetProperty("instanceId", out var instanceId)) error.Data["instanceId"] = instanceId.GetString();
                    source.TrySetException(error);
                }
                else source.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : JsonSerializer.SerializeToElement(new { }));
            }
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _closing) == 0)
            {
                DiagnosticLog.Write("机架 IPC", "rack-ipc-read-failed", DiagnosticLevel.Error, "音频引擎响应读取失败", "响应结构或管道异常；未记录原始帧和异常原文。", [.. DiagnosticLog.ExceptionFacts(ex), ("purpose", _purpose)]);
                ReportFailure(ex);
            }
        }
        finally
        {
            var error = new IOException("音频引擎进程已结束，机架输出停止。");
            var closing = Volatile.Read(ref _closing) != 0; var termination = Volatile.Read(ref _termination);
            int? exitCode = null;
            try
            {
                using var wait = new CancellationTokenSource(1000);
                await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                exitCode = _process.ExitCode;
            }
            catch (OperationCanceledException) { } catch (InvalidOperationException) { }
            var reason = termination == 1 ? "已取消的 IPC 请求触发自有进程终止。" : termination == 2 ? "IPC 响应超时后终止自有进程。" : closing ? "主动关闭子进程。" : "子进程或输出管道意外结束；没有足够证据认定驱动或硬件掉线。";
            DiagnosticLog.Write("机架 IPC", closing || termination != 0 ? "rack-process-exited" : "rack-process-unexpected-exit", closing && termination == 0 ? DiagnosticLevel.Info : DiagnosticLevel.Error,
                "音频引擎进程或输出流已结束", reason, ("purpose", _purpose), ("processId", _process.Id), ("exitCode", exitCode), ("intent", termination == 1 ? "ipc-cancelled" : termination == 2 ? "ipc-timeout" : closing ? "shutdown" : "unknown"));
            error.Data["rackDiagnosticCode"] = termination == 1 ? "ipc-cancelled" : termination == 2 ? "ipc-timeout" : closing ? "shutdown" : "process-exit-unknown";
            if (!closing) ReportFailure(error);
            foreach (var source in _pending.Values) source.TrySetException(error);
        }
    }
    private void ReportFailure(Exception error) { if (Volatile.Read(ref _closing) == 0 && Interlocked.Exchange(ref _failureReported, 1) == 0) Failed?.Invoke(error); }
    internal void BeginShutdown() => Interlocked.Exchange(ref _closing, 1);
    private void LogCancellation(string op, string cause, bool sent)
    {
        var closing = Volatile.Read(ref _closing) != 0;
        DiagnosticLog.Write("机架 IPC", cause == "response-timeout" ? "rack-ipc-timeout" : "rack-ipc-cancelled", closing ? DiagnosticLevel.Info : DiagnosticLevel.Warning,
            cause == "response-timeout" ? "音频引擎请求超时" : "音频引擎请求已取消", closing ? "主动关闭期间取消等待。" : sent ? "已发送请求的结果未知，自有子进程将终止以防迟到操作。" : "请求尚未发送，不终止正在处理其他请求的进程。",
            ("operation", RackDiagnosticState.Operation(op)), ("purpose", _purpose), ("cause", cause), ("sent", sent), ("intentionalShutdown", closing));
    }
    private async Task DrainErrorsAsync() { try { while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) { } } catch (IOException) { } }
    private void KillOwned() { try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        BeginShutdown();
        try { if (IsAlive) await RequestAsync("shutdown").ConfigureAwait(false); } catch (Exception) { KillOwned(); }
        _disposed = true;
        if (!_process.HasExited) { using var wait = new CancellationTokenSource(3000); try { await _process.WaitForExitAsync(wait.Token); } catch (OperationCanceledException) { KillOwned(); } }
        await Task.WhenAll(_reader, _errors).ConfigureAwait(false); _process.Dispose();
    }
    public static async Task<JsonElement> ScanAsync(string executable, string path, CancellationToken token)
    {
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 } };
        process.StartInfo.ArgumentList.Add(path);
        if (!process.Start()) throw new IOException("插件扫描进程未启动。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); var output = await stdout; _ = await stderr;
            using var json = JsonDocument.Parse(output);
            if (process.ExitCode != 0 || !AudioRackService.Flag(json.RootElement, "ok")) throw new IOException(AudioRackService.Text(json.RootElement, "error", "插件扫描失败。"));
            return json.RootElement.Clone();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            if (token.IsCancellationRequested) throw;
            throw new IOException("扫描超时（45 秒），该插件尚未确认兼容。");
        }
    }
}
