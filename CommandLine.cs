using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FileLens;

internal static class CommandLine
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static int Run(string[] args)
    {
        if (args.Length == 1 && args[0] == "--register-context-menu") { ShellIntegration.Register(); return 0; }
        if (args.Length == 1 && args[0] == "--unregister-context-menu") { ShellIntegration.Unregister(); return 0; }
        if (args.Length == 3 && args[0] == "--scan-json")
        {
            ScanEnvelope envelope;
            try { envelope = new() { Result = FileScanner.Scan(args[1]) }; }
            catch (Exception ex) { envelope = new() { Error = ex.Message }; }
            File.WriteAllText(args[2], JsonSerializer.Serialize(envelope, JsonOptions));
            return envelope.Result is null ? 1 : 0;
        }
        if (args.Length == 5 && args[0] == "--hold-file")
        {
            FileShare sharing = args[4] switch { "exclusive" => FileShare.None, "delete" => FileShare.ReadWrite | FileShare.Delete, _ => FileShare.Read };
            using var stream = new FileStream(args[1], FileMode.Open, FileAccess.Read, sharing);
            File.WriteAllText(args[2], Environment.ProcessId.ToString());
            WaitForRelease(args[3]);
            return 0;
        }
        if (args.Length == 4 && args[0] == "--load-library")
        {
            var module = Native.LoadLibrary(args[1]);
            if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try { File.WriteAllText(args[2], Environment.ProcessId.ToString()); WaitForRelease(args[3]); }
            finally { Native.FreeLibrary(module); }
            return 0;
        }
        if (args.Length == 3 && args[0] == "--self-test") return SelfTest(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--preview")
        {
            var window = new MainWindow();
            RenderPreview(window, args[1]);
            return 0;
        }
        return 2;
    }

    private static void WaitForRelease(string path)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path) && timer.Elapsed < TimeSpan.FromMinutes(3)) Thread.Sleep(100);
    }

    private static ScanResult Scan(string path) => Task.Run(() => ScanClient.RunAsync(path, CancellationToken.None)).GetAwaiter().GetResult();

    private static int SelfTest(string output, string artifactDirectory)
    {
        // Preserve the user's clipboard when exercising the real copy button.
        IDataObject? previousClipboard = null;
        try { previousClipboard = Clipboard.GetDataObject(); } catch (System.Runtime.InteropServices.COMException) { }
        Directory.CreateDirectory(artifactDirectory);
        string assetDirectory = System.IO.Path.Combine(artifactDirectory, "test-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(assetDirectory);
        string target = System.IO.Path.Combine(assetDirectory, "Тест блокировки с пробелами.txt");
        File.WriteAllText(target, "FileLens: this content must never change.\n");
        string originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target)));
        var checks = new List<object>();
        int failed = 0;
        void Check(string name, bool passed, string detail = "")
        {
            checks.Add(new { name, passed, detail });
            if (!passed) failed++;
        }
        var helpers = new List<Helper>();
        try
        {
            Check("Quoted Unicode path", FileScanner.NormalizePath($"  \"{target}\"  ") == target);
            Check("Exact Far Manager Afterburner path", FileScanner.NormalizePath("\"C:\\Program Files (x86)\\MSI Afterburner\\\"RTCore64.sys  ") == @"C:\Program Files (x86)\MSI Afterburner\RTCore64.sys");
            Check("Unmatched opening quote", FileScanner.NormalizePath($"\"{target}") == target);
            Check("Unmatched closing quote", FileScanner.NormalizePath($"{target}\"") == target);
            Check("Valid apostrophes and spaces preserved", FileScanner.NormalizePath(@"C:\O'Brien\My file's name.txt") == @"C:\O'Brien\My file's name.txt");
            Check("File URI", FileScanner.NormalizePath(new Uri(target).AbsoluteUri) == target);
            string environmentTarget = Environment.GetEnvironmentVariable("FILELENS_TEST_PATH") ?? "";
            Environment.SetEnvironmentVariable("FILELENS_TEST_PATH", target);
            try { Check("Environment variable path", FileScanner.NormalizePath("%FILELENS_TEST_PATH%") == target); }
            finally { Environment.SetEnvironmentVariable("FILELENS_TEST_PATH", environmentTarget.Length == 0 ? null : environmentTarget); }
            try { FileScanner.NormalizePath("relative.txt"); Check("Reject relative path", false); }
            catch (ArgumentException) { Check("Reject relative path", true); }

            var a = Helper.Start("--hold-file", target, assetDirectory, "read"); helpers.Add(a);
            string holderScript = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(artifactDirectory)!, "tests", "holder32.ps1");
            var b = Helper.StartPowerShellHolder(target, assetDirectory, holderScript); helpers.Add(b);
            string farTarget = $"\"{System.IO.Path.GetDirectoryName(target)}\\\"{System.IO.Path.GetFileName(target)}  ";
            var held = Scan(farTarget);
            Check("Far path normalized during real scan", held.Path == target);
            Check("Two file owners detected", held.Processes.Any(p => p.Pid == a.Pid) && held.Processes.Any(p => p.Pid == b.Pid), JsonSerializer.Serialize(held.Processes));
            Check("Deletion sharing conflict detected", held.DeleteAccessError is 32 or 33, held.DeleteAccessError.ToString());
            Check("Executable paths of distinct programs resolved", held.Processes.Any(p => p.Pid == a.Pid && p.ExecutablePath.Equals(a.ExecutablePath, StringComparison.OrdinalIgnoreCase)) && held.Processes.Any(p => p.Pid == b.Pid && p.ExecutablePath.Equals(b.ExecutablePath, StringComparison.OrdinalIgnoreCase)));
            File.WriteAllText(System.IO.Path.Combine(artifactDirectory, "locked-scan.json"), JsonSerializer.Serialize(held, JsonOptions));
            File.WriteAllText(System.IO.Path.Combine(artifactDirectory, "example-report.txt"), held.ToReport());

            var window = new MainWindow();
            window.Present(held);
            Check("GUI shows both owners", window.ProcessGrid.Items.Count == held.Processes.Count && window.ProcessCount.Text == held.Processes.Count.ToString());
            Check("GUI shows blocked state", window.StatusTitle.Text == "Удаление блокируется");
            Check("GUI provides selected EXE path", window.SelectedPath.Text == held.Processes[0].DisplayPath && window.RevealButton.IsEnabled);
            var firstOwner = held.Processes.Single(p => p.Pid == a.Pid);
            var secondOwner = held.Processes.Single(p => p.Pid == b.Pid);
            Check("Copy-path test uses different executable paths", !firstOwner.ExecutablePath.Equals(secondOwner.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            window.ProcessGrid.SelectedItem = firstOwner;
            window.CopyPathButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Copy path uses first selected owner", window.CopyPathButton.IsEnabled && Clipboard.GetText() == firstOwner.ExecutablePath);
            window.ProcessGrid.SelectedItem = secondOwner;
            window.CopyPathButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Copy path follows second selected owner", window.CopyPathButton.IsEnabled && Clipboard.GetText() == secondOwner.ExecutablePath && window.SelectedPath.Text == secondOwner.ExecutablePath && window.RevealButton.IsEnabled);
            window.ProcessGrid.SelectedIndex = -1;
            Check("Row actions disabled with no selection", !window.CopyPathButton.IsEnabled && !window.RevealButton.IsEnabled);
            window.CopyPathButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("No selection leaves clipboard unchanged", Clipboard.GetText() == secondOwner.ExecutablePath);
            string secondPath = secondOwner.ExecutablePath;
            secondOwner.ExecutablePath = "";
            window.ProcessGrid.SelectedItem = secondOwner;
            Check("Unknown executable path disables row actions", !window.CopyPathButton.IsEnabled && !window.RevealButton.IsEnabled);
            window.CopyPathButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Unknown path is not copied", Clipboard.GetText() == secondPath);
            window.ProcessGrid.SelectedIndex = -1;
            secondOwner.ExecutablePath = secondPath;
            window.ProcessGrid.SelectedItem = secondOwner;
            window.CopyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Clipboard report contains owner PIDs", Clipboard.GetText().Contains($"PID {a.Pid}") && Clipboard.GetText().Contains($"PID {b.Pid}"));
            window.CopyButton.Content = "Копировать отчёт";
            RenderPreview(window, System.IO.Path.Combine(artifactDirectory, "preview-locked.png"));
            window.PathBox.Text = target + ".different";
            Check("Changing path clears stale results", window.ProcessGrid.Items.Count == 0 && !window.CopyButton.IsEnabled && !window.CopyPathButton.IsEnabled && !window.RevealButton.IsEnabled && window.StatusTitle.Text == "Путь изменён");
            window.PathBox.Text = farTarget;
            window.ScanButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitForUiScan(window);
            Check("Search button completes real worker scan", window.ProcessGrid.Items.Cast<ProcessEntry>().Any(p => p.Pid == a.Pid) && window.ProcessGrid.Items.Cast<ProcessEntry>().Any(p => p.Pid == b.Pid) && window.CopyButton.IsEnabled);
            Check("GUI displays normalized Far path", window.PathBox.Text == target);
            window.PathBox.Text = "relative.txt";
            window.ScanButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Invalid path displayed in GUI", window.StatusTitle.Text == "Проверь путь" && window.ProcessGrid.Items.Count == 0);

            ShellIntegrationTests.Run(artifactDirectory, target, [a.Pid, b.Pid], Check);

            a.Release();
            var one = Scan(target);
            Check("Refresh removes released owner", !one.Processes.Any(p => p.Pid == a.Pid) && one.Processes.Any(p => p.Pid == b.Pid));
            b.Release();
            var free = Scan(target);
            Check("Released file has delete access", free.DeleteAccessError == 0, free.DeleteAccessError.ToString());
            Check("Released owners absent", !free.Processes.Any(p => p.Pid == a.Pid || p.Pid == b.Pid));
            var freeWindow = new MainWindow();
            freeWindow.Present(free);
            RenderPreview(freeWindow, System.IO.Path.Combine(artifactDirectory, "preview-free.png"));

            var exclusive = Helper.Start("--hold-file", target, assetDirectory, "exclusive"); helpers.Add(exclusive);
            var exclusiveResult = Scan(target);
            Check("Exclusive holder detected", exclusiveResult.Processes.Any(p => p.Pid == exclusive.Pid), JsonSerializer.Serialize(exclusiveResult.Processes));
            Check("Exclusive hold blocks deletion", exclusiveResult.DeleteAccessError is 32 or 33);
            exclusive.Release();

            var permissive = Helper.Start("--hold-file", target, assetDirectory, "delete"); helpers.Add(permissive);
            var shared = Scan(target);
            Check("Share-delete owner still detected", shared.Processes.Any(p => p.Pid == permissive.Pid), JsonSerializer.Serialize(shared.Processes));
            Check("Share-delete permits delete access", shared.DeleteAccessError == 0, shared.DeleteAccessError.ToString());
            permissive.Release();

            string modulePath = System.IO.Path.Combine(assetDirectory, "FileLens-test-module.dll");
            File.Copy(System.IO.Path.Combine(Environment.SystemDirectory, "version.dll"), modulePath);
            var loader = Helper.Start("--load-library", modulePath, assetDirectory); helpers.Add(loader);
            var loaded = Scan(modulePath);
            Check("Loaded DLL owner detected", loaded.Processes.Any(p => p.Pid == loader.Pid));
            Check("DLL marked as loaded module", loaded.Processes.Any(p => p.Pid == loader.Pid && p.LoadedModule), JsonSerializer.Serialize(loaded.Processes));
            File.WriteAllText(System.IO.Path.Combine(artifactDirectory, "dll-scan.json"), JsonSerializer.Serialize(loaded, JsonOptions));
            var moduleWindow = new MainWindow();
            moduleWindow.Present(loaded);
            RenderPreview(moduleWindow, System.IO.Path.Combine(artifactDirectory, "preview-dll.png"));
            loader.Release();
            Check("Unloaded DLL owner absent", !Scan(modulePath).Processes.Any(p => p.Pid == loader.Pid));

            File.SetAttributes(target, FileAttributes.ReadOnly);
            try { Check("Read-only attribute detected", Scan(target).ReadOnly); }
            finally { File.SetAttributes(target, FileAttributes.Normal); }
            try { Scan(System.IO.Path.Combine(assetDirectory, "missing.txt")); Check("Missing file produces error", false); }
            catch (IOException) { Check("Missing file produces error", true); }
            try { Scan(assetDirectory); Check("Folder produces error", false); }
            catch (IOException) { Check("Folder produces error", true); }
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try { Task.Run(() => ScanClient.RunAsync(target, cancellation.Token)).GetAwaiter().GetResult(); Check("Worker cancellation", false); }
                catch (OperationCanceledException) { Check("Worker cancellation", true); }
            }
            Check("Target file contents unchanged", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))) == originalHash);
            Check("Target file still exists", File.Exists(target));
        }
        catch (Exception ex) { Check("Self-test completed", false, ex.ToString()); }
        finally
        {
            foreach (var helper in helpers) helper.Dispose();
            try { if (previousClipboard is not null) Clipboard.SetDataObject(previousClipboard, true); else Clipboard.Clear(); }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed = failed == 0, failed, checks, executable = Environment.ProcessPath, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), completedAt = DateTime.Now }, JsonOptions));
        return failed == 0 ? 0 : 1;
    }

    private static void WaitForUiScan(MainWindow window)
    {
        var timer = Stopwatch.StartNew();
        while (!window.PathBox.IsEnabled)
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (timer.Elapsed > TimeSpan.FromSeconds(70)) throw new TimeoutException("UI scan did not finish.");
            Thread.Sleep(10);
        }
    }

    internal static void RenderPreview(Window window, string output)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowInTaskbar = false;
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth + 64), (int)Math.Ceiling(content.ActualHeight + 44), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(11, 16, 26)), null, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
            drawing.DrawRectangle(new VisualBrush(content), null, new Rect(32, 24, content.ActualWidth, content.ActualHeight));
        }
        image.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(output)) encoder.Save(stream);
        window.Hide();
    }

    private sealed class Helper : IDisposable
    {
        private readonly Process process;
        private readonly string releasePath;
        public int Pid => process.Id;
        public string ExecutablePath => process.StartInfo.FileName;
        private Helper(Process process, string releasePath) { this.process = process; this.releasePath = releasePath; }
        public static Helper Start(string mode, string target, string folder, string? sharing = null)
        {
            string id = Guid.NewGuid().ToString("N");
            string ready = System.IO.Path.Combine(folder, id + ".ready");
            string release = System.IO.Path.Combine(folder, id + ".release");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { mode, target, ready, release }) start.ArgumentList.Add(argument);
            if (sharing is not null) start.ArgumentList.Add(sharing);
            return StartAndWait(start, ready, release);
        }
        public static Helper StartPowerShellHolder(string target, string folder, string script)
        {
            string id = Guid.NewGuid().ToString("N");
            string ready = System.IO.Path.Combine(folder, id + ".ready");
            string release = System.IO.Path.Combine(folder, id + ".release");
            var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Target", target, "-Ready", ready, "-Release", release }) start.ArgumentList.Add(argument);
            return StartAndWait(start, ready, release);
        }
        private static Helper StartAndWait(ProcessStartInfo start, string ready, string release)
        {
            var process = Process.Start(start) ?? throw new IOException("Test helper failed to start.");
            var helper = new Helper(process, release);
            var timer = Stopwatch.StartNew();
            while (!File.Exists(ready))
            {
                if (process.HasExited || timer.Elapsed > TimeSpan.FromSeconds(20)) { helper.Dispose(); throw new IOException("Test helper did not become ready."); }
                Thread.Sleep(50);
            }
            return helper;
        }
        public void Release()
        {
            if (process.HasExited) return;
            File.WriteAllText(releasePath, "release");
            if (!process.WaitForExit(10000)) { process.Kill(); process.WaitForExit(); }
        }
        public void Dispose() { Release(); process.Dispose(); }
    }
}
