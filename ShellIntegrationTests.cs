using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace FileLens;

internal static class ShellIntegrationTests
{
    internal static void Run(string artifacts, string heldFile, int[] owners, Action<string, bool, string> check)
    {
        var backup = RegistryBackup.Capture(ShellIntegration.KeyPath);
        string? previousScope = Environment.GetEnvironmentVariable("FILELENS_TEST_INSTANCE_SCOPE");
        Environment.SetEnvironmentVariable("FILELENS_TEST_INSTANCE_SCOPE", Guid.NewGuid().ToString("N"));
        Process? primary = null;
        SettingsWindow? settings = null;
        string freeFile = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(heldFile)!, "Другой файл с пробелами.txt");
        File.WriteAllText(freeFile, "Shell integration must not change this file.");
        try
        {
            ShellIntegration.Unregister();
            settings = new SettingsWindow();
            check("Settings show unregistered context menu", settings.RegisterButton.IsEnabled && !settings.UnregisterButton.IsEnabled, "");
            settings.RegisterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("Settings register menu for current EXE", ShellIntegration.IsRegisteredForCurrentCopy && settings.UnregisterButton.IsEnabled && !settings.RegisterButton.IsEnabled, "");
            using (var key = Registry.CurrentUser.OpenSubKey(ShellIntegration.KeyPath))
            using (var command = key!.OpenSubKey("command"))
            {
                check("Exact Explorer verb and quoted command", (string?)key.GetValue("") == "Проверить, кто держит файл" && (string?)key.GetValue("MultiSelectModel") == "Single" && (string?)command!.GetValue("") == $"\"{Environment.ProcessPath}\" --check-file \"%1\"", "");
            }
            CommandLine.RenderPreview(settings, System.IO.Path.Combine(artifacts, "preview-settings.png"));

            primary = LaunchFromShell(heldFile);
            var first = WaitForState(state => state.Path == heldFile && !state.Scanning && owners.All(state.ProcessIds.Contains));
            check("Explorer command opens and scans held file", first.Pid == primary.Id && owners.All(first.ProcessIds.Contains), "");
            using (var secondary = LaunchFromShell(freeFile))
            {
                check("Second shell launch exits after forwarding", secondary.WaitForExit(15000) && secondary.ExitCode == 0, "");
                var forwarded = WaitForState(state => state.Path == freeFile && !state.Scanning);
                check("Second file scanned in original process", forwarded.Pid == primary.Id && !primary.HasExited && forwarded.ProcessIds.Length == 0, "");
            }

            InstanceNative.ShowWindowAsync(new IntPtr(first.WindowHandle), 6);
            WaitForState(state => state.Minimized);
            using (var secondary = LaunchFromShell(heldFile))
            {
                if (!secondary.WaitForExit(15000)) throw new TimeoutException("Secondary activation did not exit.");
                var restored = WaitForState(state => state.Path == heldFile && !state.Scanning && !state.Minimized);
                check("Context-menu activation restores minimized window", restored.Pid == primary.Id && owners.All(restored.ProcessIds.Contains), "");
            }

            Send(new ActivationRequest { Path = freeFile });
            WaitForState(state => state.Path == freeFile && !state.Scanning);
            Send(new ActivationRequest { Path = heldFile });
            var inProgress = Send(new ActivationRequest { Action = "State" });
            check("Remote request starts asynchronous scan", inProgress.Scanning, "");
            Send(new ActivationRequest { Path = freeFile });
            Send(new ActivationRequest { Path = heldFile });
            var final = WaitForState(state => state.Path == heldFile && !state.Scanning && owners.All(state.ProcessIds.Contains));
            check("Activation during scan cancels and checks latest file", final.Pid == primary.Id, "");
            bool repeated = true;
            for (int i = 0; i < 50; i++) repeated &= Send(new ActivationRequest { Action = "State" }).Pid == primary.Id;
            check("Fifty consecutive IPC replies are delivered", repeated, "");

            using (var secondary = LaunchApplication())
                check("Plain launch also reuses original window", secondary.WaitForExit(15000) && secondary.ExitCode == 0 && Send(new ActivationRequest { Action = "State" }).Path == heldFile, "");
            var separateScan = Task.Run(() => ScanClient.RunAsync(freeFile, CancellationToken.None)).GetAwaiter().GetResult();
            check("Scanner worker bypasses GUI singleton", separateScan.Path == freeFile && Send(new ActivationRequest { Action = "State" }).Path == heldFile, "");

            InstanceNative.PostMessage(new IntPtr(first.WindowHandle), 0x0010, IntPtr.Zero, IntPtr.Zero);
            check("Closing GUI releases instance ownership", primary.WaitForExit(10000), "");
            primary.Dispose();
            primary = LaunchFromShell(freeFile);
            var restarted = WaitForState(state => state.Pid == primary.Id && state.Path == freeFile && !state.Scanning);
            check("Context menu starts a new instance after close", restarted.Pid == primary.Id, "");
            primary.Kill();
            primary.WaitForExit();
            primary.Dispose();
            primary = LaunchFromShell(heldFile);
            var recovered = WaitForState(state => state.Pid == primary.Id && state.Path == heldFile && !state.Scanning);
            check("Abandoned instance mutex recovers after crash", owners.All(recovered.ProcessIds.Contains), "");

            var replacement = LaunchApplication("--elevated-relaunch", freeFile);
            try
            {
                Thread.Sleep(250);
                check("Relaunch waits while original window owns instance", !replacement.HasExited && Send(new ActivationRequest { Action = "State" }).Pid == primary.Id, "");
                InstanceNative.PostMessage(new IntPtr(recovered.WindowHandle), 0x0010, IntPtr.Zero, IntPtr.Zero);
                if (!primary.WaitForExit(10000)) throw new TimeoutException("Original window did not close for relaunch.");
                primary.Dispose();
                primary = replacement;
                var handedOff = WaitForState(state => state.Pid == primary.Id && state.Path == freeFile && !state.Scanning);
                check("Relaunch acquires same instance after window closes", handedOff.Pid == replacement.Id, "");
            }
            catch
            {
                if (!replacement.HasExited) { replacement.Kill(entireProcessTree: true); replacement.WaitForExit(); }
                throw;
            }

            settings.UnregisterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("Settings remove context menu", ShellIntegration.RegisteredExecutable is null && !settings.UnregisterButton.IsEnabled && settings.RegisterButton.IsEnabled, "");
            using (var registration = LaunchApplication("--register-context-menu"))
                check("Registration command bypasses running GUI", registration.WaitForExit(10000) && registration.ExitCode == 0 && ShellIntegration.IsRegisteredForCurrentCopy, "");
            using (var unregistration = LaunchApplication("--unregister-context-menu"))
                check("Removal command bypasses running GUI", unregistration.WaitForExit(10000) && unregistration.ExitCode == 0 && ShellIntegration.RegisteredExecutable is null, "");
            ShellIntegration.Unregister();
            check("Repeated unregister is harmless", ShellIntegration.RegisteredExecutable is null, "");
            check("Shell checks leave source files intact", File.Exists(heldFile) && File.ReadAllText(freeFile) == "Shell integration must not change this file.", "");
        }
        finally
        {
            if (primary is not null)
            {
                try { if (!primary.HasExited) { primary.Kill(entireProcessTree: true); primary.WaitForExit(); } } finally { primary.Dispose(); }
            }
            settings?.Close();
            backup.Restore(ShellIntegration.KeyPath);
            ShellIntegration.NotifyExplorer();
            Environment.SetEnvironmentVariable("FILELENS_TEST_INSTANCE_SCOPE", previousScope);
        }
    }

    private static Process LaunchFromShell(string file) => Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Verb = ShellIntegration.Verb }) ?? throw new IOException("Explorer verb did not launch FileLens.");
    private static Process LaunchApplication(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("FileLens did not start.");
    }
    private static ActivationReply Send(ActivationRequest request) => Task.Run(() => SingleInstance.SendAsync(request, 3000)).GetAwaiter().GetResult();
    private static ActivationReply WaitForState(Func<ActivationReply, bool> matches)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(25))
        {
            try { var state = Send(new ActivationRequest { Action = "State" }); if (matches(state)) return state; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            Thread.Sleep(50);
        }
        throw new TimeoutException("Expected FileLens instance state was not reached.");
    }

    private sealed class RegistryBackup
    {
        private readonly bool existed;
        private readonly Dictionary<string, (object Value, RegistryValueKind Kind)> values = [];
        private readonly Dictionary<string, RegistryBackup> children = [];
        private RegistryBackup(RegistryKey? key)
        {
            existed = key is not null;
            if (key is null) return;
            foreach (string name in key.GetValueNames()) values[name] = (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, key.GetValueKind(name));
            foreach (string name in key.GetSubKeyNames()) { using var child = key.OpenSubKey(name); children[name] = new RegistryBackup(child); }
        }
        internal static RegistryBackup Capture(string path) { using var key = Registry.CurrentUser.OpenSubKey(path); return new RegistryBackup(key); }
        internal void Restore(string path)
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, false);
            if (!existed) return;
            using var key = Registry.CurrentUser.CreateSubKey(path);
            RestoreValues(key);
        }
        private void RestoreValues(RegistryKey key)
        {
            foreach (var (name, value) in values) key.SetValue(name, value.Value, value.Kind);
            foreach (var (name, child) in children) { using var childKey = key.CreateSubKey(name); child.RestoreValues(childKey); }
        }
    }
}
