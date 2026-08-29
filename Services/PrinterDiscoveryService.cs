using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using BambuFilamentImporter.Models;

namespace BambuFilamentImporter.Services;

public sealed record PrinterDiscoveryResult(
    IReadOnlyList<PrinterTarget> Printers,
    string Status,
    bool UsesRegisteredDevices,
    int RegisteredDeviceCount,
    int UnrecognizedDeviceCount);

public sealed class PrinterDiscoveryService
{
    private static readonly IReadOnlyDictionary<string, string> SerialPrefixModels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["00M"] = "Bambu Lab X1 Carbon",
            ["00W"] = "Bambu Lab X1",
            ["03W"] = "Bambu Lab X1E",
            ["01S"] = "Bambu Lab P1P",
            ["01P"] = "Bambu Lab P1S",
            ["030"] = "Bambu Lab A1 mini",
            ["039"] = "Bambu Lab A1",
            ["22E"] = "Bambu Lab P2S",
            ["093"] = "Bambu Lab H2S",
            ["094"] = "Bambu Lab H2D",
            ["239"] = "Bambu Lab H2D Pro"
        };

    private readonly BambuPaths _paths;

    public PrinterDiscoveryService(BambuPaths paths)
    {
        _paths = paths;
    }

    public List<PrinterTarget> DiscoverConfiguredPrinters() => Discover().Printers.ToList();

    public PrinterDiscoveryResult Discover()
    {
        var config = ReadConfig();
        var configuredModels = ReadConfiguredModels(config);
        var registeredDevices = ReadRegisteredDeviceIds(config);
        var machineProfiles = ReadMachineProfiles();
        var registeredModels = ResolveRegisteredModels(registeredDevices, configuredModels, machineProfiles);
        var unrecognizedCount = registeredDevices.Count - registeredModels.ResolvedDeviceCount;

        IReadOnlyList<ConfiguredModel> modelsToUse = registeredModels.Models.Count > 0
            ? registeredModels.Models
            : configuredModels;
        var usesRegisteredDevices = registeredModels.Models.Count > 0;
        var targets = BuildTargets(modelsToUse, machineProfiles);
        if (targets.Count == 0)
        {
            targets = BuildTargets(
                machineProfiles
                    .GroupBy(machine => new { machine.Vendor, machine.ModelName })
                    .Select(group => new ConfiguredModel(group.Key.Vendor, group.Key.ModelName, []))
                    .ToList(),
                machineProfiles);
        }

        var status = usesRegisteredDevices
            ? $"Matched {targets.Count} printer model(s) from {registeredDevices.Count} locally registered Bambu device(s). No printer or account connection."
            : "No registered device model could be identified; showing enabled local machine presets. Review the selection before importing.";
        if (usesRegisteredDevices && unrecognizedCount > 0)
        {
            status += $" {unrecognizedCount} newer or unknown device identifier(s) could not be matched.";
        }

        return new PrinterDiscoveryResult(
            targets,
            status,
            usesRegisteredDevices,
            registeredDevices.Count,
            Math.Max(0, unrecognizedCount));
    }

    private static List<PrinterTarget> BuildTargets(
        IReadOnlyList<ConfiguredModel> configuredModels,
        IReadOnlyList<MachineProfile> machineProfiles)
    {
        var targets = new List<PrinterTarget>();
        foreach (var configured in configuredModels
            .DistinctBy(model => model.Vendor + "|" + model.ModelName, StringComparer.OrdinalIgnoreCase))
        {
            var matching = machineProfiles
                .Where(machine => machine.Vendor.Equals(configured.Vendor, StringComparison.OrdinalIgnoreCase)
                    && machine.ModelName.Equals(configured.ModelName, StringComparison.OrdinalIgnoreCase)
                    && (configured.Nozzles.Count == 0 || configured.Nozzles.Contains(machine.Nozzle)))
                .OrderBy(machine => ParseNozzle(machine.Nozzle))
                .ToList();
            if (matching.Count == 0)
            {
                continue;
            }

            targets.Add(new PrinterTarget
            {
                Vendor = configured.Vendor,
                ModelName = configured.ModelName,
                ProfileSuffix = GetProfileSuffix(configured.Vendor, configured.ModelName),
                MachinePresetNames = matching.Select(machine => machine.PresetName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                NozzleSummary = string.Join(", ", matching.Select(machine => machine.Nozzle).Distinct().OrderBy(ParseNozzle))
            });
        }

        return targets.OrderBy(target => target.Vendor).ThenBy(target => target.ModelName).ToList();
    }

    private JsonObject? ReadConfig()
    {
        if (!File.Exists(_paths.ConfigPath))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(_paths.ConfigPath);
            var jsonText = text.Split("\n# MD5 checksum ", StringSplitOptions.None)[0].TrimEnd();
            return JsonNode.Parse(jsonText)?.AsObject();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<ConfiguredModel> ReadConfiguredModels(JsonObject? config)
    {
        var results = new List<ConfiguredModel>();
        var models = config?["models"]?.AsArray();
        if (models is null)
        {
            return results;
        }

        foreach (var node in models.OfType<JsonObject>())
        {
            var model = node["model"]?.GetValue<string>() ?? "";
            var vendor = node["vendor"]?.GetValue<string>() ?? "";
            var nozzles = (node["nozzle_diameter"]?.GetValue<string>() ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(vendor))
            {
                results.Add(new ConfiguredModel(vendor, model, nozzles));
            }
        }

        return results;
    }

    private static HashSet<string> ReadRegisteredDeviceIds(JsonObject? config)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddSectionKeys(config, "access_code", results);
        AddSectionKeys(config, "user_access_code", results);

        var lastSelected = config?["app"]?["user_last_selected_machine"]?.GetValue<string>();
        if (LooksLikeDeviceId(lastSelected))
        {
            results.Add(lastSelected!);
        }

        return results;
    }

    private static void AddSectionKeys(JsonObject? config, string sectionName, HashSet<string> results)
    {
        if (config?[sectionName] is not JsonObject section)
        {
            return;
        }

        foreach (var item in section)
        {
            if (LooksLikeDeviceId(item.Key))
            {
                results.Add(item.Key);
            }
        }
    }

    private static bool LooksLikeDeviceId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length >= 8
        && value.Take(3).All(char.IsLetterOrDigit);

    private static RegisteredModelResolution ResolveRegisteredModels(
        IReadOnlySet<string> deviceIds,
        IReadOnlyList<ConfiguredModel> configuredModels,
        IReadOnlyList<MachineProfile> machineProfiles)
    {
        var resolved = new List<ConfiguredModel>();
        var resolvedDevices = 0;
        foreach (var deviceId in deviceIds)
        {
            if (!SerialPrefixModels.TryGetValue(deviceId[..3], out var expectedModel))
            {
                continue;
            }

            var configured = configuredModels.FirstOrDefault(model =>
                model.Vendor.Equals("BBL", StringComparison.OrdinalIgnoreCase)
                && model.ModelName.Equals(expectedModel, StringComparison.OrdinalIgnoreCase));
            configured ??= ResolveP1Alternative(expectedModel, configuredModels);
            if (configured is not null)
            {
                resolved.Add(configured);
                resolvedDevices++;
                continue;
            }

            if (machineProfiles.Any(machine =>
                machine.Vendor.Equals("BBL", StringComparison.OrdinalIgnoreCase)
                && machine.ModelName.Equals(expectedModel, StringComparison.OrdinalIgnoreCase)))
            {
                resolved.Add(new ConfiguredModel("BBL", expectedModel, []));
                resolvedDevices++;
            }
        }

        return new RegisteredModelResolution(resolved, resolvedDevices);
    }

    private static ConfiguredModel? ResolveP1Alternative(
        string expectedModel,
        IReadOnlyList<ConfiguredModel> configuredModels)
    {
        if (!expectedModel.Equals("Bambu Lab P1P", StringComparison.OrdinalIgnoreCase)
            && !expectedModel.Equals("Bambu Lab P1S", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var alternatives = configuredModels
            .Where(model => model.Vendor.Equals("BBL", StringComparison.OrdinalIgnoreCase)
                && (model.ModelName.Equals("Bambu Lab P1P", StringComparison.OrdinalIgnoreCase)
                    || model.ModelName.Equals("Bambu Lab P1S", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return alternatives.Count == 1 ? alternatives[0] : null;
    }

    private List<MachineProfile> ReadMachineProfiles()
    {
        var results = new List<MachineProfile>();
        if (!Directory.Exists(_paths.RoamingProfileRoot))
        {
            return results;
        }

        foreach (var vendorFolder in Directory.EnumerateDirectories(_paths.RoamingProfileRoot))
        {
            var machineFolder = Path.Combine(vendorFolder, "machine");
            if (!Directory.Exists(machineFolder))
            {
                continue;
            }

            var vendor = Path.GetFileName(vendorFolder);
            foreach (var path in Directory.EnumerateFiles(machineFolder, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    var root = document.RootElement;
                    if (!root.TryGetProperty("type", out var type)
                        || !string.Equals(type.GetString(), "machine", StringComparison.OrdinalIgnoreCase)
                        || !root.TryGetProperty("instantiation", out var instantiation)
                        || !string.Equals(instantiation.GetString(), "true", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var name = root.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? "" : "";
                    var model = root.TryGetProperty("printer_model", out var modelValue) ? modelValue.GetString() ?? "" : "";
                    var nozzle = root.TryGetProperty("printer_variant", out var nozzleValue) ? nozzleValue.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(nozzle))
                    {
                        results.Add(new MachineProfile(vendor, model, nozzle, name));
                    }
                }
                catch (JsonException)
                {
                    // Ignore non-profile helper JSON files.
                }
            }
        }

        return results;
    }

    private static string GetProfileSuffix(string vendor, string model)
    {
        if (vendor.Equals("BBL", StringComparison.OrdinalIgnoreCase))
        {
            var shortModel = model.StartsWith("Bambu Lab ", StringComparison.OrdinalIgnoreCase)
                ? model["Bambu Lab ".Length..]
                : model;
            shortModel = shortModel switch
            {
                "X1 Carbon" => "X1C",
                "A1 mini" => "A1M",
                "H2D Pro" => "H2DP",
                _ => shortModel
            };
            return "BBL " + shortModel;
        }

        return $"{vendor} {model}".Trim();
    }

    private static decimal ParseNozzle(string value) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : decimal.MaxValue;

    private sealed record ConfiguredModel(string Vendor, string ModelName, HashSet<string> Nozzles);
    private sealed record MachineProfile(string Vendor, string ModelName, string Nozzle, string PresetName);
    private sealed record RegisteredModelResolution(List<ConfiguredModel> Models, int ResolvedDeviceCount);
}
