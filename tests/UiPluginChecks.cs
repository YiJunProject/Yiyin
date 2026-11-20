using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightDeck.App;
using LightDeck.Core;

internal static partial class UiPluginChecks
{
 static readonly BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
 static readonly List<object> Checks=[];
 static string Evidence="";
 static MainWindow Window=null!;
 static IRackUiHost Host=>(IRackUiHost)typeof(MainWindow).GetProperty("RackHost",Flags)!.GetValue(Window)!;
 static object Call(object instance,string name,params object?[] args)=>instance.GetType().GetMethod(name,Flags)!.Invoke(instance,args)!;
 static void Check(bool pass,string name,object? facts=null){Checks.Add(new{name,pass,facts});if(!pass)throw new Exception(name);Console.WriteLine("PASS "+name);}
 static void Invoke(string id)=>((IInvokeProvider)new ButtonAutomationPeer(Find<Button>(Window,id)!).GetPattern(PatternInterface.Invoke)).Invoke();
 static T? Find<T>(DependencyObject parent,string id) where T:DependencyObject{
  if(parent is T value&&AutomationProperties.GetAutomationId(parent)==id)return value;
  for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)if(Find<T>(VisualTreeHelper.GetChild(parent,i),id)is{} child)return child;
  return null;
 }
 [STAThread] static int Main(string[] args){
  Evidence=Path.GetFullPath(args[0]);Directory.CreateDirectory(Evidence);Environment.SetEnvironmentVariable("YIYIN_NO_AUDIO","1");
  var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};int exit=0;
  app.Startup+=async(_,_)=>{try{if(args.Contains("--multi-output"))await CheckMultiOutput(args[2], args[3]);else if(args.Contains("--v110-host"))await RunHost(args.Contains("--baseline"));else await Run(args.Contains("--before"));}catch(Exception e){exit=1;Console.Error.WriteLine(e);File.WriteAllText(Path.Combine(Evidence,"failure.txt"),e.ToString());}finally{
   if(Window is not null){await Window.RackService.DisposeAsync();typeof(MainWindow).GetField("_exitReady",Flags)!.SetValue(Window,true);Window.Close();}
   File.WriteAllText(Path.Combine(Evidence,"result.json"),JsonSerializer.Serialize(new{exit,checks=Checks},ProjectStore.JsonOptions));app.Shutdown(exit);
  }};return app.Run();
 }
 static async Task Run(bool before){
  Window=new MainWindow(Path.Combine(Evidence,"data")){Width=1400,Height=850,ShowActivated=false,ShowInTaskbar=false};Window.Show();await Task.Delay(150);
  foreach(var id in new[]{"yiyin-project","yiyin-tablet","yiyin-theme","yiyin-settings"}){
   var button=Find<FrameworkElement>(Window,id)!;var point=button.PointToScreen(new Point(button.ActualWidth/2,button.ActualHeight/2));var hit=SendMessage(new WindowInteropHelper(Window).Handle,0x84,0,(nint)(((int)point.Y<<16)|((int)point.X&0xffff))).ToInt64();
   Check(hit==(before?2:1),(before?"复现标题栏吞掉点击：":"顶部物理命中客户区：")+id,new{hit});
  }
  if(before){Check(Find<Button>(Window,"window-close") is null,"复现缺少窗口控制按钮");return;}
  Capture("initial.png");
  foreach(var id in new[]{"window-minimize","window-maximize","window-close"}){var button=Find<Button>(Window,id);Check(button is{IsVisible:true,IsEnabled:true},"窗口按钮可见可用："+id,new{found=button is not null,visible=button?.IsVisible,enabled=button?.IsEnabled,resize=Window.ResizeMode.ToString()});}
  Invoke("window-maximize");await Task.Delay(100);Check(Window.WindowState==WindowState.Maximized,"最大化按钮改变真实窗口状态");Capture("maximized.png");
  Invoke("window-maximize");await Task.Delay(100);Check(Window.WindowState==WindowState.Normal,"最大化按钮还原");
  Invoke("window-minimize");await Task.Delay(100);Check(Window.WindowState==WindowState.Minimized,"最小化按钮改变真实窗口状态");Window.WindowState=WindowState.Normal;
  Find<Button>(Window,"yiyin-project")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(80);Check(Window.OwnedWindows.Count==0,"工程入口不创建无关窗口");
  Find<Button>(Window,"rack-edit-mode")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  var track=RackTrack.Create(RackTrackKind.Bus,"VST3 实际插入验证");var rack=new RackProject{Tracks=[track],SelectedTrackId=track.Id};await Host.ApplyProjectAsync(rack);
  foreach(var pluginFile in new[]{@"C:\Program Files\Common Files\VST3\ValhallaDSP\ValhallaRoom.vst3",@"C:\Program Files\Common Files\VST3\FabFilter\FabFilter Pro-Q 3.vst3"}){
   var info=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"audio-native","Yideck.Audio.Scan.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add(pluginFile);
   using var scanner=Process.Start(info)!;var output=await scanner.StandardOutput.ReadToEndAsync();await scanner.WaitForExitAsync();using var json=JsonDocument.Parse(output);var item=json.RootElement.GetProperty("plugins")[0];
   var identity=new RackPluginIdentity{Format="VST3",ClassId=item.GetProperty("classId").GetString()!,Name=item.GetProperty("name").GetString()!,Vendor=item.GetProperty("vendor").GetString()!,Version=item.GetProperty("version").GetString()!};
   Host.Local.PluginLocations[identity.Key]=pluginFile;Host.Local.PluginCatalog=[new(){Identity=identity,Path=pluginFile,Available=true}];
   Call(Window,"BuildShell");await Task.Delay(80);if(Find<Button>(Window,"rack-library-tab") is {} legacyTab){legacyTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(80);}
   var insert=Find<Button>(Window,"rack-library-add-"+identity.ClassId);Check(insert is not null,"真实插件库提供插入按钮："+identity.Name);var count=Host.Project.Tracks[0].Inserts.Count;insert!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   var deadline=DateTime.UtcNow.AddSeconds(20);while(Host.Project.Tracks[0].Inserts.Count==count&&DateTime.UtcNow<deadline)await Task.Delay(50);
   Check(Host.Project.Tracks[0].Inserts.Count==count+1,"插件库按钮实际插入："+identity.Name);
   var instance=Host.Project.Tracks[0].Inserts.Last();await Host.ShowPluginEditorAsync(track.Id,instance.Id,new WindowInteropHelper(Window).Handle);
   var command=typeof(AudioRackService).GetMethod("CommandAsync",Flags)!;
   var response=await(Task<JsonElement>)command.Invoke(Window.RackService,["editor",new JsonObject{["instanceId"]=instance.Id.ToString(),["show"]=true},CancellationToken.None])!;
   Check(response.GetProperty("visible").GetBoolean(),"实际 VST3 原生窗口打开："+identity.Name);
   var handle=(nint)response.GetProperty("windowHandle").GetInt64();await Task.Delay(150);Print(handle,identity.Name.Replace(' ','-')+".png");
   var snapshot=await Window.RackService.CapturePluginStatesAsync();Check(snapshot.Tracks[0].Inserts.Last().StateBase64.Length>0,"实际 VST3 参数状态可取得："+identity.Name);
   await Window.RackService.HidePluginEditorsAsync();
  }
  var saved=Path.Combine(Evidence,"plugins.yiyin");await(Task)Call(Window,"CaptureAndSaveAsync",saved);Check(ProjectStore.Load(saved).Rack.Tracks[0].Inserts.Count==2,"两款实际 VST3 保存重读");
  Window.Width=1000;Window.Height=650;await Task.Delay(150);Capture("minimum.png");Check(Find<Button>(Window,"window-close")!.IsVisible,"最小窗口保留关闭按钮");
 }
 static void Capture(string file){Window.UpdateLayout();var bmp=new RenderTargetBitmap((int)Window.ActualWidth,(int)Window.ActualHeight,96,96,PixelFormats.Pbgra32);bmp.Render(Window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bmp));using var stream=File.Create(Path.Combine(Evidence,file));encoder.Save(stream);}
 static void Print(nint handle,string file){GetWindowRect(handle,out var rect);using var bmp=new System.Drawing.Bitmap(rect.Right-rect.Left,rect.Bottom-rect.Top);using var graphics=System.Drawing.Graphics.FromImage(bmp);var dc=graphics.GetHdc();try{Check(PrintWindow(handle,dc,2),"捕获插件实际 HWND");}finally{graphics.ReleaseHdc(dc);}bmp.Save(Path.Combine(Evidence,file),System.Drawing.Imaging.ImageFormat.Png);}
 [StructLayout(LayoutKind.Sequential)]struct Rect{public int Left,Top,Right,Bottom;}
 [DllImport("user32.dll")]static extern nint SendMessage(nint h,uint m,nint w,nint l);
 [DllImport("user32.dll")]static extern bool GetWindowRect(nint h,out Rect r);
 [DllImport("user32.dll")]static extern bool PrintWindow(nint h,nint dc,uint flags);
}
