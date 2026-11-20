using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using LightDeck.Core;

namespace LightDeck.App;

internal sealed partial class RackDeviceDialog
{
    private void Render()
    {
        _matrix.Children.Clear();
        foreach (var input in new[] { true, false })
        {
            var pane = MatrixPane(input); Grid.SetColumn(pane, input ? 0 : 1); _matrix.Children.Add(pane);
        }
    }
    private Border MatrixPane(bool input)
    {
        var section = new StackPanel(); var title = new TextBlock { Text = input ? "输入 · 声音从哪里来" : "输出 · 声音送到哪里", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = DeckTheme.Brush(Theme.Text), Margin = new Thickness(2, 0, 0, 10) }; section.Children.Add(title);
        var ports = input ? _project.InputPorts : _project.OutputPorts; var maps = input ? _local.InputPorts : _local.OutputPorts;
        var channels = ActiveDevices.Where(d => d.Available && !d.ProbeRequired).SelectMany(d => Enumerable.Range(0, input ? d.InputChannels : d.OutputChannels).Select(n => (Device: d, Index: n))).ToArray();
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(112) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(48) });
        foreach (var _ in channels) grid.ColumnDefinitions.Add(Multi ? new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 68, MaxWidth = 172 } : new() { Width = new GridLength(48) });
        var showUsage = !Multi || ports.Any(p => (input ? maps.TryGetValue(p.Id, out var m) ? [m] : Array.Empty<RackHardwarePort>() : AllOutputTargets(p.Id).ToArray()).Any(m => !channels.Any(c => c.Device.Id == m.DeviceId)));
        if (showUsage) grid.ColumnDefinitions.Add(new() { Width = new GridLength(80) });
        grid.RowDefinitions.Add(new() { Height = new GridLength(Multi ? 34 : 72) });
        if (Multi) grid.RowDefinitions.Add(new() { Height = new GridLength(38) });
        void Cell(UIElement element, int row, int col) { Grid.SetRow(element, row); Grid.SetColumn(element, col); grid.Children.Add(element); }
        TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = Multi ? 12 : 10, Foreground = DeckTheme.Brush(Theme.Text), Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
        var headerRow = Multi ? 1 : 0;
        Cell(Text("通道名称"), headerRow, 0); Cell(Text("类型"), headerRow, 1);
        if (Multi)
        {
            var column = 2;
            foreach (var group in channels.GroupBy(c => c.Device))
            {
                var d = group.Key;
                var sameGroup = channels.Select(c => c.Device).Distinct().Count(other => CardKey(other) == CardKey(d));
                var caption = d.GroupName.Length > 0 && sameGroup == 1 ? d.GroupName : d.Name;
                var heading = Text(caption); heading.FontSize = 13; heading.TextAlignment = TextAlignment.Center;
                heading.TextTrimming = TextTrimming.CharacterEllipsis; heading.TextWrapping = TextWrapping.NoWrap;
                var band = new Border { Child = heading, ToolTip = d.Name, BorderThickness = new Thickness(1), Margin = new Thickness(2, 0, 2, 0), BorderBrush = DeckTheme.Brush(Theme.Line), CornerRadius = new CornerRadius(4, 4, 0, 0), Background = Theme.Surface(Theme.Input) };
                Grid.SetColumnSpan(band, group.Count()); Cell(band, 0, column); column += group.Count();
            }
        }
        for (var c = 0; c < channels.Length; c++)
        {
            var channel = channels[c]; var names = input ? channel.Device.InputChannelNames : channel.Device.OutputChannelNames;
            int count = input ? channel.Device.InputChannels : channel.Device.OutputChannels;
            var name = names is not null && channel.Index < names.Count && !string.IsNullOrWhiteSpace(names[channel.Index]) ? names[channel.Index] :
                Multi && !input && count == 2 ? channel.Index == 0 ? "L" : "R" : "通道 " + (channel.Index + 1);
            var label = Text(Multi ? name : (channel.Index + 1) + "\n" + name); label.FontSize = Multi ? 11 : 9;
            label.ToolTip = channel.Device.Name + " / " + name; label.TextAlignment = TextAlignment.Center;
            AutomationProperties.SetAutomationId(label, "rack-io-" + (input ? "input" : "output") + "-channel-" + c);
            if (!Multi) { Cell(label, headerRow, c + 2); continue; }
            var first = channel.Index == 0; var last = channel.Index == count - 1;
            var lower = new Border { Child = label, BorderBrush = DeckTheme.Brush(Theme.Line), Background = Theme.Surface(Theme.Input),
                BorderThickness = new Thickness(first ? 1 : 0, 0, 1, 1), Margin = new Thickness(first ? 2 : 0, 0, last ? 2 : 0, 3),
                CornerRadius = new CornerRadius(0, 0, last ? 4 : 0, first ? 4 : 0) };
            Cell(lower, headerRow, c + 2);
        }
        if (showUsage) Cell(Text("使用 / 映射"), headerRow, channels.Length + 2);
        for (var r = 0; r < ports.Count; r++)
        {
            var port = ports[r]; grid.RowDefinitions.Add(new() { Height = new GridLength(43) }); var row = r + headerRow + 1;
            var name = new TextBox { Text = port.Name, MinHeight = 32, FontSize = Multi ? 12 : 11, Padding = new Thickness(4), Margin = new Thickness(2), BorderBrush = DeckTheme.Brush(_selection == port.Id ? Theme.Accent : Theme.Line) };
            AutomationProperties.SetAutomationId(name, "rack-io-name-" + port.Id); name.GotKeyboardFocus += (_, _) => { _input = input; _selection = port.Id; }; name.TextChanged += (_, _) => { port.Name = name.Text; ClearError(); }; Cell(name, row, 0);
            Cell(Text(port.Channels == 1 ? "单声道" : "立体声"), row, 1); maps.TryGetValue(port.Id, out var map);
            var targets = Multi && !input ? AllOutputTargets(port.Id) : map is null ? [] : new List<RackHardwarePort> { map };
            for (var c = 0; c < channels.Length; c++)
            {
                var channel = channels[c]; var target = targets.FirstOrDefault(m => m.DeviceId == channel.Device.Id && m.Channels.Contains(channel.Index));
                var position = target is null ? -1 : Array.IndexOf(target.Channels, channel.Index);
                var assignment = port.Channels == 1 ? "M" : position == 0 ? "L" : "R";
                var button = new Button { Content = position < 0 ? "·" : Multi ? "✓  " + assignment : assignment, MinHeight = 32, MinWidth = 28, Padding = new Thickness(2), Margin = new Thickness(2), Background = DeckTheme.Brush(position >= 0 ? Theme.Accent : Theme.Input), Foreground = DeckTheme.Brush(position >= 0 ? Theme.AccentText : Theme.Muted), ToolTip = port.Name + " → " + channel.Device.Name + " / " + (channel.Index + 1) };
                AutomationProperties.SetName(button, button.ToolTip.ToString()); AutomationProperties.SetAutomationId(button, $"rack-io-cell-{port.Id}-{c}"); button.Click += (_, _) => { _input = input; SelectCell(port, channel.Device, channel.Index); }; Cell(button, row, c + 2);
            }
            if (!showUsage) continue;
            var unmatched = targets.Where(m => !channels.Any(c => c.Device.Id == m.DeviceId)).ToArray();
            if (Multi && unmatched.Length > 0)
            {
                var clear = new Button { Content = "移除未匹配", FontSize = 10, Padding = new Thickness(2), ToolTip = string.Join("\n", unmatched.Select(m => m.DeviceName)) };
                clear.Click += (_, _) => { if (input) _local.InputPorts.Remove(port.Id); else SetOutputTargets(port.Id, targets.Except(unmatched).ToList()); Render(); QueuePreview(); };
                Cell(clear, row, channels.Length + 2); continue;
            }
            var used = _project.Tracks.Count(t => (input ? t.InputPortId : t.HardwareOutputPortId) == port.Id); var valid = map is not null && channels.Any(c => c.Device.Id == map.DeviceId);
            var mapped = map is null ? "未映射" : !valid ? "未匹配" : map.Channels.Length != port.Channels ? "待补齐" : "已映射";
            var usage = Text(used + " 轨\n" + mapped); usage.ToolTip = map?.DeviceName ?? "没有绑定硬件通道"; if (map is not null && !valid) usage.Foreground = DeckTheme.Brush(Theme.Light ? "#B13A32" : "#FFB39B"); Cell(usage, row, channels.Length + 2);
        }
        var content = new StackPanel(); content.Children.Add(grid); if (ports.Count == 0) content.Children.Add(Text("添加单声道或立体声命名通道，再点选矩阵中的 M / L / R。"));
        var scroll = new ScrollViewer { Content = content, MinHeight = 245, MaxHeight = 350, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; AutomationProperties.SetAutomationId(scroll, "rack-io-" + (input ? "input" : "output") + "-matrix"); section.Children.Add(scroll);
        if (Multi) grid.SetBinding(MinWidthProperty, new System.Windows.Data.Binding(nameof(ScrollViewer.ViewportWidth)) { Source = scroll });
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        void Action(string caption, string suffix, Action action) { var button = new Button { Content = caption, MinHeight = 30, Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 5, 0) }; AutomationProperties.SetAutomationId(button, "rack-io-" + (input ? "input" : "output") + "-" + suffix); button.Click += (_, _) => { _input = input; action(); }; actions.Children.Add(button); }
        Action("＋ 单声道", "add-mono", () => AddPort(1)); Action("＋ 立体声", "add-stereo", () => AddPort(2)); Action("移除选中", "remove", RemovePort); section.Children.Add(actions);
        return new Border { Child = section, Padding = new Thickness(9), Margin = new Thickness(input ? 0 : 7, 4, input ? 7 : 0, 0), BorderThickness = new Thickness(1), BorderBrush = DeckTheme.Brush(Theme.Line), CornerRadius = new CornerRadius(Theme.CardRadius), Background = Theme.Surface(Theme.Panel) };
    }
}
