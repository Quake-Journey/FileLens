using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FileLens;

public sealed class ProcessEntry
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public long? StartTimeUtcTicks { get; set; }
    public bool OpenHandle { get; set; }
    public bool LoadedModule { get; set; }
    public bool RestartManager { get; set; }
    public string Usage => LoadedModule ? "Загружен EXE / DLL" : OpenHandle ? "Открытый файл" : "Использует файл";
    public string DisplayPath => string.IsNullOrEmpty(ExecutablePath) ? "Путь недоступен" : ExecutablePath;
    public string DescriptionLine => string.IsNullOrEmpty(ServiceName) ? Description : $"{Description} · Служба: {ServiceName}";
}

public sealed class ScanResult
{
    public string Path { get; set; } = "";
    public DateTime CheckedAt { get; set; }
    public double DurationSeconds { get; set; }
    public bool Elevated { get; set; }
    public int DeleteAccessError { get; set; }
    public bool ReadOnly { get; set; }
    public List<ProcessEntry> Processes { get; set; } = [];
    public List<string> Notices { get; set; } = [];
    public string StatusTitle => DeleteAccessError switch
    {
        32 or 33 => "Удаление блокируется",
        5 => "Нет доступа для удаления",
        0 when ReadOnly => "Атрибут «Только чтение»",
        0 when Processes.Any(p => p.LoadedModule) => "Файл загружен в процесс",
        0 => "Доступ для удаления открыт",
        _ => "Не удалось проверить доступ"
    };
    public string StatusDetail => DeleteAccessError switch
    {
        32 or 33 => "Windows обнаружила конфликт с открытым файлом",
        5 => "Возможны ограничения прав или защита файла",
        0 when ReadOnly => "Этот атрибут тоже может мешать удалению",
        0 when Processes.Any(p => p.LoadedModule) => "Загруженный EXE / DLL может мешать удалению",
        0 => "На момент проверки конфликта доступа нет",
        _ => $"Windows: {new Win32Exception(DeleteAccessError).Message} ({DeleteAccessError})"
    };

