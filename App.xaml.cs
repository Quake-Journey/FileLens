using System.IO;
using System.Windows;

namespace FileLens;

public partial class App : Application
{
    private SingleInstance? instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool checkFile = e.Args.FirstOrDefault() == "--check-file";
        bool elevatedRelaunch = e.Args.FirstOrDefault() == "--elevated-relaunch";
        if (e.Args.Length > 0 && e.Args[0].StartsWith("--", StringComparison.Ordinal) && !checkFile && !elevatedRelaunch)
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
        string? path = checkFile || elevatedRelaunch ? e.Args.ElementAtOrDefault(1) : e.Args.FirstOrDefault();
        if (checkFile && path is null) { Shutdown(2); return; }
        try
        {
            instance = new SingleInstance();
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (!instance.TryOwn())
            {
                if (!elevatedRelaunch)
                {
                    try
                    {
                        var reply = await SingleInstance.SendAsync(new ActivationRequest { Path = path }, 3000);
                        if (reply.Accepted) { Shutdown(); return; }
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
                }
                if (DateTime.UtcNow >= deadline) throw new IOException("Окно FileLens уже открыто, но не отвечает. Повтори запуск через несколько секунд.");
                await Task.Delay(100);
            }
            var window = new MainWindow(path);
            MainWindow = window;
            if (Environment.GetEnvironmentVariable("FILELENS_TEST_INSTANCE_SCOPE") is not null)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = window.Top = -20000;
                window.ShowInTaskbar = false;
            }
            instance.StartServer(request => window.Dispatcher.InvokeAsync(() =>
            {
                if (request.Action == "State") return window.GetActivationState();
                if (request.Action != "Activate") return new ActivationReply { Accepted = false };
                _ = window.OpenRequestedFileAsync(request.Path);
                return new ActivationReply();
            }).Task);
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось открыть FileLens", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
