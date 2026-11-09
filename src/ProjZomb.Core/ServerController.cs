using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ProjZomb.Core;

public sealed class ServerController : IDisposable
{
    private readonly TimeSpan consoleCheckTimeout;
    private CancellationTokenSource? consoleCheckCancellation;
    public ServerController(TimeSpan? consoleCheckTimeout = null)
    {
        this.consoleCheckTimeout = consoleCheckTimeout ?? TimeSpan.FromSeconds(30);
        if (this.consoleCheckTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(consoleCheckTimeout));
    }
    private Process? process;
    private bool stopping;
    private string? generatedLauncher;
    public bool Running { get { lock (outputLock) return process is { HasExited: false }; } }
    public const string ReadyState = "Server Start Complete";
    public const string VerifiedState = ReadyState + " — console verified";
    public const string UnconfirmedState = ReadyState + " — help check unconfirmed";
    public bool ConsoleVerified { get; private set; }
    public bool IsReady { get; private set; }
    public string State { get; private set; } = "Stopped";
    public event Action<string>? Output;
    public event Action<string>? StateChanged;
    public event Action<IReadOnlyList<string>>? PlayersChanged;
    private List<string>? collectingPlayers;
    private void SetState(string value)
    {
        IsReady = (value == ReadyState || value == VerifiedState || value == UnconfirmedState) && Running;
        if (State == value) return;
        State = value;
        StateChanged?.Invoke(value);
    }
    public void Start(ServerProfile profile)
    {
        lock (outputLock)
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
    }
    // Also used by the dependency-free integration harness with a fake server.
    public void StartProcess(ProcessStartInfo info)
    {
        lock (outputLock)
        {
            if (Running) throw new InvalidOperationException("A process is already running.");
            CancelConsoleCheck();
            ConsoleVerified = false;
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
                    CancelConsoleCheck(); ConsoleVerified = false;
                    CleanupLauncher(); collectingPlayers = null; PlayersChanged?.Invoke(Array.Empty<string>());
                    SetState(stopping ? "Stopped" : "Exited — check console");
                }
            };
            SetState("Starting");
            try { process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); }
            catch { SetState("Start failed"); throw; }
        }
    }
    private readonly object outputLock = new();
    private void Receive(Process source, string? line)
    {
        if (line is null) return;
        lock (outputLock)
        {
            if (!ReferenceEquals(source, process)) return;
            Output?.Invoke(line);
            if (!stopping && Running && !IsReady && ServerReadiness.IsStartupComplete(line))
            {
                SetState(ReadyState);
                consoleCheckCancellation = new CancellationTokenSource();
                try
                {
                    Output?.Invoke("[ProjZomb] Sending automatic startup console check: help");
                    Send("help");
                    _ = WatchConsoleCheckAsync(source, consoleCheckCancellation.Token);
                }
                catch (Exception e) when (e is IOException or InvalidOperationException)
                {
                    CancelConsoleCheck();
                    Output?.Invoke("[ProjZomb] Could not send the help check: " + e.Message);
                    SetState(UnconfirmedState);
                }
            }
            else if (!stopping && Running && IsReady && !ConsoleVerified && consoleCheckCancellation is not null && ServerReadiness.IsHelpResponse(line))
            {
                ConsoleVerified = true;
                CancelConsoleCheck();
                Output?.Invoke("[ProjZomb] Startup console check passed — help response received.");
                SetState(VerifiedState);
            }
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
    private async Task WatchConsoleCheckAsync(Process source, CancellationToken cancel)
    {
        try { await Task.Delay(consoleCheckTimeout, cancel); }
        catch (OperationCanceledException) { return; }
        lock (outputLock)
        {
            if (cancel.IsCancellationRequested || !ReferenceEquals(source, process) || stopping || !Running || ConsoleVerified) return;
            Output?.Invoke("[ProjZomb] The server reported startup complete, but a recognizable help response was not received. Inspect Console; the help check does not test external connectivity.");
            SetState(UnconfirmedState);
        }
    }
    private void CancelConsoleCheck()
    {
        consoleCheckCancellation?.Cancel();
        consoleCheckCancellation?.Dispose();
        consoleCheckCancellation = null;
    }
    private int expectedPlayers;
    public void Send(string command)
    {
        lock (outputLock)
        {
            if (!Running) throw new InvalidOperationException("Start this server in the manager first.");
            if (string.IsNullOrWhiteSpace(command) || command.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new InvalidOperationException("Enter one console command.");
            process!.StandardInput.WriteLine(command);
            process.StandardInput.Flush();
        }
    }
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        if (!Running) return;
        lock (outputLock)
        {
            stopping = true;
            CancelConsoleCheck(); ConsoleVerified = false;
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
    public void Dispose() { lock (outputLock) { CancelConsoleCheck(); if (!Running) CleanupLauncher(); process?.Dispose(); process = null; } }
}
