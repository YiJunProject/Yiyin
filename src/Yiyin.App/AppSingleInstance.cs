using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace LightDeck.App;

internal sealed class AppSingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<Action<string?>> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task? _listener;
    internal bool IsPrimary { get; }

    internal AppSingleInstance(string profilePath)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        var key = $"{identity.User!.Value}|{process.SessionId}|{Path.GetFullPath(profilePath).ToUpperInvariant()}";
        _pipeName = "Yiyin-instance-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        _mutex = new Mutex(false, "Local\\" + _pipeName);
        try { IsPrimary = _mutex.WaitOne(0); } catch (AbandonedMutexException) { IsPrimary = true; }
        if (IsPrimary) _listener = Task.Run(ListenAsync);
    }

    internal void Attach(Window window, Func<string, Task<bool>> openProject)
    {
        _ready.TrySetResult(path => window.Dispatcher.BeginInvoke(new Action(async () =>
        {
            Activate(window);
            if (path is not null) await openProject(path);
        })));
    }

    internal async Task ActivateExistingAsync(string? projectPath)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        var process = int.Parse((await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false))!);
        AllowSetForegroundWindow(process);
        await writer.WriteLineAsync(JsonSerializer.Serialize(projectPath is null ? null : Path.GetFullPath(projectPath))).ConfigureAwait(false);
        if (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) != "accepted") throw new IOException("现有易音未确认唤醒请求，请稍后再次打开。");
    }

    private async Task ListenAsync()
    {
        var token = _lifetime.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                await writer.WriteLineAsync(Environment.ProcessId.ToString()).ConfigureAwait(false);
                var message = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) ?? throw new IOException("启动请求为空。");
                if (message.Length > 32768) throw new IOException("工程路径过长。");
                var requested = JsonSerializer.Deserialize<string?>(message);
                var path = requested is null ? null : Path.GetFullPath(requested);
                await writer.WriteLineAsync("accepted").ConfigureAwait(false);
                var activate = await _ready.Task.WaitAsync(token).ConfigureAwait(false);
                activate(path);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or OperationCanceledException or JsonException or ArgumentException)
            { /* A failed launch request must not retire the owning application. */ }
        }
    }

    internal static void Activate(Window window) => Activate(new WindowInteropHelper(window).Handle);
    private static void Activate(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        if (IsIconic(handle)) ShowWindowAsync(handle, 9);
        var popup = GetLastActivePopup(handle);
        SetForegroundWindow(popup != IntPtr.Zero && IsWindowVisible(popup) ? popup : handle);
    }
    public void Dispose()
    {
        _lifetime.Cancel();
        if (_listener is not null) try { _listener.Wait(1000); } catch (AggregateException) { }
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose(); _lifetime.Dispose();
    }
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr window);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
}
