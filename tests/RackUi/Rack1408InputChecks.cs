using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LightDeck.App;

internal static class Rack1408InputChecks
{
    public static async Task RunAsync(string directory)
    {
        var checks = new List<object>(); var failures = 0; Exception? error = null;
        void Check(string name, bool passed) { checks.Add(new { name, passed }); if (!passed) failures++; }
        var fixtureType = typeof(RackWorkspaceAudit).GetNestedType("FixtureHost", BindingFlags.NonPublic)!;
        var host = (IRackUiHost)Activator.CreateInstance(fixtureType)!;
        host.Local.ShowRackInspector = host.Local.ShowRackLibrary = true;
        var first = host.Project.Tracks[0]; first.Pan = .65; var savedGain = first.GainDb;
        var theme = DeckTheme.Find("graphite"); var window = new Window { Width = 1280, Height = 800, Left = 0, Top = 0, ShowInTaskbar = false, Title = "声像与中键横移 · 隔离输入检查" };
        theme.Apply(window); var view = new RackWorkspaceControl(window, theme, host) { Margin = new Thickness(10) }; window.Content = view; window.Show(); await Settle();
        var mixer = Find<ScrollViewer>(view, "rack-mixer");
        var pan = Find<Slider>(view, "rack-pan-" + first.Id); var gain = Find<Slider>(view, "rack-gain-" + first.Id);
        var move = typeof(RackWorkspaceControl).GetMethod("ContinueMixerPan", BindingFlags.Instance | BindingFlags.NonPublic);
        var start = typeof(RackWorkspaceControl).GetField("_mixerPanStart", BindingFlags.Instance | BindingFlags.NonPublic);
        MouseButtonEventArgs Button(UIElement target, MouseButton button, bool down, int clicks = 1)
        {
            var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button) { RoutedEvent = down ? Mouse.PreviewMouseDownEvent : Mouse.PreviewMouseUpEvent };
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(e, clicks); target.RaiseEvent(e); return e;
        }
        async Task MoveTo(double x) { move!.Invoke(view, [x]); await Settle(); }
        async Task Begin(UIElement target) { Button(target, MouseButton.Middle, true); await Settle(); }
        try
        {
            foreach (var value in new[] { .65, -.8 })
            {
                pan.Value = value; await Settle(); var reset = Button(pan, MouseButton.Left, true, 2); await Settle();
                Check("double click resets pan from " + value, reset.Handled && pan.Value == 0 && host.Project.FindTrack(first.Id)!.Pan == 0);
                Check("center label updates immediately from " + value, Find<TextBlock>(view, "rack-pan-number-" + first.Id + "-display").Text.Contains("C"));
                Check("double click does not leave pan dragging from " + value, !pan.IsMouseCaptured);
                Button(pan, MouseButton.Left, false); Check("reset leaves gain unchanged", host.Project.FindTrack(first.Id)!.GainDb == savedGain);
            }
            pan.Value = 0; await Settle();
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent }; pan.RaiseEvent(wheel); await Settle();
            Check("pan wheel remains one normalized unit after reset", wheel.Handled && Math.Abs(host.Project.FindTrack(first.Id)!.Pan - .01) < .000001);
            Slider.IncreaseSmall.Execute(null, pan); await Settle(); Check("pan keyboard command remains available", Math.Abs(pan.Value - .02) < .000001);
            var panThumb = ((Track)pan.Template.FindName("PART_Track", pan)).Thumb;
            panThumb.RaiseEvent(new DragDeltaEventArgs((pan.ActualWidth - panThumb.ActualWidth) / 4, 0) { RoutedEvent = Thumb.DragDeltaEvent }); await Settle();
            Check("pan ordinary thumb dragging is retained", Math.Abs(pan.Value - .52) < .000001);
            Button(pan, MouseButton.Left, true, 2); Button(pan, MouseButton.Left, false); await Settle();
            Check("narrow mixer has real horizontal overflow", mixer.ScrollableWidth > 100);
            Check("middle pan implementation is present", move is not null && start is not null);
            if (move is not null && start is not null)
            {
                mixer.ScrollToHorizontalOffset(100); await Settle(); var graph = JsonSerializer.Serialize(host.Project); var local = JsonSerializer.Serialize(host.Local); var initial = mixer.HorizontalOffset; var oldCursor = mixer.Cursor;
                var gainThumb = ((Track)gain.Template.FindName("PART_Track", gain)).Thumb;
                await Begin(gainThumb); Check("middle down on nested gain thumb captures mixer", mixer.IsMouseCaptured && start.GetValue(view) is double);
                var origin = (double)start.GetValue(view)!;
                var routedMove = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent }; mixer.RaiseEvent(routedMove);
                Check("captured mouse move reaches panning event handler", routedMove.Handled);
                await MoveTo(origin - 80); Check("dragging middle left reveals tracks to the right", Math.Abs(mixer.HorizontalOffset - initial - 80) < 1);
                await MoveTo(origin - 80); Check("stationary pointer does not accumulate scrolling", Math.Abs(mixer.HorizontalOffset - initial - 80) < 1);
                await MoveTo(origin + 50); Check("dragging middle right restores leftward viewport", Math.Abs(mixer.HorizontalOffset - (initial - 50)) < 1);
                await MoveTo(origin - 100000); Check("middle drag clamps at right edge", Math.Abs(mixer.HorizontalOffset - mixer.ScrollableWidth) < 1);
                await MoveTo(origin + 100000); Check("middle drag clamps at left edge", mixer.HorizontalOffset == 0);
                var release = Button(mixer, MouseButton.Middle, false); await Settle();
                Check("middle release restores cursor and capture", release.Handled && !mixer.IsMouseCaptured && start.GetValue(view) is null && Equals(mixer.Cursor, oldCursor));
                await MoveTo(origin - 100); Check("released pan ignores later movement", mixer.HorizontalOffset == 0);
                Check("middle drag leaves audio graph and settings untouched", graph == JsonSerializer.Serialize(host.Project) && local == JsonSerializer.Serialize(host.Local));
                await Begin(mixer); Find<Button>(view, "rack-more").Focus(); await Settle(); Check("focus leaving mixer releases capture", !mixer.IsMouseCaptured && start.GetValue(view) is null);
                await Begin(mixer); typeof(Window).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [EventArgs.Empty]); await Settle(); Check("window deactivation releases capture", !mixer.IsMouseCaptured && start.GetValue(view) is null);
                await Begin(mixer); mixer.ReleaseMouseCapture(); await Settle(); Check("lost capture clears panning state", start.GetValue(view) is null);
                var left = Button(Find<Button>(view, "rack-mute-" + first.Id), MouseButton.Left, true); Check("left button is not consumed by middle pan", !left.Handled && !mixer.IsMouseCaptured && start.GetValue(view) is null); Button(mixer, MouseButton.Left, false);
                var mute = Find<Button>(view, "rack-mute-" + first.Id); var before = host.Project.FindTrack(first.Id)!.Mute; mute.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Settle(); Check("left-button control action remains functional", host.Project.FindTrack(first.Id)!.Mute != before);
                pan.Value = .2; await Settle(); Button(pan, MouseButton.Left, true); Check("ordinary pan left press captures pan instead of mixer", pan.IsMouseCaptured && !mixer.IsMouseCaptured); Button(pan, MouseButton.Left, false); await Settle();
                Button(pan, MouseButton.Left, true, 2); Button(pan, MouseButton.Left, false); await Settle();
                Capture(window, Path.Combine(directory, "rack-input-centered.png"));
                await Begin(mixer); view.Dispose(); Check("disposing active view releases capture", !mixer.IsMouseCaptured && start.GetValue(view) is null);
                var remaining = host.Project.Tracks[0]; host.Project.Tracks.RemoveAll(t => t.Id != remaining.Id); remaining.Sends.Clear(); remaining.OutputTrackId = null; host.Local.ShowRackInspector = host.Local.ShowRackLibrary = false;
                view = new RackWorkspaceControl(window, theme, host); window.Content = view; await Settle(); mixer = Find<ScrollViewer>(view, "rack-mixer");
                var noOverflow = Button(mixer, MouseButton.Middle, true); Check("mixer without overflow does not capture middle input", mixer.ScrollableWidth == 0 && !noOverflow.Handled && !mixer.IsMouseCaptured); Button(mixer, MouseButton.Middle, false);
            }
        }
        catch (Exception ex) { error = ex; failures++; }
        finally
        {
            view.Dispose(); window.Close();
            File.WriteAllText(Path.Combine(directory, "rack-input-report.json"), JsonSerializer.Serialize(new { passed = failures == 0, count = checks.Count, failures, checks, error = error?.ToString(), boundary = "Isolated actual WPF controls, routed button/wheel/focus/capture events. Synthetic pointer coordinates invoke the same production movement method; no physical mouse or keyboard injection, no real audio or MA." }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (failures > 0) throw new InvalidOperationException("Rack input checks failed: " + failures, error);
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root) { yield return root; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Walk(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => Walk(root).OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
    private static async Task Settle() { await Task.Delay(80); await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
    private static void Capture(Window window, string path) { var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); png.Save(stream); }
}
