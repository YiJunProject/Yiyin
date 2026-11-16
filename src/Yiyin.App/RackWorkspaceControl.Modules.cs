using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LightDeck.Core;

namespace LightDeck.App;

public sealed partial class RackWorkspaceControl
{
    private bool ModuleVisible(string key) => key switch
    {
        "insert" => _host.Local.ShowRackInsert, "send" => _host.Local.ShowRackSend,
        "output" => _host.Local.ShowRackOutput, "inspector" => _host.Local.ShowRackInspector,
        _ => _host.Local.ShowRackLibrary
    };
    private void AddModuleChoice(Panel toolbar, string title, string key)
    {
        var check = new CheckBox { Content = title, IsChecked = ModuleVisible(key), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 4, 0, 8) };
        AutomationProperties.SetAutomationId(check, "rack-show-" + key); _moduleChoices[key] = check;
        check.Click += (_, _) => ToggleModule(key); toolbar.Children.Add(check);
    }
    private void ToggleModule(string key) => Run(async () =>
    {
        var draft = CopyLocal(_host.Local);
        switch (key)
        {
            case "insert": draft.ShowRackInsert = !draft.ShowRackInsert; break;
            case "send": draft.ShowRackSend = !draft.ShowRackSend; break;
            case "output": draft.ShowRackOutput = !draft.ShowRackOutput; break;
            case "inspector": draft.ShowRackInspector = !draft.ShowRackInspector; _showAppearance = false; break;
            case "library": draft.ShowRackLibrary = !draft.ShowRackLibrary; break;
        }
        await _host.ApplyLocalAsync(draft);
    });
    private void UpdateModuleChoices()
    {
        foreach (var pair in _moduleChoices) { pair.Value.IsChecked = ModuleVisible(pair.Key); pair.Value.IsEnabled = !_busy; }
    }
    private Border CreateSidePanel(string title, string key, UIElement content)
    {
        var panel = new DockPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var close = Button("×", "rack-close-" + key, () => { if (key == "inspector" && _showAppearance) { _showAppearance = false; RefreshSidePanel(); } else ToggleModule(key); });
        close.Padding = new Thickness(4); close.MinHeight = 28; close.Width = 28; DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        var text = Label(title, 18); text.FontWeight = FontWeights.SemiBold; header.Children.Add(text);
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header); panel.Children.Add(content);
        var frame = PanelFrame(panel, 12); frame.Margin = new Thickness(7, 0, 0, 0);
        AutomationProperties.SetAutomationId(frame, "rack-panel-" + key); return frame;
    }
    private FrameworkElement SoloMuteButtons(RackTrack track, bool inspector = false)
    {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 6) }; row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new());
        var suffix = (inspector ? "inspector-" : "") + track.Id;
        var mute = Button("M", "rack-mute-" + suffix, () => Live(track.Id, t => t.Mute = !t.Mute));
        var solo = Button("S", "rack-solo-" + suffix, () => Live(track.Id, t => { if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) t.SoloSafe = !t.SoloSafe; else t.Solo = !t.Solo; }));
        foreach (var button in new[] { mute, solo }) { button.MinHeight = 30; button.Padding = new Thickness(4, 3, 4, 3); button.FontWeight = FontWeights.SemiBold; }
        mute.Margin = new Thickness(0, 0, 4, 0); solo.Margin = new Thickness(4, 0, 0, 0); Grid.SetColumn(solo, 1); row.Children.Add(mute); row.Children.Add(solo);
        if (!_soloButtons.TryGetValue(track.Id, out var buttons)) _soloButtons[track.Id] = buttons = [];
        buttons.Add((mute, solo)); return row;
    }
    private void UpdateSoloButtons()
    {
        _clearSolo.IsEnabled = !_busy && _host.Project.Tracks.Any(t => (_pending.GetValueOrDefault(t.Id) ?? t).Solo);
        foreach (var pair in _soloButtons)
        {
            var track = _pending.GetValueOrDefault(pair.Key) ?? _host.Project.FindTrack(pair.Key); if (track is null) continue;
            foreach (var (mute, solo) in pair.Value)
            {
                mute.Background = DeckTheme.Brush(track.Mute ? "#C83547" : _theme.Card); mute.Foreground = DeckTheme.Brush(track.Mute ? "#FFFFFF" : _theme.Text);
                mute.ToolTip = track.Mute ? "已静音；点击恢复" : "静音，保留推子数值";
                solo.Content = track.Solo ? "S" : track.SoloSafe ? "S SAFE" : "S";
                solo.Background = DeckTheme.Brush(track.Solo ? _theme.Accent : track.SoloSafe ? "#205C3D" : _theme.Card);
                solo.Foreground = DeckTheme.Brush(track.Solo ? _theme.AccentText : track.SoloSafe ? "#91F5B6" : _theme.Text);
                solo.ToolTip = "独听；可同时选多轨。Shift + 点击切换独听保护";
                AutomationProperties.SetName(mute, track.Name + "静音 " + track.Mute);
                AutomationProperties.SetName(solo, track.Name + "独听 " + track.Solo + "；独听保护 " + track.SoloSafe);
            }
        }
    }
}
