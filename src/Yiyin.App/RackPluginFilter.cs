using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LightDeck.Core;

namespace LightDeck.App;

internal sealed class RackPluginFilter : Grid
{
    private readonly TextBox _search = new();
    private readonly ComboBox _vendor = new() { DisplayMemberPath = nameof(VendorChoice.Label), SelectedValuePath = nameof(VendorChoice.Value) };
    private bool _updating;
    public string SearchText { get => _search.Text; set => _search.Text = value; }
    public string? SelectedVendor { get => (_vendor.SelectedItem as VendorChoice)?.Value; set => _vendor.SelectedItem = _vendor.Items.OfType<VendorChoice>().FirstOrDefault(v => v.Value == value) ?? _vendor.Items.OfType<VendorChoice>().FirstOrDefault(); }
    public event Action? Changed;
    public RackPluginFilter(DeckTheme theme, string id)
    {
        Margin = new Thickness(0, 5, 0, 8); ColumnDefinitions.Add(new()); ColumnDefinitions.Add(new() { Width = new GridLength(170) }); RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        Children.Add(new TextBlock { Text = "搜索名称、格式或厂商", Foreground = DeckTheme.Brush(theme.Muted), Margin = new Thickness(0, 0, 0, 5) });
        var vendorLabel = new TextBlock { Text = "厂商", Foreground = DeckTheme.Brush(theme.Muted), Margin = new Thickness(8, 0, 0, 5) }; SetColumn(vendorLabel, 1); Children.Add(vendorLabel);
        AutomationProperties.SetAutomationId(_search, id + "-search"); AutomationProperties.SetName(_search, "搜索插件"); SetRow(_search, 1); Children.Add(_search);
        AutomationProperties.SetAutomationId(_vendor, id + "-vendor"); AutomationProperties.SetName(_vendor, "筛选插件厂商"); _vendor.Margin = new Thickness(8, 0, 0, 0); SetColumn(_vendor, 1); SetRow(_vendor, 1); Children.Add(_vendor);
        _search.TextChanged += (_, _) => { if (!_updating) Changed?.Invoke(); }; _vendor.SelectionChanged += (_, _) => { if (!_updating) Changed?.Invoke(); };
    }
    public void SetPlugins(IEnumerable<RackPluginIdentity> plugins)
    {
        var selected = (_vendor.SelectedItem as VendorChoice)?.Value; _updating = true;
        try { var choices = new[] { new VendorChoice(null, "全部厂商") }.Concat(plugins.Select(p => p.Vendor.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(v => new VendorChoice(v, v.Length == 0 ? "未标明厂商" : v))).ToArray(); _vendor.ItemsSource = choices; _vendor.SelectedItem = choices.FirstOrDefault(v => v.Value == selected) ?? choices[0]; }
        finally { _updating = false; }
    }
    public bool Matches(RackPluginIdentity plugin)
    {
        var vendor = (_vendor.SelectedItem as VendorChoice)?.Value; if (vendor is not null && !plugin.Vendor.Trim().Equals(vendor, StringComparison.OrdinalIgnoreCase)) return false;
        var search = _search.Text.Trim(); return search.Length == 0 || (plugin.Name + " " + plugin.Format + " " + plugin.Vendor).Contains(search, StringComparison.OrdinalIgnoreCase);
    }
    private sealed record VendorChoice(string? Value, string Label);
}
