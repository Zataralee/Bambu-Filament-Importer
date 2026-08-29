using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BambuFilamentImporter;
using BambuFilamentImporter.Models;
using BambuFilamentImporter.Services;

var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var packagePath = Path.Combine(projectRoot, "catalogs", "source", "SUNLU-legacy-0.3.bflib");
var package = PackageReader.Load(packagePath);
Check(package.Manifest.Profiles.Count == 128, "SUNLU package profile count");
Check(package.ProfileJsonByPath.Count == 128, "SUNLU package payload count");

var overturePath = Path.Combine(projectRoot, "packages", "manufacturers", "Overture.bflib");
var overturePackage = PackageReader.Load(overturePath);
Check(overturePackage.Manifest.PrinterNeutral, "manufacturer package is printer-neutral");
Check(overturePackage.Manifest.Profiles.Count == 14, "Overture complete catalog count");
Check(overturePackage.Manifest.SourceUrls.Count >= 2, "manufacturer source attribution");
var manufacturerPackages = Directory.EnumerateFiles(Path.Combine(projectRoot, "packages", "manufacturers"), "*.bflib")
    .Select(PackageReader.Load)
    .ToList();
Check(manufacturerPackages.Count == 9, "manufacturer package count");
Check(manufacturerPackages.Sum(item => item.Manifest.Profiles.Count) == 243, "manufacturer filament total");
var manufacturerIds = manufacturerPackages
    .SelectMany(item => item.Manifest.Profiles.Select(profile => (Package: item, Profile: profile)))
    .Where(item => item.Profile.Name.EndsWith("@base", StringComparison.OrdinalIgnoreCase))
    .Select(item =>
    {
        var json = JsonNode.Parse(item.Package.ProfileJsonByPath[item.Profile.RelativePath])!.AsObject();
        return AmsFilamentId.Read(json) ?? "";
    })
    .ToList();
Check(manufacturerIds.All(id => id.Length is > 0 and <= AmsFilamentId.MaximumLength), "all manufacturer IDs fit AMS limit");
Check(manufacturerIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() == manufacturerIds.Count, "manufacturer AMS IDs are globally unique");
var neutralSunlu = PackageReader.Load(Path.Combine(projectRoot, "packages", "manufacturers", "SUNLU.bflib"));
var mattePetgProfile = neutralSunlu.Manifest.Profiles.First(profile => profile.Name == "SUNLU High Speed Matte PETG @base");
var mattePetgJson = JsonNode.Parse(neutralSunlu.ProfileJsonByPath[mattePetgProfile.RelativePath])!.AsObject();
Check(AmsFilamentId.Read(mattePetgJson) == "GFSNL194", "P1S AMS round-trip ID retained");
Check(manufacturerPackages.All(item => item.Manifest.PrinterNeutral), "all manufacturer packages printer-neutral");
Check(manufacturerPackages.SelectMany(item => item.ProfileJsonByPath.Values)
    .All(json => !json.Contains("compatible_printers", StringComparison.OrdinalIgnoreCase)), "no printer dependency in package payloads");

