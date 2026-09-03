using BambuFilamentImporter.Services;

namespace BambuFilamentImporter;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppLog.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                AppLog.WriteException("Unhandled application exception.", exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            AppLog.WriteException("Unobserved background task exception.", eventArgs.Exception);
            eventArgs.SetObserved();
        };

        if (args.Length == 4 && args[0].Equals("--apply-update", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return UpdateService.ApplyUpdate(args[1], args[2], int.Parse(args[3]));
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    ex.Message,
                    "Update failed",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                return 2;
            }
        }

        if (args.Length == 2 && args[0].Equals("--update-complete", StringComparison.OrdinalIgnoreCase))
        {
            UpdateService.IsRestartAfterUpdate = true;
            UpdateService.PendingCleanupDirectory = args[1];
        }

        if (args.Length == 2 && args[0].Equals("--elevated-remove", StringComparison.OrdinalIgnoreCase))
        {
            return ElevationService.ExecuteRemovalPlan(args[1]);
        }

        if (args.Length == 2 && args[0].Equals("--elevated-repair-ams-ids", StringComparison.OrdinalIgnoreCase))
        {
            return ElevationService.ExecuteAmsIdRepair(args[1]);
        }

        if (args.Length == 2 && args[0].Equals("--validate-package", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var package = PackageReader.Load(args[1]);
                Console.WriteLine($"{package.Manifest.DisplayName}: {package.Manifest.Profiles.Count} profiles");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
        }

        if (args.Length == 1 && args[0].Equals("--repair-ams-ids", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var result = ElevationService.RepairAmsIdsWithElevation(new BambuPaths());
                Console.WriteLine($"Repaired {result.RepairedProducts} filament IDs across {result.ChangedFiles} files.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
