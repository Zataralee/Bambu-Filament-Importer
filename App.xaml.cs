using System.IO;
using System.Windows;
using System.Windows.Threading;
using BambuFilamentImporter.Services;

namespace BambuFilamentImporter;

public partial class App : Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        if (BambuProcess.IsAnotherImporterRunning())
        {
            MessageBox.Show(
                "Another Bambu Filament Importer window is already open. Close it before starting this version.",
                "Importer already open",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        var settings = AppSettingsStore.Load();
        ThemeService.Apply(settings.DarkMode);
        new MainWindow().Show();
    }

    private void Application_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BambuFilamentImporter");
            Directory.CreateDirectory(logFolder);
            File.AppendAllText(
                Path.Combine(logFolder, "error.log"),
                $"[{DateTime.Now:O}]{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // The error dialog is still useful if Windows also blocks the fallback log.
        }

        MessageBox.Show(
            e.Exception.Message + Environment.NewLine + Environment.NewLine +
            "The error was recorded and the importer will remain open.",
            "Bambu Filament Importer error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
