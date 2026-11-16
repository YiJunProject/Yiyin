using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace LightDeck.App;

// App-owned confirmations/errors share themed chrome. OS file pickers remain
// native, and callers keep the same MessageBoxResult contract.
public static class ThemedMessageBox
{
    public static MessageBoxResult Show(string message,string caption,MessageBoxButton buttons=MessageBoxButton.OK,MessageBoxImage image=MessageBoxImage.None,MessageBoxResult defaultResult=MessageBoxResult.None)
        => Show(null,message,caption,buttons,image,defaultResult);

    public static MessageBoxResult Show(Window? owner,string message,string caption,MessageBoxButton buttons=MessageBoxButton.OK,MessageBoxImage image=MessageBoxImage.None,MessageBoxResult defaultResult=MessageBoxResult.None)
    {
        var app=Application.Current;
        if(app is not null&&!app.Dispatcher.CheckAccess())return app.Dispatcher.Invoke(()=>Show(owner,message,caption,buttons,image,defaultResult));
        var priorMain=app?.MainWindow;
        var themeOwner=owner??priorMain;
        var theme=themeOwner?.TryFindResource("LightDeck.Theme") as DeckTheme??DeckTheme.All[0];
        var actualOwner=themeOwner is {IsVisible:true}?themeOwner:null;
        var options=buttons switch
        {
            MessageBoxButton.OK=>new[]{MessageBoxResult.OK},
            MessageBoxButton.OKCancel=>new[]{MessageBoxResult.OK,MessageBoxResult.Cancel},
            MessageBoxButton.YesNo=>new[]{MessageBoxResult.Yes,MessageBoxResult.No},
            MessageBoxButton.YesNoCancel=>new[]{MessageBoxResult.Yes,MessageBoxResult.No,MessageBoxResult.Cancel},
            _=>throw new ArgumentOutOfRangeException(nameof(buttons))
        };
        var dismissed=options.Contains(MessageBoxResult.Cancel)?MessageBoxResult.Cancel:options.Contains(MessageBoxResult.No)?MessageBoxResult.No:MessageBoxResult.OK;
        var selected=defaultResult!=MessageBoxResult.None&&options.Contains(defaultResult)?defaultResult:dismissed;
        var result=dismissed;
        // A first-start error can be the first WPF Window. It must not replace
        // the application's future MainWindow or shut down its dispatcher.
        var priorShutdown=app?.ShutdownMode;
        if(app is not null)app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        Window? dialog=null;
        try
        {
            dialog=new Window {Title=caption,Owner=actualOwner,Width=540,MinWidth=360,SizeToContent=SizeToContent.Height,MaxHeight=Math.Max(240,SystemParameters.WorkArea.Height-80),ResizeMode=ResizeMode.NoResize,ShowInTaskbar=actualOwner is null,WindowStartupLocation=actualOwner is null?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner,Icon=actualOwner?.Icon};
            theme.Apply(dialog);AutomationProperties.SetAutomationId(dialog,"themed-message-box");
            var layout=new DockPanel {Margin=new Thickness(24)};
            var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,20,0,0)};
            DockPanel.SetDock(actions,Dock.Bottom);layout.Children.Add(actions);
            Button? initial=null;
            foreach(var value in options)
            {
                var label=value switch {MessageBoxResult.OK=>"确定",MessageBoxResult.Cancel=>"取消",MessageBoxResult.Yes=>"是",_=>"否"};
                var button=new Button {Content=label,MinWidth=92,Margin=new Thickness(8,0,0,0),IsDefault=value==selected,IsCancel=value==dismissed};
                AutomationProperties.SetAutomationId(button,"message-box-"+value.ToString().ToLowerInvariant());
                if(value==selected)initial=button;
                button.Click+=(_,_)=>{result=value;dialog.Close();};actions.Children.Add(button);
            }
            var body=new StackPanel();
            var header=new TextBlock {Text=caption,FontSize=22,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14)};body.Children.Add(header);
            if(image!=MessageBoxImage.None)
            {
                var kind=image==MessageBoxImage.Question?"请确认":image==MessageBoxImage.Warning?"提示":image==MessageBoxImage.Error?"操作未完成":"提示";
                body.Children.Add(new TextBlock {Text=kind,Foreground=DeckTheme.Brush(theme.Accent),FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,8)});
            }
            var text=new TextBlock {Text=message,TextWrapping=TextWrapping.Wrap,LineHeight=23,FontSize=14};AutomationProperties.SetAutomationId(text,"message-box-message");body.Children.Add(text);
            layout.Children.Add(new ScrollViewer {Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,MaxHeight=Math.Max(120,SystemParameters.WorkArea.Height-260)});
            dialog.Content=layout;dialog.Loaded+=(_,_)=>initial?.Focus();dialog.ShowDialog();return result;
        }
        finally
        {
            if(app is not null)
            {
                if(ReferenceEquals(app.MainWindow,dialog))app.MainWindow=priorMain;
                app.ShutdownMode=priorShutdown!.Value;
            }
        }
    }
}
