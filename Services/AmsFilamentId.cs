using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BambuFilamentImporter.Models;

namespace BambuFilamentImporter.Services;

public static class AmsFilamentId
{
    public const int MaximumLength = 8;
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string Assign(string? currentId, string stableKey, ISet<string> usedIds)
    {
        var current = currentId?.Trim() ?? "";
        if (current.Length > 0 && current.Length <= MaximumLength && usedIds.Add(current))
        {
            return current;
        }

        if (current.Length > MaximumLength)
        {
            var truncated = current[..MaximumLength];
            if (usedIds.Add(truncated))
            {
                return truncated;
            }
        }

        for (var salt = 0; ; salt++)
        {
            var input = salt == 0 ? stableKey : $"{stableKey}|{salt}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            var candidate = "V" + Convert.ToHexString(hash)[..7];
            if (usedIds.Add(candidate))
            {
                return candidate;
            }
        }
    }

    public static string? Read(JsonObject profile)
    {
        if (profile["filament_id"] is JsonValue value && value.TryGetValue<string>(out var direct))
        {
            return direct;
        }

        if (profile["filament_id"] is JsonArray array
            && array.FirstOrDefault() is JsonValue first
            && first.TryGetValue<string>(out var arrayValue))
        {
            return arrayValue;
        }

        return null;
    }

    public static LoadedFilamentPackage NormalizePackage(LoadedFilamentPackage package)
    {
        var jsonByPath = package.ProfileJsonByPath.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in package.Manifest.Profiles
            .Where(profile => profile.IsSelected && profile.Name.EndsWith("@base", StringComparison.OrdinalIgnoreCase))
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase))
        {
            var sourcePath = string.IsNullOrWhiteSpace(profile.OriginalRelativePath)
                ? profile.RelativePath
                : profile.OriginalRelativePath;
            if (!jsonByPath.TryGetValue(sourcePath, out var json))
            {
                continue;
            }

            var node = JsonNode.Parse(json)?.AsObject()
                ?? throw new InvalidDataException($"Profile JSON could not be parsed: {profile.Name}");
            node["filament_id"] = Assign(
                Read(node),
                $"{package.Manifest.PackageId}|{profile.Name}",
                usedIds);
            jsonByPath[sourcePath] = node.ToJsonString(WriteOptions);
        }

        return new LoadedFilamentPackage
        {
            FilePath = package.FilePath,
            Manifest = package.Manifest,
            ProfileJsonByPath = jsonByPath
        };
    }
}
