using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using BambuFilamentImporter.Models;
using BambuFilamentImporter.Services;
using BambuFilamentImporter.Workspace;

internal static class StudioRecoveryTests
{
    public static void Run(WorkspaceContext workspace)
    {
        var paths = workspace.Paths;
        var manifestPath = Path.Combine(paths.RoamingProfileRoot, "BBL.json");
        var manifest = BambuJson.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var stock = new JsonObject { ["filament_list"] = new JsonArray(manifest["filament_list"]!.AsArray()
            .Where(n => n!["name"]!.GetValue<string>().StartsWith("fdm_")).Select(n => n!.DeepClone()).ToArray()) };
        Directory.CreateDirectory(paths.ProgramProfileRoot);
        File.WriteAllText(Path.Combine(paths.ProgramProfileRoot, "BBL.json"), stock.ToJsonString());
        string? version = "2.9.1.70";
        var recovery = new StudioRecoveryService(workspace, () => version);
        Check(recovery.Check() is null && recovery.Capture(), "first Studio version creates a baseline without an update prompt");
        var initial = recovery.ReadState()!;
        Check(initial.ProfileNames.Count > 0 && initial.ProfileNames.All(n => !n.StartsWith("fdm_")), "recovery tracks imported profiles and excludes bundled system presets");
        Check(recovery.Capture() && recovery.ReadState()!.BackupFile == initial.BackupFile, "unchanged library reuses its recovery point");
        var imported = new BambuLibraryScanner(paths).LoadCurrentFilaments().First(p => initial.ProfileNames.Contains(p.Name) && !p.IsBaseProfile);
        var edited = BambuJson.Parse(File.ReadAllText(imported.ProfilePath))!.AsObject();
        edited["nozzle_temperature"] = new JsonArray("251");
        File.WriteAllText(imported.ProfilePath, edited.ToJsonString());
        Check(recovery.Capture(explicitChange: true), "post-edit recovery captures tuned system profiles");
        var saved = recovery.ReadState()!;
        Check(saved.BackupFile != initial.BackupFile, "new recovery points keep previous snapshots intact");
        var folder = Path.Combine(workspace.BackupFolder, "StudioRecovery");
        var backup = Path.Combine(folder, saved.BackupFile);
        Check(File.ReadAllText(Path.ChangeExtension(backup, ".conf")) == File.ReadAllText(paths.ConfigPath), "full Studio config is saved alongside the scoped recovery archive");
        using (var archive = ZipFile.OpenRead(backup))
            Check(archive.Entries.All(e => !e.FullName.StartsWith("user/")), "automatic Studio recovery excludes custom preset payloads");

        // Simulate a new installer replacing the system catalog in this isolated fixture.
        manifest["filament_list"] = stock["filament_list"]!.DeepClone();
        var entries = manifest["filament_list"]!.AsArray();
        var newName = "New bundled PLA";
        var newPath = Path.Combine(paths.RoamingProfileRoot, "BBL", "filament", newName + ".json");
        var newJson = new JsonObject { ["name"] = newName, ["filament_vendor"] = new JsonArray("Bambu Lab"),
            ["filament_type"] = new JsonArray("PLA"), ["nozzle_temperature"] = new JsonArray("219") }.ToJsonString();
        File.WriteAllText(newPath, newJson);
        entries.Add(new JsonObject { ["name"] = newName, ["sub_path"] = "filament/" + newName + ".json" });
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        File.WriteAllText(Path.Combine(paths.ProgramProfileRoot, "BBL.json"), manifest.ToJsonString());
        foreach (var file in Directory.EnumerateFiles(Path.Combine(paths.RoamingProfileRoot, "BBL", "filament"), "*.json", SearchOption.AllDirectories))
        {
            var node = BambuJson.Parse(File.ReadAllText(file))!;
            if (!saved.ProfileNames.Contains(node["name"]!.GetValue<string>())) continue;
            if (!Path.GetFullPath(file).StartsWith(Path.GetFullPath(workspace.Root) + Path.DirectorySeparatorChar)) throw new Exception("Unsafe fixture path");
            File.Delete(file);
        }
        var config = BambuJson.Parse(File.ReadAllText(paths.ConfigPath))!.AsObject();
        config["new_studio_option"] = "keep this";
        config["filaments"] = new JsonArray(newName);
        config["presets"]!["filaments"] = new JsonArray(newName);
        File.WriteAllText(paths.ConfigPath, config.ToJsonString());
        var users = Directory.EnumerateFiles(paths.UserRoot, "*.json", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllText);
        version = "2.10.0.1";
        var offer = recovery.Check()!;
        Check(offer.Saved.StudioVersion == "2.9.1.70" && offer.CurrentVersion == version, "Studio version change offers the previous configuration");
        Check(!recovery.Capture() && !recovery.Capture(explicitChange: true) && recovery.ReadState()!.BackupFile == saved.BackupFile,
            "opening or editing after a version change cannot replace the pre-update backup");
        Check(new StudioRecoveryService(workspace, () => version).Check() is not null, "deferred recovery survives a BFI restart");
        version = null;
        Check(recovery.Check() is null && !recovery.Capture() && recovery.ReadState()!.BackupFile == saved.BackupFile,
            "unknown Studio version leaves the recovery point intact");
        version = "2.10.0.1";
        var before = WorkspaceContext.Fingerprint(paths);
        var calls = 0;
        var changingVersion = new StudioRecoveryService(workspace, () => ++calls == 1 ? version : "2.11.0.1");
        try { changingVersion.Restore(offer); throw new Exception("Changing Studio version accepted"); }
        catch (IOException) { Check(WorkspaceContext.Fingerprint(paths) == before && recovery.Check() is not null, "mid-restore version change rolls back all writes and retains recovery offer"); }
        var bytes = File.ReadAllBytes(backup);
        File.WriteAllText(backup, "broken backup");
        try { recovery.Restore(offer); throw new Exception("Corrupt backup accepted"); }
        catch (IOException) { Check(WorkspaceContext.Fingerprint(paths) == before && recovery.Check() is not null, "corrupt recovery archive cannot alter the library or consume the offer"); }
        finally { File.WriteAllBytes(backup, bytes); }
        recovery.Restore(offer);
        Check(File.Exists(imported.ProfilePath) && BambuJson.Parse(File.ReadAllText(imported.ProfilePath))!["nozzle_temperature"]![0]!.GetValue<string>() == "251",
            "update recovery restores missing imports with their last saved tuning");
        Check(File.ReadAllText(newPath) == newJson && BambuJson.Parse(File.ReadAllText(manifestPath))!["filament_list"]!.AsArray().Any(n => n!["name"]!.GetValue<string>() == newName),
            "update recovery retains new bundled profiles and catalog entries");
        config = BambuJson.Parse(File.ReadAllText(paths.ConfigPath))!.AsObject();
        Check(config["new_studio_option"]!.GetValue<string>() == "keep this" && config["presets"]!["filaments"]!.AsArray().Count == 1
            && config["presets"]!["filaments"]![0]!.GetValue<string>() == newName, "update recovery preserves new options and current filament slot order");
        Check(config["filaments"]!.AsArray().Any(n => n!.GetValue<string>() == imported.Name), "update recovery merges imported filament registrations");
        Check(users.All(p => File.ReadAllText(p.Key) == p.Value), "update recovery leaves all custom presets unchanged");
        Check(recovery.Check() is null && recovery.ReadState()!.StudioVersion == version && File.Exists(backup), "successful recovery advances the saved version and keeps its original backup");
        BambuCatalogIntegrity.ValidateProfileRoot(paths.RoamingProfileRoot);
    }
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); }
}
