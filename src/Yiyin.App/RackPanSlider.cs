using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace LightDeck.App;

// Value stays normalized pan; the wedge is only a view of the existing audio value.
internal sealed class RackPanSlider : Slider
{
    private const double Edge = 5;
    public RackPanSlider()
    {
        Minimum = -1; Maximum = 1; SmallChange = .01; LargeChange = .1;
        MinHeight = 26; Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 57)); ToolTip = "拖动调整声像；双击回到中央 C";
        Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
              <Grid Background="Transparent">
                <Track x:Name="PART_Track" Orientation="Horizontal" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}">
                  <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Opacity="0" Focusable="False"/></Track.DecreaseRepeatButton>
                  <Track.Thumb><Thumb Width="10"><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="Transparent"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                  <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Opacity="0" Focusable="False"/></Track.IncreaseRepeatButton>
                </Track>
              </Grid>
            </ControlTemplate>
            """);
    }
    protected override void OnValueChanged(double oldValue, double newValue) { base.OnValueChanged(oldValue, newValue); InvalidateVisual(); }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ForegroundProperty || e.Property == IsEnabledProperty) InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var right = Math.Max(Edge, ActualWidth - Edge); var center = (Edge + right) / 2;
        var baseline = Math.Max(5, ActualHeight - 5); var pan = Math.Clamp(Value, -1, 1);
        dc.PushOpacity(IsEnabled ? 1 : .4);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(53, 63, 75)), 3), new Point(Edge, baseline), new Point(right, baseline));
        if (pan != 0)
        {
            var side = pan < 0 ? Edge : right;
            var peak = new Point(side, baseline - Math.Abs(pan) * Math.Max(0, baseline - 3));
            var wedge = new StreamGeometry();
            using (var path = wedge.Open()) { path.BeginFigure(new Point(center, baseline), true, true); path.LineTo(new Point(side, baseline), true, false); path.LineTo(peak, true, false); }
            wedge.Freeze(); dc.PushOpacity(.85); dc.DrawGeometry(Foreground, null, wedge); dc.Pop();
            dc.DrawLine(new Pen(Foreground, 1.8), new Point(center, baseline), peak);
            dc.DrawLine(new Pen(Foreground, 1.8), new Point(center, baseline), new Point(side, baseline));
        }
        dc.DrawLine(new Pen(Foreground, 1), new Point(center, baseline - 4), new Point(center, baseline + 3));
        dc.Pop();
    }
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (!IsEnabled) return;
        if (e.ClickCount == 2) { if (IsMouseCaptured) ReleaseMouseCapture(); Focus(); SetCurrentValue(ValueProperty, 0d); e.Handled = true; return; }
        Focus(); CaptureMouse(); SetPointerValue(e.GetPosition(this).X); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        SetPointerValue(e.GetPosition(this).X); e.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture(); e.Handled = true;
    }
    private void SetPointerValue(double x) => SetCurrentValue(ValueProperty, Math.Clamp((x - Edge) / Math.Max(1, ActualWidth - Edge * 2), 0, 1) * 2 - 1);
}

internal static class RackSliderWheel
{
    public static void HandlePreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = Parent(node))
        {
            if (node is TextBox) return;
            if (node is Slider slider)
            {
                if (slider.IsEnabled) slider.SetCurrentValue(Slider.ValueProperty, Math.Clamp(Math.Round(slider.Value + e.Delta / 120d * slider.SmallChange, 6), slider.Minimum, slider.Maximum));
                e.Handled = true; return;
            }
            if (ReferenceEquals(node, sender)) return;
        }
    }
    private static DependencyObject? Parent(DependencyObject node) => node is Visual ? VisualTreeHelper.GetParent(node) : node is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(node);
}
