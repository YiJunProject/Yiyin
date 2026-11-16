using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LightDeck.App;
using LightDeck.Core;

internal static class Rack1407LayoutChecks
{
    public static async Task RunAsync(string directory)
    {
        var checks = new List<object>(); var failures = 0;
        void Check(string name, bool passed) { checks.Add(new { name, passed }); if (!passed) failures++; }
        var type = typeof(RackWorkspaceAudit).GetNestedType("FixtureHost", BindingFlags.NonPublic)!;
        var host = (IRackUiHost)Activator.CreateInstance(type)!;
        var first = host.Project.Tracks[0]; first.Inserts.Add(new() { Identity = host.Plugins[0] });
        var local = host.Local; local.InsertHeight = 0; local.SendHeight = 0; local.OutputHeight = 112; local.CompactView = false;
        local.ShowRackInsert = local.ShowRackSend = local.ShowRackOutput = true;
        local.ShowRackInspector = local.ShowRackLibrary = false;
        var window = new Window { Width = 1600, Height = 1000, Left = 0, Top = 0, ShowInTaskbar = false, Title = "机架分隔拖动 · 隔离检查" };
        var theme = DeckTheme.Find("graphite"); theme.Apply(window);
        var view = new RackWorkspaceControl(window, theme, host) { Margin = new Thickness(10) }; window.Content = view; window.Show(); await Settle();
        FrameworkElement Area(string kind) => (FrameworkElement)Find<ScrollViewer>(view, "rack-" + kind + "-area-" + first.Id).Parent;
        Thumb Grip(string kind) => Find<Thumb>(view, "rack-resize-" + kind + "-" + first.Id);
        double Y(FrameworkElement element) => element.TranslatePoint(new Point(), window).Y;
        double Width() => Find<Border>(view, "rack-strip-" + first.Id).ActualWidth;
        async Task Toggle(string key) { Find<CheckBox>(view, "rack-show-" + key).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Settle(); }
        bool Ordered() => Y(Area("insert")) + Area("insert").ActualHeight <= Y(Area("send")) + 1 && Y(Area("send")) + Area("send").ActualHeight <= Y(Area("output")) + 1;
        async Task Drag(string name, double distance, bool cancel = false)
        {
            var grip = Grip(name); var origin = Y(grip);
            grip.RaiseEvent(new DragStartedEventArgs(10, 10) { RoutedEvent = Thumb.DragStartedEvent });
            for (var i = 1; i <= 12; i++)
            {
                var pointer = origin + distance * i / 12;
                grip.RaiseEvent(new DragDeltaEventArgs(0, pointer - Y(grip)) { RoutedEvent = Thumb.DragDeltaEvent });
                await Settle();
                Check(name + " motion " + i + " keeps modules ordered and grip reachable", Ordered() && grip.IsVisible && grip.ActualHeight >= 20 && Y(grip) >= Y(Area("insert")));
            }
            grip.RaiseEvent(new DragCompletedEventArgs(0, distance, cancel) { RoutedEvent = Thumb.DragCompletedEvent }); await Settle();
        }
        try
        {
            Check("legacy zero heights retain visible insert and send with content", Area("insert").IsVisible && Area("insert").ActualHeight >= 50 && Area("send").IsVisible && Area("send").ActualHeight >= 52);
            Check("insert has no redundant top resize grip", !Walk(view).OfType<Thumb>().Any(t => AutomationProperties.GetAutomationId(t) == "rack-resize-insert-" + first.Id));
            var width = Width(); var mixerBefore = Find<ScrollViewer>(view, "rack-mixer").ActualWidth; var windowBefore = window.ActualWidth; await Toggle("inspector"); Check("inspector expands without changing track width", Math.Abs(Width() - width) < 1); await Toggle("library"); Check("library expands without changing track width", Math.Abs(Width() - width) < 1);
            Check("both panels consume 500 viewport pixels only", Math.Abs(Find<ScrollViewer>(view, "rack-mixer").ActualWidth - mixerBefore + 500) < 1 && window.ActualWidth == windowBefore);
            Find<Button>(view, "rack-close-inspector").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Settle();
            Check("inspector close immediately returns 250 pixels", Math.Abs(Find<ScrollViewer>(view, "rack-mixer").ActualWidth - mixerBefore + 250) < 1);
            Find<Button>(view, "rack-close-library").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Settle();
            Check("closing panels restores viewport and retains window and track width", Math.Abs(Width() - width) < 1 && Math.Abs(Find<ScrollViewer>(view, "rack-mixer").ActualWidth - mixerBefore) < 1 && window.ActualWidth == windowBefore);
            foreach (var (key, title) in new[] { ("insert", "插入"), ("send", "发送"), ("output", "输出") })
            {
                var heading = Walk(Area(key)).OfType<TextBlock>().First(t => t.Text == title);
                Check(key + " heading centered in full bar", heading.TextAlignment == TextAlignment.Center && Math.Abs(heading.TranslatePoint(new Point(heading.ActualWidth / 2, 0), Area(key)).X - Area(key).ActualWidth / 2) < 1);
            }
            Check("fader boundary has centered name", Walk(Grip("fader")).OfType<TextBlock>().Any(t => t.Text == "推子" && t.HorizontalAlignment == HorizontalAlignment.Center));
            Check("control boundary is above pan", Y(Grip("fader")) + Grip("fader").ActualHeight <= Y(Find<Slider>(view, "rack-pan-" + first.Id)));
            foreach (var name in new[] { "send", "output", "fader" })
            {
                var grip = Grip(name);
                Check(name + " entire bar spans strip", grip.ActualWidth >= width - 20);
                Check(name + " title and blank middle are draggable hit targets", new[] { .3, .55, .75 }.All(x =>
                {
                    var hit = window.InputHitTest(grip.TranslatePoint(new Point(grip.ActualWidth * x, grip.ActualHeight / 2), window)) as DependencyObject;
                    while (hit is not null && hit != grip) hit = VisualTreeHelper.GetParent(hit);
                    return hit == grip;
                }));
            }
            Capture(window, Path.Combine(directory, "01-legacy-heights.png"));
            var snapshot = JsonSerializer.Serialize(host.Project);
            foreach (var (grip, above) in new[] { ("send", "insert"), ("output", "send"), ("fader", "output") })
            {
                var before = Area(above).ActualHeight; var own = grip == "fader" ? 0 : Area(grip).ActualHeight;
                await Drag(grip, 36);
                Check(grip + " grows preceding area by pointer displacement", Math.Abs(Area(above).ActualHeight - before - 36) < 2);
                if (grip != "fader") Check(grip + " keeps its own content height", Math.Abs(Area(grip).ActualHeight - own) < 2);
                var saved = JsonSerializer.Serialize(host.Local); await Drag(grip, -20, true);
                Check(grip + " canceled motion restores persisted layout", saved == JsonSerializer.Serialize(host.Local) && Math.Abs(Area(above).ActualHeight - before - 36) < 2);
            }
            await Drag("send", -800); await Drag("output", -800); await Drag("fader", -800);
            Check("upward drag fully collapses insert and send content but retains output row", Math.Abs(Area("insert").ActualHeight - 24) < 1 && Math.Abs(Area("send").ActualHeight - 24) < 1 && Area("output").ActualHeight >= 52 && Ordered());
            var collapsed = JsonSerializer.Serialize(host.Local);
            await host.ApplyLocalAsync(JsonSerializer.Deserialize<RackLocalSettings>(collapsed)!);
            view.Dispose(); view = new RackWorkspaceControl(window, theme, host) { Margin = new Thickness(10) }; window.Content = view; await Settle();
            Check("collapsed heights reopen with reachable bars", Math.Abs(Area("insert").ActualHeight - 24) < 1 && Math.Abs(Area("send").ActualHeight - 24) < 1 && Grip("send").IsVisible && Grip("output").IsVisible);
            Capture(window, Path.Combine(directory, "02-minimum-boundaries.png"));
            await Drag("send", 60); await Drag("output", 60);
            Check("collapsed content expands again", Math.Abs(Area("insert").ActualHeight - 84) < 1 && Math.Abs(Area("send").ActualHeight - 84) < 1);
            await Drag("send", 2000); await Drag("output", 2000); await Drag("fader", 2000);
            Check("extreme downward drags keep pan and fader inside viewport", Y(Find<TextBlock>(view, "rack-plugin-latency-" + first.Id)) < window.ActualHeight - 35 && Grip("fader").IsVisible);
            Check("layout never changes audio graph", snapshot == JsonSerializer.Serialize(host.Project));
            Capture(window, Path.Combine(directory, "03-maximum-boundaries.png"));
            var savedLocal = JsonSerializer.Serialize(host.Local);
            File.WriteAllText(Path.Combine(directory, "saved-local.json"), savedLocal);
            await host.ApplyLocalAsync(JsonSerializer.Deserialize<RackLocalSettings>(File.ReadAllText(Path.Combine(directory, "saved-local.json")))!);
            view.Dispose(); view = new RackWorkspaceControl(window, theme, host) { Margin = new Thickness(10) }; window.Content = view; await Settle();
            Check("saved layout reopens with all module boundaries", savedLocal == JsonSerializer.Serialize(host.Local) && Ordered() && Grip("send").IsVisible && Grip("output").IsVisible && Grip("fader").IsVisible);
            type.GetField("FailLocal")!.SetValue(host, true); await Drag("send", -20); type.GetField("FailLocal")!.SetValue(host, false);
            Check("failed save restores old preferences and leaves controls available", savedLocal == JsonSerializer.Serialize(host.Local) && Ordered() && Grip("send").IsVisible);
            host.Local.CompactView = true; type.GetMethod("Notify")!.Invoke(host, null); await Settle();
            var compactHeight = Area("insert").ActualHeight; await Drag("send", 30);
            Check("compact mode boundary can expand and save detailed height", !host.Local.CompactView && Math.Abs(Area("insert").ActualHeight - compactHeight - 30) < 2);
            foreach (var key in new[] { "insert", "send", "output" }) { await Toggle(key); Check(key + " hidden content leaves header reachable", Area(key).IsVisible && Area(key).ActualHeight >= 24); await Toggle(key); }
            window.Width = 1280; window.Height = 800; await Settle(); await Toggle("inspector"); await Toggle("library");
            Check("narrow sidebar expansion keeps fixed track width", Math.Abs(Width() - width) < 1);
            var mixer = Find<ScrollViewer>(view, "rack-mixer"); mixer.ScrollToRightEnd(); await Settle();
            Check($"narrow view scroll reaches last complete track (x={mixer.HorizontalOffset}, scrollY={mixer.ScrollableHeight}, height={mixer.ViewportHeight})", mixer.HorizontalOffset > 0 && mixer.ScrollableHeight < 1);
            Capture(window, Path.Combine(directory, "04-narrow-sidebars.png"));
            var narrowedWidth = mixer.ActualWidth;
            await Toggle("inspector"); await Toggle("library"); await Settle();
            Check("narrow view closing checkboxes restores all 500 viewport pixels", Math.Abs(mixer.ActualWidth - narrowedWidth - 500) < 1 && Math.Abs(Width() - width) < 1 && window.ActualWidth == 1280);
            Check("expanded viewport clamps previous right scroll offset", mixer.HorizontalOffset <= mixer.ScrollableWidth + 1);
            Capture(window, Path.Combine(directory, "05-narrow-panels-closed.png"));
        }
        finally { view.Dispose(); window.Close(); File.WriteAllText(Path.Combine(directory, "layout-report.json"), JsonSerializer.Serialize(new { passed = failures == 0, count = checks.Count, failures, checks, boundary = "Real WPF controls; synthetic routed drag with stable window coordinates; isolated host, no hardware" }, new JsonSerializerOptions { WriteIndented = true })); }
        if (failures > 0) throw new InvalidOperationException(failures + " rack layout checks failed");
    }
    private static async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(12); }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root) { yield return root; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var item in Walk(VisualTreeHelper.GetChild(root, i))) yield return item; }
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => Walk(root).OfType<T>().First(x => AutomationProperties.GetAutomationId(x) == id);
    private static void Capture(Window window, string path) { var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); png.Save(stream); }
}
