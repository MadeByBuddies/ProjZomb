using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProjZomb.Core;

public sealed class ServerProfile
{
    public string InstallDirectory { get; set; } = "";
    public string DataDirectory { get; set; } = "";
    public string SteamCmdPath { get; set; } = "";
    public string ServerName { get; set; } = "servertest";
    public string BackupDirectory { get; set; } = "";
    // Empty means Steam's public branch. The user must confirm it is Build 42.
    public string Branch { get; set; } = "";
    public string ConfigDirectory => Path.Combine(DataDirectory, "Server");
    public string WorldDirectory => Path.Combine(DataDirectory, "Saves", "Multiplayer", ServerName);
    public string[] ConfigFileNames => new[] { ServerName + ".ini", ServerName + "_SandboxVars.lua", ServerName + "_spawnpoints.lua", ServerName + "_spawnregions.lua" };
    public string IniPath => Path.Combine(ConfigDirectory, ServerName + ".ini");
    public void Validate(bool requireInstall = true)
    {
        if (!Regex.IsMatch(ServerName, @"\A[A-Za-z0-9_-]{1,64}\z"))
            throw new InvalidOperationException("Server name must contain 1–64 letters, digits, underscores or hyphens.");
        foreach (var path in new[] { InstallDirectory, DataDirectory, BackupDirectory })
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new InvalidOperationException("Choose absolute installation, data and backup folders.");
        if (BackupService.IsInside(BackupDirectory, DataDirectory) || BackupService.IsInside(DataDirectory, BackupDirectory))
            throw new InvalidOperationException("Backup and data folders must be separate, without nesting.");
        if (requireInstall && !File.Exists(Path.Combine(InstallDirectory, "StartServer64.bat")))
            throw new FileNotFoundException("Select a dedicated-server installation containing StartServer64.bat.");
    }
    public static ServerProfile Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<ServerProfile>(File.ReadAllText(path)) ?? new() : new();
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Write(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public static class AtomicFile
{
    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content, new System.Text.UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
