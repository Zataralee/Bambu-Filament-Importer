using System.Diagnostics;

namespace BambuFilamentImporter.Services;

public static class BambuProcess
{
    public static bool IsAnotherImporterRunning()
    {
        var currentId = Environment.ProcessId;
        return Process.GetProcessesByName("BambuFilamentImporter")
            .Any(process => process.Id != currentId);
    }

    public static bool IsStudioRunning()
    {
        var currentId = Environment.ProcessId;
        return Process.GetProcesses().Any(process =>
        {
            if (process.Id == currentId)
            {
                return false;
            }

            var name = process.ProcessName;
            return name.Equals("BambuStudio", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Bambu Studio", StringComparison.OrdinalIgnoreCase)
                || name.Equals("bambu-studio", StringComparison.OrdinalIgnoreCase);
        });
    }
}