var livePaths = new BambuPaths();
BambuCatalogIntegrity.ValidateProfileRoot(livePaths.RoamingProfileRoot);
BambuCatalogIntegrity.ValidateProfileRoot(livePaths.ProgramProfileRoot);
Check(true, "live catalog integrity");
var liveLibrary = new BambuLibraryScanner(livePaths).LoadCurrentFilaments();
Check(liveLibrary.Any(profile => profile.StorageKind == FilamentStorageKind.SystemCatalog), "system catalog scan");
Check(liveLibrary.Any(profile => profile.StorageKind == FilamentStorageKind.UserPreset), "user Project Library scan");
var liveProfile = liveLibrary.First(profile => profile.Name.Equals("eSUN PLA+ @BBL X1C", StringComparison.OrdinalIgnoreCase));
var liveSettings = new FilamentSettingsService(livePaths).Load(liveProfile);
Check(liveSettings.Any(setting => setting.Key == "nozzle_temperature" && setting.Value.Contains("220")), "direct nozzle setting");
Check(liveSettings.Any(setting => setting.Key == "hot_plate_temp" && !setting.IsDirect), "inherited bed setting");
var discoveredPrinters = new PrinterDiscoveryService(livePaths).DiscoverConfiguredPrinters();
Check(discoveredPrinters.Any(printer => printer.ModelName == "Bambu Lab X1 Carbon"), "configured X1C discovery");
Check(discoveredPrinters.Any(printer => printer.ModelName == "Bambu Lab P1S"), "configured P1S discovery");
var expandedOverture = PrinterProfileExpander.Expand(overturePackage, discoveredPrinters.Take(2).ToList());
Check(expandedOverture.Manifest.Profiles.Count == 42, "printer-neutral package expansion");
Check(expandedOverture.Manifest.Profiles.Any(profile => profile.Name.Contains("@BBL P1S", StringComparison.OrdinalIgnoreCase)), "canonical P1S profile naming");
var gapPackage = PackageReader.Load(overturePath);
SelectOnly(gapPackage, "Overture ABS @base");
var x1cTarget = new PrinterTarget
{
    Vendor = "BBL",
    ModelName = "Bambu Lab X1 Carbon",
    ProfileSuffix = "BBL X1C",
    NozzleSummary = "0.4, 0.6",
    MachinePresetNames = ["Bambu Lab X1 Carbon 0.4 nozzle", "Bambu Lab X1 Carbon 0.6 nozzle"]
};
var p1sTarget = new PrinterTarget
{
    Vendor = "BBL",
    ModelName = "Bambu Lab P1S",
    ProfileSuffix = "BBL P1S",
    NozzleSummary = "0.4, 0.6",
    MachinePresetNames = ["Bambu Lab P1S 0.4 nozzle", "Bambu Lab P1S 0.6 nozzle"]
};
var partialLibrary = new List<CurrentFilamentEntry>
{
    new() { Name = "Overture ABS @base", StorageKind = FilamentStorageKind.SystemCatalog },
    new()
    {
        Name = "Overture ABS @BBL X1C",
        StorageKind = FilamentStorageKind.SystemCatalog,
        CompatiblePrinters = [.. x1cTarget.MachinePresetNames]
    }
};
var gapExpansion = PrinterProfileExpander.Expand(
    gapPackage,
    [x1cTarget, p1sTarget],
    partialLibrary,
    ImportDestination.DeviceAms);
Check(gapExpansion.Manifest.Profiles.Count == 2, "only missing printer coverage generated");
Check(!gapExpansion.Manifest.Profiles.First(profile => profile.Name.EndsWith("@base")).IsSelected, "existing base profile preserved");
Check(gapExpansion.Manifest.Profiles.Any(profile => profile.Name.EndsWith("@BBL P1S") && profile.IsSelected), "missing P1S coverage selected");
Check(gapExpansion.Manifest.Profiles.All(profile => !profile.Name.EndsWith("@BBL X1C")), "covered X1C profile not duplicated");

var sandbox = Path.Combine(Path.GetTempPath(), "BambuFilamentImporterSmoke-" + Guid.NewGuid().ToString("N"));
var roaming = Path.Combine(sandbox, "BambuStudio");
var program = Path.Combine(sandbox, "ProgramProfiles");
Directory.CreateDirectory(Path.Combine(roaming, "system"));
Directory.CreateDirectory(program);
Directory.CreateDirectory(Path.Combine(roaming, "system", "BBL", "filament"));
File.WriteAllText(
    Path.Combine(roaming, "system", "BBL", "filament", "fdm_filament_abs.json"),
    "{\"type\":\"filament\",\"name\":\"fdm_filament_abs\"}");
File.WriteAllText(
    Path.Combine(roaming, "system", "BBL.json"),
    "{\"filament_list\":[{\"name\":\"fdm_filament_abs\",\"sub_path\":\"filament/fdm_filament_abs.json\"}]}");
File.WriteAllText(Path.Combine(program, "BBL.json"), "{\"filament_list\":[]}");
File.WriteAllText(Path.Combine(roaming, "BambuStudio.conf"), "{\"app\":{\"preset_folder\":\"test-user\"},\"filaments\":[],\"presets\":{\"filaments\":[]}}");
var testPaths = new BambuPaths(roaming, program);

