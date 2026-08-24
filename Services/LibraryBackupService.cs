using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BambuFilamentImporter.Models;

namespace BambuFilamentImporter.Services;

public sealed class LibraryBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly BambuPaths _paths;
    private readonly Func<bool> _isStudioRunning;

    public LibraryBackupService(BambuPaths paths, Func<bool>? isStudioRunning = null)
    {
        _paths = paths;
        _isStudioRunning = isStudioRunning ?? BambuProcess.IsStudioRunning;
    }

    public LibraryBackupResult Create(string outputPath)
    {
        EnsureStudioClosed("backing up the library");
        var library = new BambuLibraryScanner(_paths).LoadCurrentFilaments();
        var manifest = new LibraryBackupManifest
        {
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            SourcePresetFolder = Path.GetFileName(_paths.ActiveUserPresetFolder),
            CatalogEntries = library
                .Where(entry => entry.StorageKind == FilamentStorageKind.SystemCatalog)
                .Select(entry => new LibraryBackupCatalogEntry
                {
                    Name = entry.Name,
                    RelativePath = NormalizeRelativePath(entry.RelativePath),
                    VendorGroup = entry.VendorGroup,
                    MaterialFamily = entry.MaterialFamily,
                    WasInProjectLibrary = entry.IsProjectPreset
                })
                .DistinctBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
        LoadConfigState(manifest);

        var catalogFiles = CollectCatalogFiles();
        var userFiles = CollectUserFiles();
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);
        foreach (var file in catalogFiles.Values.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            AddFile(archive, file.SourcePath, "catalog/BBL/" + file.RelativePath, "catalog", file.RelativePath, manifest);
        }

        foreach (var file in userFiles.OrderBy(file => file.ArchivePath, StringComparer.OrdinalIgnoreCase))
        {
            AddFile(archive, file.SourcePath, file.ArchivePath, file.Kind, file.RelativePath, manifest);
        }

        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(JsonSerializer.Serialize(manifest, JsonOptions));
        }

        return new LibraryBackupResult
        {
            FilePath = outputPath,
            CatalogProfiles = manifest.CatalogEntries.Count,
            CatalogFiles = manifest.Files.Count(file => file.Kind == "catalog"),
            UserPresetFiles = manifest.Files.Count(file => file.Kind.StartsWith("user-", StringComparison.Ordinal)),
            ProjectPresetNames = manifest.ProjectPresetNames.Count
        };
    }

    public LibraryBackupSummary Inspect(string backupPath)
    {
        using var archive = ZipFile.OpenRead(backupPath);
        var manifest = ReadManifest(archive);
        ValidateArchive(archive, manifest, verifyHashes: false);
        return new LibraryBackupSummary
        {
            CreatedUtc = manifest.CreatedUtc,
            CatalogProfiles = manifest.CatalogEntries.Count,
            CatalogFiles = manifest.Files.Count(file => file.Kind == "catalog"),
            UserPresetFiles = manifest.Files.Count(file => file.Kind.StartsWith("user-", StringComparison.Ordinal)),
            ProjectPresetNames = manifest.ProjectPresetNames.Count
        };
    }

    public LibraryRestoreResult Restore(string backupPath)
    {
        EnsureStudioClosed("restoring the library");
        using var archive = ZipFile.OpenRead(backupPath);
        var manifest = ReadManifest(archive);
        ValidateArchive(archive, manifest, verifyHashes: true);
        var result = new LibraryRestoreResult();

        foreach (var file in manifest.Files)
        {
            var destination = file.Kind switch
            {
                "catalog" => ResolveDestination(Path.Combine(_paths.RoamingProfileRoot, "BBL"), file.RelativePath),
                "user-active" => ResolveDestination(_paths.ActiveUserFilamentFolder, file.RelativePath),
                "user-default" => ResolveDestination(Path.Combine(_paths.UserRoot, "default", "filament"), file.RelativePath),
                _ => throw new InvalidDataException($"Unsupported backup file kind: {file.Kind}")
            };

            var archiveEntry = FindEntry(archive, file.ArchivePath)
                ?? throw new InvalidDataException($"Backup payload is missing: {file.ArchivePath}");
            using var source = archiveEntry.Open();
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            var bytes = memory.ToArray();

            if (File.Exists(destination) && File.ReadAllBytes(destination).AsSpan().SequenceEqual(bytes))
            {
                result.UnchangedFiles++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var backup = FileBackup.Create(destination, "bflbackup-restore-backup");
            if (backup is not null)
            {
                result.SafetyBackups++;
                result.UpdatedFiles++;
            }
            else
            {
                result.AddedFiles++;
            }

            File.WriteAllBytes(destination, bytes);
        }

        MergeCatalogManifest(manifest, result);
        MergeConfig(manifest, result);
        return result;
    }

    private Dictionary<string, BackupSourceFile> CollectCatalogFiles()
    {
        var files = new Dictionary<string, BackupSourceFile>(StringComparer.OrdinalIgnoreCase);
        AddCatalogRoot(_paths.ProgramProfileRoot, files);
        AddCatalogRoot(_paths.RoamingProfileRoot, files);
        return files;
    }

    private static void AddCatalogRoot(string profileRoot, Dictionary<string, BackupSourceFile> files)
    {
        var filamentRoot = Path.Combine(profileRoot, "BBL", "filament");
        if (!Directory.Exists(filamentRoot))
        {
            return;
        }

        var bblRoot = Path.Combine(profileRoot, "BBL");
        foreach (var path in Directory.EnumerateFiles(filamentRoot, "*.json", SearchOption.AllDirectories))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(bblRoot, path));
            files[relativePath] = new BackupSourceFile(path, relativePath, "", "catalog");
        }
    }

    private List<BackupSourceFile> CollectUserFiles()
    {
        var files = new List<BackupSourceFile>();
        AddUserFolder(_paths.ActiveUserFilamentFolder, "user/active", "user-active", files);
        var defaultFolder = Path.Combine(_paths.UserRoot, "default", "filament");
        if (!defaultFolder.Equals(_paths.ActiveUserFilamentFolder, StringComparison.OrdinalIgnoreCase))
        {
            AddUserFolder(defaultFolder, "user/default", "user-default", files);
        }

        return files;
    }

    private static void AddUserFolder(string folder, string archivePrefix, string kind, List<BackupSourceFile> files)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".info", StringComparison.OrdinalIgnoreCase)))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(folder, path));
            files.Add(new BackupSourceFile(path, relativePath, archivePrefix + "/" + relativePath, kind));
        }
    }

    private static void AddFile(
        ZipArchive archive,
        string sourcePath,
        string archivePath,
        string kind,
        string relativePath,
        LibraryBackupManifest manifest)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        var entry = archive.CreateEntry(archivePath, CompressionLevel.Optimal);
        using (var destination = entry.Open())
        {
            destination.Write(bytes);
        }

        manifest.Files.Add(new LibraryBackupFileEntry
        {
            ArchivePath = archivePath,
            RelativePath = relativePath,
            Kind = kind,
            Length = bytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
        });
    }

    private void LoadConfigState(LibraryBackupManifest manifest)
    {
        if (!File.Exists(_paths.ConfigPath))
        {
            return;
        }

        try
        {
            var config = LoadJsonWithoutChecksum(_paths.ConfigPath);
            manifest.ProjectPresetNames = ReadStringArray(config["filaments"]?.AsArray());
            manifest.ActiveFilamentSelections = ReadStringArray(config["presets"]?["filaments"]?.AsArray());
            var recent = config["app"]?["ams_recent_filament_presets"]?.GetValue<string>() ?? "";
            manifest.AmsRecentPresetNames = recent.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        catch
        {
            // Profile files still make a useful backup if an unrelated config field is malformed.
        }
    }

    private void MergeCatalogManifest(LibraryBackupManifest backup, LibraryRestoreResult result)
    {
        var manifestPath = Path.Combine(_paths.RoamingProfileRoot, "BBL.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var manifest = File.Exists(manifestPath)
            ? JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject()
            : new JsonObject();
        var filamentList = manifest["filament_list"] as JsonArray ?? [];
        manifest["filament_list"] = filamentList;
        var byName = filamentList
            .Where(item => item?["name"] is not null)
            .GroupBy(item => item!["name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First()!, StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var entry in backup.CatalogEntries)
        {
            if (byName.TryGetValue(entry.Name, out var existing))
            {
                var currentPath = existing["sub_path"]?.GetValue<string>() ?? "";
                if (!currentPath.Equals(entry.RelativePath, StringComparison.OrdinalIgnoreCase))
                {
                    existing["sub_path"] = entry.RelativePath;
                    changed = true;
                    result.ManifestEntriesUpdated++;
                }
            }
            else
            {
                var item = new JsonObject { ["name"] = entry.Name, ["sub_path"] = entry.RelativePath };
                filamentList.Add(item);
                byName[entry.Name] = item;
                changed = true;
                result.ManifestEntriesAdded++;
            }
        }

        if (changed || !File.Exists(manifestPath))
        {
            if (FileBackup.Create(manifestPath, "bflbackup-restore-backup") is not null)
            {
                result.SafetyBackups++;
            }
            File.WriteAllText(manifestPath, manifest.ToJsonString(JsonOptions) + Environment.NewLine);
        }
    }

    private void MergeConfig(LibraryBackupManifest backup, LibraryRestoreResult result)
    {
        if (!File.Exists(_paths.ConfigPath))
        {
            return;
        }

        var config = LoadJsonWithoutChecksum(_paths.ConfigPath);
        var changed = MergeNames(config, "filaments", backup.ProjectPresetNames, result);
        var presets = config["presets"] as JsonObject ?? new JsonObject();
        config["presets"] = presets;
        changed |= MergeNames(presets, "filaments", backup.ActiveFilamentSelections, result);

        var app = config["app"] as JsonObject ?? new JsonObject();
        config["app"] = app;
        var currentRecent = (app["ams_recent_filament_presets"]?.GetValue<string>() ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var mergedRecent = currentRecent
            .Concat(backup.AmsRecentPresetNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var mergedRecentText = string.Join('\n', mergedRecent);
        if (!string.Equals(app["ams_recent_filament_presets"]?.GetValue<string>() ?? "", mergedRecentText, StringComparison.Ordinal))
        {
            app["ams_recent_filament_presets"] = mergedRecentText;
            changed = true;
        }

        if (changed)
        {
            if (FileBackup.Create(_paths.ConfigPath, "bflbackup-restore-backup") is not null)
            {
                result.SafetyBackups++;
            }
            File.WriteAllText(_paths.ConfigPath, config.ToJsonString(JsonOptions) + Environment.NewLine);
        }
    }

    private static bool MergeNames(JsonObject parent, string propertyName, IEnumerable<string> incoming, LibraryRestoreResult result)
    {
        var array = parent[propertyName] as JsonArray ?? [];
        parent[propertyName] = array;
        var existing = array
            .Select(item => item?.GetValue<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var name in incoming.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            if (existing.Add(name))
            {
                array.Add(name);
                changed = true;
                result.ProjectNamesAdded++;
            }
        }

        return changed;
    }

    private static LibraryBackupManifest ReadManifest(ZipArchive archive)
    {
        var entry = FindEntry(archive, "manifest.json")
            ?? throw new InvalidDataException("The backup does not contain manifest.json.");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<LibraryBackupManifest>(stream, JsonOptions)
            ?? throw new InvalidDataException("The backup manifest could not be read.");
        if (!manifest.Format.Equals("bambu-filament-library-backup", StringComparison.OrdinalIgnoreCase)
            || manifest.FormatVersion != 1)
        {
            throw new InvalidDataException("This is not a supported Bambu Filament Importer library backup.");
        }

        return manifest;
    }

    private static void ValidateArchive(ZipArchive archive, LibraryBackupManifest manifest, bool verifyHashes)
    {
        foreach (var file in manifest.Files)
        {
            ValidateRelativePath(file.RelativePath);
            var entry = FindEntry(archive, file.ArchivePath)
                ?? throw new InvalidDataException($"Backup payload is missing: {file.ArchivePath}");
            if (entry.Length != file.Length)
            {
                throw new InvalidDataException($"Backup payload length does not match: {file.ArchivePath}");
            }

            if (!verifyHashes)
            {
                continue;
            }

            using var stream = entry.Open();
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Backup checksum failed: {file.ArchivePath}");
            }
        }
    }

    private static JsonObject LoadJsonWithoutChecksum(string path)
    {
        var text = File.ReadAllText(path);
        var jsonText = text.Split("\n# MD5 checksum ", StringSplitOptions.None)[0].TrimEnd();
        return JsonNode.Parse(jsonText)!.AsObject();
    }

    private static List<string> ReadStringArray(JsonArray? array) => array?
        .Select(item => item?.GetValue<string>())
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => name!)
        .ToList() ?? [];

    private static string ResolveDestination(string root, string relativePath)
    {
        ValidateRelativePath(relativePath);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The backup contains a path outside the Bambu filament library.");
        }

        return destination;
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Split('/', '\\').Any(part => part is ".." or "." or ""))
        {
            throw new InvalidDataException($"Unsafe backup path: {path}");
        }
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string path)
    {
        var normalized = NormalizeRelativePath(path);
        return archive.Entries.FirstOrDefault(entry =>
            NormalizeRelativePath(entry.FullName).Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureStudioClosed(string operation)
    {
        if (_isStudioRunning())
        {
            throw new InvalidOperationException($"Close Bambu Studio before {operation}.");
        }
    }

    private sealed record BackupSourceFile(string SourcePath, string RelativePath, string ArchivePath, string Kind);
}

public sealed class LibraryBackupManifest
{
    public string Format { get; set; } = "bambu-filament-library-backup";
    public int FormatVersion { get; set; } = 1;
    public string CreatedUtc { get; set; } = "";
    public string SourcePresetFolder { get; set; } = "";
    public List<LibraryBackupCatalogEntry> CatalogEntries { get; set; } = [];
    public List<LibraryBackupFileEntry> Files { get; set; } = [];
    public List<string> ProjectPresetNames { get; set; } = [];
    public List<string> ActiveFilamentSelections { get; set; } = [];
    public List<string> AmsRecentPresetNames { get; set; } = [];
}

public sealed class LibraryBackupCatalogEntry
{
    public string Name { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string VendorGroup { get; set; } = "";
    public string MaterialFamily { get; set; } = "";
    public bool WasInProjectLibrary { get; set; }
}

public sealed class LibraryBackupFileEntry
{
    public string ArchivePath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Kind { get; set; } = "";
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class LibraryBackupSummary
{
    public string CreatedUtc { get; set; } = "";
    public int CatalogProfiles { get; set; }
    public int CatalogFiles { get; set; }
    public int UserPresetFiles { get; set; }
    public int ProjectPresetNames { get; set; }
}

public sealed class LibraryBackupResult
{
    public string FilePath { get; set; } = "";
    public int CatalogProfiles { get; set; }
    public int CatalogFiles { get; set; }
    public int UserPresetFiles { get; set; }
    public int ProjectPresetNames { get; set; }
}

public sealed class LibraryRestoreResult
{
    public int AddedFiles { get; set; }
    public int UpdatedFiles { get; set; }
    public int UnchangedFiles { get; set; }
    public int SafetyBackups { get; set; }
    public int ManifestEntriesAdded { get; set; }
    public int ManifestEntriesUpdated { get; set; }
    public int ProjectNamesAdded { get; set; }
}
