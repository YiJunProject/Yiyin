using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;
using LightDeck.Core;

namespace LightDeck.App;

public sealed partial class RackWorkspaceControl
{
    private const double PreferredStripWidth = 150;
    private const double MinimumStripWidth = 132;
    private const double MinimumFaderHeight = 120;
    private readonly List<StripLayout> _stripLayouts = [];
    private readonly List<FrameworkElement> _groupHeadings = [];
    private readonly Dictionary<RackModule, double> _effectiveHeights = [];
    private readonly Dictionary<RackModule, double> _minimumHeights = [];
    private double _optionalHeightBudget;
    private readonly double _originalWindowMinimum;
    private readonly double _originalWindowMinimumWidth;
    private readonly double _originalWindowMaximumWidth;
    private double _appliedWindowMinimum;
    private double _appliedWindowMinimumWidth;
    private double _appliedWindowMaximumWidth = double.PositiveInfinity;
    private Border _widthNotice = null!;
    private TextBlock _widthNoticeText = null!;
    private Rect _lastConstraintWorkArea = Rect.Empty;
    private bool _sizing;
    private sealed record StripLayout(Border Frame, StackPanel Header, StackPanel Footer);

    private void AddWidthNotice(Grid body)
    {
        var content = new StackPanel { MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var heading = Label("显示宽度不足", 20); heading.FontWeight = FontWeights.SemiBold; content.Children.Add(heading);
        _widthNoticeText = Label("", 13, true); _widthNoticeText.TextWrapping = TextWrapping.Wrap; _widthNoticeText.Margin = new Thickness(0, 12, 0, 18); content.Children.Add(_widthNoticeText);
        var adjust = Button("调整可见轨道", "rack-width-visibility", () => new RackVisibilityDialog(_owner, _theme, _host).ShowDialog(), true); adjust.HorizontalAlignment = HorizontalAlignment.Left; content.Children.Add(adjust);
        _widthNotice = PanelFrame(content, 24); _widthNotice.Visibility = Visibility.Collapsed; _widthNotice.Margin = new Thickness(4, 0, 8, 0);
        AutomationProperties.SetAutomationId(_widthNotice, "rack-width-notice"); AutomationProperties.SetAutomationId(_widthNoticeText, "rack-width-details"); body.Children.Add(_widthNotice);
        _owner.LocationChanged += OwnerLayoutChanged;
    }

    private void OwnerLayoutChanged(object? sender, EventArgs e) { if (!_disposed && !_sizing) ApplyModuleHeights(); }

    private void UpdateResponsiveLayout()
    {
        if (_disposed || _sizing || _mixer.ActualWidth <= 0 || _mixer.ActualHeight <= 0) return;
        _sizing = true;
        try
        {
            // Use the outer viewport allocation, not the previous scroll extent.
            // Scrollbars and track widths are chosen together, once per resize.
            var width = Math.Max(0, _mixer.ActualWidth - _mixer.Padding.Left - _mixer.Padding.Right);
            var height = Math.Max(0, _mixer.ActualHeight - _mixer.Padding.Top - _mixer.Padding.Bottom);
            var count = _stripLayouts.Count;
            var gaps = count * 5d + Math.Max(0, _groups.Children.OfType<DockPanel>().Count() - 1) * 7d + _groups.Children.OfType<DockPanel>().Sum(g => g.Margin.Left + g.Margin.Right);
            var minimumWidth = count * PreferredStripWidth + gaps;
            if (minimumWidth > width) height = Math.Max(0, height - SystemParameters.HorizontalScrollBarHeight);
            var fixedHeight = FixedStripHeight();
            var outputMinimum = _host.Local.ShowRackOutput ? ConnectionMinimumHeight : RackRowAppearance.HeaderHeight;
            // Insert/send may shrink to their header by dragging. Window resizing
            // preserves that choice; an expanded section retains at least one row.
            _minimumHeights[RackModule.Insert] = Math.Min(InsertMinimumHeight, Math.Max(RackRowAppearance.HeaderHeight, ModuleHeight(RackModule.Insert)));
            _minimumHeights[RackModule.Send] = Math.Min(ConnectionMinimumHeight, Math.Max(RackRowAppearance.HeaderHeight, ModuleHeight(RackModule.Send)));
            _minimumHeights[RackModule.Output] = outputMinimum;
            _minimumHeights[RackModule.Fader] = MinimumFaderHeight;
            var minimumHeight = count == 0 ? 0 : fixedHeight + _minimumHeights.Values.Sum();
            var vertical = minimumHeight > height + .1;
            if (vertical) width = Math.Max(0, width - SystemParameters.VerticalScrollBarWidth);
            _mixer.HorizontalScrollBarVisibility = minimumWidth > width ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            _mixer.VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Visible : ScrollBarVisibility.Hidden;
            // Side panels consume viewport space, never resize the channel strips.
            var trackWidth = PreferredStripWidth;
            // Round down to a physical pixel so accumulated rounding never creates
            // an unintended extra scrollbar at a narrow width / non-100% DPI.
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            trackWidth = Math.Max(MinimumStripWidth, Math.Floor(trackWidth * dpi) / dpi);
            foreach (var strip in _stripLayouts) strip.Frame.Width = trackWidth;
            _groups.MinWidth = 0; _groups.MinHeight = 0;
            _groups.Width = Math.Max(width, count * trackWidth + gaps);
            _groups.Height = Math.Max(Math.Floor(height * dpi) / dpi, minimumHeight);

            var insert = Math.Max(_minimumHeights[RackModule.Insert], ModuleHeight(RackModule.Insert));
            var send = Math.Max(_minimumHeights[RackModule.Send], ModuleHeight(RackModule.Send));
            var output = Math.Max(outputMinimum, ModuleHeight(RackModule.Output));
            var fader = Math.Max(MinimumFaderHeight, ModuleHeight(RackModule.Fader));
            var optional = insert + send + output + fader - _minimumHeights.Values.Sum();
            var budget = Math.Max(0, height - minimumHeight);
            _optionalHeightBudget = budget;
            var scale = optional > 0 ? Math.Min(1, budget / optional) : 1;
            // These are display sizes only. Window resizing never saves over the
            // user's preferred module sizes; expanding restores those preferences.
            _effectiveHeights[RackModule.Insert] = _minimumHeights[RackModule.Insert] + (insert - _minimumHeights[RackModule.Insert]) * scale;
            _effectiveHeights[RackModule.Send] = _minimumHeights[RackModule.Send] + (send - _minimumHeights[RackModule.Send]) * scale;
            _effectiveHeights[RackModule.Output] = outputMinimum + (output - outputMinimum) * scale;
            _effectiveHeights[RackModule.Fader] = MinimumFaderHeight + (fader - MinimumFaderHeight) * scale;
            CaptureTabletLayout(fixedHeight);

            if (IsLoaded && IsVisible)
            {
                var outside = Math.Max(0, _owner.ActualHeight - _mixer.ActualHeight);
                var required = outside + minimumHeight + 2;
                var workHeight = YideckWindowChrome.WorkAreaHeight(_owner);
                _appliedWindowMinimum = Math.Max(_originalWindowMinimum, Math.Min(Math.Ceiling(required), workHeight));
                _owner.MinHeight = _appliedWindowMinimum;
                _widthNotice.Visibility = Visibility.Collapsed;
                _mixer.Visibility = Visibility.Visible;
                _appliedWindowMinimumWidth = _originalWindowMinimumWidth; _owner.MinWidth = _originalWindowMinimumWidth;

            }
        }
        finally { _sizing = false; }
    }

    private double FixedStripHeight()
    {
        var moduleElements = _moduleHeights.Where(m => m.Kind != RackModule.Fader).Select(m => m.Element).ToHashSet();
        var fixedHeight = 0d;
        foreach (var strip in _stripLayouts)
        {
            var total = strip.Frame.Padding.Top + strip.Frame.Padding.Bottom + strip.Frame.BorderThickness.Top + strip.Frame.BorderThickness.Bottom;
            foreach (var child in strip.Header.Children.OfType<FrameworkElement>())
            {
                if (moduleElements.Contains(child)) { total += child.Margin.Top + child.Margin.Bottom; continue; }
                child.Measure(new Size(MinimumStripWidth - 16, double.PositiveInfinity));
                total += child.DesiredSize.Height;
            }
            strip.Footer.Measure(new Size(MinimumStripWidth - 16, double.PositiveInfinity));
            total += strip.Footer.DesiredSize.Height;
            fixedHeight = Math.Max(fixedHeight, total);
        }
        var headingHeight = 0d;
        foreach (var heading in _groupHeadings)
        {
            heading.Measure(new Size(MinimumStripWidth, double.PositiveInfinity));
            headingHeight = Math.Max(headingHeight, heading.DesiredSize.Height);
        }
        return Math.Ceiling(fixedHeight + headingHeight);
    }

    private void RestoreWindowMinimum()
    {
        if (Math.Abs(_owner.MinHeight - _appliedWindowMinimum) < .1) _owner.MinHeight = _originalWindowMinimum;
        if (Math.Abs(_owner.MinWidth - _appliedWindowMinimumWidth) < .1) _owner.MinWidth = _originalWindowMinimumWidth;
        if (_owner.MaxWidth == _appliedWindowMaximumWidth) _owner.MaxWidth = _originalWindowMaximumWidth;
    }
}
