using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LightDeck.Core;

public sealed class RackDocument
{
    public string Format { get; set; } = "Yiyin.Rack";
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "未命名工程";
    public Guid RackId { get; set; } = Guid.NewGuid();
    public string RackName { get; set; } = "主机架";
    public RackProject Rack { get; set; } = new();
    public List<RackSnapshot> RackSnapshots { get; set; } = [];
    public TabletAccessSettings TabletAccess { get; set; } = new();

    public void Validate()
    {
        if (Format != "Yiyin.Rack" || Version != 1) throw new InvalidDataException("不支持的易音工程格式或版本。");
        RackSnapshotCatalog.Validate(RackId, RackName, Rack, RackSnapshots);
        if (Rack.Tracks.Concat(RackSnapshots.SelectMany(s => s.Rack.Tracks)).Any(t => t.Kind == RackTrackKind.Playback))
            throw new InvalidDataException("纯机架工程不支持内部音视频输入，请先通过导入转换为编组。");
    }
}

public sealed class TabletAccessSettings { public bool AllowRack { get; set; } = true; }

public static class ProjectStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static RackDocument Load(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        var properties = json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        if (!properties.TryGetValue("Format", out var format) || format.GetString() != "Yiyin.Rack" || !properties.ContainsKey("Version")) throw new InvalidDataException("文件不是易音工程。");
        var document = json.RootElement.Deserialize<RackDocument>(JsonOptions) ?? throw new InvalidDataException("工程内容为空。");
        document.Validate(); return document;
    }

    public static void Save(string path, RackDocument document)
    {
        document.Validate(); WriteAtomic(path, document);
    }

    public static void WriteAtomic<T>(string path, T value)
    {
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var pending = full + ".pending";
        try
        {
            File.WriteAllBytes(pending, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
            if (File.Exists(full)) File.Replace(pending, full, full + ".bak");
            else File.Move(pending, full);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    public static RackDocument ImportYideck(string path, out int converted)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("project.json") ?? throw new InvalidDataException("易控工程缺少 project.json。");
        if (entry.Length > 256 * 1024 * 1024) throw new InvalidDataException("工程数据过大。");
        using var stream = entry.Open();
        var document = JsonSerializer.Deserialize<RackDocument>(stream, JsonOptions) ?? throw new InvalidDataException("工程内容为空。");
        document.Format = "Yiyin.Rack"; document.Version = 1; converted = 0;
        foreach (var rack in new[] { document.Rack }.Concat(document.RackSnapshots.Select(s => s.Rack)))
        {
            foreach (var track in rack.Tracks.Where(t => t.Kind == RackTrackKind.Playback))
            {
                track.Kind = RackTrackKind.Bus; converted++;
            }
            rack.PlaybackTrackId = null;
        }
        document.Validate(); return document;
    }
}
