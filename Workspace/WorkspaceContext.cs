using System.IO;
using System.Security.Cryptography;
using BambuFilamentImporter.Services;

namespace BambuFilamentImporter.Workspace;

public sealed class WorkspaceContext
{
    public string Root { get; }
    public bool IsPreview { get; }
    public string LibraryFolder => IsPreview ? Path.Combine(Root, "Libraries") : ManufacturerLibraryStore.ManagedDirectory;
    public string CatalogStatePath => IsPreview ? Path.Combine(Root, "library-catalog-state.json") : ManufacturerLibraryCatalogStateStore.StatePath;
    public string BackupFolder => Path.Combine(Root, "Backups");
    public BambuPaths Paths { get; private set; }
    public bool IsLive { get; private set; }
    public WorkspaceContext(string? root = null, bool preview = false)
    {
        IsPreview = preview || root is not null;
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BambuFilamentImporter", IsPreview ? "RedesignPreview" : "Workspace");
        IsLive = !IsPreview;
        Paths = IsLive ? new BambuPaths() : SandboxPaths();
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(BackupFolder);
    }
    private BambuPaths SandboxPaths() => new(Path.Combine(Root, "TestLibrary"), Path.Combine(Root, "ReadOnlyProgramCatalog"));
    public void Initialize()
    {
        Directory.CreateDirectory(LibraryFolder);
        if (!IsPreview)
        {
            ManufacturerLibraryStore.MigrateLegacyLibraries();
            return;
        }
        // The local preview includes libraries for offline testing. Existing downloads win.
        foreach (var folder in new[] { ManufacturerLibraryStore.ManagedDirectory, Path.Combine(AppContext.BaseDirectory, "Libraries") })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var source in Directory.EnumerateFiles(folder, "*.bflib"))
            {
                var target = Path.Combine(LibraryFolder, Path.GetFileName(source));
                if (!File.Exists(target)) File.Copy(source, target);
            }
        }
        if (!File.Exists(Path.Combine(SandboxPaths().RoamingProfileRoot, "BBL.json"))) ResetTestCopy();
    }
    public void SetLive(bool live)
    {
        if (!live && !File.Exists(Path.Combine(SandboxPaths().RoamingProfileRoot, "BBL.json")))
        {
            var previous = IsLive;
            IsLive = false;
            try { ResetTestCopy(); }
            finally { IsLive = previous; }
        }
        IsLive = live;
        Paths = live ? new BambuPaths() : SandboxPaths();
    }
    public void EnsureWritable()
    {
        if (!IsLive) return;
        if (BambuProcess.IsStudioRunning()) throw new InvalidOperationException("Close Bambu Studio before applying changes. Your selection is preserved.");
        if (IsAnotherWorkspaceRunning()) throw new InvalidOperationException("Close other BFI windows, including the preview, before changing your live library.");
    }
    public static bool IsAnotherWorkspaceRunning()
    {
        if (BambuProcess.IsAnotherImporterRunning()) return true;
        var previewProcesses = System.Diagnostics.Process.GetProcessesByName("BambuFilamentImporter.Preview");
        try
        {
            return previewProcesses.Any(p => p.Id != Environment.ProcessId);
        }
        finally { foreach (var process in previewProcesses) process.Dispose(); }
    }
    public void ResetTestCopy()
    {
        if (IsLive) throw new InvalidOperationException("Switch to Test copy before resetting it.");
        var source = new BambuPaths();
        if (BambuProcess.IsStudioRunning()) throw new InvalidOperationException("Close Bambu Studio once to create a consistent test copy. You can reopen it afterward.");
        var destination = SandboxPaths();
        var stage = Path.Combine(Root, "Snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        CopyTree(source.RoamingProfileRoot, Path.Combine(stage, "system"));
        CopyTree(source.UserRoot, Path.Combine(stage, "user"));
        if (!File.Exists(source.ConfigPath)) throw new FileNotFoundException("Bambu Studio configuration was not found.", source.ConfigPath);
        File.Copy(source.ConfigPath, Path.Combine(stage, "BambuStudio.conf"));
        BambuCatalogIntegrity.ValidateProfileRoot(Path.Combine(stage, "system"));
        if (Directory.Exists(destination.RoamingRoot))
        {
            // Preserve previous test data rather than recursively deleting it.
            var archive = Path.Combine(Root, "PreviousTestCopies", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            Directory.Move(destination.RoamingRoot, archive);
        }
        Directory.Move(stage, destination.RoamingRoot);
        Paths = destination;
    }
    private static void CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            // Exclude historical backups, cloud/cache data and unrelated binary assets.
            if (Path.GetExtension(file) is not (".json" or ".info")) continue;
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
    public string CreateBackup(string reason)
    {
        EnsureWritable();
        var path = Path.Combine(BackupFolder, $"{(IsLive ? "Live" : "Test")}-{DateTime.Now:yyyyMMdd-HHmmss}-{reason}-{Guid.NewGuid().ToString("N")[..6]}.bflbackup");
        new LibraryBackupService(Paths, () => IsLive && BambuProcess.IsStudioRunning()).Create(path);
        return path;
    }
    public static string Fingerprint(BambuPaths paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = new[] { Path.Combine(paths.RoamingProfileRoot, "BBL.json"), paths.ConfigPath }
            .Concat(Directory.Exists(Path.Combine(paths.RoamingProfileRoot, "BBL", "filament"))
                ? Directory.EnumerateFiles(Path.Combine(paths.RoamingProfileRoot, "BBL", "filament"), "*.json", SearchOption.AllDirectories) : [])
            .Concat(Directory.Exists(paths.UserRoot) ? Directory.EnumerateFiles(paths.UserRoot, "*.json", SearchOption.AllDirectories) : [])
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(file));
            if (File.Exists(file)) hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
