using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LightDeck.Core;

namespace LightDeck.App;

public sealed partial class RackWorkspaceControl
{
    private IReadOnlyList<RackPluginIdentity>? _libraryCatalog;
    private RackPluginFilter? _libraryFilter;
    private ComboBox? _libraryScopeChoice;
    private ListBox? _libraryList;
    private TextBlock? _libraryTarget, _libraryCount;
    private DispatcherTimer? _libraryDebounce;
    private bool _libraryUpdating;
    private int _libraryCategory;
    private readonly List<Button> _libraryCategories = [];

    private void BuildLibrary()
    {
        // The list owns its viewport: an outer ScrollViewer would measure it at
        // infinite height and force every plugin row to be materialized.
        if (_libraryList is null)
        {
            _library.RowDefinitions.Add(new() { Height = GridLength.Auto });
            _library.RowDefinitions.Add(new());
            var header = new StackPanel(); _library.Children.Add(header);

            _libraryFilter = new RackPluginFilter(_theme, "rack-library"); _libraryFilter.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            _libraryFilter.ColumnDefinitions[1].Width = new GridLength(0);
            foreach (var child in _libraryFilter.Children.OfType<FrameworkElement>()) if (Grid.GetColumn(child) == 1) child.Visibility = Visibility.Collapsed;
            ((TextBlock)_libraryFilter.Children[0]).Text = "搜索插件"; header.Children.Add(_libraryFilter);
            var categories = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 8) };
            foreach (var title in new[] { "全部", "EQ", "动态", "混响" })
            {
                var index = _libraryCategories.Count;
                var button = Button(title, "rack-library-category-" + index, () => { _libraryCategory = index; RenderLibrary(); });
                button.MinHeight = 32; button.Padding = new Thickness(3); button.Margin = new Thickness(0, 0, 3, 0);
                button.ToolTip = index == 0 ? "显示全部插件" : "按插件名称关键字快速筛选；未归类插件可在“全部”中查找";
                _libraryCategories.Add(button); categories.Children.Add(button);
            }
            header.Children.Add(categories);
            _libraryScopeChoice = new ComboBox { ItemsSource = new[] { "全部插件", "常用插件", "最近使用" }, SelectedIndex = _libraryScope, Margin = new Thickness(0, 0, 0, 5) };
            AutomationProperties.SetAutomationId(_libraryScopeChoice, "rack-library-scope"); header.Children.Add(_libraryScopeChoice);
            _libraryTarget = Label("", 11, true); _libraryTarget.TextWrapping = TextWrapping.Wrap; header.Children.Add(_libraryTarget);
            _libraryCount = Label("", 11, true); header.Children.Add(_libraryCount);
            _library.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var scan = Button("扫描与管理…", "rack-library-scan", ShowPluginManager); scan.Margin = new Thickness(0, 8, 0, 0); Grid.SetRow(scan, 2); _library.Children.Add(scan);
            _libraryList = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(_libraryList, "rack-library-list");
            ScrollViewer.SetCanContentScroll(_libraryList, true); ScrollViewer.SetHorizontalScrollBarVisibility(_libraryList, ScrollBarVisibility.Disabled);
            VirtualizingPanel.SetIsVirtualizing(_libraryList, true); VirtualizingPanel.SetVirtualizationMode(_libraryList, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(_libraryList, ScrollUnit.Pixel);
            _libraryList.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
            _libraryList.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(LibraryPresenter)) };
            var container = new Style(typeof(ListBoxItem)); container.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            container.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            _libraryList.ItemContainerStyle = container;
            Grid.SetRow(_libraryList, 1); _library.Children.Add(_libraryList);
            _libraryDebounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
            _libraryDebounce.Tick += (_, _) => { _libraryDebounce.Stop(); RenderLibrary(); };
            _libraryFilter.Changed += QueueLibraryFilter;
            _libraryScopeChoice.SelectionChanged += (_, _) => QueueLibraryFilter();
        }
        var catalog = _host.Plugins;
        var target = _host.Project.FindTrack(_host.Project.SelectedTrackId ?? Guid.Empty);
        var signature = (target?.Id.ToString() ?? "") + ":" + target?.Name + ":" + string.Join('|', _host.Local.FavoritePlugins) + ":" + string.Join('|', _host.Local.RecentPlugins);
        if (ReferenceEquals(catalog, _libraryCatalog) && signature == _librarySignature) return;
        _librarySignature = signature;
        _libraryTarget!.Text = target is null ? "先选择轨道，再添加或拖入插件。" : "添加到：" + target.Name;
        _libraryTarget.ToolTip = "拖到插入区添加；拖到发送区新建 FX。";
        if (!ReferenceEquals(catalog, _libraryCatalog))
        {
            var initial = _libraryCatalog is null;
            _libraryUpdating = true;
            try
            {
                _libraryCatalog = catalog; _libraryFilter!.SetPlugins(catalog);
                // Catalog publication may arrive during the debounce window.
                // Preserve the actual input, not the last rendered query.
                if (initial) { _libraryFilter.SearchText = _librarySearch; _libraryFilter.SelectedVendor = _libraryVendor; }
            }
            finally { _libraryUpdating = false; }
        }
        RenderLibrary();
    }
    private void QueueLibraryFilter()
    {
        if (_libraryUpdating || _disposed) return;
        _libraryDebounce!.Stop(); _libraryDebounce.Start();
    }
    private void RenderLibrary()
    {
        if (_disposed || _libraryList is null) return;
        _libraryDebounce!.Stop(); _librarySearch = _libraryFilter!.SearchText;
        _libraryVendor = _libraryFilter.SelectedVendor; _libraryScope = _libraryScopeChoice!.SelectedIndex;
        var favorites = _host.Local.FavoritePlugins.ToHashSet(); var recent = _host.Local.RecentPlugins;
        for (var i = 0; i < _libraryCategories.Count; i++) { _libraryCategories[i].Background = DeckTheme.Brush(i == _libraryCategory ? _theme.Accent : _theme.Card); _libraryCategories[i].Foreground = DeckTheme.Brush(i == _libraryCategory ? _theme.AccentText : _theme.Text); }
        var plugins = (_libraryCatalog ?? []).Where(_libraryFilter.Matches).Where(p => MatchesLibraryCategory(p.Name, _libraryCategory)).Where(p => _libraryScope != 1 || favorites.Contains(p.Key)).Where(p => _libraryScope != 2 || recent.Contains(p.Key));
        if (_libraryScope == 2) plugins = plugins.OrderBy(p => recent.IndexOf(p.Key));
        var rows = plugins.Select(p => new LibraryRow(this, p)).ToArray();
        _libraryList.ItemsSource = rows;
        _libraryCount!.Text = rows.Length == 0 ? "没有匹配插件。" : $"{rows.Length} 个插件";
    }
    private static bool MatchesLibraryCategory(string name, int category)
    {
        string[] keywords = category switch
        {
            1 => ["eq", "pro-q", "均衡"],
            2 => ["compress", "limiter", "gate", "pro-c", "pro-mb", "pro-ds", "压缩", "限制", "动态", "齿音"],
            3 => ["reverb", "room", "plate", "hall", "verb", "lx480", "混响"],
            _ => []
        };
        return category == 0 || keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
    }
    private FrameworkElement CreateLibraryRow(RackPluginIdentity plugin)
    {
        var row = new StackPanel(); var name = Label(plugin.Name, 13); name.TextWrapping = TextWrapping.Wrap; name.FontWeight = FontWeights.SemiBold; row.Children.Add(name);
        row.Children.Add(Label(plugin.Format + " · " + plugin.Vendor, 11, true));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var favorite = Button(_host.Local.FavoritePlugins.Contains(plugin.Key) ? "★" : "☆", "rack-library-favorite-" + plugin.ClassId,
            () => Run(async () => { var local = CopyLocal(_host.Local); if (!local.FavoritePlugins.Remove(plugin.Key)) local.FavoritePlugins.Add(plugin.Key); await _host.ApplyLocalAsync(local); }));
        favorite.ToolTip = "收藏 / 取消常用插件"; favorite.Padding = new Thickness(3); favorite.MinHeight = 28; actions.Children.Add(favorite);
        var insert = Button("›", "rack-library-add-" + plugin.ClassId, () => {
            if (_host.Project.FindTrack(_host.Project.SelectedTrackId ?? Guid.Empty) is { } target)
                Run(() => _host.AddPluginAsync(target.Id, plugin, target.Inserts.Count));
        });
        insert.ToolTip = "插入到所选轨道"; insert.IsEnabled = _host.IsEditing && _host.Project.SelectedTrackId is not null; insert.Padding = new Thickness(5, 3, 5, 3); insert.MinHeight = 28; insert.Margin = new Thickness(3, 0, 0, 0); actions.Children.Add(insert);
        var layout = new DockPanel(); DockPanel.SetDock(actions, Dock.Right); layout.Children.Add(actions); layout.Children.Add(row);
        var frame = PanelFrame(layout); frame.MinHeight = 70; frame.Margin = new Thickness(0, 6, 0, 0); frame.ToolTip = plugin.Name + "\n拖到插入区添加；拖到发送区新建 FX";
        AutomationProperties.SetAutomationId(frame, "rack-library-plugin-" + plugin.ClassId);
        MakeDragSource(frame, () => new RackDrag(_host, RackDragKind.Plugin, Guid.Empty, Plugin: plugin)); return frame;
    }
    private sealed record LibraryRow(RackWorkspaceControl Owner, RackPluginIdentity Plugin);
    public sealed class LibraryPresenter : ContentControl
    {
        public LibraryPresenter() => DataContextChanged += (_, _) => Content = DataContext is LibraryRow row ? row.Owner.CreateLibraryRow(row.Plugin) : null;
    }
}
