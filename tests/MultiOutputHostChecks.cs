using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LightDeck.App;
using LightDeck.Core;

internal static partial class UiPluginChecks
{
    static async Task CheckMultiOutput(string clockName, string mirrorName)
    {
        var data = Path.Combine(Evidence, "data");
        Window = new MainWindow(data) { ShowActivated = false, ShowInTaskbar = false };
        Window.Show();
        Find<Button>(Window, "rack-edit-mode")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var devices = await Host.EnumerateDevicesAsync("wasapi");
        RackUiDevice Output(string name) => devices.Single(d => d.Available && d.Name == name && d.InputChannels == 0 && d.OutputChannels >= 2);
        var clock = Output(clockName);
        var second = Output(mirrorName);
        var rack = Host.Project.Copy();
        var port = new RackIoPort { Name = "双输出", Channels = 2 };
        rack.OutputPorts.Add(port);
        var local = CopyLocal();
        Check(local.IoMode == "single", "old settings default to single device");
        local.IoMode = "multi";
        local.DeviceApi = "wasapi";
        local.OutputDeviceId = clock.Id;
        local.OutputDeviceName = clock.Name;
        local.SampleRate = clock.PreferredSampleRate;
        local.SelectedEndpointIds = [clock.Id, second.Id];
        local.OutputPorts[port.Id] = new() { DeviceId = clock.Id, DeviceName = clock.Name, Channels = [0, 1] };
        local.OutputMirrors[port.Id] = [new() { DeviceId = second.Id, DeviceName = second.Name, Channels = [0, 1] }];
        await Host.ApplyIoAsync(rack, local);
        Check(Host.Project.Tracks.All(t => t.Kind == RackTrackKind.Output) && Host.Status.IsRunning, "output-only graph started by production host");
        string Label() => JsonSerializer.SerializeToElement(Call(Window, "ReadTabletRackState")).GetProperty("Tracks")[0].GetProperty("HardwareOutputLabel").GetString()!;
        Check(Label().Contains(clockName) && Label().Contains(mirrorName), "tablet output label includes both physical devices");
        var id = (string)Call(Window, "TabletRackStructureIdentity");
        var partial = CopyLocal();
        partial.OutputMirrors[port.Id][0] = partial.OutputMirrors[port.Id][0] with { Channels = [-1, 1] };
        await Host.PreviewHardwareRoutesAsync(Host.Project.Copy(), partial);
        Check(Host.Local.OutputMirrors[port.Id][0].Channels[0] == 0, "preview leaves saved side intact");
        await Host.PreviewHardwareRoutesAsync(Host.Project.Copy(), CopyLocal());
        await Host.ApplyIoAsync(Host.Project.Copy(), partial);
        Check(id != (string)Call(Window, "TabletRackStructureIdentity") && Label().Contains("— / 2"), "tablet refreshes after partial-side commit");
        await Task.Delay(600);
        var status = await (Task<JsonElement>)Call(Window.RackService, "CommandAsync", "status", null, CancellationToken.None);
        var endpoints = status.GetProperty("endpoints").EnumerateArray().ToArray();
        Check(endpoints.Length == 2 && endpoints.All(e => e.GetProperty("callbackCount").GetInt64() > 0 && !e.GetProperty("muted").GetBoolean()), "both actual render streams advanced through preview, restore and save");
        Check(status.GetProperty("outputSignal").GetProperty("peakSinceReset").GetDouble() == 0, "both outputs remained digitally silent");
        var saved = new AppSettingsStore(Path.Combine(data, "app-settings.json")).Load().RackLocal;
        Check(saved.OutputMirrors[port.Id][0].Channels.SequenceEqual(new[]{-1, 1}) && saved.OutputDeviceId == clock.Id, "saved settings restore same master clock and mirror side");
        await Host.SetRunningAsync(false);
    }
}
