using System.Windows;
using System.Windows.Threading;

namespace DesktopSonos;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A dropped speaker or a share going offline should never take the app down.
        MessageBox.Show(
            e.Exception.Message,
            "DesktopSonos — unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}
