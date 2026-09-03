using System.IO;
using System.Reflection;
using System.Text;

namespace BambuFilamentImporter.Services;

public static class AppLog
{
    private const int RetentionDays = 14;
    private static readonly object Sync = new();
    private static bool _initialized;

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BambuFilamentImporter");

    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "Logs");
    public static string ReportDirectory { get; } = Path.Combine(DataDirectory, "Bug Reports");
    public static string CurrentLogPath => Path.Combine(LogDirectory, $"bfi-{DateTime.Now:yyyyMMdd}.log");

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            try
            {
                Directory.CreateDirectory(LogDirectory);
                PruneOldLogs();
            }
            catch
            {
                // Logging must never prevent BFI from starting.
            }
        }

        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
        Write($"BFI {version} session started on {Environment.OSVersion.VersionString}.");
    }

    public static void Write(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                var entry = new StringBuilder()
                    .Append('[')
                    .Append(DateTimeOffset.Now.ToString("O"))
                    .Append("] ")
                    .AppendLine(message.TrimEnd())
                    .ToString();
                File.AppendAllText(CurrentLogPath, entry);
            }
            catch
            {
                // The on-screen log remains available if persistent logging is blocked.
            }
        }
    }

    public static void WriteException(string context, Exception exception) =>
        Write($"{context}{Environment.NewLine}{exception}");

    private static void PruneOldLogs()
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        foreach (var path in Directory.EnumerateFiles(LogDirectory, "bfi-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // A locked or protected old log can be retried on the next launch.
            }
        }
    }
}
