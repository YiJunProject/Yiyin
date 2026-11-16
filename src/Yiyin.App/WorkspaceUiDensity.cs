namespace LightDeck.App;

/// <summary>Local presentation preference; never part of a recalled audio/light scene.</summary>
internal static class WorkspaceUiDensity
{
    public static string Mode { get; set; } = "standard";
    public static bool IsTouch => Mode == "touch";
    public static bool IsCompact => Mode == "compact";
    public static double ControlHeight => IsTouch ? 44 : IsCompact ? 28 : 34;
}
