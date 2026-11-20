using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LightDeck.Core;

namespace LightDeck.App;

public static partial class RackWorkspaceAudit
{
    private static void Modal(Window owner, Action open, Action<Window> answer)
        => ModalTask(owner, open, dialog => { answer(dialog); return Task.CompletedTask; });
    private static void ModalTask(Window owner, Action open, Func<Window, Task> answer)
    {
        Exception? failure = null; var answered = false;
        owner.Dispatcher.BeginInvoke(new Action(async () => { Window? dialog = null; try { await Settle(); dialog = Application.Current.Windows.Cast<Window>().Single(w => w.IsVisible && ReferenceEquals(w.Owner, owner)); answered = true; await answer(dialog); } catch (Exception ex) { failure = ex; dialog?.Close(); } }), DispatcherPriority.ApplicationIdle);
        open(); if (failure is not null) throw failure; if (!answered) throw new InvalidOperationException("Expected modal dialog was not handled.");
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static bool StripsFillViewport(RackWorkspaceControl view)
    {
        var mixer = Find<ScrollViewer>(view, "rack-mixer"); var content = (FrameworkElement)mixer.Content;
        var host = (IRackUiHost)typeof(RackWorkspaceControl).GetProperty("Host", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
        return mixer.ScrollableHeight < 1 && host.Project.Tracks.All(track =>
        {
            var strip = Find<Border>(view, "rack-strip-" + track.Id); var label = Find<TextBlock>(view, "rack-plugin-latency-" + track.Id);
            var edge = strip.TranslatePoint(new Point(0, strip.ActualHeight), content).Y;
            var labelEdge = label.TranslatePoint(new Point(0, label.ActualHeight), strip).Y;
            return Math.Abs(edge - mixer.ViewportHeight) < 2 && strip.ActualHeight - labelEdge is >= 7 and <= 12;
        });
    }
    private static IEnumerable<DependencyObject> Elements(DependencyObject root) { yield return root; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Elements(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => Elements(root).OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
    private static async Task Settle() { await Task.Delay(80); await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
    private static async Task Until(Func<bool> condition) { for (var i = 0; i < 100; i++) { if (condition()) return; await Task.Delay(30); } throw new TimeoutException("UI condition timed out"); }
    private static void Capture(Window window, string path) { window.UpdateLayout(); var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); encoder.Save(file); }
    private sealed partial class FixtureHost : IRackUiHost
    {
        public RackProject Project { get; private set; } = CreateProject();
        public RackLocalSettings Local { get; private set; } = new() { DeviceApi = "wasapi", InputDeviceId = "fixture-input", OutputDeviceId = "fixture-output", SampleRate = 48000, BufferSize = 256 };
        public Dictionary<Guid, double> PluginLatencies { get; } = [];
        public RackUiOutputSignal? OutputSignal;
        public RackUiStatus Status => new(Running, false, Running ? "隔离测试机架运行中" : "隔离测试机架已停止", 12, 5.3, Project.Tracks.ToDictionary(t => t.Id, _ => new RackUiMeter(.45, .32, false, .14, .1)), 48000, 480, PluginLatencies, Local.DeviceApi, DriverResetRequired, ScanProgress: ScanProgress, OutputSignal: OutputSignal);
        public bool Editing = true, Running = true, FailApply, FailLocal, FailRename;
        public bool IsEditing => Editing;
        public IReadOnlyList<RackPluginIdentity> Plugins { get; } = [new() { ClassId = "fixture-eq", Name = "参数均衡器", Vendor = "UI fixture" }, new() { ClassId = "fixture-compressor", Name = "压缩器", Vendor = "Studio B" }, new() { ClassId = "fixture-reverb", Name = "混响", Vendor = "UI fixture" }];
        public FixtureHost() { Local.PluginLocations = Plugins.ToDictionary(p => p.Key, p => Path.Combine(AppContext.BaseDirectory, "isolated-fixture", p.ClassId + ".vst3")); Local.PluginCatalog = Plugins.Select(p => new RackPluginCatalogEntry { Identity = p, Path = Path.Combine(AppContext.BaseDirectory, "isolated-fixture", p.ClassId + ".vst3"), Available = true }).ToList(); }
        public event Action? Changed;
        public int Applies, StartStopCalls;
        public (Guid, Guid) EditorTarget;
        public void Notify() => Changed?.Invoke();
        public Task ApplyProjectAsync(RackProject project) { project.Validate(); if (FailApply) throw new IOException("隔离保存失败"); Project = project.Copy(); Applies++; Notify(); return Task.CompletedTask; }
        public Task ApplyLocalAsync(RackLocalSettings local) { if (FailLocal) throw new IOException("隔离本机保存失败"); Local = CopyLocal(local); Notify(); return Task.CompletedTask; }
        public Task SetRunningAsync(bool running) { StartStopCalls++; return Task.CompletedTask; }
        public int LiveCalls;
        public async Task UpdateTrackAsync(RackTrack track) { LiveCalls++; await Task.Delay(20); var copy = Project.Copy(); copy.Tracks[copy.Tracks.FindIndex(t => t.Id == track.Id)] = track.Copy(); Project = copy; Notify(); }
        public Task RenameTrackAsync(Guid id, string name) { if (FailRename) throw new IOException("隔离改名保存失败"); Project.FindTrack(id)!.Name = name; Notify(); return Task.CompletedTask; }
        public Task SelectTrackAsync(Guid? id) { Project.SelectedTrackId = id; Notify(); return Task.CompletedTask; }
        public Task ScanPluginsAsync(CancellationToken token) => FixtureScanAsync(false, token);
        public Task ShowPluginEditorAsync(Guid trackId, Guid insertId, nint owner) { EditorTarget = (trackId, insertId); return Task.CompletedTask; }
        public Task<IReadOnlyList<RackUiDevice>> EnumerateDevicesAsync(string? api = null) => Task.FromResult<IReadOnlyList<RackUiDevice>>((api ?? Local.DeviceApi) == "asio" ? AsioDevices : IoFixtureDevices ?? [new("fixture-input", "隔离输入设备", "wasapi", 8, 0, [44100, 48000], 48000, "card-one", "隔离声卡"), new("fixture-output", "隔离输出设备", "wasapi", 0, 8, [44100, 48000, 96000], 48000, "card-one", "隔离声卡"), new("fixture-output-alt", "另一输出设备", "wasapi", 0, 2, [44100, 48000, 96000], 44100, "card-one", "隔离声卡"), new("other-input", "另一声卡输入", "wasapi", 1, 0, [44100], 44100, "card-two", "隔离声卡"), new("other-output", "另一声卡输出", "wasapi", 0, 2, [44100], 44100, "card-two", "隔离声卡")]);
        public static RackProject CreateProject()
        {
            var a = RackTrack.Create(RackTrackKind.Mono, "话筒 1"); a.GainDb = -6; a.Monitor = true;
            var b = RackTrack.Create(RackTrackKind.Mono, "话筒 2"); var stereo = RackTrack.Create(RackTrackKind.Stereo, "立体声输入"); var player = RackTrack.Create(RackTrackKind.Stereo, "伴奏输入");
            var bus = RackTrack.Create(RackTrackKind.Bus, "人声编组 BUS"); var fx = RackTrack.Create(RackTrackKind.FX, "共享混响 FX"); var output = RackTrack.Create(RackTrackKind.Output, "主输出"); var phones = RackTrack.Create(RackTrackKind.Output, "耳机监听");
            a.OutputTrackId = b.OutputTrackId = bus.Id; stereo.OutputTrackId = player.OutputTrackId = bus.OutputTrackId = output.Id; fx.OutputTrackId = bus.Id;
            a.Sends.Add(new RackSend { TargetTrackId = fx.Id, GainDb = -12 }); a.Sends.Add(new RackSend { TargetTrackId = phones.Id, GainDb = -6, PreFader = true });
            return new RackProject { Tracks = [a, b, stereo, player, bus, fx, output, phones], SelectedTrackId = a.Id };
        }
    }
}
