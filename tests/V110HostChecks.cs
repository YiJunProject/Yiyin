using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LightDeck.App;
using LightDeck.Core;

internal static partial class UiPluginChecks
{
    static RackLocalSettings CopyLocal() => JsonSerializer.Deserialize<RackLocalSettings>(JsonSerializer.Serialize(Host.Local, ProjectStore.JsonOptions), ProjectStore.JsonOptions)!;
    static async Task RunHost(bool baseline)
    {
        Window = new MainWindow(Path.Combine(Evidence,"data")){Width=1600,Height=950,ShowActivated=false,ShowInTaskbar=false};
        Window.Show(); await Task.Delay(150);
        if(baseline)
        {
            var draft=CopyLocal();draft.ShowRackLibrary=false;
            try {await Host.ApplyLocalAsync(draft);Check(false,"old host must reproduce rejected display setting");}
            catch(InvalidOperationException ex){Check(ex.Message.Contains("演播模式"),"reproduced: performance display toggle rejected by old host");}
            return;
        }
        foreach(var key in new[]{"insert","send","output","inspector","library"})
        {
            var check=Find<CheckBox>(Window,"rack-show-"+key)!;
            check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await Task.Delay(140);
            Check(check.IsChecked==false,"performance mode can hide "+key);
            check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await Task.Delay(140);
            Check(check.IsChecked==true,"performance mode can restore "+key);
        }
        Find<Button>(Window,"rack-edit-mode")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(80);
        var draftIo=Host.Project.Copy();var input=new RackIoPort{Name="话筒",Channels=1};var output=new RackIoPort{Name="主输出",Channels=2};
        draftIo.InputPorts.Add(input);draftIo.OutputPorts.Add(output);
        await Host.ApplyIoAsync(draftIo,CopyLocal());await Task.Delay(120);
        var mic=Host.Project.Tracks.Single(t=>t.InputPortId==input.Id);var main=Host.Project.Tracks.Single(t=>t.HardwareOutputPortId==output.Id);
        Check(mic.Name==input.Name&&mic.OutputTrackId==main.Id&&!mic.Monitor,"actual I/O host creates bound tracks and leaves monitoring off");
        var ioCount=Host.Project.Tracks.Count;await Host.ApplyIoAsync(Host.Project.Copy(),CopyLocal());
        Check(Host.Project.Tracks.Count==ioCount,"reapplying I/O does not duplicate tracks");
        var p=Host.Project.Copy();var guest=RackTrack.Create(RackTrackKind.Mono,"嘉宾");guest.OutputTrackId=main.Id;
        var fx=RackTrack.Create(RackTrackKind.FX,"混响 FX");fx.OutputTrackId=main.Id;p.Tracks.AddRange([guest,fx]);p.FindTrack(mic.Id)!.Sends.Add(new(){TargetTrackId=fx.Id});
        await Host.ApplyProjectAsync(p);await Task.Delay(120);
        Find<Button>(Window,"rack-edit-mode")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(120);
        Find<Button>(Window,"rack-solo-"+mic.Id)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(220);
        Check(Host.Project.FindTrack(mic.Id)!.Solo,"actual Solo button updates real performance host");
        var graph=(JsonObject)Call(Window.RackService,"BuildGraph",Host.Project);
        bool Muted(Guid id)=>graph["nodes"]!.AsArray().Single(n=>n!["id"]!.GetValue<string>()==id.ToString())!["mute"]!.GetValue<bool>();
        Check(!Muted(mic.Id)&&Muted(guest.Id)&&!Muted(fx.Id)&&!Muted(main.Id),"actual engine graph mutes unrelated input and preserves FX/output path");
        Find<Button>(Window,"rack-mute-"+mic.Id)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(180);
        Check(Host.Project.FindTrack(mic.Id)!.Mute,"actual Mute button updates real performance host");
        Find<Button>(Window,"rack-clear-solo")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(180);
        Check(!Host.Project.Tracks.Any(t=>t.Solo)&&Host.Project.FindTrack(mic.Id)!.Mute,"clear Solo preserves explicit Mute");
        var track=Host.Project.FindTrack(mic.Id)!.Copy();track.Solo=true;await Host.UpdateTrackAsync(track);
        var saved=Path.Combine(Evidence,"roundtrip.yiyin");await(Task)Call(Window,"CaptureAndSaveAsync",saved);
        Check(ProjectStore.Load(saved).Rack.FindTrack(mic.Id) is {Solo:true,Mute:true},"Solo and Mute persist in actual saved project");
        var local=CopyLocal();local.ShowRackLibrary=false;local.SendHeight=24;await Host.ApplyLocalAsync(local);
        var loaded=new AppSettingsStore(Path.Combine(Evidence,"data","app-settings.json")).Load();
        Check(!loaded.RackLocal.ShowRackLibrary&&loaded.RackLocal.SendHeight==24,"display choices and collapsed height persist in actual settings");
        var oldSettings=JsonNode.Parse(JsonSerializer.Serialize(loaded,ProjectStore.JsonOptions))!;
        foreach(var key in new[]{"ShowRackInsert","ShowRackSend","ShowRackOutput","ShowRackInspector","ShowRackLibrary"})oldSettings["RackLocal"]!.AsObject().Remove(key);
        var legacy=JsonSerializer.Deserialize<AppSettings>(oldSettings.ToJsonString(),ProjectStore.JsonOptions)!;
        Check(legacy.RackLocal.ShowRackInsert&&legacy.RackLocal.ShowRackLibrary,"old settings without new fields default to visible modules");
        await Task.Delay(150);
        Check(Find<CheckBox>(Window,"rack-show-library")!.IsChecked==false,"saved module choice updates current rendered view");
        await Window.RackService.DisposeAsync();typeof(MainWindow).GetField("_exitReady",Flags)!.SetValue(Window,true);Window.Close();
        Window=new MainWindow(Path.Combine(Evidence,"data"),saved){Width=1600,Height=950,ShowActivated=false,ShowInTaskbar=false};
        Window.Show();await Task.Delay(150);
        Check(Host.Project.FindTrack(mic.Id) is {Solo:true,Mute:true},"reopened real window preserves Solo and Mute");
        Check(!Host.Local.ShowRackLibrary&&Find<CheckBox>(Window,"rack-show-library")!.IsChecked==false,"reopened real window preserves hidden plugin library");
        Capture("actual-host.png");
    }
}
