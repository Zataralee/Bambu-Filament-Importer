using System.IO;
using System.Text.Json.Nodes;

namespace BambuFilamentImporter.Services;

public static class BambuCatalogIntegrity
{
    public static void ValidateProfileRoot(string profileRoot)
    {
        var manifestPath = Path.Combine(profileRoot, "BBL.json");
        var bblRoot = Path.GetFullPath(Path.Combine(profileRoot, "BBL"));
        var filamentRoot = Path.Combine(bblRoot, "filament");
        if (!File.Exists(manifestPath) || !Directory.Exists(filamentRoot))
        {
            throw new InvalidDataException($"Bambu catalog is incomplete: {profileRoot}");
        }

        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
            ?? throw new InvalidDataException($"Bambu catalog manifest could not be read: {manifestPath}");
        var filamentList = manifest["filament_list"]?.AsArray()
            ?? throw new InvalidDataException($"Bambu catalog has no filament_list: {manifestPath}");

        ValidateUniqueValues(filamentList, "name", "profile name", manifestPath, NormalizeName);
        ValidateUniqueValues(filamentList, "sub_path", "profile path", manifestPath, NormalizePath);

        var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(filamentRoot, "*.json", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)))
        {
            var profile = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
                ?? throw new InvalidDataException($"Filament profile could not be read: {path}");
            var name = profile["name"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(name))
            {
                knownNames.Add(name);
            }
        }

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
            if (!profilePath.StartsWith(bblRootWithSeparator, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(profilePath))
            {
                throw new InvalidDataException($"Bambu catalog profile file is missing: {name} -> {relativePath}");
            }

            var profile = JsonNode.Parse(File.ReadAllText(profilePath))?.AsObject()
                ?? throw new InvalidDataException($"Filament profile could not be read: {profilePath}");
            var jsonName = profile["name"]?.GetValue<string>();
            if (!string.Equals(name, jsonName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Bambu catalog name mismatch: '{name}' does not match '{jsonName}' in {profilePath}");
            }

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
