using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LightDeck.Core;

namespace LightDeck.App;

public static partial class RackWorkspaceAudit
{
    public static async Task RunDesignAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>(); var geometry = new List<object>(); Exception? failure = null;
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed }); File.AppendAllText(Path.Combine(directory, "design-progress.txt"), name + "=" + passed + "\n");
            if (!passed) throw new InvalidOperationException(name);
        }
        var host = new FixtureHost();
        var mic = RackTrack.Create(RackTrackKind.Mono, "主持 mic"); mic.Monitor = true; mic.GainDb = -6; mic.Pan = -.5;
        var guest = RackTrack.Create(RackTrackKind.Mono, "嘉宾 mic"); guest.Monitor = true; guest.GainDb = -9;
        var music = RackTrack.Create(RackTrackKind.Stereo, "伴奏输入"); music.GainDb = -12; music.Pan = .5;
        var bus = RackTrack.Create(RackTrackKind.Bus, "人声 BUS"); var room = RackTrack.Create(RackTrackKind.FX, "Room"); var delay = RackTrack.Create(RackTrackKind.FX, "Delay");
        var foh = RackTrack.Create(RackTrackKind.Output, "FOH"); var ob = RackTrack.Create(RackTrackKind.Output, "OB");
        mic.Color = guest.Color = music.Color = "#8A6427"; bus.Color = "#167F89"; room.Color = delay.Color = "#74428C"; foh.Color = ob.Color = "#93495B";
        var mic1 = new RackIoPort { Name = "Mic1", Channels = 1 }; var mic2 = new RackIoPort { Name = "Mic2", Channels = 1 };
        var out12 = new RackIoPort { Name = "Out1/2", Channels = 2 }; var out34 = new RackIoPort { Name = "Out3/4", Channels = 2 };
        host.Project.InputPorts = [mic1, mic2]; host.Project.OutputPorts = [out12, out34];
        mic.InputPortId = mic1.Id; guest.InputPortId = mic2.Id; foh.HardwareOutputPortId = out12.Id; ob.HardwareOutputPortId = out34.Id;
        host.Local.InputPorts[mic1.Id] = new() { DeviceId = "fixture-input", DeviceName = "隔离声卡输入", Channels = [0] };
        host.Local.InputPorts[mic2.Id] = new() { DeviceId = "fixture-input", DeviceName = "隔离声卡输入", Channels = [1] };
        host.Local.OutputPorts[out12.Id] = new() { DeviceId = "fixture-output", DeviceName = "隔离声卡输出", Channels = [0, 1] };
        host.Local.OutputPorts[out34.Id] = new() { DeviceId = "fixture-output", DeviceName = "隔离声卡输出", Channels = [2, 3] };
        host.Local.InputDeviceName = host.Local.OutputDeviceName = "隔离声卡（模拟数据）"; host.Local.RoutePlayer = false;
        mic.OutputTrackId = guest.OutputTrackId = bus.Id; music.OutputTrackId = bus.OutputTrackId = room.OutputTrackId = delay.OutputTrackId = foh.Id;
        mic.Sends.Add(new() { TargetTrackId = room.Id, GainDb = -12 }); mic.Sends.Add(new() { TargetTrackId = ob.Id, GainDb = -6, PreFader = true });
        guest.Sends.Add(new() { TargetTrackId = delay.Id, GainDb = -15 });
        mic.Inserts.Add(new() { Identity = host.Plugins[0] }); guest.Inserts.Add(new() { Identity = host.Plugins[1] }); room.Inserts.Add(new() { Identity = host.Plugins[2] }); delay.Inserts.Add(new() { Identity = host.Plugins[2] with { Name = "Delay" } });
        host.Project.Tracks.Clear(); host.Project.Tracks.AddRange([mic, guest, music, bus, room, delay, foh, ob]); host.Project.SelectedTrackId = mic.Id;
        host.Local.CompactView = false; host.Local.InsertHeight = 100; host.Local.SendHeight = 120; host.Local.OutputHeight = 150;
        host.Local.ShowRackInsert = host.Local.ShowRackSend = host.Local.ShowRackOutput = host.Local.ShowRackInspector = host.Local.ShowRackLibrary = true;
        var theme = DeckTheme.Find("graphite"); var window = new Window { Width = 1920, Height = 1080, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0, ShowInTaskbar = false, Title = "音频机架设计隔离检查" };
        theme.Apply(window); var view = new RackWorkspaceControl(window, theme, host) { Margin = new Thickness(10) }; window.Content = view; window.Show(); await Settle();
        var send = mic.Sends[0].Id;
        RackTrack Current(Guid id) => host.Project.FindTrack(id)!;
        Border Panel(string key) => Find<Border>(view, "rack-panel-" + key);
        async Task Toggle(string key) { Find<CheckBox>(view, "rack-show-" + key).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Settle(); }
        async Task Press(string id) { Click(Find<Button>(view, id)); await Settle(); }
        bool Close(double a, double b) => Math.Abs(a - b) < .000001;
        MouseWheelEventArgs Wheel(UIElement target, int delta = 120)
        {
            var e = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
            target.RaiseEvent(e); return e;
        }
        void KeyPress(UIElement target, Key key) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), Environment.TickCount, key) { RoutedEvent = UIElement.PreviewKeyDownEvent });
        async Task Reopen()
        {
            view.Dispose(); view = new RackWorkspaceControl(window, theme, host) { Margin = new Thickness(10) }; window.Content = view; await Settle();
        }
        try
        {
            Check("eight representative tracks and independent module choices render", host.Project.Tracks.Count == 8 && new[] { "insert", "send", "output", "inspector", "library" }.All(k => Find<CheckBox>(view, "rack-show-" + k).IsChecked == true));
            Check("both side panels visible without overlap", Panel("inspector").IsVisible && Panel("library").IsVisible && Panel("inspector").TranslatePoint(new Point(Panel("inspector").ActualWidth, 0), view).X <= Panel("library").TranslatePoint(new Point(), view).X + 1);
            var mixer = Find<ScrollViewer>(view, "rack-mixer"); var groups = ((Grid)mixer.Content).Children.OfType<DockPanel>().ToArray();
            Check("four groups have three full-height separators", groups.Length == 4 && groups.Take(3).All(g => g.Children.OfType<Border>().Any(b => b.Width == 3 && b.ActualHeight > g.ActualHeight * .8)));
            foreach (var kind in new[] { "insert", "send", "output" })
            {
                var area = Find<ScrollViewer>(view, "rack-" + kind + "-area-" + mic.Id);
                if (kind != "insert")
                {
                    var handle = Find<Thumb>(view, "rack-resize-" + kind + "-" + mic.Id);
                    Check(kind + " resize handle is above content", handle.TranslatePoint(new Point(0, handle.ActualHeight), view).Y <= area.TranslatePoint(new Point(), view).Y + 1);
                }
                else Check("insert top is fixed without resize handle", !Elements(view).OfType<Thumb>().Any(t => AutomationProperties.GetAutomationId(t) == "rack-resize-insert-" + mic.Id));
                var before = JsonSerializer.Serialize(host.Project); await Toggle(kind);
                Check(kind + " visibility hides content without changing audio graph", !area.IsVisible && JsonSerializer.Serialize(host.Project) == before && Find<CheckBox>(view, "rack-show-" + kind).IsChecked == false);
                await Toggle(kind); Check(kind + " module restores same live content", area.IsVisible);
            }
            await Toggle("inspector"); Check("hide inspector keeps library", !Panel("inspector").IsVisible && Panel("library").IsVisible);
            await Toggle("library"); Check("both panels can be hidden", !Panel("inspector").IsVisible && !Panel("library").IsVisible);
            await Toggle("inspector"); Check("inspector alone can be restored", Panel("inspector").IsVisible && !Panel("library").IsVisible);
            var savedPath = Path.Combine(directory, "isolated-local-settings.json"); File.WriteAllText(savedPath, JsonSerializer.Serialize(host.Local));
            await host.ApplyLocalAsync(JsonSerializer.Deserialize<RackLocalSettings>(File.ReadAllText(savedPath))!); await Reopen();
            Check("saved local panel choices survive view reopen", Panel("inspector").IsVisible && !Panel("library").IsVisible && Find<CheckBox>(view, "rack-show-library").IsChecked == false);
            await Toggle("library"); await Toggle("library"); await Toggle("library"); Check("repeated toggles restore both panels", Panel("inspector").IsVisible && Panel("library").IsVisible);
            await Press("rack-close-inspector"); Check("inspector close synchronizes its independent choice", !Panel("inspector").IsVisible && Find<CheckBox>(view, "rack-show-inspector").IsChecked == false && Panel("library").IsVisible); await Toggle("inspector");
            foreach (var category in new[] { (Index: 1, Name: "参数均衡器"), (Index: 2, Name: "压缩器"), (Index: 3, Name: "混响") })
            {
                await Press("rack-library-category-" + category.Index); var list = Find<ListBox>(view, "rack-library-list");
                Check("library category " + category.Index + " shows only " + category.Name, list.Items.Count == 1 && Elements(list).OfType<TextBlock>().Any(t => t.Text == category.Name));
            }
            await Press("rack-library-category-0"); var allPlugins = Find<ListBox>(view, "rack-library-list");
            Check("all library category restores three original plugin names", allPlugins.Items.Count == 3 && host.Plugins.All(p => Elements(allPlugins).OfType<TextBlock>().Any(t => t.Text == p.Name)));
            await Press("rack-solo-" + mic.Id); await Press("rack-solo-" + guest.Id);
            Check("solo buttons permit multiple selected tracks", Current(mic.Id).Solo && Current(guest.Id).Solo && Current(room.Id).SoloSafe);
            Check("selected inspector solo state matches strip", AutomationProperties.GetName(Find<Button>(view, "rack-solo-inspector-" + mic.Id)).Contains("True") && Find<Button>(view, "rack-solo-" + mic.Id).Background.ToString() == Find<Button>(view, "rack-solo-inspector-" + mic.Id).Background.ToString());
            var preservedGain = Current(mic.Id).GainDb; await Press("rack-mute-inspector-" + mic.Id);
            Check("inspector mute synchronizes strip without moving fader", Current(mic.Id).Mute && Current(mic.Id).GainDb == preservedGain && Find<Button>(view, "rack-mute-" + mic.Id).Background.ToString() == Find<Button>(view, "rack-mute-inspector-" + mic.Id).Background.ToString());
            await Press("rack-mute-" + mic.Id); await Press("rack-mute-" + foh.Id); Check("muting one output leaves other output unchanged", Current(foh.Id).Mute && !Current(ob.Id).Mute); await Press("rack-mute-" + foh.Id);
            await Press("rack-clear-solo"); Check("clear solo clears all selected solos and preserves protection", host.Project.Tracks.All(t => !t.Solo) && Current(room.Id).SoloSafe);

            foreach (var item in new[] { ("rack-gain-" + mic.Id, .1), ("rack-pan-" + mic.Id, .01), ("rack-strip-send-gain-" + send, .1), ("rack-main-output-gain-" + mic.Id, .1) })
            {
                var slider = Find<Slider>(view, item.Item1); var before = slider.Value; var scroll = Find<ScrollViewer>(view, "rack-mixer").VerticalOffset;
                var e = Wheel(((Track)slider.Template.FindName("PART_Track", slider)).Thumb); await Settle();
                Check(item.Item1 + " wheel uses actual value step and consumes scrolling", e.Handled && Close(slider.Value, before + item.Item2) && Close(Find<ScrollViewer>(view, "rack-mixer").VerticalOffset, scroll));
            }
            Check("wheel pan reaches normalized host value", Close(Current(mic.Id).Pan, -.49));
            var pan = Find<Slider>(view, "rack-pan-" + mic.Id); pan.Value = 0; await Settle();
            Slider.IncreaseSmall.Execute(null, pan); await Settle(); Check("pan keyboard command preserves one normalized step", Close(Current(mic.Id).Pan, .01));
            var panThumb = ((Track)pan.Template.FindName("PART_Track", pan)).Thumb;
            panThumb.RaiseEvent(new DragDeltaEventArgs((pan.ActualWidth - panThumb.ActualWidth) / 4, 0) { RoutedEvent = Thumb.DragDeltaEvent }); await Settle();
            Check("pan thumb drag retains normalized coordinates", Close(pan.Value, .51) && Close(Current(mic.Id).Pan, .51));
            pan.Value = 1; await Settle(); Check("pan upper limit consumes wheel without overflow", Wheel(pan).Handled && pan.Value == 1); pan.Value = -1; await Settle(); Check("pan lower limit consumes wheel without overflow", Wheel(pan, -120).Handled && pan.Value == -1);
            pan.Value = 0; await Settle(); var panNumber = Find<TextBlock>(view, "rack-pan-number-" + mic.Id + "-display"); Wheel(panNumber); await Settle(); Check("pan numeric wheel changes one display unit", Close(Current(mic.Id).Pan, .01));
            var gainNumber = Find<TextBlock>(view, "rack-gain-value-" + mic.Id); var gainBefore = Current(mic.Id).GainDb; Wheel(gainNumber); await Settle(); Check("gain numeric wheel changes point one dB", Close(Current(mic.Id).GainDb, gainBefore + .1));
            Double(gainNumber, UIElement.MouseLeftButtonDownEvent); await Settle(); var input = Find<TextBox>(view, "rack-gain-number-" + mic.Id); var committed = Current(mic.Id).GainDb;
            Check("double click opens numeric editor", input.IsVisible); input.Text = "-12.3"; Wheel(input); await Settle(); Check("wheel in editor changes draft only", input.Text == "-12.2" && Close(Current(mic.Id).GainDb, committed)); KeyPress(input, Key.Escape); await Settle(); Check("Escape discards wheel draft", !input.IsVisible && Close(Current(mic.Id).GainDb, committed));
            Double(gainNumber, UIElement.MouseLeftButtonDownEvent); await Settle(); input.Text = "-12.3"; Wheel(input); KeyPress(input, Key.Enter); await Settle(); Check("Enter commits wheel draft", !input.IsVisible && Close(Current(mic.Id).GainDb, -12.2));
            Double(gainNumber, UIElement.MouseLeftButtonDownEvent); await Settle(); input.Text = "bad"; Wheel(input); KeyPress(input, Key.Escape); await Settle(); Check("invalid numeric wheel draft never changes live gain", Close(Current(mic.Id).GainDb, -12.2));

            pan.Value = -.5; Find<Slider>(view, "rack-pan-" + guest.Id).Value = 0; Find<Slider>(view, "rack-pan-" + music.Id).Value = .5; await Settle(); await Press("rack-solo-" + mic.Id); await Press("rack-mute-" + music.Id);
            foreach (var themeId in new[] { "graphite", "amber", "silver", "warm", "frost", "metal", "soft", "contrast" })
            {
                theme = DeckTheme.Find(themeId); theme.Apply(window); await Reopen();
                var sizes = themeId is "graphite" or "amber" or "silver" ? new[] { (Width: 1920d, Height: 1080d), (Width: 1280d, Height: 800d) } : new[] { (Width: 1280d, Height: 800d) };
                foreach (var size in sizes)
                {
                    window.Width = size.Width; window.Height = size.Height; await Settle(); mixer = Find<ScrollViewer>(view, "rack-mixer");
                    var inspector = Panel("inspector"); var library = Panel("library");
                    Check(themeId + " " + size.Width + " panels keep independent usable width", inspector.IsVisible && library.IsVisible && inspector.ActualWidth >= 240 && library.ActualWidth >= 240 && inspector.TranslatePoint(new Point(inspector.ActualWidth, 0), view).X <= library.TranslatePoint(new Point(), view).X + 1);
                    Check(themeId + " " + size.Width + " controls and pan remain rendered", Find<Slider>(view, "rack-pan-" + mic.Id) is { IsVisible: true } slider && slider.GetType().Name == "RackPanSlider" && Find<Button>(view, "rack-solo-inspector-" + mic.Id).IsVisible);
                    if (themeId == "graphite" && size.Width == 1280)
                    {
                        Check(themeId + " narrow window keeps complete tracks through horizontal scrolling", mixer.ScrollableWidth > 0 && host.Project.Tracks.All(t => Find<Border>(view, "rack-strip-" + t.Id).ActualWidth >= 131.9));
                        mixer.ScrollToRightEnd(); await Settle();
                        Check(themeId + " narrow scroll reaches final output", Find<Border>(view, "rack-strip-" + ob.Id).TranslatePoint(new Point(Find<Border>(view, "rack-strip-" + ob.Id).ActualWidth, 0), mixer).X <= mixer.ViewportWidth + 2);
                        var latency = Find<TextBlock>(view, "rack-plugin-latency-" + ob.Id);
                        var latencyTop = latency.TranslatePoint(new Point(), mixer).Y; var latencyBottom = latency.TranslatePoint(new Point(0, latency.ActualHeight), mixer).Y;
                        Check($"narrow last output latency footer fits viewport (top={latencyTop:0.##}, bottom={latencyBottom:0.##}, viewport={mixer.ViewportHeight:0.##})", latency.IsVisible && latencyTop >= -1 && latencyBottom <= mixer.ViewportHeight + 2);
                        mixer.ScrollToLeftEnd(); await Settle();
                    }
                    geometry.Add(new { themeId, requested = size, window.ActualWidth, window.ActualHeight, dpi = VisualTreeHelper.GetDpi(window), mixer.ViewportWidth, mixer.ScrollableWidth, inspectorWidth = inspector.ActualWidth, libraryWidth = library.ActualWidth });
                    Capture(window, Path.Combine(directory, $"rack-design-{themeId}-{size.Width:0}.png"));
                }
            }
            Check("design interactions never start stop fixture audio", host.StartStopCalls == 0 && host.Running);
        }
        catch (Exception ex) { failure = ex; Capture(window, Path.Combine(directory, "rack-design-failed.png")); }
        finally
        {
            view.Dispose(); window.Close();
            File.WriteAllText(Path.Combine(directory, "rack-design-report.json"), JsonSerializer.Serialize(new { passed = failure is null, count = checks.Count, checks, geometry, error = failure?.ToString(), boundary = "Actual isolated WPF render and synthetic routed control events; fixture host with isolated JSON save/reopen. No physical mouse/keyboard, production process, audio hardware or DSP output validation." }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (failure is not null) throw failure;
    }
}
