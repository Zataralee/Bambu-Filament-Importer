using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using BambuFilamentImporter.Models;
using BambuFilamentImporter.Services;

internal static class StudioCompatibilityTests
{
    public static void CheckJsonReader()
    {
        var json = BambuJson.Parse("""
            {"filament_dev_ams_drying_time":["12","8.0","12","8.0"],
             "filament_dev_ams_drying_time":["12","8.0","12","8.0","8.0","8.0","12","12"],
             "nested":{"a":1,"a":2},"items":[{"a":false,"a":true}],
             "cleared":1,"cleared":null,"Name":"upper","name":"lower",
             "gcode":"G1 X2\n; \\"}
            """)!.AsObject();
        Check(json["filament_dev_ams_drying_time"]!.AsArray().Count == 8, "last drying array wins");
        Check(json["nested"]!["a"]!.GetValue<int>() == 2, "nested duplicates");
        Check(json["items"]![0]!["a"]!.GetValue<bool>(), "duplicates inside arrays");
        Check(json.ContainsKey("cleared") && json["cleared"] is null, "last null wins");
        Check(json["Name"]!.GetValue<string>() == "upper" && json["name"]!.GetValue<string>() == "lower",
            "JSON keys remain case sensitive");
        Check(JsonNode.DeepEquals(json, JsonNode.Parse(json.ToJsonString())), "editable JSON round trip");
        try
        {
            BambuJson.Parse("{\"a\":1,}");
            throw new Exception("Malformed JSON was accepted.");
        }
        catch (JsonException) { }
        Console.WriteLine("PASS malformed JSON still rejected");
    }

    public static void CheckUpdatedCatalog(BambuPaths live, string sandbox, string projectRoot)
    {
        // Exercise real updated profiles, but never write to the user's installation/config.
        var paths = new BambuPaths(Path.Combine(sandbox, "UpdatedStudio"), Path.Combine(sandbox, "UnusedProgram"));
        var sourceBbl = Path.Combine(live.RoamingProfileRoot, "BBL");
        var targetBbl = Path.Combine(paths.RoamingProfileRoot, "BBL");
        Directory.CreateDirectory(targetBbl);
        File.Copy(Path.Combine(live.RoamingProfileRoot, "BBL.json"), Path.Combine(paths.RoamingProfileRoot, "BBL.json"));
        foreach (var source in Directory.EnumerateFiles(sourceBbl, "*.json", SearchOption.AllDirectories))
        {
            var target = Path.Combine(targetBbl, Path.GetRelativePath(sourceBbl, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        // Always retain a duplicate-key fixture even after upstream fixes its profiles.
        var duplicatePath = Path.Combine(targetBbl, "filament", "BFI compatibility fixture.json");
        const string duplicateJson = """
            {"type":"filament","name":"BFI compatibility fixture @base","filament_id":"BFITEST1",
             "filament_dev_ams_drying_time":["12"],"filament_dev_ams_drying_time":["12","8.0"]}
            """;
        File.WriteAllText(duplicatePath, duplicateJson);
        var manifestPath = Path.Combine(paths.RoamingProfileRoot, "BBL.json");
        var manifest = BambuJson.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["filament_list"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "BFI compatibility fixture @base",
            ["sub_path"] = "filament/BFI compatibility fixture.json"
        });
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var nativePcPath = Path.Combine(targetBbl, "filament", "fdm_filament_pc.json");
        var nativePc = File.ReadAllBytes(nativePcPath);
        BambuCatalogIntegrity.ValidateProfileRoot(paths.RoamingProfileRoot);
        new AmsFilamentIdRepairService(paths, () => false).Audit();
        Check(true, "updated catalog validates and audits with duplicate keys");

        var package = PackageReader.Load(Path.Combine(projectRoot, "packages", "manufacturers", "SUNLU.bflib"));
        foreach (var profile in package.Manifest.Profiles)
            profile.IsSelected = profile.Name == "SUNLU High Speed Matte PETG @base";
        var targetPrinter = new PrinterTarget
        {
            Vendor = "BBL", ModelName = "Bambu Lab P1S", ProfileSuffix = "BBL P1S",
            MachinePresetNames = ["Bambu Lab P1S 0.4 nozzle"]
        };
        var expanded = PrinterProfileExpander.Expand(package, [targetPrinter]);
        var installed = new BambuInstaller(paths, () => false).Install(expanded, ImportDestination.DeviceAms, false);
        Check(installed.WrittenFiles.Count == 2 && installed.Backups.Count > 0,
            "reported SUNLU import succeeds with backups on updated catalog copy");
        var library = new BambuLibraryScanner(paths).LoadCurrentFilaments();
        var child = library.First(p => p.Name == "SUNLU High Speed Matte PETG @BBL P1S");
        Check(child.CompatiblePrinters.Contains("Bambu Lab P1S 0.4 nozzle"), "imported printer coverage");
        var fixture = library.First(p => p.Name == "BFI compatibility fixture @base");
        var service = new FilamentSettingsService(paths);
        var settings = service.Load(fixture);
        var drying = settings.Single(s => s.Key == "filament_dev_ams_drying_time");
        Check(drying.Value.Contains("8.0"), "settings show final duplicate value");
        new BambuLibraryEditor(paths).Save(fixture, "BFI compatibility renamed @base", "BFI Test");
        library = new BambuLibraryScanner(paths).LoadCurrentFilaments();
        fixture = library.First(p => p.Name == "BFI compatibility renamed @base");
        Check(BambuJson.Parse(File.ReadAllText(fixture.ProfilePath))!["filament_dev_ams_drying_time"]!.AsArray().Count == 2,
            "editing preserves effective duplicate value");
        Check(new BambuLibraryEditor(paths).RemoveMany([fixture]) == 1, "removal on updated catalog copy");
        BambuCatalogIntegrity.ValidateProfileRoot(paths.RoamingProfileRoot);
        Check(File.ReadAllBytes(nativePcPath).SequenceEqual(nativePc), "untouched upstream profile remains byte identical");

        var validManifest = File.ReadAllText(manifestPath);
        manifest = BambuJson.Parse(validManifest)!.AsObject();
        manifest["filament_list"]!.AsArray().Add(manifest["filament_list"]![0]!.DeepClone());
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        try
        {
            BambuCatalogIntegrity.ValidateProfileRoot(paths.RoamingProfileRoot);
            throw new Exception("Duplicate catalog identity was accepted.");
        }
        catch (InvalidDataException) { }
        File.WriteAllText(manifestPath, validManifest);
        Console.WriteLine("PASS duplicate catalog identities still rejected");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }
}
