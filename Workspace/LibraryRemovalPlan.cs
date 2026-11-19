using System.IO;
using BambuFilamentImporter.Models;
using BambuFilamentImporter.Services;

namespace BambuFilamentImporter.Workspace;

public sealed class LibraryRemovalPlan
{
    public IReadOnlyList<CurrentFilamentEntry> Profiles { get; }
    private readonly string _fingerprint;
    public LibraryRemovalPlan(BambuPaths paths, IEnumerable<CurrentFilamentEntry> profiles)
    {
        Profiles = profiles.DistinctBy(p => p.ProfilePath, StringComparer.OrdinalIgnoreCase).ToList();
        if (Profiles.Count == 0 || Profiles.Any(p => !p.CanEdit)
            || Profiles.Select(p => p.StorageKind).Distinct().Count() != 1)
            throw new InvalidOperationException("Select a filament, type, or brand within one editable library.");
        _fingerprint = WorkspaceContext.Fingerprint(paths);
        foreach (var profile in Profiles)
            if (!File.Exists(profile.ProfilePath) || BambuJson.Parse(File.ReadAllText(profile.ProfilePath))?["name"]?.GetValue<string>() != profile.Name)
                throw new IOException("The library changed. Refresh it before deleting this selection.");
    }
    public int Apply(WorkspaceContext workspace)
    {
        workspace.EnsureWritable();
        if (_fingerprint != WorkspaceContext.Fingerprint(workspace.Paths))
            throw new IOException("The library changed after deletion was reviewed. Select it again before deleting.");
        return LibraryTransaction.Run(workspace, "delete-selection", () => new BambuLibraryEditor(workspace.Paths).RemoveMany(Profiles));
    }
}