    public string ToReport()
    {
        var text = new StringBuilder();
        text.AppendLine("FileLens 1.0.2 · Windows x64");
        text.AppendLine($"Файл: {Path}");
        text.AppendLine($"Проверка: {CheckedAt:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine($"Администратор: {(Elevated ? "да" : "нет")}");
        text.AppendLine($"Состояние: {StatusTitle}. {StatusDetail}");
        text.AppendLine($"Открытие с правом DELETE: {(DeleteAccessError == 0 ? "успешно" : $"код Windows {DeleteAccessError}")}");
        text.AppendLine($"Только чтение: {(ReadOnly ? "да" : "нет")}");
        text.AppendLine($"Процессов: {Processes.Count}");
        foreach (var process in Processes)
        {
            text.AppendLine();
            text.AppendLine($"{process.Name} · PID {process.Pid} · {process.Usage}");
            text.AppendLine(process.DisplayPath);
            if (process.ServiceName.Length > 0) text.AppendLine($"Служба: {process.ServiceName}");
            text.AppendLine($"Источники: {string.Join(", ", new[] { process.OpenHandle ? "открытые файлы" : null, process.RestartManager ? "Restart Manager" : null, process.LoadedModule ? "модули" : null }.Where(x => x != null))}");
        }
        foreach (var notice in Notices) text.AppendLine($"Примечание: {notice}");
        text.AppendLine();
        text.AppendLine("Список — снимок на момент проверки. Использование файла не всегда блокирует его удаление.");
        text.AppendLine("Проверяется открытие с правом DELETE; реальное удаление не выполняется и не гарантируется.");
        return text.ToString();
    }
}

public sealed class ScanEnvelope
{
    public ScanResult? Result { get; set; }
    public string? Error { get; set; }
}

public static class FileScanner
{
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static string NormalizePath(string input)
    {
        // Far can quote only the directory: "C:\Folder\"file.dll. Double
        // quotes cannot occur in Windows file names, so remove them anywhere.
        // Keep spaces and apostrophes inside the path: those are valid names.
        var path = input.Replace("\"", "", StringComparison.Ordinal).Trim();
        if (path.Length >= 2 && path[0] == '\'' && path[^1] == '\'') path = path[1..^1].Trim();
        path = Environment.ExpandEnvironmentVariables(path).Replace("\"", "", StringComparison.Ordinal).Trim();
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile) path = uri.LocalPath;
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Вставь путь или выбери файл через кнопку «Выбрать файл…».");
        if (path.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new ArgumentException("Укажи путь к одному файлу в одной строке.");
        if (!System.IO.Path.IsPathFullyQualified(path)) throw new ArgumentException("Укажи полный путь к файлу, например C:\\Folder\\file.dll.");
        if (path.StartsWith("\\\\.\\", StringComparison.Ordinal) || path.StartsWith("\\\\?\\GLOBALROOT", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Укажи обычный файл, а не устройство Windows.");
        return System.IO.Path.GetFullPath(path);
    }

    public static ScanResult Scan(string input)
    {
        var timer = Stopwatch.StartNew();
        var path = NormalizePath(input);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { throw new FileNotFoundException("Файл не найден. Проверь путь и повтори поиск."); }
        catch (DirectoryNotFoundException) { throw new FileNotFoundException("Файл или папка не найдены. Проверь путь."); }
        if (attributes.HasFlag(FileAttributes.Directory)) throw new ArgumentException("Выбрана папка. Укажи конкретный файл внутри неё.");

        var result = new ScanResult { Path = path, CheckedAt = DateTime.Now, Elevated = IsElevated, ReadOnly = attributes.HasFlag(FileAttributes.ReadOnly) };
        var entries = new Dictionary<int, ProcessEntry>();
        QueryOpenFiles(path, entries, result.Notices);
        QueryRestartManager(path, entries, result.Notices);
        var extension = System.IO.Path.GetExtension(path);
        if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".sys", StringComparison.OrdinalIgnoreCase))
            QueryModules(path, entries, result.Notices);

        foreach (var entry in entries.Values) FillProcessDetails(entry);
        result.Processes = entries.Values.Where(p => p.Pid != Environment.ProcessId).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Pid).ToList();

        // Requesting DELETE access with OPEN_EXISTING does not mark a file for deletion.
        // Never call DeleteFile or SetFileInformationByHandle(FileDispositionInfo).
        using (var probe = Native.CreateFile(path, 0x00010000, 7, IntPtr.Zero, 3, 0x80, IntPtr.Zero))
            result.DeleteAccessError = probe.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        if (path.StartsWith("\\\\", StringComparison.Ordinal) && !path.StartsWith("\\\\?\\", StringComparison.Ordinal)) result.Notices.Add("Для сетевого файла видны только процессы этого компьютера; подключения на сервере не перечисляются.");
        result.DurationSeconds = timer.Elapsed.TotalSeconds;
        return result;
    }

    private static ProcessEntry Get(Dictionary<int, ProcessEntry> entries, int pid)
    {
        if (!entries.TryGetValue(pid, out var entry)) entries[pid] = entry = new ProcessEntry { Pid = pid, Name = $"Процесс {pid}" };
        return entry;
    }

