using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace FileLens;

public partial class MainWindow : Window
{
    private ScanResult? current;
    private CancellationTokenSource? scanCancellation;
    private bool scanning;
    private Task? activeScan;
    private readonly SemaphoreSlim activationGate = new(1, 1);
    private SettingsWindow? settingsWindow;
    private bool closed;
    private System.Windows.Threading.DispatcherTimer? copyPathFeedback;
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x72, 0xE4, 0xCC));
    private static readonly Brush Warning = new SolidColorBrush(Color.FromRgb(0xFF, 0xBC, 0x7E));

    public MainWindow(string? path = null)
    {
        InitializeComponent();
        if (FileScanner.IsElevated)
        {
            AdminButton.Content = "✓  Администратор";
            AdminButton.IsEnabled = false;
        }
        if (!string.IsNullOrWhiteSpace(path))
        {
            PathBox.Text = path;
            Loaded += async (_, _) => await StartScanAsync();
        }
        else Loaded += (_, _) => PathBox.Focus();
        Closed += (_, _) => { closed = true; scanCancellation?.Cancel(); copyPathFeedback?.Stop(); settingsWindow?.Close(); };
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        int enabled = 1;
        Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int));
        int corners = 2;
        Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref corners, sizeof(int));
    }

    private void Path_Changed(object sender, TextChangedEventArgs e)
    {
        if (PathPlaceholder is null) return;
        PathPlaceholder.Visibility = string.IsNullOrWhiteSpace(PathBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (current is null || scanning) return;
        current = null;
        CopyButton.IsEnabled = false;
        CopyPathButton.IsEnabled = false;
        ProcessGrid.ItemsSource = null;
        DetailPanel.Visibility = Visibility.Collapsed;
        RevealButton.IsEnabled = false;
        ProcessCount.Text = "—";
        StatusTitle.Text = "Путь изменён";
        StatusTitle.Foreground = Good;
        StatusDetail.Text = "Нажми «Найти процессы» для новой проверки";
        ScanTime.Text = "—";
        ScanDuration.Text = "ожидает проверки";
        EmptyState.Visibility = Visibility.Visible;
        EmptyIcon.Text = "⌕";
        EmptyTitle.Text = "Проверь новый файл";
        EmptyDetail.Text = "Здесь появятся процессы, использующие указанный файл.";
        Footer.Text = "Процессы, допускающие удаление, тоже могут быть в списке. Проверка не удаляет файл.";
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (scanning) { scanCancellation?.Cancel(); return; }
        await StartScanAsync();
    }

    private async void Path_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !scanning) { e.Handled = true; await StartScanAsync(); }
        if (e.Key == Key.Escape && scanning) { e.Handled = true; scanCancellation?.Cancel(); }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Выбери файл для проверки", Filter = "Все файлы (*.*)|*.*", CheckFileExists = true, Multiselect = false };
        try
        {
            var path = FileScanner.NormalizePath(PathBox.Text);
            var directory = System.IO.Path.GetDirectoryName(path);
            if (Directory.Exists(directory)) dialog.InitialDirectory = directory;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FileName;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !scanning && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (scanning || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        if (files.Length != 1) { ShowError("Перетащи один файл", "Программа проверяет один выбранный файл за раз."); return; }
        PathBox.Text = files[0];
        await StartScanAsync();
    }

    private Task StartScanAsync()
    {
        if (scanning) return activeScan ?? Task.CompletedTask;
        return activeScan = RunScanAsync();
    }

    private async Task RunScanAsync()
    {
        if (scanning) return;
        string path;
        try { path = FileScanner.NormalizePath(PathBox.Text); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        { ShowError("Проверь путь", ex.Message); return; }
        PathBox.Text = path;
        current = null;
        ProcessGrid.ItemsSource = null;
        CopyButton.IsEnabled = false;
        CopyPathButton.IsEnabled = false;
        RevealButton.IsEnabled = false;
        DetailPanel.Visibility = Visibility.Collapsed;
        scanning = true;
        scanCancellation?.Dispose();
        scanCancellation = new CancellationTokenSource();
        PathBox.IsEnabled = BrowseButton.IsEnabled = AdminButton.IsEnabled = false;
        ScanButton.Content = "Отменить";
        ScanProgress.Visibility = Visibility.Visible;
        StatusTitle.Text = "Идёт проверка…";
        StatusTitle.Foreground = Good;
        StatusDetail.Text = "Windows ищет открытые файлы и загруженные модули";
        ProcessCount.Text = "…";
        ScanTime.Text = "—";
        ScanDuration.Text = "обычно несколько секунд";
        EmptyState.Visibility = Visibility.Visible;
        EmptyTitle.Text = "Ищем использующие процессы";
        EmptyDetail.Text = "Можно отменить проверку кнопкой выше или клавишей Esc.";
        try { Present(await ScanClient.RunAsync(path, scanCancellation.Token)); }
        catch (OperationCanceledException) { ShowError("Проверка отменена", "Нажми «Найти процессы», чтобы повторить поиск.", false); }
        catch (Exception ex) { ShowError("Не удалось проверить файл", ex.Message); }
        finally
        {
            scanning = false;
            PathBox.IsEnabled = BrowseButton.IsEnabled = true;
            AdminButton.IsEnabled = !FileScanner.IsElevated;
            ScanButton.Content = "Найти процессы";
            ScanProgress.Visibility = Visibility.Collapsed;
        }
    }

    internal ActivationReply GetActivationState() => new()
    {
        Path = PathBox.Text,
        Scanning = scanning,
        Minimized = WindowState == WindowState.Minimized,
        WindowHandle = new WindowInteropHelper(this).Handle.ToInt64(),
        ProcessIds = current?.Processes.Select(p => p.Pid).ToArray() ?? []
    };

    internal async Task OpenRequestedFileAsync(string? path)
    {
        if (closed) return;
        if (path is not null) settingsWindow?.Close();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        if (string.IsNullOrWhiteSpace(path)) return;
        await activationGate.WaitAsync();
        try
        {
            if (scanning)
            {
                scanCancellation?.Cancel();
                if (activeScan is not null) await activeScan;
            }
            if (closed) return;
            PathBox.Text = path;
            _ = StartScanAsync();
        }
        finally { activationGate.Release(); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (settingsWindow is null)
        {
            settingsWindow = new SettingsWindow { Owner = this };
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
        }
        else settingsWindow.Activate();
    }

    internal void Present(ScanResult result)
    {
        PathBox.Text = result.Path;
        current = result;
        StatusTitle.Text = result.StatusTitle;
        StatusTitle.Foreground = result.DeleteAccessError != 0 || result.ReadOnly || result.Processes.Any(p => p.LoadedModule) ? Warning : Good;
        StatusDetail.Text = result.StatusDetail;
        ProcessCount.Text = result.Processes.Count.ToString();
        ScanTime.Text = result.CheckedAt.ToString("HH:mm:ss");
        ScanDuration.Text = $"за {result.DurationSeconds:0.0} сек.";
        ProcessGrid.ItemsSource = result.Processes;
        EmptyState.Visibility = result.Processes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyIcon.Text = result.DeleteAccessError == 0 && !result.ReadOnly ? "✓" : "!";
        EmptyTitle.Text = "Использующие процессы не найдены";
        EmptyDetail.Text = result.DeleteAccessError is 32 or 33
            ? "Блокировка обнаружена, но её владелец не виден. Попробуй проверку от администратора; причиной также может быть драйвер или удалённый компьютер."
            : "Если удалить файл всё ещё не получается, причиной могут быть права доступа, атрибуты файла или драйвер.";
        Footer.Text = result.Notices.Count > 0 ? string.Join(" ", result.Notices) : "Процессы, допускающие удаление, тоже могут быть в списке. Проверка не удаляет файл.";
        Footer.ToolTip = Footer.Text;
        CopyButton.IsEnabled = true;
        if (result.Processes.Count > 0) ProcessGrid.SelectedIndex = 0;
    }

    private void ShowError(string title, string detail, bool warning = true)
    {
        current = null;
        StatusTitle.Text = title;
        StatusTitle.Foreground = warning ? Warning : Good;
        StatusDetail.Text = detail;
        ProcessCount.Text = "—";
        ScanTime.Text = "—";
        ScanDuration.Text = "результат отсутствует";
        ProcessGrid.ItemsSource = null;
        CopyButton.IsEnabled = RevealButton.IsEnabled = CopyPathButton.IsEnabled = false;
        DetailPanel.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        EmptyIcon.Text = warning ? "!" : "⌕";
        EmptyTitle.Text = title;
        EmptyDetail.Text = detail;
        Footer.Text = "Проверка не изменяет файл и не завершает использующие его процессы.";
    }

    private void Process_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        copyPathFeedback?.Stop();
        CopyPathButton.Content = "Копировать путь";
        if (ProcessGrid.SelectedItem is not ProcessEntry process)
        { DetailPanel.Visibility = Visibility.Collapsed; RevealButton.IsEnabled = CopyPathButton.IsEnabled = false; return; }
        DetailPanel.Visibility = Visibility.Visible;
        SelectedTitle.Text = $"{process.Name}  ·  PID {process.Pid}{(process.ServiceName.Length > 0 ? $"  ·  Служба: {process.ServiceName}" : "")}";
        SelectedPath.Text = process.DisplayPath;
        RevealButton.IsEnabled = File.Exists(process.ExecutablePath);
        CopyPathButton.IsEnabled = !string.IsNullOrWhiteSpace(process.ExecutablePath);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not ProcessEntry process || string.IsNullOrWhiteSpace(process.ExecutablePath)) return;
        try { Clipboard.SetText(process.ExecutablePath); }
        catch (System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(this, "Буфер обмена занят. Повтори копирование.", "FileLens");
            return;
        }
        CopyPathButton.Content = "✓  Скопировано";
        if (copyPathFeedback is null)
        {
            copyPathFeedback = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            copyPathFeedback.Tick += (_, _) => { CopyPathButton.Content = "Копировать путь"; copyPathFeedback.Stop(); };
        }
        copyPathFeedback.Stop();
        copyPathFeedback.Start();
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not ProcessEntry process || !File.Exists(process.ExecutablePath)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{process.ExecutablePath}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось открыть Проводник", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (current is null) return;
        try { Clipboard.SetText(current.ToReport()); CopyButton.Content = "✓  Скопировано"; }
        catch (System.Runtime.InteropServices.COMException) { MessageBox.Show(this, "Буфер обмена занят. Повтори копирование.", "FileLens"); }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { CopyButton.Content = "Копировать отчёт"; timer.Stop(); };
        timer.Start();
    }

    private void Admin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            start.ArgumentList.Add("--elevated-relaunch");
            if (!string.IsNullOrWhiteSpace(PathBox.Text)) start.ArgumentList.Add(PathBox.Text);
            if (Process.Start(start) is not null) Close();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось запустить программу", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
