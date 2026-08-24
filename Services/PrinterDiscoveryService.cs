using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using BambuFilamentImporter.Models;

namespace BambuFilamentImporter.Services;

public sealed class PrinterDiscoveryService
{
    private readonly BambuPaths _paths;

    public PrinterDiscoveryService(BambuPaths paths)
    {
        _paths = paths;
    }

    public List<PrinterTarget> DiscoverConfiguredPrinters()
    {
        var configuredModels = ReadConfiguredModels();
        var machineProfiles = ReadMachineProfiles();
        var targets = new List<PrinterTarget>();

        foreach (var configured in configuredModels)
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

        if (targets.Count == 0)
        {
            targets.AddRange(machineProfiles
                .GroupBy(machine => new { machine.Vendor, machine.ModelName })
                .Select(group => new PrinterTarget
                {
                    Vendor = group.Key.Vendor,
                    ModelName = group.Key.ModelName,
                    ProfileSuffix = GetProfileSuffix(group.Key.Vendor, group.Key.ModelName),
                    MachinePresetNames = group.Select(machine => machine.PresetName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    NozzleSummary = string.Join(", ", group.Select(machine => machine.Nozzle).Distinct().OrderBy(ParseNozzle))
                }));
        }

        return targets.OrderBy(target => target.Vendor).ThenBy(target => target.ModelName).ToList();
    }

    private List<ConfiguredModel> ReadConfiguredModels()
    {
        var results = new List<ConfiguredModel>();
        if (!File.Exists(_paths.ConfigPath))
        {
            return results;
        }

        try
        {
            var text = File.ReadAllText(_paths.ConfigPath);
            var jsonText = text.Split("\n# MD5 checksum ", StringSplitOptions.None)[0].TrimEnd();
            var models = JsonNode.Parse(jsonText)?["models"]?.AsArray();
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
        }
        catch (JsonException)
        {
            // Machine profile discovery below remains available as a fallback.
        }

        return results;
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
}
