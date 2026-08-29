using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace BambuFilamentImporter.Services;

public sealed class ManufacturerLibraryUpdateService : IDisposable
{
    private const string DefaultIndexUrl =
        "https://raw.githubusercontent.com/Zataralee/Bambu-Filament-Importer/main/packages/manufacturers/index.json";
    private const string DefaultPackageBaseUrl =
        "https://raw.githubusercontent.com/Zataralee/Bambu-Filament-Importer/main/packages/manufacturers/";
    private const long MaximumIndexBytes = 1024 * 1024;
    private const long MaximumPackageBytes = 10L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Uri _indexUri;
    private readonly Uri _packageBaseUri;

    public ManufacturerLibraryUpdateService(
        HttpClient? client = null,
        string? indexUrl = null,
        string? packageBaseUrl = null)
    {
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _ownsClient = client is null;
        _indexUri = new Uri(indexUrl ?? DefaultIndexUrl, UriKind.Absolute);
        _packageBaseUri = new Uri(packageBaseUrl ?? DefaultPackageBaseUrl, UriKind.Absolute);
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
        {
            _client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("BambuFilamentImporter", UpdateService.CurrentVersion.ToString(3)));
        }
    }

    public async Task<ManufacturerLibraryUpdateCheck> CheckAsync(
        string libraryDirectory,
        CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync(_indexUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumIndexBytes)
        {
            throw new InvalidDataException("The manufacturer library index is unexpectedly large.");
        }

        var indexBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (indexBytes.LongLength > MaximumIndexBytes)
        {
            throw new InvalidDataException("The manufacturer library index is unexpectedly large.");
        }

        var index = JsonSerializer.Deserialize<ManufacturerLibraryIndex>(indexBytes, JsonOptions)
            ?? throw new InvalidDataException("The manufacturer library index could not be read.");
        ValidateIndex(index);

        var fullLibraryDirectory = Path.GetFullPath(libraryDirectory);
        var updates = new List<ManufacturerLibraryIndexEntry>();
        var currentCount = 0;
        foreach (var entry in index.Packages)
        {
            var localPath = Path.Combine(fullLibraryDirectory, entry.FileName);
            if (IsCurrentPackage(localPath, entry))
            {
                currentCount++;
            }
            else
            {
                updates.Add(entry);
            }
        }

        return new ManufacturerLibraryUpdateCheck(
            index.CatalogVersion,
            fullLibraryDirectory,
            currentCount,
            updates);
    }

    public async Task<ManufacturerLibraryUpdateResult> InstallAsync(
        ManufacturerLibraryUpdateCheck check,
        CancellationToken cancellationToken = default)
    {
        if (check.Updates.Count == 0)
        {
            return new ManufacturerLibraryUpdateResult(check.CatalogVersion, check.LibraryDirectory, []);
        }

        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BambuFilamentImporter",
            "LibraryUpdates",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateRoot);
        var stagedPackages = new Dictionary<ManufacturerLibraryIndexEntry, string>();

        try
        {
            foreach (var entry in check.Updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packageUri = new Uri(_packageBaseUri, Uri.EscapeDataString(entry.FileName));
                using var response = await _client.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaximumPackageBytes)
                {
                    throw new InvalidDataException($"{entry.FileName} is unexpectedly large.");
                }

                var packageBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (packageBytes.LongLength > MaximumPackageBytes)
                {
                    throw new InvalidDataException($"{entry.FileName} is unexpectedly large.");
                }

                var actualHash = Convert.ToHexString(SHA256.HashData(packageBytes));
                if (!actualHash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"{entry.FileName} failed its SHA-256 integrity check.");
                }

                var stagedPath = Path.Combine(updateRoot, entry.FileName);
                await File.WriteAllBytesAsync(stagedPath, packageBytes, cancellationToken);
                var package = PackageReader.Load(stagedPath);
                if (!package.Manifest.PackageId.Equals(entry.PackageId, StringComparison.OrdinalIgnoreCase)
                    || !package.Manifest.Version.Equals(entry.Version, StringComparison.OrdinalIgnoreCase)
                    || package.Manifest.Profiles.Count != entry.ProfileCount)
                {
                    throw new InvalidDataException($"{entry.FileName} does not match the published library index.");
                }

                stagedPackages[entry] = stagedPath;
            }

            Directory.CreateDirectory(check.LibraryDirectory);
            var originalFiles = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var (entry, stagedPath) in stagedPackages)
                {
                    var destinationPath = Path.Combine(check.LibraryDirectory, entry.FileName);
                    originalFiles[destinationPath] = File.Exists(destinationPath)
                        ? await File.ReadAllBytesAsync(destinationPath, cancellationToken)
                        : null;
                    File.Copy(stagedPath, destinationPath, overwrite: true);
                }
            }
            catch
            {
                RestoreOriginalFiles(originalFiles);
                throw;
            }

            return new ManufacturerLibraryUpdateResult(
                check.CatalogVersion,
                check.LibraryDirectory,
                stagedPackages.Keys.Select(entry => entry.FileName).Order(StringComparer.OrdinalIgnoreCase).ToList());
        }
        finally
        {
            TryDeleteUpdateDirectory(updateRoot);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private static bool IsCurrentPackage(string path, ManufacturerLibraryIndexEntry entry)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var package = PackageReader.Load(path);
            return package.Manifest.PackageId.Equals(entry.PackageId, StringComparison.OrdinalIgnoreCase)
                && CompareCatalogVersions(package.Manifest.Version, entry.Version) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static int CompareCatalogVersions(string left, string right)
    {
        if (Version.TryParse(left, out var leftVersion) && Version.TryParse(right, out var rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ? 0 : -1;
    }

    private static void ValidateIndex(ManufacturerLibraryIndex index)
    {
        if (!index.Format.Equals("bfi-manufacturer-library-index", StringComparison.OrdinalIgnoreCase)
            || index.FormatVersion != 1
            || string.IsNullOrWhiteSpace(index.CatalogVersion)
            || index.Packages.Count == 0)
        {
            throw new InvalidDataException("The manufacturer library index format is not supported.");
        }

        var duplicate = index.Packages
            .GroupBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"The manufacturer library index lists {duplicate.Key} more than once.");
        }

        foreach (var entry in index.Packages)
        {
            if (string.IsNullOrWhiteSpace(entry.FileName)
                || !Path.GetFileName(entry.FileName).Equals(entry.FileName, StringComparison.Ordinal)
                || !entry.FileName.EndsWith(".bflib", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(entry.PackageId)
                || string.IsNullOrWhiteSpace(entry.Version)
                || entry.ProfileCount <= 0
                || entry.Sha256.Length != 64
                || entry.Sha256.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException($"The manufacturer library index contains an invalid entry for '{entry.FileName}'.");
            }
        }
    }

    private static void RestoreOriginalFiles(IReadOnlyDictionary<string, byte[]?> originalFiles)
    {
        foreach (var (path, contents) in originalFiles.Reverse())
        {
            if (contents is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            else
            {
                File.WriteAllBytes(path, contents);
            }
        }
    }

    private static void TryDeleteUpdateDirectory(string directory)
    {
        try
        {
            var allowedRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BambuFilamentImporter",
                "LibraryUpdates"));
            var fullDirectory = Path.GetFullPath(directory);
            if (fullDirectory.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(fullDirectory))
            {
                Directory.Delete(fullDirectory, recursive: true);
            }
        }
        catch
        {
            // A future library update can clean up a staging folder still held by Windows.
        }
    }
}

public sealed class ManufacturerLibraryIndex
{
    public string Format { get; set; } = "";
    public int FormatVersion { get; set; }
    public string CatalogVersion { get; set; } = "";
    public string GeneratedUtc { get; set; } = "";
    public List<ManufacturerLibraryIndexEntry> Packages { get; set; } = [];
}

public sealed class ManufacturerLibraryIndexEntry
{
    public string FileName { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Version { get; set; } = "";
    public int ProfileCount { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed record ManufacturerLibraryUpdateCheck(
    string CatalogVersion,
    string LibraryDirectory,
    int CurrentCount,
    IReadOnlyList<ManufacturerLibraryIndexEntry> Updates);

public sealed record ManufacturerLibraryUpdateResult(
    string CatalogVersion,
    string LibraryDirectory,
    IReadOnlyList<string> UpdatedFiles);
