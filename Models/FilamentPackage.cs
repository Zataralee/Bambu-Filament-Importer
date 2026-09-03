using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BambuFilamentImporter.Models;

public sealed class FilamentPackage
{
    public string Format { get; set; } = "bambu-filament-library";
    public int FormatVersion { get; set; } = 1;
    public string PackageId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Version { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public bool PrinterNeutral { get; set; }
    public string CompatibilityNote { get; set; } = "";
    public List<string> SourceUrls { get; set; } = [];
    public List<FilamentProfileEntry> Profiles { get; set; } = [];
    public List<string> ProjectPresetNames { get; set; } = [];
}

public sealed class FilamentProfileEntry : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private string _name = "";
    private string _vendorGroup = "";
    private string _status = "";
    private bool _isDuplicate;

    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }
    public string Name { get => _name; set => SetField(ref _name, value); }
    public string OriginalName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string OriginalRelativePath { get; set; } = "";
    public string Kind { get; set; } = "system";
    public string VendorGroup { get => _vendorGroup; set => SetField(ref _vendorGroup, value); }
    public string MaterialFamily { get; set; } = "";
    public string Status { get => _status; set => SetField(ref _status, value); }
    public bool IsDuplicate { get => _isDuplicate; set => SetField(ref _isDuplicate, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum FilamentStorageKind
{
    SystemCatalog,
    UserPreset
}

public sealed class CurrentFilamentEntry
{
    public string Name { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string VendorGroup { get; set; } = "";
    public string MaterialFamily { get; set; } = "";
    public string Source { get; set; } = "";
    public string Location { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string ProfileRoot { get; set; } = "";
    public string ProfilePath { get; set; } = "";
    public string InfoPath { get; set; } = "";
    public bool IsProjectPreset { get; set; }
    public bool CanEdit { get; set; }
    public List<string> CompatiblePrinters { get; set; } = [];
    public FilamentStorageKind StorageKind { get; set; }
    public List<FilamentProfileCopy> AdditionalCopies { get; set; } = [];
    public int CopyCount => 1 + AdditionalCopies.Count;
    public bool HasInstalledMirror => AdditionalCopies.Any(copy => copy.Source.StartsWith("Installed", StringComparison.OrdinalIgnoreCase));
    public bool IsBaseProfile => Name.EndsWith("@base", StringComparison.OrdinalIgnoreCase);
    public string ProductName => GetProductName(Name);

    public static string GetProductName(string name)
    {
        var marker = name.LastIndexOf(" @", StringComparison.Ordinal);
        return marker > 0 ? name[..marker] : name;
    }
}

public sealed class FilamentProfileCopy
{
    public string Source { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string ProfileRoot { get; set; } = "";
    public string ProfilePath { get; set; } = "";
    public string InfoPath { get; set; } = "";
    public FilamentStorageKind StorageKind { get; set; }
}

public sealed class FilamentProductGroup
{
    public string ProductName { get; set; } = "";
    public List<CurrentFilamentEntry> Items { get; set; } = [];
    public int Count => Items.Count;
    public int EditableCount => Items.Count(item => item.CanEdit);
    public string Header => Count == 1 ? ProductName : $"{ProductName} ({Count} profiles)";
}

public sealed class FilamentGroup
{
    public string VendorGroup { get; set; } = "";
    public List<FilamentProductGroup> Items { get; set; } = [];
    public int ProductCount => Items.Count;
    public int ProfileCount => Items.Sum(item => item.Count);
    public int EditableCount => Items.Sum(item => item.EditableCount);
    public IEnumerable<CurrentFilamentEntry> Profiles => Items.SelectMany(item => item.Items);
    public string Header => $"{VendorGroup} ({ProductCount} filaments)";
}

public sealed class ProfileSettingEntry : INotifyPropertyChanged
{
    private string _value = "";

    public string Key { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Category { get; init; } = "";
    public string SourceProfile { get; init; } = "";
    public string ValueFormat { get; init; } = "";
    public string OriginalJson { get; init; } = "";
    public bool IsDirect { get; init; }
    public bool IsEditable { get; init; }
    public string Origin => IsDirect ? "This profile" : $"Inherited: {SourceProfile}";
    public bool IsModified => !string.Equals(_value, OriginalValue, StringComparison.Ordinal);
    public string OriginalValue { get; init; } = "";

    public string Value
    {
        get => _value;
        set
        {
            if (string.Equals(_value, value, StringComparison.Ordinal))
            {
                return;
            }

            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsModified)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public enum ImportDestination
{
    DeviceAms,
    ProjectLibrary,
    Both
}

public sealed class LoadedFilamentPackage
{
    public required string FilePath { get; init; }
    public required FilamentPackage Manifest { get; init; }
    public required IReadOnlyDictionary<string, string> ProfileJsonByPath { get; init; }
}

public sealed class PrinterTarget : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public string Vendor { get; init; } = "";
    public string ModelName { get; init; } = "";
    public string ProfileSuffix { get; init; } = "";
    public List<string> MachinePresetNames { get; init; } = [];
    public string NozzleSummary { get; init; } = "";
    public bool IsInferred { get; init; }
    public string DisplayName => $"{ModelName} ({NozzleSummary} mm){(IsInferred ? " - inferred, review" : "")}";
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
