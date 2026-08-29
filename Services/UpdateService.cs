using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace BambuFilamentImporter.Services;

public sealed record UpdateRelease(
    Version Version,
    string TagName,
    string AssetName,
    string DownloadUrl,
    string ReleaseUrl);

public static class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/Zataralee/Bambu-Filament-Importer/releases/latest";
    private const long MaximumDownloadBytes = 250L * 1024 * 1024;

    public static bool IsRestartAfterUpdate { get; set; }
    public static string? PendingCleanupDirectory { get; set; }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static async Task<UpdateRelease> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(LatestReleaseUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tagName = root.GetProperty("tag_name").GetString()
            ?? throw new InvalidDataException("The latest GitHub release has no version tag.");
        if (!Version.TryParse(tagName.Trim().TrimStart('v', 'V'), out var version))
        {
            throw new InvalidDataException($"The GitHub release tag '{tagName}' is not a supported version number.");
        }

        var asset = root.GetProperty("assets")
            .EnumerateArray()
            .Select(item => new
            {
                Name = item.GetProperty("name").GetString() ?? "",
                Url = item.GetProperty("browser_download_url").GetString() ?? ""
            })
            .FirstOrDefault(item => item.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase));
        if (asset is null || string.IsNullOrWhiteSpace(asset.Url))
        {
            throw new InvalidDataException("The latest release does not include the Windows update package.");
        }

        return new UpdateRelease(
            version,
            tagName,
            asset.Name,
            asset.Url,
            root.GetProperty("html_url").GetString() ?? "https://github.com/Zataralee/Bambu-Filament-Importer/releases/latest");
    }

    public static bool IsNewer(UpdateRelease release) => release.Version > CurrentVersion;

    public static async Task<string> DownloadAndExtractAsync(
        UpdateRelease release,
        CancellationToken cancellationToken = default)
    {
        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BambuFilamentImporter",
            "Updates",
            release.TagName.Trim().Replace('/', '-').Replace('\\', '-') + "-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(updateRoot, release.AssetName);
        var extractPath = Path.Combine(updateRoot, "package");
        Directory.CreateDirectory(updateRoot);

        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
            {
                throw new InvalidDataException("The update package is unexpectedly large and was not downloaded.");
            }

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = File.Create(archivePath))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            Directory.CreateDirectory(extractPath);
            ZipFile.ExtractToDirectory(archivePath, extractPath, overwriteFiles: false);
            var stagedExecutable = Directory.EnumerateFiles(
                    extractPath,
                    "BambuFilamentImporter.exe",
                    SearchOption.AllDirectories)
                .SingleOrDefault()
                ?? throw new InvalidDataException("The downloaded update does not contain BambuFilamentImporter.exe.");
            return stagedExecutable;
        }
        catch
        {
            TryDeleteDirectory(updateRoot);
            throw;
        }
    }

    public static void LaunchUpdater(string stagedExecutable)
    {
        var currentExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current application path could not be determined.");
        var startInfo = new ProcessStartInfo(stagedExecutable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(stagedExecutable)!
        };
        startInfo.ArgumentList.Add("--apply-update");
        startInfo.ArgumentList.Add(Path.GetDirectoryName(stagedExecutable)!);
        startInfo.ArgumentList.Add(currentExecutable);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        if (!CanWriteToDirectory(Path.GetDirectoryName(currentExecutable)!))
        {
            startInfo.Verb = "runas";
        }

        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows could not start the update installer.");
    }

    public static int ApplyUpdate(string sourceDirectory, string targetExecutable, int processId)
    {
        WaitForProcessExit(processId);
        var sourceRoot = Path.GetFullPath(sourceDirectory);
        var targetPath = Path.GetFullPath(targetExecutable);
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("The application folder could not be determined.");
        var stagedExecutable = Path.Combine(sourceRoot, "BambuFilamentImporter.exe");
        if (!File.Exists(stagedExecutable))
        {
            throw new FileNotFoundException("The staged update executable is missing.", stagedExecutable);
        }

        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            var destinationPath = relativePath.Equals("BambuFilamentImporter.exe", StringComparison.OrdinalIgnoreCase)
                ? targetPath
                : Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }

        var restart = new ProcessStartInfo(targetPath)
        {
            UseShellExecute = true,
            WorkingDirectory = targetDirectory
        };
        restart.ArgumentList.Add("--update-complete");
        restart.ArgumentList.Add(Path.GetDirectoryName(sourceRoot)!);
        Process.Start(restart);
        return 0;
    }

    public static void ScheduleCleanup(string directory)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            TryDeleteDirectory(directory);
        });
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BambuFilamentImporter", CurrentVersion.ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static void WaitForProcessExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
            {
                throw new TimeoutException("The running importer did not close in time. The update was not installed.");
            }
        }
        catch (ArgumentException)
        {
            // The original process has already exited.
        }
    }

    private static bool CanWriteToDirectory(string directory)
    {
        try
        {
            var testPath = Path.Combine(directory, $".bfi-update-test-{Guid.NewGuid():N}");
            File.WriteAllText(testPath, "test");
            File.Delete(testPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // A future update can reuse the folder if Windows still has a file open.
        }
    }
}
