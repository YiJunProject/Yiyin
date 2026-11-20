using System.Text.Json;
namespace LightDeck.Core;
public sealed partial class RackLocalSettings
{
    public RackRowAppearance RowAppearance { get; set; } = new();
    public bool RoutePlayer { get; set; }
    public bool WasRunning { get; set; }
    private string _deviceApi = "asio";
    public string DeviceApi { get => _deviceApi; set => _deviceApi = NormalizeDeviceApi(value); }
    public static string NormalizeDeviceApi(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "wasapi" or "windows audio" => "wasapi",
        "asio" => "asio",
        _ => throw new ArgumentException("机架音频 API 只支持 WASAPI 或 ASIO。")
    };
    public string InputDeviceId { get; set; } = "";
    public string InputDeviceName { get; set; } = "";
    public string OutputDeviceId { get; set; } = "";
    public string OutputDeviceName { get; set; } = "";
    public int SampleRate { get; set; } = 48000;
    public int BufferSize { get; set; } = 256;
    public Dictionary<Guid, int[]> InputChannels { get; set; } = [];
    public Dictionary<Guid, int[]> OutputChannels { get; set; } = [];
    public Dictionary<Guid, RackHardwarePort> InputPorts { get; set; } = [];
    public Dictionary<Guid, RackHardwarePort> OutputPorts { get; set; } = [];
    public Dictionary<string, string> PluginLocations { get; set; } = [];
    public List<string> ScanDirectories { get; set; } = [];
    public bool PluginDefaultDirectoriesInitialized { get; set; }
    public bool InitialPluginScanCompleted { get; set; }
    public List<RackPluginCatalogEntry> PluginCatalog { get; set; } = [];
    public Dictionary<string, RackPluginFileStamp> PluginFileStamps { get; set; } = [];
    public double InsertHeight { get; set; } = 120;
    public double SendHeight { get; set; } = 100;
    public double OutputHeight { get; set; } = 112;
    public double FaderHeight { get; set; } = 180;
    public bool CompactView { get; set; }
    public bool ShowRackInsert { get; set; } = true;
    public bool ShowRackSend { get; set; } = true;
    public bool ShowRackOutput { get; set; } = true;
    public bool ShowRackInspector { get; set; } = true;
    public bool ShowRackLibrary { get; set; } = true;
    public List<string> FavoritePlugins { get; set; } = [];
    public List<string> RecentPlugins { get; set; } = [];
    public void Validate()
    {
        FavoritePlugins ??= []; RecentPlugins ??= [];
        RowAppearance ??= new(); RowAppearance.Validate();
        FavoritePlugins = FavoritePlugins.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).Take(256).ToList();
        RecentPlugins = RecentPlugins.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).Take(20).ToList();
        if (SampleRate is < 8000 or > 384000 || BufferSize < (DeviceApi == "asio" ? 8 : 16) || BufferSize > 8192 || DeviceApi is not ("wasapi" or "asio")) throw new ArgumentException("音频设备 API、采样率或缓冲大小无效。");
        if (InputChannels is null || OutputChannels is null || InputPorts is null || OutputPorts is null || PluginLocations is null || ScanDirectories is null || PluginCatalog is null || PluginFileStamps is null) throw new ArgumentException("本机机架映射缺失。");
        if (!double.IsFinite(InsertHeight) || InsertHeight is < 0 or > 360 || !double.IsFinite(SendHeight) || SendHeight is < 0 or > 360 || !double.IsFinite(OutputHeight) || OutputHeight is < 48 or > 720 || !double.IsFinite(FaderHeight) || FaderHeight is < 120 or > 720) throw new ArgumentException("插入/发送高度需为 0–360，输出为 48–720，推子为 120–720 像素。");
        ValidateHardwareRoutes();
        foreach (var map in InputChannels.Concat(OutputChannels))
            if (map.Key == Guid.Empty || map.Value is null || map.Value.Length is < 1 or > 2 || map.Value.Any(channel => channel is < 0 or > 255) || map.Value.Distinct().Count() != map.Value.Length) throw new ArgumentException("物理声道映射需为 1–2 个不同的零起始声道。");
        foreach (var location in PluginLocations) if (string.IsNullOrWhiteSpace(location.Key) || !Path.IsPathFullyQualified(location.Value)) throw new ArgumentException("本机插件路径无效。");
        foreach (var directory in ScanDirectories) if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("插件扫描目录须为完整路径。");
        foreach (var entry in PluginCatalog)
        {
            if (entry is null || !Path.IsPathFullyQualified(entry.Path)) throw new ArgumentException("本机插件目录项无效。");
            RackValidation.ValidateIdentity(entry.Identity);
        }
    }
}

public sealed record RackPluginFileStamp(long Length, long LastWriteTimeUtcTicks);

public sealed record RackPluginCatalogEntry
{
    public RackPluginIdentity Identity { get; set; } = new();
    public string Path { get; set; } = "";
    public DateTimeOffset? LastScannedUtc { get; set; }
    public bool Available { get; set; } = true;
    public string Error { get; set; } = "";
}
