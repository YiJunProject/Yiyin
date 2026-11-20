using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using LightDeck.Core;

namespace LightDeck.App;

internal sealed partial class RackDeviceDialog
{
    private string _clockId = "";
    private bool _edited;
    private RackUiDevice? DriverDevice => Multi
        ? ActiveDevices.FirstOrDefault(d => d.Api == "asio")
        : SelectedCard?.Devices.FirstOrDefault(d => d.Api == "asio");
    private IReadOnlyList<RackUiDevice> ActiveDevices => Multi
        ? _devices.Where(d => _selectedIds.Contains(d.Id) && DeviceAllowed(d)).ToArray()
        : SelectedCard?.Devices ?? [];
    private bool DeviceAllowed(RackUiDevice d) => d.Api == "wasapi" || _mode == "hybrid" && d.Api == "asio";

    private void BuildDeviceControls()
    {
        _clockId = _local.OutputDeviceId;
        _modes.ToolTip = "选择设备后，在左右矩阵中点选硬件声道。";
        var modeLabel = SectionLabel("工作模式"); modeLabel.VerticalAlignment = VerticalAlignment.Center; modeLabel.Margin = new Thickness(0, 0, 8, 0);
        _modes.Children.Add(modeLabel);
        var template = (ControlTemplate)XamlReader.Parse($$"""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="RadioButton">
              <Grid>
                <Border x:Name="Outline" BorderBrush="Transparent" Background="Transparent" BorderThickness="1" CornerRadius="5" Padding="8,4">
                  <StackPanel Orientation="Horizontal">
                    <Grid Width="17" Height="17" Margin="0,0,7,0">
                      <Ellipse x:Name="Ring" Stroke="{{Theme.Muted}}" StrokeThickness="1.3"/>
                      <Ellipse x:Name="Dot" Fill="{{Theme.AccentText}}" Margin="5" Visibility="Collapsed"/>
                    </Grid>
                    <ContentPresenter VerticalAlignment="Center" RecognizesAccessKey="True"/>
                  </StackPanel>
                </Border>
                <Rectangle x:Name="KeyboardFocus" Margin="2" Stroke="{{Theme.Text}}" StrokeDashArray="2 2" RadiusX="3" RadiusY="3" Visibility="Collapsed" IsHitTestVisible="False"/>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Outline" Property="Background" Value="{{Theme.Panel}}"/></Trigger>
                <Trigger Property="IsChecked" Value="True">
                  <Setter TargetName="Outline" Property="BorderBrush" Value="{{Theme.Accent}}"/>
                  <Setter TargetName="Outline" Property="Background" Value="{{Theme.Panel}}"/>
                  <Setter TargetName="Ring" Property="Fill" Value="{{Theme.Accent}}"/>
                  <Setter TargetName="Ring" Property="Stroke" Value="{{Theme.Accent}}"/>
                  <Setter TargetName="Dot" Property="Visibility" Value="Visible"/>
                </Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="KeyboardFocus" Property="Visibility" Value="Visible"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        foreach (var (id, label) in new[] { ("single", "仅声卡"), ("hybrid", "专业声卡＋USB 设备"), ("multi", "普通多 USB 设备") })
        {
            var mode = new RadioButton { Content = label, Template = template, GroupName = "RackIoMode", Foreground = DeckTheme.Brush(Theme.Text), FontSize = 14, IsChecked = id == _mode, Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(mode, "rack-device-mode-" + id);
            mode.PreviewKeyDown += MoveModeFocus;
            mode.Checked += async (_, _) =>
            {
                if (_updating || _mode == id) return;
                bool hadFocus = mode.IsKeyboardFocused; _edited = true;
                await ChangeModeAsync(id);
                if (hadFocus && !_closed && mode.IsChecked == true) mode.Focus();
            };
            _modes.Children.Add(mode);
        }
        Body.Children.Add(_modes);
        _api.ItemsSource = new[] { new ApiChoice("wasapi", "WASAPI"), new ApiChoice("asio", "ASIO") };
        _api.DisplayMemberPath = nameof(ApiChoice.Name); _api.SelectedValuePath = nameof(ApiChoice.Id); _api.SelectedValue = _activeApi;
        AutomationProperties.SetAutomationId(_api, "rack-device-api"); SetCards(); AutomationProperties.SetAutomationId(_card, "rack-device-card");
        AutomationProperties.SetAutomationId(_rate, "rack-device-rate"); AutomationProperties.SetAutomationId(_buffer, "rack-device-buffer");
        AutomationProperties.SetAutomationId(_clock, "rack-device-clock");
        var row = new Grid { Margin = new Thickness(0, 6, 0, 2) };
        row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(108) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(115) });
        StackPanel Field(string label, UIElement value)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            panel.Children.Add(SectionLabel(label)); panel.Children.Add(value); return panel;
        }
        var deviceFields = new Grid { VerticalAlignment = VerticalAlignment.Center };
        var container = new Border { Child = deviceFields, BorderBrush = DeckTheme.Brush(Theme.Line), Background = Theme.Surface(Theme.Panel),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(Theme.CardRadius), Padding = new Thickness(12, 7, 8, 7), Margin = new Thickness(0, 0, 12, 0) };
        row.Children.Add(container);
        var single = new Grid(); single.ColumnDefinitions.Add(new() { Width = new GridLength(130) }); single.ColumnDefinitions.Add(new());
        single.Children.Add(Field("音频驱动", _api)); var card = Field("音频设备 · 物理声卡", _card);
        Grid.SetColumn(card, 1); single.Children.Add(card); _singleFields = single; deviceFields.Children.Add(single);
        var multi = new Grid();
        multi.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        multi.ColumnDefinitions.Add(new() { Width = new GridLength(3, GridUnitType.Star) });
        multi.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star), MinWidth = 215, MaxWidth = 420 });
        var deviceLabel = SectionLabel("设备"); deviceLabel.Margin = new Thickness(0, 0, 12, 0); deviceLabel.VerticalAlignment = VerticalAlignment.Center; multi.Children.Add(deviceLabel);
        var groupScroll = new ScrollViewer { Content = _groups, MaxHeight = 90, VerticalAlignment = VerticalAlignment.Center, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(groupScroll, 1); multi.Children.Add(groupScroll);
        var clockRow = new Grid(); clockRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); clockRow.ColumnDefinitions.Add(new());
        var clockLabel = SectionLabel("主时钟输出"); clockLabel.Margin = new Thickness(0, 0, 10, 0); clockLabel.VerticalAlignment = VerticalAlignment.Center;
        clockRow.Children.Add(clockLabel); _clock.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(_clock, 1); clockRow.Children.Add(_clock);
        var divider = new Border { BorderThickness = new Thickness(1, 0, 0, 0), BorderBrush = DeckTheme.Brush(Theme.Line), Child = clockRow, Padding = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(divider, 2); multi.Children.Add(divider); _multiFields = multi; deviceFields.Children.Add(multi);
        var rate = Field("采样率 Hz", _rate); var frames = Field("缓冲帧数", _buffer);
        Grid.SetColumn(rate, 1); Grid.SetColumn(frames, 2); row.Children.Add(rate); row.Children.Add(frames); Body.Children.Add(row);
        _clock.SelectionChanged += (_, _) => { if (!_updating && _clock.SelectedItem is RackUiDevice d) { _clockId = d.Id; UpdateRates(); ClearError(); ReconcileStreamSelection(); } };
        RefreshDeviceControls();
    }

    private void MoveModeFocus(object sender, KeyEventArgs e)
    {
        var step = e.Key switch { Key.Left or Key.Up => -1, Key.Right or Key.Down => 1, _ => 0 };
        if (step == 0) return;
        var modes = _modes.Children.OfType<RadioButton>().ToArray();
        var next = modes[(Array.IndexOf(modes, (RadioButton)sender) + step + modes.Length) % modes.Length];
        e.Handled = true; next.Focus(); next.IsChecked = true;
    }

    private async Task ChangeModeAsync(string mode)
    {
        await HardwareActionAsync(async () =>
        {
            string api = mode == "hybrid" ? "asio" : mode == "multi" ? "wasapi" : _local.DeviceApi;
            var devices = (await _host.EnumerateDevicesAsync(api)).ToList();
            if (mode == "hybrid") devices.AddRange(await _host.EnumerateDevicesAsync("wasapi"));
            if (!Multi && mode != "single" && _selectedIds.Count == 0 && SelectedCard is { } previous)
                _selectedIds.UnionWith(previous.Devices.Where(d => d.Api == "wasapi" || mode == "hybrid").Select(d => d.Id));
            _activeApi = api; _mode = mode; _devices = devices;
            _updating = true; try { _api.SelectedValue = api; } finally { _updating = false; }
            SetCards(); RefreshDeviceControls(); UpdateRates(); Render(); ReconcileStreamSelection();
        }, "正在读取音频设备…");
        _updating = true;
        try
        {
            foreach (var button in _modes.Children.OfType<RadioButton>())
                button.IsChecked = AutomationProperties.GetAutomationId(button) == "rack-device-mode-" + _mode;
        }
        finally { _updating = false; }
    }

    private void RefreshDeviceControls()
    {
        _singleFields.Visibility = Multi ? Visibility.Collapsed : Visibility.Visible;
        _multiFields.Visibility = Multi ? Visibility.Visible : Visibility.Collapsed;
        if (!Multi) { _summary.Visibility = Visibility.Visible; return; }
        _groups.Children.Clear();
        var available = _devices.Where(DeviceAllowed).GroupBy(d => (d.Api, Key: CardKey(d)));
        foreach (var group in available)
        {
            var members = group.ToArray();
            string name = members[0].GroupName.Length > 0 ? members[0].GroupName : members[0].Name;
            AddGroup(name, members, group.Key.Key, members.Any(d => _selectedIds.Contains(d.Id)));
        }
        foreach (var id in _selectedIds.Where(id => RelevantId(id) && _devices.All(d => d.Id != id)).ToArray())
        {
            var name = _local.InputPorts.Values.Concat(_local.OutputPorts.Values).Concat(_local.OutputMirrors.Values.SelectMany(m => m)).FirstOrDefault(m => m.DeviceId == id)?.DeviceName;
            var missing = new RackUiDevice(id, (name ?? (id == _local.OutputDeviceId ? _local.OutputDeviceName : id)) + "（离线）", id.StartsWith("asio:", StringComparison.OrdinalIgnoreCase) ? "asio" : "wasapi", 0, 0, Available: false);
            AddGroup(missing.Name, [missing], id, true);
        }

        var clocks = ActiveDevices.Where(d => (d.OutputChannels > 0 || d.ProbeRequired) && (_mode != "hybrid" || d.Api == "asio")).ToArray();
        var clock = clocks.FirstOrDefault(d => d.Id == _clockId);
        if (clock is null && !_selectedIds.Contains(_clockId)) clock = clocks.FirstOrDefault();
        if (clock is null && _mode == "hybrid") clock = clocks.FirstOrDefault();
        if (clock is not null) _clockId = clock.Id;
        _updating = true;
        try { _clock.ItemsSource = clocks; _clock.SelectedItem = clock; }
        finally { _updating = false; }
    }

    private bool RelevantId(string id) => _mode == "hybrid" || !id.StartsWith("asio:", StringComparison.OrdinalIgnoreCase);

    private void AddGroup(string name, RackUiDevice[] members, string key, bool selected)
    {
        var box = new CheckBox { Content = name, IsChecked = selected, Margin = new Thickness(0, 5, 14, 5),
            ToolTip = string.Join("\n", members.Select(d => d.Name)) };
        AutomationProperties.SetAutomationId(box, "rack-device-group-" + key);
        box.Click += (_, _) =>
        {
            _edited = true;
            if (box.IsChecked == true)
            {
                if (members[0].Api == "asio") RemoveEndpoints(_selectedIds.Where(id => id.StartsWith("asio:", StringComparison.OrdinalIgnoreCase)).ToHashSet());
                _selectedIds.UnionWith(members.Select(d => d.Id));
            }
            else RemoveEndpoints(members.Select(d => d.Id).ToHashSet());
            RefreshDeviceControls(); ClearError(); UpdateRates(); Render(); ReconcileStreamSelection();
        };
        _groups.Children.Add(box);
    }

    private void RemoveEndpoints(HashSet<string> ids)
    {
        _selectedIds.ExceptWith(ids);
        foreach (var key in _local.InputPorts.Where(p => ids.Contains(p.Value.DeviceId)).Select(p => p.Key).ToArray()) _local.InputPorts.Remove(key);
        foreach (var port in _local.OutputPorts.Keys.ToArray()) SetOutputTargets(port, AllOutputTargets(port).Where(m => !ids.Contains(m.DeviceId)).ToList());
    }

    private void UpdateMultiRates(int oldRate, bool newDevice)
    {
        var endpoints = ActiveDevices.Where(d => d.Available).ToArray();
        var rates = new[] { 44100, 48000 }.Where(r => _clock.SelectedItem is RackUiDevice { Available: true, ProbeRequired: false } clock && clock.SampleRates?.Contains(r) == true).ToArray();
        _rate.ItemsSource = rates; _rate.SelectedItem = rates.Contains(oldRate) ? oldRate : rates.Cast<int?>().FirstOrDefault();
        var driver = DriverDevice;
        var frames = _buffer.SelectedItem is int old ? old : _local.BufferSize;
        var buffers = _mode == "hybrid" ? driver is null ? [] : AsioBufferChoices(driver) : new[] { 64, 128, 256, 512, 1024, 2048 }.Append(_local.BufferSize).Distinct().Order().ToArray();
        if (newDevice && driver is not null) frames = driver.BufferPreferredFrames;
        _buffer.ItemsSource = buffers; _buffer.SelectedItem = buffers.Contains(frames) ? frames : driver is not null && buffers.Contains(driver.BufferPreferredFrames) ? driver.BufferPreferredFrames : null;
        _summary.Text = _mode == "hybrid" && driver is null ? "请勾选一台 ASIO 专业声卡作为主时钟。"
            : driver?.ProbeRequired == true ? "请探测所选 ASIO 驱动，再映射它的硬件声道。"
            : _selectedIds.Any(id => RelevantId(id) && endpoints.All(d => d.Id != id)) ? "已保存的设备离线，原映射保留。可重新连接，或取消勾选以移除绑定。"
            : endpoints.Length > 0 && rates.Length == 0 ? "请检查主时钟输出支持的采样率。" : "";
        _summary.Visibility = _summary.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateActionAvailability();
    }
}

