using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace ProjZomb.Core;

[SupportedOSPlatform("windows")]
public sealed class WindowsSteamCmdPlatform : ISteamCmdPlatform
{
    public void VerifyPublisher(string executable)
    {
        var file = new WinTrustFile { Size = (uint)Marshal.SizeOf<WinTrustFile>(), Path = executable };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFile>());
        Marshal.StructureToPtr(file, filePointer, false);
        var data = new WinTrustData
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, RevocationChecks = 1,
            UnionChoice = 1, File = filePointer, StateAction = 1,
            ProviderFlags = 0x80 // Check the signing chain excluding the root; normal Windows trust/revocation remains enabled.
        };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (result != 0) throw new InvalidDataException($"SteamCMD signature verification failed (0x{result:X8}). Check Windows certificate/revocation access and retry; the executable was not run.");
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(executable));
            var publisher = certificate.GetNameInfo(X509NameType.SimpleName, false);
            if (publisher is not ("Valve" or "Valve Corp." or "Valve Corporation"))
                throw new InvalidDataException("The downloaded executable is not signed by Valve. It was not run.");
        }
        finally
        {
            data.StateAction = 2;
            _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<WinTrustFile>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    public async Task InitializeAsync(string executable, Action<string> log, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        info.ArgumentList.Add("+quit");
        using var process = new Process { StartInfo = info };
        var gate = new object();
        void Receive(object sender, DataReceivedEventArgs e) { if (e.Data is { } line) lock (gate) log(line); }
        process.OutputDataReceived += Receive; process.ErrorDataReceived += Receive;
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try { await process.WaitForExitAsync(cancel); process.WaitForExit(); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        if (process.ExitCode != 0) throw new IOException($"SteamCMD initialization exited with code {process.ExitCode}. Review the console and retry.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFile
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr File;
        public IntPtr KnownSubject;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallback, SipClient;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData, UrlReference;
        public uint ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
}
