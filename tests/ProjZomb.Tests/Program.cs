using System.Diagnostics;
using System.IO.Compression;
using ProjZomb.Core;

if (args.Contains("+app_update"))
{
    int i = Array.IndexOf(args, "+force_install_dir");
    if (i < 0 || !args.Contains("380870") || !args.Contains("anonymous") || !args.Contains("validate") || args.Contains("bad"))
    { Console.WriteLine("ERROR: fake SteamCMD rejected arguments"); Environment.ExitCode = 1; return; }
    Directory.CreateDirectory(args[i + 1]);
    File.WriteAllText(Path.Combine(args[i + 1], "StartServer64.bat"), "java.exe zombie.network.GameServer %1 %2\nPAUSE\n");
    Console.WriteLine("Success! App '380870' fully installed.");
    return;
}
if (args.Contains("--fake-server"))
{
    Console.WriteLine("*** SERVER STARTED ***");
    while (Console.ReadLine() is { } line)
    {
        if (line == "players") { Console.WriteLine("Players connected (2):"); Console.WriteLine("-Alice"); Console.WriteLine("-Bob"); }
        if (line == "quit") break;
    }
    return;
}
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
void Throws(Action action, string name) { try { action(); } catch { Check(true, name); return; } throw new Exception("FAIL: " + name); }
string root = Path.Combine(Path.GetTempPath(), "ProjZomb-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await SteamCmdSetupTests.RunAsync(root, Check);
    await SteamCmdBootstrapTests.RunAsync(Check);
    var launcher = "@cd /d \"%~dp0\"\r\njava.exe -Xmx4g zombie.network.GameServer %1 %2\r\nPAUSE\r\n";
    var launch = WindowsLauncher.Prepare(launcher);
    Check(launch.Contains("%~dp0") && launch.Contains("%*") && !launch.Contains("%1") && !launch.Contains("\r\nPAUSE"), "Launcher forwards all arguments and removes pause without changing stock file");
    Throws(() => WindowsLauncher.Prepare("java zombie.network.GameServer -servername other"), "Conflicting custom launcher rejected");
    Throws(() => WindowsLauncher.Prepare("echo no java launcher"), "Unknown launcher rejected");
    var longLua = new ConfigDocument("SandboxVars = {\n Notes = [=[\nFakeSetting = true,\n]=],\n Real = false,\n}\n", true);
    Check(longLua.Entries.All(x => x.Key != "FakeSetting") && longLua.Entries.Any(x => x.Key == "Real"), "Lua long strings are not parsed as settings");
    const string ini = "# Keep comment\r\nPublicName=My Server\r\nMods=A;B\r\nWorkshopItems=123;456\r\nUnknownModKey=x=y\r\nDefaultPort=16261\r\nPassword=secret\r\n";
    var doc = new ConfigDocument(ini, false);
    Check(doc.Render() == ini, "INI exact round trip including CRLF and unknown values");
    doc.Entries.Single(x => x.Key == "Mods").Value = "B;A";
    Check(doc.Render() == ini.Replace("Mods=A;B", "Mods=B;A"), "INI targeted edit preserves other bytes");
    Check(doc.Entries.Single(x => x.Key == "Password").Sensitive, "Sensitive settings flagged");
    doc.Entries.Single(x => x.Key == "DefaultPort").Value = "70000";
    Throws(() => doc.Render(), "Invalid port rejected");
    doc.Entries.Single(x => x.Key == "DefaultPort").Value = "16261\nPassword=oops";
    Throws(() => doc.Render(), "INI newline injection rejected");
    const string lua = "SandboxVars = {\n    VERSION = 5,\n    -- Zombie choice\n    Zombies = 3,\n    Nested = {\n        Enabled = true, -- preserve\n    },\n    Complex = function() return 1 end,\n}\n";
    var ld = new ConfigDocument(lua, true);
    Check(ld.Render() == lua, "Lua exact round trip with complex expressions");
    ld.Entries.Single(x => x.Key == "Nested.Enabled").Value = "false";
    Check(ld.Render() == lua.Replace("true, -- preserve", "false, -- preserve"), "Nested Lua scalar edit preserves comments");
    ld.Entries.Single(x => x.Key == "Nested.Enabled").Value = "os.execute('bad')";
    Throws(() => ld.Render(), "Lua expression injection blocked in structured editor");
    Throws(() => ServerController.QuotePlayer("bob\"\nquit"), "Player command injection rejected");
    Check(ServerController.QuotePlayer("Alice Smith") == "\"Alice Smith\"", "Player names quoted");
    var p = new ServerProfile { InstallDirectory = Path.Combine(root, "install"), DataDirectory = Path.Combine(root, "data"), BackupDirectory = Path.Combine(root, "backups"), ServerName = "test" };
    Directory.CreateDirectory(p.ConfigDirectory); Directory.CreateDirectory(p.WorldDirectory); Directory.CreateDirectory(Path.Combine(p.DataDirectory, "db"));
    File.WriteAllText(p.IniPath, ini); File.WriteAllText(Path.Combine(p.WorldDirectory, "map.bin"), "world version one");
    File.WriteAllText(Path.Combine(p.DataDirectory, "db", "test.db"), "database one");
    File.WriteAllText(Path.Combine(p.ConfigDirectory, "other.ini"), "unrelated");
    File.WriteAllText(Path.Combine(p.ConfigDirectory, "test_other_SandboxVars.lua"), "other profile with shared prefix");
    string backup = BackupService.Create(p);
    using (var checkZip = ZipFile.OpenRead(backup)) Check(!checkZip.Entries.Any(e => e.FullName.Contains("test_other")), "Shared-prefix profiles excluded from backup");
    Check(File.Exists(backup), "Backup archive created");
    File.WriteAllText(p.IniPath, "changed"); File.WriteAllText(Path.Combine(p.WorldDirectory, "map.bin"), "changed world");
    File.WriteAllText(Path.Combine(p.WorldDirectory, "stale.bin"), "remove on restore");
    string safety = BackupService.Restore(p, backup);
    Check(File.ReadAllText(p.IniPath) == ini && File.ReadAllText(Path.Combine(p.WorldDirectory, "map.bin")) == "world version one", "Restore config and world");
    Check(!File.Exists(Path.Combine(p.WorldDirectory, "stale.bin")), "Restore removes stale world files");
    Check(File.ReadAllText(Path.Combine(p.ConfigDirectory, "other.ini")) == "unrelated", "Other profile preserved");
    Check(File.Exists(safety), "Restore creates safety backup");
    string corrupt = Path.Combine(root, "corrupt.zip"); File.Copy(backup, corrupt);
    using (var zip = ZipFile.Open(corrupt, ZipArchiveMode.Update))
    {
        zip.GetEntry("Server/test.ini")!.Delete();
        using var w = new StreamWriter(zip.CreateEntry("Server/test.ini").Open()); w.Write("tampered");
    }
    Throws(() => BackupService.Restore(p, corrupt), "Tampered backup rejected");
    Check(File.ReadAllText(p.IniPath) == ini, "Failed restore preserves live files");
    var sandbox = Path.Combine(p.ConfigDirectory, "test_SandboxVars.lua");
    File.WriteAllText(sandbox, "SandboxVars = {}\n");
    var withSandbox = BackupService.Create(p);
    File.Delete(sandbox); Directory.CreateDirectory(sandbox);
    File.WriteAllText(p.IniPath, "state before failed restore");
    Throws(() => BackupService.Restore(p, withSandbox), "Destination conflict triggers restore rollback");
    Check(File.ReadAllText(p.IniPath) == "state before failed restore" && File.ReadAllText(Path.Combine(p.WorldDirectory, "map.bin")) == "world version one", "Rollback restores previous config and world");
    Directory.Delete(sandbox); File.WriteAllText(p.IniPath, ini);
    string traversal = Path.Combine(root, "traversal.zip"); File.Copy(backup, traversal);
    using (var zip = ZipFile.Open(traversal, ZipArchiveMode.Update)) { using var w = new StreamWriter(zip.CreateEntry("../escape").Open()); w.Write("bad"); }
    Throws(() => BackupService.Restore(p, traversal), "Traversal archive rejected");
    var savedName = p.ServerName; p.ServerName = "../escape"; Throws(() => p.Validate(false), "Unsafe profile name rejected"); p.ServerName = savedName;
    var oldBackup = p.BackupDirectory; p.BackupDirectory = Path.Combine(p.DataDirectory, "backups"); Throws(() => p.Validate(false), "Nested backup directory rejected"); p.BackupDirectory = oldBackup;
    var fakeTools = Path.Combine(root, "fake tools"); Directory.CreateDirectory(fakeTools);
    foreach (var f in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(f, Path.Combine(fakeTools, Path.GetFileName(f)), true);
    var host = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ProjZomb.Tests.exe" : "ProjZomb.Tests");
    p.SteamCmdPath = Path.Combine(fakeTools, "steamcmd.exe"); File.Copy(host, p.SteamCmdPath, true);
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(p.SteamCmdPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    p.InstallDirectory = Path.Combine(root, "server install with spaces");
    await SteamInstaller.RunAsync(p, _ => { }, CancellationToken.None);
    Check(File.Exists(Path.Combine(p.InstallDirectory, "StartServer64.bat")), "SteamCMD orchestration passes anonymous app and spaced installation arguments");
    p.Branch = "bad";
    bool installFailed = false;
    try { await SteamInstaller.RunAsync(p, _ => { }, CancellationToken.None); } catch (InvalidOperationException) { installFailed = true; }
    Check(installFailed, "SteamCMD failure is not reported as installation success");
    p.Branch = "";
    using var server = new ServerController();
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var roster = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
    server.StateChanged += s => { if (s == "Running") ready.TrySetResult(); };
    server.PlayersChanged += players => { if (players.Count == 2) roster.TrySetResult(players); };
    var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    info.ArgumentList.Add(typeof(Program).Assembly.Location); info.ArgumentList.Add("--fake-server");
    server.StartProcess(info);
    await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); Check(server.Running && server.State == "Running", "Server readiness from output");
    server.Send("players"); var players = await roster.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(players.SequenceEqual(new[] { "Alice", "Bob" }), "Players command parsed");
    await server.StopAsync(TimeSpan.FromSeconds(10)); Check(!server.Running && server.State == "Stopped", "Graceful save/quit lifecycle");
    Console.WriteLine($"\n{passed} checks passed.");
}
finally { Directory.Delete(root, true); }
