using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using LightDeck.Core;

namespace LightDeck.App;

public sealed partial class RackWorkspaceControl
{
    private sealed class TabletLayoutMemory { public Size Size; public double FixedHeight; public double HeaderHeight; public double Dpi = 1; public string Density = ""; public string Signature = ""; public Size Fallback; }
    private static readonly ConditionalWeakTable<IRackUiHost, TabletLayoutMemory> TabletLayouts = new();
    private static readonly ConditionalWeakTable<RackProject, TabletLayoutMemory> UnmountedTabletLayouts = new();
    private void CaptureTabletLayout(double fixedHeight)
    {
        if (_mixer.ActualWidth <= 0 || _mixer.ActualHeight <= 0) return;
        var memory = TabletLayouts.GetOrCreateValue(_host);
        memory.Size = new(_mixer.ActualWidth - _mixer.Padding.Left - _mixer.Padding.Right, _mixer.ActualHeight - _mixer.Padding.Top - _mixer.Padding.Bottom);
        memory.FixedHeight = fixedHeight;
        memory.HeaderHeight = Math.Max(0, ActualHeight - memory.Size.Height);
        memory.Dpi = VisualTreeHelper.GetDpi(this).DpiScaleX; memory.Density = WorkspaceUiDensity.Mode;
    }
    internal static TabletRackDesktopLayout ReadTabletLayout(IRackUiHost? host, RackProject project, RackLocalSettings local, RackUiStatus status, DeckTheme theme, Size fallback)
    {
        TabletLayoutMemory? memory = null; if (host is not null) TabletLayouts.TryGetValue(host, out memory);
        if (memory is null || memory.Density != WorkspaceUiDensity.Mode)
        {
            memory = UnmountedTabletLayouts.GetOrCreateValue(project);
            var signature = local.RowAppearance.Signature + ":" + WorkspaceUiDensity.Mode + ":" + string.Join('|', project.Tracks.Where(t => t.Visible).Select(t => $"{t.Id}:{t.Kind}:{t.Name}"));
            if (memory.Signature != signature || memory.Fallback != fallback)
            {
                // Measure the same view without mounting/showing it. This host has no
                // runtime access; device, plugin and mutation operations cannot run.
                var snapshot = new TabletMeasureHost(project, local, status);
                var owner = new Window(); theme.Apply(owner);
                using var view = new RackWorkspaceControl(owner, theme, snapshot);
                owner.Content = view;
                view.Measure(fallback); view.Arrange(new Rect(fallback)); view.UpdateLayout(); view.UpdateResponsiveLayout();
                memory.Size = new(Math.Max(1, view._mixer.ActualWidth - view._mixer.Padding.Left - view._mixer.Padding.Right), Math.Max(1, view._mixer.ActualHeight - view._mixer.Padding.Top - view._mixer.Padding.Bottom));
                memory.FixedHeight = view.FixedStripHeight(); memory.Dpi = VisualTreeHelper.GetDpi(view).DpiScaleX;
                memory.HeaderHeight = Math.Max(0, view.ActualHeight - memory.Size.Height);
                memory.Signature = signature; memory.Fallback = fallback; memory.Density = WorkspaceUiDensity.Mode;
                owner.Content = null; owner.Close();
            }
        }
        var size = memory.Size;
        var tracks = project.Tracks.Where(t => t.Visible).ToArray(); var count = tracks.Length;
        var groups = tracks.Select(t => t.Kind == RackTrackKind.Output ? 2 : t.Kind is RackTrackKind.Bus or RackTrackKind.FX ? 1 : 0).Distinct().Count(g => g != 2);
        var gaps = count * 5d + groups * 14; var fixedHeight = memory.FixedHeight;
        var insertMinimum = local.RowAppearance.ContentHeight("insert", 1);
        var connectionMinimum = Math.Max(local.RowAppearance.ContentHeight("send", 1), local.RowAppearance.ContentHeight("output", 1));
        var insert = local.InsertHeight; var send = local.SendHeight; var output = local.OutputHeight; var fader = local.FaderHeight;
        if (local.CompactView)
        {
            insert = tracks.Any(t => t.Inserts.Count > 0) ? Math.Min(insert, insertMinimum) : 0;
            send = tracks.Any(t => t.Sends.Any(s => project.FindTrack(s.TargetTrackId)?.Kind is RackTrackKind.Bus or RackTrackKind.FX)) ? Math.Min(send, connectionMinimum) : 0;
            output = Math.Min(output, connectionMinimum);
        }
        var minInsert = WorkspaceUiDensity.IsTouch && insert > 0 ? insertMinimum : Math.Min(insert, insertMinimum);
        var minSend = WorkspaceUiDensity.IsTouch && send > 0 ? connectionMinimum : Math.Min(send, connectionMinimum);
        var minimum = minInsert + minSend + connectionMinimum + MinimumFaderHeight;
        var minHeight = count == 0 ? 0 : fixedHeight + minimum;
        var width = Math.Max(0, size.Width - (minHeight > size.Height + .1 ? SystemParameters.VerticalScrollBarWidth : 0));
        var strip = count == 0 ? PreferredStripWidth : Math.Clamp((width - gaps) / count, MinimumStripWidth, PreferredStripWidth);
        strip = Math.Max(MinimumStripWidth, Math.Floor(strip * memory.Dpi) / memory.Dpi);
        insert = Math.Max(minInsert, insert); send = Math.Max(minSend, send); output = Math.Max(connectionMinimum, output); fader = Math.Max(MinimumFaderHeight, fader);
        var optional = insert + send + output + fader - minimum; var budget = Math.Max(0, size.Height - minHeight); var scale = optional > 0 ? Math.Min(1, budget / optional) : 1;
        return new(size.Width, size.Height, Math.Max(width, count * strip + gaps), Math.Max(size.Height, minHeight), strip,
            minInsert + (insert - minInsert) * scale, minSend + (send - minSend) * scale,
            connectionMinimum + (output - connectionMinimum) * scale, MinimumFaderHeight + (fader -MinimumFaderHeight) * scale, fixedHeight, WorkspaceUiDensity.Mode, memory.HeaderHeight, local.RowAppearance.Copy());
    }

