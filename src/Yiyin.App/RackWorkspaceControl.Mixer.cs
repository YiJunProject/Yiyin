using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using LightDeck.Core;

namespace LightDeck.App;

public sealed partial class RackWorkspaceControl
{
    private readonly Dictionary<Guid, SendControls> _stripSendControls = [];
    private readonly Dictionary<Guid, Button> _mainOutputControls = [];
    private readonly Dictionary<Guid, Button> _pluginBypassControls = [];
    private readonly Dictionary<Guid, Button> _slotBypassControls = [];
    private readonly Dictionary<Guid, Button> _preFaderControls = [];
    private readonly Dictionary<Guid, TextBlock> _insertLabels = [];
    private readonly Dictionary<Guid, Button> _insertButtons = [];
    private readonly Dictionary<Guid, SendControls> _outputControls = [];
    private string _librarySignature = "";
    private string _librarySearch = "";
    private string? _libraryVendor;
    private int _libraryScope;
    private static bool TrackEnabled(RackTrack track) => !track.Mute && (track.Kind is not (RackTrackKind.Mono or RackTrackKind.Stereo) || track.Monitor);
    private void PaintPower(Button button, bool on, string operation)
    {
        if (button.Tag is bool current && current == on && button.Content is System.Windows.Shapes.Path) return;
        button.Tag = on;
        PaintToggle(button, on); button.ToolTip = operation + (on ? "已开启，点击关闭" : "已关闭，点击开启"); AutomationProperties.SetName(button, button.ToolTip.ToString());
        button.Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M8,1 L8,7 M4,3 A6,6 0 1 0 12,3"), Stroke = button.Foreground, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 15, Height = 15, Stretch = Stretch.Uniform };
    }
    private ComboBox RouteChoice(string label, string id, Guid? selected, IEnumerable<RackRouteChoice> available, Action<Guid?> apply)
    {
        var choices = new[] { new RackRouteChoice(null, "未分配") }.Concat(available).Select(c => c with { Name = label.Length > 0 ? label + " · " + c.Name : c.Name }).ToArray();
        var combo = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(RackRouteChoice.Name), SelectedValuePath = nameof(RackRouteChoice.Id), SelectedItem = choices.FirstOrDefault(c => c.Id == selected) ?? choices[0], IsEnabled = _host.IsEditing, FontSize = 11, Height = 30, MinHeight = 30, Padding = new Thickness(4), Margin = new Thickness(0, 2, 0, 2) }; combo.ToolTip = (combo.SelectedItem as RackRouteChoice)?.Name; AutomationProperties.SetAutomationId(combo, id); AutomationProperties.SetName(combo, label); combo.SelectionChanged += (_, _) => { if (!_syncing && combo.SelectedItem is RackRouteChoice choice) { combo.ToolTip = choice.Name; apply(choice.Id); } }; return combo;
    }
    private void RefreshSidePanel()
    {
        if (_sideContent.Content is ScrollViewer previous) previous.Content = null;
        _sideContent.Content = new ScrollViewer { Content = _showAppearance ? _appearancePanel ??= BuildAppearancePanel() : _inspector, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        UpdateSideVisibility();
    }
    private void ShowVisibility() => new RackVisibilityDialog(_owner, _theme, _host).ShowDialog();
    private sealed record RackRouteChoice(Guid? Id, string Name);
}
