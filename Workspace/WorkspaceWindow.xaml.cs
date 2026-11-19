using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BambuFilamentImporter.Models;
using BambuFilamentImporter.Services;
using Microsoft.Win32;

namespace BambuFilamentImporter.Workspace;

public partial class WorkspaceWindow : Window
{
    private readonly WorkspaceContext _workspace;
    private readonly ObservableCollection<ProductChoice> _products = [];
    private readonly ObservableCollection<PrinterChoice> _printers = [];
    private readonly ObservableCollection<LibraryProduct> _library = [];
    private readonly ObservableCollection<ProfileSettingEntry> _settings = [];
    private readonly ICollectionView _settingsView;
    private List<FilamentTreeNode> _productNodes = [], _libraryNodes = [];
    private FilamentTreeNode? _chosenNode;
    private readonly BambuProcessMonitor _monitor = new();
    private List<CurrentFilamentEntry> _current = [];
    private ImportSelectionPlan? _plan;
    private CurrentFilamentEntry? _editing;
    private LibraryProduct? _chosenProduct;
    private string _editHash = "";
    private bool _busy, _ready, _suppress;
    private bool _checkingUpdates;
    private UpdateRelease? _availableAppUpdate;
    private LibraryUpdateNotice? _libraryNotice;
    private string? _deferredRecoveryVersion;
    private int _lastTab;
    public bool InitializeOnLoad { get; set; } = true;
    public bool StartupUpdateChecksEnabled { get; set; } = true;
    public WorkspaceWindow(WorkspaceContext? workspace = null)
    {
        _workspace = workspace ?? new WorkspaceContext();
        InitializeComponent();
        Title = _workspace.IsPreview ? "BFI Redesign Preview · Compact workspace" : $"Bambu Filament Importer {UpdateService.CurrentVersion.ToString(3)} by Zataralee";
        BuildLabel.Text = _workspace.IsPreview ? "Compact workspace · Preview · By Zataralee" : $"Version {UpdateService.CurrentVersion.ToString(3)} · Compact workspace · By Zataralee";
        _settingsView = CollectionViewSource.GetDefaultView(_settings);
        _settingsView.Filter = p => p is ProfileSettingEntry item
            && (CategoryFilter.SelectedItem is not string category || category == "All settings" || item.Category == category)
            && (string.IsNullOrWhiteSpace(SettingSearch.Text) || (item.Key + " " + item.DisplayName).Contains(SettingSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        PrinterChoices.ItemsSource = _printers;
        SettingsGrid.ItemsSource = _settingsView;
        DarkCheck.IsChecked = ThemeService.IsDark;
        Loaded += async (_, _) =>
        {
            if (!InitializeOnLoad) return;
            await RunAsync("Preparing workspace…", async () => await Task.Run(_workspace.Initialize));
            // Offer recovery before trying to read a catalog that an update may have damaged.
            if (!_workspace.IsPreview) await CheckStudioRecoveryAsync(false);
            await RunAsync("Loading your filament library…", async () =>
            {
                await ReloadAsync();
                _ready = true;
                Log("Workspace ready.");
            });
            if (StartupUpdateChecksEnabled && !_workspace.IsPreview) await CheckUpdatesAsync(false);
        };
        Closing += (_, e) => { if (_busy) { e.Cancel = true; return; } if (!CanLeaveEditor()) e.Cancel = true; };
        Closed += (_, _) => _monitor.Dispose();
        _monitor.StateChanged += running => Dispatcher.BeginInvoke(async () =>
        {
            _plan = null;
            if (ReviewPanel.Visibility == Visibility.Visible) StatusText.Text = "Studio state changed. Go back and review again before installing.";
            UpdateControls();
            if (!running && !_busy && _ready && !_settings.Any(s => s.IsModified))
            {
                await RunAsync("Refreshing Studio records…", ReloadAsync);
                if (!_workspace.IsPreview) await CheckStudioRecoveryAsync(false);
            }
        });
        _monitor.Start(BambuProcess.IsStudioRunning());
        UpdateControls();
    }
    private async Task RunAsync(string label, Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        StatusText.Text = label;
        Navigation.IsEnabled = false;
        BusyProgress.Visibility = Visibility.Visible;
        try { await action(); }
        catch (Exception ex)
        {
            Log(ex.Message);
            AppLog.WriteException("Workspace operation failed", ex);
            MessageBox.Show(this, ex.Message, "Bambu Filament Importer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            Navigation.IsEnabled = true;
            BusyProgress.Visibility = Visibility.Collapsed;
            UpdateControls();
        }
    }
    private async Task ReloadAsync()
    {
        _ready = false;
        var selected = _products.Where(p => p.IsSelected).Select(p => p.PackagePath + "|" + p.ProfileName).ToHashSet();
        var oldPrinters = _printers.ToDictionary(p => p.Target.ModelName, p => (p.IsSelected, p.Nozzles.Where(n => n.IsSelected).Select(n => n.Name).ToHashSet()));
        var data = await Task.Run(() =>
        {
            var library = new BambuLibraryScanner(_workspace.Paths).LoadCurrentFilaments();
            var discovery = new PrinterDiscoveryService(_workspace.Paths).Discover();
            var packages = Directory.EnumerateFiles(_workspace.LibraryFolder, "*.bflib").Select(PackageReader.Load).Where(p => p.Manifest.PrinterNeutral).ToList();
            return (library, discovery, packages);
        });
        _suppress = true;
        try
        {
            _current = data.library;
            _products.Clear();
            foreach (var package in data.packages.OrderBy(p => p.Manifest.Manufacturer))
            foreach (var profile in package.Manifest.Profiles.OrderBy(p => p.Name))
            {
                var product = new ProductChoice { PackagePath = package.FilePath, ProfileName = profile.Name,
                    Manufacturer = package.Manifest.Manufacturer, Material = profile.MaterialFamily,
                    IsSelected = selected.Contains(package.FilePath + "|" + profile.Name) };
                var matching = _current.Where(e => e.CanEdit && e.ProductName.Equals(product.Name, StringComparison.OrdinalIgnoreCase) && !e.IsBaseProfile).ToList();
                var custom = matching.Count(p => p.StorageKind == FilamentStorageKind.UserPreset);
                var system = matching.Count - custom;
                product.Coverage = matching.Count == 0 ? "Not installed" : $"Custom: {custom} · System: {system} profiles";
                product.PropertyChanged += Selection_Changed;
                _products.Add(product);
            }
            var vendor = VendorFilter.SelectedItem as string;
            VendorFilter.ItemsSource = new[] { "All manufacturers" }.Concat(_products.Select(p => p.Manufacturer).Distinct().Order()).ToList();
            VendorFilter.SelectedItem = vendor ?? "All manufacturers";
            if (VendorFilter.SelectedIndex < 0) VendorFilter.SelectedIndex = 0;
            _printers.Clear();
            foreach (var printer in data.discovery.Printers)
            {
                var choice = new PrinterChoice(printer);
                if (oldPrinters.TryGetValue(printer.ModelName, out var previous))
                {
                    choice.IsSelected = previous.IsSelected;
                    foreach (var nozzle in choice.Nozzles) nozzle.IsSelected = previous.Item2.Contains(nozzle.Name);
                }
                choice.PropertyChanged += Selection_Changed;
                foreach (var nozzle in choice.Nozzles) nozzle.PropertyChanged += Selection_Changed;
                _printers.Add(choice);
            }
            _library.Clear();
            foreach (var product in FilamentTree.Products(_current, _products.Select(p => p.Manufacturer))) _library.Add(product);
            ClearEditor();
            _plan = null;
            LoadBackups();
            CatalogCount.Text = $"{_products.Count} filaments · {data.packages.Count} downloaded libraries";
            EmptyLibraryPanel.Visibility = _products.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            WorkspacePath.Text = "Workspace: " + _workspace.Root + Environment.NewLine + "Active library: " + _workspace.Paths.RoamingProfileRoot;
        }
        finally { _suppress = false; }
        _ready = true;
        RefreshProductTree(); RefreshLibraryTree();
        UpdateControls();
    }
    private void Log(string message)
    {
        StatusText.Text = message;
        ActivityLog.AppendText($"[{DateTime.Now:t}] {message}{Environment.NewLine}");
        ActivityLog.ScrollToEnd();
    }
    private List<PrinterTarget> SelectedPrinters() => _printers.Where(p => p.IsSelected).Select(p => p.ToTarget()).ToList();
    private void UpdateControls()
    {
        if (StudioStatus is null) return;
        var studio = BambuProcess.IsStudioRunning();
        var other = WorkspaceContext.IsAnotherWorkspaceRunning();
        var canWrite = !_busy && _ready && (!_workspace.IsLive || (!studio && !other));
        StudioStatus.Text = studio ? "Studio open · live writes paused" : "Bambu Studio closed";
        if (_workspace.IsLive && other) StudioStatus.Text += " · close other BFI windows to write";
        var selectedCount = _products.Count(p => p.IsSelected);
        SelectionCount.Text = $"{selectedCount} filament{(selectedCount == 1 ? "" : "s")} selected";
        ReviewButton.IsEnabled = !_busy && _ready && _products.Any(p => p.IsSelected) && _printers.Any(p => p.IsSelected && p.Nozzles.Any(n => n.IsSelected));
        InstallButton.IsEnabled = canWrite && _plan?.HasChanges == true;
        InstallButton.Content = UseCustomCheck.IsChecked == true ? "Install into Custom Library" : "Install into System Library";
        SaveButton.IsEnabled = canWrite && _editing is not null && _settings.Any(s => s.IsModified);
        RemoveButton.IsEnabled = ResetOverrideButton.IsEnabled = canWrite && _editing is not null;
        CheckUpdatesButton.IsEnabled = !_busy && !_checkingUpdates;
        UpdateAppButton.IsEnabled = !_busy && !_workspace.IsPreview;
        EditStatus.Text = _editing is null ? "Choose a profile to view settings." : $"{_settings.Count(s => s.IsModified)} unsaved changes · {_settings.Count} settings";
    }
    private void Selection_Changed(object? sender, PropertyChangedEventArgs e) { if (_suppress) return; _plan = null; UpdateControls(); }
    private void ImportOption_Changed(object sender, RoutedEventArgs e) { _plan = null; UpdateControls(); }
    private void RefreshProductTree()
    {
        if (ProductsTree is null || _suppress) return;
        var nodes = FilamentTree.Catalog(_products, ProductSearch.Text, VendorFilter.SelectedItem as string ?? "All manufacturers");
        if (string.IsNullOrWhiteSpace(ProductSearch.Text)) FilamentTree.PreserveExpansion(_productNodes, nodes);
        _productNodes = nodes; ProductsTree.ItemsSource = nodes;
    }
    private void RefreshLibraryTree()
    {
        if (LibraryTree is null || _suppress) return;
        var nodes = FilamentTree.Library(_library, LibrarySearch.Text);
        if (string.IsNullOrWhiteSpace(LibrarySearch.Text)) FilamentTree.PreserveExpansion(_libraryNodes, nodes);
        _libraryNodes = nodes; LibraryTree.ItemsSource = nodes;
    }
    private void ProductFilter_Changed(object sender, RoutedEventArgs e) => RefreshProductTree();
    private void LibraryFilter_Changed(object sender, TextChangedEventArgs e) => RefreshLibraryTree();
    private void SettingsFilter_Changed(object sender, RoutedEventArgs e) => _settingsView?.Refresh();
    private void SetSelections(IEnumerable<ProductChoice> products, bool selected)
    {
        _suppress = true;
        try { foreach (var product in products) product.IsSelected = selected; }
        finally { _suppress = false; _plan = null; UpdateControls(); }
    }
    private void SelectVisible_Click(object sender, RoutedEventArgs e) => SetSelections(FilamentTree.Flatten(_productNodes).Where(n => n.Product is not null).Select(n => n.Product!), true);
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => SetSelections(_products, false);
    private void Dark_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.Apply(DarkCheck.IsChecked == true);
        if (!_workspace.IsPreview) AppSettingsStore.Save(new ImporterSettings { DarkMode = ThemeService.IsDark });
    }
    private async void OpenPackage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Filament package (*.bflib)|*.bflib", InitialDirectory = _workspace.LibraryFolder };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync("Reading package…", async () =>
        {
            var package = await Task.Run(() => PackageReader.Load(dialog.FileName));
            if (!package.Manifest.PrinterNeutral) throw new InvalidDataException("Use a printer-neutral manufacturer package with this workspace.");
            var target = Path.Combine(_workspace.LibraryFolder, Path.GetFileName(dialog.FileName));
            if (!Path.GetFullPath(target).Equals(Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(target) && MessageBox.Show(this, "Replace the downloaded copy of this package? Installed profiles will not change.", "Package already downloaded", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                File.Copy(dialog.FileName, target, true);
            }
            await ReloadAsync();
            VendorFilter.SelectedItem = package.Manifest.Manufacturer;
            Log("Package ready. Select products to review an import.");
        });
    }
    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        var products = _products.Where(p => p.IsSelected).ToList();
        var printers = SelectedPrinters();
        var useCustom = UseCustomCheck.IsChecked == true;
        await RunAsync("Building a change plan…", async () =>
        {
            _plan = await Task.Run(() => ImportSelectionPlan.Create(_workspace.Paths, products, printers, useCustom));
            ReviewGrid.ItemsSource = _plan.Changes;
            ReviewSummary.Text = $"{products.Count} filament{(products.Count == 1 ? "" : "s")} · {_plan.Changes.Count} profile changes · {string.Join(", ", printers.Select(p => p.DisplayName))}\n" +
                (useCustom ? "Custom Library only. Saved separately from Bambu's system catalog." : "Bambu System Library. Grouped by brand in Studio, with a recovery backup in BFI.");
            BrowsePanel.Visibility = SuccessPanel.Visibility = Visibility.Collapsed;
            ReviewPanel.Visibility = Visibility.Visible;
            Log(_plan.HasChanges ? "Review is ready. Nothing has been installed yet." : "Everything selected is already available for these printers and destinations.");
        });
    }
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null) return;
        var plan = _plan;
        await RunAsync("Backing up and installing…", async () =>
        {
            var result = await Task.Run(() => plan.Apply(_workspace));
            await ReloadAsync();
            BrowsePanel.Visibility = ReviewPanel.Visibility = Visibility.Collapsed;
            SuccessPanel.Visibility = Visibility.Visible;
            SuccessSummary.Text = $"{result.WrittenFiles.Count} profile files written; {result.ProjectPresetEntriesAdded.Count} project dropdown entries added.\n\n" +
                (_workspace.IsLive ? "Open Bambu Studio and select the target printer/nozzle to use your filaments." : "This was a real import into your test copy. Bambu Studio's live profiles were not changed. Inspect the results in My Library.") + "\n\nThe full backup is available in Maintenance.";
            Log("Import complete and catalog validation passed.");
        });
    }
    private void Back_Click(object sender, RoutedEventArgs e) { _plan = null; BrowsePanel.Visibility = Visibility.Visible; ReviewPanel.Visibility = SuccessPanel.Visibility = Visibility.Collapsed; UpdateControls(); }
    private void ViewLibrary_Click(object sender, RoutedEventArgs e) => Navigation.SelectedIndex = 1;
    private bool CanLeaveEditor()
    {
        if (_suppress || !_settings.Any(s => s.IsModified)) return true;
        return MessageBox.Show(this, "Discard the unsaved settings on this profile?", "Unsaved changes", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }
    private void Navigation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Navigation || _suppress || !_ready) return;
        if (!CanLeaveEditor()) { _suppress = true; Navigation.SelectedIndex = _lastTab; _suppress = false; return; }
        if (_settings.Any(s => s.IsModified) && _editing is not null) LoadEditor(_editing);
        _lastTab = Navigation.SelectedIndex;
    }
    private void ClearEditor()
    {
        _editing = null; _chosenProduct = null; _chosenNode = null;
        _settings.Clear(); ProfilePicker.ItemsSource = null;
        EditorTitle.Text = "Choose a filament"; ScopeText.Text = "";
    }
    private void LibrarySelection_Changed(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppress) return;
        if (e.NewValue is not FilamentTreeNode node || node.LibraryProduct is null) return;
        if (!CanLeaveEditor())
        {
            _suppress = true; node.IsTreeSelected = false;
            if (_chosenNode is not null) _chosenNode.IsTreeSelected = true;
            _suppress = false; return;
        }
        _chosenNode = node;
        _chosenProduct = node.LibraryProduct;
        _suppress = true;
        _editing = null; _settings.Clear();
        EditorTitle.Text = _chosenProduct.Name;
        ProfilePicker.ItemsSource = _chosenProduct.Profiles.OrderBy(p => p.IsBaseProfile).ThenBy(p => p.Name).Select(p => new ProfileOption(p)).ToList();
        ProfilePicker.SelectedIndex = -1;
        ScopeText.Text = _chosenProduct.StorageLabel + ". Choose the printer profile explicitly. Shared bases affect all inheriting profiles.";
        _suppress = false; UpdateControls();
    }
    private void ProfileSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (!CanLeaveEditor())
        {
            _suppress = true;
            ProfilePicker.SelectedItem = ProfilePicker.Items.Cast<ProfileOption>().FirstOrDefault(p => p.Entry == _editing);
            _suppress = false; return;
        }
        if (ProfilePicker.SelectedItem is ProfileOption option) LoadEditor(option.Entry);
    }
    private void LoadEditor(CurrentFilamentEntry profile)
    {
        _editing = profile; _settings.Clear();
        var service = new FilamentSettingsService(_workspace.Paths);
        foreach (var setting in service.Load(profile))
        {
            setting.PropertyChanged += (_, _) => UpdateControls();
            _settings.Add(setting);
        }
        _editHash = FileHash(profile.ProfilePath);
        CategoryFilter.ItemsSource = new[] { "All settings" }.Concat(_settings.Select(s => s.Category).Distinct()).ToList();
        CategoryFilter.SelectedIndex = 0;
        ScopeText.Text = (profile.StorageKind == FilamentStorageKind.UserPreset ? "Custom Library" : "Bambu System Library · Studio updates may replace these changes") + "\n" +
            (profile.IsBaseProfile ? "SHARED BASE: changes are inherited by dependent printer profiles."
            : profile.Name + "\nCompatible: " + string.Join(", ", profile.CompatiblePrinters));
        UpdateControls();
    }
    private static string FileHash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "missing";
    private void CheckEditCurrent()
    {
        _workspace.EnsureWritable();
        if (_editing is null || _editHash != FileHash(_editing.ProfilePath)) throw new IOException("This profile changed since it was opened. Reselect it before editing.");
    }
    private void Setting_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is ProfileSettingEntry setting && !setting.IsEditable) e.Cancel = true;
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        SettingsGrid.CommitEdit(DataGridEditingUnit.Cell, true); SettingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_editing is null) return;
        if (_editing.IsBaseProfile && MessageBox.Show(this, "Save changes to this shared base? All dependent profiles inherit these changes unless they override them.", "Shared base", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var profile = _editing; var values = _settings.ToList();
        await RunAsync("Backing up and saving settings…", async () =>
        {
            CheckEditCurrent();
            var count = await Task.Run(() => LibraryTransaction.Run(_workspace, "edit", () => new FilamentSettingsService(_workspace.Paths).Save(profile, values)));
            LoadEditor(profile); LoadBackups(); Log($"Saved {count} settings on {profile.Name}.");
        });
    }
    private void Discard_Click(object sender, RoutedEventArgs e) { if (_editing is not null && CanLeaveEditor()) LoadEditor(_editing); }
    private async void ResetOverride_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null || SettingsGrid.SelectedItem is not ProfileSettingEntry setting || !setting.IsDirect || !setting.IsEditable)
        { MessageBox.Show(this, "Select an editable setting that is defined on this profile.", "Use inherited value"); return; }
        if (!CanLeaveEditor()) return;
        var profile = _editing;
        var json = BambuJson.Parse(File.ReadAllText(profile.ProfilePath))!.AsObject();
        if (json["inherits"] is null) { MessageBox.Show(this, "This profile has no parent to inherit from."); return; }
        if (MessageBox.Show(this, $"Remove the local override for {setting.DisplayName}? The inherited value will be used.", "Use inherited value", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await RunAsync("Resetting override…", async () =>
        {
            CheckEditCurrent();
            await Task.Run(() => LibraryTransaction.Run(_workspace, "reset", () =>
            {
                json.Remove(setting.Key);
                File.WriteAllText(profile.ProfilePath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return true;
            }));
            LoadEditor(profile); LoadBackups(); Log("The local override was removed.");
        });
    }
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null || !CanLeaveEditor()) return;
        var profile = _editing;
        if (MessageBox.Show(this, $"Remove this one profile from {(_workspace.IsLive ? "your live Studio library" : "the test copy")}?\n\n{profile.Name}\n\nA backup will be created. Profiles with dependent children cannot be removed alone.", "Remove profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync("Backing up and removing profile…", async () =>
        {
            CheckEditCurrent();
            await Task.Run(() => LibraryTransaction.Run(_workspace, "remove", () => new BambuLibraryEditor(_workspace.Paths).Remove(profile)));
            await ReloadAsync(); Log("Profile removed; catalog validation passed.");
        });
    }
    private void LoadBackups() => BackupList.ItemsSource = Directory.EnumerateFiles(_workspace.BackupFolder, "*.bflbackup")
        .Select(p => new FileInfo(p)).OrderByDescending(p => p.LastWriteTime).ToList();
    private async void StudioRecovery_Click(object sender, RoutedEventArgs e) => await CheckStudioRecoveryAsync(true);
    private async Task CheckStudioRecoveryAsync(bool manual)
    {
        if (_workspace.IsPreview) return;
        await RunAsync("Checking Studio update recovery…", async () =>
        {
            var service = new StudioRecoveryService(_workspace);
            var offer = await Task.Run(service.Check);
            if (offer is not null)
            {
                StudioRecoveryText.Text = $"Studio changed from {offer.Saved.StudioVersion} to {offer.CurrentVersion}. Your backup from {offer.Saved.CreatedUtc.ToLocalTime():g} is kept until you restore it.";
                if (!manual && _deferredRecoveryVersion == offer.CurrentVersion) return;
                var answer = MessageBox.Show(this,
                    $"Bambu Studio has been updated since you last opened BFI ({offer.Saved.StudioVersion} → {offer.CurrentVersion}).\n\nYour imported filaments may have been overwritten by this update. Would you like to restore your last saved filament configuration?\n\nBackup: {offer.Saved.CreatedUtc.ToLocalTime():g} · {offer.Saved.ProfileNames.Count} imported system profiles.\n\nThis restores saved settings for these profiles and merges their library entries. New Bambu profiles, custom presets, and unrelated Studio settings are kept. Close Bambu Studio before restoring.\n\nChoose No to do this later from Maintenance; the backup will be kept.",
                    "Restore filaments after Studio update?", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) { _deferredRecoveryVersion = offer.CurrentVersion; return; }
                if (!CanLeaveEditor()) return;
                await Task.Run(() => service.Restore(offer));
                _deferredRecoveryVersion = null;
                await ReloadAsync();
                StudioRecoveryText.Text = $"Imported system profiles restored for Studio {offer.CurrentVersion}. Recovery backup refreshed.";
                Log("Your saved filament configuration was restored. You can reopen Bambu Studio.");
                return;
            }
            if (BambuProcess.IsStudioRunning() || WorkspaceContext.IsAnotherWorkspaceRunning())
            {
                StudioRecoveryText.Text = "Close Bambu Studio and other BFI windows to refresh the recovery backup. Any previous recovery point is kept.";
                if (manual) MessageBox.Show(this, StudioRecoveryText.Text, "Studio update recovery");
                return;
            }
            var saved = await Task.Run(() => service.Capture());
            var state = service.ReadState();
            StudioRecoveryText.Text = saved && state is not null
                ? $"Recovery ready for Studio {state.StudioVersion}: {state.ProfileNames.Count} imported profiles, saved {state.CreatedUtc.ToLocalTime():g}."
                : "Recovery backup was not refreshed. The Studio version may be unavailable or previously backed-up profiles may be missing. Existing backups are kept.";
            if (manual) MessageBox.Show(this, StudioRecoveryText.Text, "Studio update recovery");
        });
    }
    private async void Backup_Click(object sender, RoutedEventArgs e) => await RunAsync("Creating backup…", async () =>
    { var path = await Task.Run(() => _workspace.CreateBackup("manual")); LoadBackups(); Log("Backup saved: " + Path.GetFileName(path)); });
    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(_workspace.BackupFolder) { UseShellExecute = true });
    private async void RestoreSelected_Click(object sender, RoutedEventArgs e)
    { if (BackupList.SelectedItem is FileInfo file) await RestoreAsync(file.FullName); else MessageBox.Show(this, "Choose a backup from the list first."); }
    private async void RestoreFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "BFI backup (*.bflbackup)|*.bflbackup", InitialDirectory = _workspace.BackupFolder };
        if (dialog.ShowDialog(this) == true) await RestoreAsync(dialog.FileName);
    }
    private async Task RestoreAsync(string path)
    {
        if (!CanLeaveEditor()) return;
        await RunAsync("Inspecting backup…", async () =>
        {
            var service = new LibraryBackupService(_workspace.Paths, () => _workspace.IsLive && BambuProcess.IsStudioRunning());
            var summary = await Task.Run(() => service.Inspect(path));
            if (MessageBox.Show(this, $"Restore into {(_workspace.IsLive ? "LIVE STUDIO LIBRARY" : "TEST COPY")}?\n\n{summary.CatalogProfiles} catalog profiles\n{summary.UserPresetFiles} user preset files\nCreated: {summary.CreatedUtc}\n\nMatching files may be overwritten, including newer tuning. Unrelated profiles stay. A new safety backup will be created first.", "Review restoration", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await Task.Run(() => LibraryTransaction.Run(_workspace, "before-restore", () => service.Restore(path)));
            await ReloadAsync(); Log("Backup restored and catalog validated.");
        });
    }
    private async void Audit_Click(object sender, RoutedEventArgs e) => await RunAsync("Checking catalog and AMS IDs…", async () =>
    {
        var audit = await Task.Run(() => { BambuCatalogIntegrity.ValidateProfileRoot(_workspace.Paths.RoamingProfileRoot); return new AmsFilamentIdRepairService(_workspace.Paths).Audit(); });
        HealthText.Text = $"Catalog validation passed. AMS ID audit: {audit.AffectedProducts} product(s) need attention.";
        Log(HealthText.Text);
    });
    private void RepairCoverage_Click(object sender, RoutedEventArgs e)
    {
        if (!CanLeaveEditor()) return;
        var targets = SelectedPrinters();
        if (!targets.Any()) { MessageBox.Show(this, "Select the printers and nozzles on Add Filaments first."); return; }
        var count = 0;
        _suppress = true;
        try { foreach (var product in _products)
        {
            var exists = _current.Any(p => p.CanEdit && p.ProductName.Equals(product.Name, StringComparison.OrdinalIgnoreCase));
            var profile = new FilamentProfileEntry { Name = product.ProfileName };
            product.IsSelected = exists && targets.Any(t => !PrinterProfileExpander.IsTargetCovered(profile, t, _current,
                UseCustomCheck.IsChecked == true ? ImportDestination.ProjectLibrary : ImportDestination.DeviceAms));
            if (product.IsSelected) count++;
        } }
        finally { _suppress = false; _plan = null; UpdateControls(); }
        Navigation.SelectedIndex = 0; Back_Click(this, new RoutedEventArgs());
        ProductSearch.Clear(); VendorFilter.SelectedIndex = 0;
        Log($"Selected {count} existing products with missing target-printer coverage. Review before repairing.");
    }
    private async void Downloads_Click(object sender, RoutedEventArgs e) => await RunAsync("Checking manufacturer downloads…", async () =>
    {
        if (!CanLeaveEditor()) return;
        using var service = new ManufacturerLibraryUpdateService();
        var catalog = await service.GetCatalogAsync(_workspace.LibraryFolder);
        _libraryNotice = LibraryUpdateNotice.From(catalog, ManufacturerLibraryCatalogStateStore.Load(_workspace.CatalogStatePath));
        ManufacturerLibraryCatalogStateStore.Save(catalog.Entries.Select(e => e.Package.PackageId), _workspace.CatalogStatePath);
        // Downloading a package is separate from installing its profiles into Studio.
        var dialog = new LibraryCatalogWindow(catalog, _libraryNotice.NewPackageIds) { Owner = this, Title = "Download manufacturer libraries" };
        if (dialog.FindName("CatalogSummaryText") is TextBlock summary) summary.Text = $"{catalog.Entries.Count} libraries · {catalog.InstalledCount} downloaded · {catalog.Updates.Count} package updates";
        if (dialog.FindName("CatalogGrid") is DataGrid grid)
            foreach (var column in grid.Columns.OfType<DataGridTextColumn>().Where(c => c.Header?.ToString() == "Status"))
                column.Binding = new Binding("StatusText") { Converter = new DownloadStatusConverter() };
        if (dialog.ShowDialog() != true) { ShowLibraryNotice(catalog); return; }
        var result = await service.InstallAsync(catalog, dialog.SelectedPackages);
        await ReloadAsync(); Log($"Downloaded {result.UpdatedFiles.Count} package(s). Installed profiles were not changed.");
        ShowLibraryNotice(await service.GetCatalogAsync(_workspace.LibraryFolder));
    });

    private void ShowLibraryNotice(ManufacturerLibraryCatalog catalog)
    {
        var state = ManufacturerLibraryCatalogStateStore.Load(_workspace.CatalogStatePath);
        _libraryNotice = LibraryUpdateNotice.From(catalog, state);
        LibraryUpdateText.Text = _libraryNotice.Text;
        LibraryUpdateBanner.Visibility = _libraryNotice.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        if (!state.HasSavedState)
            ManufacturerLibraryCatalogStateStore.Save(catalog.Entries.Select(e => e.Package.PackageId), _workspace.CatalogStatePath);
    }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(true);
    private async Task CheckUpdatesAsync(bool manual)
    {
        if (_checkingUpdates) return;
        _checkingUpdates = true;
        UpdateControls();
        var failures = new List<string>();
        async Task AppCheck()
        {
            try
            {
                var release = await UpdateService.CheckAsync();
                _availableAppUpdate = UpdateService.IsNewer(release) ? release : null;
                AppUpdateText.Text = $"BFI {release.Version.ToString(3)} is available. Your libraries and settings are kept when updating.";
                AppUpdateBanner.Visibility = _availableAppUpdate is null || _workspace.IsPreview ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception ex) { failures.Add("BFI update check: " + ex.Message); }
        }
        async Task LibraryCheck()
        {
            try
            {
                using var service = new ManufacturerLibraryUpdateService();
                ShowLibraryNotice(await service.GetCatalogAsync(_workspace.LibraryFolder));
            }
            catch (Exception ex) { failures.Add("Library update check: " + ex.Message); }
        }
        try
        {
            await Task.WhenAll(AppCheck(), LibraryCheck());
            if (failures.Count > 0) Log(string.Join("; ", failures));
            else if (manual) Log(_availableAppUpdate is null && _libraryNotice?.IsVisible != true
                ? "BFI and your downloaded libraries are up to date." : "Update check complete. Review the notices above.");
        }
        finally { _checkingUpdates = false; UpdateControls(); }
    }
    private async void UpdateApp_Click(object sender, RoutedEventArgs e)
    {
        if (_availableAppUpdate is null || _workspace.IsPreview || !CanLeaveEditor()) return;
        var release = _availableAppUpdate;
        if (MessageBox.Show(this, $"Download and install BFI {release.Version.ToString(3)}? BFI will close and reopen. Filament profiles are not changed.", "Update BFI", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await RunAsync("Downloading BFI update…", async () =>
        {
            var executable = await UpdateService.DownloadAndExtractAsync(release);
            UpdateService.LaunchUpdater(executable);
            _suppress = true; _busy = false;
            Application.Current.Shutdown();
        });
    }
    private void ReportBug_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var discovery = new PrinterDiscoveryService(_workspace.Paths).Discover();
            var dialog = new BugReportWindow(_workspace.Paths, discovery,
                $"Compact workspace; mode: {(_workspace.IsLive ? "Live" : "Test copy")}; selected products: {_products.Count(p => p.IsSelected)}") { Owner = this };
            if (dialog.ShowDialog() != true || dialog.ReportResult is null) return;
            var report = dialog.ReportResult;
            try { Clipboard.SetText(report.IssueBody); } catch { }
            Process.Start(new ProcessStartInfo(report.IssueUrl) { UseShellExecute = true });
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{report.ZipPath}\"") { UseShellExecute = true });
            Log("Diagnostic report created locally. Review it before attaching it to your issue; nothing was uploaded.");
        }
        catch (Exception ex) { Log(ex.Message); MessageBox.Show(this, ex.Message, "Report a problem"); }
    }
    private async void AdvancedTools_Click(object sender, RoutedEventArgs e)
    {
        if (!_workspace.IsLive) { MessageBox.Show(this, "Advanced tools operate on the live library. The separate development preview does not offer advanced live tools."); return; }
        if (!CanLeaveEditor()) return;
        try { _workspace.EnsureWritable(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Advanced tools"); return; }
        var window = new MainWindow { StartupUpdateChecksEnabled = false, Owner = this };
        window.ShowDialog();
        await RunAsync("Refreshing library…", ReloadAsync);
    }

}

public sealed record LibraryProduct(string Name, List<CurrentFilamentEntry> Profiles, string? Manufacturer = null)
{
    public string Brand => Manufacturer ?? FilamentTree.NormalizeBrand(Profiles[0].VendorGroup, Profiles[0].MaterialFamily);
    public string Material => Profiles[0].MaterialFamily;
    public FilamentStorageKind StorageKind => Profiles[0].StorageKind;
    public string StorageLabel => StorageKind == FilamentStorageKind.UserPreset ? "Custom Library" : "Bambu System Library";
    public string Summary => $"{Brand} · {Material} · {StorageLabel} · {Profiles.Count} profiles";
}
public sealed record ProfileOption(CurrentFilamentEntry Entry)
{
    public string Label => Entry.IsBaseProfile ? "Shared base · affects dependent printers" : Entry.Name + (Entry.StorageKind == FilamentStorageKind.UserPreset ? " · User preset" : " · Device/AMS");
}
public class ToggleChoice : INotifyPropertyChanged
{
    private bool _selected = true;
    public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed class NozzleChoice(string name) : ToggleChoice
{
    public string Name { get; } = name;
    public string Label => Regex.Match(Name, @"[\d.]+ nozzle.*$").Success ? Regex.Match(Name, @"[\d.]+ nozzle.*$").Value : Name;
}
public sealed class PrinterChoice : ToggleChoice
{
    public PrinterTarget Target { get; }
    public List<NozzleChoice> Nozzles { get; }
    public string Note => Target.IsInferred ? "Inferred model · review before selecting" : "Matched from local Studio records";
    public PrinterChoice(PrinterTarget target) { Target = target; IsSelected = target.IsSelected; Nozzles = target.MachinePresetNames.Select(n => new NozzleChoice(n)).ToList(); }
    public PrinterTarget ToTarget() => new() { Vendor = Target.Vendor, ModelName = Target.ModelName, ProfileSuffix = Target.ProfileSuffix,
        MachinePresetNames = Nozzles.Where(n => n.IsSelected).Select(n => n.Name).ToList(),
        NozzleSummary = string.Join(", ", Nozzles.Where(n => n.IsSelected).Select(n => Regex.Match(n.Name, @"([\d.]+) nozzle").Groups[1].Value)), IsSelected = true };
}
public sealed class DownloadStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value?.ToString() == "Installed" ? "Downloaded" : value ?? "";
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