    private static void QueryOpenFiles(string path, Dictionary<int, ProcessEntry> entries, List<string> notices)
    {
        using var file = Native.CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (file.IsInvalid)
        {
            notices.Add($"Открытые файлы: {new Win32Exception(Marshal.GetLastWin32Error()).Message}. Остальные методы проверки выполнены.");
            return;
        }
        // FILE_PROCESS_IDS_USING_FILE_INFORMATION, class 47, is an optional native
        // Windows facility reserved for system use. Fall back to the supported RM API.
        for (int length = 4096; length <= 1024 * 1024; length *= 2)
        {
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                int status = Native.NtQueryInformationFile(file, out _, buffer, (uint)length, 47);
                if (status == unchecked((int)0x80000005) || status == unchecked((int)0xC0000004) || status == unchecked((int)0xC0000023)) continue;
                if (status < 0)
                {
                    notices.Add($"Метод открытых файлов недоступен (NTSTATUS 0x{status:X8}); использован Restart Manager.");
                    return;
                }
                uint count = unchecked((uint)Marshal.ReadInt32(buffer));
                int offset = IntPtr.Size == 8 ? 8 : 4;
                if (count > (length - offset) / IntPtr.Size) { notices.Add("Windows вернула неполный список открытых файлов."); return; }
                for (int i = 0; i < count; i++)
                {
                    long pid = Marshal.ReadIntPtr(buffer, offset + i * IntPtr.Size).ToInt64();
                    if (pid > 0 && pid <= int.MaxValue && pid != Environment.ProcessId) Get(entries, (int)pid).OpenHandle = true;
                }
                return;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        notices.Add("Список открытых файлов превысил доступный размер буфера.");
    }

    private static void QueryRestartManager(string path, Dictionary<int, ProcessEntry> entries, List<string> notices)
    {
        uint code = Native.RmStartSession(out uint session, 0, new StringBuilder(33));
        if (code != 0) { notices.Add($"Restart Manager: {new Win32Exception((int)code).Message} ({code})."); return; }
        try
        {
            code = Native.RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, IntPtr.Zero);
            if (code != 0) { notices.Add($"Регистрация файла: {new Win32Exception((int)code).Message} ({code})."); return; }
            uint size = 0;
            Native.RmProcessInfo[]? array = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                code = Native.RmGetList(session, out uint needed, ref size, array, out _);
                if (code == 234)
                {
                    if (needed > 100000) break;
                    size = Math.Max(needed, size + 16);
                    array = new Native.RmProcessInfo[size];
                    continue;
                }
                if (code != 0) break;
                for (int i = 0; i < size; i++)
                {
                    var info = array![i];
                    var entry = Get(entries, info.Process.Pid);
                    entry.RestartManager = true;
                    if (!string.IsNullOrWhiteSpace(info.AppName)) entry.Description = info.AppName;
                    entry.ServiceName = info.ServiceName ?? "";
                    long time = ((long)info.Process.StartTime.High << 32) | info.Process.StartTime.Low;
                    if (time > 0) entry.StartTimeUtcTicks = DateTime.FromFileTimeUtc(time).Ticks;
                }
                return;
            }
            notices.Add($"Restart Manager не вернул полный список ({code}). Повтори проверку.");
        }
        finally { Native.RmEndSession(session); }
    }

    private static void QueryModules(string target, Dictionary<int, ProcessEntry> entries, List<string> notices)
    {
        int denied = 0;
        string canonical = CanonicalPath(target);
        string targetName = System.IO.Path.GetFileName(target);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                int pid;
                try { pid = process.Id; } catch { continue; }
                if (pid <= 4 || pid == Environment.ProcessId) continue;
                using var handle = Native.OpenProcess(0x0410, false, pid);
                if (handle.IsInvalid) { if (Marshal.GetLastWin32Error() == 5) denied++; continue; }
                try
                {
                    var modules = new IntPtr[512];
                    if (!Native.EnumProcessModulesEx(handle, modules, modules.Length * IntPtr.Size, out int needed, 3)) continue;
                    if (needed > modules.Length * IntPtr.Size)
                    {
                        if (needed > 16 * 1024 * 1024) continue;
                        modules = new IntPtr[needed / IntPtr.Size + 32];
                        if (!Native.EnumProcessModulesEx(handle, modules, modules.Length * IntPtr.Size, out needed, 3)) continue;
                    }
                    var name = new StringBuilder(32768);
                    for (int i = 0; i < Math.Min(needed / IntPtr.Size, modules.Length); i++)
                    {
                        name.Clear();
                        if (Native.GetModuleFileNameEx(handle, modules[i], name, name.Capacity) == 0) continue;
                        string module = NormalizeComparison(name.ToString());
                        if (!System.IO.Path.GetFileName(module).Equals(targetName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!module.Equals(NormalizeComparison(target), StringComparison.OrdinalIgnoreCase) && !CanonicalPath(module).Equals(canonical, StringComparison.OrdinalIgnoreCase)) continue;
                        Get(entries, pid).LoadedModule = true;
                        break;
                    }
                }
                catch (Win32Exception) { /* A process may exit during this snapshot. */ }
            }
        }
        if (denied > 0) notices.Add($"Модули {denied} процессов недоступны. {(IsElevated ? "Защищённые процессы и драйверы могут быть скрыты даже с правами администратора." : "Запуск от администратора может расширить результаты.")}");
        if (System.IO.Path.GetExtension(target).Equals(".sys", StringComparison.OrdinalIgnoreCase)) notices.Add("Загруженные драйверы ядра не перечисляются как модули обычных процессов.");
    }

    private static string NormalizeComparison(string path) => path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase) ? "\\\\" + path[8..] : path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path[4..] : path;

    private static string CanonicalPath(string path)
    {
        using var file = Native.CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (file.IsInvalid) return NormalizeComparison(path);
        var buffer = new StringBuilder(32768);
        uint length = Native.GetFinalPathNameByHandle(file, buffer, (uint)buffer.Capacity, 0);
        return length > 0 && length < buffer.Capacity ? NormalizeComparison(buffer.ToString()) : NormalizeComparison(path);
    }

    private static void FillProcessDetails(ProcessEntry entry)
    {
        try
        {
            using var process = Process.GetProcessById(entry.Pid);
            long? observedStart = null;
            try { observedStart = process.StartTime.ToUniversalTime().Ticks; } catch (Win32Exception) { }
            if (entry.StartTimeUtcTicks.HasValue && observedStart.HasValue && entry.StartTimeUtcTicks != observedStart)
            {
                entry.Name = $"Завершившийся процесс {entry.Pid}";
                entry.Description = "PID уже принадлежит другому процессу";
                return;
            }
            entry.StartTimeUtcTicks ??= observedStart;
            entry.Name = process.ProcessName + (process.ProcessName.Equals("System", StringComparison.OrdinalIgnoreCase) ? "" : ".exe");
            using var handle = Native.OpenProcess(0x1000, false, entry.Pid);
            if (!handle.IsInvalid)
            {
                var path = new StringBuilder(32768);
                int length = path.Capacity;
                if (Native.QueryFullProcessImageName(handle, 0, path, ref length)) entry.ExecutablePath = path.ToString();
            }
            if (entry.Description.Length == 0 && entry.ExecutablePath.Length > 0)
                try { entry.Description = FileVersionInfo.GetVersionInfo(entry.ExecutablePath).FileDescription ?? ""; } catch { }
            if (entry.Description.Length == 0) entry.Description = entry.ServiceName.Length > 0 ? $"Служба {entry.ServiceName}" : "Процесс Windows";
        }
        catch (ArgumentException) { entry.Description = "Завершился во время проверки"; }
        catch (InvalidOperationException) { entry.Description = "Завершился во время проверки"; }
        catch (Win32Exception) { entry.Description = "Сведения о процессе недоступны"; }
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct FileTime { public uint Low; public uint High; }
    [StructLayout(LayoutKind.Sequential)] internal struct RmUniqueProcess { public int Pid; public FileTime StartTime; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public uint ApplicationType;
        public uint AppStatus;
        public uint SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] internal static extern uint RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] internal static extern uint RmRegisterResources(uint session, uint files, string[] names, uint apps, IntPtr applications, uint services, IntPtr serviceNames);
    [DllImport("rstrtmgr.dll")] internal static extern uint RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? apps, out uint reasons);
    [DllImport("rstrtmgr.dll")] internal static extern uint RmEndSession(uint session);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint attributes, IntPtr template);
    [DllImport("ntdll.dll")] internal static extern int NtQueryInformationFile(SafeFileHandle file, out IoStatusBlock io, IntPtr data, uint length, int informationClass);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
    [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumProcessModulesEx(SafeProcessHandle process, [Out] IntPtr[] modules, int bytes, out int needed, uint flags);
    [DllImport("psapi.dll", EntryPoint = "GetModuleFileNameExW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint GetModuleFileNameEx(SafeProcessHandle process, IntPtr module, StringBuilder path, int size);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool FreeLibrary(IntPtr module);
}
