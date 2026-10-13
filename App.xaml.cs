using System.IO;
using System.Windows;

namespace FileLens;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0].StartsWith("--", StringComparison.Ordinal))
        {
            try { Shutdown(CommandLine.Run(e.Args)); }
            catch (Exception ex)
            {
                if (e.Args.Length > 2 && e.Args[0] == "--scan-json")
                    File.WriteAllText(e.Args[2], System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message }));
                Shutdown(1);
            }
            return;
        }
        var window = new MainWindow(e.Args.FirstOrDefault());
        MainWindow = window;
        window.Show();
    }
}
