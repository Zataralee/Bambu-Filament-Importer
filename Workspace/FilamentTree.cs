using System.ComponentModel;
using BambuFilamentImporter.Models;

namespace BambuFilamentImporter.Workspace;

public sealed class FilamentTreeNode : INotifyPropertyChanged
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string Detail { get; init; } = "";
    public List<FilamentTreeNode> Children { get; init; } = [];
    public ProductChoice? Product { get; init; }
    public LibraryProduct? LibraryProduct { get; init; }
    public bool HasProduct => Product is not null;
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    private bool _expanded, _selected;
    public bool IsExpanded { get => _expanded; set { if (_expanded == value) return; _expanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); } }
    public bool IsTreeSelected { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsTreeSelected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class FilamentTree
{
    private static string Brand(string value) => string.IsNullOrWhiteSpace(value) ? "Unspecified brand" : value.Trim();
    private static string Material(string value) => string.IsNullOrWhiteSpace(value) ? "Unspecified type" : value.Trim();
    public static string NormalizeBrand(string vendor, string material)
    {
        var value = Brand(vendor);
        var suffix = " " + material.Trim();
        return !string.IsNullOrWhiteSpace(material) && value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? value[..^suffix.Length] : value;
    }
    public static List<FilamentTreeNode> Catalog(IEnumerable<ProductChoice> products, string search = "", string vendor = "All manufacturers")
    {
        var query = search.Trim();
        return products.Where(p => (vendor == "All manufacturers" || p.Manufacturer == vendor)
                && (p.Name + " " + p.Manufacturer + " " + p.Material).Contains(query, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => Brand(p.Manufacturer), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key)
            .Select(brand => new FilamentTreeNode { Key = "catalog/" + brand.Key, Name = brand.Key + $" ({brand.Count()})", IsExpanded = query.Length > 0,
                Children = brand.GroupBy(p => Material(p.Material), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key)
                    .Select(type => new FilamentTreeNode { Key = "catalog/" + brand.Key + "/" + type.Key, Name = type.Key + $" ({type.Count()})", IsExpanded = query.Length > 0,
                        Children = type.OrderBy(p => p.Name).Select(p => new FilamentTreeNode { Key = p.PackagePath + "|" + p.ProfileName, Name = p.Name, Detail = p.Coverage, Product = p }).ToList() }).ToList() }).ToList();
    }
    public static List<LibraryProduct> Products(IEnumerable<CurrentFilamentEntry> profiles, IEnumerable<string>? knownBrands = null)
    {
        var brands = (knownBrands ?? []).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(b => b.Length).ToList();
        var materialGroups = new HashSet<string>(["PLA", "PETG", "ABS", "ASA", "TPU", "PA", "PC", "PVA", "PET", "PPS", "PPA", "HIPS"], StringComparer.OrdinalIgnoreCase);
        string CanonicalBrand(CurrentFilamentEntry p) => brands.FirstOrDefault(b => p.VendorGroup.Equals(b, StringComparison.OrdinalIgnoreCase)
            || (p.VendorGroup.StartsWith(b + " ", StringComparison.OrdinalIgnoreCase) && materialGroups.Contains(p.VendorGroup[(b.Length + 1)..])))
            ?? NormalizeBrand(p.VendorGroup, p.MaterialFamily);
        return profiles
        .Where(p => p.CanEdit && !p.Name.StartsWith("fdm_", StringComparison.OrdinalIgnoreCase))
        .GroupBy(p => string.Join("|", p.StorageKind, CanonicalBrand(p), Material(p.MaterialFamily), p.ProductName), StringComparer.OrdinalIgnoreCase)
        .Select(g => new LibraryProduct(g.First().ProductName, g.ToList(), CanonicalBrand(g.First()))).OrderBy(p => p.Name).ToList();
    }

    public static List<FilamentTreeNode> Library(IEnumerable<LibraryProduct> products, string search = "")
    {
        var query = search.Trim();
        return products.Where(p => (p.Name + " " + p.Brand + " " + p.Material + " " + p.StorageLabel).Contains(query, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.StorageKind).OrderByDescending(g => g.Key == FilamentStorageKind.UserPreset)
            .Select(storage => new FilamentTreeNode { Key = "library/" + storage.Key, Name = storage.First().StorageLabel, IsExpanded = true,
                Children = storage.GroupBy(p => Brand(p.Brand), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key)
                    .Select(brand => new FilamentTreeNode { Key = $"library/{storage.Key}/{brand.Key}", Name = brand.Key + $" ({brand.Count()})", IsExpanded = query.Length > 0,
                        Children = brand.GroupBy(p => Material(p.Material), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key)
                            .Select(type => new FilamentTreeNode { Key = $"library/{storage.Key}/{brand.Key}/{type.Key}", Name = type.Key + $" ({type.Count()})", IsExpanded = query.Length > 0,
                                Children = type.OrderBy(p => p.Name).Select(p => new FilamentTreeNode { Key = p.Profiles[0].ProfilePath, Name = p.Name, Detail = $"{p.Profiles.Count} profile(s)", LibraryProduct = p }).ToList() }).ToList() }).ToList() }).ToList();
    }
    public static IEnumerable<FilamentTreeNode> Flatten(IEnumerable<FilamentTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }
    public static List<CurrentFilamentEntry> DeletionProfiles(FilamentTreeNode? node)
    {
        // Library headings are navigation, not a delete-all action.
        if (node is null || (node.LibraryProduct is null && node.Key.Split('/').Length < 3)) return [];
        return Flatten([node]).Where(n => n.LibraryProduct is not null)
            .SelectMany(n => n.LibraryProduct!.Profiles).Where(p => p.CanEdit)
            .DistinctBy(p => p.ProfilePath, StringComparer.OrdinalIgnoreCase).ToList();
    }
    public static void PreserveExpansion(IEnumerable<FilamentTreeNode> previous, IEnumerable<FilamentTreeNode> next)
    {
        var expanded = Flatten(previous).ToDictionary(n => n.Key, n => n.IsExpanded, StringComparer.OrdinalIgnoreCase);
        foreach (var node in Flatten(next))
            if (expanded.TryGetValue(node.Key, out var value)) node.IsExpanded = value;
    }
}
