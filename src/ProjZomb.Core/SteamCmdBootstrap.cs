namespace ProjZomb.Core;

public static class SteamCmdBootstrap
{
    // SteamCMD's first-run updater can exit with 7 before the updated client is usable.
    // Retry that result only; neither an exit of 7 nor downloaded files establish success.
    public static async Task RunAsync(Func<CancellationToken, Task<int>> runOnce, Action verifyPublisher,
        Action<string> log, CancellationToken cancel, TimeSpan? retryDelay = null)
    {
        const int maximumAttempts = 3;
        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            verifyPublisher(); // The preceding updater run may have replaced the executable.
            cancel.ThrowIfCancellationRequested();
            int code = await runOnce(cancel);
            cancel.ThrowIfCancellationRequested();
            if (code == 0) return;
            if (code != 7)
                throw new IOException($"SteamCMD initialization exited with code {code}. Review the console for the underlying error.");
            if (attempt == maximumAttempts)
                throw new IOException($"SteamCMD initialization still exited with code 7 after {maximumAttempts} attempts. The installation was not marked ready. Review the console for updater, network or dependency errors.");
            log($"ProjZomb: SteamCMD exited with code 7 during initialization; retrying the updater (attempt {attempt + 1}/{maximumAttempts})…");
            await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(1), cancel);
        }
    }
}
