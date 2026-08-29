using System.IO;

namespace BambuFilamentImporter.Services;

public static class ManufacturerLibraryStore
{
    public static string ManagedDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BambuFilamentImporter",
        "Manufacturer Libraries");

    public static string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "Manufacturer Libraries");

    public static int SeedBundledLibraries() => SeedBundledLibraries(ManagedDirectory, BundledDirectory);

    public static int SeedBundledLibraries(string managedDirectory, string bundledDirectory)
    {
        if (!Directory.Exists(bundledDirectory))
        {
            return 0;
        }

        var bundledPackages = Directory.EnumerateFiles(bundledDirectory, "*.bflib", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (bundledPackages.Count == 0)
        {
            return 0;
        }

        Directory.CreateDirectory(managedDirectory);
        var copied = 0;
        foreach (var sourcePath in bundledPackages)
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
            BundledDirectory,
            Path.Combine(AppContext.BaseDirectory, "packages", "manufacturers")
        ];
    }
}
