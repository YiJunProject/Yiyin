using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using LightDeck.Core;

namespace LightDeck.App;

public sealed partial class RackWorkspaceControl
{
    private const string RackDragFormat = "Yideck.Rack.ControlDrag";
    private enum RackDragKind { Track, Insert, Send, Plugin }
    private enum RackDropKind { Track, Insert, Send, Output }
    private sealed record RackDrag(IRackUiHost Host, RackDragKind Kind, Guid TrackId, Guid ItemId = default, RackPluginIdentity? Plugin = null);
    private Canvas? _dragOverlay;
    private Image? _dragGhost;
    private Border? _dropMarker;
    private Border? _dragDeleteHint;
    private FrameworkElement? _dragOrigin;
    private double _dragOriginOpacity;
    private void BeginDragVisual(FrameworkElement source)
    {
        EndDragVisual(); if (source.ActualWidth <= 0 || source.ActualHeight <= 0) return;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(source.ActualWidth), (int)Math.Ceiling(source.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(source); bitmap.Freeze();
        _dragOverlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true }; Grid.SetRowSpan(_dragOverlay, 3); Panel.SetZIndex(_dragOverlay, 50); Children.Add(_dragOverlay);
        _dragGhost = new Image { Source = bitmap, Width = source.ActualWidth, Height = source.ActualHeight, Opacity = .76, IsHitTestVisible = false }; AutomationProperties.SetAutomationId(_dragGhost, "rack-drag-ghost");
        _dropMarker = new Border { Background = DeckTheme.Brush(_theme.Accent), CornerRadius = new CornerRadius(2), Visibility = Visibility.Collapsed }; AutomationProperties.SetAutomationId(_dropMarker, "rack-drag-marker");
        _dragDeleteHint = new Border { Background = DeckTheme.Brush("#A62A3F"), Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(4), Visibility = Visibility.Collapsed, Child = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold } }; AutomationProperties.SetAutomationId(_dragDeleteHint, "rack-drag-delete-hint");
        _dragOverlay.Children.Add(_dropMarker); _dragOverlay.Children.Add(_dragGhost); _dragOverlay.Children.Add(_dragDeleteHint); _dragOrigin = source; _dragOriginOpacity = source.Opacity; source.Opacity = .45;
        MoveDragGhost(Mouse.GetPosition(this));
    }
    private void MoveDragGhost(Point position) { if (_dragGhost is null) return; Canvas.SetLeft(_dragGhost, position.X + 12); Canvas.SetTop(_dragGhost, position.Y + 12); if (_dragDeleteHint is not null) { Canvas.SetLeft(_dragDeleteHint, Math.Max(0, Math.Min(ActualWidth - 225, position.X + 12))); Canvas.SetTop(_dragDeleteHint, Math.Max(0, position.Y - 28)); } }
    private void ShowDropMarker(FrameworkElement target, RackDropKind kind, bool after)
    {
        if (_dropMarker is null) return; if (_dragDeleteHint is not null) _dragDeleteHint.Visibility = Visibility.Collapsed; var point = target.TranslatePoint(new Point(), this); _dropMarker.Visibility = Visibility.Visible;
        if (kind == RackDropKind.Track) { _dropMarker.Width = 4; _dropMarker.Height = target.ActualHeight; Canvas.SetLeft(_dropMarker, point.X + (after ? target.ActualWidth - 2 : -2)); Canvas.SetTop(_dropMarker, point.Y); }
        else { _dropMarker.Width = target.ActualWidth; _dropMarker.Height = 4; Canvas.SetLeft(_dropMarker, point.X); Canvas.SetTop(_dropMarker, point.Y + (after ? target.ActualHeight - 2 : -2)); }
    }
    private void ShowDropResult(RackDrag drag, Guid target, RackDropKind kind)
    {
        if (_dragDeleteHint?.Child is not TextBlock text) return;
        var name = _host.Project.FindTrack(target)?.Name ?? "目标轨道";
        text.Text = kind switch
        {
            RackDropKind.Track => "移动轨道顺序",
            RackDropKind.Send when drag.Kind == RackDragKind.Plugin => "新建 FX，并从「" + name + "」发送",
            RackDropKind.Insert when drag.Kind == RackDragKind.Plugin => "插入插件到「" + name + "」",
            _ => drag.TrackId == target ? "调整本轨顺序" : "复制到「" + name + "」· 原件保留"
        };
        _dragDeleteHint.Background = DeckTheme.Brush(_theme.Accent); text.Foreground = DeckTheme.Brush(_theme.AccentText); _dragDeleteHint.Visibility = Visibility.Visible;
    }
    private void EndDragVisual()
    {
        if (_dragOrigin is not null) _dragOrigin.Opacity = _dragOriginOpacity;
        if (_dragOverlay is not null) Children.Remove(_dragOverlay); _dragOverlay = null; _dragGhost = null; _dropMarker = null; _dragDeleteHint = null; _dragOrigin = null;
    }
    private void EnableOutsideRemoval()
    {
        AllowDrop = true;
        bool Removable(DragEventArgs e, out RackDrag? drag)
        {
            drag = e.Data.GetDataPresent(RackDragFormat) ? e.Data.GetData(RackDragFormat) as RackDrag : null;
            if (!_host.IsEditing || _busy || (e.KeyStates & DragDropKeyStates.ControlKey) == 0 || drag is null || !ReferenceEquals(drag.Host, _host) || drag.Kind is not (RackDragKind.Insert or RackDragKind.Send)) return false;
            var areaId = "rack-" + (drag.Kind == RackDragKind.Insert ? "insert" : SendGoesToOutput(drag) ? "output" : "send") + "-area-" + drag.TrackId;
            var source = FindVisual(this, areaId); if (source is null) return false; var module = source.Parent as FrameworkElement ?? source; var position = e.GetPosition(this);
            return new Rect(0, 0, ActualWidth, ActualHeight).Contains(position) && !new Rect(module.TranslatePoint(new Point(), this), new Size(module.ActualWidth, module.ActualHeight)).Contains(position);
        }
        DragOver += (_, e) =>
        {
            MoveDragGhost(e.GetPosition(this)); var remove = Removable(e, out var drag); e.Effects = remove ? DragDropEffects.Move : DragDropEffects.None;
            if (_dragDeleteHint is not null) { _dragDeleteHint.Background = DeckTheme.Brush("#A62A3F"); ((TextBlock)_dragDeleteHint.Child).Foreground = Brushes.White; _dragDeleteHint.Visibility = remove ? Visibility.Visible : Visibility.Collapsed; ((TextBlock)_dragDeleteHint.Child).Text = drag?.Kind == RackDragKind.Insert ? "松手移除此插件" : "松手移除此发送 · 保留目标轨道"; }
            if (_dropMarker is not null) _dropMarker.Visibility = Visibility.Collapsed; e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (!Removable(e, out var drag) || drag is null) return; e.Handled = true; e.Effects = DragDropEffects.Move; EndDragVisual();
            Run(() => MutateProjectAsync(project => { var track = project.FindTrack(drag.TrackId) ?? throw new InvalidOperationException("轨道已删除"); if (drag.Kind == RackDragKind.Insert) track.Inserts.RemoveAll(p => p.Id == drag.ItemId); else track.Sends.RemoveAll(s => s.Id == drag.ItemId); }));
        };
    }
    private static FrameworkElement? FindVisual(DependencyObject root, string id)
    {
        if (root is FrameworkElement element && AutomationProperties.GetAutomationId(element) == id) return element;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (FindVisual(VisualTreeHelper.GetChild(root, i), id) is { } found) return found;
        return null;
    }
    private void MakeDragSource(FrameworkElement element, Func<RackDrag> create)
    {
        Point? start = null;
        element.PreviewMouseLeftButtonDown += (_, e) => { start = _host.IsEditing && !_busy ? e.GetPosition(element) : null; };
        element.PreviewMouseLeftButtonUp += (_, _) => start = null;
        element.PreviewMouseMove += (_, e) =>
        {
            if (start is not { } origin || e.LeftButton != MouseButtonState.Pressed || !_host.IsEditing || _busy) return;
            var point = e.GetPosition(element); if (Math.Abs(point.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            start = null; e.Handled = true; BeginDragVisual(element);
            try { DragDrop.DoDragDrop(element, new DataObject(RackDragFormat, create()), DragDropEffects.Copy | DragDropEffects.Move); }
            finally { EndDragVisual(); }
        };
    }
    private void MakeDropTarget(FrameworkElement element, Guid target, RackDropKind kind, int index, bool row = false)
    {
        element.AllowDrop = true;
        bool Accept(DragEventArgs e) => _host.IsEditing && !_busy && (kind is not (RackDropKind.Send or RackDropKind.Output) || _host.Project.FindTrack(target)?.Kind != RackTrackKind.Output) && e.Data.GetDataPresent(RackDragFormat) && e.Data.GetData(RackDragFormat) is RackDrag drag && ReferenceEquals(drag.Host, _host) && (kind switch { RackDropKind.Track => drag.Kind == RackDragKind.Track && TrackGroup(_host.Project.FindTrack(drag.TrackId)) == TrackGroup(_host.Project.FindTrack(target)), RackDropKind.Insert => drag.Kind is RackDragKind.Insert or RackDragKind.Plugin, RackDropKind.Output => drag.Kind == RackDragKind.Send && SendGoesToOutput(drag), _ => drag.Kind == RackDragKind.Plugin || drag.Kind == RackDragKind.Send && !SendGoesToOutput(drag) });
        element.DragOver += (_, e) => { MoveDragGhost(e.GetPosition(this)); if (!Accept(e) || e.Data.GetData(RackDragFormat) is not RackDrag drag) { e.Effects = DragDropEffects.None; if (_dropMarker is not null) _dropMarker.Visibility = Visibility.Collapsed; return; } e.Effects = drag.Kind == RackDragKind.Track || drag.TrackId == target ? DragDropEffects.Move : DragDropEffects.Copy; ShowDropMarker(element, kind, kind == RackDropKind.Track ? e.GetPosition(element).X >= element.ActualWidth / 2 : row && e.GetPosition(element).Y >= element.ActualHeight / 2); ShowDropResult(drag, target, kind); e.Handled = true; };
        element.DragLeave += (_, _) => { if (_dropMarker is not null) _dropMarker.Visibility = Visibility.Collapsed; };
        element.Drop += (_, e) =>
        {
            if (!Accept(e) || e.Data.GetData(RackDragFormat) is not RackDrag drag) { e.Effects = DragDropEffects.None; return; }
            var destination = kind == RackDropKind.Track ? _host.Project.Tracks.FindIndex(t => t.Id == target) + (e.GetPosition(element).X >= element.ActualWidth / 2 ? 1 : 0) : index + (row && e.GetPosition(element).Y >= element.ActualHeight / 2 ? 1 : 0);
            if (drag.TrackId == target || kind == RackDropKind.Track)
            {
                var source = kind == RackDropKind.Track ? _host.Project.Tracks.FindIndex(t => t.Id == drag.TrackId) : kind == RackDropKind.Insert ? _host.Project.FindTrack(target)?.Inserts.FindIndex(p => p.Id == drag.ItemId) ?? -1 : _host.Project.FindTrack(target)?.Sends.FindIndex(s => s.Id == drag.ItemId) ?? -1;
                if (source >= 0 && source < destination) destination--;
            }
            e.Handled = true; e.Effects = kind == RackDropKind.Track || drag.TrackId == target ? DragDropEffects.Move : DragDropEffects.Copy;
            EndDragVisual(); Run(() => DropAsync(drag, target, kind, destination));
        };
    }
    private static int TrackGroup(RackTrack? track) => track?.Kind switch { RackTrackKind.Bus or RackTrackKind.FX => 1, RackTrackKind.Output => 2, _ => 0 };
    private bool SendGoesToOutput(RackDrag drag) => _host.Project.FindTrack(drag.TrackId)?.Sends.FirstOrDefault(s => s.Id == drag.ItemId) is { } send && _host.Project.FindTrack(send.TargetTrackId)?.Kind == RackTrackKind.Output;
    private Task DropAsync(RackDrag drag, Guid target, RackDropKind kind, int destination)
    {
        if (!_host.IsEditing || !ReferenceEquals(drag.Host, _host)) throw new InvalidOperationException("请在当前工程的编辑模式中拖动。");
        return kind switch
        {
            RackDropKind.Track when drag.Kind == RackDragKind.Track => _host.ReorderTrackAsync(drag.TrackId, destination),
            RackDropKind.Insert when drag.Kind == RackDragKind.Plugin && drag.Plugin is not null => _host.AddPluginAsync(target, drag.Plugin, destination),
            RackDropKind.Send when drag.Kind == RackDragKind.Plugin && drag.Plugin is not null => _host.AddPluginSendAsync(target, drag.Plugin),
            RackDropKind.Insert when drag.Kind == RackDragKind.Insert => _host.TransferInsertAsync(drag.TrackId, drag.ItemId, target, destination),
            RackDropKind.Send or RackDropKind.Output when drag.Kind == RackDragKind.Send => _host.TransferSendAsync(drag.TrackId, drag.ItemId, target, destination),
            _ => throw new InvalidOperationException("此内容不能放到该区域。")
        };
    }
}
