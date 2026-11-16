using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;

namespace LightDeck.App;

public sealed record DeckTheme(string Id, string Name, string Subtitle, string Background, string Panel,
    string Card, string Input, string Text, string Muted, string Line, string Accent, string AccentText,
    double CardRadius, double ButtonRadius, string Material, string Handle, bool Light = false)
{
    public override string ToString() => Name;
    // Keep the live MA indicator legible independently of theme accents or custom fader colors.
    public string MaRunningBackground => Material == "contrast" ? "#FFFFFF" : Light ? "#07583F" : "#A1F0C4";
    public string MaRunningForeground => Light ? "#FFFFFF" : "#10271C";
    public static IReadOnlyList<DeckTheme> All { get; } = [
        new("graphite", "石墨专业", "GRAPHITE", "#11171E", "#18212B", "#202B37", "#111B24", "#EAF2FA", "#9CABB9", "#344252", "#62D6BC", "#092A26", 10, 7, "matte", "bar"),
        new("amber", "控台琥珀", "AMBER", "#171411", "#251F17", "#332A1E", "#191710", "#FFEAC5", "#B9A98B", "#665237", "#FFB844", "#2E210C", 2, 2, "console", "console"),
        new("silver", "银白极简", "SILVER", "#E9EDF2", "#F8FAFC", "#FFFFFF", "#E9EEF4", "#203040", "#5D6E7E", "#C3CED9", "#247CA4", "#FFFFFF", 5, 5, "flat", "slim", true),
        new("warm", "暖灰编辑台", "WARM", "#EBE6DF", "#F9F4EB", "#FFFBF5", "#EEE6DA", "#47382C", "#786959", "#D5C6B6", "#A35F3D", "#FFFFFF", 16, 18, "paper", "round", true),
        new("frost", "冷调磨砂", "FROST", "#142938", "#203E51", "#294C61", "#193345", "#EDF9FF", "#A7C8DC", "#5A7D94", "#8FE2F5", "#113443", 18, 16, "glass", "glass"),
        new("metal", "工业金属", "METAL", "#23272A", "#353B40", "#474E54", "#242A2F", "#F0F2F4", "#B1BAC2", "#6B747C", "#D5E28A", "#303919", 2, 3, "metal", "metal"),
        new("soft", "柔和触控", "SOFT", "#DCE4EE", "#E9EFF7", "#F1F5FB", "#D8E2EF", "#35435D", "#63738E", "#BFCCDF", "#7361B8", "#FFFFFF", 22, 22, "soft", "pill", true),
        new("contrast", "黑白高对比", "CONTRAST", "#000000", "#0A0A0A", "#191919", "#000000", "#FFFFFF", "#DDDDDD", "#EEEEEE", "#FFFFFF", "#000000", 0, 0, "contrast", "wide")
    ];

    public static DeckTheme Find(string? id) => All.FirstOrDefault(x => x.Id == id) ?? All[0];
    private static readonly Dictionary<string, SolidColorBrush> BrushesByColor = [];
    public static SolidColorBrush Brush(string color)
    {
        lock (BrushesByColor)
        {
            if (BrushesByColor.TryGetValue(color, out var existing)) return existing;
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); b.Freeze();
            if (BrushesByColor.Count >= 512) BrushesByColor.Clear();
            BrushesByColor[color] = b; return b;
        }
    }
    public Brush Surface(string color)
    {
        if (Material is not ("glass" or "metal" or "soft")) return Brush(color);
        var original = (Color)ColorConverter.ConvertFromString(color);
        static byte Up(byte c, int n) => (byte)Math.Clamp(c + n, 0, 255);
        var lighter = Color.FromRgb(Up(original.R, Material == "metal" ? 18 : 8), Up(original.G, 14), Up(original.B, 17));
        var b = new LinearGradientBrush(lighter, original, new Point(0, 0), new Point(0, 1)); b.Freeze(); return b;
    }
    public void Apply(Window window)
    {
        window.Background = Brush(Background); window.Foreground = Brush(Text);
        window.FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"); window.FontSize = 13;
        var borderWidth = Material == "contrast" ? 2 : 1;
        var xml = $$"""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="Button">
            <Setter Property="Foreground" Value="{{Text}}"/><Setter Property="Background" Value="{{Card}}"/>
            <Setter Property="BorderBrush" Value="{{Line}}"/><Setter Property="BorderThickness" Value="{{borderWidth}}"/>
            <Setter Property="Padding" Value="14,8"/><Setter Property="MinHeight" Value="38"/><Setter Property="Cursor" Value="Hand"/>
            <Setter Property="HorizontalContentAlignment" Value="Center"/><Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
              <Border x:Name="shell" CornerRadius="{{ButtonRadius}}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Background="{TemplateBinding Background}">
                <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="shell" Property="BorderBrush" Value="{{Accent}}"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="shell" Property="Opacity" Value="0.68"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="shell" Property="BorderBrush" Value="{{Accent}}"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter TargetName="shell" Property="Opacity" Value="0.48"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="TextBox">
            <Setter Property="Foreground" Value="{{Text}}"/><Setter Property="Background" Value="{{Input}}"/><Setter Property="CaretBrush" Value="{{Accent}}"/>
            <Setter Property="BorderBrush" Value="{{Line}}"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="10,6"/>
            <Setter Property="MinHeight" Value="38"/><Setter Property="VerticalContentAlignment" Value="Center"/><Setter Property="SelectionBrush" Value="{{Accent}}"/>
          </Style>
          <Style TargetType="PasswordBox">
            <Setter Property="Foreground" Value="{{Text}}"/><Setter Property="Background" Value="{{Input}}"/><Setter Property="CaretBrush" Value="{{Accent}}"/>
            <Setter Property="BorderBrush" Value="{{Line}}"/><Setter Property="Padding" Value="10,6"/><Setter Property="MinHeight" Value="38"/><Setter Property="VerticalContentAlignment" Value="Center"/>
          </Style>
          <Style TargetType="CheckBox"><Setter Property="Foreground" Value="{{Text}}"/><Setter Property="VerticalContentAlignment" Value="Center"/><Setter Property="MinHeight" Value="32"/></Style>
          <Style TargetType="ComboBox">
            <Setter Property="Foreground" Value="{{Text}}"/><Setter Property="Background" Value="{{Input}}"/><Setter Property="BorderBrush" Value="{{Line}}"/>
            <Setter Property="Padding" Value="10,6"/><Setter Property="MinHeight" Value="38"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
              <Grid>
                <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}" ClickMode="Press">
                  <ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="{{Input}}" BorderBrush="{{Line}}" BorderThickness="1" CornerRadius="{{ButtonRadius}}"><TextBlock Text="⌄" Foreground="{{Text}}" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,12,0"/></Border></ControlTemplate></ToggleButton.Template>
                </ToggleButton>
                <ContentPresenter IsHitTestVisible="False" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}" Margin="12,7,35,7" VerticalAlignment="Center"/>
                <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False" PopupAnimation="Fade">
                  <Border Background="{{Panel}}" BorderBrush="{{Line}}" BorderThickness="1" MinWidth="{TemplateBinding ActualWidth}" MaxHeight="{TemplateBinding MaxDropDownHeight}" Padding="4"><ScrollViewer CanContentScroll="True" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled"><ItemsPresenter/></ScrollViewer></Border>
                </Popup>
              </Grid>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="ComboBoxItem"><Setter Property="Foreground" Value="{{Text}}"/><Setter Property="Padding" Value="10,8"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem"><Border x:Name="b" Background="Transparent" Padding="{TemplateBinding Padding}"><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="b" Property="Background" Value="{{Card}}"/></Trigger><Trigger Property="IsSelected" Value="True"><Setter TargetName="b" Property="Background" Value="{{Accent}}"/><Setter Property="Foreground" Value="{{AccentText}}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="ToolTip"><Setter Property="Foreground" Value="{{Text}}"/><Setter Property="Background" Value="{{Panel}}"/><Setter Property="BorderBrush" Value="{{Line}}"/></Style>
        </ResourceDictionary>
        """;
        var resources = (ResourceDictionary)XamlReader.Parse(xml);
        ThemedControls.AddTo(resources, this);
        resources["LightDeck.Theme"] = this;
        window.Resources = resources;
        YideckWindowChrome.Apply(window, this);
    }
}
