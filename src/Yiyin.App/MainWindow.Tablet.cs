using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LightDeck.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LightDeck.App;

public sealed partial class MainWindow
{
    private WebApplication? _tabletServer;
    private string _tabletCode = "", _tabletToken = "", _tabletStructure = "";
    private int _tabletPort;
    private long _tabletRevision;
    private Window? _tabletDialog;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private async void ShowTablet()
    {
        if (_tabletDialog is not null) { _tabletDialog.Activate(); return; }
        var dialog = new Window { Title = "易音 · 平板连接", Owner = this, Width = 660, Height = 430, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        _theme.Apply(dialog); var body = new StackPanel { Margin = new Thickness(24) };
        var info = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 235, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 12) };
        body.Children.Add(new TextBlock { Text = "同一局域网内，用平板浏览器打开地址并输入连接码。", TextWrapping = TextWrapping.Wrap }); body.Children.Add(info);
        var toggle = new Button { MinHeight = 36 }; body.Children.Add(toggle);
        void Refresh()
        {
            toggle.Content = _tabletServer is null ? "开启平板连接" : "停止平板连接";
            if (_tabletServer is null) { info.Text = "平板连接已关闭。不会自动修改系统防火墙。"; return; }
            var addresses = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => $"http://{a.Address}:{_tabletPort}/");
            info.Text = string.Join("\n", addresses) + $"\n\n连接码：{_tabletCode}\n\n电脑编辑期间暂停平板控制。网络需允许本程序接收连接；停止或退出会断开所有平板。";
        }
        toggle.Click += async (_, _) =>
        {
            toggle.IsEnabled = false;
            try { if (_tabletServer is null) await StartTabletAsync(); else await StopTabletAsync(); Refresh(); }
            catch (Exception e) { info.Text = "连接未开启：" + e.Message; }
            finally { toggle.IsEnabled = true; }
        };
        dialog.Content = body; _tabletDialog = dialog; dialog.Closed += (_, _) => _tabletDialog = null; Refresh(); dialog.Show(); await Task.CompletedTask;
    }

    internal async Task StartTabletAsync()
    {
        if (_tabletServer is not null) return;
        _tabletCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        _tabletToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(0));
        var app = builder.Build(); app.UseWebSockets();
        app.MapGet("/", () => Results.File(Path.Combine(AppContext.BaseDirectory, "Web", "index.html"), "text/html"));
        foreach (var file in new[] { "rack.js", "rack-row.js", "tablet.js", "rack.css", "rack-row.css", "tablet.css" })
        {
            var name = file;
            app.MapGet("/" + name, () => Results.File(Path.Combine(AppContext.BaseDirectory, "Web", name), name.EndsWith(".js") ? "text/javascript" : "text/css"));
        }
        app.MapPost("/api/pair", async (HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync<PairRequest>();
            if (request?.Code != _tabletCode) return Results.Unauthorized();
            context.Response.Cookies.Append("yiyin-session", _tabletToken, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, IsEssential = true });
            return Results.Ok(new { paired = true });
        });
        app.Map("/ws", async (HttpContext context) =>
        {
            if (context.Request.Cookies["yiyin-session"] != _tabletToken || !context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 401; return; }
            var origin = context.Request.Headers.Origin.ToString();
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || originUri.Authority != context.Request.Host.Value) { context.Response.StatusCode = 403; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
            var token = lifetime.Token;
            var receive = ReceiveTabletAsync(socket, token);
            try
            {
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open && !receive.IsCompleted)
                {
                    var state = await Dispatcher.InvokeAsync(ReadTabletEnvelope);
                    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(state, WebJson), WebSocketMessageType.Text, true, token);
                    await Task.WhenAny(Task.Delay(50, token), receive);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
            finally { lifetime.Cancel(); socket.Abort(); try { await receive; } catch (Exception e) when (e is OperationCanceledException or WebSocketException) { } }
        });
        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            _tabletPort = new Uri(address).Port; _tabletServer = app;
        }
        catch { await app.DisposeAsync(); throw; }
    }

    private object ReadTabletEnvelope()
    {
        var signature = _rackGeneration + ":" + TabletRackStructureIdentity() + ":" + _rackEditing;
        if (signature != _tabletStructure) { _tabletStructure = signature; _tabletRevision++; }
        return new { revision = _tabletRevision, rackAvailable = true, lightingAvailable = false, musicAvailable = false, rack = ReadTabletRackState(), theme = _theme.Id };
    }

    private async Task ReceiveTabletAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[8192]; long sequence = 0;
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (!result.EndOfMessage || result.MessageType != WebSocketMessageType.Text) { socket.Abort(); break; }
            try
            {
                var message = JsonSerializer.Deserialize<TabletMessage>(buffer.AsSpan(0, result.Count), WebJson) ?? throw new ArgumentException("无效的机架消息。");
                if (message.Type == "rackSubscribe") continue;
                if (message.Type != "rackControl" || message.Seq <= sequence) continue;
                sequence = message.Seq;
                await Dispatcher.InvokeAsync(async () =>
                {
                    ReadTabletEnvelope();
                    if (message.Revision != _tabletRevision || token.IsCancellationRequested) return;
                    await ApplyTabletRackControlAsync(new(message.TrackId, message.Control, message.SendId, message.Value, message.Enabled, message.PluginId), () => !token.IsCancellationRequested && message.Revision == _tabletRevision);
                }).Task.Unwrap();
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or OperationCanceledException) { }
        }
    }

    private async Task StopTabletAsync()
    {
        var app = _tabletServer; _tabletServer = null; _tabletToken = ""; _tabletCode = "";
        if (app is not null) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await app.StopAsync(timeout.Token); await app.DisposeAsync(); }
    }
    private sealed record PairRequest(string Code);
    private sealed record TabletMessage(string Type, long Revision, long Seq, Guid TrackId, string Control = "", Guid? SendId = null, double? Value = null, bool? Enabled = null, Guid? PluginId = null);
    private static bool MergePluginStates(RackProject destination, RackProject captured)
    {
        var changed = false;
        foreach (var saved in captured.Tracks.SelectMany(t => t.Inserts))
        {
            var current = destination.Tracks.SelectMany(t => t.Inserts).FirstOrDefault(i => i.Id == saved.Id && i.Identity.Key == saved.Identity.Key);
            if (current is not null && current.StateBase64 != saved.StateBase64) { current.StateBase64 = saved.StateBase64; changed = true; }
        }
        return changed;
    }
}