try
{
    var amsRoaming = Path.Combine(sandbox, "AmsRepairBambuStudio");
    var amsProgram = Path.Combine(sandbox, "AmsRepairProgramProfiles");
    var amsRoamingSunlu = Path.Combine(amsRoaming, "system", "BBL", "filament", "SUNLU");
    var amsProgramSunlu = Path.Combine(amsProgram, "BBL", "filament", "SUNLU");
    Directory.CreateDirectory(amsRoamingSunlu);
    Directory.CreateDirectory(amsProgramSunlu);
    File.WriteAllText(Path.Combine(amsRoamingSunlu, "SUNLU High Speed Matte PETG @base.json"),
        "{\"type\":\"filament\",\"name\":\"SUNLU High Speed Matte PETG @base\",\"filament_id\":\"GFSNL194539FC\"}");
    File.WriteAllText(Path.Combine(amsProgramSunlu, "SUNLU High Speed Matte PETG @base.json"),
        "{\"type\":\"filament\",\"name\":\"SUNLU High Speed Matte PETG @base\",\"filament_id\":\"GFSNL194539FC\"}");
    File.WriteAllText(Path.Combine(amsRoamingSunlu, "SUNLU PA6-CF @base.json"),
        "{\"type\":\"filament\",\"name\":\"SUNLU PA6-CF @base\",\"filament_id\":\"GFSNLPA6CF\"}");
    File.WriteAllText(Path.Combine(amsRoamingSunlu, "SUNLU PA6-GF @base.json"),
        "{\"type\":\"filament\",\"name\":\"SUNLU PA6-GF @base\",\"filament_id\":\"GFSNLPA6GF\"}");
    var amsRepairService = new AmsFilamentIdRepairService(new BambuPaths(amsRoaming, amsProgram), () => false);
    var amsAudit = amsRepairService.Audit();
    Check(amsAudit.AffectedProducts == 3 && amsAudit.AffectedFiles == 4 && amsAudit.ProgramFilesAffected == 1, "AMS ID repair audit");
    var amsRepair = amsRepairService.Repair();
    Check(amsRepair.RepairedProducts == 3 && amsRepair.ChangedFiles == 4 && amsRepair.BackupFiles.Count == 4, "AMS ID repair backups");
    var repairedMatteRoaming = JsonNode.Parse(File.ReadAllText(Path.Combine(amsRoamingSunlu, "SUNLU High Speed Matte PETG @base.json")))!.AsObject();
    var repairedMatteProgram = JsonNode.Parse(File.ReadAllText(Path.Combine(amsProgramSunlu, "SUNLU High Speed Matte PETG @base.json")))!.AsObject();
    Check(AmsFilamentId.Read(repairedMatteRoaming) == "GFSNL194" && AmsFilamentId.Read(repairedMatteProgram) == "GFSNL194", "AMS truncated ID matches device value");
    var repairedPaCf = AmsFilamentId.Read(JsonNode.Parse(File.ReadAllText(Path.Combine(amsRoamingSunlu, "SUNLU PA6-CF @base.json")))!.AsObject());
    var repairedPaGf = AmsFilamentId.Read(JsonNode.Parse(File.ReadAllText(Path.Combine(amsRoamingSunlu, "SUNLU PA6-GF @base.json")))!.AsObject());
    Check(repairedPaCf!.Length <= 8 && repairedPaGf!.Length <= 8 && !repairedPaCf.Equals(repairedPaGf, StringComparison.OrdinalIgnoreCase), "AMS collision assigned unique IDs");
    Check(amsRepairService.Audit().AffectedFiles == 0, "AMS repair is idempotent");

    var devicePackage = PackageReader.Load(packagePath);
    SelectOnly(devicePackage, "SUNLU ABS @base", "SUNLU ABS @BBL X1C");
    var editedBase = devicePackage.Manifest.Profiles.First(profile => profile.Name == "SUNLU ABS @base");
    editedBase.VendorGroup = "SUNLU ABS TEST";
    var renamed = devicePackage.Manifest.Profiles.First(profile => profile.Name == "SUNLU ABS @BBL X1C");
    renamed.Name = "SUNLU ABS Smoke @BBL X1C";
    var deviceResult = new BambuInstaller(testPaths, () => false).Install(devicePackage, ImportDestination.DeviceAms, installProgram: false);
    Check(deviceResult.WrittenFiles.Count == 2, "renamed Device/AMS import");
    Check(File.Exists(Path.Combine(roaming, "system", "BBL", "filament", "SUNLU", "SUNLU ABS @BBL X1C.json")), "renamed profile preserves package path");
    var savedBase = JsonNode.Parse(File.ReadAllText(Path.Combine(roaming, "system", "BBL", "filament", "SUNLU", "SUNLU ABS @base.json")))!.AsObject();
    Check(savedBase["filament_vendor"]?[0]?.GetValue<string>() == "SUNLU ABS TEST", "edited manufacturer JSON");

    var testLibrary = new BambuLibraryScanner(testPaths).LoadCurrentFilaments();
    var testChild = testLibrary.First(profile => profile.Name == "SUNLU ABS Smoke @BBL X1C");
    Check(testChild.VendorGroup == "SUNLU ABS TEST", "edited manufacturer regroup");

    var neutralPackage = PackageReader.Load(overturePath);
    SelectOnly(neutralPackage, "Overture ABS @base");
    var neutralTarget = new PrinterTarget
    {
        Vendor = "BBL",
        ModelName = "Bambu Lab P1S",
        ProfileSuffix = "BBL P1S",
        NozzleSummary = "0.4, 0.6",
        MachinePresetNames = ["Bambu Lab P1S 0.4 nozzle", "Bambu Lab P1S 0.6 nozzle"]
    };
    var expandedNeutralPackage = PrinterProfileExpander.Expand(neutralPackage, [neutralTarget]);
    var neutralResult = new BambuInstaller(testPaths, () => false)
        .Install(expandedNeutralPackage, ImportDestination.DeviceAms, installProgram: false);
    Check(neutralResult.WrittenFiles.Count == 2, "dynamic per-printer profile install");
    var dynamicChildPath = Path.Combine(roaming, "system", "BBL", "filament", "Overture", "Overture ABS @BBL P1S.json");
    var dynamicChild = JsonNode.Parse(File.ReadAllText(dynamicChildPath))!.AsObject();
    Check(dynamicChild["compatible_printers"]?.AsArray().Count == 2, "selected nozzle variants written");
    var settingsService = new FilamentSettingsService(testPaths);
    var testSettings = settingsService.Load(testChild);
    var bedTemperature = testSettings.First(setting => setting.Key == "hot_plate_temp");
    Check(!bedTemperature.IsDirect, "sandbox inherited bed setting");
    bedTemperature.Value = "95";
    Check(settingsService.Save(testChild, testSettings) == 1, "settings override save");
    var savedChild = JsonNode.Parse(File.ReadAllText(testChild.ProfilePath))!.AsObject();
    Check(savedChild["hot_plate_temp"]?[0]?.GetValue<string>() == "95", "settings override JSON");

    var collisionRoaming = Path.Combine(sandbox, "CollisionBambuStudio");
    var collisionProgram = Path.Combine(sandbox, "CollisionProgramProfiles");
    var collisionFilaments = Path.Combine(collisionRoaming, "system", "BBL", "filament");
    var collisionSunlu = Path.Combine(collisionFilaments, "SUNLU");
    Directory.CreateDirectory(collisionSunlu);
    Directory.CreateDirectory(collisionProgram);
    var collisionManifest = new JsonObject
    {
        ["filament_list"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "fdm_filament_pla",
                ["sub_path"] = "filament/fdm_filament_pla.json"
            },
            new JsonObject
            {
                ["name"] = "SUNLU PLA Marble @base",
                ["sub_path"] = "filament/SUNLU/SUNLU Marble PLA @base.json"
            },
            new JsonObject
            {
                ["name"] = "SUNLU PLA Marble @BBL A1",
                ["sub_path"] = "filament/SUNLU/SUNLU Marble PLA @BBL A1.json"
            },
            new JsonObject
            {
                ["name"] = "SUNLU PLA Marble @BBL A1M",
                ["sub_path"] = "filament/SUNLU/SUNLU Marble PLA @BBL A1M.json"
            },
            new JsonObject
            {
                ["name"] = "SUNLU PLA Marble @BBL P1P",
                ["sub_path"] = "filament/SUNLU/SUNLU Marble PLA @BBL P1P.json"
            },
            new JsonObject
            {
                ["name"] = "SUNLU PLA Marble @BBL X1",
                ["sub_path"] = "filament/SUNLU/SUNLU Marble PLA @BBL X1.json"
            },
            new JsonObject
            {
                ["name"] = "SUNLU PLA Marble @BBL X1C",
                ["sub_path"] = "filament/SUNLU/SUNLU Marble PLA @BBL X1C.json"
            },
        }
    };
    File.WriteAllText(Path.Combine(collisionFilaments, "fdm_filament_pla.json"), "{\"type\":\"filament\",\"name\":\"fdm_filament_pla\"}");
    File.WriteAllText(Path.Combine(collisionSunlu, "SUNLU Marble PLA @base.json"),
        "{\"type\":\"filament\",\"name\":\"SUNLU PLA Marble @base\",\"inherits\":\"fdm_filament_pla\"}");
    foreach (var suffix in new[] { "BBL A1", "BBL A1M", "BBL P1P", "BBL X1", "BBL X1C" })
    {
        File.WriteAllText(Path.Combine(collisionSunlu, $"SUNLU Marble PLA @{suffix}.json"),
            $"{{\"type\":\"filament\",\"name\":\"SUNLU PLA Marble @{suffix}\",\"inherits\":\"SUNLU PLA Marble @base\"}}");
    }
    File.WriteAllText(Path.Combine(collisionRoaming, "system", "BBL.json"), collisionManifest.ToJsonString());
    File.WriteAllText(Path.Combine(collisionProgram, "BBL.json"), "{\"filament_list\":[]}");
    File.WriteAllText(Path.Combine(collisionRoaming, "BambuStudio.conf"), "{\"app\":{\"preset_folder\":\"collision-user\"},\"filaments\":[]}");
    var collisionPackage = PackageReader.Load(packagePath);
    SelectOnly(collisionPackage, "SUNLU Marble PLA @base", "SUNLU Marble PLA @BBL A1");
    var collisionResult = new BambuInstaller(new BambuPaths(collisionRoaming, collisionProgram), () => false)
        .Install(collisionPackage, ImportDestination.DeviceAms, installProgram: false);
    var repairedManifest = JsonNode.Parse(File.ReadAllText(Path.Combine(collisionRoaming, "system", "BBL.json")))!["filament_list"]!.AsArray();
    Check(repairedManifest.Count == 7, "complete catalog retained during alias import");
    Check(repairedManifest.Any(item => item?["name"]?.GetValue<string>() == "SUNLU Marble PLA @base"), "selected canonical manifest name retained");
    Check(collisionResult.ManifestEntriesUpdated.Count == 2, "catalog collision updates reported");
    var repairedChild = JsonNode.Parse(File.ReadAllText(Path.Combine(collisionRoaming, "system", "BBL", "filament", "SUNLU", "SUNLU Marble PLA @BBL A1.json")))!.AsObject();
    Check(repairedChild["inherits"]?.GetValue<string>() == "SUNLU Marble PLA @base", "canonical inheritance retained");
    var migratedSibling = JsonNode.Parse(File.ReadAllText(Path.Combine(collisionSunlu, "SUNLU Marble PLA @BBL A1M.json")))!.AsObject();
    Check(migratedSibling["inherits"]?.GetValue<string>() == "SUNLU Marble PLA @base", "unselected sibling inheritance migrated");
    BambuCatalogIntegrity.ValidateProfileRoot(Path.Combine(collisionRoaming, "system"));
    Check(true, "full alias catalog remains loadable");

    var correctedPackage = PackageReader.Load(Path.Combine(projectRoot, "packages", "manufacturers", "SUNLU.bflib"));
    SelectOnly(correctedPackage, "SUNLU PLA Marble @base");
    var a1Target = new PrinterTarget
    {
        Vendor = "BBL",
        ModelName = "Bambu Lab A1",
        ProfileSuffix = "BBL A1",
        NozzleSummary = "0.4, 0.6, 0.8",
        MachinePresetNames = ["Bambu Lab A1 0.4 nozzle", "Bambu Lab A1 0.6 nozzle", "Bambu Lab A1 0.8 nozzle"]
    };
    var correctedExpanded = PrinterProfileExpander.Expand(correctedPackage, [a1Target]);
    new BambuInstaller(new BambuPaths(collisionRoaming, collisionProgram), () => false)
        .Install(correctedExpanded, ImportDestination.DeviceAms, installProgram: false);
    BambuCatalogIntegrity.ValidateProfileRoot(Path.Combine(collisionRoaming, "system"));
    var restoredManifest = JsonNode.Parse(File.ReadAllText(Path.Combine(collisionRoaming, "system", "BBL.json")))!["filament_list"]!.AsArray();
    Check(restoredManifest.Where(item => item?["name"]?.GetValue<string>().Contains("Marble", StringComparison.Ordinal) == true)
        .All(item => item?["name"]?.GetValue<string>().StartsWith("SUNLU PLA Marble", StringComparison.Ordinal) == true), "corrected SUNLU package repairs affected catalog");

    var dependencyRoaming = Path.Combine(sandbox, "DependencyBambuStudio");
    var dependencyProgram = Path.Combine(sandbox, "DependencyProgramProfiles");
    var dependencyFilaments = Path.Combine(dependencyRoaming, "system", "BBL", "filament");
    Directory.CreateDirectory(Path.Combine(dependencyFilaments, "P1P"));
    Directory.CreateDirectory(Path.Combine(dependencyProgram, "BBL", "filament"));
    File.WriteAllText(
        Path.Combine(dependencyFilaments, "Example PLA @base.json"),
        "{\"type\":\"filament\",\"name\":\"Example PLA @base\",\"inherits\":\"fdm_filament_pla\",\"filament_vendor\":[\"Example Vendor\"],\"filament_type\":[\"PLA\"]}");
    File.WriteAllText(
        Path.Combine(dependencyFilaments, "fdm_filament_pla.json"),
        "{\"type\":\"filament\",\"name\":\"fdm_filament_pla\"}");
    File.WriteAllText(
        Path.Combine(dependencyFilaments, "P1P", "Example PLA @BBL P1P.json"),
        "{\"type\":\"filament\",\"name\":\"Example PLA @BBL P1P\",\"inherits\":\"Example PLA @base\"}");
    var dependencyManifest = new JsonObject
    {
        ["filament_list"] = new JsonArray
        {
            new JsonObject { ["name"] = "Example PLA @base", ["sub_path"] = "filament/Example PLA @base.json" },
            new JsonObject { ["name"] = "Example PLA @BBL P1P", ["sub_path"] = "filament/P1P/Example PLA @BBL P1P.json" }
        }
    };
    File.WriteAllText(Path.Combine(dependencyRoaming, "system", "BBL.json"), dependencyManifest.ToJsonString());
    File.WriteAllText(Path.Combine(dependencyProgram, "BBL.json"), "{\"filament_list\":[]}");
    File.WriteAllText(Path.Combine(dependencyRoaming, "BambuStudio.conf"), "{\"app\":{\"preset_folder\":\"dependency-user\"},\"filaments\":[]}");
    var dependencyPaths = new BambuPaths(dependencyRoaming, dependencyProgram);
    var dependencyProfiles = new BambuLibraryScanner(dependencyPaths).LoadCurrentFilaments();
    var dependencyBase = dependencyProfiles.First(profile => profile.Name == "Example PLA @base");
    var dependencyChild = dependencyProfiles.First(profile => profile.Name == "Example PLA @BBL P1P");
    Check(dependencyChild.VendorGroup == "Example Vendor", "cross-folder manufacturer grouping");
    CheckThrows<InvalidOperationException>(
        () => new BambuLibraryEditor(dependencyPaths).RemoveMany([dependencyBase]),
        "cross-folder dependency blocks partial removal");
    Check(File.Exists(dependencyBase.ProfilePath) && File.Exists(dependencyChild.ProfilePath), "blocked removal preserves files");
    Check(new BambuLibraryEditor(dependencyPaths).RemoveMany([dependencyBase, dependencyChild]) == 2, "cross-folder filament removal");
    BambuCatalogIntegrity.ValidateProfileRoot(dependencyPaths.RoamingProfileRoot);
    Check(true, "post-removal catalog integrity");

    var projectPackage = PackageReader.Load(packagePath);
    SelectOnly(projectPackage, "SUNLU ABS @BBL X1C");
    var projectResult = new BambuInstaller(testPaths, () => false).Install(projectPackage, ImportDestination.ProjectLibrary, installProgram: false);
    var userPreset = Path.Combine(roaming, "user", "test-user", "filament", "SUNLU ABS @BBL X1C.json");
    Check(projectResult.UserPresetEntriesWritten.Count == 1 && File.Exists(userPreset), "Project Library import");
    var userJson = JsonNode.Parse(File.ReadAllText(userPreset))!.AsObject();
    Check(userJson["from"]?.GetValue<string>() == "User", "Project Library profile type");
    Check(userJson["inherits"]?.GetValue<string>() == "Generic ABS", "Project Library generic parent");

    var backupPath = Path.Combine(sandbox, "complete-library.bflbackup");
    var backupResult = new LibraryBackupService(testPaths, () => false).Create(backupPath);
    Check(File.Exists(backupPath) && backupResult.CatalogProfiles == 5, "complete library backup");
    var backupSummary = new LibraryBackupService(testPaths, () => false).Inspect(backupPath);
    Check(backupSummary.UserPresetFiles == 1 && backupSummary.ProjectPresetNames == 1, "backup manifest summary");

    var restoredRoaming = Path.Combine(sandbox, "RestoredBambuStudio");
    var restoredProgram = Path.Combine(sandbox, "RestoredProgramProfiles");
    Directory.CreateDirectory(Path.Combine(restoredRoaming, "system"));
    Directory.CreateDirectory(restoredProgram);
    File.WriteAllText(Path.Combine(restoredRoaming, "system", "BBL.json"), "{\"filament_list\":[]}");
    File.WriteAllText(Path.Combine(restoredProgram, "BBL.json"), "{\"filament_list\":[]}");
    File.WriteAllText(Path.Combine(restoredRoaming, "BambuStudio.conf"), "{\"app\":{\"preset_folder\":\"restored-user\"},\"filaments\":[],\"presets\":{\"filaments\":[]}}");
    var restoredPaths = new BambuPaths(restoredRoaming, restoredProgram);
    var restoreResult = new LibraryBackupService(restoredPaths, () => false).Restore(backupPath);
    Check(restoreResult.AddedFiles >= 3, "complete library restore files");
    var restoredLibrary = new BambuLibraryScanner(restoredPaths).LoadCurrentFilaments();
    var restoredChild = restoredLibrary.First(profile => profile.Name == "SUNLU ABS Smoke @BBL X1C");
    Check(restoredChild.VendorGroup == "SUNLU ABS TEST", "restored manufacturer grouping");
    var restoredChildJson = JsonNode.Parse(File.ReadAllText(restoredChild.ProfilePath))!.AsObject();
    Check(restoredChildJson["hot_plate_temp"]?[0]?.GetValue<string>() == "95", "restored custom temperature");
    Check(restoredLibrary.Any(profile => profile.Name == "SUNLU ABS @BBL X1C" && profile.StorageKind == FilamentStorageKind.UserPreset), "restored Project Library preset");

    var deviceProfiles = new BambuLibraryScanner(testPaths).LoadCurrentFilaments()
        .Where(profile => profile.StorageKind == FilamentStorageKind.SystemCatalog
            && profile.ProductName is "SUNLU ABS" or "SUNLU ABS Smoke")
        .ToList();
    Check(new BambuLibraryEditor(testPaths).RemoveMany(deviceProfiles) == 2, "filament-level removal");

    var screenshotPath = Path.Combine(projectRoot, "Tests", "artifacts", "MainWindow.png");
    var libraryScreenshotPath = Path.Combine(projectRoot, "Tests", "artifacts", "CurrentLibrary.png");
    var darkScreenshotPath = Path.Combine(projectRoot, "Tests", "artifacts", "MainWindow-Dark.png");
    RenderWindow(screenshotPath, libraryScreenshotPath, darkScreenshotPath);
    Check(File.Exists(screenshotPath) && new FileInfo(screenshotPath).Length > 50_000, "main window render");
    Check(File.Exists(libraryScreenshotPath) && new FileInfo(libraryScreenshotPath).Length > 50_000, "settings window render");
    Check(File.Exists(darkScreenshotPath) && new FileInfo(darkScreenshotPath).Length > 50_000, "dark mode render");

    Console.WriteLine("All smoke tests passed.");
}
finally
{
    var resolvedSandbox = Path.GetFullPath(sandbox);
    if (resolvedSandbox.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(resolvedSandbox).StartsWith("BambuFilamentImporterSmoke-", StringComparison.Ordinal))
    {
        Directory.Delete(resolvedSandbox, recursive: true);
    }
}

