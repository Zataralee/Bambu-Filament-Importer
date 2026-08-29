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
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == currentId)
                    {
                        continue;
                    }

                    var name = process.ProcessName;
                    if (name.Equals("BambuStudio", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Bambu Studio", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("bambu-studio", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited between enumeration and inspection.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Windows denied inspection of an unrelated protected process.
                }
            }
        }

        return false;
    }
}
