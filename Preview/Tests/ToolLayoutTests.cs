using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BambuFilamentImporter.Models;
using BambuFilamentImporter.Services;
using BambuFilamentImporter.Workspace;

internal static class ToolLayoutTests
{
    public static void Run(WorkspaceWindow window, WorkspaceContext workspace, string repo, string folder, bool dark)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(WorkspaceWindow).GetField("_ready", flags)!.SetValue(window, true);
        var current = new BambuLibraryScanner(workspace.Paths).LoadCurrentFilaments();
        typeof(WorkspaceWindow).GetField("_current", flags)!.SetValue(window, current);
        var tabs = (TabControl)window.FindName("Navigation");
        tabs.SelectedIndex = 1;
        var tree = (TreeView)window.FindName("LibraryTree");
        var nodes = FilamentTree.Library(FilamentTree.Products(current));
        foreach (var node in FilamentTree.Flatten(nodes)) node.IsExpanded = true;
        tree.ItemsSource = nodes; window.UpdateLayout();
        foreach (var kind in new[] { FilamentStorageKind.SystemCatalog, FilamentStorageKind.UserPreset })
        {
            var leaf = FilamentTree.Flatten(nodes).First(n => n.LibraryProduct?.StorageKind == kind);
            leaf.IsTreeSelected = true; window.UpdateLayout();
            Check(((Button)window.FindName("DeleteSelectionButton")).IsEnabled && ((ComboBox)window.FindName("ProfilePicker")).SelectedIndex == -1,
                $"{kind} can be deleted directly without selecting a printer profile");
        }
        Save(window, Path.Combine(folder, dark ? "library-dark.png" : "library-light.png"));
        var before = Application.Current.Windows.Count;
        Invoke("ShowLibraryTools_Click"); window.UpdateLayout();
        Check(((FrameworkElement)window.FindName("AdvancedLibraryPanel")).IsVisible && !((FrameworkElement)window.FindName("LibraryMainPanel")).IsVisible
            && tabs.SelectedIndex == 1 && Application.Current.Windows.Count == before, "advanced library tools stay in My Library in the same window");
        ((ComboBox)window.FindName("AdvancedProfilePicker")).SelectedIndex = 0; window.UpdateLayout();
        Check(((Button)window.FindName("RenameProfileButton")).IsEnabled && ((Button)window.FindName("AdvancedRemoveProfileButton")).IsEnabled,
            "advanced library profile selection enables rename and delete in place");
        Save(window, Path.Combine(folder, dark ? "library-tools-dark.png" : "library-tools-light.png"));
        Invoke("HideLibraryTools_Click");
        var package = PackageReader.Load(Path.Combine(repo,"packages","manufacturers","Overture.bflib"));
        typeof(WorkspaceWindow).GetMethod("LoadAdvancedPackage", flags)!.Invoke(window, [package]);
        window.UpdateLayout();
        Check(((FrameworkElement)window.FindName("AdvancedImportPanel")).IsVisible && tabs.SelectedIndex == 0
            && Application.Current.Windows.Count == before, "advanced package editing stays in Add Filaments in the same window");
        Save(window, Path.Combine(folder, dark ? "import-tools-dark.png" : "import-tools-light.png"));
        Invoke("HideImportTools_Click");
        Check(((FrameworkElement)window.FindName("BrowsePanel")).IsVisible && !((FrameworkElement)window.FindName("AdvancedImportPanel")).IsVisible, "back returns to the compact filament browser");
        void Invoke(string method) => typeof(WorkspaceWindow).GetMethod(method, flags)!.Invoke(window, [window, new RoutedEventArgs()]);
    }
    private static void Save(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); }
}
