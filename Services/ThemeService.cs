using System.Windows;
using System.Windows.Media;

namespace BambuFilamentImporter.Services;

public static class ThemeService
{
    public static bool IsDark { get; private set; }

    public static void Apply(bool darkMode)
    {
        IsDark = darkMode;
        var colors = darkMode
            ? new Dictionary<string, string>
            {
                ["WindowBackgroundBrush"] = "#17191D",
                ["SurfaceBrush"] = "#22252A",
                ["SurfaceAltBrush"] = "#2B2F35",
                ["TextBrush"] = "#F1F3F5",
                ["MutedTextBrush"] = "#A9B0BA",
                ["BorderBrush"] = "#454B54",
                ["InputBackgroundBrush"] = "#292D33",
                ["SelectionBrush"] = "#315B48",
                ["WarningBrush"] = "#5A4B22",
                ["DangerBrush"] = "#FF7A7A",
                ["SuccessBrush"] = "#53D68A",
                ["BusyBrush"] = "#AEBBFF"
            }
            : new Dictionary<string, string>
            {
                ["WindowBackgroundBrush"] = "#F3F5F8",
                ["SurfaceBrush"] = "#FFFFFF",
                ["SurfaceAltBrush"] = "#F7F9FC",
                ["TextBrush"] = "#202733",
                ["MutedTextBrush"] = "#627083",
                ["BorderBrush"] = "#D8DEE8",
                ["InputBackgroundBrush"] = "#FFFFFF",
                ["SelectionBrush"] = "#DDEFE6",
                ["WarningBrush"] = "#FFF3C4",
                ["DangerBrush"] = "#9B1C31",
                ["SuccessBrush"] = "#147A43",
                ["BusyBrush"] = "#4E5D94"
            };

        foreach (var pair in colors)
        {
            Application.Current.Resources[pair.Key] = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(pair.Value));
        }

        Application.Current.Resources[SystemColors.HighlightBrushKey] = Application.Current.Resources["SelectionBrush"];
        Application.Current.Resources[SystemColors.HighlightTextBrushKey] = Application.Current.Resources["TextBrush"];
        Application.Current.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = Application.Current.Resources["SelectionBrush"];
        Application.Current.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = Application.Current.Resources["TextBrush"];
    }

    public static System.Windows.Media.Brush Brush(string key) =>
        (System.Windows.Media.Brush)Application.Current.Resources[key];
}
