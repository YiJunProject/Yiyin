using System.Text.Json;

namespace LightDeck.Core;

public sealed class AppSettings
{
    public RackLocalSettings RackLocal { get; set; } = new();
    public string ThemeId { get; set; } = "graphite";
    public string Density { get; set; } = "standard";
    public string? ProjectPath { get; set; }
    public double Width { get; set; } = 1500;
    public double Height { get; set; } = 900;
    public bool Maximized { get; set; }
}

public sealed class AppSettingsStore(string path)
{
    public AppSettings Load()
    {
        if (!File.Exists(path)) return new();
        var value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(path), ProjectStore.JsonOptions) ?? throw new InvalidDataException("本机设置为空。");
        value.RackLocal.Validate();
        if (value.RackLocal.RoutePlayer) throw new InvalidDataException("易音不支持内部播放器路由。");
        return value;
    }
    public void Save(AppSettings value) { value.RackLocal.Validate(); ProjectStore.WriteAtomic(path, value); }
}
