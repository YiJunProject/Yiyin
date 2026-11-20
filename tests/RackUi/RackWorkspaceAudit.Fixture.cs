using System.IO;
using LightDeck.Core;

namespace LightDeck.App;

public static partial class RackWorkspaceAudit
{
    private sealed partial class FixtureHost
    {
        public int IncrementalScans, FullScans, CanceledScans;
        public RackPluginScanProgress? ScanProgress;
        public Task CheckPluginChangesAsync(CancellationToken token) => FixtureScanAsync(true, token);
        private async Task FixtureScanAsync(bool incremental, CancellationToken token)
        {
            if (incremental) IncrementalScans++; else FullScans++;
            ScanProgress = new(true, incremental, incremental ? "检查文件变化" : "完整扫描", 1, 3, "fixture.vst3"); Notify();
            try { await Task.Delay(250, token); } catch (OperationCanceledException) { CanceledScans++; throw; }
            finally { ScanProgress = null; Notify(); }
        }
        public string LastOperation = "";
        public int LastDestination = -1, VisibilityCalls;
        public int ProbeCalls, DriverPanelCalls;
        public bool FailProbe, DriverResetRequired;
        public List<RackUiDevice> AsioDevices = [new("asio:fixture-a", "隔离 ASIO 声卡", "asio", 0, 0, GroupId: "asio:fixture-a", GroupName: "隔离 ASIO 声卡", ProbeRequired: true), new("asio:fixture-b", "另一 ASIO 声卡", "asio", 0, 0, GroupId: "asio:fixture-b", GroupName: "另一 ASIO 声卡", ProbeRequired: true)];
        public async Task<RackUiDevice> ProbeDeviceAsync(string api, string deviceId)
        {
            if (Running) throw new InvalidOperationException("运行中不探测驱动"); ProbeCalls++; await Task.Delay(40); if (FailProbe) throw new IOException("隔离驱动探测失败");
            var device = AsioDevices.Single(d => d.Id == deviceId) with { ProbeRequired = false, InputChannels = 4, OutputChannels = 6, SampleRates = [44100], PreferredSampleRate = 44100, CurrentSampleRate = 44100,
                InputChannelNames = ["Mic / Line 1", "Mic / Line 2", "Loopback L", "Loopback R"], OutputChannelNames = ["Main Left", "Main Right", "Phones Left", "Phones Right", "Virtual Left", "Virtual Right"],
                BufferMinFrames = 64, BufferMaxFrames = 1024, BufferPreferredFrames = 128, BufferGranularity = -1, InputLatencyFrames = 160, OutputLatencyFrames = 192 };
            AsioDevices[AsioDevices.FindIndex(d => d.Id == deviceId)] = device; return device;
        }
        public Task ShowDriverControlPanelAsync(string deviceId)
        {
            if (Running) throw new InvalidOperationException("运行中不打开驱动面板"); DriverPanelCalls++; var index = AsioDevices.FindIndex(d => d.Id == deviceId); AsioDevices[index] = AsioDevices[index] with { ProbeRequired = true }; return Task.CompletedTask;
        }
        private Task Mutate(string operation, Action<RackProject> edit)
        {
            var draft = Project.Copy(); edit(draft); LastOperation = operation; return ApplyProjectAsync(draft);
        }
        public Task ReorderTrackAsync(Guid trackId, int destinationIndex) => Mutate("reorder-track", p =>
        {
            var track = p.FindTrack(trackId)!; p.Tracks.Remove(track); LastDestination = Math.Clamp(destinationIndex, 0, p.Tracks.Count); p.Tracks.Insert(LastDestination, track);
        });
        public Task CopyTrackAsync(Guid trackId) => Mutate("copy-track", p => { var source = p.FindTrack(trackId)!; var copy = source.Copy(true); copy.Name += " 副本"; p.Tracks.Insert(p.Tracks.IndexOf(source) + 1, copy); p.SelectedTrackId = copy.Id; });
        public Task TransferInsertAsync(Guid sourceTrackId, Guid insertId, Guid destinationTrackId, int destinationIndex) => Mutate("transfer-insert", p =>
        {
            var source = p.FindTrack(sourceTrackId)!.Inserts; var target = p.FindTrack(destinationTrackId)!.Inserts; var plugin = source.First(i => i.Id == insertId);
            if (sourceTrackId == destinationTrackId) source.Remove(plugin); else plugin = plugin.Copy(true);
            LastDestination = Math.Clamp(destinationIndex, 0, target.Count); target.Insert(LastDestination, plugin);
        });
        public Task TransferSendAsync(Guid sourceTrackId, Guid sendId, Guid destinationTrackId, int destinationIndex) => Mutate("transfer-send", p =>
        {
            var source = p.FindTrack(sourceTrackId)!.Sends; var target = p.FindTrack(destinationTrackId)!.Sends; var send = source.First(i => i.Id == sendId);
            if (sourceTrackId == destinationTrackId) source.Remove(send); else send = send with { Id = Guid.NewGuid() };
            LastDestination = Math.Clamp(destinationIndex, 0, target.Count); target.Insert(LastDestination, send);
        });
        public Task AddPluginAsync(Guid targetTrackId, RackPluginIdentity plugin, int destinationIndex) => Mutate("add-plugin", p =>
        {
            var target = p.FindTrack(targetTrackId)!.Inserts; LastDestination = Math.Clamp(destinationIndex, 0, target.Count); target.Insert(LastDestination, new() { Identity = plugin with { } });
        });
        public Task AddPluginSendAsync(Guid sourceTrackId, RackPluginIdentity plugin) => Mutate("add-plugin-send", p =>
        {
            var source = p.FindTrack(sourceTrackId)!; var fx = RackTrack.Create(RackTrackKind.FX, plugin.Name + " FX"); fx.Inserts.Add(new() { Identity = plugin with { } });
            fx.OutputTrackId = source.OutputTrackId ?? p.Tracks.FirstOrDefault(t => t.Kind == RackTrackKind.Output)?.Id; p.Tracks.Add(fx); source.Sends.Add(new() { TargetTrackId = fx.Id });
        });
        public Task SetTrackVisibleAsync(Guid id, bool visible)
        {
            if (FailApply) throw new IOException("隔离显示状态保存失败"); Project.FindTrack(id)!.Visible = visible; VisibilityCalls++; Notify(); return Task.CompletedTask;
        }
        public IReadOnlyList<RackUiDevice>? IoFixtureDevices;
        public List<RackLocalSettings> HardwarePreviews { get; } = [];
        public TaskCompletionSource? HoldNextPreview;
        public async Task PreviewHardwareRoutesAsync(RackProject project, RackLocalSettings local)
        {
            if (HoldNextPreview is { } gate) { HoldNextPreview = null; await gate.Task; }
            local.Validate(); HardwarePreviews.Add(CopyLocal(local));
        }
        public Task ApplyIoAsync(RackProject project, RackLocalSettings local)
        {
            project.Validate(); if (FailApply || FailLocal) throw new IOException("隔离 I/O 保存失败");
            Project = RackIoTrackCreation.Prepare(Project, project); Local = CopyLocal(local); Applies++; Notify(); return Task.CompletedTask;
        }
    }
}
