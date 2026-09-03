using System.Windows;
using BambuFilamentImporter.Services;

namespace BambuFilamentImporter;

public partial class BugReportWindow : Window
{
    private readonly BambuPaths _paths;
    private readonly PrinterDiscoveryResult _discovery;
    private readonly string _applicationContext;

    public BugReportResult? ReportResult { get; private set; }

    public BugReportWindow(
        BambuPaths paths,
        PrinterDiscoveryResult discovery,
        string applicationContext)
    {
        InitializeComponent();
        _paths = paths;
        _discovery = discovery;
        _applicationContext = applicationContext;
        PrinterSummaryText.Text = discovery.Printers.Count == 0
            ? "Detected printers: none"
            : "Detected printers: " + string.Join(", ", discovery.Printers.Select(printer => printer.DisplayName));
        Loaded += (_, _) => SummaryText.Focus();
    }

    private void CreateReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CreateReportButton.IsEnabled = false;
            CreateReportButton.Content = "Creating...";
            var request = new BugReportRequest(
                SummaryText.Text,
                StepsText.Text,
                ExpectedText.Text,
                ActualText.Text,
                IncludeLogsCheck.IsChecked == true,
                _applicationContext);
            ReportResult = new DiagnosticReportService(_paths).Create(request, _discovery);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            AppLog.WriteException("Bug report package creation failed.", ex);
            MessageBox.Show(
                this,
                ex.Message,
                "Report could not be created",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            CreateReportButton.Content = "Create Report";
            CreateReportButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
