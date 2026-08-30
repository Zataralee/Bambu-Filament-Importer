using System.IO;

namespace BambuFilamentImporter.Services;

public static class ManufacturerLibraryStore
{
    public static string ManagedDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BambuFilamentImporter",
        "Manufacturer Libraries");

    public static string LegacyBundledDirectory => Path.Combine(AppContext.BaseDirectory, "Manufacturer Libraries");

    public static int MigrateLegacyLibraries() => MigrateLegacyLibraries(ManagedDirectory, LegacyBundledDirectory);

    public static int MigrateLegacyLibraries(string managedDirectory, string legacyDirectory)
    {
        if (!Directory.Exists(legacyDirectory))
        {
            return 0;
        }

        var legacyPackages = Directory.EnumerateFiles(legacyDirectory, "*.bflib", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (legacyPackages.Count == 0)
        {
            return 0;
        }

        Directory.CreateDirectory(managedDirectory);
        var copied = 0;
        foreach (var sourcePath in legacyPackages)
        {
            var destinationPath = Path.Combine(managedDirectory, Path.GetFileName(sourcePath));
            if (File.Exists(destinationPath))
            {
                continue;
            }

            _ = PackageReader.Load(sourcePath);
            File.Copy(sourcePath, destinationPath, overwrite: false);
            copied++;
        }

        return copied;
    }

    public static IReadOnlyList<string> DiscoveryDirectories()
    {
        return
        [
            ManagedDirectory,
            Path.Combine(AppContext.BaseDirectory, "packages", "manufacturers")
        ];
    }
}
