using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace LightDeck.App;

public sealed partial class RackWorkspaceControl
{
    private FrameworkElement EditableNumber(TextBlock display, string id, Func<double> current, Action<double> apply, double minimum = -90, double maximum = 12, bool decibels = true)
    {
        var holder = new Grid { MinWidth = 40, Background = System.Windows.Media.Brushes.Transparent }; holder.Children.Add(display);
        var input = new TextBox { Visibility = Visibility.Collapsed, MinHeight = 0, Height = 24, MinWidth = 40, FontSize = 11, Padding = new Thickness(2, 1, 2, 1), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(display, id + "-display"); AutomationProperties.SetAutomationId(input, id); AutomationProperties.SetName(input, decibels ? "增益 dB" : "声像 -100左 / 100右"); holder.Children.Add(input);
        var help = decibels ? "悬停滚轮每格调整 0.1 dB；双击输入 dB；Enter 保存，Esc 或失焦取消；-inf 静音。" : "悬停滚轮每格调整 1；双击输入声像 -100（左）至100（右），0居中；Enter 保存，Esc 或失焦取消。";
        display.ToolTip = input.ToolTip = help; display.Cursor = Cursors.IBeam;
        void Close() { input.Visibility = Visibility.Collapsed; display.Visibility = Visibility.Visible; }
        holder.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            if (_busy || _disposed || !holder.IsEnabled || e.Delta == 0) return;
            var value = current();
            if (input.IsVisible)
            {
                var text = input.Text.Trim().Replace('−', '-');
                if (decibels && (text.Equals("-inf", StringComparison.OrdinalIgnoreCase) || text == "-∞")) value = minimum;
                else if ((!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) || !double.IsFinite(value)) return;
            }
            value = Math.Clamp(Math.Round(value + e.Delta / 120d * (decibels ? .1 : 1), 6), minimum, maximum);
            if (input.IsVisible) { input.Text = value.ToString("0.###", CultureInfo.InvariantCulture); input.ClearValue(Control.BorderBrushProperty); input.ToolTip = help; }
            else apply(value);
        };
        display.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2 || _busy || _disposed) return; var value = current(); input.Text = decibels && value <= minimum ? "-inf" : value.ToString("0.###", CultureInfo.InvariantCulture); input.ClearValue(Control.BorderBrushProperty); input.ToolTip = help; display.Visibility = Visibility.Hidden; input.Visibility = Visibility.Visible; input.Focus(); input.SelectAll(); e.Handled = true;
        };
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
            if (e.Key != Key.Enter) return; e.Handled = true; var text = input.Text.Trim().Replace('−', '-');
            double value;
            if (decibels && (text.Equals("-inf", StringComparison.OrdinalIgnoreCase) || text == "-∞")) value = minimum;
            else if ((!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) || double.IsNaN(value)) { input.BorderBrush = DeckTheme.Brush(_theme.Light ? "#B13A32" : "#FFB39B"); input.ToolTip = "请输入有效数字" + (decibels ? "或 -inf" : "") + "；Esc 取消。"; input.SelectAll(); return; }
            value = Math.Clamp(value, minimum, maximum); Close(); apply(value);
        };
        input.LostKeyboardFocus += (_, _) => { if (input.IsVisible) Close(); };
        return holder;
    }
    private double CurrentGain(Guid trackId) => (_pending.GetValueOrDefault(trackId) ?? _host.Project.FindTrack(trackId))?.GainDb ?? 0;
}
