using System.Windows;
using System.Windows.Interop;

namespace FileLens;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        RefreshRegistration();
        Activated += (_, _) => RefreshRegistration();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        int enabled = 1;
        Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int));
    }

    private void RefreshRegistration()
    {
        try
        {
            string? registered = ShellIntegration.RegisteredExecutable;
            bool current = ShellIntegration.IsRegisteredForCurrentCopy;
            RegistrationStatus.Text = current ? "Пункт добавлен для этой копии FileLens" : registered is null ? "Пункт меню пока не добавлен" : "Пункт добавлен для другой копии FileLens";
            RegisteredPath.Text = registered ?? "";
            RegisterButton.Content = current ? "Добавлено" : registered is null ? "Добавить в меню" : "Привязать к этой копии";
            RegisterButton.IsEnabled = !current;
            UnregisterButton.IsEnabled = registered is not null;
        }
        catch (Exception ex)
        {
            RegistrationStatus.Text = "Не удалось прочитать параметры: " + ex.Message;
            RegisterButton.IsEnabled = UnregisterButton.IsEnabled = false;
        }
    }

    private void Register_Click(object sender, RoutedEventArgs e) => ChangeRegistration(ShellIntegration.Register);
    private void Unregister_Click(object sender, RoutedEventArgs e) => ChangeRegistration(ShellIntegration.Unregister);
    private void ChangeRegistration(Action change)
    {
        try { change(); RefreshRegistration(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось изменить контекстное меню", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
