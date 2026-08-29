using System.Text.Json;
using System.Text.Json.Nodes;
using BambuFilamentImporter.Models;

namespace BambuFilamentImporter.Services;

public static class PrinterProfileExpander
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static LoadedFilamentPackage Expand(
        LoadedFilamentPackage package,
        IReadOnlyCollection<PrinterTarget> targets,
        IReadOnlyCollection<CurrentFilamentEntry>? currentEntries = null,
        ImportDestination destination = ImportDestination.Both)
    {
        package = AmsFilamentId.NormalizePackage(package);
        if (!package.Manifest.PrinterNeutral)
        {
            return package;
        }

        if (targets.Count == 0)
        {
            throw new InvalidOperationException("Select at least one target printer before importing this manufacturer library.");
        }

        var selectedProducts = package.Manifest.Profiles.Where(profile => profile.IsSelected).ToList();
        if (selectedProducts.Count == 0)
        {
            throw new InvalidOperationException("No filaments are selected for import.");
        }

        var expandedManifest = new FilamentPackage
        {
            Format = package.Manifest.Format,
            FormatVersion = package.Manifest.FormatVersion,
            PackageId = package.Manifest.PackageId,
            DisplayName = package.Manifest.DisplayName,
            Manufacturer = package.Manifest.Manufacturer,
            Version = package.Manifest.Version,
            CreatedUtc = package.Manifest.CreatedUtc,
            PrinterNeutral = false,
            CompatibilityNote = package.Manifest.CompatibilityNote,
            SourceUrls = [.. package.Manifest.SourceUrls]
        };
        var jsonByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var product in selectedProducts)
        {
            var sourcePath = string.IsNullOrWhiteSpace(product.OriginalRelativePath)
                ? product.RelativePath
                : product.OriginalRelativePath;
            var baseJson = JsonNode.Parse(package.ProfileJsonByPath[sourcePath])!.AsObject();
            var baseName = product.Name.EndsWith("@base", StringComparison.OrdinalIgnoreCase)
                ? product.Name
                : product.Name + " @base";
            baseJson["name"] = baseName;
            var basePath = product.RelativePath;
            var overwriteExisting = product.IsDuplicate;
            var baseExists = currentEntries?.Any(entry =>
                entry.StorageKind == FilamentStorageKind.SystemCatalog
                && entry.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase)) == true;
            var writeBase = destination != ImportDestination.ProjectLibrary && (!baseExists || overwriteExisting);
            expandedManifest.Profiles.Add(CloneEntry(product, baseName, basePath, writeBase));
            jsonByPath[basePath] = baseJson.ToJsonString(WriteOptions);

            foreach (var target in targets)
            {
                var targetCovered = currentEntries is not null && IsTargetCovered(product, target, currentEntries, destination);
                if (targetCovered && !overwriteExisting)
                {
                    continue;
                }

                var productName = CurrentFilamentEntry.GetProductName(baseName);
                var childName = $"{productName} @{target.ProfileSuffix}";
                var childPath = BuildPrinterPath(product.RelativePath, target.ProfileSuffix, childName);
                var child = new JsonObject
                {
                    ["type"] = "filament",
                    ["name"] = childName,
                    ["inherits"] = baseName,
                    ["from"] = "system",
                    ["setting_id"] = "GFV" + GetCode(childName, 6),
                    ["instantiation"] = "true",
                    ["compatible_printers"] = new JsonArray(target.MachinePresetNames.Select(name => JsonValue.Create(name)).ToArray())
                };
                expandedManifest.Profiles.Add(CloneEntry(product, childName, childPath, isSelected: true));
                expandedManifest.ProjectPresetNames.Add(childName);
                jsonByPath[childPath] = child.ToJsonString(WriteOptions);
            }
        }

        return new LoadedFilamentPackage
        {
            FilePath = package.FilePath,
            Manifest = expandedManifest,
            ProfileJsonByPath = jsonByPath
        };
    }

    public static string ChildName(FilamentProfileEntry product, PrinterTarget target)
    {
        var productName = CurrentFilamentEntry.GetProductName(product.Name);
        return $"{productName} @{target.ProfileSuffix}";
    }

    public static bool IsTargetCovered(
        FilamentProfileEntry product,
        PrinterTarget target,
        IReadOnlyCollection<CurrentFilamentEntry> currentEntries,
        ImportDestination destination)
    {
        return destination switch
        {
            ImportDestination.DeviceAms => IsTargetCoveredInStorage(product, target, currentEntries, FilamentStorageKind.SystemCatalog),
            ImportDestination.ProjectLibrary => IsTargetCoveredInStorage(product, target, currentEntries, FilamentStorageKind.UserPreset),
            _ => IsTargetCoveredInStorage(product, target, currentEntries, FilamentStorageKind.SystemCatalog)
                && IsTargetCoveredInStorage(product, target, currentEntries, FilamentStorageKind.UserPreset)
        };
    }

    private static bool IsTargetCoveredInStorage(
        FilamentProfileEntry product,
        PrinterTarget target,
        IReadOnlyCollection<CurrentFilamentEntry> currentEntries,
        FilamentStorageKind storageKind)
    {
        var productName = CurrentFilamentEntry.GetProductName(product.Name);
        var matching = currentEntries
            .Where(entry => entry.StorageKind == storageKind
                && entry.ProductName.Equals(productName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var coveredMachines = matching
            .SelectMany(entry => entry.CompatiblePrinters)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (target.MachinePresetNames.All(coveredMachines.Contains))
        {
            return true;
        }

        var expectedName = $"{productName} @{target.ProfileSuffix}";
        return matching.Any(entry => entry.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase));
    }

    private static FilamentProfileEntry CloneEntry(FilamentProfileEntry source, string name, string path, bool isSelected) => new()
    {
        IsSelected = isSelected,
        Name = name,
        OriginalName = name,
        RelativePath = path,
        OriginalRelativePath = path,
        Kind = "system",
        VendorGroup = source.VendorGroup,
        MaterialFamily = source.MaterialFamily
    };

    private static string BuildPrinterPath(string originalPath, string profileSuffix, string fallbackName)
    {
        var normalized = originalPath.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var directory = slash >= 0 ? normalized[..(slash + 1)] : "";
        var fileName = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        var stem = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? fileName[..^5] : fileName;
        var marker = stem.LastIndexOf(" @base", StringComparison.OrdinalIgnoreCase);
        return marker >= 0
            ? $"{directory}{stem[..marker]} @{profileSuffix}.json"
            : $"{directory}{fallbackName}.json";
    }

    private static string GetCode(string text, int length)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash)[..length];
    }
}
