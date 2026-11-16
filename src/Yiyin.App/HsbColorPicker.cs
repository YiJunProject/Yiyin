using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LightDeck.App;

/// <summary>A local editing control: it never receives an executor or a device client.</summary>
public sealed class HsbColorPicker : Grid
{
    private readonly HueSurface _surface;
    private readonly TextBox _hue;
    private readonly TextBox _saturation;
    private readonly TextBox _brightness;
    private readonly TextBox _hex;
    private readonly Border _preview;
    private readonly TextBlock _previewText;
    private bool _syncing;
    private double _h, _s, _b;
    public string ColorHex { get; private set; } = "#000000";
    public event Action<string>? ColorChanged;
    public bool IsValid => ReadHsb(out _, out _, out _) && Regex.IsMatch(_hex.Text.Trim(), "^#[0-9a-fA-F]{6}$");

    public HsbColorPicker(DeckTheme theme, string color)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(206) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        ColumnDefinitions.Add(new ColumnDefinition());
        _surface = new HueSurface { Width = 206, Height = 206, VerticalAlignment = VerticalAlignment.Top, Focusable = true, Cursor = Cursors.Cross };
        AutomationProperties.SetAutomationId(_surface, "fader-color-wheel");
        AutomationProperties.SetName(_surface, "色相环与饱和度明度方块；也可使用右侧数值输入");
        _surface.Changed += (h, s, b) => { _h = h; _s = s; _b = b; UpdateColor(); };
        Children.Add(_surface);
        var controls = new StackPanel(); SetColumn(controls, 2); Children.Add(controls);
        TextBox Entry(string label, string id)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = label, Foreground = DeckTheme.Brush(theme.Muted), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
            var input = new TextBox { MinWidth = 0, MinHeight = 32, Padding = new Thickness(8, 4, 8, 4), MaxLength = 9 };
            AutomationProperties.SetAutomationId(input, id); AutomationProperties.SetName(input, label);
            SetColumn(input, 1); row.Children.Add(input); controls.Children.Add(row); return input;
        }
        _hue = Entry("H 色相 °", "fader-color-h");
        _saturation = Entry("S 饱和 %", "fader-color-s");
        _brightness = Entry("B 明度 %", "fader-color-b");
        _hex = Entry("Hex", "fader-color"); _hex.MaxLength = 7;
        _previewText = new TextBlock { Text = "整面板预览", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        _preview = new Border { Child = _previewText, Height = 47, Margin = new Thickness(0, 1, 0, 0), CornerRadius = new CornerRadius(theme.CardRadius), BorderBrush = DeckTheme.Brush(theme.Line), BorderThickness = new Thickness(1) };
        AutomationProperties.SetAutomationId(_preview, "fader-color-preview"); controls.Children.Add(_preview);
        SetColor(color);
        void HsbChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncing || !ReadHsb(out double h, out double s, out double b)) return;
            _h = h == 360 ? 0 : h; _s = s / 100; _b = b / 100; UpdateColor(syncHsb: false);
        }
        _hue.TextChanged += HsbChanged; _saturation.TextChanged += HsbChanged; _brightness.TextChanged += HsbChanged;
        _hex.TextChanged += (_, _) =>
        {
            if (_syncing || !Regex.IsMatch(_hex.Text.Trim(), "^#[0-9a-fA-F]{6}$")) return;
            SetColor(_hex.Text.Trim(), syncHex: false);
        };
    }

    public void SetColor(string color) => SetColor(color, syncHex: true);
    private void SetColor(string color, bool syncHex)
    {
        if (!Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$")) color = "#275E74";
        var value = (Color)ColorConverter.ConvertFromString(color);
        double r = value.R / 255d, g = value.G / 255d, blue = value.B / 255d;
        double max = Math.Max(r, Math.Max(g, blue)), min = Math.Min(r, Math.Min(g, blue)), delta = max - min;
        // Keep the last hue for greys so increasing saturation produces the expected hue.
        if (delta > 0) _h = ((max == r ? (g - blue) / delta : max == g ? (blue - r) / delta + 2 : (r - g) / delta + 4) * 60 + 360) % 360;
        _s = max == 0 ? 0 : delta / max; _b = max;
        UpdateColor(syncHex: syncHex);
    }
    private bool ReadHsb(out double h, out double s, out double b)
    {
        static bool Parse(string text, double max, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value >= 0 && value <= max;
        bool validH = Parse(_hue.Text, 360, out h), validS = Parse(_saturation.Text, 100, out s), validB = Parse(_brightness.Text, 100, out b);
        return validH && validS && validB;
    }
    private void UpdateColor(bool syncHsb = true, bool syncHex = true)
    {
        _syncing = true;
        try
        {
            var color = FromHsb(_h, _s, _b); ColorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            if (syncHsb) { _hue.Text = _h.ToString("0.##", CultureInfo.InvariantCulture); _saturation.Text = (_s * 100).ToString("0.##", CultureInfo.InvariantCulture); _brightness.Text = (_b * 100).ToString("0.##", CultureInfo.InvariantCulture); }
            if (syncHex) _hex.Text = ColorHex;
            _preview.Background = new SolidColorBrush(color);
            _previewText.Foreground = color.R * .299 + color.G * .587 + color.B * .114 > 155 ? Brushes.Black : Brushes.White;
            _surface.SetHsb(_h, _s, _b);
        }
        finally { _syncing = false; }
        ColorChanged?.Invoke(ColorHex);
    }
    internal static Color FromHsb(double h, double s, double b)
    {
        double c = b * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = b - c;
        (double r, double g, double blue) = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((blue + m) * 255));
    }

    private sealed class HueSurface : FrameworkElement
    {
        private double _h, _s, _b;
        private bool _dragHue;
        private readonly DrawingGroup _ring = MakeRing();
        private static readonly Rect Square = new(51, 51, 104, 104);
        public event Action<double, double, double>? Changed;
        public void SetHsb(double h, double s, double b) { _h = h; _s = s; _b = b; InvalidateVisual(); }
        private static Point Radial(double angle, double radius) => new(103 + Math.Cos(angle * Math.PI / 180) * radius, 103 + Math.Sin(angle * Math.PI / 180) * radius);
        private static DrawingGroup MakeRing()
        {
            var group = new DrawingGroup(); using (var drawing = group.Open())
            for (int hue = 0; hue < 360; hue += 2)
            {
                var shape = new StreamGeometry(); using (var context = shape.Open())
                {
                    context.BeginFigure(Radial(hue, 99), true, true);
                    context.LineTo(Radial(hue + 2.3, 99), true, false); context.LineTo(Radial(hue + 2.3, 80), true, false); context.LineTo(Radial(hue, 80), true, false);
                }
                shape.Freeze(); drawing.DrawGeometry(new SolidColorBrush(FromHsb(hue, 1, 1)), null, shape);
            }
            group.Freeze(); return group;
        }
        protected override void OnRender(DrawingContext drawing)
        {
            drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 206, 206));
            drawing.DrawDrawing(_ring);
            drawing.DrawRectangle(new LinearGradientBrush(Colors.White, FromHsb(_h, 1, 1), new Point(0, 0), new Point(1, 0)), null, Square);
            drawing.DrawRectangle(new LinearGradientBrush(Colors.Transparent, Colors.Black, new Point(0, 0), new Point(0, 1)), new Pen(Brushes.Gray, 1), Square);
            Marker(drawing, Radial(_h, 89.5)); Marker(drawing, new Point(Square.Left + _s * Square.Width, Square.Top + (1 - _b) * Square.Height));
        }
        private static void Marker(DrawingContext drawing, Point point)
        {
            drawing.DrawEllipse(null, new Pen(Brushes.Black, 4), point, 5, 5); drawing.DrawEllipse(null, new Pen(Brushes.White, 2), point, 5, 5);
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var p = e.GetPosition(this); double radius = Math.Sqrt(Math.Pow(p.X - 103, 2) + Math.Pow(p.Y - 103, 2));
            if (!Square.Contains(p) && (radius < 77 || radius > 103)) return;
            Focus(); _dragHue = !Square.Contains(p); CaptureMouse(); ChangeAt(p); e.Handled = true;
        }
        protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) ChangeAt(e.GetPosition(this)); }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { if (IsMouseCaptured) { ChangeAt(e.GetPosition(this)); ReleaseMouseCapture(); e.Handled = true; } }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
            _h = (_h + (e.Key is Key.Left or Key.Down ? -1 : 1) + 360) % 360; Changed?.Invoke(_h, _s, _b); e.Handled = true;
        }
        private void ChangeAt(Point point)
        {
            if (_dragHue) _h = (Math.Atan2(point.Y - 103, point.X - 103) * 180 / Math.PI + 360) % 360;
            else { _s = Math.Clamp((point.X - Square.Left) / Square.Width, 0, 1); _b = 1 - Math.Clamp((point.Y - Square.Top) / Square.Height, 0, 1); }
            Changed?.Invoke(_h, _s, _b);
        }
    }
}
