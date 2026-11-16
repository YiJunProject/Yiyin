using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace LightDeck.Core;

public sealed record DiagnosticEntry(DateTimeOffset TimeUtc, string RunId, long Sequence,
    DiagnosticLevel Level, string Category, string Code, string Summary, string Reason,
    IReadOnlyDictionary<string, object?> Details);

public sealed record DiagnosticLogOptions(int QueueCapacity = 2048, int RecentCapacity = 2000,
    int MaxFiles = 10, int MaxFileBytes = 2 * 1024 * 1024);

/// <summary>Bounded, background-only disk writing. No project or credential files are exported.</summary>
public sealed class DiagnosticLogStore : IAsyncDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    { Converters = { new JsonStringEnumConverter() }, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Channel<DiagnosticEntry> _queue;
    private readonly ConcurrentQueue<DiagnosticEntry> _recent = new();
    private readonly SemaphoreSlim _disk = new(1, 1);
    private readonly DiagnosticLogOptions _options;
    private readonly Task _writer;
    private long _sequence, _dropped, _writeFailures;
    private int _disposed, _fileIndex;
    private FileStream? _stream;
    private string? _currentPath, _lastWriteError;
    public string DirectoryPath { get; }
    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public string Version { get; }
    public string? LastWriteError => Volatile.Read(ref _lastWriteError);
    public long DroppedCount => Interlocked.Read(ref _dropped);
    public long WriteFailureCount => Interlocked.Read(ref _writeFailures);

    public DiagnosticLogStore(string directory, string version, DiagnosticLogOptions? options = null)
    {
        DirectoryPath = Path.GetFullPath(directory); Version = SafeText(version, 80);
        _options = options ?? new();
        if (_options.QueueCapacity is < 1 or > 16384 || _options.RecentCapacity is < 1 or > 10000 ||
            _options.MaxFiles is < 2 or > 50 || _options.MaxFileBytes is < 1024 or > 10 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options));
        _queue = Channel.CreateBounded<DiagnosticEntry>(new BoundedChannelOptions(_options.QueueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        _writer = Task.Run(WriteLoopAsync);
    }

    public void Write(string category, string code, DiagnosticLevel level, string summary, string reason,
        params (string Key, object? Value)[] details)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var entry = Create(category, code, level, summary, reason, details);
        Remember(entry);
        if (!_queue.Writer.TryWrite(entry)) Interlocked.Increment(ref _dropped);
    }

    private DiagnosticEntry Create(string category, string code, DiagnosticLevel level, string summary, string reason,
        IEnumerable<(string Key, object? Value)> details) => new(DateTimeOffset.UtcNow, RunId,
        Interlocked.Increment(ref _sequence), level, SafeText(category, 40), SafeText(code, 80),
        SafeText(summary, 240), SafeText(reason, 600), SafeDetails(details));

    private void Remember(DiagnosticEntry entry)
    {
        _recent.Enqueue(entry);
        while (_recent.Count > _options.RecentCapacity) _recent.TryDequeue(out _);
    }

