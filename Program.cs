using BambuFilamentImporter.Services;

namespace BambuFilamentImporter;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
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
