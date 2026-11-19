using System.IO;
using System.Text.Json;
using BambuFilamentImporter.Services;

internal static class UpdateNotificationTests
{
    public static void Run(string root)
    {
        const string releaseJson = """
            {"tag_name":"v0.6.1","draft":false,"prerelease":false,
             "html_url":"https://github.com/Zataralee/Bambu-Filament-Importer/releases/tag/v0.6.1",
             "assets":[{"name":"BambuFilamentImporter.exe","browser_download_url":"https://example.invalid/app.exe"},
                       {"name":"Bambu-Filament-Importer-v0.6.1-win-x64.zip","browser_download_url":"https://example.invalid/update.zip"}]}
            """;
        using var document = JsonDocument.Parse(releaseJson);
        var release = UpdateService.ParseRelease(document.RootElement);
        Check(release.Version > new Version(0, 4, 15) && release.Version > new Version(0, 6, 0), "stable release upgrades public and compatibility builds");
        // This is the exact tag/asset selection contract used by the existing public client.
        var legacyVersion = Version.Parse(document.RootElement.GetProperty("tag_name").GetString()!.TrimStart('v', 'V'));
        var legacyAsset = document.RootElement.GetProperty("assets").EnumerateArray().First(a => a.GetProperty("name").GetString()!.EndsWith("-win-x64.zip"));
        Check(legacyVersion == release.Version && legacyAsset.GetProperty("name").GetString() == release.AssetName, "existing clients discover the same Windows update archive");
        Check(!UpdateService.IsNewer(release), "current stable version does not prompt itself");
        foreach (var field in new[] { "draft", "prerelease" })
        {
            using var unpublished = JsonDocument.Parse(releaseJson.Replace($"\"{field}\":false", $"\"{field}\":true"));
            try { UpdateService.ParseRelease(unpublished.RootElement); throw new Exception("Unpublished release accepted"); }
            catch (InvalidDataException) { Check(true, field + " excluded from stable updates"); }
        }
        ManufacturerLibraryCatalogEntry Entry(string id, ManufacturerLibraryStatus status) => new(new() { PackageId = id }, status, "unused", "1.0");
        var old = Entry("sunlu", ManufacturerLibraryStatus.UpdateAvailable);
        var knownOptional = Entry("overture", ManufacturerLibraryStatus.Available);
        var added = Entry("new-manufacturer", ManufacturerLibraryStatus.Available);
        var catalog = new ManufacturerLibraryCatalog("1.1", root, [old, knownOptional, added]);
        var path = Path.Combine(root, "notification-state.json");
        ManufacturerLibraryCatalogStateStore.Save(["SUNLU", "Overture"], path);
        var state = ManufacturerLibraryCatalogStateStore.Load(path);
        var notice = LibraryUpdateNotice.From(catalog, state);
        Check(notice.DownloadedUpdates == 1 && !notice.NeedsFirstDownload, "downloaded library updates remain visible");
        Check(notice.NewPackageIds.SetEquals(["new-manufacturer"]), "new libraries are separate from known optional downloads");
        ManufacturerLibraryCatalogStateStore.Save(catalog.Entries.Select(e => e.Package.PackageId), path);
        var acknowledged = LibraryUpdateNotice.From(catalog, ManufacturerLibraryCatalogStateStore.Load(path));
        Check(acknowledged.NewPackageIds.Count == 0 && acknowledged.DownloadedUpdates == 1 && acknowledged.IsVisible, "viewing catalog acknowledges new libraries without dismissing pending updates");
        var current = new ManufacturerLibraryCatalog("1.1", root, [old with { Status = ManufacturerLibraryStatus.Current }, knownOptional, added]);
        Check(!LibraryUpdateNotice.From(current, ManufacturerLibraryCatalogStateStore.Load(path)).IsVisible, "notice clears after all downloaded packages are current");
        var initial = LibraryUpdateNotice.From(catalog, new(false, new HashSet<string>()));
        Check(initial.DownloadedUpdates == 1 && initial.NewPackageIds.Count == 0, "first catalog baseline still announces existing package updates");
        Check(LibraryUpdateNotice.From(new("1.1", root, [knownOptional]), state).NeedsFirstDownload, "empty package library offers first download");
        var production = new BambuFilamentImporter.Workspace.WorkspaceContext();
        Check(production.IsLive && !production.IsPreview && production.LibraryFolder == ManufacturerLibraryStore.ManagedDirectory
            && production.CatalogStatePath == ManufacturerLibraryCatalogStateStore.StatePath, "stable workspace reuses existing downloads and notification history");
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }
}
