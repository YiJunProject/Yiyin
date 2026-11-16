using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;

namespace LightDeck.App;

// One coordinate system for the fader thumb centre, tick labels and signal meters.
internal static class RackDbScale
{
    public const double Minimum = -90, Maximum = 12, ThumbHeight = 38;
    // Equal travel per dB across the working range, on both sides of unity.
    // Low levels remain compressed; every consumer uses this same transform.
    private static readonly (double Db, double Position)[] Points = [(-90, 0), (-60, .05), (-40, .17), (-30, .28), (-20, .42), (-10, .56), (0, .76), (12, 1)];
    public static double Fraction(double db)
    {
        db = Math.Clamp(db, Minimum, Maximum);
        for (var i = 1; i < Points.Length; i++) if (db <= Points[i].Db) return Points[i - 1].Position + (db - Points[i - 1].Db) / (Points[i].Db - Points[i - 1].Db) * (Points[i].Position - Points[i - 1].Position);
        return 1;
    }
    public static double FromFraction(double position)
    {
        position = Math.Clamp(position, 0, 1);
        for (var i = 1; i < Points.Length; i++) if (position <= Points[i].Position) return Points[i - 1].Db + (position - Points[i - 1].Position) / (Points[i].Position - Points[i - 1].Position) * (Points[i].Db - Points[i - 1].Db);
        return Maximum;
    }
    public static double Db(double amplitude) => double.IsFinite(amplitude) && amplitude > 0 ? Math.Max(Minimum, 20 * Math.Log10(amplitude)) : Minimum;
    public static double Y(double db, double height) => ThumbHeight / 2 + (1 - Fraction(db)) * Math.Max(0, height - ThumbHeight);
    public static readonly double[] Ticks = [12, 6, 0, -5, -10, -20, -30, -40, -60, -90];
    public static ControlTemplate FaderTemplate() => (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Slider">
          <Grid Background="Transparent" MinWidth="36">
            <Border Width="10" Margin="0,19" Background="#090D12" BorderBrush="#697583" BorderThickness="1" CornerRadius="4"/>
            <Track x:Name="PART_Track" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Orientation="Vertical" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}">
              <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Opacity="0" Focusable="False"/></Track.DecreaseRepeatButton>
              <Track.Thumb><Thumb Width="36" Height="38"><Thumb.Template><ControlTemplate TargetType="Thumb">
                <Grid>
                  <Border Background="#70000000" Margin="2,3,0,-2" CornerRadius="3"/>
                  <Border BorderBrush="#303C48" BorderThickness="1" CornerRadius="3" Margin="0,0,1,1">
                    <Border.Background><LinearGradientBrush StartPoint="0,0" EndPoint="0,1"><GradientStop Color="#F9FCFF" Offset="0"/><GradientStop Color="#B6C1CD" Offset="0.12"/><GradientStop Color="#E3E9F0" Offset="0.38"/><GradientStop Color="#8A97A7" Offset="0.49"/><GradientStop Color="#C9D3DE" Offset="0.55"/><GradientStop Color="#82909F" Offset="0.87"/><GradientStop Color="#455260" Offset="1"/></LinearGradientBrush></Border.Background>
                    <Grid>
                      <Border BorderBrush="#DCFFFFFF" BorderThickness="1,1,0,0" CornerRadius="2" Margin="1"/>
                      <StackPanel Margin="5,6,5,0" VerticalAlignment="Top"><Border Height="1" Background="#F4FFFFFF"/><Border Height="1" Background="#536270" Margin="0,2,0,0"/></StackPanel>
                      <Border Height="4" Background="#34414D" VerticalAlignment="Center" Margin="2,0"><Border Height="1" Background="#F2F7FA" VerticalAlignment="Bottom"/></Border>
                      <StackPanel Margin="5,0,5,6" VerticalAlignment="Bottom"><Border Height="1" Background="#F4FFFFFF"/><Border Height="1" Background="#536270" Margin="0,2,0,0"/></StackPanel>
                    </Grid>
                  </Border>
                </Grid>
              </ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
              <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Opacity="0" Focusable="False"/></Track.IncreaseRepeatButton>
            </Track>
          </Grid>
        </ControlTemplate>
        """);
}

// Slider.Value remains real dB for host updates, keyboard commands and automation.
// Only the template Track and pointer displacement use the shared nonlinear position.
internal sealed class RackGainSlider : Slider
{
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (Template.FindName("PART_Track", this) is not Track track) return;
        track.Minimum = 0; track.Maximum = 1;
        track.SetBinding(Track.ValueProperty, new Binding(nameof(Value)) { Source = this, Mode = BindingMode.OneWay, Converter = PositionConverter.Instance });
    }
    protected override void OnThumbDragDelta(DragDeltaEventArgs e)
    {
        var travel = Math.Max(1, ActualHeight - RackDbScale.ThumbHeight);
        SetCurrentValue(ValueProperty, RackDbScale.FromFraction(RackDbScale.Fraction(Value) - e.VerticalChange / travel)); e.Handled = true;
    }
    private sealed class PositionConverter : IValueConverter
    {
        public static readonly PositionConverter Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => RackDbScale.Fraction((double)value);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}

internal sealed class RackScaleView : FrameworkElement
{
    private readonly Brush _ink;
    public RackScaleView(Brush ink) { _ink = ink; IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var previousY = double.NegativeInfinity;
        foreach (var db in RackDbScale.Ticks)
        {
            var text = new FormattedText(db <= -90 ? "−∞" : db > 0 ? "+" + db : db.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, db == 0 ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), 9, _ink, dpi);
            var y = RackDbScale.Y(db, ActualHeight); dc.DrawLine(new Pen(_ink, db == 0 ? 1.5 : .5), new Point(0, y), new Point(5, y));
            var reserveBottomLabel = db != RackDbScale.Minimum && RackDbScale.Y(RackDbScale.Minimum, ActualHeight) - y < text.Height + 1;
            if (!reserveBottomLabel && (y - previousY >= text.Height + 1 || db is 0 or RackDbScale.Minimum)) { dc.DrawText(text, new Point(7, y - text.Height / 2)); previousY = y; }
        }
    }
}
