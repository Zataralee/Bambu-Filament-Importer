using System.IO;
using System.Text.Json;

namespace BambuFilamentImporter.Services;

public sealed record ManufacturerLibraryCatalogState(
    bool HasSavedState,
    IReadOnlySet<string> KnownPackageIds);

public static class ManufacturerLibraryCatalogStateStore
{
    public static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BambuFilamentImporter",
        "library-catalog-state.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static ManufacturerLibraryCatalogState Load(string? path = null)
    {
        var statePath = path ?? StatePath;
        try
        {
            if (!File.Exists(statePath))
            {
                return new ManufacturerLibraryCatalogState(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }

            var document = JsonSerializer.Deserialize<CatalogStateDocument>(File.ReadAllText(statePath), JsonOptions);
            var knownIds = (document?.KnownPackageIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new ManufacturerLibraryCatalogState(true, knownIds);
        }
        catch (JsonException)
        {
            return new ManufacturerLibraryCatalogState(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    public static void Save(IEnumerable<string> packageIds, string? path = null)
    {
        var statePath = path ?? StatePath;
        var folder = Path.GetDirectoryName(statePath)
            ?? throw new InvalidOperationException("The library catalog state folder could not be determined.");
        Directory.CreateDirectory(folder);
        var document = new CatalogStateDocument
        {
            KnownPackageIds = packageIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
        var temporaryPath = statePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine);
        File.Move(temporaryPath, statePath, overwrite: true);
    }

    private sealed class CatalogStateDocument
    {
        public List<string> KnownPackageIds { get; set; } = [];
    }
}
