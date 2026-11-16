using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;

namespace LightDeck.App;

// Shared native WPF templates. Keep the named Track/Thumb/Popup parts so the
// framework continues to own input, keyboard navigation and automation peers.
internal static class ThemedControls
{
    public static void AddTo(ResourceDictionary resources, DeckTheme t)
    {
        var radius = Math.Min(10, t.ButtonRadius);
        var thumbRadius = t.Material == "contrast" ? 0 : 9;
        var border = t.Material == "contrast" ? 2 : 1;
        var xml = $$"""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style x:Key="DeckEmptyRepeat" TargetType="RepeatButton">
            <Setter Property="Focusable" Value="False"/><Setter Property="IsTabStop" Value="False"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="DeckSliderFill" TargetType="RepeatButton" BasedOn="{StaticResource DeckEmptyRepeat}">
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RepeatButton"><Border Height="8" Margin="0,0,-9,0" VerticalAlignment="Center" Background="{{t.Accent}}" CornerRadius="{{Math.Min(4, t.ButtonRadius)}}"/></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="DeckSliderVerticalFill" TargetType="RepeatButton" BasedOn="{StaticResource DeckEmptyRepeat}">
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RepeatButton"><Border Width="8" Margin="0,-9,0,0" HorizontalAlignment="Center" Background="{{t.Accent}}" CornerRadius="{{Math.Min(4, t.ButtonRadius)}}"/></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="DeckSliderThumb" TargetType="Thumb">
            <Setter Property="Width" Value="18"/><Setter Property="Height" Value="18"/><Setter Property="Focusable" Value="False"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Thumb">
              <Grid Background="Transparent">
                <Border x:Name="Halo" Margin="-3" CornerRadius="{{thumbRadius + (thumbRadius == 0 ? 0 : 3)}}" BorderBrush="{{t.Accent}}" BorderThickness="2" Opacity="0"/>
                <Border x:Name="Knob" Background="{{t.Accent}}" BorderBrush="{{(t.Light ? t.Text : "#FFFFFF")}}" BorderThickness="3" CornerRadius="{{thumbRadius}}"/>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Halo" Property="Opacity" Value="0.65"/></Trigger>
                <Trigger Property="IsDragging" Value="True"><Setter TargetName="Halo" Property="Opacity" Value="1"/><Setter TargetName="Halo" Property="BorderThickness" Value="3"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <ControlTemplate x:Key="DeckSliderHorizontal" TargetType="Slider">
            <Grid x:Name="SliderSurface" Background="Transparent" MinHeight="24">
              <Border Height="8" Margin="9,0" Background="{{t.Input}}" BorderBrush="{{t.Line}}" BorderThickness="1" CornerRadius="{{Math.Min(4, t.ButtonRadius)}}"/>
              <Track x:Name="PART_Track" Orientation="Horizontal" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}" IsDirectionReversed="{TemplateBinding IsDirectionReversed}">
                <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Style="{StaticResource DeckSliderFill}"/></Track.DecreaseRepeatButton>
                <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Style="{StaticResource DeckEmptyRepeat}"/></Track.IncreaseRepeatButton>
                <Track.Thumb><Thumb Style="{StaticResource DeckSliderThumb}"/></Track.Thumb>
              </Track>
              <Border x:Name="Focus" Margin="0,-1" BorderBrush="{{t.Accent}}" BorderThickness="1" CornerRadius="{{radius}}" IsHitTestVisible="False" Visibility="Collapsed"/>
            </Grid>
            <ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Focus" Property="Visibility" Value="Visible"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="SliderSurface" Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers>
          </ControlTemplate>
          <ControlTemplate x:Key="DeckSliderVertical" TargetType="Slider">
            <Grid x:Name="SliderSurface" Background="Transparent" MinWidth="24">
              <Border Width="8" Margin="0,9" Background="{{t.Input}}" BorderBrush="{{t.Line}}" BorderThickness="1" CornerRadius="{{Math.Min(4, t.ButtonRadius)}}"/>
              <Track x:Name="PART_Track" Orientation="Vertical" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}" IsDirectionReversed="{TemplateBinding IsDirectionReversed}">
                <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Style="{StaticResource DeckSliderVerticalFill}"/></Track.DecreaseRepeatButton>
                <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Style="{StaticResource DeckEmptyRepeat}"/></Track.IncreaseRepeatButton>
                <Track.Thumb><Thumb Style="{StaticResource DeckSliderThumb}"/></Track.Thumb>
              </Track>
            </Grid>
            <ControlTemplate.Triggers><Trigger Property="IsEnabled" Value="False"><Setter TargetName="SliderSurface" Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers>
          </ControlTemplate>
          <Style TargetType="Slider"><Setter Property="Focusable" Value="True"/><Setter Property="Template" Value="{StaticResource DeckSliderHorizontal}"/><Style.Triggers><Trigger Property="Orientation" Value="Vertical"><Setter Property="Template" Value="{StaticResource DeckSliderVertical}"/></Trigger></Style.Triggers></Style>
          <Style x:Key="DeckScrollThumb" TargetType="Thumb">
            <Setter Property="MinWidth" Value="18"/><Setter Property="MinHeight" Value="18"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Thumb"><Border x:Name="ScrollThumb" Margin="3" Background="{{t.Muted}}" CornerRadius="{{Math.Min(5,t.ButtonRadius)}}"/><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="ScrollThumb" Property="Background" Value="{{t.Accent}}"/></Trigger><Trigger Property="IsDragging" Value="True"><Setter TargetName="ScrollThumb" Property="Background" Value="{{t.Accent}}"/><Setter TargetName="ScrollThumb" Property="Margin" Value="2"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <ControlTemplate x:Key="DeckScrollVertical" TargetType="ScrollBar"><Grid Background="{{t.Input}}"><Track x:Name="PART_Track" IsDirectionReversed="True" Orientation="Vertical" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}" ViewportSize="{TemplateBinding ViewportSize}"><Track.DecreaseRepeatButton><RepeatButton Command="ScrollBar.PageUpCommand" Style="{StaticResource DeckEmptyRepeat}"/></Track.DecreaseRepeatButton><Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.PageDownCommand" Style="{StaticResource DeckEmptyRepeat}"/></Track.IncreaseRepeatButton><Track.Thumb><Thumb Style="{StaticResource DeckScrollThumb}"/></Track.Thumb></Track></Grid></ControlTemplate>
          <ControlTemplate x:Key="DeckScrollHorizontal" TargetType="ScrollBar"><Grid Background="{{t.Input}}"><Track x:Name="PART_Track" IsDirectionReversed="False" Orientation="Horizontal" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}" ViewportSize="{TemplateBinding ViewportSize}"><Track.DecreaseRepeatButton><RepeatButton Command="ScrollBar.PageLeftCommand" Style="{StaticResource DeckEmptyRepeat}"/></Track.DecreaseRepeatButton><Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.PageRightCommand" Style="{StaticResource DeckEmptyRepeat}"/></Track.IncreaseRepeatButton><Track.Thumb><Thumb Style="{StaticResource DeckScrollThumb}"/></Track.Thumb></Track></Grid></ControlTemplate>
          <Style TargetType="ScrollBar"><Setter Property="Width" Value="18"/><Setter Property="MinWidth" Value="18"/><Setter Property="Focusable" Value="False"/><Setter Property="Template" Value="{StaticResource DeckScrollVertical}"/><Style.Triggers><Trigger Property="Orientation" Value="Horizontal"><Setter Property="Width" Value="Auto"/><Setter Property="Height" Value="18"/><Setter Property="MinHeight" Value="18"/><Setter Property="Template" Value="{StaticResource DeckScrollHorizontal}"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></Style.Triggers></Style>
          <Style TargetType="ScrollViewer"><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollViewer"><Grid Background="{TemplateBinding Background}"><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions><Grid.RowDefinitions><RowDefinition/><RowDefinition Height="Auto"/></Grid.RowDefinitions><ScrollContentPresenter x:Name="PART_ScrollContentPresenter" Margin="{TemplateBinding Padding}" Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}" CanContentScroll="{TemplateBinding CanContentScroll}"/><ScrollBar x:Name="PART_VerticalScrollBar" Grid.Column="1" Orientation="Vertical" Minimum="0" Maximum="{TemplateBinding ScrollableHeight}" Value="{TemplateBinding VerticalOffset}" ViewportSize="{TemplateBinding ViewportHeight}" Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}"/><ScrollBar x:Name="PART_HorizontalScrollBar" Grid.Row="1" Orientation="Horizontal" Minimum="0" Maximum="{TemplateBinding ScrollableWidth}" Value="{TemplateBinding HorizontalOffset}" ViewportSize="{TemplateBinding ViewportWidth}" Visibility="{TemplateBinding ComputedHorizontalScrollBarVisibility}"/><Border x:Name="ScrollCorner" Grid.Row="1" Grid.Column="1" Background="{{t.Input}}"/></Grid></ControlTemplate></Setter.Value></Setter></Style>
          <ControlTemplate x:Key="DeckTextInput" TargetType="TextBox"><Border x:Name="InputShell" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="{{Math.Min(5,radius)}}"><ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" Focusable="False"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="InputShell" Property="BorderBrush" Value="{{t.Accent}}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="InputShell" Property="BorderBrush" Value="{{t.Accent}}"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="InputShell" Property="Opacity" Value="0.48"/></Trigger></ControlTemplate.Triggers></ControlTemplate>
          <ControlTemplate x:Key="DeckPasswordInput" TargetType="PasswordBox"><Border x:Name="InputShell" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="{{Math.Min(5,radius)}}"><ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" Focusable="False"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="InputShell" Property="BorderBrush" Value="{{t.Accent}}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="InputShell" Property="BorderBrush" Value="{{t.Accent}}"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="InputShell" Property="Opacity" Value="0.48"/></Trigger></ControlTemplate.Triggers></ControlTemplate>
          <Style TargetType="ContextMenu">
            <Setter Property="Foreground" Value="{{t.Text}}"/><Setter Property="Background" Value="{{t.Panel}}"/><Setter Property="FontFamily" Value="Microsoft YaHei UI, Segoe UI"/><Setter Property="FontSize" Value="13"/><Setter Property="Padding" Value="5"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ContextMenu"><Border x:Name="MenuSurface" Background="{TemplateBinding Background}" BorderBrush="{{t.Line}}" BorderThickness="{{border}}" CornerRadius="{{radius}}" Padding="{TemplateBinding Padding}"><ScrollViewer CanContentScroll="True" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle"/></ScrollViewer></Border></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="Separator"><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Separator"><Border Height="1" Margin="9,5" Background="{{t.Line}}"/></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="MenuItem">
            <Setter Property="Foreground" Value="{{t.Text}}"/><Setter Property="Background" Value="Transparent"/><Setter Property="Padding" Value="9,8"/><Setter Property="MinHeight" Value="34"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="MenuItem">
              <Border x:Name="MenuRow" Background="{TemplateBinding Background}" CornerRadius="{{Math.Min(6,radius)}}" Padding="{TemplateBinding Padding}">
                <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="21"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="16"/></Grid.ColumnDefinitions>
                  <ContentPresenter x:Name="Icon" ContentSource="Icon" Width="16" Height="16" VerticalAlignment="Center"/>
                  <Path x:Name="Checked" Data="M 2,8 L 6,12 L 14,3" Stroke="{TemplateBinding Foreground}" StrokeThickness="2" Width="16" Height="16" VerticalAlignment="Center" Visibility="Collapsed"/>
                  <ContentPresenter Grid.Column="1" ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center"/>
                  <TextBlock Grid.Column="2" Text="{TemplateBinding InputGestureText}" Margin="24,0,4,0" Foreground="{TemplateBinding Foreground}" Opacity="0.8" VerticalAlignment="Center"/>
                  <Path x:Name="Arrow" Grid.Column="3" Data="M 2,2 L 6,6 L 2,10" Stroke="{TemplateBinding Foreground}" StrokeThickness="1.5" HorizontalAlignment="Center" VerticalAlignment="Center" Visibility="Collapsed"/>
                  <Popup x:Name="PART_Popup" Placement="Right" HorizontalOffset="-1" AllowsTransparency="True" Focusable="False" IsOpen="{Binding IsSubmenuOpen,RelativeSource={RelativeSource TemplatedParent},Mode=TwoWay}"><Border Background="{{t.Panel}}" BorderBrush="{{t.Line}}" BorderThickness="{{border}}" CornerRadius="{{radius}}" Padding="5"><ScrollViewer CanContentScroll="True" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle"/></ScrollViewer></Border></Popup>
                </Grid>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="MenuRow" Property="Background" Value="{{t.Accent}}"/><Setter Property="Foreground" Value="{{t.AccentText}}"/></Trigger>
                <Trigger Property="IsChecked" Value="True"><Setter TargetName="Checked" Property="Visibility" Value="Visible"/><Setter TargetName="Icon" Property="Visibility" Value="Collapsed"/></Trigger>
                <Trigger Property="HasItems" Value="True"><Setter TargetName="Arrow" Property="Visibility" Value="Visible"/></Trigger>
                <Trigger Property="Role" Value="TopLevelHeader"><Setter TargetName="PART_Popup" Property="Placement" Value="Bottom"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="{{t.Muted}}"/><Setter TargetName="MenuRow" Property="Opacity" Value="0.55"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="CheckBox"><Setter Property="Foreground" Value="{{t.Text}}"/><Setter Property="MinHeight" Value="32"/><Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox"><Grid x:Name="CheckRoot" Background="Transparent"><Grid.ColumnDefinitions><ColumnDefinition Width="22"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions><Border x:Name="CheckBox" Width="18" Height="18" HorizontalAlignment="Left" VerticalAlignment="Center" Background="{{t.Input}}" BorderBrush="{{t.Line}}" BorderThickness="{{border}}" CornerRadius="{{Math.Min(4,radius)}}"><Grid><Path x:Name="CheckMark" Data="M 2,7 L 5,10 L 12,2" Stroke="{{t.AccentText}}" StrokeThickness="2" HorizontalAlignment="Center" VerticalAlignment="Center" Visibility="Collapsed"/><Border x:Name="Indeterminate" Height="3" Width="10" Background="{{t.AccentText}}" Visibility="Collapsed"/></Grid></Border><ContentPresenter Grid.Column="1" Margin="4,0,0,0" VerticalAlignment="{TemplateBinding VerticalContentAlignment}" RecognizesAccessKey="True"/></Grid><ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="CheckBox" Property="Background" Value="{{t.Accent}}"/><Setter TargetName="CheckBox" Property="BorderBrush" Value="{{t.Accent}}"/><Setter TargetName="CheckMark" Property="Visibility" Value="Visible"/></Trigger><Trigger Property="IsChecked" Value="{x:Null}"><Setter TargetName="CheckBox" Property="Background" Value="{{t.Accent}}"/><Setter TargetName="Indeterminate" Property="Visibility" Value="Visible"/></Trigger><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="CheckBox" Property="BorderBrush" Value="{{t.Accent}}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="CheckBox" Property="BorderBrush" Value="{{t.Accent}}"/><Setter TargetName="CheckBox" Property="BorderThickness" Value="2"/></Trigger><Trigger Property="IsPressed" Value="True"><Setter TargetName="CheckRoot" Property="Opacity" Value="0.75"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="CheckRoot" Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="Expander"><Setter Property="Foreground" Value="{{t.Text}}"/><Setter Property="Background" Value="{{t.Panel}}"/><Setter Property="BorderBrush" Value="{{t.Line}}"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Expander"><Border BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Background="{TemplateBinding Background}" CornerRadius="{{radius}}"><DockPanel><ToggleButton x:Name="HeaderButton" DockPanel.Dock="Top" Content="{TemplateBinding Header}" ContentTemplate="{TemplateBinding HeaderTemplate}" Foreground="{TemplateBinding Foreground}" IsChecked="{Binding IsExpanded,RelativeSource={RelativeSource TemplatedParent},Mode=TwoWay}"><ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border x:Name="HeaderSurface" Padding="10,5" Background="Transparent"><DockPanel><TextBlock x:Name="Arrow" DockPanel.Dock="Left" Text="›" FontSize="22" Margin="0,0,10,0" VerticalAlignment="Center"/><ContentPresenter VerticalAlignment="Center" RecognizesAccessKey="True"/></DockPanel></Border><ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="Arrow" Property="Text" Value="⌄"/></Trigger><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="HeaderSurface" Property="Background" Value="{{t.Card}}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="HeaderSurface" Property="BorderBrush" Value="{{t.Accent}}"/><Setter TargetName="HeaderSurface" Property="BorderThickness" Value="1"/></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template></ToggleButton><ContentPresenter x:Name="ExpandSite" Visibility="Collapsed" ContentSource="Content"/></DockPanel></Border><ControlTemplate.Triggers><Trigger Property="IsExpanded" Value="True"><Setter TargetName="ExpandSite" Property="Visibility" Value="Visible"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="ProgressBar"><Setter Property="Foreground" Value="{{t.Accent}}"/><Setter Property="Background" Value="{{t.Input}}"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ProgressBar"><Grid x:Name="PART_Track" Background="{TemplateBinding Background}" ClipToBounds="True"><Border x:Name="PART_Indicator" HorizontalAlignment="Left" Background="{TemplateBinding Foreground}" CornerRadius="{{Math.Min(4,radius)}}"/><Border x:Name="Pulse" Background="{TemplateBinding Foreground}" Opacity="0"/></Grid><ControlTemplate.Triggers><Trigger Property="IsIndeterminate" Value="True"><Setter TargetName="PART_Indicator" Property="Visibility" Value="Collapsed"/><Trigger.EnterActions><BeginStoryboard x:Name="PulseAnimation"><Storyboard><DoubleAnimation Storyboard.TargetName="Pulse" Storyboard.TargetProperty="Opacity" From="0.2" To="0.8" Duration="0:0:0.7" AutoReverse="True" RepeatBehavior="Forever"/></Storyboard></BeginStoryboard></Trigger.EnterActions><Trigger.ExitActions><RemoveStoryboard BeginStoryboardName="PulseAnimation"/></Trigger.ExitActions></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="ToolTip"><Setter Property="Foreground" Value="{{t.Text}}"/><Setter Property="Background" Value="{{t.Panel}}"/><Setter Property="BorderBrush" Value="{{t.Line}}"/><Setter Property="Padding" Value="10,7"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToolTip"><Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="{{radius}}" Padding="{TemplateBinding Padding}"><ContentPresenter/></Border></ControlTemplate></Setter.Value></Setter></Style>
        </ResourceDictionary>
        """;
        var additions=(ResourceDictionary)XamlReader.Parse(xml);
        foreach(DictionaryEntry entry in additions)resources[entry.Key]=entry.Value;
        // Menu containers use this framework key instead of the implicit type style.
        resources[MenuItem.SeparatorStyleKey]=resources[typeof(Separator)];
        // Preserve the established text/combo structure, while completing contrast
        // and disabled states that the old system defaults did not cover.
        // Default WPF adorner selection paints over text: keep it translucent.
        // Do not change the process-wide text-rendering compatibility switch.
        var text=(Style)resources[typeof(TextBox)];text.Setters.Add(new Setter(TextBox.SelectionTextBrushProperty,DeckTheme.Brush(t.AccentText)));text.Setters.Add(new Setter(TextBox.SelectionOpacityProperty,.4));text.Setters.Add(new Setter(Control.TemplateProperty,resources["DeckTextInput"]));
        var password=(Style)resources[typeof(PasswordBox)];password.Setters.Add(new Setter(PasswordBox.SelectionBrushProperty,DeckTheme.Brush(t.Accent)));password.Setters.Add(new Setter(PasswordBox.SelectionTextBrushProperty,DeckTheme.Brush(t.AccentText)));password.Setters.Add(new Setter(PasswordBox.SelectionOpacityProperty,.4));password.Setters.Add(new Setter(Control.TemplateProperty,resources["DeckPasswordInput"]));
        // WPF's private EditorContextMenu uses a system style, even when our
        // normal ContextMenu resource is present. Explicit standard commands
        // retain read-only/password restrictions and keyboard behavior.
        text.Setters.Add(new Setter(FrameworkElement.ContextMenuProperty,TextMenu(resources,false)));
        password.Setters.Add(new Setter(FrameworkElement.ContextMenuProperty,TextMenu(resources,true)));
        var combo=(Style)resources[typeof(ComboBox)];var disabled=new Trigger {Property=UIElement.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,.48));combo.Triggers.Add(disabled);
    }
    private static ContextMenu TextMenu(ResourceDictionary resources,bool password)
    {
        var menu=new ContextMenu {Style=(Style)resources[typeof(ContextMenu)]};
        void Add(string label,ICommand command,string shortcut)
        {
            var item=new MenuItem {Header=label,Command=command,InputGestureText=shortcut,Style=(Style)resources[typeof(MenuItem)]};
            item.SetBinding(MenuItem.CommandTargetProperty,new Binding("PlacementTarget") {RelativeSource=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(ContextMenu),1)});
            menu.Items.Add(item);
        }
        if(!password){Add("剪切",ApplicationCommands.Cut,"Ctrl+X");Add("复制",ApplicationCommands.Copy,"Ctrl+C");}
        Add("粘贴",ApplicationCommands.Paste,"Ctrl+V");Add("全选",ApplicationCommands.SelectAll,"Ctrl+A");
        return menu;
    }
}
