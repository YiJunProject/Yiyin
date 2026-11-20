using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using LightDeck.Core;

namespace LightDeck.App;

public static partial class RackWorkspaceAudit
{
    private static Window OpenIo(Window owner, DeckTheme theme, IRackUiHost host, IReadOnlyList<RackUiDevice> devices) =>
        (Window)Activator.CreateInstance(typeof(RackWorkspaceControl).Assembly.GetType("LightDeck.App.RackDeviceDialog", true)!, owner, theme, host, devices)!;

    public static async Task RunIoAsync(string directory)
    {
        Directory.CreateDirectory(directory); var checks = new List<object>();
        void Check(string name, bool value) { checks.Add(new { name, passed = value }); File.AppendAllText(Path.Combine(directory, "progress.txt"), name + "=" + value + "\n"); if (!value) throw new InvalidOperationException(name); }
        var host = new FixtureHost { Running = false }; var window = new Window { Title = "易音 I/O 隔离检查", Width = 1280, Height = 900, ShowInTaskbar = false };
        DeckTheme.Find("graphite").Apply(window); window.Show(); await Settle();
        var mono = host.Project.Tracks.First(t => t.Kind == RackTrackKind.Mono); var stereo = host.Project.Tracks.First(t => t.Kind == RackTrackKind.Stereo); var output = host.Project.Tracks.First(t => t.Kind == RackTrackKind.Output);
        host.Local.InputChannels[mono.Id] = [0]; host.Local.InputChannels[stereo.Id] = [2, 3]; host.Local.OutputChannels[output.Id] = [0, 1];
        var devices = await host.EnumerateDevicesAsync();
        RackProject Draft(Window dialog) => (RackProject)dialog.GetType().GetField("_project", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
        RackLocalSettings Local(Window dialog) => (RackLocalSettings)dialog.GetType().GetField("_local", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
        void ClickIo(Window dialog, string id) { Click(Find<Button>(dialog, id)); dialog.UpdateLayout(); }
        void Open(string theme, Action<Window> action) => Modal(window, () => OpenIo(window, DeckTheme.Find(theme), host, devices).ShowDialog(), action);
        void AsyncOpen(string theme, Func<Window, Task> action) => ModalTask(window, () => OpenIo(window, DeckTheme.Find(theme), host, devices).ShowDialog(), action);
        try
        {
            var before = JsonSerializer.Serialize(host.Project); var beforeLocal = JsonSerializer.Serialize(host.Local);
            Open("graphite", dialog =>
            {
                Check("standalone IO has no player option", !Elements(dialog).Any(e => AutomationProperties.GetAutomationId(e) == "rack-route-player"));
                var draft = Draft(dialog); var local = Local(dialog);
                Check("legacy track channel mappings migrate into draft named IO ports", draft.InputPorts.Count == 2 && draft.OutputPorts.Count == 1 && draft.FindTrack(stereo.Id)!.InputPortId is not null && local.InputChannels.Count == 0);
                Check("same card name with different physical group remains separate", Find<ComboBox>(dialog, "rack-device-card").Items.Count == 3);
                Check("stereo mapping shows L and R on distinct hardware columns", Find<Button>(dialog, $"rack-io-cell-{draft.FindTrack(stereo.Id)!.InputPortId}-2").Content?.ToString() == "L" && Find<Button>(dialog, $"rack-io-cell-{draft.FindTrack(stereo.Id)!.InputPortId}-3").Content?.ToString() == "R");
                ClickIo(dialog, "rack-io-input-add-mono");
                Check("adding IO remains a draft and does not create live tracks", host.Project.Tracks.Count == draft.Tracks.Count && draft.InputPorts.Count == 3);
                Capture(dialog, Path.Combine(directory, "io-input-dark.png")); dialog.Close();
            });
            Check("cancel leaves legacy project and hardware mappings exactly unchanged", before == JsonSerializer.Serialize(host.Project) && beforeLocal == JsonSerializer.Serialize(host.Local));
            AsyncOpen("silver", async dialog =>
            {
                ClickIo(dialog, "rack-io-input-add-mono"); var mic = Draft(dialog).InputPorts.Last(); Find<TextBox>(dialog, "rack-io-name-" + mic.Id).Text = "主持 mic";
                ClickIo(dialog, $"rack-io-cell-{mic.Id}-1");
                ClickIo(dialog, "rack-io-input-add-stereo"); var port = Draft(dialog).InputPorts.Last(); Find<TextBox>(dialog, "rack-io-name-" + port.Id).Text = "music";
                ClickIo(dialog, $"rack-io-cell-{port.Id}-4"); ClickIo(dialog, "rack-device-save"); await Settle();
                Check("partial stereo mapping is not saved and editor stays open", dialog.IsVisible && host.Project.InputPorts.Count == 0);
                ClickIo(dialog, $"rack-io-cell-{port.Id}-5");
                Check("stereo hardware selection remains distinct and zero based", Local(dialog).InputPorts[port.Id].Channels.SequenceEqual(new[] { 4, 5 }));
                ClickIo(dialog, "rack-io-output-add-stereo"); var destination = Draft(dialog).OutputPorts.Last(); Find<TextBox>(dialog, "rack-io-name-" + destination.Id).Text = "ob";
                ClickIo(dialog, $"rack-io-cell-{destination.Id}-8"); ClickIo(dialog, $"rack-io-cell-{destination.Id}-9");
                Check("one card output matrix selects separate endpoint with local channel numbers", Local(dialog).OutputPorts[destination.Id].DeviceId == "fixture-output-alt" && Local(dialog).OutputPorts[destination.Id].Channels.SequenceEqual(new[] { 0, 1 }));
                Capture(dialog, Path.Combine(directory, "io-output-silver.png")); ClickIo(dialog, "rack-device-save"); await Settle();
            });
            Check("IO apply commits logical names and separate physical mappings", host.Project.InputPorts.Any(p => p.Name == "music") && host.Project.OutputPorts.Any(p => p.Name == "ob") && host.Local.OutputPorts.Count == 2 && host.Local.OutputDeviceId == "fixture-output");
            var createdMic = host.Project.Tracks.Single(t => t.Name == "主持 mic"); var createdMusic = host.Project.Tracks.Single(t => t.Name == "music"); var createdOutput = host.Project.Tracks.Single(t => t.Name == "ob");
            Check("new mono and stereo inputs bind matching named IO and first output", createdMic.Kind == RackTrackKind.Mono && createdMic.Channels == 1 && createdMusic.Kind == RackTrackKind.Stereo && createdMusic.Channels == 2 && createdMic.InputPortId == host.Project.InputPorts.Single(p => p.Name == "主持 mic").Id && createdMusic.InputPortId == host.Project.InputPorts.Single(p => p.Name == "music").Id && createdMic.OutputTrackId == output.Id && createdMusic.OutputTrackId == output.Id);
            Check("new output binds its own hardware endpoint and port", createdOutput.Kind == RackTrackKind.Output && createdOutput.HardwareOutputPortId == host.Project.OutputPorts.Single(p => p.Name == "ob").Id && host.Local.OutputPorts[createdOutput.HardwareOutputPortId!.Value].DeviceId == "fixture-output-alt");
            Check("existing routes and tracks survive automatic IO creation", host.Project.FindTrack(mono.Id)!.OutputTrackId == mono.OutputTrackId && host.Project.FindTrack(stereo.Id)!.OutputTrackId == stereo.OutputTrackId);
            var savedTrackIds = host.Project.Tracks.Select(t => t.Id).ToArray();
            AsyncOpen("graphite", async dialog => { ClickIo(dialog, "rack-device-save"); await Settle(); });
            Check("repeated apply does not duplicate tracks", host.Project.Tracks.Select(t => t.Id).SequenceEqual(savedTrackIds));
            Open("graphite", dialog =>
            {
                var port = Draft(dialog).InputPorts.Single(p => p.Name == "music");
                Check("reopen restores named input and channel assignments", Find<TextBox>(dialog, "rack-io-name-" + port.Id).Text == "music" && Local(dialog).InputPorts[port.Id].Channels.SequenceEqual(new[] { 4, 5 }));
                Find<ComboBox>(dialog, "rack-device-card").SelectedIndex = 2; ClickIo(dialog, "rack-device-save");
                Check("changing physical card cannot silently reassign prior port mappings", dialog.IsVisible && host.Local.OutputDeviceId == "fixture-output"); dialog.Close();
            });
            host.FailLocal = true;
            AsyncOpen("graphite", async dialog =>
            {
                var applied = JsonSerializer.Serialize(host.Project); ClickIo(dialog, "rack-io-input-add-mono"); var port = Draft(dialog).InputPorts.Last(); Find<TextBox>(dialog, "rack-io-name-" + port.Id).Text = "重试输入";
                ClickIo(dialog, "rack-device-save"); await Settle();
                Check("failed IO save keeps draft visible and previous applied project", dialog.IsVisible && applied == JsonSerializer.Serialize(host.Project) && Draft(dialog).Tracks.Count == savedTrackIds.Length);
                host.FailLocal = false; ClickIo(dialog, "rack-device-save"); await Settle();
            });
            Check("retry after failure creates only one matching track", host.Project.Tracks.Count(t => t.Name == "重试输入") == 1);
            Open("graphite", dialog =>
            {
                var applied = JsonSerializer.Serialize(host.Project); var draft = Draft(dialog); draft.OutputPorts.Clear(); Local(dialog).OutputPorts.Clear();
                foreach (var track in draft.Tracks) track.HardwareOutputPortId = null;
                ClickIo(dialog, "rack-device-save");
                Check("empty hardware output stays an explicit configuration error", dialog.IsVisible && applied == JsonSerializer.Serialize(host.Project) && Elements(dialog).OfType<TextBlock>().Any(t => t.Text.Contains("至少添加并映射一个硬件输出"))); dialog.Close();
            });
            await CheckMultiDeviceMatrixAsync(window, directory, Check);
            var portable = new RackDocument { Rack = host.Project };
            var json = JsonSerializer.Serialize(portable); Check("portable rack IO contains names but no hardware endpoint identity", json.Contains("music") && !json.Contains("fixture-output") && !json.Contains("fixture-input"));
            var migrated = host.Project.Copy(); var copiedLocal = CopyLocal(host.Local); var count = migrated.InputPorts.Count; RackIoMigration.Upgrade(migrated, copiedLocal); Check("named IO migration is idempotent", migrated.InputPorts.Count == count && migrated.Tracks.Single(t => t.Id == stereo.Id).InputPortId == host.Project.FindTrack(stereo.Id)!.InputPortId);
            var empty = new RackProject(); var emptyDraft = empty.Copy(); var newInput = new RackIoPort { Name = "首次输入", Channels = 1 }; var newOutput = new RackIoPort { Name = "首次输出", Channels = 1 }; emptyDraft.InputPorts.Add(newInput); emptyDraft.OutputPorts.Add(newOutput);
            var first = RackIoTrackCreation.Prepare(empty, emptyDraft);
            Check("first IO apply creates mono output before routing the first input", first.Tracks.Count == 2 && first.Tracks.Single(t => t.Kind == RackTrackKind.Output).Channels == 1 && first.Tracks.Single(t => t.Kind == RackTrackKind.Mono).OutputTrackId == first.Tracks.Single(t => t.Kind == RackTrackKind.Output).Id);
            Check("new tracks join the active display view", first.DisplayViews.Single(v => v.Id == first.ActiveDisplayViewId).TrackIds.Count == 2);
            var retained = new RackIoPort { Name = "旧未绑定输入", Channels = 1 }; first.InputPorts.Add(retained); var noOp = RackIoTrackCreation.Prepare(first, first.Copy());
            Check("old unused IO does not silently recreate manually removed tracks", noOp.Tracks.Count == first.Tracks.Count);
            var concurrent = first.Copy(); var ioDraft = first.Copy(); concurrent.Tracks[0].Name = "并行改名"; concurrent.Tracks[1].OutputTrackId = null; concurrent.Tracks[1].GainDb = -9;
            var merged = RackIoTrackCreation.Prepare(concurrent, ioDraft);
            Check("IO merge preserves newer track edits and manual routes", merged.Tracks[0].Name == "并行改名" && merged.Tracks[1].OutputTrackId is null && merged.Tracks[1].GainDb == -9);
            var removal = merged.Copy(); removal.InputPorts.Clear(); foreach (var track in removal.Tracks) track.InputPortId = null;
            var removed = RackIoTrackCreation.Prepare(merged, removal);
            Check("removing IO preserves its track and clears only the binding", removed.Tracks.Count == merged.Tracks.Count && removed.Tracks[1].InputPortId is null && removed.Tracks[1].GainDb == -9);
            var full = new RackProject { Tracks = Enumerable.Range(0, RackValidation.MaximumTracks).Select(i => RackTrack.Create(RackTrackKind.Bus, "Bus " + i)).ToList() }; full.Validate(); var fullDraft = full.Copy(); fullDraft.InputPorts.Add(new() { Name = "超过上限", Channels = 1 });
            var unchanged = JsonSerializer.Serialize(full); var draftUnchanged = JsonSerializer.Serialize(fullDraft); var rejected = false;
            try { RackIoTrackCreation.Prepare(full, fullDraft); } catch (ArgumentException) { rejected = true; }
            Check("track limit rejects candidate without mutating applied project or draft", rejected && unchanged == JsonSerializer.Serialize(full) && draftUnchanged == JsonSerializer.Serialize(fullDraft));
        }
        finally
        {
            window.Close(); File.WriteAllText(Path.Combine(directory, "rack-io-ui-report.json"), JsonSerializer.Serialize(new { checks, layer = "Real WPF controls and themed rendering, fixture hardware/host; no physical device opened" }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private static async Task CheckMultiDeviceMatrixAsync(Window owner, string directory, Action<string, bool> check)
    {
        var speaker = new RackUiDevice("speaker-render", "USB 音响", "wasapi", 0, 2, [48000], 48000, "speaker", "USB 音响", OutputChannelNames: ["L", "R"]);
        var mic = new RackUiDevice("headset-capture", "耳麦麦克风", "wasapi", 1, 0, [44100], 44100, "phones", "USB 游戏耳机", InputChannelNames: ["麦克风"]);
        var phones = new RackUiDevice("headset-render", "耳机输出", "wasapi", 0, 2, [44100], 44100, "phones", "USB 游戏耳机", OutputChannelNames: ["L", "R"]);
        var monitor = new RackUiDevice("monitor-render", "显示器音频", "wasapi", 0, 2, [48000], 48000, "monitor", "显示器音频");
        var host = new FixtureHost { Running = false, IoFixtureDevices = [speaker, mic, phones, monitor] };
        var input = new RackIoPort { Name = "耳麦", Channels = 1 }; var output = new RackIoPort { Name = "主输出", Channels = 2 };
        host.Project.InputPorts.Add(input); host.Project.OutputPorts.Add(output);
        host.Project.Tracks.First(t => t.Kind == RackTrackKind.Mono).InputPortId = input.Id;
        host.Project.Tracks.First(t => t.Kind == RackTrackKind.Output).HardwareOutputPortId = output.Id;
        host.Local.InputDeviceId = ""; host.Local.OutputDeviceId = speaker.Id; host.Local.OutputDeviceName = speaker.Name;
        host.Local.OutputPorts[output.Id] = new() { DeviceId = speaker.Id, DeviceName = speaker.Name, Channels = [0, 1] };

        void Open(Func<Window, Task> answer, string theme = "graphite", bool narrow = false) => ModalTask(owner, () =>
        {
            var editor = OpenIo(owner, DeckTheme.Find(theme), host, host.IoFixtureDevices);
            if (narrow) editor.Width = editor.MinWidth;
            editor.ShowDialog();
        }, answer);
        void Cell(Window w, Guid port, int index) { Click(Find<Button>(w, $"rack-io-cell-{port}-{index}")); w.UpdateLayout(); }
        void Toggle(Window w, string id)
        {
            var box = Find<CheckBox>(w, "rack-device-group-" + id); box.IsChecked = box.IsChecked != true;
            box.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); w.UpdateLayout();
        }
        void Mode(Window w, string id)
        {
            var button = Find<RadioButton>(w, "rack-device-mode-" + id); button.IsChecked = true;
            button.RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent)); w.UpdateLayout();
        }
        Open(async w =>
        {
            var initial = Find<RadioButton>(w, "rack-device-mode-single"); initial.Focus();
            initial.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w)!, Environment.TickCount, Key.Left) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Settle(); var chosen = Find<RadioButton>(w, "rack-device-mode-multi");
            check("keyboard arrows select a mode with a visible themed ring and border", chosen.IsChecked == true &&
                chosen.Template.FindName("Dot", chosen) is FrameworkElement { Visibility: Visibility.Visible } &&
                chosen.Template.FindName("Outline", chosen) is Border outline && outline.BorderBrush.ToString() == DeckTheme.Brush(DeckTheme.Find("graphite").Accent).ToString());

            check("only checked output-only speaker is visible before checking headset", !Elements(w).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b).StartsWith($"rack-io-cell-{input.Id}")) && Find<ComboBox>(w, "rack-device-clock").Items.Count == 1);
            Toggle(w, "phones"); Cell(w, input.Id, 0); Cell(w, output.Id, 2); Cell(w, output.Id, 3); w.UpdateLayout();
            check("mic M and four simultaneous LR checkmarks appear in the two real WPF matrices",
                ((string)Find<Button>(w, $"rack-io-cell-{input.Id}-0").Content).EndsWith("M") && Enumerable.Range(0, 4).Select(i => Find<Button>(w, $"rack-io-cell-{output.Id}-{i}").Content).SequenceEqual(new[] { "✓  L", "✓  R", "✓  L", "✓  R" }));
            check("clock choices include only checked outputs; 44.1k helper leaves 48k speaker clock available", Find<ComboBox>(w, "rack-device-clock").Items.Count == 2 && Equals(Find<ComboBox>(w, "rack-device-rate").SelectedItem, 48000));
            Capture(w, Path.Combine(directory, "multi-device-matrix.png"));
            Click(Find<Button>(w, "rack-device-save")); await Settle();
        });
        check("multi save persists actual endpoint IDs and both output targets", host.Local.IoMode == "multi" && host.Local.SelectedEndpointIds.ToHashSet().SetEquals(new[] { speaker.Id, mic.Id, phones.Id }) && host.Local.OutputTargets(output.Id).Count() == 2);
        host.Running = true;
        Open(async w =>
        {
            Cell(w, output.Id, 2); await Settle();
            check("deselecting L live preserves headset R and both speaker channels", host.HardwarePreviews.Last().OutputMirrors[output.Id][0].Channels.SequenceEqual(new[] { -1, 1 }) && host.Local.OutputMirrors[output.Id][0].Channels.SequenceEqual(new[] { 0, 1 }));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.HoldNextPreview = gate; Cell(w, output.Id, 3); w.Close();
            check("close waits for an outstanding hardware write", w.IsVisible); gate.SetResult(); await Settle();
        });
        check("closing restores saved fanout after delayed preview completes", host.HardwarePreviews.Last().OutputMirrors[output.Id][0].Channels.SequenceEqual(new[] { 0, 1 }));
        Open(async w =>
        {
            Cell(w, output.Id, 3); host.FailLocal = true; Click(Find<Button>(w, "rack-device-save")); await Settle();
            check("save failure restores committed hardware routing and keeps editor open", w.IsVisible && host.HardwarePreviews.Last().OutputMirrors[output.Id][0].Channels.SequenceEqual(new[] { 0, 1 }));
            host.FailLocal = false; Click(Find<Button>(w, "rack-device-save")); await Settle();
        });
        check("retry saves only selected side and reopening retains mapping", host.Local.OutputMirrors[output.Id][0].Channels.SequenceEqual(new[] { 0, -1 }));
        Open(async w =>
        {
            check("saved partial mapping reopens on the same cells", Find<Button>(w, $"rack-io-cell-{output.Id}-2").Content.Equals("✓  L") && Find<Button>(w, $"rack-io-cell-{output.Id}-3").Content.Equals("·"));
            var calls = host.HardwarePreviews.Count; Toggle(w, "phones"); Cell(w, output.Id, 0); await Settle();
            check("changing the device combination suppresses mapping preview until applied", calls == host.HardwarePreviews.Count && Find<ComboBox>(w, "rack-device-clock").Items.Count == 1);
            w.Close();
        });
        host.IoFixtureDevices = [speaker, monitor]; host.Running = false;
        Open(async w =>
        {
            check("offline selected endpoints retain explicit checked identities", Find<CheckBox>(w, "rack-device-group-" + phones.Id).IsChecked == true && Find<CheckBox>(w, "rack-device-group-" + mic.Id).IsChecked == true);
            Click(Find<Button>(w, "rack-device-save")); await Settle();
        });
        check("saving while auxiliary is offline preserves both offline mappings", host.Local.OutputMirrors[output.Id][0].DeviceId == phones.Id && host.Local.InputPorts[input.Id].DeviceId == mic.Id);
        host.IoFixtureDevices = [speaker, mic, phones, monitor];
        Open(async w =>
        {
            Mode(w, "hybrid"); await Settle();
            var asio = host.AsioDevices.First();
            Toggle(w, asio.GroupId.Length > 0 ? asio.GroupId : "endpoint:" + asio.Id);
            check("hybrid clock selector offers just the checked ASIO driver", Find<ComboBox>(w, "rack-device-clock").Items.Count == 1 && ((RackUiDevice)Find<ComboBox>(w, "rack-device-clock").SelectedItem).Id == asio.Id);
            Click(Find<Button>(w, "rack-device-probe")); await Settle();
            check("explicit hybrid probe exposes real driver channels alongside USB endpoints", host.ProbeCalls == 1 && Elements(w).OfType<TextBlock>().Any(t => t.Text.Contains("Main Left")));
            Click(Find<Button>(w, "rack-device-save")); await Settle();
        });
        check("hybrid save commits ASIO clock with WASAPI headset and speaker", host.Local.IoMode == "hybrid" && host.Local.DeviceApi == "asio" && host.Local.SelectedEndpointIds.Contains(phones.Id) && host.Local.SelectedEndpointIds.Contains(host.Local.OutputDeviceId));
        Open(async w =>
        {
            await Settle();
            var button = Find<RadioButton>(w, "rack-device-mode-hybrid"); var clock = Find<ComboBox>(w, "rack-device-clock");
            check($"light-theme hybrid controls and clock stay reachable at minimum width ({w.ActualWidth:F0} DIP)", Math.Abs(w.ActualWidth - 920) <= 1 && button.IsChecked == true && clock.IsVisible && clock.ActualWidth >= 80 &&
                clock.TranslatePoint(new Point(clock.ActualWidth, 0), w).X < w.ActualWidth && Find<Button>(w, "rack-device-save").IsVisible);
            Capture(w, Path.Combine(directory, "hybrid-light-min-width.png"));
            Mode(w, "single"); await Settle();
            check("minimum width also exposes the single-card control and its themed selection", Find<ComboBox>(w, "rack-device-card").IsVisible &&
                Find<RadioButton>(w, "rack-device-mode-single") is { IsChecked: true });
            w.Close();
        }, "silver", narrow: true);
        await Task.CompletedTask;
    }

}
