using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using BambuFilamentImporter.Models;

namespace BambuFilamentImporter.Services;

public sealed class BambuInstaller
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly BambuPaths _paths;
    private readonly Func<bool> _isStudioRunning;

    public BambuInstaller(BambuPaths paths, Func<bool>? isStudioRunning = null)
    {
        _paths = paths;
        _isStudioRunning = isStudioRunning ?? BambuProcess.IsStudioRunning;
    }

    public InstallResult Install(LoadedFilamentPackage package, ImportDestination destination, bool installProgram)
    {
        package = AmsFilamentId.NormalizePackage(package);
        if (_isStudioRunning())
        {
            throw new InvalidOperationException("Close Bambu Studio before importing. Bambu Studio may overwrite preset/config files while it exits.");
        }

        if (installProgram && !ElevationService.IsAdministrator())
        {
            throw new UnauthorizedAccessException("Program Files mirroring requires Administrator access.");
        }

        ValidateSelection(package);
        var result = new InstallResult();
        if (destination is ImportDestination.DeviceAms or ImportDestination.Both)
        {
            InstallIntoProfileRoot(package, _paths.RoamingProfileRoot, result);
        }

        if (installProgram && destination is ImportDestination.DeviceAms or ImportDestination.Both)
        {
            InstallIntoProfileRoot(package, _paths.ProgramProfileRoot, result);
        }

        if (destination == ImportDestination.ProjectLibrary)
        {
            InstallUserPresets(package, result);
            UpdateProjectFilamentList(package, result);
        }
        else if (destination == ImportDestination.Both)
        {
            UpdateProjectFilamentList(package, result);
        }

        return result;
    }

    private static void ValidateSelection(LoadedFilamentPackage package)
    {
        var selected = package.Manifest.Profiles.Where(profile => profile.IsSelected).ToList();
        if (selected.Count == 0)
        {
            throw new InvalidOperationException("No profiles are selected for import.");
        }

        var duplicateName = selected
            .GroupBy(profile => profile.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicateName is not null)
        {
            throw new InvalidDataException(string.IsNullOrWhiteSpace(duplicateName.Key)
                ? "A selected profile has a blank name."
                : $"More than one selected profile is named {duplicateName.Key}.");
        }

        foreach (var profile in selected)
        {
            if (profile.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException($"{profile.Name} contains a character Windows cannot use in a file name.");
            }
        }
    }

    private static void InstallIntoProfileRoot(LoadedFilamentPackage package, string profileRoot, InstallResult result)
    {
        var selectedProfiles = package.Manifest.Profiles.Where(profile => profile.IsSelected).ToList();
        var nameMap = selectedProfiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.OriginalName))
            .ToDictionary(profile => profile.OriginalName, profile => profile.Name, StringComparer.OrdinalIgnoreCase);
        var manifestPath = Path.Combine(profileRoot, "BBL.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("Bambu profile manifest was not found.", manifestPath);
        }

        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
            ?? throw new InvalidDataException($"Bambu profile manifest could not be read: {manifestPath}");
        var filamentList = manifest["filament_list"]?.AsArray()
            ?? throw new InvalidDataException($"Bambu profile manifest has no filament_list: {manifestPath}");
        var proposedProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in selectedProfiles)
        {
            var relativePath = BuildProfileRelativePath(profile);
            proposedProfiles[NormalizeRelativePath(relativePath)] = BuildProfileJson(package, profile, nameMap);
        }

        var addedEntries = new List<string>();
        var updatedEntries = new List<string>();
        var replacedBaseNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in selectedProfiles)
        {
            var relativePath = BuildProfileRelativePath(profile);
            var matchingEntries = filamentList
                .OfType<JsonObject>()
                .Where(item =>
                    string.Equals(GetManifestString(item, "name"), profile.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        NormalizeRelativePath(GetManifestString(item, "sub_path")),
                        NormalizeRelativePath(relativePath),
                        StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matchingEntries.Count == 0)
            {
                filamentList.Add(new JsonObject
                {
                    ["name"] = profile.Name,
                    ["sub_path"] = relativePath
                });
                addedEntries.Add(profile.Name);
                continue;
            }

            if (profile.Name.EndsWith("@base", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var existingName in matchingEntries
                    .Select(item => GetManifestString(item, "name"))
                    .Where(name => name.EndsWith("@base", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    replacedBaseNames[existingName] = profile.Name;
                }
            }

            var keeper = matchingEntries.FirstOrDefault(item =>
                    string.Equals(GetManifestString(item, "name"), profile.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        NormalizeRelativePath(GetManifestString(item, "sub_path")),
                        NormalizeRelativePath(relativePath),
                        StringComparison.OrdinalIgnoreCase))
                ?? matchingEntries.FirstOrDefault(item => string.Equals(
                    NormalizeRelativePath(GetManifestString(item, "sub_path")),
                    NormalizeRelativePath(relativePath),
                    StringComparison.OrdinalIgnoreCase))
                ?? matchingEntries[0];

            var changed = !string.Equals(GetManifestString(keeper, "name"), profile.Name, StringComparison.Ordinal)
                || !string.Equals(GetManifestString(keeper, "sub_path"), relativePath, StringComparison.Ordinal);
            keeper["name"] = profile.Name;
            keeper["sub_path"] = relativePath;

            foreach (var duplicate in matchingEntries.Where(item => !ReferenceEquals(item, keeper)))
            {
                filamentList.Remove(duplicate);
                changed = true;
            }

            if (changed)
            {
                updatedEntries.Add(profile.Name);
            }
        }

        MigrateDependentInheritance(profileRoot, filamentList, proposedProfiles, replacedBaseNames, updatedEntries);
        ValidateManifestEntries(filamentList, manifestPath);
        BambuCatalogIntegrity.ValidateProfileRoot(profileRoot, manifest, proposedProfiles);

        var profileWrites = proposedProfiles.ToDictionary(
            pair => Path.Combine(profileRoot, "BBL", pair.Key.Replace('/', Path.DirectorySeparatorChar)),
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        var originalFiles = profileWrites.Keys
            .Append(manifestPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                path => path,
                path => File.Exists(path) ? File.ReadAllBytes(path) : null,
                StringComparer.OrdinalIgnoreCase);

        try
        {
            Backup(manifestPath, result);
            foreach (var destination in profileWrites.Keys.Where(File.Exists))
            {
                Backup(destination, result);
            }

            foreach (var (destination, json) in profileWrites)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllText(destination, json);
            }

            File.WriteAllText(manifestPath, manifest.ToJsonString(WriteOptions) + Environment.NewLine);
            BambuCatalogIntegrity.ValidateProfileRoot(profileRoot);
        }
        catch (Exception writeError)
        {
            try
            {
                RestoreOriginalFiles(originalFiles);
            }
            catch (Exception restoreError)
            {
                throw new AggregateException(
                    "The Bambu catalog update failed and its automatic rollback also failed. Use the .bflib-backup files beside the affected profiles to restore the catalog.",
                    writeError,
                    restoreError);
            }

            throw new IOException(
                "The Bambu catalog update failed. All files changed by this import were restored automatically.",
                writeError);
        }

        result.WrittenFiles.AddRange(profileWrites.Keys);
        result.ManifestEntriesAdded.AddRange(addedEntries);
        result.ManifestEntriesUpdated.AddRange(updatedEntries);
    }

    private static void MigrateDependentInheritance(
        string profileRoot,
        JsonArray filamentList,
        Dictionary<string, string> proposedProfiles,
        IReadOnlyDictionary<string, string> replacedBaseNames,
        List<string> updatedEntries)
    {
        if (replacedBaseNames.Count == 0)
        {
            return;
        }

        foreach (var item in filamentList.OfType<JsonObject>())
        {
            var relativePath = NormalizeRelativePath(GetManifestString(item, "sub_path"));
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            string json;
            if (proposedProfiles.TryGetValue(relativePath, out var proposedJson))
            {
                json = proposedJson;
            }
            else
            {
                var profilePath = Path.Combine(profileRoot, "BBL", relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(profilePath))
                {
                    continue;
                }

                json = File.ReadAllText(profilePath);
            }

            var profile = JsonNode.Parse(json)?.AsObject();
            var inherits = profile?["inherits"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(inherits)
                || !replacedBaseNames.TryGetValue(inherits, out var replacement))
            {
                continue;
            }

            profile!["inherits"] = replacement;
            var oldProductName = CurrentFilamentEntry.GetProductName(inherits);
            var newProductName = CurrentFilamentEntry.GetProductName(replacement);
            var profileName = profile["name"]?.GetValue<string>() ?? GetManifestString(item, "name");
            if (CurrentFilamentEntry.GetProductName(profileName).Equals(oldProductName, StringComparison.OrdinalIgnoreCase))
            {
                var suffix = profileName[oldProductName.Length..];
                var replacementName = newProductName + suffix;
                profile["name"] = replacementName;
                item["name"] = replacementName;
                if (!updatedEntries.Contains(replacementName, StringComparer.OrdinalIgnoreCase))
                {
                    updatedEntries.Add(replacementName);
                }
            }

            proposedProfiles[relativePath] = profile.ToJsonString(WriteOptions) + Environment.NewLine;
        }
    }

    private static void RestoreOriginalFiles(IReadOnlyDictionary<string, byte[]?> originalFiles)
    {
        foreach (var (path, contents) in originalFiles)
        {
            if (contents is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, contents);
        }
    }

    private static string GetManifestString(JsonObject item, string propertyName)
    {
        return item[propertyName]?.GetValue<string>() ?? "";
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static void ValidateManifestEntries(JsonArray filamentList, string manifestPath)
    {
        var entries = filamentList.OfType<JsonObject>().ToList();
        var duplicateName = entries
            .GroupBy(item => GetManifestString(item, "name").Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1);
        if (duplicateName is not null)
        {
            throw new InvalidDataException($"Catalog would contain duplicate profile name '{duplicateName.Key}' in {manifestPath}.");
        }

        var duplicatePath = entries
            .GroupBy(item => NormalizeRelativePath(GetManifestString(item, "sub_path")), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1);
        if (duplicatePath is not null)
        {
            throw new InvalidDataException($"Catalog would point more than one profile at '{duplicatePath.Key}' in {manifestPath}.");
        }
    }

    private void UpdateProjectFilamentList(LoadedFilamentPackage package, InstallResult result)
    {
        if (!File.Exists(_paths.ConfigPath))
        {
            throw new FileNotFoundException("BambuStudio.conf was not found.", _paths.ConfigPath);
        }

        Backup(_paths.ConfigPath, result);
        var text = File.ReadAllText(_paths.ConfigPath);
        var jsonText = text.Split("\n# MD5 checksum ", StringSplitOptions.None)[0].TrimEnd();
        var config = JsonNode.Parse(jsonText)!.AsObject();
        var filaments = config["filaments"] as JsonArray ?? [];
        config["filaments"] = filaments;

        var existing = filaments
            .Select(item => item?.GetValue<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selectedNames = package.Manifest.Profiles
            .Where(profile => profile.IsSelected && !profile.Name.EndsWith("@base", StringComparison.OrdinalIgnoreCase))
            .Select(profile => profile.Name)
            .Order(StringComparer.OrdinalIgnoreCase);

        foreach (var name in selectedNames)
        {
            if (existing.Contains(name))
            {
                continue;
            }

            filaments.Add(name);
            existing.Add(name);
            result.ProjectPresetEntriesAdded.Add(name);
        }

        File.WriteAllText(_paths.ConfigPath, config.ToJsonString(WriteOptions) + Environment.NewLine);
    }

    private void InstallUserPresets(LoadedFilamentPackage package, InstallResult result)
    {
        Directory.CreateDirectory(_paths.ActiveUserFilamentFolder);
        var selectedProfiles = package.Manifest.Profiles
            .Where(profile => profile.IsSelected && !profile.Name.EndsWith("@base", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var profilesByOriginalName = package.Manifest.Profiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.OriginalName))
            .GroupBy(profile => profile.OriginalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var profile in selectedProfiles)
        {
            var destination = Path.Combine(_paths.ActiveUserFilamentFolder, profile.Name + ".json");
            if (File.Exists(destination))
            {
                Backup(destination, result);
            }

            File.WriteAllText(destination, BuildUserProfileJson(package, profile, profilesByOriginalName));
            result.WrittenFiles.Add(destination);
            result.UserPresetEntriesWritten.Add(profile.Name);
        }
    }

    private static void Backup(string path, InstallResult result)
    {
        var backupPath = FileBackup.Create(path, "bflib-backup");
        if (backupPath is not null)
        {
            result.Backups.Add(backupPath);
        }
    }

    private static string BuildProfileRelativePath(FilamentProfileEntry profile)
    {
        var path = string.IsNullOrWhiteSpace(profile.RelativePath)
            ? profile.OriginalRelativePath
            : profile.RelativePath;
        return NormalizeRelativePath(path);
    }

    private static string BuildProfileJson(LoadedFilamentPackage package, FilamentProfileEntry profile, Dictionary<string, string> nameMap)
    {
        var sourcePath = string.IsNullOrWhiteSpace(profile.OriginalRelativePath)
            ? profile.RelativePath
            : profile.OriginalRelativePath;
        var json = package.ProfileJsonByPath[sourcePath];
        var node = JsonNode.Parse(json)!.AsObject();
        var originalJsonName = node["name"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(originalJsonName) && nameMap.TryGetValue(originalJsonName, out var mappedName))
        {
            node["name"] = mappedName;
        }
        else
        {
            node["name"] = profile.Name;
        }

        var inherits = node["inherits"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(inherits) && nameMap.TryGetValue(inherits, out var mappedInherits))
        {
            node["inherits"] = mappedInherits;
        }

        if (profile.Name.EndsWith("@base", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(profile.VendorGroup))
        {
            node["filament_vendor"] = new JsonArray(profile.VendorGroup);
        }

        return node.ToJsonString(WriteOptions) + Environment.NewLine;
    }

    private static string BuildUserProfileJson(
        LoadedFilamentPackage package,
        FilamentProfileEntry selectedProfile,
        Dictionary<string, FilamentProfileEntry> profilesByOriginalName)
    {
        var chain = new List<JsonObject>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AppendPackageChain(package, selectedProfile, profilesByOriginalName, chain, visited);

        var merged = new JsonObject();
        foreach (var profile in chain)
        {
            foreach (var pair in profile)
            {
                if (pair.Value is not null && pair.Key is not "name" and not "inherits" and not "from"
                    and not "filament_id" and not "setting_id" and not "instantiation" and not "include"
                    and not "type" and not "filament_settings_id")
                {
                    merged[pair.Key] = pair.Value.DeepClone();
                }
            }
        }

        merged["name"] = selectedProfile.Name;
        merged["from"] = "User";
        merged["inherits"] = GetGenericParent(selectedProfile.MaterialFamily);
        merged["filament_settings_id"] = new JsonArray(selectedProfile.Name);
        merged["version"] = "2.8.0.0";
        return merged.ToJsonString(WriteOptions) + Environment.NewLine;
    }

    private static void AppendPackageChain(
        LoadedFilamentPackage package,
        FilamentProfileEntry profile,
        Dictionary<string, FilamentProfileEntry> profilesByOriginalName,
        List<JsonObject> chain,
        HashSet<string> visited)
    {
        if (!visited.Add(profile.OriginalName))
        {
            return;
        }

        var sourcePath = string.IsNullOrWhiteSpace(profile.OriginalRelativePath) ? profile.RelativePath : profile.OriginalRelativePath;
        var json = JsonNode.Parse(package.ProfileJsonByPath[sourcePath])!.AsObject();
        var inherits = json["inherits"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(inherits) && profilesByOriginalName.TryGetValue(inherits, out var parent))
        {
            AppendPackageChain(package, parent, profilesByOriginalName, chain, visited);
        }

        chain.Add(json);
    }

    private static string GetGenericParent(string materialFamily)
    {
        var family = string.IsNullOrWhiteSpace(materialFamily) ? "PLA" : materialFamily.Trim();
        return family.StartsWith("Generic ", StringComparison.OrdinalIgnoreCase) ? family : $"Generic {family}";
    }
}

public sealed class InstallResult
{
    public List<string> WrittenFiles { get; } = [];
    public List<string> ManifestEntriesAdded { get; } = [];
    public List<string> ManifestEntriesUpdated { get; } = [];
    public List<string> ProjectPresetEntriesAdded { get; } = [];
    public List<string> UserPresetEntriesWritten { get; } = [];
    public List<string> Backups { get; } = [];
}
