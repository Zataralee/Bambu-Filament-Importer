using System.IO;
using System.Text.Json.Nodes;

namespace BambuFilamentImporter.Services;

public static class BambuCatalogIntegrity
{
    public static void ValidateProfileRoot(string profileRoot)
    {
        var manifestPath = Path.Combine(profileRoot, "BBL.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException($"Bambu catalog is incomplete: {profileRoot}");
        }

        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
            ?? throw new InvalidDataException($"Bambu catalog manifest could not be read: {manifestPath}");
        ValidateProfileRoot(profileRoot, manifest, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    internal static void ValidateProfileRoot(
        string profileRoot,
        JsonObject manifest,
        IReadOnlyDictionary<string, string> proposedProfiles)
    {
        var manifestPath = Path.Combine(profileRoot, "BBL.json");
        var bblRoot = Path.GetFullPath(Path.Combine(profileRoot, "BBL"));
        var filamentRoot = Path.Combine(bblRoot, "filament");
        if (!Directory.Exists(filamentRoot) && proposedProfiles.Count == 0)
        {
            throw new InvalidDataException($"Bambu catalog is incomplete: {profileRoot}");
        }

        var filamentList = manifest["filament_list"]?.AsArray()
            ?? throw new InvalidDataException($"Bambu catalog has no filament_list: {manifestPath}");

        ValidateUniqueValues(filamentList, "name", "profile name", manifestPath, NormalizeName);
        ValidateUniqueValues(filamentList, "sub_path", "profile path", manifestPath, NormalizePath);

        var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loadedProfiles = new List<(string Name, JsonObject Profile)>();
        var bblRootWithSeparator = bblRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var item in filamentList)
        {
            var name = item?["name"]?.GetValue<string>();
            var relativePath = item?["sub_path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(relativePath))
            {
                throw new InvalidDataException($"Bambu catalog contains a blank profile name or path: {manifestPath}");
            }

            var profilePath = Path.GetFullPath(Path.Combine(bblRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var normalizedPath = NormalizePath(relativePath);
            if (!profilePath.StartsWith(bblRootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Bambu catalog profile path leaves the BBL folder: {relativePath}");
            }

            string profileJson;
            if (proposedProfiles.TryGetValue(normalizedPath, out var proposedJson))
            {
                profileJson = proposedJson;
            }
            else if (File.Exists(profilePath))
            {
                profileJson = File.ReadAllText(profilePath);
            }
            else
            {
                throw new InvalidDataException($"Bambu catalog profile file is missing: {name} -> {relativePath}");
            }

            var profile = JsonNode.Parse(profileJson)?.AsObject()
                ?? throw new InvalidDataException($"Filament profile could not be read: {profilePath}");
            var jsonName = profile["name"]?.GetValue<string>();
            if (!string.Equals(name, jsonName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Bambu catalog name mismatch: '{name}' does not match '{jsonName}' in {profilePath}");
            }

            knownNames.Add(name);
            loadedProfiles.Add((name, profile));
        }

        foreach (var (name, profile) in loadedProfiles)
        {
            var inherits = profile["inherits"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(inherits) && !knownNames.Contains(inherits))
            {
                throw new InvalidDataException($"Bambu catalog profile '{name}' inherits missing profile '{inherits}'.");
            }
        }
    }

    private static void ValidateUniqueValues(
        JsonArray filamentList,
        string propertyName,
        string label,
        string manifestPath,
        Func<string, string> normalize)
    {
        var duplicate = filamentList
            .Select(item => item?[propertyName]?.GetValue<string>() ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(normalize, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Bambu catalog contains duplicate {label} '{duplicate.Key}': {manifestPath}");
        }
    }

    private static string NormalizeName(string value) => value.Trim();
    private static string NormalizePath(string value) => value.Replace('\\', '/').TrimStart('/');
}
