using System.IO;
using System.IO.Compression;
using System.Text.Json;
using BambuFilamentImporter.Models;

namespace BambuFilamentImporter.Services;

public static class PackageReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static LoadedFilamentPackage Load(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("The package does not contain manifest.json.");

        using var manifestStream = manifestEntry.Open();
        var manifest = JsonSerializer.Deserialize<FilamentPackage>(manifestStream, JsonOptions)
            ?? throw new InvalidDataException("manifest.json could not be read.");

        if (!string.Equals(manifest.Format, "bambu-filament-library", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("This is not a Bambu filament library package.");
        }

        if (manifest.FormatVersion != 1)
        {
            throw new InvalidDataException($"Unsupported package format version {manifest.FormatVersion}.");
        }

        if (manifest.Profiles.Count == 0)
        {
            throw new InvalidDataException("The package does not contain any filament profiles.");
        }

        var duplicatePath = manifest.Profiles
            .GroupBy(profile => NormalizeZipPath(profile.RelativePath), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicatePath is not null)
        {
            throw new InvalidDataException(string.IsNullOrWhiteSpace(duplicatePath.Key)
                ? "A package profile has a blank path."
                : $"The package lists the same profile path more than once: {duplicatePath.Key}");
        }

        var duplicateName = manifest.Profiles
            .GroupBy(profile => profile.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicateName is not null)
        {
            throw new InvalidDataException(string.IsNullOrWhiteSpace(duplicateName.Key)
                ? "A package profile has a blank name."
                : $"The package lists the same profile name more than once: {duplicateName.Key}");
        }

        var profileJson = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inheritanceTargets = new List<(string ProfileName, string Target)>();
        foreach (var profile in manifest.Profiles)
        {
            profile.OriginalName = string.IsNullOrWhiteSpace(profile.OriginalName) ? profile.Name : profile.OriginalName;
            profile.OriginalRelativePath = string.IsNullOrWhiteSpace(profile.OriginalRelativePath) ? profile.RelativePath : profile.OriginalRelativePath;
            var entry = FindEntry(archive, profile.RelativePath)
                ?? throw new InvalidDataException($"Missing profile file: {profile.RelativePath}");

            using var reader = new StreamReader(entry.Open());
            var json = reader.ReadToEnd();
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Profile file is not a JSON object: {profile.RelativePath}");
            }

            if (!document.RootElement.TryGetProperty("name", out var nameElement)
                || nameElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nameElement.GetString()))
            {
                throw new InvalidDataException($"Profile file has no name: {profile.RelativePath}");
            }

            var jsonName = nameElement.GetString()!;
            if (!string.Equals(jsonName, profile.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Profile name mismatch in {profile.RelativePath}: manifest says '{profile.Name}', file says '{jsonName}'.");
            }

            if (document.RootElement.TryGetProperty("inherits", out var inheritsElement)
                && inheritsElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(inheritsElement.GetString()))
            {
                inheritanceTargets.Add((profile.Name, inheritsElement.GetString()!));
            }

            profileJson[profile.OriginalRelativePath] = json;
        }

        var packageNames = manifest.Profiles
            .Select(profile => profile.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (profileName, target) in inheritanceTargets)
        {
            if (IsManufacturerProfile(target, manifest.Manufacturer) && !packageNames.Contains(target))
            {
                throw new InvalidDataException(
                    $"Profile '{profileName}' inherits missing package profile '{target}'.");
            }
        }

        return new LoadedFilamentPackage
        {
            FilePath = filePath,
            Manifest = manifest,
            ProfileJsonByPath = profileJson
        };
    }

    private static bool IsManufacturerProfile(string profileName, string manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer))
        {
            return false;
        }

        return profileName.StartsWith(manufacturer.Trim() + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string path)
    {
        var normalized = NormalizeZipPath(path);
        return archive.GetEntry(normalized)
            ?? archive.GetEntry(normalized.Replace('/', '\\'))
            ?? archive.Entries.FirstOrDefault(entry =>
                string.Equals(NormalizeZipPath(entry.FullName), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeZipPath(string path) => path.Replace('\\', '/').TrimStart('/');
}
