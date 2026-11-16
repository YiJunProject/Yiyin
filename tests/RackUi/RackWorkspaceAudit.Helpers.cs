using System.Reflection;
using System.Windows;
using System.Windows.Input;
using LightDeck.Core;
namespace LightDeck.App;
public static partial class RackWorkspaceAudit
{
    private static RackLocalSettings CopyLocal(RackLocalSettings local) => (RackLocalSettings)typeof(RackWorkspaceControl).GetMethod("CopyLocal", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [local])!;
    private static void Double(UIElement target, RoutedEvent routedEvent)
    {
        var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routedEvent };
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(e, 2);
        target.RaiseEvent(e);
    }
}
