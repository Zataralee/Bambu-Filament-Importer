using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using BambuFilamentImporter.Services;

namespace BambuFilamentImporter;

public partial class LibraryCatalogWindow : Window
{
    private readonly ObservableCollection<ManufacturerLibraryChoice> _choices;
    private readonly ICollectionView _choicesView;

    public IReadOnlyList<ManufacturerLibraryIndexEntry> SelectedPackages { get; private set; } = [];

    public LibraryCatalogWindow(
        ManufacturerLibraryCatalog catalog,
        IReadOnlySet<string>? newPackageIds = null)
    {
        InitializeComponent();
        var newIds = newPackageIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _choices = new ObservableCollection<ManufacturerLibraryChoice>(catalog.Entries
            .OrderBy(entry => entry.Package.Manufacturer, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Package.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new ManufacturerLibraryChoice(entry, newIds.Contains(entry.Package.PackageId))));
        _choicesView = CollectionViewSource.GetDefaultView(_choices);
        _choicesView.Filter = FilterChoice;
        CatalogGrid.ItemsSource = _choicesView;
        CatalogSummaryText.Text = $"{catalog.Entries.Count} optional libraries | {catalog.InstalledCount} installed | {catalog.Updates.Count} update(s) available";
    }

    private bool FilterChoice(object item)
    {
        if (item is not ManufacturerLibraryChoice choice)
        {
            return false;
        }

        var filter = CatalogSearchText.Text.Trim();
        return string.IsNullOrWhiteSpace(filter)
            || choice.Manufacturer.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || choice.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || choice.StatusText.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void CatalogSearchText_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        _choicesView?.Refresh();

    private void SelectUpdates_Click(object sender, RoutedEventArgs e) =>
        AddSelection(choice => choice.Status == ManufacturerLibraryStatus.UpdateAvailable);

    private void SelectAvailable_Click(object sender, RoutedEventArgs e) =>
        AddSelection(choice => choice.Status == ManufacturerLibraryStatus.Available);

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var choice in _choices)
        {
            choice.IsSelected = false;
        }
    }

    private void AddSelection(Func<ManufacturerLibraryChoice, bool> selector)
    {
        foreach (var choice in _choices)
        {
            if (choice.CanDownload && selector(choice))
            {
                choice.IsSelected = true;
            }
        }
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        SelectedPackages = _choices
            .Where(choice => choice.IsSelected && choice.CanDownload)
            .Select(choice => choice.Package)
            .ToList();
        if (SelectedPackages.Count == 0)
        {
            MessageBox.Show(
                this,
                "Select at least one available library or library update to download.",
                "No libraries selected",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class ManufacturerLibraryChoice : INotifyPropertyChanged
{
    private bool _isSelected;

    public ManufacturerLibraryChoice(ManufacturerLibraryCatalogEntry entry, bool isNew)
    {
        Package = entry.Package;
        Status = entry.Status;
        Manufacturer = entry.Package.Manufacturer;
        DisplayName = entry.Package.DisplayName;
        ProfileCount = entry.Package.ProfileCount;
        StatusText = entry.Status switch
        {
            ManufacturerLibraryStatus.UpdateAvailable => "Updates Available",
            ManufacturerLibraryStatus.Current => "Installed",
            _ when isNew => "New Library",
            _ => "Available"
        };
        VersionText = entry.Status == ManufacturerLibraryStatus.Current
            ? entry.InstalledVersion ?? entry.Package.Version
            : entry.Package.Version;
        CanDownload = entry.Status != ManufacturerLibraryStatus.Current;
        _isSelected = entry.Status == ManufacturerLibraryStatus.UpdateAvailable;
    }

    public ManufacturerLibraryIndexEntry Package { get; }
    public ManufacturerLibraryStatus Status { get; }
    public string Manufacturer { get; }
    public string DisplayName { get; }
    public int ProfileCount { get; }
    public string StatusText { get; }
    public string VersionText { get; }
    public bool CanDownload { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
