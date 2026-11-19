using System.IO;
using System.Windows;
using System.Windows.Controls;
using BambuFilamentImporter.Models;
using BambuFilamentImporter.Services;
using Microsoft.Win32;

namespace BambuFilamentImporter.Workspace;

public partial class WorkspaceWindow
{
    private LoadedFilamentPackage? _advancedPackage;
    private AdvancedImportPlan? _advancedPlan;
    private void DismissAppNotice_Click(object sender, RoutedEventArgs e) => AppUpdateBanner.Visibility = Visibility.Collapsed;
    private void DismissLibraryNotice_Click(object sender, RoutedEventArgs e) => LibraryUpdateBanner.Visibility = Visibility.Collapsed;
    private void InvalidateAdvancedPlan()
    {
        _advancedPlan = null;
        if (AdvancedImportReview is not null) AdvancedImportReview.Visibility = Visibility.Collapsed;
    }
    private void UpdateToolControls(bool canWrite)
    {
        if (AdvancedInstallButton is null) return;
        AdvancedInstallButton.IsEnabled = canWrite && _advancedPlan is not null;
        MirrorInstallButton.IsEnabled = canWrite && _workspace.IsLive && _advancedPlan?.Destination is ImportDestination.Both or ImportDestination.DeviceAms;
        AdvancedReviewButton.IsEnabled = !_busy && _advancedPackage is not null;
        RenameProfileButton.IsEnabled = AdvancedRemoveProfileButton.IsEnabled = canWrite && _editing is not null;
        RenameBrandButton.IsEnabled = AdvancedDeleteButton.IsEnabled = DeleteSelectionButton.IsEnabled;
        DeleteInstalledCopiesButton.IsEnabled = canWrite && _workspace.IsLive && FilamentTree.DeletionProfiles(LibraryTree.SelectedItem as FilamentTreeNode)
            .Any(p => p.AdditionalCopies.Any(c => c.ProfileRoot.Equals(_workspace.Paths.ProgramProfileRoot, StringComparison.OrdinalIgnoreCase)));
        RepairAmsButton.IsEnabled = canWrite;
        RepairAmsAdminButton.IsEnabled = canWrite && _workspace.IsLive;
    }
    private void ShowImportTools_Click(object sender, RoutedEventArgs e)
    {
        if (!CanLeaveEditor()) return;
        Navigation.SelectedIndex = 0;
        BrowsePanel.Visibility = ReviewPanel.Visibility = SuccessPanel.Visibility = Visibility.Collapsed;
        AdvancedImportPanel.Visibility = Visibility.Visible;
    }
    private void HideImportTools_Click(object sender, RoutedEventArgs e)
    { AdvancedImportPanel.Visibility = Visibility.Collapsed; Back_Click(sender, e); }
    private void ShowLibraryTools_Click(object sender, RoutedEventArgs e)
    {
        if (!CanLeaveEditor()) return;
        LibraryMainPanel.Visibility = LibraryHeadingPanel.Visibility = Visibility.Collapsed;
        AdvancedLibraryPanel.Visibility = Visibility.Visible;
        var node = LibraryTree.SelectedItem as FilamentTreeNode;
        AdvancedSelectionText.Text = node is null ? "Select a filament, type, or brand in My Library to rename or delete it. Library repairs are available below." : "Selected: " + node.Name + " · " + DeleteSelectionHint.Text;
        AdvancedProfileName.Text = _editing?.Name ?? "";
        AdvancedProfileBrand.Text = _editing?.VendorGroup ?? "";
        AdvancedBrandName.Text = FilamentTree.DeletionProfiles(node).FirstOrDefault()?.VendorGroup ?? "";
        UpdateControls();
    }
    private void HideLibraryTools_Click(object sender, RoutedEventArgs e)
    { AdvancedLibraryPanel.Visibility = Visibility.Collapsed; LibraryMainPanel.Visibility = LibraryHeadingPanel.Visibility = Visibility.Visible; }
    private async void RefreshLibrary_Click(object sender, RoutedEventArgs e)
    { if (CanLeaveEditor()) await RunAsync("Refreshing library…", ReloadAsync); }
    private void SelectAllPrinters_Click(object sender, RoutedEventArgs e) => SetAllPrinters(true);
    private void ClearPrinters_Click(object sender, RoutedEventArgs e) => SetAllPrinters(false);
    private void SetAllPrinters(bool selected)
    {
        _suppress = true;
        foreach (var printer in _printers) { printer.IsSelected = selected; foreach (var nozzle in printer.Nozzles) nozzle.IsSelected = selected; }
        _suppress = false; _plan = null; InvalidateAdvancedPlan(); UpdateControls();
    }
    private async void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null || !CanLeaveEditor()) return;
        var entry = _editing; var name = AdvancedProfileName.Text.Trim(); var brand = AdvancedProfileBrand.Text.Trim();
        if (MessageBox.Show(this, $"Save profile identity?\n\n{entry.Name} → {name}\nBrand: {brand}\n\nDependent profiles and library registrations will follow a name change. Shared-base settings are inherited by child profiles.", "Save profile identity", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunAsync("Renaming profile…", async () =>
        {
            CheckEditCurrent();
            await Task.Run(() => LibraryTransaction.Run(_workspace, "rename-profile", () => { new BambuLibraryEditor(_workspace.Paths).Save(entry, name, brand); return true; }));
            await ReloadAsync(); HideLibraryTools_Click(this, new()); Log("Profile renamed. Backup saved in Maintenance.");
        });
    }
    private async void RenameBrand_Click(object sender, RoutedEventArgs e)
    {
        var profiles = FilamentTree.DeletionProfiles(LibraryTree.SelectedItem as FilamentTreeNode);
        if (profiles.Count == 0 || !CanLeaveEditor()) return;
        var name = AdvancedBrandName.Text.Trim();
        var before = WorkspaceContext.Fingerprint(_workspace.Paths);
        if (MessageBox.Show(this, $"Change the brand to {name} on {profiles.Count} selected profiles? Filament names stay as they are.", "Change brand", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunAsync("Changing brand…", async () =>
        {
            if (before != WorkspaceContext.Fingerprint(_workspace.Paths)) throw new IOException("The library changed. Select the profiles again.");
            await Task.Run(() => LibraryTransaction.Run(_workspace, "rename-brand", () => new BambuLibraryEditor(_workspace.Paths).SetManufacturer(profiles, name)));
            await ReloadAsync(); HideLibraryTools_Click(this, new()); Log("Brand changed for the selected profiles. Backup saved in Maintenance.");
        });
    }
    private async void DeleteInstalledCopies_Click(object sender, RoutedEventArgs e)
    {
        if (!_workspace.IsLive || !CanLeaveEditor()) return;
        var profiles = ElevationService.ExpandPhysicalCopies(FilamentTree.DeletionProfiles(LibraryTree.SelectedItem as FilamentTreeNode));
        if (profiles.Count == 0) return;
        await RunAsync("Reviewing installed copies…", async () =>
        {
            _workspace.EnsureWritable();
            if (MessageBox.Show(this, $"Delete {profiles.Count} profile files, including matching installed Program Files copies? Windows may request administrator approval. Backups are retained.", "Delete installed copies", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await Task.Run(() => { _workspace.CreateBackup("delete-installed"); ElevationService.RemoveWithElevation(_workspace.Paths, profiles); new StudioRecoveryService(_workspace).Capture(explicitChange: true); });
            await ReloadAsync(); HideLibraryTools_Click(this, new()); Log("Selected profiles and installed copies removed.");
        });
    }
    private async void RepairAms_Click(object sender, RoutedEventArgs e) => await RepairAmsAsync(false);
    private async void RepairAmsAdmin_Click(object sender, RoutedEventArgs e) => await RepairAmsAsync(true);
    private async Task RepairAmsAsync(bool allowAdministrator)
    {
        if (!CanLeaveEditor()) return;
        await RunAsync("Checking AMS IDs…", async () =>
        {
            _workspace.EnsureWritable();
            var service = new AmsFilamentIdRepairService(_workspace.Paths, () => _workspace.IsLive && BambuProcess.IsStudioRunning());
            var audit = await Task.Run(service.Audit);
            LibraryRepairStatus.Text = $"{audit.AffectedProducts} products · {audit.AffectedFiles} files need repair.";
            if (audit.AffectedFiles == 0) { Log("AMS IDs are healthy."); return; }
            if (audit.ProgramFilesAffected > 0 && !allowAdministrator)
            { RepairAmsAdminButton.Visibility = Visibility.Visible; LibraryRepairStatus.Text += " Use the administrator button to include installed copies."; return; }
            if (allowAdministrator && !_workspace.IsLive) return;
            if (MessageBox.Show(this, $"Repair {audit.AffectedProducts} filament IDs across {audit.AffectedFiles} files? Backups will be created." + (audit.ProgramFilesAffected > 0 ? " Windows may request administrator approval." : ""), "Repair AMS IDs", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await Task.Run(() => {
                if (audit.ProgramFilesAffected > 0) { _workspace.CreateBackup("ams-repair"); ElevationService.RepairAmsIdsWithElevation(_workspace.Paths); new StudioRecoveryService(_workspace).Capture(explicitChange: true); }
                else LibraryTransaction.Run(_workspace, "ams-repair", service.Repair);
            });
            await ReloadAsync(); LibraryRepairStatus.Text = "AMS ID repair complete."; RepairAmsAdminButton.Visibility = Visibility.Collapsed;
        });
    }
    private void LoadAdvancedPackage(LoadedFilamentPackage package)
    {
        _advancedPackage = package;
        AdvancedPackageGrid.ItemsSource = package.Manifest.Profiles;
        foreach (var p in package.Manifest.Profiles) p.PropertyChanged += (_, _) => { InvalidateAdvancedPlan(); UpdateControls(); };
        AdvancedPackageSummary.Text = $"{package.Manifest.DisplayName} · {package.Manifest.Profiles.Count} profiles · " + (package.Manifest.PrinterNeutral ? "Choose printers below" : "Printer-specific package");
        ShowImportTools_Click(this, new());
        SkipAdvancedDuplicates(); UpdateControls();
    }
    private async void AdvancedOpenPackage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "BFI package (*.bflib)|*.bflib", InitialDirectory = _workspace.LibraryFolder };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync("Reading package…", async () => LoadAdvancedPackage(await Task.Run(() => PackageReader.Load(dialog.FileName))));
    }
    private ImportDestination AdvancedImportDestination => AdvancedDestination.SelectedIndex switch { 1 => ImportDestination.DeviceAms, 2 => ImportDestination.ProjectLibrary, _ => ImportDestination.Both };
    private void AdvancedDestination_Changed(object sender, SelectionChangedEventArgs e) { InvalidateAdvancedPlan(); }
    private void PackageSelectAll_Click(object sender, RoutedEventArgs e) { if (_advancedPackage is not null) foreach (var p in _advancedPackage.Manifest.Profiles) p.IsSelected = true; }
    private void PackageClear_Click(object sender, RoutedEventArgs e) { if (_advancedPackage is not null) foreach (var p in _advancedPackage.Manifest.Profiles) p.IsSelected = false; }
    private void PackageSkipDuplicates_Click(object sender, RoutedEventArgs e) => SkipAdvancedDuplicates();
    private void SkipAdvancedDuplicates()
    {
        if (_advancedPackage is null) return;
        var targets = SelectedPrinters();
        var destination = AdvancedImportDestination;
        if (destination == ImportDestination.ProjectLibrary) targets = targets.Select(t => new PrinterTarget { ModelName=t.ModelName, ProfileSuffix=t.ProfileSuffix+" (Custom)", MachinePresetNames=t.MachinePresetNames }).ToList();
        foreach (var p in _advancedPackage.Manifest.Profiles)
        {
            p.IsDuplicate = _advancedPackage.Manifest.PrinterNeutral
                ? targets.Count > 0 && targets.All(t => PrinterProfileExpander.IsTargetCovered(p, t, _current, destination))
                : _current.Any(c => c.CanEdit && c.Name == p.Name && c.StorageKind == (destination == ImportDestination.ProjectLibrary ? FilamentStorageKind.UserPreset : FilamentStorageKind.SystemCatalog));
            p.Status = p.IsDuplicate ? "Duplicate" : "New / partial";
            if (p.IsDuplicate) p.IsSelected = false;
        }
    }
    private async void FindCatalogRepairs_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("Finding missing catalog profiles…", async () =>
        {
            var choices = await Task.Run(() =>
            {
                var mirrors = new BambuLibraryScanner(_workspace.Paths).LoadCurrentFilaments()
                    .Where(p => p.StorageKind == FilamentStorageKind.SystemCatalog && !PrinterProfileExpander.IsActiveEntry(p, FilamentStorageKind.SystemCatalog)).ToList();
                var folders = new[] { _workspace.LibraryFolder }.Concat(_workspace.IsLive ? ManufacturerLibraryStore.DiscoveryDirectories() : []);
                return folders.Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*.bflib"))
                    .DistinctBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(PackageReader.Load)
                    .Select(p => new CatalogRepairChoice(p.FilePath, p.Manifest.DisplayName, p.Manifest.Profiles.Count(f => mirrors.Any(m => m.Name == f.Name || m.ProductName == CurrentFilamentEntry.GetProductName(f.Name)))))
                    .Where(p => p.Missing > 0).ToList();
            });
            CatalogRepairPackages.ItemsSource = choices; CatalogRepairPackages.SelectedIndex = choices.Count > 0 ? 0 : -1;
            LibraryRepairStatus.Text = choices.Count == 0 ? "No downloaded packages match profiles found only in the installed catalog." : $"{choices.Count} package(s) can repair inactive catalog copies. Choose one and review it in Add Filaments.";
        });
    }
    private async void LoadCatalogRepair_Click(object sender, RoutedEventArgs e)
    {
        if (CatalogRepairPackages.SelectedItem is not CatalogRepairChoice choice) return;
        await RunAsync("Loading catalog repair…", async () => { AdvancedDestination.SelectedIndex = 1; LoadAdvancedPackage(await Task.Run(() => PackageReader.Load(choice.Path))); });
    }
    private async void AdvancedReview_Click(object sender, RoutedEventArgs e)
    {
        if (_advancedPackage is null) return;
        AdvancedPackageGrid.CommitEdit(DataGridEditingUnit.Cell, true); AdvancedPackageGrid.CommitEdit(DataGridEditingUnit.Row, true);
        await RunAsync("Reviewing package import…", async () =>
        {
            var package = _advancedPackage; var printers = SelectedPrinters(); var destination = AdvancedImportDestination;
            _advancedPlan = await Task.Run(() => AdvancedImportPlan.Create(_workspace.Paths, package, printers, destination));
            AdvancedReviewGrid.ItemsSource = _advancedPlan.Changes;
            AdvancedReviewSummary.Text = $"{_advancedPlan.Changes.Count} profile changes · {((ComboBoxItem)AdvancedDestination.SelectedItem).Content}";
            AdvancedImportReview.Visibility = Visibility.Visible;
        });
    }
    private async void AdvancedInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_advancedPlan is null) return;
        var plan = _advancedPlan;
        await RunAsync("Installing reviewed profiles…", async () => { await Task.Run(() => plan.Apply(_workspace)); InvalidateAdvancedPlan(); await ReloadAsync(); Log("Package imported. Backup saved in Maintenance."); });
    }
    private async void MirrorInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_advancedPlan is null || !_workspace.IsLive) return;
        var plan = _advancedPlan;
        await RunAsync("Preparing administrator import…", async () =>
        {
            plan.Validate(_workspace);
            if (MessageBox.Show(this, "Install the reviewed profiles and mirror them into Program Files? Windows will request administrator approval. Matching files can be replaced; backups are retained.", "Mirror installed catalog", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await Task.Run(() => { _workspace.CreateBackup("mirror-import"); PackageElevationService.Install(plan.Package, plan.Destination); new StudioRecoveryService(_workspace).Capture(explicitChange: true, importedNames: plan.Package.Manifest.Profiles.Where(p => p.IsSelected).Select(p => p.Name)); });
            InvalidateAdvancedPlan(); await ReloadAsync(); Log("Reviewed profiles installed and mirrored into Program Files.");
        });
    }
}
public sealed record CatalogRepairChoice(string Path, string Name, int Missing)
{ public string Label => $"{Name} · {Missing} missing profiles"; }
