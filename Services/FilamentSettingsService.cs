using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using BambuFilamentImporter.Models;

namespace BambuFilamentImporter.Services;

public sealed class FilamentSettingsService
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> ProtectedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "name", "inherits", "from", "filament_id", "setting_id", "filament_settings_id",
        "instantiation", "compatible_printers", "compatible_prints", "include", "version"
    };

    private readonly BambuPaths _paths;
    private Dictionary<string, string>? _profilePathByName;

    public FilamentSettingsService(BambuPaths paths)
    {
        _paths = paths;
    }

    public List<ProfileSettingEntry> Load(CurrentFilamentEntry entry)
    {
        if (!File.Exists(entry.ProfilePath))
        {
            throw new FileNotFoundException("The selected profile file could not be found.", entry.ProfilePath);
        }

        var chain = new List<ProfileDocument>();
        LoadChain(entry.ProfilePath, chain, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var effective = new Dictionary<string, EffectiveValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in chain)
        {
            foreach (var pair in document.Json)
            {
                if (pair.Value is not null)
                {
                    effective[pair.Key] = new EffectiveValue(pair.Value.DeepClone(), document.Name, document.Path);
                }
            }
        }

        return effective
            .Select(pair => CreateSetting(pair.Key, pair.Value, entry))
            .OrderBy(setting => CategoryOrder(setting.Category))
            .ThenBy(setting => setting.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public int Save(CurrentFilamentEntry entry, IEnumerable<ProfileSettingEntry> settings)
    {
        if (!entry.CanEdit)
        {
            throw new InvalidOperationException("This profile is read-only. Copy it to the roaming or user library before changing settings.");
        }

        var modified = settings.Where(setting => setting.IsEditable && setting.IsModified).ToList();
        if (modified.Count == 0)
        {
            return 0;
        }

        var profile = JsonNode.Parse(File.ReadAllText(entry.ProfilePath))!.AsObject();
        foreach (var setting in modified)
        {
            profile[setting.Key] = ParseValue(setting);
        }

        FileBackup.Create(entry.ProfilePath, "bflib-settings-backup");
        File.WriteAllText(entry.ProfilePath, profile.ToJsonString(WriteOptions) + Environment.NewLine);
        return modified.Count;
    }

    public void InvalidateIndex() => _profilePathByName = null;

    private void LoadChain(string path, List<ProfileDocument> chain, HashSet<string> visited)
    {
        var fullPath = Path.GetFullPath(path);
        if (!visited.Add(fullPath) || !File.Exists(fullPath))
        {
            return;
        }

        var json = JsonNode.Parse(File.ReadAllText(fullPath))!.AsObject();
        var inherits = json["inherits"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(inherits))
        {
            var inheritedPath = ResolveInheritedPath(fullPath, inherits);
            if (inheritedPath is not null)
            {
                LoadChain(inheritedPath, chain, visited);
            }
        }

        chain.Add(new ProfileDocument(
            json["name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(fullPath),
            fullPath,
            json));
    }

    private string? ResolveInheritedPath(string childPath, string inheritedName)
    {
        var sibling = Path.Combine(Path.GetDirectoryName(childPath)!, inheritedName + ".json");
        if (File.Exists(sibling))
        {
            return sibling;
        }

        EnsureProfileIndex();
        return _profilePathByName!.GetValueOrDefault(inheritedName);
    }

    private void EnsureProfileIndex()
    {
        if (_profilePathByName is not null)
        {
            return;
        }

        _profilePathByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IndexSystemRoot(_paths.ProgramProfileRoot);
        IndexSystemRoot(_paths.RoamingProfileRoot);
        IndexUserFolder(Path.Combine(_paths.UserRoot, "default", "filament"));
        IndexUserFolder(_paths.ActiveUserFilamentFolder);
    }

    private void IndexSystemRoot(string root)
    {
        var folder = Path.Combine(root, "BBL", "filament");
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories))
        {
            TryIndex(path);
        }
    }

    private void IndexUserFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories))
        {
            TryIndex(path);
        }
    }

    private void TryIndex(string path)
    {
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var name = json["name"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(name))
            {
                _profilePathByName![name] = path;
            }
        }
        catch
        {
            // One malformed third-party preset should not hide the rest of the library.
        }
    }

    private static ProfileSettingEntry CreateSetting(string key, EffectiveValue effective, CurrentFilamentEntry entry)
    {
        var (value, format, canRepresent) = FormatValue(effective.Value);
        var editable = entry.CanEdit && canRepresent && !ProtectedKeys.Contains(key);
        return new ProfileSettingEntry
        {
            Key = key,
            DisplayName = FriendlyName(key),
            Category = GetCategory(key),
            SourceProfile = effective.SourceProfile,
            ValueFormat = format,
            OriginalJson = effective.Value.ToJsonString(),
            IsDirect = string.Equals(effective.SourcePath, entry.ProfilePath, StringComparison.OrdinalIgnoreCase),
            IsEditable = editable,
            OriginalValue = value,
            Value = value
        };
    }

    private static (string Value, string Format, bool Editable) FormatValue(JsonNode value)
    {
        if (value is JsonArray array)
        {
            var values = new List<string>();
            foreach (var item in array)
            {
                if (item is null)
                {
                    values.Add("nil");
                }
                else if (item is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var stringValue))
                {
                    values.Add(stringValue);
                }
                else
                {
                    return (value.ToJsonString(), "json", false);
                }
            }

            if (array.Count > 1 && values.Any(value => value.Contains(',') || value.Contains('\n') || value.Contains('\r')))
            {
                return (value.ToJsonString(), "json", false);
            }

            return (array.Count <= 1 ? values.FirstOrDefault() ?? "" : string.Join(", ", values),
                array.Count <= 1 ? "array-single" : "array-multi", true);
        }

        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<string>(out var text)) return (text, "string", true);
            if (scalar.TryGetValue<bool>(out var boolean)) return (boolean ? "true" : "false", "boolean", true);
            if (scalar.TryGetValue<long>(out var integer)) return (integer.ToString(CultureInfo.InvariantCulture), "integer", true);
            if (scalar.TryGetValue<double>(out var number)) return (number.ToString(CultureInfo.InvariantCulture), "number", true);
        }

        return (value.ToJsonString(), "json", false);
    }

    private static JsonNode? ParseValue(ProfileSettingEntry setting)
    {
        return setting.ValueFormat switch
        {
            "array-single" => new JsonArray(setting.Value),
            "array-multi" => new JsonArray(setting.Value.Split(',', StringSplitOptions.TrimEntries).Select(value => (JsonNode?)value).ToArray()),
            "string" => JsonValue.Create(setting.Value),
            "boolean" when bool.TryParse(setting.Value, out var boolean) => JsonValue.Create(boolean),
            "integer" when long.TryParse(setting.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => JsonValue.Create(integer),
            "number" when double.TryParse(setting.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => JsonValue.Create(number),
            _ => throw new InvalidDataException($"{setting.DisplayName} has an invalid {setting.ValueFormat} value: {setting.Value}")
        };
    }

    private static string GetCategory(string key)
    {
        if (key.Contains("temp", StringComparison.OrdinalIgnoreCase) || key.Contains("chamber", StringComparison.OrdinalIgnoreCase)) return "Temperatures";
        if (key.Contains("flow", StringComparison.OrdinalIgnoreCase) || key.Contains("volumetric", StringComparison.OrdinalIgnoreCase) || key.Contains("pressure", StringComparison.OrdinalIgnoreCase)) return "Flow & calibration";
        if (key.Contains("fan", StringComparison.OrdinalIgnoreCase) || key.Contains("cool", StringComparison.OrdinalIgnoreCase) || key.Contains("slow_down", StringComparison.OrdinalIgnoreCase)) return "Cooling";
        if (key.Contains("retract", StringComparison.OrdinalIgnoreCase) || key.Contains("ramming", StringComparison.OrdinalIgnoreCase)) return "Retraction & material change";
        if (key.Contains("dry", StringComparison.OrdinalIgnoreCase) || key.Contains("softening", StringComparison.OrdinalIgnoreCase)) return "Drying";
        if (ProtectedKeys.Contains(key) || key is "filament_vendor" or "filament_type" or "description") return "Identity & compatibility";
        return "Other settings";
    }

    private static int CategoryOrder(string category) => category switch
    {
        "Temperatures" => 0,
        "Flow & calibration" => 1,
        "Cooling" => 2,
        "Retraction & material change" => 3,
        "Drying" => 4,
        "Other settings" => 5,
        _ => 6
    };

    private static string FriendlyName(string key)
    {
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ams"] = "AMS", ["hrc"] = "HRC", ["id"] = "ID", ["temp"] = "Temperature",
            ["max"] = "Maximum", ["min"] = "Minimum", ["dev"] = "Device", ["ec"] = "Extruder cutter",
            ["nc"] = "Nozzle cutter", ["eng"] = "Engineering", ["x"] = "X"
        };
        return string.Join(" ", key.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => replacements.TryGetValue(word, out var replacement)
                ? replacement
                : char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private sealed record ProfileDocument(string Name, string Path, JsonObject Json);
    private sealed record EffectiveValue(JsonNode Value, string SourceProfile, string SourcePath);
}
