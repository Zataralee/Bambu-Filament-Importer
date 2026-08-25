using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BambuFilamentImporter.Services;

public sealed class AmsFilamentIdRepairService
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly BambuPaths _paths;
    private readonly Func<bool> _isStudioRunning;

    public AmsFilamentIdRepairService(BambuPaths paths, Func<bool>? isStudioRunning = null)
    {
        _paths = paths;
        _isStudioRunning = isStudioRunning ?? BambuProcess.IsStudioRunning;
    }

    public AmsFilamentIdAudit Audit()
    {
        var plan = BuildPlan();
        return new AmsFilamentIdAudit
        {
            AffectedProducts = plan.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            AffectedFiles = plan.Sum(item => item.Files.Count),
            ProgramFilesAffected = plan.Sum(item => item.Files.Count(file => IsWithin(file.Path, _paths.ProgramProfileRoot))),
            Changes = plan.Select(item => new AmsFilamentIdChange
            {
                Name = item.Name,
                CurrentId = string.Join(", ", item.Files.Select(file => file.Id).Distinct(StringComparer.OrdinalIgnoreCase)),
                NewId = item.NewId,
                FileCount = item.Files.Count
            }).ToList()
        };
    }

    public AmsFilamentIdRepairResult Repair()
    {
        if (_isStudioRunning())
        {
            throw new InvalidOperationException("Bambu Studio is open. Close it before repairing AMS filament IDs.");
        }

        var plan = BuildPlan();
        var result = new AmsFilamentIdRepairResult();
        foreach (var item in plan)
        {
            foreach (var file in item.Files)
            {
                var profile = JsonNode.Parse(File.ReadAllText(file.Path))?.AsObject()
                    ?? throw new InvalidDataException($"Profile JSON could not be parsed: {file.Path}");
                var backup = FileBackup.Create(file.Path, "bflib-ams-id-backup");
                if (!string.IsNullOrWhiteSpace(backup))
                {
                    result.BackupFiles.Add(backup);
                }

                profile["filament_id"] = item.NewId;
                File.WriteAllText(file.Path, profile.ToJsonString(WriteOptions) + Environment.NewLine);
                result.ChangedFiles++;
            }

            result.RepairedProducts++;
            result.Changes.Add(new AmsFilamentIdChange
            {
                Name = item.Name,
                CurrentId = string.Join(", ", item.Files.Select(file => file.Id).Distinct(StringComparer.OrdinalIgnoreCase)),
                NewId = item.NewId,
                FileCount = item.Files.Count
            });
        }

        return result;
    }

    private List<RepairPlanItem> BuildPlan()
    {
        var files = LoadBaseProfiles();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<RepairPlanItem>();
        var groups = files
            .GroupBy(file => file.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var preservedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var idGroup in groups
            .Select(group => new
            {
                Group = group,
                Ids = group.Select(file => file.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            })
            .Where(item => item.Ids.Count == 1 && item.Ids[0].Length <= AmsFilamentId.MaximumLength)
            .GroupBy(item => item.Ids[0], StringComparer.OrdinalIgnoreCase))
        {
            var owner = idGroup.First();
            preservedGroups.Add(owner.Group.Key);
            usedIds.Add(owner.Ids[0]);
        }

        foreach (var group in groups)
        {
            var copies = group
                .OrderBy(file => file.IsProgramCopy)
                .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var displayName = copies[0].Name;
            var preferred = copies
                .Select(file => file.Id)
                .OrderBy(id => id.Length > AmsFilamentId.MaximumLength)
                .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
                .First();
            var assigned = preservedGroups.Contains(group.Key)
                ? preferred
                : AmsFilamentId.Assign(preferred, displayName, usedIds);
            if (copies.All(file => file.Id.Equals(assigned, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(new RepairPlanItem(displayName, assigned, copies));
        }

        return result;
    }

    private List<ProfileFile> LoadBaseProfiles()
    {
        var results = new List<ProfileFile>();
        foreach (var root in new[] { _paths.RoamingProfileRoot, _paths.ProgramProfileRoot }
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var filamentRoot = Path.Combine(root, "BBL", "filament");
            if (!Directory.Exists(filamentRoot))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(filamentRoot, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var profile = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
                    var name = profile?["name"]?.GetValue<string>() ?? "";
                    var id = profile is null ? null : AmsFilamentId.Read(profile);
                    if (!name.EndsWith("@base", StringComparison.OrdinalIgnoreCase)
                        || string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    var key = Path.GetRelativePath(filamentRoot, path).Replace('\\', '/');
                    results.Add(new ProfileFile(
                        name,
                        id,
                        path,
                        key,
                        IsWithin(path, _paths.ProgramProfileRoot)));
                }
                catch (JsonException)
                {
                    continue;
                }
            }
        }

        return results;
    }

    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ProfileFile(string Name, string Id, string Path, string Key, bool IsProgramCopy);
    private sealed record RepairPlanItem(string Name, string NewId, List<ProfileFile> Files);
}

public sealed class AmsFilamentIdAudit
{
    public int AffectedProducts { get; set; }
    public int AffectedFiles { get; set; }
    public int ProgramFilesAffected { get; set; }
    public List<AmsFilamentIdChange> Changes { get; set; } = [];
}

public sealed class AmsFilamentIdRepairResult
{
    public int RepairedProducts { get; set; }
    public int ChangedFiles { get; set; }
    public List<string> BackupFiles { get; set; } = [];
    public List<AmsFilamentIdChange> Changes { get; set; } = [];
}

public sealed class AmsFilamentIdChange
{
    public string Name { get; set; } = "";
    public string CurrentId { get; set; } = "";
    public string NewId { get; set; } = "";
    public int FileCount { get; set; }
}
