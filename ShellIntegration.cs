using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FileLens;

internal static class ShellIntegration
{
    internal const string Verb = "FileLens.CheckFile";
    internal const string KeyPath = @"Software\Classes\*\shell\FileLens.CheckFile";
    internal const string MenuText = "Проверить, кто держит файл";
    internal static string Command => $"\"{Environment.ProcessPath}\" --check-file \"%1\"";

    internal static string? RegisteredExecutable
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue("FileLensExecutable") as string;
        }
    }

    internal static bool IsRegisteredForCurrentCopy
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            using var command = key?.OpenSubKey("command");
            return string.Equals(key?.GetValue("FileLensExecutable") as string, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(command?.GetValue("") as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static void Register()
    {
        string executable = Environment.ProcessPath ?? throw new IOException("Не удалось определить путь к FileLens.exe.");
        if (!File.Exists(executable)) throw new FileNotFoundException("Не найден FileLens.exe для регистрации меню.");
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue("", MenuText, RegistryValueKind.String);
        key.SetValue("MUIVerb", MenuText, RegistryValueKind.String);
        key.SetValue("Icon", $"\"{executable}\",0", RegistryValueKind.String);
        key.SetValue("MultiSelectModel", "Single", RegistryValueKind.String);
        key.SetValue("FileLensExecutable", executable, RegistryValueKind.String);
        using var command = key.CreateSubKey("command");
        command.SetValue("", Command, RegistryValueKind.String);
        NotifyExplorer();
    }

    internal static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
        NotifyExplorer();
    }

    internal static void NotifyExplorer() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
