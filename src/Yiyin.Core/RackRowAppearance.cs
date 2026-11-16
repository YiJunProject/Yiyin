namespace LightDeck.Core;

/// <summary>Item pitch in logical pixels. Independent of a module's viewport height.</summary>
public sealed class RackRowAppearance
{
    public double InsertItemHeight { get; set; } = 26;
    public double SendItemHeight { get; set; } = 28;
    public double OutputItemHeight { get; set; } = 28;
    public RackRowAppearance Copy() => new() { InsertItemHeight = InsertItemHeight, SendItemHeight = SendItemHeight, OutputItemHeight = OutputItemHeight };
    public void Validate()
    {
        if (new[] { InsertItemHeight, SendItemHeight, OutputItemHeight }.Any(h => !double.IsFinite(h) || h < 24 || h > 72))
            throw new ArgumentException("机架条目厚度需为 24–72 像素；区域高度单独调整。");
    }
    public double Height(string section) => section switch { "insert" => InsertItemHeight, "send" => SendItemHeight, _ => OutputItemHeight };
    public void SetHeight(string section, double value)
    {
        if (!double.IsFinite(value) || value < 24 || value > 72) throw new ArgumentOutOfRangeException(nameof(value));
        switch (section) { case "insert": InsertItemHeight = value; break; case "send": SendItemHeight = value; break; case "output": OutputItemHeight = value; break; default: throw new ArgumentException("未知条目类型。"); }
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Signature => $"{InsertItemHeight:R}/{SendItemHeight:R}/{OutputItemHeight:R}";
    public static RackRowAppearance Resolve(RackRowAppearance? defaults, RackRowAppearance? custom) => (custom ?? defaults ?? new()).Copy();
    public static RackRowAppearance Preset(string preset) => preset switch
    {
        "standard" => new() { InsertItemHeight = 34, SendItemHeight = 36, OutputItemHeight = 36 },
        "touch" => new() { InsertItemHeight = 44, SendItemHeight = 44, OutputItemHeight = 44 },
        _ => new()
    };
    public const double HeaderHeight = 24;
    public double ContentHeight(string section, int count) => HeaderHeight + Math.Max(1, count) * Height(section);
}
