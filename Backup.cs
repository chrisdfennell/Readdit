using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Skein;

/// <summary>
/// One zip with everything you'd want on a new PC or after a reinstall: settings (folders, alerts,
/// filters, saved searches…), user notes, reading history and, optionally, the offline library.
/// Never the browser profile: that's Reddit's sign-in, and it stays on this PC.
/// </summary>
public static class Backup
{
    const string ManifestName = "skein-backup.json";
    const string PendingFolder = "restore-pending";

    /// <summary>Small files in the data folder worth keeping (the offline library is optional).</summary>
    static readonly string[] LocalFiles =
    [
        "user-notes.json", "alerts.json", "community-visits.json", "read.txt", "visits.json", "post-draft.json",
    ];

    public sealed record Manifest(int Version, DateTimeOffset Created, string AppVersion, bool IncludesLibrary);

    /// <summary>The offline library's size on disk, to show next to the option.</summary>
    public static long LibraryBytes(string localDir)
    {
        var dir = Path.Combine(localDir, "offline");
        return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
    }

    public static void Create(string zipPath, string roamingDir, string localDir, bool includeLibrary, string appVersion)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var manifest = new Manifest(1, DateTimeOffset.Now, appVersion, includeLibrary);
        using (var writer = new StreamWriter(zip.CreateEntry(ManifestName).Open()))
            writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        AddFile(zip, Path.Combine(roamingDir, "settings.json"), "roaming/settings.json");
        foreach (var name in LocalFiles) AddFile(zip, Path.Combine(localDir, name), "local/" + name);
        foreach (var file in Directory.Exists(localDir) ? Directory.GetFiles(localDir, "subscriptions*.json") : [])
            AddFile(zip, file, "local/" + Path.GetFileName(file));
        if (includeLibrary)
        {
            var library = Path.Combine(localDir, "offline");
            if (Directory.Exists(library))
                foreach (var file in Directory.EnumerateFiles(library, "*", SearchOption.AllDirectories))
                    AddFile(zip, file, "local/offline/" + Path.GetRelativePath(library, file).Replace('\\', '/'));
        }
    }

    static void AddFile(ZipArchive zip, string path, string entryName)
    {
        if (!File.Exists(path)) return;
        // Shared read: Skein may have the file open.
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
        source.CopyTo(target);
    }

    /// <summary>Reads a backup's manifest (null if it isn't a Skein backup).</summary>
    public static Manifest? ReadManifest(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry(ManifestName);
            if (entry == null) return null;
            using var reader = new StreamReader(entry.Open());
            return JsonSerializer.Deserialize<Manifest>(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Unpacks a backup into a waiting folder; <see cref="ApplyPending"/> puts it in place at the
    /// next start, before anything reads the files (so nothing in memory can overwrite them).
    /// </summary>
    public static void Stage(string zipPath, string localDir)
    {
        var pending = Path.Combine(localDir, PendingFolder);
        if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
        Directory.CreateDirectory(pending);
        var root = Path.GetFullPath(pending) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            // Only our two areas, and never outside the waiting folder (no "../" tricks).
            if (entry.FullName.EndsWith('/') ||
                !(entry.FullName.StartsWith("roaming/") || entry.FullName.StartsWith("local/")) ||
                entry.FullName.StartsWith("local/WebView2", StringComparison.OrdinalIgnoreCase))
                continue;
            var target = Path.GetFullPath(Path.Combine(pending, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>Puts a staged backup in place (called at startup, before settings load).</summary>
    public static bool ApplyPending(string roamingDir, string localDir)
    {
        var pending = Path.Combine(localDir, PendingFolder);
        if (!Directory.Exists(pending)) return false;
        try
        {
            Copy(Path.Combine(pending, "roaming"), roamingDir);
            var local = Path.Combine(pending, "local");
            // A backup with a library replaces the library (rather than mixing two).
            if (Directory.Exists(Path.Combine(local, "offline")))
            {
                var library = Path.Combine(localDir, "offline");
                if (Directory.Exists(library)) Directory.Delete(library, recursive: true);
            }
            Copy(local, localDir);
            return true;
        }
        finally
        {
            try
            {
                Directory.Delete(pending, recursive: true);
            }
            catch
            {
                // Tried again at the next start; harmless.
            }
        }

        static void Copy(string from, string to)
        {
            if (!Directory.Exists(from)) return;
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
    }
}
