using System.IO;
using System.Text.Json;
using BambuFilamentImporter.Models;
using BambuFilamentImporter.Services;

namespace BambuFilamentImporter.Workspace;

public sealed class AdvancedImportPlan
{
    public required LoadedFilamentPackage Package { get; init; }
    public required ImportDestination Destination { get; init; }
    public required string Fingerprint { get; init; }
    public required List<PlannedChange> Changes { get; init; }
    public static AdvancedImportPlan Create(BambuPaths paths, LoadedFilamentPackage package, IReadOnlyCollection<PrinterTarget> printers, ImportDestination destination)
    {
        var before = WorkspaceContext.Fingerprint(paths);
        var current = new BambuLibraryScanner(paths).LoadCurrentFilaments();
        var copy = JsonSerializer.Deserialize<LoadedFilamentPackage>(JsonSerializer.Serialize(package))!;
        if (destination == ImportDestination.ProjectLibrary && copy.Manifest.PrinterNeutral)
            printers = printers.Select(p => new PrinterTarget { ModelName = p.ModelName, Vendor = p.Vendor,
                ProfileSuffix = p.ProfileSuffix + " (Custom)", MachinePresetNames = p.MachinePresetNames }).ToList();
        var expanded = PrinterProfileExpander.Expand(copy, printers, current, destination);
        var selected = expanded.Manifest.Profiles.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("No profile changes are selected. Choose profiles or skip duplicates again for this destination.");
        if (selected.Any(p => string.IsNullOrWhiteSpace(p.Name) || p.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidOperationException("Every profile needs a valid filename-safe name.");
        var names = current.Where(p => p.StorageKind == (destination == ImportDestination.ProjectLibrary ? FilamentStorageKind.UserPreset : FilamentStorageKind.SystemCatalog))
            .Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (destination == ImportDestination.ProjectLibrary && selected.Any(p => !p.Name.EndsWith("@base")
            && current.Any(c => c.StorageKind == FilamentStorageKind.SystemCatalog && c.Name == p.Name)))
            throw new InvalidOperationException("A custom preset name matches a system preset. Rename it before importing so Studio can display it.");
        if (before != WorkspaceContext.Fingerprint(paths)) throw new IOException("The library changed. Review the import again.");
        return new() { Package = expanded, Destination = destination, Fingerprint = before,
            Changes = selected.Select(p => new PlannedChange(p.Name, names.Contains(p.Name) ? "Replace existing profile and tuning" : "Add profile", destination.ToString())).ToList() };
    }
    public void Validate(WorkspaceContext workspace)
    {
        workspace.EnsureWritable();
        if (Fingerprint != WorkspaceContext.Fingerprint(workspace.Paths)) throw new IOException("The library changed after review. Review this import again.");
    }
    public InstallResult Apply(WorkspaceContext workspace)
    {
        Validate(workspace);
        return LibraryTransaction.Run(workspace, "advanced-import", () => new BambuInstaller(workspace.Paths,
            () => workspace.IsLive && BambuProcess.IsStudioRunning()).Install(Package, Destination, false));
    }
}
