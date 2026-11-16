using System.IO;
using System.Windows;
using LightDeck.Core;

namespace LightDeck.App;

public sealed class App : Application
{
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new App();
        app.DispatcherUnhandledException += (_, e) => { MessageBox.Show(e.Exception.Message, "易音"); e.Handled = true; };
        try
        {
            var data = Environment.GetEnvironmentVariable("YIYIN_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yiyin");
            Directory.CreateDirectory(data);
            var requested = args.FirstOrDefault(a => a.EndsWith(".yiyin", StringComparison.OrdinalIgnoreCase));
            using var instance = new AppSingleInstance(Path.Combine(data, "current.yiyin"));
            if (!instance.IsPrimary) { instance.ActivateExistingAsync(requested).GetAwaiter().GetResult(); return 0; }
            var window = new MainWindow(data, requested);
            instance.Attach(window, window.OpenExternalProjectAsync);
            return app.Run(window);
        }
        catch (Exception e) { MessageBox.Show(e.Message, "易音启动失败"); return 1; }
    }
}
