using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using LightDeck.Core;

namespace LightDeck.App;

public sealed partial class AudioRackService
{
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    internal TimeSpan PluginDiscoveryQuickBudget { get; set; } = TimeSpan.FromSeconds(3);
    internal Func<string[]> PluginDefaultDirectoryProvider { get; set; } = DefaultPluginDirectories;
    public Task ScanPluginsAsync(CancellationToken token) => ScanCatalogAsync(false, token);
    public Task CheckPluginChangesAsync(CancellationToken token) => ScanCatalogAsync(true, token);

    private async Task ScanCatalogAsync(bool incremental, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        token = linked.Token;
        if (!await _scanGate.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("效果器检查已在进行。");
        try
        {
            SetScan(new(true, incremental, "检查文件", 0, 0));
            bool initializeDefaults;
            lock (_controlGate) initializeDefaults = !Local.PluginDefaultDirectoriesInitialized;
            if (initializeDefaults)
            {
                var defaults = await Task.Run(() => PluginDefaultDirectoryProvider(), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                lock (_controlGate)
                {
                    if (!Local.PluginDefaultDirectoriesInitialized)
                    {
                        var merged = Local.ScanDirectories.ToList();
                        var known = new HashSet<string>(merged.Select(NormalizeDirectory), StringComparer.OrdinalIgnoreCase);
                        foreach (var directory in defaults)
                            if (known.Add(NormalizeDirectory(directory))) merged.Add(directory);
                        Local.ScanDirectories = merged;
                        // Keep absent standard paths registered for future installations.
                        // Once merged, a user can remove any default without a cancelled
                        // scan adding it back.
                        Local.PluginDefaultDirectoriesInitialized = true;
                    }
                }
            }
            string[] directories;
            Dictionary<string, RackPluginFileStamp> savedStamps;
            List<RackPluginCatalogEntry> savedCatalog;
            lock (_controlGate)
            {
                directories = Local.ScanDirectories.ToArray();
                savedStamps = new(Local.PluginFileStamps, StringComparer.OrdinalIgnoreCase);
                savedCatalog = Local.PluginCatalog.ToList();
            }
            // Legacy caches did not save file identities. Establish a baseline
            // from their successful scan timestamp, without loading every plugin.
            var result = await Task.Run(() => Discover(directories, savedStamps, savedCatalog, incremental, PluginDiscoveryQuickBudget,
                () => SetScan(new(true, incremental, "后台继续检查剩余文件", 0, 0, Deferred: true)), token), token).ConfigureAwait(false);
            var catalog = savedCatalog.ToList(); var stamps = new Dictionary<string, RackPluginFileStamp>(savedStamps, StringComparer.OrdinalIgnoreCase);
            foreach (var p in result.Unchanged) stamps[p.Key] = p.Value;
            foreach (var item in catalog.Where(c => !result.Seen.Contains(c.Path) && !File.Exists(c.Path) && !Directory.Exists(c.Path)).ToArray()) { var index = catalog.IndexOf(item); catalog[index] = item with { Available = false, Error = "插件文件已不存在，请重新安装或扫描。" }; stamps.Remove(item.Path); }
            int completed = 0;
            foreach (var file in result.Changed)
            {
                token.ThrowIfCancellationRequested(); SetScan(new(true, incremental, "扫描变动", completed, result.Changed.Count, file.Key));
                var replacements = new List<RackPluginCatalogEntry>();
                try
                {
                    var scan = await RackEngineProcess.ScanAsync(NativePath("Yideck.Audio.Scan.exe"), file.Key, token).ConfigureAwait(false);
                    foreach (var item in scan.GetProperty("plugins").EnumerateArray())
                    {
                        var identity = new RackPluginIdentity { Format = Text(item, "format").ToUpperInvariant(), ClassId = Text(item, "classId"), Name = Text(item, "name"), Vendor = Text(item, "vendor"), Version = Text(item, "version") };
                        RackValidation.ValidateIdentity(identity);
                        replacements.Add(new() { Identity = identity, Path = file.Key, LastScannedUtc = DateTimeOffset.UtcNow });
                    }
                    if (replacements.Count == 0) throw new IOException("插件未返回可用类别。");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    var existing = catalog.Where(c => string.Equals(c.Path, file.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (existing.Length != 0) replacements.AddRange(existing.Select(c => c with { Available = false, Error = ex.Message, LastScannedUtc = DateTimeOffset.UtcNow }));
                    else replacements.Add(new() { Identity = new() { Format = file.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "VST2" : "VST3", ClassId = "scan-failure-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(file.Key)))[..16], Name = Path.GetFileName(file.Key) }, Path = file.Key, Available = false, Error = ex.Message, LastScannedUtc = DateTimeOffset.UtcNow });
                }
                catalog.RemoveAll(c => string.Equals(c.Path, file.Key, StringComparison.OrdinalIgnoreCase)); catalog.AddRange(replacements); stamps[file.Key] = file.Value; completed++;
            }
            // Publish a complete result at once. Cancellation retains the prior
            // catalog and never leaves half of a shell's classes removed.
            lock (_controlGate)
            {
                token.ThrowIfCancellationRequested();
                Local.PluginCatalog = catalog; Local.PluginFileStamps = stamps;
                var locations = new Dictionary<string, string>(Local.PluginLocations);
                foreach (var item in catalog.Where(c => c.Available)) locations[item.Identity.Key] = item.Path;
                Local.PluginLocations = locations;
                // A recorded per-file failure is a completed scan result too. Its
                // stamp avoids repeatedly loading an unchanged incompatible plugin.
                Local.InitialPluginScanCompleted = true;
            }
            RefreshPluginErrors(); SetScan(new(false, incremental, "完成", completed, result.Changed.Count));
        }
        catch (OperationCanceledException) { SetScan(new(false, incremental, "已取消")); throw; }
        catch (Exception ex) { SetScan(new(false, incremental, "检查失败：" + ex.Message)); throw; }
        finally { _scanGate.Release(); }
    }
    private static string NormalizeDirectory(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    internal static string[] DefaultPluginDirectories() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Common", "VST3"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST2"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VstPlugins"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steinberg", "VstPlugins")
    ];
    private void SetScan(RackPluginScanProgress progress) { Status = Status with { ScanProgress = progress }; Notify(); }
    private sealed record Discovery(Dictionary<string, RackPluginFileStamp> Changed, Dictionary<string, RackPluginFileStamp> Unchanged, HashSet<string> Seen);
    private static Discovery Discover(string[] directories, Dictionary<string, RackPluginFileStamp> saved, List<RackPluginCatalogEntry> catalog, bool incremental, TimeSpan quickBudget, Action continueInBackground, CancellationToken token)
    {
        var changed = new Dictionary<string, RackPluginFileStamp>(StringComparer.OrdinalIgnoreCase); var unchanged = new Dictionary<string, RackPluginFileStamp>(StringComparer.OrdinalIgnoreCase); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var watch = Stopwatch.StartNew(); bool background = false;
        foreach (var file in EnumeratePluginFiles(directories, token))
        {
            token.ThrowIfCancellationRequested();
            // Keep this enumerator and its directory stack alive after the quick
            // phase. Restarting from the root can starve later files forever.
            if (incremental && !background && watch.Elapsed >= quickBudget) { background = true; continueInBackground(); token.ThrowIfCancellationRequested(); }
            if (!seen.Add(file)) continue;
            RackPluginFileStamp stamp;
            try { stamp = FileStamp(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep a broken bundle visible and let the isolated scanner give
                // a concrete error. Never silently retain an old available entry.
                stamp = new(0, 0);
            }
            var previous = catalog.Where(c => string.Equals(c.Path, file, StringComparison.OrdinalIgnoreCase)).ToArray();
            var same = saved.TryGetValue(file, out var old) ? old == stamp : stamp.LastWriteTimeUtcTicks > 0 && previous.Length > 0 && previous.All(c => c.LastScannedUtc?.UtcTicks >= stamp.LastWriteTimeUtcTicks);
            (incremental && same ? unchanged : changed)[file] = stamp;
        }
        return new(changed, unchanged, seen);
    }
    internal static RackPluginFileStamp FileStamp(string path)
    {
        if (File.Exists(path)) { var file = new FileInfo(path); return new(file.Length, file.LastWriteTimeUtc.Ticks); }
        // VST3 bundles contain their loadable module under Contents/x86_64-win.
        var binaryDirectory = Path.Combine(path, "Contents", "x86_64-win");
        if (!Directory.Exists(binaryDirectory)) throw new IOException("VST3 目录缺少 x64 模块。");
        var files = Directory.EnumerateFiles(binaryDirectory).Select(p => new FileInfo(p)).ToArray();
        if (files.Length == 0) throw new IOException("VST3 目录没有可加载模块。");
        return new(files.Sum(f => f.Length), files.Max(f => f.LastWriteTimeUtc.Ticks));
    }
    private static IEnumerable<string> EnumeratePluginFiles(IEnumerable<string> directories, CancellationToken token)
    {
        foreach (var directory in directories.Where(Directory.Exists))
        {
            var pending = new Stack<string>(); pending.Push(directory);
            while (pending.TryPop(out var current))
            {
                token.ThrowIfCancellationRequested(); string[] entries;
                try { entries = Directory.GetFileSystemEntries(current); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { throw new IOException("无法读取插件扫描目录：" + current, ex); }
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase) || File.Exists(entry) && entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) yield return entry;
                    else if (Directory.Exists(entry) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0) pending.Push(entry);
                }
            }
        }
    }
    private void RefreshPluginErrors()
    {
        var errors = new Dictionary<Guid, string>();
        foreach (var insert in Project.Tracks.SelectMany(t => t.Inserts))
        {
            if (!Local.PluginLocations.TryGetValue(insert.Identity.Key, out var path) || !(File.Exists(path) || Directory.Exists(path))) errors[insert.Id] = "插件文件缺失，请安装或重新扫描。";
            else if (Local.PluginCatalog.FirstOrDefault(c => c.Identity.Key == insert.Identity.Key && string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)) is { Available: false } entry) errors[insert.Id] = entry.Error;
            else if (_pluginLoadErrors.TryGetValue(insert.Id, out var existing)) errors[insert.Id] = existing;
        }
        Status = Status with { PluginErrors = errors };
    }
}
