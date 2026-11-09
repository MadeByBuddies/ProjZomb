using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProjZomb.Core;

public sealed record BackupManifest(int Format, string ServerName, DateTime CreatedUtc, Dictionary<string, string> Files);
public static class BackupService
{
    public static bool IsInside(string candidate, string root)
    {
        var c = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return c.Equals(r, StringComparison.OrdinalIgnoreCase) || c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static string[] Targets(ServerProfile p)
    {
        var paths = new List<string>();
        if (Directory.Exists(p.ConfigDirectory)) paths.AddRange(p.ConfigFileNames.Select(f => Path.Combine(p.ConfigDirectory, f)).Where(File.Exists));
        var db = Path.Combine(p.DataDirectory, "db", p.ServerName + ".db");
        if (File.Exists(db)) paths.Add(db);
        if (Directory.Exists(p.WorldDirectory)) paths.Add(p.WorldDirectory);
        return paths.ToArray();
    }
    private static void RejectLinks(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Links/reparse points are not allowed in backup data: " + path);
    }
    private static IEnumerable<string> Walk(string path)
    {
        RejectLinks(path);
        if (File.Exists(path)) { yield return path; yield break; }
        foreach (var file in Directory.EnumerateFiles(path)) { RejectLinks(file); yield return file; }
        foreach (var dir in Directory.EnumerateDirectories(path)) foreach (var file in Walk(dir)) yield return file;
    }
    public static string Create(ServerProfile p)
    {
        p.Validate(false);
        Directory.CreateDirectory(p.BackupDirectory);
        if (Directory.Exists(p.DataDirectory)) RejectLinks(p.DataDirectory);
        foreach (var parent in new[] { p.ConfigDirectory, Path.Combine(p.DataDirectory, "Saves"), Path.Combine(p.DataDirectory, "Saves", "Multiplayer"), Path.Combine(p.DataDirectory, "db") })
            if (Directory.Exists(parent)) RejectLinks(parent);
        var targets = Targets(p);
        if (targets.Length == 0) throw new InvalidOperationException("No configuration, world or database exists for this server name.");
        var output = Path.Combine(p.BackupDirectory, $"{p.ServerName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.zip");
        var temporary = output + ".partial";
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var f in targets.SelectMany(Walk))
                {
                    var relative = Path.GetRelativePath(p.DataDirectory, f).Replace('\\', '/');
                    var entry = zip.CreateEntry(relative, CompressionLevel.Fastest);
                    using (var input = File.OpenRead(f)) using (var dest = entry.Open()) input.CopyTo(dest);
                }
            }
        }
        catch { if (File.Exists(temporary)) File.Delete(temporary); throw; }
        // Read back the completed archive and hash the actual stored bytes.
        using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Update))
        {
            foreach (var e in zip.Entries) { using var s = e.Open(); hashes.Add(e.FullName, Convert.ToHexString(SHA256.HashData(s))); }
            var manifest = zip.CreateEntry("manifest.json");
            using var writer = new StreamWriter(manifest.Open());
            writer.Write(JsonSerializer.Serialize(new BackupManifest(1, p.ServerName, DateTime.UtcNow, hashes)));
        }
        File.Move(temporary, output);
        return output;
    }
    private static bool Allowed(string path, string server)
    {
        if (path.Contains('\\') || path.StartsWith('/') || path.Split('/').Any(x => x is ".." or "." or "" || x.Contains(':'))) return false;
        return path.StartsWith($"Saves/Multiplayer/{server}/", StringComparison.OrdinalIgnoreCase)
            || path.Equals($"db/{server}.db", StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith("Server/", StringComparison.OrdinalIgnoreCase) && path.Count(c => c == '/') == 1 &&
                new[] { server + ".ini", server + "_SandboxVars.lua", server + "_spawnpoints.lua", server + "_spawnregions.lua" }.Contains(path[7..], StringComparer.OrdinalIgnoreCase));
    }
    public static string Restore(ServerProfile p, string archive)
    {
        p.Validate(false);
        // A safety backup is mandatory and stays outside the restore transaction.
        var safety = Targets(p).Length > 0 ? Create(p) : "No previous profile data";
        var stage = Path.Combine(Path.GetTempPath(), "ProjZomb-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var rollback = Path.Combine(p.DataDirectory, ".restore-" + Guid.NewGuid().ToString("N"));
        var moved = new List<(string Original, string Saved)>();
        var applied = new List<string>();
        try
        {
            using (var zip = ZipFile.OpenRead(archive))
            {
                var me = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Not a ProjZomb backup.");
                if (me.Length > 16 * 1024 * 1024) throw new InvalidDataException("Manifest is too large.");
                using var ms = me.Open();
                var manifest = JsonSerializer.Deserialize<BackupManifest>(ms) ?? throw new InvalidDataException("Missing manifest.");
                if (manifest.Format != 1 || manifest.ServerName != p.ServerName) throw new InvalidDataException("Backup format or server name does not match.");
                if (zip.Entries.Count != manifest.Files.Count + 1 || manifest.Files.Count == 0) throw new InvalidDataException("Archive entry count does not match manifest.");
                long total = 0;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in zip.Entries.Where(x => x.FullName != "manifest.json"))
                {
                    if (!Allowed(e.FullName, p.ServerName) || !seen.Add(e.FullName) || !manifest.Files.TryGetValue(e.FullName, out var hash)) throw new InvalidDataException("Invalid or duplicate backup path.");
                    total = checked(total + e.Length);
                    if (total > 100L * 1024 * 1024 * 1024) throw new InvalidDataException("Backup exceeds the 100 GiB restore limit.");
                    var dest = Path.Combine(stage, e.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest);
                    using var stream = File.OpenRead(dest);
                    if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Backup integrity check failed: " + e.FullName);
                }
            }
            Directory.CreateDirectory(p.DataDirectory);
            RejectLinks(p.DataDirectory);
            foreach (var parent in new[] { p.ConfigDirectory, Path.Combine(p.DataDirectory, "db"), Path.Combine(p.DataDirectory, "Saves"), Path.Combine(p.DataDirectory, "Saves", "Multiplayer") })
                if (Directory.Exists(parent)) RejectLinks(parent);
            Directory.CreateDirectory(rollback);
            foreach (var path in Targets(p))
            {
                RejectLinks(path);
                var saved = Path.Combine(rollback, moved.Count.ToString());
                if (Directory.Exists(path)) Directory.Move(path, saved); else File.Move(path, saved);
                moved.Add((path, saved));
            }
            foreach (var file in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(p.DataDirectory, Path.GetRelativePath(stage, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, false); applied.Add(dest);
            }
        }
        catch
        {
            foreach (var f in applied) File.Delete(f);
            foreach (var (original, saved) in moved.AsEnumerable().Reverse())
            {
                if (Directory.Exists(saved)) { if (Directory.Exists(original)) Directory.Delete(original, true); Directory.Move(saved, original); }
                else File.Move(saved, original, true);
            }
            throw;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        if (Directory.Exists(rollback)) Directory.Delete(rollback, true);
        return safety;
    }
}
