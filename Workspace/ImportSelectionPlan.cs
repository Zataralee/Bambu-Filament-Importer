using BambuFilamentImporter.Models;
using BambuFilamentImporter.Services;

namespace BambuFilamentImporter.Workspace;

/// <summary>Use the grouped system catalog by default, or custom presets instead.</summary>
public sealed class ImportSelectionPlan
{
    public required IReadOnlyList<ImportPlan> Plans { get; init; }
    public bool HasChanges => Plans.Any(p => p.HasChanges);
    public bool IncludesSystem => Plans.Any(p => p.Destination != ImportDestination.ProjectLibrary);
    public IReadOnlyList<PlannedChange> Changes => Plans.SelectMany(p => p.Changes.Select(c => c with
    { Location = p.Destination == ImportDestination.ProjectLibrary ? "Custom Library" : "Bambu System Library" })).ToList();

    public static ImportSelectionPlan Create(BambuPaths paths, IReadOnlyCollection<ProductChoice> products,
        IReadOnlyCollection<PrinterTarget> printers, bool useCustom = false)
    {
        var before = WorkspaceContext.Fingerprint(paths);
        var plans = new List<ImportPlan> { useCustom
            ? ImportPlan.Create(paths, products, printers, ImportDestination.ProjectLibrary, customLibrary: true)
            : ImportPlan.Create(paths, products, printers, ImportDestination.Both) };
        if (plans.Any(p => p.Fingerprint != before) || before != WorkspaceContext.Fingerprint(paths))
            throw new System.IO.IOException("The library changed during review. Review again before installing.");
        return new ImportSelectionPlan { Plans = plans };
    }
    public InstallResult Apply(WorkspaceContext workspace)
    {
        workspace.EnsureWritable();
        if (!HasChanges) throw new InvalidOperationException("There are no changes to apply.");
        foreach (var plan in Plans) plan.ValidateCurrent(workspace);
        return LibraryTransaction.Run(workspace, "import", () =>
        {
            var combined = new InstallResult();
            var installer = new BambuInstaller(workspace.Paths, () => workspace.IsLive && BambuProcess.IsStudioRunning());
            foreach (var plan in Plans.Where(p => p.HasChanges))
            {
                var result = installer.Install(plan.Package, plan.Destination, false);
                combined.WrittenFiles.AddRange(result.WrittenFiles);
                combined.UserPresetEntriesWritten.AddRange(result.UserPresetEntriesWritten);
                combined.ProjectPresetEntriesAdded.AddRange(result.ProjectPresetEntriesAdded);
                combined.ManifestEntriesAdded.AddRange(result.ManifestEntriesAdded);
                combined.ManifestEntriesUpdated.AddRange(result.ManifestEntriesUpdated);
                combined.Backups.AddRange(result.Backups);
            }
            return combined;
        });
    }
}
