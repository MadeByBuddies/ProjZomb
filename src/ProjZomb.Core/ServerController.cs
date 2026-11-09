using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ProjZomb.Core;

public sealed class ServerController : IDisposable
{
    private Process? process;
    private bool stopping;
    private string? generatedLauncher;
    public bool Running => process is { HasExited: false };
    public const string ReadyState = "Server Start Complete";
    public bool IsReady { get; private set; }
    public string State { get; private set; } = "Stopped";
    public event Action<string>? Output;
    public event Action<string>? StateChanged;
    public event Action<IReadOnlyList<string>>? PlayersChanged;
    private List<string>? collectingPlayers;
    private void SetState(string value)
    {
        IsReady = value == ReadyState && Running;
        if (State == value) return;
        State = value;
        StateChanged?.Invoke(value);
    }
    public void Start(ServerProfile profile)
    {
        if (Running) throw new InvalidOperationException("Server is already running.");
        profile.Validate();
        var launcher = Path.Combine(profile.InstallDirectory, "StartServer64.bat");
        // cmd.exe is only used for the game's own batch launcher. Reject shell metacharacters,
        // including percent expansion, before composing its command string.
        if (new[] { launcher, profile.DataDirectory }.Any(x => x.IndexOfAny("\"%&|<>^!\r\n".ToCharArray()) >= 0))
            throw new InvalidOperationException("Installation/data paths cannot contain cmd shell characters (quotes, %, &, !, |, <, >, ^).");
        var prepared = WindowsLauncher.Prepare(File.ReadAllText(launcher));
        generatedLauncher = Path.Combine(profile.InstallDirectory, ".projzomb-launch-" + Guid.NewGuid().ToString("N") + ".cmd");
        // Preserve the original launcher's encoding (normally Windows ANSI/ASCII).
        // UTF-8 is supported by modern Windows, while stock launcher text is ASCII.
        AtomicFile.Write(generatedLauncher, prepared);
        launcher = generatedLauncher;
        var info = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = profile.InstallDirectory, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true,
            Arguments = $"/d /s /c \"\"{launcher}\" -servername {profile.ServerName} -cachedir=\"{profile.DataDirectory}\"\""
        };
        try { StartProcess(info); }
        catch { CleanupLauncher(); throw; }
    }
    // Also used by the dependency-free integration harness with a fake server.
    public void StartProcess(ProcessStartInfo info)
    {
        if (Running) throw new InvalidOperationException("A process is already running.");
        process?.Dispose();
        stopping = false;
        collectingPlayers = null;
        var current = new Process { StartInfo = info, EnableRaisingEvents = true };
        process = current;
        current.OutputDataReceived += (_, e) => Receive(current, e.Data);
        current.ErrorDataReceived += (_, e) => Receive(current, e.Data);
        current.Exited += (_, _) =>
        {
            lock (outputLock)
            {
                if (!ReferenceEquals(current, process)) return;
                CleanupLauncher(); collectingPlayers = null; PlayersChanged?.Invoke(Array.Empty<string>());
                SetState(stopping ? "Stopped" : "Exited — check console");
            }
        };
        SetState("Starting");
        try { process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); }
        catch { SetState("Start failed"); throw; }
    }
    private readonly object outputLock = new();
    private void Receive(Process source, string? line)
    {
        if (line is null) return;
        lock (outputLock)
        {
            if (!ReferenceEquals(source, process)) return;
            Output?.Invoke(line);
            if (!stopping && Running && !IsReady && ServerReadiness.IsStartupComplete(line)) SetState(ReadyState);
            // PZ's console `players` response: Players connected (N): followed by -username.
            var header = Regex.Match(line, @"Players connected \((\d+)\):", RegexOptions.IgnoreCase);
            if (header.Success)
            {
                collectingPlayers = new();
                expectedPlayers = int.Parse(header.Groups[1].Value);
                if (expectedPlayers == 0) { PlayersChanged?.Invoke(collectingPlayers); collectingPlayers = null; }
            }
            else if (collectingPlayers is not null)
            {
                var player = Regex.Match(line, @"^\s*-([^\r\n]+)$");
                if (player.Success)
                {
                    collectingPlayers.Add(player.Groups[1].Value.Trim());
                    if (collectingPlayers.Count >= expectedPlayers) { PlayersChanged?.Invoke(collectingPlayers.ToArray()); collectingPlayers = null; }
                }
            }
        }
    }
    private int expectedPlayers;
    public void Send(string command)
    {
        if (!Running) throw new InvalidOperationException("Start this server in the manager first.");
        if (string.IsNullOrWhiteSpace(command) || command.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            throw new InvalidOperationException("Enter one console command.");
        process!.StandardInput.WriteLine(command);
        process.StandardInput.Flush();
    }
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        if (!Running) return;
        lock (outputLock)
        {
            stopping = true;
            SetState("Stopping — saving world");
        }
        Send("save");
        Send("quit"); // The game performs its own final save as part of quit.
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(90));
        try { await process!.WaitForExitAsync(cancellation.Token); SetState("Stopped"); }
        catch (OperationCanceledException) { SetState("Shutdown timed out — server still running"); throw new TimeoutException("Graceful shutdown timed out. Check the console; the server was not force-killed."); }
    }
    public static string QuotePlayer(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => char.IsControl(c) || c is '"' or '\\'))
            throw new InvalidOperationException("Player name cannot contain quotes, backslashes or control characters.");
        return "\"" + name + "\"";
    }
    private void CleanupLauncher()
    {
        var path = Interlocked.Exchange(ref generatedLauncher, null);
        try { if (path is not null) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public void Dispose() { if (!Running) CleanupLauncher(); process?.Dispose(); }
}
