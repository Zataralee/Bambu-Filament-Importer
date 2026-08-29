using System.IO;
using System.Text.Json.Nodes;
using BambuFilamentImporter.Models;

namespace BambuFilamentImporter.Services;

public sealed class BambuLibraryScanner
{
    private readonly BambuPaths _paths;

    public BambuLibraryScanner(BambuPaths paths)
    {
        _paths = paths;
    }

    public List<CurrentFilamentEntry> LoadCurrentFilaments()
    {
        var byName = new Dictionary<string, CurrentFilamentEntry>(StringComparer.OrdinalIgnoreCase);
        var projectPresets = LoadProjectPresetNames();
        LoadFromRoot(_paths.RoamingProfileRoot, "Roaming system catalog", projectPresets, byName);
        LoadFromRoot(_paths.ProgramProfileRoot, "Installed system catalog", projectPresets, byName);
        var results = byName.Values.ToList();
        LoadUserPresets(results);
        return results.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private HashSet<string> LoadProjectPresetNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_paths.ConfigPath))
        {
            return names;
        }

        try
        {
            var text = File.ReadAllText(_paths.ConfigPath);
            var jsonText = text.Split("\n# MD5 checksum ", StringSplitOptions.None)[0].TrimEnd();
            var config = JsonNode.Parse(jsonText)!.AsObject();
            var filaments = config["filaments"]?.AsArray();
            if (filaments is null)
            {
                return names;
            }

            foreach (var item in filaments)
            {
                var name = item?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }
        catch
        {
            return names;
        }

        return names;
    }

    private static void LoadFromRoot(string profileRoot, string source, HashSet<string> projectPresets, Dictionary<string, CurrentFilamentEntry> byName)
    {
        var manifestPath = Path.Combine(profileRoot, "BBL.json");
        if (!File.Exists(manifestPath))
        {
            return;
        }

        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var filamentList = manifest["filament_list"]?.AsArray();
        if (filamentList is null)
        {
            return;
        }

        var pathsByName = filamentList
            .Where(item => !string.IsNullOrWhiteSpace(item?["name"]?.GetValue<string>())
                && !string.IsNullOrWhiteSpace(item?["sub_path"]?.GetValue<string>()))
            .GroupBy(item => item!["name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => Path.Combine(
                    profileRoot,
                    "BBL",
                    group.First()!["sub_path"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar)),
                StringComparer.OrdinalIgnoreCase);

        foreach (var item in filamentList)
        {
            var name = item?["name"]?.GetValue<string>();
            var relativePath = item?["sub_path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var profilePath = Path.Combine(profileRoot, "BBL", relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (byName.TryGetValue(name, out var existingEntry))
            {
                existingEntry.AdditionalCopies.Add(new FilamentProfileCopy
                {
                    Source = source,
                    RelativePath = relativePath,
                    ProfileRoot = profileRoot,
                    ProfilePath = profilePath,
                    StorageKind = FilamentStorageKind.SystemCatalog
                });
                existingEntry.Source = "Roaming + Installed system catalogs";
                continue;
            }

            var inProject = projectPresets.Contains(name);
            var isRoaming = source.StartsWith("Roaming", StringComparison.OrdinalIgnoreCase);
            var entry = new CurrentFilamentEntry
            {
                Name = name,
                OriginalName = name,
                Source = source,
                Location = isRoaming
                    ? inProject ? "Device/AMS catalog + Project Library" : "Device/AMS catalog only"
                    : "Installed mirror only (not active Device/AMS)",
                RelativePath = relativePath,
                ProfileRoot = profileRoot,
                ProfilePath = profilePath,
                IsProjectPreset = inProject,
                CanEdit = isRoaming,
                StorageKind = FilamentStorageKind.SystemCatalog,
                VendorGroup = "",
                MaterialFamily = ""
            };

            if (File.Exists(profilePath))
            {
                TryReadProfileDetails(profilePath, entry);
                if (string.IsNullOrWhiteSpace(entry.VendorGroup))
                {
                    TryReadInheritedDetails(profilePath, entry, pathsByName);
                }
            }

            byName[name] = entry;
        }
    }

    private void LoadUserPresets(List<CurrentFilamentEntry> results)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            _paths.ActiveUserFilamentFolder,
            Path.Combine(_paths.UserRoot, "default", "filament")
        };

        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var profilePath in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var profile = JsonNode.Parse(File.ReadAllText(profilePath))!.AsObject();
                    var name = profile["name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(profilePath);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var entry = new CurrentFilamentEntry
                    {
                        Name = name,
                        OriginalName = name,
                        VendorGroup = "Custom / User Presets",
                        MaterialFamily = profile["filament_type"]?.AsArray().FirstOrDefault()?.GetValue<string>() ?? "",
                        Source = folder.Equals(_paths.ActiveUserFilamentFolder, StringComparison.OrdinalIgnoreCase)
                            ? "Active user preset library"
                            : "Default user preset library",
                        Location = "Project Library (user preset)",
                        RelativePath = Path.GetFileName(profilePath),
                        ProfileRoot = folder,
                        ProfilePath = profilePath,
                        InfoPath = Path.ChangeExtension(profilePath, ".info"),
                        IsProjectPreset = true,
                        CanEdit = true,
                        StorageKind = FilamentStorageKind.UserPreset,
                        CompatiblePrinters = profile["compatible_printers"]?.AsArray()
                            .Select(item => item?.GetValue<string>() ?? "")
                            .Where(name => !string.IsNullOrWhiteSpace(name))
                            .ToList() ?? []
                    };

                    if (string.IsNullOrWhiteSpace(entry.MaterialFamily))
                    {
                        TryReadInheritedDetails(profilePath, entry);
                    }

                    results.Add(entry);
                }
                catch
                {
                    continue;
                }
            }
        }
    }

    private static void TryReadProfileDetails(string profilePath, CurrentFilamentEntry entry)
    {
        try
        {
            var profile = JsonNode.Parse(File.ReadAllText(profilePath))!.AsObject();
            entry.VendorGroup = profile["filament_vendor"]?.AsArray().FirstOrDefault()?.GetValue<string>() ?? "";
            entry.MaterialFamily = profile["filament_type"]?.AsArray().FirstOrDefault()?.GetValue<string>() ?? "";
            entry.CompatiblePrinters = profile["compatible_printers"]?.AsArray()
                .Select(item => item?.GetValue<string>() ?? "")
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList() ?? [];
        }
        catch
        {
            entry.VendorGroup = "";
            entry.MaterialFamily = "";
        }
    }

    private static void TryReadInheritedDetails(
        string profilePath,
        CurrentFilamentEntry entry,
        IReadOnlyDictionary<string, string>? catalogPaths = null)
    {
        try
        {
            var currentPath = profilePath;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var profile = JsonNode.Parse(File.ReadAllText(currentPath))!.AsObject();
                var inherits = profile["inherits"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(inherits) || !visited.Add(inherits))
                {
                    return;
                }

                var inheritedPath = catalogPaths is not null && catalogPaths.TryGetValue(inherits, out var indexedPath)
                    ? indexedPath
                    : Path.Combine(Path.GetDirectoryName(currentPath)!, inherits + ".json");
                if (!File.Exists(inheritedPath))
                {
                    return;
                }

                var inherited = JsonNode.Parse(File.ReadAllText(inheritedPath))!.AsObject();
                if (string.IsNullOrWhiteSpace(entry.VendorGroup))
                {
                    entry.VendorGroup = inherited["filament_vendor"]?.AsArray().FirstOrDefault()?.GetValue<string>() ?? "";
                }
                if (string.IsNullOrWhiteSpace(entry.MaterialFamily))
                {
                    entry.MaterialFamily = inherited["filament_type"]?.AsArray().FirstOrDefault()?.GetValue<string>() ?? "";
                }
                if (!string.IsNullOrWhiteSpace(entry.VendorGroup) && !string.IsNullOrWhiteSpace(entry.MaterialFamily))
                {
                    return;
                }

                currentPath = inheritedPath;
            }
        }
        catch
        {
            return;
        }
    }
}
