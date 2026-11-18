namespace BambuFilamentImporter.Services;

/// <summary>Classifies package updates separately from new optional libraries.</summary>
public sealed record LibraryUpdateNotice(int DownloadedUpdates, IReadOnlySet<string> NewPackageIds, bool NeedsFirstDownload)
{
    public bool IsVisible => DownloadedUpdates > 0 || NewPackageIds.Count > 0 || NeedsFirstDownload;
    public string Text => string.Join(" · ", new[]
    {
        DownloadedUpdates > 0 ? $"{DownloadedUpdates} downloaded filament library update{(DownloadedUpdates == 1 ? "" : "s")} available" : null,
        NewPackageIds.Count > 0 ? $"{NewPackageIds.Count} new filament {(NewPackageIds.Count == 1 ? "library" : "libraries")} available" : null,
        NeedsFirstDownload ? "Choose your first manufacturer libraries to get started" : null
    }.Where(s => s is not null));
    public static LibraryUpdateNotice From(ManufacturerLibraryCatalog catalog, ManufacturerLibraryCatalogState state) => new(
        catalog.Updates.Count,
        state.HasSavedState
            ? catalog.Entries.Where(e => !state.KnownPackageIds.Contains(e.Package.PackageId)).Select(e => e.Package.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        catalog.InstalledCount == 0);
}