static void RenderWindow(string screenshotPath, string libraryScreenshotPath, string darkScreenshotPath)
{
    Exception? renderError = null;
    var thread = new Thread(() =>
    {
        try
        {
            var app = new App();
            app.InitializeComponent();
            ThemeService.Apply(false);
            var window = new MainWindow
            {
                Width = 1320,
                Height = 840,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual
            };
            window.Show();
            window.UpdateLayout();
            Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
            SaveWindow(window, screenshotPath);

            var tabs = (System.Windows.Controls.TabControl)window.FindName("MainTabs");
            tabs.SelectedIndex = 1;
            window.UpdateLayout();
            var tree = (System.Windows.Controls.TreeView)window.FindName("CurrentTree");
            var manufacturer = tree.Items.Cast<FilamentGroup>()
                .First(group => group.VendorGroup.StartsWith("SUNLU", StringComparison.OrdinalIgnoreCase));
            var manufacturerNode = (System.Windows.Controls.TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(manufacturer);
            manufacturerNode.IsExpanded = true;
            window.UpdateLayout();
            var product = manufacturer.Items.First();
            var productNode = (System.Windows.Controls.TreeViewItem)manufacturerNode.ItemContainerGenerator.ContainerFromItem(product);
            productNode.IsExpanded = true;
            window.UpdateLayout();
            var profile = product.Items.First();
            var profileNode = (System.Windows.Controls.TreeViewItem)productNode.ItemContainerGenerator.ContainerFromItem(profile);
            profileNode.IsSelected = true;
            window.UpdateLayout();
            SaveWindow(window, libraryScreenshotPath);
            ThemeService.Apply(true);
            window.UpdateLayout();
            SaveWindow(window, darkScreenshotPath);
            window.Close();
            app.Shutdown();
        }
        catch (Exception ex)
        {
            renderError = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (renderError is not null)
    {
        throw renderError;
    }
}

static void SaveWindow(System.Windows.Window window, string path)
{
    var bitmap = new RenderTargetBitmap(
        Math.Max(1, (int)Math.Ceiling(window.ActualWidth)),
        Math.Max(1, (int)Math.Ceiling(window.ActualHeight)),
        96,
        96,
        PixelFormats.Pbgra32);
    bitmap.Render(window);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}

static void SelectOnly(LoadedFilamentPackage package, params string[] names)
{
    var selected = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var profile in package.Manifest.Profiles)
    {
        profile.IsSelected = selected.Contains(profile.Name);
    }
}

static void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException("Smoke test failed: " + name);
    }

    Console.WriteLine("PASS " + name);
}

static void CheckThrows<TException>(Action action, string name) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        Console.WriteLine("PASS " + name);
        return;
    }

    throw new InvalidOperationException("Smoke test failed: " + name);
}
