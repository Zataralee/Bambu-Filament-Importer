using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using BambuFilamentImporter.Models;
using BambuFilamentImporter.Services;

namespace BambuFilamentImporter.Workspace;

public sealed class ProductChoice : INotifyPropertyChanged
{
    private bool _selected;
    public required string PackagePath { get; init; }
    public required string ProfileName { get; init; }
    public required string Manufacturer { get; init; }
    public required string Material { get; init; }
    public string Name => CurrentFilamentEntry.GetProductName(ProfileName);
    public string PackageState => "Downloaded";
    public string Coverage { get; set; } = "Not in library";
    public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record PlannedChange(string Name, string Action, string Location);

public sealed class ImportPlan
{
    public required LoadedFilamentPackage Package { get; init; }
    public required string Fingerprint { get; init; }
    public required ImportDestination Destination { get; init; }
    public required List<PlannedChange> Changes { get; init; }
    public required Dictionary<string, string> PackageHashes { get; init; }
    public int SelectedProducts { get; init; }
    public bool HasChanges => Package.Manifest.Profiles.Any(p => p.IsSelected);
    public static ImportPlan Create(BambuPaths paths, IReadOnlyCollection<ProductChoice> selected,
        IReadOnlyCollection<PrinterTarget> printers, ImportDestination destination)
    {
        if (selected.Count == 0) throw new InvalidOperationException("Select at least one filament.");
        if (printers.Count == 0 || printers.Any(p => p.MachinePresetNames.Count == 0))
            throw new InvalidOperationException("Select at least one printer and nozzle.");
        var before = WorkspaceContext.Fingerprint(paths);
        var current = new BambuLibraryScanner(paths).LoadCurrentFilaments();
        var manifest = new FilamentPackage { DisplayName = "Workspace selection", PackageId = "bfi-workspace-selection", PrinterNeutral = false };
        var jsons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changes = new List<PlannedChange>();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in selected.GroupBy(p => p.PackagePath, StringComparer.OrdinalIgnoreCase))
        {
            hashes[group.Key] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(group.Key)));
            var package = PackageReader.Load(group.Key);
            if (!package.Manifest.PrinterNeutral)
                throw new InvalidOperationException("Add filaments accepts printer-neutral manufacturer packages. Import older packages through Maintenance → Advanced library tools.");
            foreach (var p in package.Manifest.Profiles)
            {
                p.IsSelected = group.Any(c => c.ProfileName.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
                p.IsDuplicate = false; // Imports preserve existing tuning; replacement is not implicit.
            }
            var expanded = PrinterProfileExpander.Expand(package, printers, current,
                destination == ImportDestination.Both ? ImportDestination.DeviceAms : destination);
            var payload = expanded.ProfileJsonByPath.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

            // "Both" uses active system profiles plus project dropdown registration, not a duplicate user preset.
            if (destination == ImportDestination.Both)
            {
                foreach (var product in package.Manifest.Profiles.Where(p => p.IsSelected))
                foreach (var printer in printers)
                {
                    if (!PrinterProfileExpander.IsTargetCovered(product, printer, current, ImportDestination.DeviceAms)) continue;
                    foreach (var existing in current.Where(e => e.CanEdit && e.StorageKind == FilamentStorageKind.SystemCatalog
                        && e.ProductName.Equals(CurrentFilamentEntry.GetProductName(product.Name), StringComparison.OrdinalIgnoreCase)
                        && !e.IsBaseProfile && !e.IsProjectPreset
                        && e.CompatiblePrinters.Intersect(PrinterProfileExpander.GetRuntimeCompatiblePrinterNames(printer), StringComparer.OrdinalIgnoreCase).Any()))
                    {
                        if (expanded.Manifest.Profiles.Any(p => p.Name.Equals(existing.Name, StringComparison.OrdinalIgnoreCase))) continue;
                        var entry = new FilamentProfileEntry { Name = existing.Name, OriginalName = existing.Name,
                            RelativePath = existing.RelativePath, OriginalRelativePath = existing.RelativePath, IsSelected = true,
                            MaterialFamily = product.MaterialFamily, VendorGroup = product.VendorGroup };
                        expanded.Manifest.Profiles.Add(entry);
                        payload[entry.RelativePath] = File.ReadAllText(existing.ProfilePath);
                    }
                }
            }
            foreach (var entry in expanded.Manifest.Profiles)
            {
                var sourceKey = entry.OriginalRelativePath;
                var text = payload[sourceKey];
                if (entry.IsSelected)
                {
                    var kind = destination == ImportDestination.ProjectLibrary ? FilamentStorageKind.UserPreset : FilamentStorageKind.SystemCatalog;
                    var existing = current.FirstOrDefault(e => e.CanEdit && e.StorageKind == kind && e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
                    var action = "Add profile";
                    if (existing is not null)
                    {
                        var original = BambuJson.Parse(File.ReadAllText(existing.ProfilePath))!.AsObject();
                        var proposed = BambuJson.Parse(text)!.AsObject();
                        var oldMachines = ReadMachines(original);
                        var merged = oldMachines.Concat(ReadMachines(proposed)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        if (merged.Count > oldMachines.Count)
                        {
                            original["compatible_printers"] = new JsonArray(merged.Select(s => (JsonNode?)s).ToArray());
                            action = "Extend printer coverage; keep settings";
                        }
                        else action = "Enable in project dropdown; keep settings";
                        text = original.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                        if (kind == FilamentStorageKind.SystemCatalog)
                        {
                            entry.RelativePath = existing.RelativePath;
                            entry.OriginalRelativePath = existing.RelativePath;
                        }
                    }
                    changes.Add(new(entry.Name, action, destination.ToString()));
                }
                if (manifest.Profiles.Any(p => p.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)) || jsons.ContainsKey(entry.OriginalRelativePath))
                    throw new InvalidDataException("Selected packages overlap on " + entry.Name + ". Import one source at a time.");
                manifest.Profiles.Add(entry);
                jsons.Add(entry.OriginalRelativePath, text);
            }
        }
        if (before != WorkspaceContext.Fingerprint(paths)) throw new IOException("Your library changed during review. Refresh and review again.");
        return new ImportPlan { Package = new LoadedFilamentPackage { FilePath = "", Manifest = manifest, ProfileJsonByPath = jsons },
            Destination = destination, Fingerprint = before, Changes = changes, SelectedProducts = selected.Count, PackageHashes = hashes };
    }
    private static List<string> ReadMachines(JsonObject node) => node["compatible_printers"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
    public InstallResult Apply(WorkspaceContext workspace)
    {
        workspace.EnsureWritable();
        if (!HasChanges) throw new InvalidOperationException("There are no changes to apply.");
        if (Fingerprint != WorkspaceContext.Fingerprint(workspace.Paths)
            || PackageHashes.Any(p => !File.Exists(p.Key) || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p.Key))) != p.Value))
            throw new InvalidOperationException("The library or package changed since review. Review the selection again before installing.");
        return LibraryTransaction.Run(workspace, "import", () => new BambuInstaller(workspace.Paths,
            () => workspace.IsLive && BambuProcess.IsStudioRunning()).Install(Package, Destination, false));
    }
}