    // Keys are deliberately small facts, not arbitrary object serialization.
    private static readonly Regex PrivateKey = new("^(password|passwd|credential.*|secret.*|cookie.*|token.*|session|sessionId|username|user|host|address|ip|path|filePath|payload|raw.*|command|stack.*|exceptionMessage)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> FactKeys = new(("exceptionType hresult socketError socketNativeError appVersion processId exitCode cleanShutdown " +
        "droppedTotal writeFailures connectionId operationId generation autoReconnect enabled retryAttempt sessionRecoveries connectedAgeMs previousRecoveries failureKind retryEnabled retryable delayMs " +
        "heartbeatIntervalMs queryTimeoutMs hadFailure authenticated releaseUnconfirmed closeStatus closeStatusCode forceLoginPresent forceLoginType forceLoginBoolean hasSession sessionRelation " +
        "pendingAgeMs lastReceiveAgeMs heartbeatThresholdMs phase lastHeartbeatAgeMs socketState connectionAgeMs " +
        "waitStage localStopping deviceChanges available powerMode exceptionFatal " +
        "api requestedSampleRate requestedBufferFrames requestedOutputDevice requestedInputDevice actualDevice actualSampleRate actualBufferFrames intent causeCode running notificationFlags purpose operation cause sent intentionalShutdown aggregationSeconds " +
        "xruns deadlineMisses gateSkips mediaUnderruns outputOverRangeSamples outputNonFiniteSamples integerOutputOverRangeSamples samplePositionDiscontinuities bridgeUnderflows bridgeOverflows asioErrorCode").Split(' '), StringComparer.Ordinal);
    private static readonly Regex SecretText = new("(?i)(password|passwd|token|secret|cookie|authorization|apiKey|accessToken|loginName|username|sessionId)\\s*[:=]\\s*[^\\s,;]+|[A-Z]:[\\\\/][^\\s]+|\\\\\\\\[^\\s]+|(?:https?|wss?)://[^\\s]+|\\b(?:[0-9]{1,3}\\.){3}[0-9]{1,3}\\b", RegexOptions.CultureInvariant);
    private static string SafeText(string? text, int limit)
    {
        if (text is null) return "";
        var bounded = text.Length > limit * 4 ? text[..(limit * 4)] : text;
        bounded = SecretText.Replace(bounded, "[已脱敏]").Replace('\r', ' ').Replace('\n', ' ');
        return bounded.Length > limit ? bounded[..limit] + "…" : bounded;
    }
    private static IReadOnlyDictionary<string, object?> SafeDetails(IEnumerable<(string Key, object? Value)> details)
    {
        var safe = new Dictionary<string, object?>();
        foreach (var (key, value) in details.Take(32))
        {
            if (!FactKeys.Contains(key) || PrivateKey.IsMatch(key)) continue;
            safe[key] = value switch
            {
                null => null, bool or int or uint or long or ulong or short or byte => value,
                double n when double.IsFinite(n) => n, float n when float.IsFinite(n) => n,
                decimal => value, Enum => value.ToString(), string s => SafeText(s, 240),
                _ => "[未记录复杂内容]"
            };
        }
        return safe;
    }

    private async Task WriteLoopAsync()
    {
        long reportedDropped = 0;
        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                await _disk.WaitAsync().ConfigureAwait(false);
                try
                {
                    var dropped = DroppedCount;
                    if (dropped != reportedDropped)
                    {
                        var warning = Create("日志", "queue-overflow", DiagnosticLevel.Warning, "诊断日志过于密集，部分记录未写入磁盘",
                            "日志队列已达到上限；程序继续运行。缺失记录数量已保留，不能据此认定没有发生故障。", [("droppedTotal", dropped)]);
                        Remember(warning); await PersistAsync(warning).ConfigureAwait(false); reportedDropped = dropped;
                    }
                    // Yield the disk lane after a bounded batch so viewing/export cannot starve.
                    for (var count = 0; count < 128 && _queue.Reader.TryRead(out var entry); count++)
                        await PersistAsync(entry).ConfigureAwait(false);
                    if (_stream is not null) await _stream.FlushAsync().ConfigureAwait(false);
                    Volatile.Write(ref _lastWriteError, null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    Interlocked.Increment(ref _writeFailures);
                    Volatile.Write(ref _lastWriteError, "日志磁盘写入失败（" + ex.GetType().Name + "）。本次内存中的近期记录仍可导出；磁盘记录可能不完整。");
                    if (_stream is not null) { try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { } _stream = null; }
                    // Keep a bounded in-memory record. Do not spend minutes draining a broken
                    // disk on exit or let disk failure throttle control producers.
                    for (var i = 0; i < 128 && _queue.Reader.TryRead(out _); i++) Interlocked.Increment(ref _dropped);
                }
                finally { _disk.Release(); }
                if (LastWriteError is not null) await Task.Delay(500).ConfigureAwait(false);
            }
        }
        finally
        {
            await _disk.WaitAsync().ConfigureAwait(false);
            try { if (_stream is not null) await _stream.DisposeAsync().ConfigureAwait(false); _stream = null; }
            finally { _disk.Release(); }
        }
    }

    private async Task PersistAsync(DiagnosticEntry entry)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n");
        if (_stream is null || _stream.Length + bytes.Length > _options.MaxFileBytes)
        {
            if (_stream is not null) await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
            Directory.CreateDirectory(DirectoryPath);
            PruneFiles(leaveRoom: true);
            _currentPath = Path.Combine(DirectoryPath, $"events-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{RunId}-{++_fileIndex:D4}.jsonl");
            _stream = new FileStream(_currentPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
            PruneFiles();
        }
        await _stream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private FileInfo[] Files() => Directory.Exists(DirectoryPath)
        ? new DirectoryInfo(DirectoryPath).GetFiles("events-*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).ToArray() : [];
    private void PruneFiles(bool leaveRoom = false)
    {
        var keep = _options.MaxFiles - (leaveRoom ? 1 : 0);
        foreach (var file in Files().Skip(keep))
        {
            try { file.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (leaveRoom && Files().Length > keep) throw new IOException("Diagnostic retention limit reached.");
    }

    public async Task<IReadOnlyList<DiagnosticEntry>> ReadRecentAsync(int limit = 2000)
    {
        limit = Math.Clamp(limit, 1, 10000);
        return await Task.Run(async () =>
        {
            var entries = new Dictionary<(string Run, long Seq), DiagnosticEntry>();
            await _disk.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_stream is not null) await _stream.FlushAsync().ConfigureAwait(false);
                foreach (var file in Files().Take(_options.MaxFiles))
                {
                    if (file.Length > _options.MaxFileBytes + 32768) continue;
                    try
                    {
                        using var reader = new StreamReader(new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                        {
                            if (line.Length > 32768) continue;
                            try { if (JsonSerializer.Deserialize<DiagnosticEntry>(line, JsonOptions) is { } e && Guid.TryParseExact(e.RunId, "N", out _))
                                entries[(e.RunId, e.Sequence)] = SanitizeEntry(e); }
                            catch (JsonException) { /* Crash-truncated lines do not hide other records. */ }
                        }
                    }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            finally { _disk.Release(); }
            foreach (var e in _recent) entries[(e.RunId, e.Sequence)] = e;
            return (IReadOnlyList<DiagnosticEntry>)entries.Values.OrderByDescending(e => e.TimeUtc).ThenByDescending(e => e.Sequence).Take(limit).ToArray();
        }).ConfigureAwait(false);
    }

    private static DiagnosticEntry SanitizeEntry(DiagnosticEntry entry)
    {
        object? Scalar(object? value) => value is JsonElement element ? element.ValueKind switch
        { JsonValueKind.String => element.GetString(), JsonValueKind.True => true, JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt64(out var n) => n,
            JsonValueKind.Number when element.TryGetDouble(out var n) => n, _ => null } : value;
        return entry with { Category = SafeText(entry.Category, 40), Code = SafeText(entry.Code, 80),
            Summary = SafeText(entry.Summary, 240), Reason = SafeText(entry.Reason, 600),
            Details = SafeDetails((entry.Details ?? new Dictionary<string, object?>()).Select(p => (p.Key, Scalar(p.Value)))) };
    }

    public async Task ExportAsync(string destination)
    {
        // Export the structured view, never directory-copy or include project settings.
        var entries = await ReadRecentAsync(10000).ConfigureAwait(false);
        await Task.Run(() =>
        {
            var full = Path.GetFullPath(destination);
            if (Path.GetDirectoryName(full) is { } parent) Directory.CreateDirectory(parent);
            var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
                {
                    void Add(string name, Action<StreamWriter> write)
                    { using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false)); write(writer); }
                    Add("events.jsonl", writer => { foreach (var e in entries.Reverse()) writer.WriteLine(JsonSerializer.Serialize(e, JsonOptions)); });
                    Add("事件时间线.txt", writer =>
                    {
                        foreach (var e in entries.Reverse())
                        {
                            writer.WriteLine($"{e.TimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff zzz} [{e.Level}] [{e.Category}] {e.Summary}");
                            writer.WriteLine("原因／已知事实：" + e.Reason);
                            writer.WriteLine($"事件 {e.Code} · 运行 {e.RunId} · 序号 {e.Sequence}");
                            writer.WriteLine(JsonSerializer.Serialize(e.Details, JsonOptions)); writer.WriteLine();
                        }
                    });
                    Add("说明.txt", w => w.Write($"易控 Yideck {Version} 诊断日志\n导出 UTC：{DateTimeOffset.UtcNow:O}\n包含最近 {entries.Count} 条（最多 10000 条），本机日志轮转默认约 20 MiB。\n仅记录软件开始采集后的可观察事件；不等于已查明所有上游根因。\n采集字段不包含密码、用户名、登录会话值、授权、原始协议帧、工程或媒体文件。\n设备友好名称可能出现；发送给他人前可检查时间线。\n当前写入状态：{LastWriteError ?? "正常"}\n本次写盘失败次数：{WriteFailureCount}（非零表示磁盘记录可能不完整）\n本次队列丢弃总数：{DroppedCount}\n"));
                }
                File.Move(temporary, full, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }
}
