using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightDeck.Core;
using Microsoft.Win32;

namespace LightDeck.App;

public class DeckDialog : Window
{
    protected readonly DeckTheme Theme;
    protected readonly StackPanel Body;
    protected readonly StackPanel Footer;
    protected readonly StackPanel TitleActions;
    protected readonly ScrollViewer BodyScroll;
    protected readonly TextBlock Error;
    protected DeckDialog(Window owner, DeckTheme theme, string title, string explanation, double width = 540)
    {
        Owner = owner; Theme = theme; Title = title; Width = width; SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 70); MinWidth = width;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        theme.Apply(this);
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var titlePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold, Foreground = DeckTheme.Brush(theme.Text), VerticalAlignment = VerticalAlignment.Center });
        TitleActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; Grid.SetColumn(TitleActions, 1); heading.Children.Add(TitleActions); titlePanel.Children.Add(heading);
        titlePanel.Children.Add(new TextBlock { Text = explanation, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = DeckTheme.Brush(theme.Muted), LineHeight = 20 });
        root.Children.Add(titlePanel);
        Body = new StackPanel(); BodyScroll = new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(280, SystemParameters.WorkArea.Height - 310) };
        Grid.SetRow(BodyScroll, 1); root.Children.Add(BodyScroll);
        var footerPanel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        Error = new TextBlock { Foreground = DeckTheme.Brush(theme.Light ? "#B13A32" : "#FFB39B"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 10) };
        footerPanel.Children.Add(Error);
        Footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; footerPanel.Children.Add(Footer);
        Grid.SetRow(footerPanel, 2); root.Children.Add(footerPanel); Content = root;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }
    protected TextBlock Label(string text) => new() { Text = text, Foreground = DeckTheme.Brush(Theme.Muted), Margin = new Thickness(0, 13, 0, 6), FontSize = 12 };
    protected TextBox Field(string label, string value, string automationId)
    {
        Body.Children.Add(Label(label)); var field = new TextBox { Text = value }; AutomationProperties.SetAutomationId(field, automationId); Body.Children.Add(field); return field;
    }
    protected TextBlock SectionLabel(string text, double top = 0) => new() { Text = text, Foreground = DeckTheme.Brush(Theme.Muted), Margin = new Thickness(0, top, 0, 6), FontSize = 12 };
    protected TextBox SectionField(Panel panel, string label, string value, string automationId, double top = 0)
    {
        panel.Children.Add(SectionLabel(label, top)); var field = new TextBox { Text = value }; AutomationProperties.SetAutomationId(field, automationId); panel.Children.Add(field); return field;
    }
    protected void UseEditorLayout()
    {
        MinWidth = Math.Min(560, SystemParameters.WorkArea.Width - 40); Width = Math.Min(920, SystemParameters.WorkArea.Width - 60);
        ResizeMode = ResizeMode.CanResizeWithGrip; BodyScroll.MaxHeight = Math.Max(280, SystemParameters.WorkArea.Height - 230);
    }
    protected Button AddButton(string text, Action clicked, bool primary = false, string? id = null)
    {
        var button = new Button { Content = text, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        if (primary) { button.Background = DeckTheme.Brush(Theme.Accent); button.Foreground = DeckTheme.Brush(Theme.AccentText); button.BorderBrush = DeckTheme.Brush(Theme.Accent); }
        if (id is not null) AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => clicked(); Footer.Children.Add(button); return button;
    }
    protected void Invalid(string message) { Error.Text = message; Error.Visibility = Visibility.Visible; }
    protected static TextBox SmallNumber(string value) => new() { Text = value, MinWidth = 130 };
}
