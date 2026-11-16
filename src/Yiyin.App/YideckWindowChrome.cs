using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;

namespace LightDeck.App;

// App-owned WPF windows share themed chrome. WindowChrome retains native move,
// resize, system menu and restore behavior; no layered/transparent window is used.
internal static class YideckWindowChrome
{
    private static readonly DependencyProperty InstalledProperty = DependencyProperty.RegisterAttached(
        "Installed", typeof(bool), typeof(YideckWindowChrome), new PropertyMetadata(false));

    internal static void Apply(Window window, DeckTheme theme)
    {
        var main = window is MainWindow;
        if (!(bool)window.GetValue(InstalledProperty))
        {
            window.SetValue(InstalledProperty, true);
            window.WindowStyle = WindowStyle.None;
            window.CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand,
                (_, e) => { window.Close(); e.Handled = true; }));
            window.CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand,
                (_, e) => { SystemCommands.MinimizeWindow(window); e.Handled = true; },
                (_, e) => { e.CanExecute = window.ResizeMode != ResizeMode.NoResize; e.Handled = true; }));
            window.CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand,
                (_, e) => { if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window); else SystemCommands.MaximizeWindow(window); e.Handled = true; },
                (_, e) => { e.CanExecute = window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip; e.Handled = true; }));
            void AttachBoundsHook()
            {
                HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(WorkAreaBounds);
            }
            if (PresentationSource.FromVisual(window) is HwndSource) AttachBoundsHook();
            else window.SourceInitialized += (_, _) => AttachBoundsHook();
        }
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = main ? 52 : 32,
            ResizeBorderThickness = window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip ? new Thickness(6) : new Thickness(0),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false
        });

        // The primary window already has its title/brand and controls in the 52px
        // workspace header. Dialogs and detached windows receive one compact row.
        var titleRow = main ? "" : $$"""
          <Border Grid.Row="0" Height="32" Background="{{theme.Panel}}" BorderBrush="{{theme.Line}}" BorderThickness="0,0,0,1" AutomationProperties.AutomationId="window-integrated-title">
            <Grid>
              <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
              <Grid Margin="10,0,8,0" VerticalAlignment="Center" ClipToBounds="True">
                <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                <Image Width="16" Height="16" Source="{TemplateBinding Icon}" Margin="0,0,8,0"/>
                <TextBlock Grid.Column="1" Text="{TemplateBinding Title}" Foreground="{{theme.Text}}" FontSize="12" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
              </Grid>
              <StackPanel Grid.Column="1" Orientation="Horizontal" shell:WindowChrome.IsHitTestVisibleInChrome="True">
                <Button x:Name="minimize" Style="{StaticResource Yideck.CaptionButton}" Content="&#xE921;" ToolTip="最小化" AutomationProperties.AutomationId="window-minimize" Command="{x:Static SystemCommands.MinimizeWindowCommand}"/>
                <Button x:Name="maximize" Style="{StaticResource Yideck.CaptionButton}" Content="&#xE922;" ToolTip="最大化 / 还原" AutomationProperties.AutomationId="window-maximize" Command="{x:Static SystemCommands.MaximizeWindowCommand}"/>
                <Button Style="{StaticResource Yideck.CaptionCloseButton}" Content="&#xE8BB;" ToolTip="关闭" AutomationProperties.AutomationId="window-close" Command="{x:Static SystemCommands.CloseWindowCommand}"/>
              </StackPanel>
            </Grid>
          </Border>
        """;
        var triggers = main ? "" : """
          <ControlTemplate.Triggers>
            <Trigger Property="WindowState" Value="Maximized"><Setter TargetName="maximize" Property="Content" Value="&#xE923;"/></Trigger>
            <Trigger Property="ResizeMode" Value="NoResize"><Setter TargetName="minimize" Property="Visibility" Value="Collapsed"/><Setter TargetName="maximize" Property="Visibility" Value="Collapsed"/></Trigger>
            <Trigger Property="ResizeMode" Value="CanMinimize"><Setter TargetName="maximize" Property="Visibility" Value="Collapsed"/></Trigger>
          </ControlTemplate.Triggers>
        """;
        var xml = $$"""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:shell="clr-namespace:System.Windows.Shell;assembly=PresentationFramework">
          <Style x:Key="Yideck.CaptionButton" TargetType="Button">
            <Setter Property="Width" Value="34"/><Setter Property="Height" Value="32"/><Setter Property="MinHeight" Value="0"/><Setter Property="MinWidth" Value="0"/>
            <Setter Property="Padding" Value="0"/><Setter Property="FontFamily" Value="Segoe MDL2 Assets"/><Setter Property="FontSize" Value="10"/>
            <Setter Property="Background" Value="Transparent"/><Setter Property="Foreground" Value="{{theme.Text}}"/><Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Focusable" Value="True"/><Setter Property="shell:WindowChrome.IsHitTestVisibleInChrome" Value="True"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
              <Border x:Name="surface" Background="{TemplateBinding Background}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="surface" Property="Background" Value="{{theme.Card}}"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="surface" Property="Background" Value="{{theme.Accent}}"/><Setter Property="Foreground" Value="{{theme.AccentText}}"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="surface" Property="BorderBrush" Value="{{theme.Accent}}"/><Setter TargetName="surface" Property="BorderThickness" Value="1"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="Yideck.CaptionCloseButton" TargetType="Button" BasedOn="{StaticResource Yideck.CaptionButton}">
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
              <Border x:Name="surface" Background="{TemplateBinding Background}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="surface" Property="Background" Value="#C42B1C"/><Setter Property="Foreground" Value="White"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="surface" Property="Background" Value="#A22015"/><Setter Property="Foreground" Value="White"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="surface" Property="BorderBrush" Value="{{theme.Accent}}"/><Setter TargetName="surface" Property="BorderThickness" Value="1"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <ControlTemplate x:Key="Yideck.WindowTemplate" TargetType="Window">
            <Border Background="{TemplateBinding Background}" BorderBrush="{{theme.Line}}" BorderThickness="1">
              <Grid><Grid.RowDefinitions><RowDefinition Height="{{(main ? "0" : "32")}}"/><RowDefinition Height="*"/></Grid.RowDefinitions>
                {{titleRow}}
                <AdornerDecorator Grid.Row="1"><ContentPresenter Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}"/></AdornerDecorator>
              </Grid>
            </Border>
            {{triggers}}
          </ControlTemplate>
        </ResourceDictionary>
        """;
        var resources = (ResourceDictionary)XamlReader.Parse(xml);
        window.Resources.MergedDictionaries.Add(resources);
        window.Template = (ControlTemplate)resources["Yideck.WindowTemplate"];
    }

    internal static FrameworkElement CaptionButtons(Window window)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        panel.Loaded += (_, _) => CommandManager.InvalidateRequerySuggested();
        WindowChrome.SetIsHitTestVisibleInChrome(panel, true);
        Button Add(string id, string glyph, string tooltip, ICommand command, bool close = false)
        {
            var button = new Button { Content = glyph, ToolTip = tooltip, Command = command, CommandTarget = window };
            button.SetResourceReference(FrameworkElement.StyleProperty, close ? "Yideck.CaptionCloseButton" : "Yideck.CaptionButton");
            AutomationProperties.SetAutomationId(button, "window-" + id); AutomationProperties.SetName(button, tooltip);
            panel.Children.Add(button); return button;
        }
        Add("minimize", "\uE921", "最小化", SystemCommands.MinimizeWindowCommand);
        var max = Add("maximize", "\uE922", "最大化 / 还原", SystemCommands.MaximizeWindowCommand);
        max.SetBinding(ContentControl.ContentProperty, new Binding(nameof(Window.WindowState)) { Source = window, Converter = MaximizeGlyph.Instance });
        Add("close", "\uE8BB", "关闭", SystemCommands.CloseWindowCommand, true);
        return panel;
    }

    private sealed class MaximizeGlyph : IValueConverter
    {
        internal static readonly MaximizeGlyph Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is WindowState.Maximized ? "\uE923" : "\uE922";
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    // A borderless maximized HWND must use the current monitor's *work* area,
    // rather than extend the WPF content into the invisible resize frame.
    private static nint WorkAreaBounds(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x24 || lParam == 0) return 0; // WM_GETMINMAXINFO
        var monitor = MonitorFromWindow(hwnd, 2); var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return 0;
        var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        bounds.MaxPosition = new NativePoint { X = info.Work.Left - info.Monitor.Left, Y = info.Work.Top - info.Monitor.Top };
        bounds.MaxSize = new NativePoint { X = info.Work.Right - info.Work.Left, Y = info.Work.Bottom - info.Work.Top };
        // Continue to WPF's hook so its DPI-aware MinWidth/MinHeight tracking
        // constraints are retained. WPF does not replace MaxPosition/MaxSize.
        Marshal.StructureToPtr(bounds, lParam, false); return 0;
    }
    internal static double WorkAreaHeight(Window window)
    {
        var monitor = MonitorFromWindow(new WindowInteropHelper(window).Handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return monitor != 0 && GetMonitorInfo(monitor, ref info)
            ? (info.Work.Bottom - info.Work.Top) / VisualTreeHelper.GetDpi(window).DpiScaleY
            : SystemParameters.WorkArea.Height;
    }
    internal static double WorkAreaWidth(Window window)
    {
        var monitor = MonitorFromWindow(new WindowInteropHelper(window).Handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return monitor != 0 && GetMonitorInfo(monitor, ref info)
            ? (info.Work.Right - info.Work.Left) / VisualTreeHelper.GetDpi(window).DpiScaleX
            : SystemParameters.WorkArea.Width;
    }
    internal static Rect WorkAreaRect(Window window)
    {
        var monitor = MonitorFromWindow(new WindowInteropHelper(window).Handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(window);
        return new Rect(info.Work.Left / dpi.DpiScaleX, info.Work.Top / dpi.DpiScaleY,
            (info.Work.Right - info.Work.Left) / dpi.DpiScaleX, (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
