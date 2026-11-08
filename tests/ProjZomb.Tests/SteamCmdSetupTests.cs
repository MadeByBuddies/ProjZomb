using System.IO.Compression;
using System.Net;
using ProjZomb.Core;

internal static class SteamCmdSetupTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        string home = Path.Combine(root, "SteamCMD setup tests"); Directory.CreateDirectory(home);
        string savedFolder = Path.Combine(home, "saved"), defaultFolder = Path.Combine(home, "default"), commonFolder = Path.Combine(home, "common");
        foreach (var folder in new[] { savedFolder, defaultFolder, commonFolder }) { Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "steamcmd.exe"), "existing"); }
        string saved = Path.Combine(savedFolder, "steamcmd.exe"), def = Path.Combine(defaultFolder, "steamcmd.exe");
        check(SteamCmdSetup.Detect(saved, defaultFolder, new[] { commonFolder }) == saved, "SteamCMD saved location takes priority");
        check(SteamCmdSetup.Detect(Path.Combine(home, "missing.exe"), defaultFolder) == def, "SteamCMD default location detected when saved path is missing");
        File.Delete(def);
        check(SteamCmdSetup.Detect(null, defaultFolder, new[] { commonFolder }) == Path.Combine(commonFolder, "steamcmd.exe"), "Common SteamCMD folder detected");
        check(SteamCmdSetup.Detect(null, defaultFolder) is null, "Missing SteamCMD exposes setup requirement");
        check(SteamCmdSetup.SelectFolder(savedFolder) == saved, "Selecting an existing SteamCMD folder resolves executable");
        try { SteamCmdSetup.SelectFolder(defaultFolder); throw new Exception("Expected missing executable error"); }
        catch (FileNotFoundException) { check(true, "Selecting a folder without SteamCMD is rejected"); }

        var handler = new ArchiveHandler(Archive("steamcmd.exe"));
        using var http = new HttpClient(handler);
        var platform = new FakePlatform();
        var service = new SteamCmdSetup(http, platform);
        var reports = new List<SteamCmdProgress>();
        var progress = new InlineProgress(reports.Add);
        string installed = await service.InstallAsync(defaultFolder, progress, _ => { }, CancellationToken.None);
        check(File.Exists(installed) && File.Exists(Path.Combine(defaultFolder, "steamclient.dll")), "SteamCMD download initialized and published to default folder");
        check(platform.Calls.SequenceEqual(new[] { "verify", "initialize", "verify" }), "SteamCMD signature checked before execution and after self-update");
        check(handler.Requests == 1 && handler.LastUri == SteamCmdSetup.OfficialDownloadUrl, "SteamCMD download uses only the official Valve URL");
        check(reports.Any(p => p.Percent == 0) && reports.Any(p => !p.Percent.HasValue) && reports[^1].Percent == 100, "SteamCMD download, initialization and completion report progress");
        await service.InstallAsync(defaultFolder, progress, _ => { }, CancellationToken.None);
        check(handler.Requests == 1, "Existing SteamCMD installation reused without overwrite or download");
        check(!Directory.EnumerateDirectories(home, ".steamcmd-install-*").Any(), "SteamCMD installation staging files cleaned");

        async Task Reject(byte[] archive, FakePlatform fake, string folder, string label, bool wrongOrigin = false)
        {
            using var client = new HttpClient(new ArchiveHandler(archive, wrongOrigin));
            var setup = new SteamCmdSetup(client, fake);
            bool rejected = false;
            try { await setup.InstallAsync(folder, progress, _ => { }, CancellationToken.None); }
            catch (Exception e) when (e is IOException or InvalidDataException) { rejected = true; }
            check(rejected && !File.Exists(Path.Combine(folder, "steamcmd.exe")), label);
            check(!Directory.EnumerateDirectories(home, ".steamcmd-install-*").Any(), label + " — staging cleaned");
        }
        var invalidPublisher = new FakePlatform { RejectSignature = true };
        await Reject(Archive("steamcmd.exe"), invalidPublisher, Path.Combine(home, "untrusted"), "Invalid SteamCMD signature blocks installation");
        check(!invalidPublisher.Calls.Contains("initialize"), "Unverified SteamCMD never executes");
        var traversalPlatform = new FakePlatform();
        await Reject(Archive("../escape.exe"), traversalPlatform, Path.Combine(home, "traversal"), "Unexpected/traversal archive contents rejected");
        check(traversalPlatform.Calls.Count == 0 && !File.Exists(Path.Combine(home, "escape.exe")), "Unsafe archive never reaches verification or execution");
        await Reject(Archive("steamcmd.exe"), new FakePlatform(), Path.Combine(home, "redirect"), "A download from a different origin is rejected", true);
        await Reject(new byte[] { 1, 2, 3 }, new FakePlatform(), Path.Combine(home, "corrupt"), "Corrupt SteamCMD archive rejected");
        var noComponents = new FakePlatform { WriteComponents = false };
        await Reject(Archive("steamcmd.exe"), noComponents, Path.Combine(home, "incomplete"), "Incomplete SteamCMD initialization is not published");

        string occupied = Path.Combine(home, "occupied"); Directory.CreateDirectory(occupied); File.WriteAllText(Path.Combine(occupied, "keep.txt"), "preserve");
        await Reject(Archive("steamcmd.exe"), new FakePlatform(), occupied, "Nonempty default folder is not overwritten");
        check(File.ReadAllText(Path.Combine(occupied, "keep.txt")) == "preserve", "Existing SteamCMD-folder data preserved");
        using var cancellation = new CancellationTokenSource();
        var cancelling = new InlineProgress(p => { if (p.Message.StartsWith("Verifying")) cancellation.Cancel(); });
        bool cancelled = false;
        try { await service.InstallAsync(Path.Combine(home, "cancelled"), cancelling, _ => { }, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && !Directory.Exists(Path.Combine(home, "cancelled")), "Cancelled SteamCMD installation never publishes partial files");
        check(!Directory.EnumerateDirectories(home, ".steamcmd-install-*").Any(), "Cancelled SteamCMD setup cleans staging files");
    }
    private static byte[] Archive(string entry)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        { using var writer = new StreamWriter(zip.CreateEntry(entry).Open()); writer.Write("fake signed executable"); }
        return buffer.ToArray();
    }
    private sealed class ArchiveHandler(byte[] archive, bool wrongOrigin = false) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests++; LastUri = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = wrongOrigin ? new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/download") : request,
                Content = new ByteArrayContent(archive)
            });
        }
    }
    private sealed class FakePlatform : ISteamCmdPlatform
    {
        public List<string> Calls { get; } = new();
        public bool RejectSignature { get; init; }
        public bool WriteComponents { get; init; } = true;
        public void VerifyPublisher(string executable)
        {
            Calls.Add("verify");
            if (RejectSignature) throw new InvalidDataException("Untrusted test publisher");
            if (File.ReadAllText(executable) != "fake signed executable") throw new InvalidDataException("Unexpected fixture");
        }
        public Task InitializeAsync(string executable, Action<string> log, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls.Add("initialize");
            if (WriteComponents) File.WriteAllText(Path.Combine(Path.GetDirectoryName(executable)!, "steamclient.dll"), "test component");
            return Task.CompletedTask;
        }
    }
    private sealed class InlineProgress(Action<SteamCmdProgress> report) : IProgress<SteamCmdProgress>
    { public void Report(SteamCmdProgress value) => report(value); }
}
