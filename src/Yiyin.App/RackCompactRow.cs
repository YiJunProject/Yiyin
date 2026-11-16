using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using LightDeck.Core;

namespace LightDeck.App;

/// <summary>Shared one-line rack visual. Owners retain input authorization and commands.</summary>
internal sealed class RackCompactRow : Border
{
    internal RackCompactRow(double pitch, Brush color, DeckTheme theme, FrameworkElement name,
        FrameworkElement? power = null, FrameworkElement? prefix = null, FrameworkElement? number = null, Slider? gain = null)
    {
        Height = pitch; Padding = new(3, 1, 2, 1); Margin = new(1, 0, 1, 0);
        Background = DeckTheme.Brush(theme.Card); BorderBrush = color; BorderThickness = new(3, 0, 0, 1);
        CornerRadius = new(2); ClipToBounds = true;
        var grid = new Grid(); Child = grid;
        grid.ColumnDefinitions.Add(new() { Width = prefix is null ? new(0) : new(Math.Min(28, pitch - 2)) });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = number is null ? new(0) : new(Math.Min(50, Math.Max(36, pitch * 1.45))) });
        grid.ColumnDefinitions.Add(new() { Width = power is null ? new(0) : new(Math.Min(32, pitch)) });
        // Increasing row thickness must not consume the target name on narrow strips.
        // Keep the same compact horizontal budget on all four rack surfaces.
        SizeChanged += (_, _) =>
        {
            var narrow = ActualWidth < 164;
            grid.ColumnDefinitions[0].Width = new(prefix is null ? 0 : narrow ? 18 : Math.Min(28, pitch - 2));
            grid.ColumnDefinitions[2].Width = new(number is null ? 0 : narrow ? 30 : Math.Min(50, Math.Max(36, pitch * 1.45)));
            grid.ColumnDefinitions[3].Width = new(power is null ? 0 : narrow ? 22 : Math.Min(32, pitch));
        };
        void Add(FrameworkElement? element, int column)
        {
            if (element is null) return;
            element.MinHeight = 0; element.Height = double.NaN; element.Width = double.NaN; element.MinWidth = 0; element.Margin = new(1, 0, 1, 0);
            element.VerticalAlignment = VerticalAlignment.Stretch;
            if (element is Control c) { c.Padding = new(0); c.HorizontalContentAlignment = HorizontalAlignment.Stretch; c.VerticalContentAlignment = VerticalAlignment.Center; }
            Grid.SetColumn(element, column); grid.Children.Add(element);
        }
        Add(prefix, 0); Add(number, 2); Add(power, 3);
        var center = new Grid { ClipToBounds = true }; Grid.SetColumn(center, 1); grid.Children.Add(center);
        name.MinWidth = 0; name.MinHeight = 0; name.Height = double.NaN; name.Margin = new(1, 0, 1, gain is null ? 0 : 8);
        name.VerticalAlignment = VerticalAlignment.Stretch;
        if (name is Control nc) { nc.Padding = new(1, 0, 1, 0); nc.HorizontalContentAlignment = HorizontalAlignment.Stretch; nc.VerticalContentAlignment = VerticalAlignment.Center; }
        center.Children.Add(name);
        if (gain is not null)
        {
            gain.MinHeight = 0; gain.Height = 9; gain.Margin = new(1, 0, 1, 0); gain.VerticalAlignment = VerticalAlignment.Bottom;
            gain.Foreground = color; gain.Template = RangeTemplate(); center.Children.Add(gain);
        }
    }
    internal static RackRowText Text(string text, double pitch, Brush foreground) => new()
    { Text = text, Foreground = foreground, MaximumSize = Math.Clamp(pitch * .43, 10, 18), VerticalAlignment = VerticalAlignment.Stretch, TextAlignment = TextAlignment.Left };
    private static ControlTemplate RangeTemplate() => (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
          <Grid Background="Transparent"><Border Height="3" Background="#60606060" VerticalAlignment="Center"/>
            <Track x:Name="PART_Track" Orientation="Horizontal" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}">
              <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Background="{TemplateBinding Foreground}" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Height="3" Background="{TemplateBinding Background}"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
              <Track.Thumb><Thumb Width="6" Height="9" Background="{TemplateBinding Foreground}"><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="{TemplateBinding Background}" CornerRadius="2"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
              <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Opacity="0" Focusable="False"/></Track.IncreaseRepeatButton>
            </Track>
          </Grid>
        </ControlTemplate>
        """);

}

internal sealed class RackRowText : TextBlock
{
    static RackRowText() => TextProperty.OverrideMetadata(typeof(RackRowText), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure, (o, _) => ((RackRowText)o).Fit()));
    internal double MaximumSize { get; init; } = 12;
    private bool _fitting;
    public RackRowText() { ClipToBounds = true; TextWrapping = TextWrapping.NoWrap; TextTrimming = TextTrimming.CharacterEllipsis; SizeChanged += (_, _) => Fit(); }
    private void Fit()
    {
        if (_fitting || ActualWidth < 1 || ActualHeight < 1) return; _fitting = true;
        try
        {
            // A centered ContentPresenter sizes to this text. Using its current
            // height here recursively shrinks the font on every SizeChanged.
            // Row pitch defines the vertical budget; only available width fits it.
            var low = 8d; var high = MaximumSize;
            for (var i = 0; i < 8; i++)
            {
                var mid = (low + high) / 2; var measure = new FormattedText(Text ?? "", CultureInfo.CurrentUICulture, FlowDirection, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), mid, Foreground ?? Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                if (measure.WidthIncludingTrailingWhitespace <= ActualWidth) low = mid; else high = mid;
            }
            FontSize = Math.Max(8, low);
        }
        finally { _fitting = false; }
    }
}