public static class LibraryTransaction
{
    public static T Run<T>(WorkspaceContext workspace, string reason, Func<T> action)
    {
        workspace.EnsureWritable();
        workspace.CreateBackup(reason);
        var paths = workspace.Paths;
        var originals = EnumerateFiles(paths).ToDictionary(p => p, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        try
        {
            workspace.EnsureWritable();
            var result = action();
            BambuCatalogIntegrity.ValidateProfileRoot(paths.RoamingProfileRoot);
            return result;
        }
        catch (Exception failure)
        {
            try
            {
                foreach (var added in EnumerateFiles(paths).Where(p => !originals.ContainsKey(p))) File.Delete(added);
                foreach (var pair in originals)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
                    File.WriteAllBytes(pair.Key, pair.Value);
                }
            }
            catch (Exception rollback) { throw new AggregateException("Operation and rollback failed. Restore the automatic backup in Maintenance.", failure, rollback); }
            throw new IOException("The change failed. The previous library was restored automatically. " + failure.Message, failure);
        }
    }
    private static IEnumerable<string> EnumerateFiles(BambuPaths paths) =>
        new[] { paths.ConfigPath, Path.Combine(paths.RoamingProfileRoot, "BBL.json") }.Where(File.Exists)
        .Concat(new[] { Path.Combine(paths.RoamingProfileRoot, "BBL", "filament"), paths.UserRoot }
            .Where(Directory.Exists).SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(p => Path.GetExtension(p) is ".json" or ".info"));
}
