using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BambuFilamentImporter.Models;
using BambuFilamentImporter.Services;
using Microsoft.Win32;

namespace BambuFilamentImporter;

public partial class MainWindow : Window
{
    private readonly BambuPaths _paths = new();
    private readonly ObservableCollection<CurrentFilamentEntry> _currentFilaments = [];
    private readonly ObservableCollection<FilamentGroup> _currentGroups = [];
    private readonly ObservableCollection<ProfileSettingEntry> _profileSettings = [];
    private readonly ObservableCollection<PrinterTarget> _printerTargets = [];
    private readonly FilamentSettingsService _settingsService;
    private readonly ICollectionView _settingsView;
    private LoadedFilamentPackage? _package;
    private CurrentFilamentEntry? _selectedCurrent;
    private FilamentProductGroup? _selectedProduct;
    private FilamentGroup? _selectedManufacturer;
    private FilamentProfileEntry? _selectedPackageProfile;

    public MainWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionText = version is null ? "0.4.2" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        BuildInfoText.Text = $"Version {versionText} | By Zataralee";
        Title = $"Bambu Filament Importer {versionText} by Zataralee";
        DarkModeCheck.IsChecked = ThemeService.IsDark;
        _settingsService = new FilamentSettingsService(_paths);
        _settingsView = CollectionViewSource.GetDefaultView(_profileSettings);
        _settingsView.Filter = FilterSetting;
        SettingsGrid.ItemsSource = _settingsView;
        PathsText.Text =
            $"Device/AMS: {_paths.RoamingProfileRoot}{Environment.NewLine}" +
            $"Project: {_paths.ActiveUserFilamentFolder}{Environment.NewLine}" +
            $"Installed: {_paths.ProgramProfileRoot}";
        CurrentTree.ItemsSource = _currentGroups;
        PrinterList.ItemsSource = _printerTargets;
        LoadPrinterTargets();
        RefreshBambuStatus();
        LoadCurrentLibrary();
    }

    private void RefreshLibrary_Click(object sender, RoutedEventArgs e)
    {
        RefreshBambuStatus();
        _settingsService.InvalidateIndex();
        LoadCurrentLibrary();
        LoadPrinterTargets();
        MarkDuplicates(autoSkipDuplicates: true);
    }

    private void RepairAmsIds_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBambuClosed("repairing AMS filament IDs"))
        {
            return;
        }

        try
        {
            var service = new AmsFilamentIdRepairService(_paths);
            var audit = service.Audit();
            if (audit.AffectedFiles == 0)
            {
                MessageBox.Show(this, "No AMS filament ID problems were found.", "AMS IDs are healthy", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshAmsIdStatus();
                return;
            }

            var approval = MessageBox.Show(
                this,
                $"Repair {audit.AffectedProducts} filament IDs across {audit.AffectedFiles} profile files?{Environment.NewLine}{Environment.NewLine}" +
                "Each changed file will be backed up first. Program Files copies may require Administrator approval.",
                "Repair AMS filament IDs",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (approval != MessageBoxResult.OK)
            {
                return;
            }

            SetLibraryOperationState("Repairing AMS filament IDs...");
            var result = ElevationService.RepairAmsIdsWithElevation(_paths);
            Log($"Repaired {result.RepairedProducts} AMS filament IDs across {result.ChangedFiles} profile files.");
            foreach (var change in result.Changes)
            {
                Log($"AMS ID: {change.Name}: {change.CurrentId} -> {change.NewId}");
            }

            RefreshAfterLibraryChange();
            MessageBox.Show(
                this,
                $"AMS ID repair complete.{Environment.NewLine}{Environment.NewLine}" +
                $"Filaments repaired: {result.RepairedProducts}{Environment.NewLine}" +
                $"Profile files updated: {result.ChangedFiles}{Environment.NewLine}{Environment.NewLine}" +
                "You can now open Bambu Studio and sync the AMS again.",
                "AMS repair complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "AMS repair failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Log(ex.ToString());
        }
        finally
        {
            RefreshBambuStatus();
        }
    }

    private void DarkModeCheck_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = DarkModeCheck.IsChecked == true;
        ThemeService.Apply(enabled);
        AppSettingsStore.Save(new ImporterSettings { DarkMode = enabled });
        RefreshBambuStatus();
    }

    private void BackupLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBambuClosed("backing up the current library"))
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Bambu library backups (*.bflbackup)|*.bflbackup|All files (*.*)|*.*",
            DefaultExt = ".bflbackup",
            AddExtension = true,
            FileName = $"Bambu-Library-{DateTime.Now:yyyyMMdd-HHmm}.bflbackup",
            Title = "Back up complete Bambu filament library"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            SetLibraryOperationState("Creating library backup...");
            var result = new LibraryBackupService(_paths).Create(dialog.FileName);
            Log($"Library backup created: {result.FilePath}");
            Log($"Backup contains {result.CatalogProfiles} catalog profiles, {result.CatalogFiles} catalog files, and {result.UserPresetFiles} user preset files.");
            MessageBox.Show(
                this,
                $"Backup complete.{Environment.NewLine}{Environment.NewLine}" +
                $"Catalog profiles: {result.CatalogProfiles}{Environment.NewLine}" +
                $"User preset files: {result.UserPresetFiles}{Environment.NewLine}" +
                $"Project Library names: {result.ProjectPresetNames}",
                "Library backup complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Backup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Log(ex.ToString());
        }
        finally
        {
            RefreshBambuStatus();
        }
    }

    private void RestoreLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBambuClosed("restoring a library backup"))
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Bambu library backups (*.bflbackup)|*.bflbackup|All files (*.*)|*.*",
            Title = "Restore Bambu filament library backup"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var service = new LibraryBackupService(_paths);
            var summary = service.Inspect(dialog.FileName);
            var created = DateTime.TryParse(summary.CreatedUtc, out var createdUtc)
                ? createdUtc.ToLocalTime().ToString("g")
                : summary.CreatedUtc;
            var confirmation = MessageBox.Show(
                this,
                $"Merge this backup into the current library?{Environment.NewLine}{Environment.NewLine}" +
                $"Created: {created}{Environment.NewLine}" +
                $"Catalog profiles: {summary.CatalogProfiles}{Environment.NewLine}" +
                $"Catalog files: {summary.CatalogFiles}{Environment.NewLine}" +
                $"User preset files: {summary.UserPresetFiles}{Environment.NewLine}" +
                $"Project Library names: {summary.ProjectPresetNames}{Environment.NewLine}{Environment.NewLine}" +
                "Existing filaments not contained in the backup will be kept.",
                "Restore library backup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            SetLibraryOperationState("Restoring library backup...");
            var result = service.Restore(dialog.FileName);
            _settingsService.InvalidateIndex();
            LoadCurrentLibrary();
            MarkDuplicates(autoSkipDuplicates: false);
            Log($"Library restore complete: {result.AddedFiles} added, {result.UpdatedFiles} updated, {result.UnchangedFiles} unchanged.");
            MessageBox.Show(
                this,
                $"Restore complete.{Environment.NewLine}{Environment.NewLine}" +
                $"Files added: {result.AddedFiles}{Environment.NewLine}" +
                $"Files updated: {result.UpdatedFiles}{Environment.NewLine}" +
                $"Files unchanged: {result.UnchangedFiles}{Environment.NewLine}" +
                $"Safety backups: {result.SafetyBackups}",
                "Library restore complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Restore failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Log(ex.ToString());
        }
        finally
        {
            RefreshBambuStatus();
        }
    }

    private void SetLibraryOperationState(string message)
    {
        BambuStatusText.Text = message;
        BambuStatusText.Foreground = ThemeService.Brush("BusyBrush");
        InstallButton.IsEnabled = false;
        BackupLibraryButton.IsEnabled = false;
        RestoreLibraryButton.IsEnabled = false;
    }

    private void OpenPackage_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 0;
        var dialog = new OpenFileDialog
        {
            Filter = "Bambu filament libraries (*.bflib)|*.bflib|All files (*.*)|*.*",
            Title = "Open filament library"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            DetachPackageEvents();
            _package = PackageReader.Load(dialog.FileName);
            foreach (var profile in _package.Manifest.Profiles)
            {
                profile.PropertyChanged += PackageProfile_PropertyChanged;
            }

            PackagePathText.Text = dialog.FileName;
            MarkDuplicates(autoSkipDuplicates: true);
            PackageGrid.ItemsSource = _package.Manifest.Profiles;
            MainTabs.SelectedIndex = 0;
            UpdateSummary();
            RefreshBambuStatus();
            Log($"Loaded {_package.Manifest.DisplayName}.");
            if (_package.Manifest.SourceUrls.Count > 0)
            {
                Log($"Package includes {_package.Manifest.SourceUrls.Count} official manufacturer source link(s).");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Package could not be opened", MessageBoxButton.OK, MessageBoxImage.Error);
            Log(ex.Message);
        }
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        RefreshBambuStatus();
        if (BambuProcess.IsStudioRunning())
        {
            MessageBox.Show(this, "Close Bambu Studio before importing. The importer will not continue while it is running.", "Bambu Studio is open", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_package is null)
        {
            return;
        }

        try
        {
            PackageGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            PackageGrid.CommitEdit(DataGridEditingUnit.Row, true);

            var installer = new BambuInstaller(_paths);
            var destination = SelectedDestination();
            var selectedPrinters = _printerTargets.Where(target => target.IsSelected).ToList();
            var installPackage = PrinterProfileExpander.Expand(_package, selectedPrinters, _currentFilaments, destination);
            var result = installer.Install(installPackage, destination, InstallProgramCheck.IsChecked == true);

            Log($"Installed {_package.Manifest.DisplayName} to {DestinationLabel(destination)}.");
            if (_package.Manifest.PrinterNeutral)
            {
                Log($"Generated profiles for {selectedPrinters.Count} configured printer model(s).");
            }
            Log($"Wrote {result.WrittenFiles.Count} profile files.");
            if (result.UserPresetEntriesWritten.Count > 0)
            {
                Log($"Created {result.UserPresetEntriesWritten.Count} Project Library user presets.");
            }
            if (result.ManifestEntriesAdded.Count > 0)
            {
                Log($"Added {result.ManifestEntriesAdded.Count} Device/AMS catalog entries.");
            }
            if (result.ManifestEntriesUpdated.Count > 0)
            {
                Log($"Updated {result.ManifestEntriesUpdated.Count} existing Device/AMS catalog entries.");
            }
            if (result.ProjectPresetEntriesAdded.Count > 0)
            {
                Log($"Added {result.ProjectPresetEntriesAdded.Count} project dropdown entries.");
            }
            Log($"Backups created: {result.Backups.Count}.");
            _settingsService.InvalidateIndex();
            LoadCurrentLibrary();
            MarkDuplicates(autoSkipDuplicates: true);
            MessageBox.Show(this, $"Import complete: {DestinationLabel(destination)}.", "Import complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Windows blocked writing to Program Files. Reopen this app as Administrator or uncheck the Program Files option.", "Administrator required", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Log(ex.ToString());
        }
    }

    private static string DestinationLabel(ImportDestination destination) => destination switch
    {
        ImportDestination.DeviceAms => "Device / AMS catalog",
        ImportDestination.ProjectLibrary => "Bambu Studio Project Library",
        _ => "Device / AMS and Project Library"
    };

    private void SkipDuplicates_Click(object sender, RoutedEventArgs e)
    {
        if (_package is null)
        {
            return;
        }

        foreach (var profile in _package.Manifest.Profiles)
        {
            profile.IsSelected = !profile.IsDuplicate;
        }

        UpdateSummary();
        Log("Duplicate profiles unchecked.");
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_package is null)
        {
            return;
        }

        foreach (var profile in _package.Manifest.Profiles)
        {
            profile.IsSelected = true;
        }

        UpdateSummary();
        Log("All package profiles selected.");
    }

    private void SelectAllPrinters_Click(object sender, RoutedEventArgs e)
    {
        foreach (var target in _printerTargets)
        {
            target.IsSelected = true;
        }

        MarkDuplicates(autoSkipDuplicates: false);
        UpdateSummary();
    }

    private void ClearPrinters_Click(object sender, RoutedEventArgs e)
    {
        foreach (var target in _printerTargets)
        {
            target.IsSelected = false;
        }

        MarkDuplicates(autoSkipDuplicates: false);
        UpdateSummary();
    }

    private void PrinterTarget_Changed(object sender, RoutedEventArgs e)
    {
        MarkDuplicates(autoSkipDuplicates: false);
        UpdateSummary();
    }

    private void Destination_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        MarkDuplicates(autoSkipDuplicates: false);
        UpdateSummary();
    }

    private void PackageProfile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilamentProfileEntry.Name))
        {
            MarkDuplicates(autoSkipDuplicates: false);
            UpdateSummary();
        }
        else if (e.PropertyName == nameof(FilamentProfileEntry.IsSelected))
        {
            UpdateSummary();
        }
        else if (e.PropertyName == nameof(FilamentProfileEntry.VendorGroup) && sender is FilamentProfileEntry profile)
        {
            ApplyManufacturerToPackageBase(profile);
        }
    }

    private void PackageGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedPackageProfile = PackageGrid.SelectedItem as FilamentProfileEntry;
        PackageNameText.DataContext = _selectedPackageProfile;
        PackageVendorText.DataContext = _selectedPackageProfile;
    }

    private void ApplyPackageEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPackageProfile is null)
        {
            return;
        }

        var proposedName = PackageNameText.Text.Trim();
        if (string.IsNullOrWhiteSpace(proposedName))
        {
            MessageBox.Show(this, "Profile name cannot be blank.", "Invalid name", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _selectedPackageProfile.Name = proposedName;
        _selectedPackageProfile.VendorGroup = PackageVendorText.Text.Trim();
        ApplyManufacturerToPackageBase(_selectedPackageProfile);
        MarkDuplicates(autoSkipDuplicates: false);
        UpdateSummary();
        Log($"Updated proposed import profile: {_selectedPackageProfile.Name}");
    }

    private void ApplyManufacturerToPackageBase(FilamentProfileEntry profile)
    {
        if (_package is null || profile.Name.EndsWith("@base", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var sourcePath = string.IsNullOrWhiteSpace(profile.OriginalRelativePath) ? profile.RelativePath : profile.OriginalRelativePath;
        if (!_package.ProfileJsonByPath.TryGetValue(sourcePath, out var json))
        {
            return;
        }

        var inherits = System.Text.Json.Nodes.JsonNode.Parse(json)?["inherits"]?.GetValue<string>();
        var baseProfile = _package.Manifest.Profiles.FirstOrDefault(candidate =>
            candidate.OriginalName.Equals(inherits, StringComparison.OrdinalIgnoreCase));
        if (baseProfile is not null)
        {
            baseProfile.VendorGroup = profile.VendorGroup;
        }
    }

    private void CurrentTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        ResetSelectionState();
        switch (e.NewValue)
        {
            case CurrentFilamentEntry profile:
                SelectCurrentProfile(profile);
                break;
            case FilamentProductGroup product:
                SelectProduct(product);
                break;
            case FilamentGroup manufacturer:
                SelectManufacturer(manufacturer);
                break;
        }
    }

    private void SelectCurrentProfile(CurrentFilamentEntry profile)
    {
        _selectedCurrent = profile;
        CurrentSelectionTitle.Text = $"Printer profile: {profile.Name}";
        CurrentNameText.Text = profile.Name;
        CurrentVendorText.Text = profile.VendorGroup;
        CurrentNameText.IsEnabled = profile.CanEdit;
        CurrentVendorText.IsEnabled = profile.CanEdit && profile.StorageKind == FilamentStorageKind.SystemCatalog;
        CurrentLocationText.Text = profile.Location;
        CurrentSourceText.Text = profile.CopyCount > 1
            ? $"{profile.Source} | {profile.CopyCount} synchronized copies"
            : $"{profile.Source} | {profile.RelativePath}";
        SaveCurrentButton.IsEnabled = profile.CanEdit;
        RemoveProfileButton.IsEnabled = profile.CanEdit;
        SaveCurrentButton.Visibility = Visibility.Visible;
        RemoveProfileButton.Visibility = Visibility.Visible;
        LoadSettings(profile);
    }

    private void SelectProduct(FilamentProductGroup product)
    {
        _selectedProduct = product;
        var preferredProfile = product.Items.FirstOrDefault(profile =>
                !profile.IsBaseProfile && profile.Name.Contains("@BBL X1C", StringComparison.OrdinalIgnoreCase))
            ?? product.Items.FirstOrDefault(profile => !profile.IsBaseProfile)
            ?? product.Items.First();
        SelectCurrentProfile(preferredProfile);
        _selectedProduct = product;
        CurrentSelectionTitle.Text = $"Filament: {product.ProductName} | {preferredProfile.Name}";
        RemoveProfileButton.Visibility = Visibility.Collapsed;
        RemoveFilamentButton.IsEnabled = product.EditableCount > 0;
        RemoveFilamentButton.Visibility = Visibility.Visible;
    }

    private void SelectManufacturer(FilamentGroup manufacturer)
    {
        _selectedManufacturer = manufacturer;
        CurrentSelectionTitle.Text = $"Manufacturer: {manufacturer.VendorGroup}";
        CurrentVendorText.Text = manufacturer.VendorGroup;
        CurrentVendorText.IsEnabled = manufacturer.EditableCount > 0;
        CurrentLocationText.Text = $"{manufacturer.ProductCount} filaments | {manufacturer.ProfileCount} profiles";
        CurrentSourceText.Text = $"{manufacturer.EditableCount} editable profiles";
        RenameManufacturerButton.IsEnabled = manufacturer.Profiles.Any(profile =>
            profile.CanEdit && profile.StorageKind == FilamentStorageKind.SystemCatalog && profile.IsBaseProfile);
        RemoveManufacturerButton.IsEnabled = manufacturer.EditableCount > 0;
        RenameManufacturerButton.Visibility = Visibility.Visible;
        RemoveManufacturerButton.Visibility = Visibility.Visible;
    }

    private void LoadSettings(CurrentFilamentEntry profile)
    {
        _profileSettings.Clear();
        try
        {
            foreach (var setting in _settingsService.Load(profile))
            {
                _profileSettings.Add(setting);
            }
            _settingsView.Refresh();
            Log($"Loaded {_profileSettings.Count} effective settings for {profile.Name}.");
        }
        catch (Exception ex)
        {
            Log($"Settings could not be loaded for {profile.Name}: {ex.Message}");
        }
    }

    private void SaveCurrentEdit_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBambuClosed("editing current profiles") || _selectedCurrent is null)
        {
            return;
        }

        try
        {
            SettingsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            SettingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var changedSettings = _settingsService.Save(_selectedCurrent, _profileSettings);
            new BambuLibraryEditor(_paths).Save(_selectedCurrent, CurrentNameText.Text.Trim(), CurrentVendorText.Text.Trim());
            Log($"Saved {_selectedCurrent.Name}; {changedSettings} setting values changed.");
            RefreshAfterLibraryChange();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Edit failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Log(ex.ToString());
        }
    }

    private void RenameManufacturer_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBambuClosed("renaming a manufacturer") || _selectedManufacturer is null)
        {
            return;
        }

        try
        {
            var changed = new BambuLibraryEditor(_paths).RenameManufacturer(_selectedManufacturer, CurrentVendorText.Text.Trim());
            Log($"Renamed manufacturer on {changed} base profiles.");
            RefreshAfterLibraryChange();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Rename failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Log(ex.ToString());
        }
    }

    private void RemoveCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCurrent is null)
        {
            return;
        }

        RemoveEntries([_selectedCurrent], "profile", _selectedCurrent.Name);
    }

    private void RemoveFilament_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProduct is null)
        {
            return;
        }

        RemoveEntries(_selectedProduct.Items, "filament", _selectedProduct.ProductName);
    }

    private void RemoveManufacturer_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedManufacturer is null)
        {
            return;
        }

        RemoveEntries(_selectedManufacturer.Profiles, "manufacturer", _selectedManufacturer.VendorGroup);
    }

    private void RemoveEntries(IEnumerable<CurrentFilamentEntry> requested, string scope, string label)
    {
        if (!EnsureBambuClosed($"removing a {scope}"))
        {
            return;
        }

        var editableEntries = requested.Where(entry => entry.CanEdit).ToList();
        if (editableEntries.Count == 0)
        {
            return;
        }

        var physicalEntries = ElevationService.ExpandPhysicalCopies(editableEntries);
        var needsAdministrator = physicalEntries.Any(entry =>
            entry.ProfileRoot.Equals(_paths.ProgramProfileRoot, StringComparison.OrdinalIgnoreCase));

        var answer = MessageBox.Show(
            this,
            $"Remove {scope} '{label}' and {physicalEntries.Count} profile file(s) across all detected locations? " +
            (needsAdministrator ? "Windows will request Administrator approval. " : "") +
            "Backups will be retained beside the original files.",
            $"Remove {scope}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var removed = needsAdministrator
                ? ElevationService.RemoveWithElevation(_paths, physicalEntries)
                : new BambuLibraryEditor(_paths).RemoveMany(physicalEntries);
            Log($"Removed {scope} {label}: {removed} profiles.");
            RefreshAfterLibraryChange();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Remove failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Log(ex.ToString());
        }
    }

    private bool EnsureBambuClosed(string operation)
    {
        RefreshBambuStatus();
        if (!BambuProcess.IsStudioRunning())
        {
            return true;
        }

        MessageBox.Show(this, $"Close Bambu Studio before {operation}.", "Bambu Studio is open", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void RefreshAfterLibraryChange()
    {
        ClearCurrentEditor();
        _settingsService.InvalidateIndex();
        LoadCurrentLibrary();
        MarkDuplicates(autoSkipDuplicates: false);
    }

    private void LoadCurrentLibrary()
    {
        try
        {
            _currentFilaments.Clear();
            foreach (var filament in new BambuLibraryScanner(_paths).LoadCurrentFilaments())
            {
                _currentFilaments.Add(filament);
            }

            BuildCurrentGroups();
            RefreshAmsIdStatus();
            var systemCount = _currentFilaments.Count(item => item.StorageKind == FilamentStorageKind.SystemCatalog);
            var userCount = _currentFilaments.Count - systemCount;
            Log($"Loaded {systemCount} Device/AMS catalog profiles and {userCount} Project Library user presets.");
        }
        catch (Exception ex)
        {
            Log($"Current library scan failed: {ex.Message}");
        }
    }

    private void RefreshAmsIdStatus()
    {
        try
        {
            var audit = new AmsFilamentIdRepairService(_paths).Audit();
            AmsIdWarningPanel.Visibility = audit.AffectedFiles > 0 ? Visibility.Visible : Visibility.Collapsed;
            AmsIdStatusText.Text = audit.AffectedFiles == 0
                ? ""
                : $"{audit.AffectedProducts} filament IDs need AMS compatibility repair ({audit.AffectedFiles} files).";
        }
        catch (Exception ex)
        {
            AmsIdWarningPanel.Visibility = Visibility.Collapsed;
            Log($"AMS ID audit failed: {ex.Message}");
        }
    }

    private void BuildCurrentGroups()
    {
        var filter = LibrarySearchText.Text.Trim();
        var filtered = _currentFilaments.Where(item => string.IsNullOrWhiteSpace(filter)
            || item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || item.VendorGroup.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || item.MaterialFamily.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || item.Location.Contains(filter, StringComparison.OrdinalIgnoreCase));

        _currentGroups.Clear();
        foreach (var vendorGroup in filtered
            .GroupBy(item => string.IsNullOrWhiteSpace(item.VendorGroup) ? "(No manufacturer)" : item.VendorGroup)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var products = vendorGroup
                .GroupBy(item => item.ProductName)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new FilamentProductGroup
                {
                    ProductName = group.Key,
                    Items = group.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList()
                })
                .ToList();
            _currentGroups.Add(new FilamentGroup { VendorGroup = vendorGroup.Key, Items = products });
        }
    }

    private void LibrarySearchText_Changed(object sender, TextChangedEventArgs e)
    {
        if (CurrentTree is not null)
        {
            BuildCurrentGroups();
        }
    }

    private void SettingsSearchText_Changed(object sender, TextChangedEventArgs e)
    {
        _settingsView?.Refresh();
    }

    private bool FilterSetting(object item)
    {
        if (item is not ProfileSettingEntry setting)
        {
            return false;
        }

        var filter = SettingsSearchText?.Text.Trim() ?? "";
        return string.IsNullOrWhiteSpace(filter)
            || setting.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || setting.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || setting.Category.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || setting.Value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void SettingsGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is not ProfileSettingEntry { IsEditable: true })
        {
            e.Cancel = true;
        }
    }

    private void MarkDuplicates(bool autoSkipDuplicates = false)
    {
        if (_package is null)
        {
            return;
        }

        var currentNames = _currentFilaments.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedTargets = _printerTargets.Where(target => target.IsSelected).ToList();
        var destination = SelectedDestination();
        foreach (var profile in _package.Manifest.Profiles)
        {
            if (_package.Manifest.PrinterNeutral)
            {
                var baseName = profile.Name.EndsWith("@base", StringComparison.OrdinalIgnoreCase)
                    ? profile.Name
                    : profile.Name + " @base";
                var baseRequired = destination != ImportDestination.ProjectLibrary;
                var baseCovered = !baseRequired || _currentFilaments.Any(entry =>
                    entry.StorageKind == FilamentStorageKind.SystemCatalog
                    && entry.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase));
                var coveredTargets = selectedTargets.Count(target =>
                    PrinterProfileExpander.IsTargetCovered(profile, target, _currentFilaments, destination));
                var existingCount = coveredTargets + (baseRequired && baseCovered ? 1 : 0);
                profile.IsDuplicate = selectedTargets.Count > 0 && baseCovered && coveredTargets == selectedTargets.Count;
                profile.Status = profile.IsDuplicate ? "Duplicate" : existingCount > 0 ? "Partial" : "New";
            }
            else
            {
                profile.IsDuplicate = currentNames.Contains(profile.Name);
                profile.Status = profile.IsDuplicate ? "Duplicate" : "New";
            }
            if (autoSkipDuplicates && profile.IsDuplicate)
            {
                profile.IsSelected = false;
            }
        }
    }

    private ImportDestination SelectedDestination() => DeviceAmsRadio.IsChecked == true
        ? ImportDestination.DeviceAms
        : ProjectLibraryRadio.IsChecked == true
            ? ImportDestination.ProjectLibrary
            : ImportDestination.Both;

    private void UpdateSummary()
    {
        if (_package is null)
        {
            SummaryText.Text = "Select a package to preview its contents and compare it to the current library.";
            return;
        }

        var duplicateCount = _package.Manifest.Profiles.Count(profile => profile.IsDuplicate);
        var selectedCount = _package.Manifest.Profiles.Count(profile => profile.IsSelected);
        var selectedPrinterCount = _printerTargets.Count(target => target.IsSelected);
        SummaryText.Text =
            $"{_package.Manifest.DisplayName}{Environment.NewLine}" +
            $"Manufacturer: {_package.Manifest.Manufacturer}{Environment.NewLine}" +
            $"Version: {_package.Manifest.Version}{Environment.NewLine}" +
            $"Filaments: {_package.Manifest.Profiles.Count}{Environment.NewLine}" +
            $"Target printers: {selectedPrinterCount}{Environment.NewLine}" +
            $"Official sources: {_package.Manifest.SourceUrls.Count}{Environment.NewLine}" +
            $"Duplicates: {duplicateCount}{Environment.NewLine}" +
            $"Selected: {selectedCount}";
    }

    private void LoadPrinterTargets()
    {
        var selectedKeys = _printerTargets
            .Where(target => target.IsSelected)
            .Select(target => target.Vendor + "|" + target.ModelName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var firstLoad = _printerTargets.Count == 0;
        _printerTargets.Clear();

        foreach (var target in new PrinterDiscoveryService(_paths).DiscoverConfiguredPrinters())
        {
            target.IsSelected = firstLoad || selectedKeys.Contains(target.Vendor + "|" + target.ModelName);
            _printerTargets.Add(target);
        }

        Log($"Detected {_printerTargets.Count} configured printer model(s) from Bambu Studio.");
    }

    private void ResetSelectionState()
    {
        _selectedCurrent = null;
        _selectedProduct = null;
        _selectedManufacturer = null;
        _profileSettings.Clear();
        CurrentNameText.Text = "";
        CurrentVendorText.Text = "";
        CurrentNameText.IsEnabled = false;
        CurrentVendorText.IsEnabled = false;
        CurrentLocationText.Text = "";
        CurrentSourceText.Text = "";
        SaveCurrentButton.IsEnabled = false;
        SaveCurrentButton.Visibility = Visibility.Collapsed;
        RenameManufacturerButton.IsEnabled = false;
        RenameManufacturerButton.Visibility = Visibility.Collapsed;
        RemoveProfileButton.IsEnabled = false;
        RemoveProfileButton.Visibility = Visibility.Collapsed;
        RemoveFilamentButton.IsEnabled = false;
        RemoveFilamentButton.Visibility = Visibility.Collapsed;
        RemoveManufacturerButton.IsEnabled = false;
        RemoveManufacturerButton.Visibility = Visibility.Collapsed;
    }

    private void ClearCurrentEditor()
    {
        ResetSelectionState();
        CurrentSelectionTitle.Text = "Select a manufacturer, filament, or printer profile";
    }

    private void RefreshBambuStatus()
    {
        if (BambuProcess.IsStudioRunning())
        {
            BambuStatusText.Text = "Bambu Studio is open";
            BambuStatusText.Foreground = ThemeService.Brush("DangerBrush");
            InstallButton.IsEnabled = false;
            BackupLibraryButton.IsEnabled = false;
            RestoreLibraryButton.IsEnabled = false;
            return;
        }

        BambuStatusText.Text = "Bambu Studio is closed";
        BambuStatusText.Foreground = ThemeService.Brush("SuccessBrush");
        InstallButton.IsEnabled = _package is not null;
        BackupLibraryButton.IsEnabled = true;
        RestoreLibraryButton.IsEnabled = true;
    }

    private void DetachPackageEvents()
    {
        if (_package is null)
        {
            return;
        }

        foreach (var profile in _package.Manifest.Profiles)
        {
            profile.PropertyChanged -= PackageProfile_PropertyChanged;
        }
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:T}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}
