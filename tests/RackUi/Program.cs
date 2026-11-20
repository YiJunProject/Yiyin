using System.IO;
using System.Windows;
using LightDeck.App;

internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        var directory = Path.GetFullPath(args[1]); Directory.CreateDirectory(directory);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var exit = 0;
        app.Startup += async (_, _) =>
        {
            try
            {
                if (args[0] == "layout") await Rack1407LayoutChecks.RunAsync(directory);
                else if (args[0] == "io") await RackWorkspaceAudit.RunIoAsync(directory);
                else if (args[0] == "input") await Rack1408InputChecks.RunAsync(directory);
                else await RackWorkspaceAudit.RunDesignAsync(directory);
            }
            catch (Exception ex) { exit = 1; File.WriteAllText(Path.Combine(directory, "error.txt"), ex.ToString()); }
            finally { app.Shutdown(exit); }
        };
        app.Run(); return exit;
    }
}
