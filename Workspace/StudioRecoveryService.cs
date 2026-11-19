using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BambuFilamentImporter.Models;
using BambuFilamentImporter.Services;

namespace BambuFilamentImporter.Workspace;

/// <summary>Retains a known-good imported catalog across Studio upgrades.</summary>
public sealed class StudioRecoveryService
{
    private readonly WorkspaceContext _workspace;
    private readonly Func<string?> _version;
    private string Folder => Path.Combine(_workspace.BackupFolder, "StudioRecovery");
    private string StatePath => Path.Combine(Folder, "state.json");
    public StudioRecoveryService(WorkspaceContext workspace, Func<string?>? version = null)
    { _workspace = workspace; _version = version ?? (() => DetectVersion(workspace.Paths)); }

    public static string? DetectVersion(BambuPaths paths)
    {
        var install = Directory.GetParent(paths.ProgramProfileRoot)?.Parent;
        var executable = install is null ? "" : Path.Combine(install.FullName, "bambu-studio.exe");
        if (!File.Exists(executable)) return null;
        var info = FileVersionInfo.GetVersionInfo(executable);
        // Studio's ProductVersion can be blank; the numeric file version is reliable.
        return info.FileMajorPart > 0 ? $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}" : null;
    }

    public StudioRecoveryState? ReadState() => File.Exists(StatePath)
        ? JsonSerializer.Deserialize<StudioRecoveryState>(File.ReadAllText(StatePath))
            ?? throw new InvalidDataException("The Studio recovery record is unreadable. Existing backups have been kept.")
        : null;

    public StudioRecoveryOffer? Check()
    {
        var version = _version();
        var state = ReadState();
        return version is not null && state is not null && version != state.StudioVersion && state.ProfileNames.Count > 0
            ? new StudioRecoveryOffer(state, version) : null;
    }

    public bool Capture(bool explicitChange = false, bool acceptVersionChange = false)
    {
        var version = _version();
        if (version is null) return false; // Never replace a known version with an unknown value.
        var previous = ReadState();
        if (!acceptVersionChange && previous is not null && previous.StudioVersion != version && previous.ProfileNames.Count > 0)
            return false; // The pre-upgrade backup must survive a declined/deferred restore.
        _workspace.EnsureWritable();
        var paths = _workspace.Paths;
        var stockManifest = Path.Combine(paths.ProgramProfileRoot, "BBL.json");
        if (!File.Exists(stockManifest)) throw new IOException("Studio's installed catalog was not found. Recovery backup was not replaced.");
        var stockNames = BambuJson.Parse(File.ReadAllText(stockManifest))!["filament_list"]!.AsArray()
            .Select(n => n!["name"]!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Include legacy imports as well as new BFI imports, without rolling back bundled presets.
        var profiles = new BambuLibraryScanner(paths).LoadCurrentFilaments()
            .Where(p => p.StorageKind == FilamentStorageKind.SystemCatalog && p.CanEdit && !stockNames.Contains(p.Name))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var names = profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!explicitChange && previous is not null && previous.ProfileNames.Any(n => !names.Contains(n))) return false;
        if (profiles.Any(p => !File.Exists(p.ProfilePath))) throw new IOException("An imported profile is missing. The previous recovery backup has been kept.");
        BambuCatalogIntegrity.ValidateProfileRoot(paths.RoamingProfileRoot);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var profile in profiles)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(profile.Name + "\n" + profile.RelativePath));
            hash.AppendData(File.ReadAllBytes(profile.ProfilePath));
        }
        var configBytes = File.ReadAllBytes(paths.ConfigPath);
        hash.AppendData(configBytes);
        var fingerprint = Convert.ToHexString(hash.GetHashAndReset());
        if (previous?.Fingerprint == fingerprint && previous.StudioVersion == version && File.Exists(BackupPath(previous))) return true;

        Directory.CreateDirectory(Folder);
        var stem = $"Studio-{version}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var backup = Path.Combine(Folder, stem + ".bflbackup");
        new LibraryBackupService(paths, () => _workspace.IsLive && BambuProcess.IsStudioRunning()).Create(backup, names);
        // Retain the full config for manual recovery; automatic restore merges only filament registrations.
        File.WriteAllBytes(Path.Combine(Folder, stem + ".conf"), configBytes);
        _workspace.EnsureWritable();
        if (_version() != version) throw new IOException("Studio changed during backup. The previous recovery point has been kept.");
        var state = new StudioRecoveryState { StudioVersion = version, BackupFile = Path.GetFileName(backup),
            Fingerprint = fingerprint, ProfileNames = names.ToList(), CreatedUtc = DateTime.UtcNow };
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, StatePath, true);
        return true;
    }

    public LibraryRestoreResult Restore(StudioRecoveryOffer offer)
    {
        _workspace.EnsureWritable();
        if (ReadState()?.BackupFile != offer.Saved.BackupFile || _version() != offer.CurrentVersion)
            throw new IOException("The Studio recovery state changed. Check for recovery again.");
        var result = LibraryTransaction.Run(_workspace, "studio-update-restore", () =>
        {
            var restored = new LibraryBackupService(_workspace.Paths, () => _workspace.IsLive && BambuProcess.IsStudioRunning())
                .Restore(BackupPath(offer.Saved));
            if (_version() != offer.CurrentVersion) throw new IOException("Studio changed during restoration. Try again when its update is finished.");
            return restored;
        });
        Capture(explicitChange: true, acceptVersionChange: true);
        return result;
    }

    private string BackupPath(StudioRecoveryState state)
    {
        if (string.IsNullOrWhiteSpace(state.BackupFile) || Path.GetFileName(state.BackupFile) != state.BackupFile)
            throw new InvalidDataException("Invalid Studio recovery backup name.");
        return Path.Combine(Folder, state.BackupFile);
    }
}

public sealed class StudioRecoveryState
{
    public string StudioVersion { get; set; } = "";
    public string BackupFile { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public List<string> ProfileNames { get; set; } = [];
    public DateTime CreatedUtc { get; set; }
}
public sealed record StudioRecoveryOffer(StudioRecoveryState Saved, string CurrentVersion);
