using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using BambuFilamentImporter.Models;

namespace BambuFilamentImporter.Services;

public static class ElevationService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static int RemoveWithElevation(BambuPaths paths, IReadOnlyCollection<CurrentFilamentEntry> entries)
    {
        if (IsAdministrator())
        {
            return new BambuLibraryEditor(paths).RemoveMany(entries);
        }

        var helperFolder = Path.Combine(Path.GetTempPath(), "BambuFilamentImporter");
        Directory.CreateDirectory(helperFolder);
        var operationId = Guid.NewGuid().ToString("N");
        var planPath = Path.Combine(helperFolder, $"remove-{operationId}.json");
        var resultPath = Path.Combine(helperFolder, $"remove-{operationId}.result.json");
        var plan = new ElevatedRemovalPlan
        {
            ResultPath = resultPath,
            Targets = entries.Select(ToTarget).ToList()
        };
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan, JsonOptions));

        try
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The importer executable path could not be determined.");
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--elevated-remove");
            startInfo.ArgumentList.Add(planPath);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The elevated removal helper could not be started.");
            process.WaitForExit();

            if (!File.Exists(resultPath))
            {
                throw new InvalidOperationException("The elevated removal helper did not return a result.");
            }

            var result = JsonSerializer.Deserialize<ElevatedRemovalResult>(File.ReadAllText(resultPath), JsonOptions)
                ?? throw new InvalidOperationException("The elevated removal result could not be read.");
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Message);
            }

            return result.RemovedCount;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Administrator approval was canceled. No profiles were removed.");
        }
        finally
        {
            TryDelete(planPath);
            TryDelete(resultPath);
        }
    }

    public static int ExecuteRemovalPlan(string planPath)
    {
        var plan = JsonSerializer.Deserialize<ElevatedRemovalPlan>(File.ReadAllText(planPath), JsonOptions)
            ?? throw new InvalidDataException("The elevated removal plan could not be read.");
        var paths = new BambuPaths();
        try
        {
            if (BambuProcess.IsStudioRunning())
            {
                throw new InvalidOperationException("Bambu Studio opened before removal completed. Close it and try again.");
            }

            var entries = plan.Targets.Select(target => ValidateAndCreateEntry(paths, target)).ToList();
            var removed = new BambuLibraryEditor(paths).RemoveMany(entries);
            WriteResult(plan.ResultPath, new ElevatedRemovalResult { Success = true, RemovedCount = removed, Message = "Removal complete." });
            return 0;
        }
        catch (Exception ex)
        {
            WriteResult(plan.ResultPath, new ElevatedRemovalResult { Success = false, Message = ex.Message });
            return 2;
        }
    }

    public static AmsFilamentIdRepairResult RepairAmsIdsWithElevation(BambuPaths paths)
    {
        var service = new AmsFilamentIdRepairService(paths);
        var audit = service.Audit();
        if (audit.ProgramFilesAffected == 0 || IsAdministrator())
        {
            return service.Repair();
        }

        var helperFolder = Path.Combine(Path.GetTempPath(), "BambuFilamentImporter");
        Directory.CreateDirectory(helperFolder);
        var resultPath = Path.Combine(helperFolder, $"ams-repair-{Guid.NewGuid():N}.result.json");

        try
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The importer executable path could not be determined.");
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--elevated-repair-ams-ids");
            startInfo.ArgumentList.Add(resultPath);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The elevated AMS repair helper could not be started.");
            process.WaitForExit();

            if (!File.Exists(resultPath))
            {
                throw new InvalidOperationException("The elevated AMS repair helper did not return a result.");
            }

            var result = JsonSerializer.Deserialize<ElevatedAmsRepairResult>(File.ReadAllText(resultPath), JsonOptions)
                ?? throw new InvalidOperationException("The elevated AMS repair result could not be read.");
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Message);
            }

            return result.Result;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Administrator approval was canceled. No AMS filament IDs were changed.");
        }
        finally
        {
            TryDelete(resultPath);
        }
    }

    public static int ExecuteAmsIdRepair(string resultPath)
    {
        try
        {
            if (BambuProcess.IsStudioRunning())
            {
                throw new InvalidOperationException("Bambu Studio opened before AMS ID repair completed. Close it and try again.");
            }

            var result = new AmsFilamentIdRepairService(new BambuPaths()).Repair();
            WriteResult(resultPath, new ElevatedAmsRepairResult
            {
                Success = true,
                Message = "AMS filament ID repair complete.",
                Result = result
            });
            return 0;
        }
        catch (Exception ex)
        {
            WriteResult(resultPath, new ElevatedAmsRepairResult { Success = false, Message = ex.Message });
            return 2;
        }
    }

    public static List<CurrentFilamentEntry> ExpandPhysicalCopies(IEnumerable<CurrentFilamentEntry> logicalEntries)
    {
        var result = new List<CurrentFilamentEntry>();
        foreach (var entry in logicalEntries.Where(entry => entry.CanEdit))
        {
            result.Add(ClonePhysical(entry, entry.ProfileRoot, entry.ProfilePath, entry.RelativePath, entry.InfoPath, entry.Source, entry.StorageKind));
            foreach (var copy in entry.AdditionalCopies)
            {
                result.Add(ClonePhysical(entry, copy.ProfileRoot, copy.ProfilePath, copy.RelativePath, copy.InfoPath, copy.Source, copy.StorageKind));
            }
        }

        return result.DistinctBy(entry => entry.ProfilePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static CurrentFilamentEntry ClonePhysical(
        CurrentFilamentEntry source,
        string profileRoot,
        string profilePath,
        string relativePath,
        string infoPath,
        string sourceLabel,
        FilamentStorageKind storageKind) => new()
        {
            Name = source.Name,
            OriginalName = source.OriginalName,
            VendorGroup = source.VendorGroup,
            MaterialFamily = source.MaterialFamily,
            Source = sourceLabel,
            Location = source.Location,
            RelativePath = relativePath,
            ProfileRoot = profileRoot,
            ProfilePath = profilePath,
            InfoPath = infoPath,
            IsProjectPreset = source.IsProjectPreset,
            CanEdit = true,
            StorageKind = storageKind
        };

    private static ElevatedRemovalTarget ToTarget(CurrentFilamentEntry entry) => new()
    {
        Name = entry.Name,
        RelativePath = entry.RelativePath,
        ProfileRoot = entry.ProfileRoot,
        ProfilePath = entry.ProfilePath,
        InfoPath = entry.InfoPath,
        StorageKind = entry.StorageKind
    };

    private static CurrentFilamentEntry ValidateAndCreateEntry(BambuPaths paths, ElevatedRemovalTarget target)
    {
        var profilePath = Path.GetFullPath(target.ProfilePath);
        var roamingFilaments = Path.GetFullPath(Path.Combine(paths.RoamingProfileRoot, "BBL", "filament"));
        var programFilaments = Path.GetFullPath(Path.Combine(paths.ProgramProfileRoot, "BBL", "filament"));
        var userRoot = Path.GetFullPath(paths.UserRoot);
        var allowed = IsWithin(profilePath, roamingFilaments)
            || IsWithin(profilePath, programFilaments)
            || IsWithin(profilePath, userRoot);
        if (!allowed || !profilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The removal plan contains a profile outside Bambu Studio's filament folders.");
        }

        return new CurrentFilamentEntry
        {
            Name = target.Name,
            OriginalName = target.Name,
            RelativePath = target.RelativePath,
            ProfileRoot = Path.GetFullPath(target.ProfileRoot),
            ProfilePath = profilePath,
            InfoPath = string.IsNullOrWhiteSpace(target.InfoPath) ? "" : Path.GetFullPath(target.InfoPath),
            CanEdit = true,
            StorageKind = target.StorageKind
        };
    }

    private static bool IsWithin(string path, string root)
    {
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteResult(string path, ElevatedRemovalResult result)
    {
        var resultPath = Path.GetFullPath(path);
        var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BambuFilamentImporter"));
        if (!IsWithin(resultPath, allowedRoot))
        {
            throw new InvalidDataException("The elevated result path is outside the importer temporary folder.");
        }

        File.WriteAllText(resultPath, JsonSerializer.Serialize(result, JsonOptions));
    }

    private static void WriteResult(string path, ElevatedAmsRepairResult result)
    {
        var resultPath = Path.GetFullPath(path);
        var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BambuFilamentImporter"));
        if (!IsWithin(resultPath, allowedRoot))
        {
            throw new InvalidDataException("The elevated result path is outside the importer temporary folder.");
        }

        File.WriteAllText(resultPath, JsonSerializer.Serialize(result, JsonOptions));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Stale operation files contain no filament data and can be overwritten later.
        }
    }

    private sealed class ElevatedRemovalPlan
    {
        public string ResultPath { get; set; } = "";
        public List<ElevatedRemovalTarget> Targets { get; set; } = [];
    }

    private sealed class ElevatedRemovalTarget
    {
        public string Name { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public string ProfileRoot { get; set; } = "";
        public string ProfilePath { get; set; } = "";
        public string InfoPath { get; set; } = "";
        public FilamentStorageKind StorageKind { get; set; }
    }

    private sealed class ElevatedRemovalResult
    {
        public bool Success { get; set; }
        public int RemovedCount { get; set; }
        public string Message { get; set; } = "";
    }

    private sealed class ElevatedAmsRepairResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public AmsFilamentIdRepairResult Result { get; set; } = new();
    }
}
