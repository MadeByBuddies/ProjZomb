using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ProjZomb.Core;

public static class SteamInstaller
{
    public static async Task RunAsync(ServerProfile profile, Action<string> log, CancellationToken cancel)
    {
        profile.Validate(false);
        if (!File.Exists(profile.SteamCmdPath) || !Path.GetFileName(profile.SteamCmdPath).Equals("steamcmd.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select a trusted steamcmd.exe obtained from Valve.");
        if (!Regex.IsMatch(profile.Branch, @"\A[A-Za-z0-9_-]*\z")) throw new InvalidOperationException("Invalid Steam branch name.");
        Directory.CreateDirectory(profile.InstallDirectory);
        var info = new ProcessStartInfo(profile.SteamCmdPath)
        {
            WorkingDirectory = Path.GetDirectoryName(profile.SteamCmdPath)!, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in new[] { "+force_install_dir", profile.InstallDirectory, "+login", "anonymous", "+app_update", "380870" }) info.ArgumentList.Add(arg);
        if (profile.Branch.Length > 0) { info.ArgumentList.Add("-beta"); info.ArgumentList.Add(profile.Branch); }
        info.ArgumentList.Add("validate"); info.ArgumentList.Add("+quit");
        using var process = new Process { StartInfo = info };
        bool installed = false;
        object gate = new();
        void Receive(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is not { } line) return;
            lock (gate)
            {
                if (line.Contains("Success! App '380870' fully installed", StringComparison.OrdinalIgnoreCase)) installed = true;
                log(line);
            }
        }
        process.OutputDataReceived += Receive; process.ErrorDataReceived += Receive;
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try { await process.WaitForExitAsync(cancel); process.WaitForExit(); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        if (process.ExitCode != 0 || !installed || !File.Exists(Path.Combine(profile.InstallDirectory, "StartServer64.bat")))
            throw new InvalidOperationException("SteamCMD did not confirm a complete installation. Review its console output.");
    }
}
