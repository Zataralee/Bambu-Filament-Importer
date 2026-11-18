using System.IO;
using System.Windows;
using System.Windows.Threading;
using BambuFilamentImporter.Services;

namespace BambuFilamentImporter.Preview;

public partial class PreviewApp : Application
{
    private Mutex? _instance;
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        _instance = new Mutex(true, "Local\\BFI.RedesignPreview", out var first);
        if (!first)
        {
            MessageBox.Show("The BFI preview is already open.", "BFI preview");
            Shutdown();
            return;
        }
        AppLog.Initialize();
        ThemeService.Apply(AppSettingsStore.Load().DarkMode);
        new BambuFilamentImporter.Workspace.WorkspaceWindow(new BambuFilamentImporter.Workspace.WorkspaceContext(preview: true)).Show();
    }
    private void Application_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.WriteException("Redesign preview error", e.Exception);
        MessageBox.Show(e.Exception.Message, "BFI preview", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
