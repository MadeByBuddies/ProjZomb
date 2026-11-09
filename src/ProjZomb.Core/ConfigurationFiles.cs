namespace ProjZomb.Core;

public static class ConfigurationFiles
{
    public static void SaveChecked(string path, string original, string content, string backupDirectory, Func<bool> serverRunning)
    {
        void RequireStopped()
        {
            if (serverRunning()) throw new InvalidOperationException("Stop the server before saving configuration. Your edits are still available in the editor.");
        }
        RequireStopped();
        if (!File.Exists(path) || File.ReadAllText(path) != original)
            throw new IOException("This file changed outside the editor. Reload before saving to avoid losing changes.");
        if (content.Contains('\0')) throw new InvalidDataException("Configuration contains a NUL character.");
        Directory.CreateDirectory(backupDirectory);
        var snapshot = Path.Combine(backupDirectory, $"config-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}-{Path.GetFileName(path)}");
        File.Copy(path, snapshot);
        RequireStopped();
        AtomicFile.Write(path, content);
    }
}
