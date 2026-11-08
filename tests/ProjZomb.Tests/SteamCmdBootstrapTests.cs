using ProjZomb.Core;

internal static class SteamCmdBootstrapTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        int runs = 0, verified = 0;
        var messages = new List<string>();
        await SteamCmdBootstrap.RunAsync(_ => Task.FromResult(++runs == 1 ? 7 : 0), () => verified++, messages.Add, CancellationToken.None, TimeSpan.Zero);
        check(runs == 2 && verified == 2 && messages.Count == 1, "Bootstrap exit 7 retries and re-verifies the updated executable before succeeding");

        runs = verified = 0;
        await SteamCmdBootstrap.RunAsync(_ => Task.FromResult(++runs < 3 ? 7 : 0), () => verified++, _ => { }, CancellationToken.None, TimeSpan.Zero);
        check(runs == 3 && verified == 3, "Bootstrap allows two updater restarts followed by a clean exit");

        runs = 0;
        bool failed = false;
        try { await SteamCmdBootstrap.RunAsync(_ => { runs++; return Task.FromResult(7); }, () => { }, _ => { }, CancellationToken.None, TimeSpan.Zero); }
        catch (IOException e) { failed = e.Message.Contains("after 3 attempts"); }
        check(failed && runs == 3, "Persistent bootstrap exit 7 fails after a bounded retry count");

        runs = 0; failed = false;
        try { await SteamCmdBootstrap.RunAsync(_ => { runs++; return Task.FromResult(8); }, () => { }, _ => { }, CancellationToken.None, TimeSpan.Zero); }
        catch (IOException e) { failed = e.Message.Contains("code 8"); }
        check(failed && runs == 1, "Other bootstrap failures are not retried or marked successful");

        runs = verified = 0; failed = false;
        try
        {
            await SteamCmdBootstrap.RunAsync(_ => { runs++; return Task.FromResult(7); }, () =>
            {
                if (++verified == 2) throw new InvalidDataException("Updated executable has an invalid signature");
            }, _ => { }, CancellationToken.None, TimeSpan.Zero);
        }
        catch (InvalidDataException) { failed = true; }
        check(failed && runs == 1 && verified == 2, "A failed signature check prevents executing the updated bootstrap");

        runs = 0;
        using var cancel = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await SteamCmdBootstrap.RunAsync(_ => { runs++; return Task.FromResult(7); }, () => { }, _ => cancel.Cancel(), cancel.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && runs == 1, "Cancelling the retry delay prevents another bootstrap launch");

        runs = 0; verified = 0;
        await SteamCmdBootstrap.RunAsync(_ => { runs++; return Task.FromResult(0); }, () => verified++, _ => { }, CancellationToken.None, TimeSpan.Zero);
        check(runs == 1 && verified == 1, "A successful first bootstrap run is not repeated");
    }
}
