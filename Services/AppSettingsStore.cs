using System.IO;
using System.Text.Json;

namespace BambuFilamentImporter.Services;

public sealed class ImporterSettings
{
    public bool DarkMode { get; set; }
}

public static class AppSettingsStore
{
    private static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BambuFilamentImporter");
    private static readonly string SettingsPath = Path.Combine(SettingsFolder, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ImporterSettings Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<ImporterSettings>(File.ReadAllText(SettingsPath)) ?? new ImporterSettings()
                : new ImporterSettings();
        }
        catch (JsonException)
        {
            return new ImporterSettings();
        }
    }

    public static void Save(ImporterSettings settings)
    {
        Directory.CreateDirectory(SettingsFolder);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions) + Environment.NewLine);
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }
}
