using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using BambuFilamentImporter.Models;

namespace BambuFilamentImporter.Services;

/// <summary>A headless helper for the explicit Program Files import action.</summary>
public static class PackageElevationService
{
    public static void Install(LoadedFilamentPackage package, ImportDestination destination)
    {
        if (destination == ImportDestination.ProjectLibrary) throw new InvalidOperationException("Custom presets cannot be mirrored into Program Files.");
        var folder = Path.Combine(Path.GetTempPath(), "BambuFilamentImporter");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "mirror-" + Guid.NewGuid().ToString("N") + ".json");
        var result = path + ".result";
        File.WriteAllText(path, JsonSerializer.Serialize(new MirrorRequest(package, destination)));
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--elevated-mirror-package"); start.ArgumentList.Add(path);
            using var process = Process.Start(start) ?? throw new IOException("Could not start the administrator helper.");
            process.WaitForExit();
            if (!File.Exists(result)) throw new IOException("The administrator helper did not return a result.");
            var error = File.ReadAllText(result);
            if (process.ExitCode != 0 || error.Length != 0) throw new IOException(error.Length == 0 ? "Administrator import failed." : error);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new InvalidOperationException("Administrator approval was canceled. Nothing was imported."); }
        finally { File.Delete(path); File.Delete(result); }
    }
    public static int Execute(string requestPath)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BambuFilamentImporter")) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(requestPath);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("mirror-")) return 2;
        try
        {
            var request = JsonSerializer.Deserialize<MirrorRequest>(File.ReadAllText(full)) ?? throw new InvalidDataException("Import plan is invalid.");
            if (request.Destination is not (ImportDestination.DeviceAms or ImportDestination.Both)) throw new InvalidDataException("Invalid mirror destination.");
            var paths = new BambuPaths();
            InstalledCatalogTransaction.Run(paths, () => new BambuInstaller(paths).Install(request.Package, request.Destination, true));
            File.WriteAllText(full + ".result", ""); return 0;
        }
        catch (Exception ex) { File.WriteAllText(full + ".result", ex.Message); return 2; }
    }
    public sealed record MirrorRequest(LoadedFilamentPackage Package, ImportDestination Destination);
}

/// <summary>Rollback includes Program Files when running inside an approved elevated helper.</summary>
public static class InstalledCatalogTransaction
{
    public static T Run<T>(BambuPaths paths, Func<T> action, Func<bool>? studioRunning = null)
    {
        studioRunning ??= BambuProcess.IsStudioRunning;
        if (studioRunning()) throw new InvalidOperationException("Close Bambu Studio before changing its installed catalog.");
        IEnumerable<string> Files() => new[] { paths.ConfigPath, Path.Combine(paths.ProgramProfileRoot, "BBL.json"), Path.Combine(paths.RoamingProfileRoot, "BBL.json") }.Where(File.Exists)
            .Concat(new[] { paths.UserRoot, Path.Combine(paths.ProgramProfileRoot, "BBL", "filament"), Path.Combine(paths.RoamingProfileRoot, "BBL", "filament") }
                .Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)).Where(p => Path.GetExtension(p) is ".json" or ".info"));
        var originals = Files().ToDictionary(p => p, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        try
        {
            var result = action();
            BambuCatalogIntegrity.ValidateProfileRoot(paths.RoamingProfileRoot);
            BambuCatalogIntegrity.ValidateProfileRoot(paths.ProgramProfileRoot);
            return result;
        }
        catch
        {
            foreach (var added in Files().Where(p => !originals.ContainsKey(p))) File.Delete(added);
            foreach (var original in originals) { Directory.CreateDirectory(Path.GetDirectoryName(original.Key)!); File.WriteAllBytes(original.Key, original.Value); }
            throw;
        }
    }
}
