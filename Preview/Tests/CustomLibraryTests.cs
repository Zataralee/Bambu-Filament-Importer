using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BambuFilamentImporter.Models;
using BambuFilamentImporter.Services;
using BambuFilamentImporter.Workspace;

internal static class CustomLibraryTests
{
    public static void Run(WorkspaceContext workspace, List<ProductChoice> products, PrinterTarget printer)
    {
        var paths = workspace.Paths;
        string SystemHash() => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n",
            Directory.EnumerateFiles(paths.RoamingProfileRoot, "*.json", SearchOption.AllDirectories).Order()
                .Select(p => p + ":" + File.ReadAllText(p))))));
        var systemBefore = SystemHash();
        var defaultPlan = ImportSelectionPlan.Create(paths, products, [printer]);
        Check(defaultPlan.IncludesSystem && defaultPlan.Plans.Count == 1 && defaultPlan.Plans[0].Destination == ImportDestination.Both, "system catalog with project registration is the default");
        var plan = ImportSelectionPlan.Create(paths, products, [printer], useCustom: true);
        Check(!plan.IncludesSystem && plan.Changes.All(c => c.Location == "Custom Library"), "custom option switches to the user library only");
        var result = plan.Apply(workspace);
        Check(result.UserPresetEntriesWritten.Count == 2 && result.WrittenFiles.All(p => p.StartsWith(paths.ActiveUserFilamentFolder + Path.DirectorySeparatorChar)), "custom option writes only into the active user library");
        Check(systemBefore == SystemHash(), "custom-only import leaves the system catalog byte-identical");
        var custom = new BambuLibraryScanner(paths).LoadCurrentFilaments().Where(p => p.Name.EndsWith(" (Custom)")).ToList();
        Check(custom.All(p => p.StorageKind == FilamentStorageKind.UserPreset && p.VendorGroup is "Overture" or "SUNLU"), "custom presets retain their manufacturer identity");
        Check(custom.All(p => BambuJson.Parse(File.ReadAllText(p.ProfilePath))!["inherits"]!.GetValue<string>() == ""), "custom presets are independent of system parents");
        Check(custom.All(p => BambuJson.Parse(File.ReadAllText(p.ProfilePath))!["filament_id"]!.GetValue<string>().Length is > 0 and <= 8), "custom root presets carry valid filament IDs");
        Check(!ImportSelectionPlan.Create(paths, products, [printer], true).HasChanges, "custom import is idempotent beside matching system profiles");
        var edited = custom.Single(p => p.VendorGroup == "Overture");
        var values = BambuJson.Parse(File.ReadAllText(edited.ProfilePath))!.AsObject();
        values["nozzle_temperature"] = new JsonArray("247");
        File.WriteAllText(edited.ProfilePath, values.ToJsonString());
        var expandedPrinter = new PrinterTarget { Vendor = printer.Vendor, ModelName = printer.ModelName, ProfileSuffix = printer.ProfileSuffix,
            MachinePresetNames = [.. printer.MachinePresetNames, "Bambu Lab P1S 0.8 nozzle"] };
        var dual = ImportSelectionPlan.Create(paths, products, [expandedPrinter], useCustom: true);
        Check(!dual.IncludesSystem && dual.Changes.All(c => c.Location == "Custom Library"), "custom coverage extension never writes system copies");
        dual.Apply(workspace);
        Check(BambuJson.Parse(File.ReadAllText(edited.ProfilePath))!["nozzle_temperature"]![0]!.GetValue<string>() == "247", "custom coverage extension retains custom tuning");
        Check(!ImportSelectionPlan.Create(paths, products, [expandedPrinter], true).HasChanges, "custom import is idempotent");
        var failingPrinter = new PrinterTarget { Vendor = "BBL", ModelName = "Bambu Lab X1 Carbon", ProfileSuffix = "BBL X1C",
            MachinePresetNames = ["Bambu Lab X1 Carbon 0.4 nozzle"] };
        var failing = ImportSelectionPlan.Create(paths, products, [failingPrinter]);
        var systemPlan = failing.Plans[0];
        var brokenEntry = systemPlan.Package.Manifest.Profiles.First(p => p.IsSelected && !p.Name.EndsWith("@base"));
        var payload = (IDictionary<string, string>)systemPlan.Package.ProfileJsonByPath;
        var broken = BambuJson.Parse(payload[brokenEntry.OriginalRelativePath])!.AsObject();
        broken["inherits"] = "Missing parent for rollback test";
        payload[brokenEntry.OriginalRelativePath] = broken.ToJsonString();
        var beforeFailure = WorkspaceContext.Fingerprint(paths);
        try { failing.Apply(workspace); throw new Exception("Broken system import was accepted"); }
        catch (IOException) { Check(WorkspaceContext.Fingerprint(paths) == beforeFailure, "failed system import rolls back library and config changes"); }
        var current = new BambuLibraryScanner(paths).LoadCurrentFilaments();
        var systemNames = current.Where(p => p.StorageKind == FilamentStorageKind.SystemCatalog).Select(p => p.Name).ToHashSet();
        Check(custom.All(p => !systemNames.Contains(p.Name)), "custom and system preset names cannot shadow each other in Studio");

        var nodes = FilamentTree.Library(FilamentTree.Products(current));
        Check(nodes.Select(n => n.Name).SequenceEqual(new[] { "Custom Library", "Bambu System Library" }), "My Library separates custom and system roots");
        var leaf = FilamentTree.Flatten(nodes).Single(n => n.LibraryProduct?.Name == "Overture ABS" && n.LibraryProduct.StorageKind == FilamentStorageKind.UserPreset);
        Check(nodes[0].Children.Any(b => b.Name.StartsWith("Overture") && b.Children.Any(t => t.Name.StartsWith("ABS") && t.Children.Contains(leaf))), "My Library follows brand then type then filament");
        var catalog = FilamentTree.Catalog(products, "matte");
        Check(catalog.Count == 1 && catalog[0].Name.StartsWith("SUNLU") && catalog[0].Children[0].Name.StartsWith("PETG")
            && catalog[0].Children[0].Children[0].Product == products[1] && catalog[0].IsExpanded, "catalog search keeps the brand/type hierarchy and reveals matches");

        // Simulate a system-catalog refresh removing BFI profiles, in the isolated fixture only.
        var userBefore = File.ReadAllText(edited.ProfilePath);
        var manifestPath = Path.Combine(paths.RoamingProfileRoot, "BBL.json");
        var originalManifest = File.ReadAllText(manifestPath);
        var manifest = BambuJson.Parse(originalManifest)!.AsObject();
        var entries = manifest["filament_list"]!.AsArray();
        var removed = entries.OfType<JsonObject>().Where(e => !e["name"]!.GetValue<string>().StartsWith("fdm_")).ToList();
        var originals = new Dictionary<string, string>();
        foreach (var entry in removed)
        {
            var file = Path.GetFullPath(Path.Combine(paths.RoamingProfileRoot, "BBL", entry["sub_path"]!.GetValue<string>()));
            if (!file.StartsWith(Path.GetFullPath(workspace.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Fixture escaped sandbox");
            originals[file] = File.ReadAllText(file); File.Delete(file); entries.Remove(entry);
        }
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        try
        {
            var afterRefresh = new BambuLibraryScanner(paths).LoadCurrentFilaments().Single(p => p.Name == edited.Name);
            var settings = new FilamentSettingsService(paths).Load(afterRefresh);
            Check(File.ReadAllText(edited.ProfilePath) == userBefore && settings.Single(s => s.Key == "nozzle_temperature").Value == "247", "custom preset and tuning survive simulated system catalog replacement");
        }
        finally
        {
            foreach (var pair in originals) File.WriteAllText(pair.Key, pair.Value);
            File.WriteAllText(manifestPath, originalManifest);
        }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("FAIL " + message); Console.WriteLine("PASS " + message); }
}
