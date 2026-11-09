using System.Text.RegularExpressions;

namespace ProjZomb.Core;

public static class ServerReadiness
{
    // The game's final startup banner, not intermediate Java/Steam/network initialization.
    // Accept raw console output and the General log prefix used by dedicated servers.
    private static readonly Regex Complete = new(
        @"^(?:(?:\[[^\r\n\]]+\]\s*)?LOG\s*:\s*General\b[^>\r\n]*>\s*(?:GameServer\.main\s*>\s*)?)?(?:\*{3,}\s*SERVER STARTED\s*\*{3,}|SERVER STARTED)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool IsStartupComplete(string line) => line.Length <= 8192 && Complete.IsMatch(line.Trim());
}
