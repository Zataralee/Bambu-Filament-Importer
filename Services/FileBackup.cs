using System.IO;

namespace BambuFilamentImporter.Services;

internal static class FileBackup
{
    public static string? Create(string path, string label)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(path)!;
        var fileName = Path.GetFileName(path);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var backupPath = Path.Combine(directory, $"{fileName}.{label}-{stamp}");
        var suffix = 1;
        while (File.Exists(backupPath))
        {
            backupPath = Path.Combine(directory, $"{fileName}.{label}-{stamp}-{suffix++}");
        }

        File.Copy(path, backupPath, overwrite: false);
        return backupPath;
    }
}
