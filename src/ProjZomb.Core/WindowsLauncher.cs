using System.Text.RegularExpressions;

namespace ProjZomb.Core;

public static class WindowsLauncher
{
    // Keep the game's classpath/JVM arguments but make forwarding deterministic.
    // Some distributed launchers forward only %1 %2 and end with PAUSE.
    // A generated sibling keeps %~dp0 correct without changing the original file.
    public static string Prepare(string original)
    {
        var newline = original.Contains("\r\n") ? "\r\n" : "\n";
        var lines = original.Replace("\r\n", "\n").Split('\n');
        int invocations = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"^\s*@?(?:rem\b|::)", RegexOptions.IgnoreCase)) continue;
            if (Regex.IsMatch(lines[i], @"^\s*@?pause\b", RegexOptions.IgnoreCase)) { lines[i] = "rem ProjZomb: skip interactive launcher pause"; continue; }
            if (!lines[i].Contains("zombie.network.GameServer", StringComparison.Ordinal)) continue;
            if (!Regex.IsMatch(lines[i], "^\\s*@?(?:\"[^\"]*java(?:\\.exe)?\"|\\S*java(?:\\.exe)?)\\s", RegexOptions.IgnoreCase))
                throw new InvalidDataException("Expected a direct java.exe invocation in the server launcher.");
            if (lines[i].TrimEnd().EndsWith('^')) throw new InvalidDataException("A multi-line/custom Java invocation is not supported. Use the stock StartServer64.bat launcher.");
            if (Regex.IsMatch(lines[i], @"(?:^|\s)-(?:servername|cachedir)(?:\s|=)", RegexOptions.IgnoreCase))
                throw new InvalidDataException("The launcher hardcodes -servername or -cachedir. Remove these custom arguments yourself before using the manager's selected profile/data root.");
            invocations++;
            lines[i] = Regex.Replace(lines[i], "\"%~?[1-9]\"", "");
            lines[i] = Regex.Replace(lines[i], @"(?<!%)%~?[1-9]\b", "");
            if (!lines[i].Contains("%*", StringComparison.Ordinal)) lines[i] += " %*";
        }
        if (invocations != 1) throw new InvalidDataException("Could not identify one direct zombie.network.GameServer invocation in StartServer64.bat. This custom launcher needs review.");
        return string.Join(newline, lines);
    }
}
