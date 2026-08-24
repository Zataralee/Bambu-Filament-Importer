using System.IO;
using System.Text.Json.Nodes;

namespace BambuFilamentImporter.Services;

public sealed class BambuPaths
{
    public string RoamingRoot { get; }
    public string RoamingProfileRoot => Path.Combine(RoamingRoot, "system");
    public string UserRoot => Path.Combine(RoamingRoot, "user");
    public string ProgramProfileRoot { get; }
    public string ConfigPath => Path.Combine(RoamingRoot, "BambuStudio.conf");
    public string ActiveUserPresetFolder => Path.Combine(UserRoot, GetActivePresetFolderName());
    public string ActiveUserFilamentFolder => Path.Combine(ActiveUserPresetFolder, "filament");

    public BambuPaths(string? roamingRoot = null, string? programProfileRoot = null)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        RoamingRoot = roamingRoot ?? Path.Combine(appData, "BambuStudio");
        ProgramProfileRoot = programProfileRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Bambu Studio",
            "resources",
            "profiles");
    }

    private string GetActivePresetFolderName()
    {
        if (File.Exists(ConfigPath))
        {
            try
            {
                var text = File.ReadAllText(ConfigPath);
                var jsonText = text.Split("\n# MD5 checksum ", StringSplitOptions.None)[0].TrimEnd();
                var config = JsonNode.Parse(jsonText)?.AsObject();
                var folder = config?["app"]?["preset_folder"]?.GetValue<string>()
                    ?? config?["preset_folder"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    return folder;
                }
            }
            catch
            {
                // Fall through to the local folder discovery below.
            }
        }

        if (Directory.Exists(UserRoot))
        {
            var accountFolder = Directory.EnumerateDirectories(UserRoot)
                .Select(Path.GetFileName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)
                    && !name.Equals("default", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(accountFolder))
            {
                return accountFolder;
            }
        }

        return "default";
    }
}
