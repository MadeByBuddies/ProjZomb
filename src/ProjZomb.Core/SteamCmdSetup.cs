using System.IO.Compression;

namespace ProjZomb.Core;

public sealed record SteamCmdProgress(string Message, double? Percent = null);

public interface ISteamCmdPlatform
{
    void VerifyPublisher(string executable);
    Task InitializeAsync(string executable, Action<string> log, CancellationToken cancellationToken);
}

public sealed class SteamCmdSetup(HttpClient http, ISteamCmdPlatform platform)
{
    public const string OfficialDownloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    private const long MaximumArchiveBytes = 32 * 1024 * 1024;
    private const long MaximumExecutableBytes = 64 * 1024 * 1024;

    public static string? Detect(string? savedExecutable, string defaultDirectory, IEnumerable<string>? otherDirectories = null)
    {
        var candidates = new[] { savedExecutable, Path.Combine(defaultDirectory, "steamcmd.exe") }
            .Concat((otherDirectories ?? Array.Empty<string>()).Select(p => Path.Combine(p, "steamcmd.exe")));
        return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p)
            && Path.GetFileName(p).Equals("steamcmd.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p));
    }

    public static string SelectFolder(string folder)
    {
        string executable = Path.Combine(Path.GetFullPath(folder), "steamcmd.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("That folder does not contain steamcmd.exe. Select its containing folder, or choose Install SteamCMD.");
        return executable;
    }

    public async Task<string> InstallAsync(string directory, IProgress<SteamCmdProgress> progress, Action<string> log, CancellationToken cancel)
    {
        directory = Path.GetFullPath(directory);
        string executable = Path.Combine(directory, "steamcmd.exe");
        if (File.Exists(executable)) return executable;
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("The default SteamCMD folder contains files but no steamcmd.exe. Select an existing SteamCMD folder or move those files before retrying. Nothing was overwritten.");
        var parent = Path.GetDirectoryName(directory) ?? throw new IOException("Choose a SteamCMD subfolder, not a drive root.");
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".steamcmd-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            progress.Report(new("Downloading SteamCMD from Valve…", 0));
            using (var response = await http.GetAsync(OfficialDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                // Do not follow a response to another origin, even with an injected HttpClient.
                if (response.RequestMessage?.RequestUri is { } uri && uri.AbsoluteUri != OfficialDownloadUrl)
                    throw new InvalidDataException("SteamCMD download left the official Valve URL.");
                var length = response.Content.Headers.ContentLength;
                if (length > MaximumArchiveBytes) throw new InvalidDataException("SteamCMD archive is unexpectedly large.");
                await using var source = await response.Content.ReadAsStreamAsync(cancel);
                await using var destination = new FileStream(Path.Combine(stage, "download.zip"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920];
                long received = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancel)) != 0)
                {
                    received += count;
                    if (received > MaximumArchiveBytes) throw new InvalidDataException("SteamCMD archive exceeds the download limit.");
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancel);
                    progress.Report(new("Downloading SteamCMD from Valve…", length is > 0 ? Math.Min(100, received * 100.0 / length.Value) : null));
                }
                if (length.HasValue && received != length.Value) throw new IOException("SteamCMD download was incomplete. Retry the installation.");
            }
            cancel.ThrowIfCancellationRequested();
            progress.Report(new("Checking the archive…"));
            var stagedExe = Path.Combine(stage, "steamcmd.exe");
            using (var archive = ZipFile.OpenRead(Path.Combine(stage, "download.zip")))
            {
                if (archive.Entries.Count != 1 || archive.Entries[0].FullName != "steamcmd.exe" || archive.Entries[0].Length is <= 0 or > MaximumExecutableBytes)
                    throw new InvalidDataException("Unexpected SteamCMD archive contents. No files were installed.");
                await using var input = archive.Entries[0].Open();
                await using var output = new FileStream(stagedExe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var extractionBuffer = new byte[81920];
                long extracted = 0;
                int count;
                while ((count = await input.ReadAsync(extractionBuffer, cancel)) != 0)
                {
                    extracted += count;
                    if (extracted > MaximumExecutableBytes) throw new InvalidDataException("SteamCMD executable exceeds the extraction limit.");
                    await output.WriteAsync(extractionBuffer.AsMemory(0, count), cancel);
                }
                if (extracted != archive.Entries[0].Length) throw new InvalidDataException("SteamCMD executable length does not match the archive.");
            }
            File.Delete(Path.Combine(stage, "download.zip"));
            progress.Report(new("Verifying Valve's digital signature…"));
            cancel.ThrowIfCancellationRequested();
            platform.VerifyPublisher(stagedExe);
            cancel.ThrowIfCancellationRequested();
            progress.Report(new("SteamCMD is downloading its components and initializing. See Console for details…"));
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(15));
                try { await platform.InitializeAsync(stagedExe, log, timeout.Token); }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                { throw new TimeoutException("SteamCMD initialization timed out. Check your connection and retry."); }
            }
            cancel.ThrowIfCancellationRequested();
            platform.VerifyPublisher(stagedExe);
            if (!File.Exists(Path.Combine(stage, "steamclient.dll"))) throw new IOException("SteamCMD did not finish installing its Windows components. Review the console and retry.");
            // Publish the complete installation only after verification and initialization.
            // A concurrent or pre-existing installation is never merged or overwritten.
            if (Directory.Exists(directory))
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("The destination changed during installation. Nothing was overwritten.");
                Directory.Delete(directory);
            }
            Directory.Move(stage, directory);
            progress.Report(new("SteamCMD is ready.", 100));
            return executable;
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { log("SteamCMD temporary setup folder could not be removed: " + stage + ". Close any process using it, then remove it manually."); }
        }
    }
}
