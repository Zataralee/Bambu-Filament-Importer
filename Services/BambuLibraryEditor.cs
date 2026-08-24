using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using BambuFilamentImporter.Models;

namespace BambuFilamentImporter.Services;

public sealed class BambuLibraryEditor
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly BambuPaths _paths;

    public BambuLibraryEditor(BambuPaths paths)
    {
        _paths = paths;
    }

    public void Save(CurrentFilamentEntry entry, string newName, string newVendorGroup)
    {
        EnsureEditable(entry);
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new InvalidOperationException("Name cannot be blank.");
        }

        if (!File.Exists(entry.ProfilePath))
        {
            throw new FileNotFoundException("The selected profile file could not be found.", entry.ProfilePath);
        }

        if (entry.StorageKind == FilamentStorageKind.UserPreset)
        {
            SaveUserPreset(entry, newName);
            return;
        }

        SaveSystemProfile(entry, newName, newVendorGroup);
    }

    public int Remove(CurrentFilamentEntry entry) => RemoveMany([entry]);

    public int RemoveMany(IEnumerable<CurrentFilamentEntry> requestedEntries)
    {
        var entries = requestedEntries
            .Where(entry => entry.CanEdit)
            .DistinctBy(entry => entry.ProfilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (entries.Count == 0)
        {
            throw new InvalidOperationException("The selection contains no editable roaming or user presets.");
        }

        foreach (var entry in entries)
        {
            EnsureEditable(entry);
        }

        EnsureDependenciesAreIncluded(entries);
        var names = entries.Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var systemGroups = entries
            .Where(entry => entry.StorageKind == FilamentStorageKind.SystemCatalog)
            .GroupBy(entry => entry.ProfileRoot, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var rootGroup in systemGroups)
        {
            RemoveFromManifest(rootGroup.Key, rootGroup);
        }

        foreach (var entry in entries.OrderBy(entry => entry.IsBaseProfile))
        {
            FileBackup.Create(entry.ProfilePath, "bflib-remove-backup");
            if (File.Exists(entry.ProfilePath))
            {
                File.Delete(entry.ProfilePath);
            }

            if (entry.StorageKind == FilamentStorageKind.UserPreset && File.Exists(entry.InfoPath))
            {
                FileBackup.Create(entry.InfoPath, "bflib-remove-backup");
                File.Delete(entry.InfoPath);
            }
        }

        foreach (var rootGroup in systemGroups)
        {
            BambuCatalogIntegrity.ValidateProfileRoot(rootGroup.Key);
        }

        RemoveFromProjectConfig(names);
        return entries.Count;
    }

    public int RenameManufacturer(FilamentGroup group, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new InvalidOperationException("Manufacturer name cannot be blank.");
        }

        var baseProfiles = group.Profiles
            .Where(entry => entry.CanEdit && entry.StorageKind == FilamentStorageKind.SystemCatalog && entry.IsBaseProfile)
            .ToList();
        if (baseProfiles.Count == 0)
        {
            throw new InvalidOperationException("This manufacturer has no editable roaming base profiles.");
        }

        foreach (var entry in baseProfiles)
        {
            FileBackup.Create(entry.ProfilePath, "bflib-edit-backup");
            var profile = JsonNode.Parse(File.ReadAllText(entry.ProfilePath))!.AsObject();
            profile["filament_vendor"] = new JsonArray(newName);
            File.WriteAllText(entry.ProfilePath, profile.ToJsonString(WriteOptions) + Environment.NewLine);
        }

        return baseProfiles.Count;
    }

    private void SaveSystemProfile(CurrentFilamentEntry entry, string newName, string newVendorGroup)
    {
        var oldName = entry.Name;
        FileBackup.Create(entry.ProfilePath, "bflib-edit-backup");
        UpdateProfileFile(entry.ProfileRoot, entry.ProfilePath, oldName, newName, newVendorGroup);
        if (entry.IsBaseProfile)
        {
            UpdateChildrenInherits(entry.ProfileRoot, entry.ProfilePath, oldName, newName);
        }

        var newRelativePath = RenameProfileFile(entry, newName);
        UpdateManifest(entry.ProfileRoot, oldName, newName, entry.RelativePath, newRelativePath);
        UpdateProjectConfig(oldName, newName);

        entry.Name = newName;
        entry.OriginalName = newName;
        entry.VendorGroup = newVendorGroup;
        entry.RelativePath = newRelativePath;
        entry.ProfilePath = Path.Combine(entry.ProfileRoot, "BBL", newRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private void SaveUserPreset(CurrentFilamentEntry entry, string newName)
    {
        var oldName = entry.Name;
        var oldPath = entry.ProfilePath;
        var newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, newName + ".json");
        if (!oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(newPath))
        {
            throw new IOException($"A user preset named {newName} already exists.");
        }

        FileBackup.Create(oldPath, "bflib-edit-backup");
        var profile = JsonNode.Parse(File.ReadAllText(oldPath))!.AsObject();
        profile["name"] = newName;
        if (profile["filament_settings_id"] is JsonArray settingsIds)
        {
            for (var i = 0; i < settingsIds.Count; i++)
            {
                if (string.Equals(settingsIds[i]?.GetValue<string>(), oldName, StringComparison.OrdinalIgnoreCase))
                {
                    settingsIds[i] = newName;
                }
            }
        }

        File.WriteAllText(oldPath, profile.ToJsonString(WriteOptions) + Environment.NewLine);
        if (!oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(oldPath, newPath);
        }

        var oldInfoPath = entry.InfoPath;
        var newInfoPath = Path.ChangeExtension(newPath, ".info");
        if (File.Exists(oldInfoPath) && !oldInfoPath.Equals(newInfoPath, StringComparison.OrdinalIgnoreCase))
        {
            FileBackup.Create(oldInfoPath, "bflib-edit-backup");
            File.Move(oldInfoPath, newInfoPath, overwrite: false);
        }

        UpdateProjectConfig(oldName, newName);
        entry.Name = newName;
        entry.OriginalName = newName;
        entry.RelativePath = Path.GetFileName(newPath);
        entry.ProfilePath = newPath;
        entry.InfoPath = newInfoPath;
    }

    private static void UpdateProfileFile(string profileRoot, string profilePath, string oldName, string newName, string newVendorGroup)
    {
        var node = JsonNode.Parse(File.ReadAllText(profilePath))!.AsObject();
        node["name"] = newName;

        if (!string.IsNullOrWhiteSpace(newVendorGroup) && newName.EndsWith("@base", StringComparison.OrdinalIgnoreCase))
        {
            node["filament_vendor"] = new JsonArray(newVendorGroup);
        }
        else if (!string.IsNullOrWhiteSpace(newVendorGroup))
        {
            UpdateInheritedBaseVendor(profileRoot, profilePath, node, newVendorGroup);
        }

        var inherits = node["inherits"]?.GetValue<string>();
        if (string.Equals(inherits, oldName, StringComparison.OrdinalIgnoreCase))
        {
            node["inherits"] = newName;
        }

        File.WriteAllText(profilePath, node.ToJsonString(WriteOptions) + Environment.NewLine);
    }

    private static void UpdateInheritedBaseVendor(string profileRoot, string profilePath, JsonObject profile, string newVendorGroup)
    {
        var inherits = profile["inherits"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(inherits))
        {
            return;
        }

        var basePath = FindProfilePath(profileRoot, inherits)
            ?? Path.Combine(Path.GetDirectoryName(profilePath)!, inherits + ".json");
        if (!File.Exists(basePath))
        {
            return;
        }

        FileBackup.Create(basePath, "bflib-edit-backup");
        var baseNode = JsonNode.Parse(File.ReadAllText(basePath))!.AsObject();
        baseNode["filament_vendor"] = new JsonArray(newVendorGroup);
        File.WriteAllText(basePath, baseNode.ToJsonString(WriteOptions) + Environment.NewLine);
    }

    private static string RenameProfileFile(CurrentFilamentEntry entry, string newName)
    {
        var directory = Path.GetDirectoryName(entry.ProfilePath)!;
        var newPath = Path.Combine(directory, newName + ".json");
        if (!entry.ProfilePath.Equals(newPath, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(newPath))
            {
                throw new IOException($"A profile file already exists for {newName}.");
            }

            File.Move(entry.ProfilePath, newPath);
        }

        var relativeDirectory = Path.GetDirectoryName(entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)) ?? "";
        return Path.Combine(relativeDirectory, newName + ".json").Replace('\\', '/');
    }

    private static void UpdateManifest(string profileRoot, string oldName, string newName, string oldRelativePath, string newRelativePath)
    {
        var manifestPath = Path.Combine(profileRoot, "BBL.json");
        FileBackup.Create(manifestPath, "bflib-edit-backup");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var filamentList = manifest["filament_list"]!.AsArray();
        foreach (var item in filamentList)
        {
            var itemName = item?["name"]?.GetValue<string>();
            var itemPath = item?["sub_path"]?.GetValue<string>();
            if (string.Equals(itemName, oldName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(itemPath, oldRelativePath, StringComparison.OrdinalIgnoreCase))
            {
                item!["name"] = newName;
                item["sub_path"] = newRelativePath;
            }
        }

        File.WriteAllText(manifestPath, manifest.ToJsonString(WriteOptions) + Environment.NewLine);
    }

    private static void UpdateChildrenInherits(string profileRoot, string baseProfilePath, string oldName, string newName)
    {
        var filamentRoot = Path.Combine(profileRoot, "BBL", "filament");
        foreach (var path in Directory.EnumerateFiles(filamentRoot, "*.json", SearchOption.AllDirectories))
        {
            if (path.Equals(baseProfilePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                if (!string.Equals(node["inherits"]?.GetValue<string>(), oldName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                FileBackup.Create(path, "bflib-edit-backup");
                node["inherits"] = newName;
                File.WriteAllText(path, node.ToJsonString(WriteOptions) + Environment.NewLine);
            }
            catch (JsonException)
            {
                continue;
            }
        }
    }

    private static string? FindProfilePath(string profileRoot, string name)
    {
        var manifestPath = Path.Combine(profileRoot, "BBL.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject();
        var item = manifest?["filament_list"]?.AsArray().FirstOrDefault(entry =>
            string.Equals(entry?["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
        var relativePath = item?["sub_path"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(relativePath)
            ? null
            : Path.Combine(profileRoot, "BBL", relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void RemoveFromManifest(string profileRoot, IEnumerable<CurrentFilamentEntry> entries)
    {
        var manifestPath = Path.Combine(profileRoot, "BBL.json");
        FileBackup.Create(manifestPath, "bflib-remove-backup");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var filamentList = manifest["filament_list"]!.AsArray();
        var names = entries.Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = entries.Select(entry => entry.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = filamentList.Count - 1; i >= 0; i--)
        {
            var itemName = filamentList[i]?["name"]?.GetValue<string>();
            var itemPath = filamentList[i]?["sub_path"]?.GetValue<string>();
            if ((!string.IsNullOrWhiteSpace(itemName) && names.Contains(itemName))
                || (!string.IsNullOrWhiteSpace(itemPath) && paths.Contains(itemPath)))
            {
                filamentList.RemoveAt(i);
            }
        }

        File.WriteAllText(manifestPath, manifest.ToJsonString(WriteOptions) + Environment.NewLine);
    }

    private void UpdateProjectConfig(string oldName, string newName)
    {
        if (!File.Exists(_paths.ConfigPath))
        {
            return;
        }

        var config = LoadConfig();
        var filaments = config["filaments"]?.AsArray();
        if (filaments is null)
        {
            return;
        }

        var changed = false;
        for (var i = 0; i < filaments.Count; i++)
        {
            if (string.Equals(filaments[i]?.GetValue<string>(), oldName, StringComparison.OrdinalIgnoreCase))
            {
                filaments[i] = newName;
                changed = true;
            }
        }

        if (changed)
        {
            FileBackup.Create(_paths.ConfigPath, "bflib-edit-backup");
            SaveConfig(config);
        }
    }

    private void RemoveFromProjectConfig(HashSet<string> names)
    {
        if (!File.Exists(_paths.ConfigPath))
        {
            return;
        }

        var config = LoadConfig();
        var changed = RemoveNames(config["filaments"]?.AsArray(), names);
        changed |= RemoveNames(config["presets"]?["filaments"]?.AsArray(), names);
        if (changed)
        {
            FileBackup.Create(_paths.ConfigPath, "bflib-remove-backup");
            SaveConfig(config);
        }
    }

    private static bool RemoveNames(JsonArray? array, HashSet<string> names)
    {
        if (array is null)
        {
            return false;
        }

        var changed = false;
        for (var i = array.Count - 1; i >= 0; i--)
        {
            var name = array[i]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(name) && names.Contains(name))
            {
                array.RemoveAt(i);
                changed = true;
            }
        }

        return changed;
    }

    private JsonObject LoadConfig()
    {
        var text = File.ReadAllText(_paths.ConfigPath);
        var jsonText = text.Split("\n# MD5 checksum ", StringSplitOptions.None)[0].TrimEnd();
        return JsonNode.Parse(jsonText)!.AsObject();
    }

    private void SaveConfig(JsonObject config) =>
        File.WriteAllText(_paths.ConfigPath, config.ToJsonString(WriteOptions) + Environment.NewLine);

    private static void EnsureDependenciesAreIncluded(List<CurrentFilamentEntry> entries)
    {
        foreach (var rootGroup in entries
            .Where(entry => entry.StorageKind == FilamentStorageKind.SystemCatalog)
            .GroupBy(entry => entry.ProfileRoot, StringComparer.OrdinalIgnoreCase))
        {
            var selectedPaths = rootGroup.Select(entry => entry.ProfilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedNames = rootGroup.Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var filamentRoot = Path.Combine(rootGroup.Key, "BBL", "filament");
            foreach (var path in Directory.EnumerateFiles(filamentRoot, "*.json", SearchOption.AllDirectories))
            {
                if (selectedPaths.Contains(path))
                {
                    continue;
                }

                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                    if (selectedNames.Contains(node["inherits"]?.GetValue<string>() ?? ""))
                    {
                        throw new InvalidOperationException(
                            $"The selection is still used by {Path.GetFileNameWithoutExtension(path)}. Remove the whole filament or manufacturer so every dependent profile is included.");
                    }
                }
                catch (JsonException)
                {
                    continue;
                }
            }
        }
    }

    private static void EnsureEditable(CurrentFilamentEntry entry)
    {
        if (!entry.CanEdit)
        {
            throw new InvalidOperationException("Installed Bambu profiles are read-only here. Roaming catalog and user presets can be edited or removed.");
        }
    }
}