    private sealed class TabletMeasureHost(RackProject project, RackLocalSettings local, RackUiStatus status) : IRackUiHost
    {
        public RackProject Project => project;
        public RackLocalSettings Local => local;
        public RackUiStatus Status => status;
        public bool IsEditing => false;
        public IReadOnlyList<RackPluginIdentity> Plugins => local.PluginCatalog.Where(p => p.Available).Select(p => p.Identity).ToArray();
        public event Action? Changed { add { } remove { } }
        private static Task ReadOnly() => throw new InvalidOperationException("只读布局测量不能执行控制。");
        public Task ApplyProjectAsync(RackProject value) => ReadOnly();
        public Task ApplyLocalAsync(RackLocalSettings value) => ReadOnly();
        public Task SetRunningAsync(bool value) => ReadOnly();
        public Task UpdateTrackAsync(RackTrack value) => ReadOnly();
        public Task RenameTrackAsync(Guid id, string name) => ReadOnly();
        public Task ReorderTrackAsync(Guid id, int index) => ReadOnly();
        public Task CopyTrackAsync(Guid id) => ReadOnly();
        public Task TransferInsertAsync(Guid source, Guid insert, Guid destination, int index) => ReadOnly();
        public Task TransferSendAsync(Guid source, Guid send, Guid destination, int index) => ReadOnly();
        public Task AddPluginAsync(Guid target, RackPluginIdentity plugin, int index) => ReadOnly();
        public Task AddPluginSendAsync(Guid source, RackPluginIdentity plugin) => ReadOnly();
        public Task SetTrackVisibleAsync(Guid id, bool visible) => ReadOnly();
        public Task ApplyIoAsync(RackProject value, RackLocalSettings settings) => ReadOnly();
        public Task SelectTrackAsync(Guid? id) => ReadOnly();
        public Task ScanPluginsAsync(CancellationToken token) => ReadOnly();
        public Task CheckPluginChangesAsync(CancellationToken token) => ReadOnly();
        public Task ShowPluginEditorAsync(Guid track, Guid insert, nint owner) => ReadOnly();
        public Task<IReadOnlyList<RackUiDevice>> EnumerateDevicesAsync(string? api = null) => throw new InvalidOperationException("只读布局测量不能枚举设备。");
        public Task<RackUiDevice> ProbeDeviceAsync(string api, string id) => throw new InvalidOperationException("只读布局测量不能探测设备。");
        public Task ShowDriverControlPanelAsync(string id) => ReadOnly();
    }
}
