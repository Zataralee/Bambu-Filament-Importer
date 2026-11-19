using System.IO;
using System.Text.Json.Nodes;
using BambuFilamentImporter.Models;
using BambuFilamentImporter.Services;
using BambuFilamentImporter.Workspace;

internal static class LibraryToolsTests
{
    public static void Run(WorkspaceContext workspace, string repo)
    {
        var paths = workspace.Paths;
        var backup = workspace.CreateBackup("before-tools-tests");
        List<CurrentFilamentEntry> Current() => new BambuLibraryScanner(paths).LoadCurrentFilaments();
        var trees = FilamentTree.Library(FilamentTree.Products(Current()));
        Check(trees.All(t => FilamentTree.DeletionProfiles(t).Count == 0), "library roots cannot trigger delete-all");
        var system = FilamentTree.Flatten(trees).Single(n => n.LibraryProduct?.Name == "Overture ABS" && n.LibraryProduct.StorageKind == FilamentStorageKind.SystemCatalog);
        var user = FilamentTree.Flatten(trees).Single(n => n.LibraryProduct?.Name == "Overture ABS" && n.LibraryProduct.StorageKind == FilamentStorageKind.UserPreset);
        var customFiles = FilamentTree.DeletionProfiles(user).ToDictionary(p => p.ProfilePath, p => File.ReadAllText(p.ProfilePath));
        var systemProfiles = FilamentTree.DeletionProfiles(system);
        var before = WorkspaceContext.Fingerprint(paths);
        var baseProfile = systemProfiles.Single(p => p.IsBaseProfile);
        Throws<IOException>(() => new LibraryRemovalPlan(paths, [baseProfile]).Apply(workspace), "deleting a shared system base alone is blocked");
        Check(before == WorkspaceContext.Fingerprint(paths), "blocked base deletion leaves all files unchanged");
        var stale = new LibraryRemovalPlan(paths, systemProfiles);
        File.AppendAllText(paths.ConfigPath, "\n");
        Throws<IOException>(() => stale.Apply(workspace), "deletion review rejects changed library state");
        new LibraryRemovalPlan(paths, systemProfiles).Apply(workspace);
        Check(systemProfiles.All(p => !File.Exists(p.ProfilePath)) && Current().All(p => p.StorageKind != FilamentStorageKind.SystemCatalog || p.ProductName != "Overture ABS"), "whole system filament deletion removes its base, children, and manifest entries");
        Check(customFiles.All(p => File.ReadAllText(p.Key) == p.Value), "system deletion leaves matching custom presets unchanged");
        var custom = FilamentTree.DeletionProfiles(user);
        var config = BambuJson.Parse(File.ReadAllText(paths.ConfigPath))!.AsObject();
        config["app"]!["ams_recent_filament_presets"] = string.Join('\n', custom.Select(p => p.Name).Append("Keep recent"));
        File.WriteAllText(paths.ConfigPath, config.ToJsonString());
        foreach (var p in custom) File.WriteAllText(p.InfoPath, "fixture metadata");
        new LibraryRemovalPlan(paths, custom).Apply(workspace);
        Check(custom.All(p => !File.Exists(p.ProfilePath) && !File.Exists(p.InfoPath)), "custom filament deletion removes JSON and sync sidecars");
        Check(BambuJson.Parse(File.ReadAllText(paths.ConfigPath))!["app"]!["ams_recent_filament_presets"]!.GetValue<string>() == "Keep recent", "deletion removes stale AMS recent entries without removing other entries");
        new LibraryBackupService(paths, () => false).Restore(backup);
        var customRoot = Current().First(p => p.StorageKind == FilamentStorageKind.UserPreset && p.Name.EndsWith("(Custom)"));
        var childPath = Path.Combine(paths.ActiveUserFilamentFolder, "Dependent fixture.json");
        File.WriteAllText(childPath, new JsonObject { ["name"] = "Dependent fixture", ["inherits"] = customRoot.Name, ["version"] = "2.8.0.0" }.ToJsonString());
        Throws<IOException>(() => new LibraryRemovalPlan(paths, [customRoot]).Apply(workspace), "custom preset parents cannot be deleted while a dependent remains");
        var originalName = customRoot.Name;
        var renamed = originalName + " renamed";
        LibraryTransaction.Run(workspace, "rename-test", () => { new BambuLibraryEditor(paths).Save(customRoot, renamed, "Test brand"); return true; });
        Check(BambuJson.Parse(File.ReadAllText(childPath))!["inherits"]!.GetValue<string>() == renamed && Current().Any(p => p.Name == renamed && p.VendorGroup == "Test brand"), "custom rename updates dependent inheritance and brand metadata");
        LibraryTransaction.Run(workspace, "rename-back", () => { new BambuLibraryEditor(paths).Save(customRoot, originalName, "Overture"); return true; });
        var dependent = Current().Single(p => p.Name == "Dependent fixture");
        new LibraryRemovalPlan(paths, [dependent]).Apply(workspace);
        var branded = Current().Where(p => p.ProductName == "Overture ABS" && p.StorageKind == FilamentStorageKind.UserPreset).ToList();
        var systemHash = string.Join("\n", Current().Where(p => p.StorageKind == FilamentStorageKind.SystemCatalog).Select(p => File.ReadAllText(p.ProfilePath)));
        LibraryTransaction.Run(workspace, "brand-test", () => new BambuLibraryEditor(paths).SetManufacturer(branded, "Overture - BFI"));
        Check(Current().Where(p => branded.Any(b => b.ProfilePath == p.ProfilePath)).All(p => p.VendorGroup == "Overture - BFI")
            && systemHash == string.Join("\n", Current().Where(p => p.StorageKind == FilamentStorageKind.SystemCatalog).Select(p => File.ReadAllText(p.ProfilePath))), "brand changes stay within the selected library");
        Check(FilamentTree.Products(Current(), ["Overture"]).Any(p => p.Brand == "Overture - BFI"), "library grouping retains a deliberately renamed brand");
        var filtered = FilamentTree.Library(FilamentTree.Products(Current()), "High Speed Matte");
        Check(FilamentTree.DeletionProfiles(filtered.First().Children.First()).All(p => p.ProductName.Contains("High Speed Matte")), "filtered brand deletion includes only the displayed matches");

        var package = PackageReader.Load(Path.Combine(repo, "packages", "manufacturers", "Overture.bflib"));
        foreach (var p in package.Manifest.Profiles) p.IsSelected = p.Name == "Overture ABS @base";
        var editedProfile = package.Manifest.Profiles.Single(p => p.IsSelected);
        editedProfile.Name = "Renamed package ABS @base"; editedProfile.VendorGroup = "Package brand";
        var printer = new PrinterTarget { Vendor="BBL", ModelName="Bambu Lab P1S", ProfileSuffix="BBL P1S", MachinePresetNames=["Bambu Lab P1S 0.4 nozzle"] };
        var plan = AdvancedImportPlan.Create(paths, package, [printer], ImportDestination.Both);
        editedProfile.Name = "Changed after review @base";
        Check(plan.Changes.Any(p => p.Name == "Renamed package ABS @base") && plan.Package.Manifest.Profiles.All(p => !p.Name.StartsWith("Changed after review")), "advanced review freezes package edits rather than sharing mutable input");
        plan.Apply(workspace);
        Check(Current().Any(p => p.Name == "Renamed package ABS @BBL P1S" && p.VendorGroup == "Package brand"), "advanced package name and brand edits install correctly");
        var legacy = plan.Package;
        var legacyPlan = AdvancedImportPlan.Create(paths, legacy, [], ImportDestination.DeviceAms);
        Check(!legacyPlan.Package.Manifest.PrinterNeutral && legacyPlan.Changes.Any(c => c.Action.StartsWith("Replace")), "legacy printer-specific packages are reviewable with explicit replacement labels");
        var stalePlan = AdvancedImportPlan.Create(paths, legacy, [], ImportDestination.DeviceAms);
        File.AppendAllText(paths.ConfigPath, "\n");
        Throws<IOException>(() => stalePlan.Apply(workspace), "advanced import rejects a stale review");
        var programManifest = Path.Combine(paths.ProgramProfileRoot, "BBL.json");
        var programBefore = File.ReadAllText(programManifest);
        Throws<InvalidOperationException>(() => InstalledCatalogTransaction.Run<int>(paths, () => { File.WriteAllText(programManifest, "{}"); throw new InvalidOperationException("fixture failure"); }, () => false), "installed-catalog failures propagate after rollback");
        Check(File.ReadAllText(programManifest) == programBefore, "administrator transaction rollback restores the installed manifest");
        // Remove generated test profiles, then restore the test fixture for rendering.
        new LibraryRemovalPlan(paths, Current().Where(p => p.ProductName == "Renamed package ABS")).Apply(workspace);
        new LibraryBackupService(paths, () => false).Restore(backup);
    }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); }
    private static void Throws<T>(Action action, string name) where T : Exception
    { try { action(); } catch (T) { Check(true, name); return; } throw new Exception("FAIL " + name); }
}
