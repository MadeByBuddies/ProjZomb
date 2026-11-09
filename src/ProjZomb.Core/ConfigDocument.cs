using System.Globalization;
using System.Text.RegularExpressions;

namespace ProjZomb.Core;

public sealed record SettingInfo(string Category, string Description, double? Min = null, double? Max = null);
public sealed class ConfigEntry
{
    public string Key { get; init; } = "";
    public string Value { get; set; } = "";
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    internal int Line { get; init; }
    internal string Prefix { get; init; } = "";
    internal string Suffix { get; init; } = "";
    internal string Original { get; init; } = "";
    internal bool Lua { get; init; }
    public bool Sensitive => Key.Contains("password", StringComparison.OrdinalIgnoreCase) || Key.Contains("token", StringComparison.OrdinalIgnoreCase) || Key.Contains("webhook", StringComparison.OrdinalIgnoreCase);
}

// A conservative, lossless line editor. Lua is never executed. Complex expressions remain raw.
public sealed class ConfigDocument
{
    private readonly string[] lines;
    private readonly string newline;
    private readonly bool trailing;
    public List<ConfigEntry> Entries { get; } = new();
    public string OriginalText { get; }
    public ConfigDocument(string text, bool lua)
    {
        OriginalText = text;
        newline = text.Contains("\r\n") ? "\r\n" : "\n";
        trailing = text.EndsWith('\n');
        lines = text.Replace("\r\n", "\n").Split('\n');
        if (trailing) lines = lines[..^1];
        string? comment = null;
        var groups = new Stack<string>();
        string? longClose = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var s = lines[i].Trim();
            if (lua)
            {
                // Never offer apparent assignments inside Lua long strings/comments.
                if (longClose is not null)
                {
                    if (s.Contains(longClose, StringComparison.Ordinal)) longClose = null;
                    continue;
                }
                var longOpen = Regex.Match(s, @"\[(=*)\[");
                if (longOpen.Success)
                {
                    var close = "]" + longOpen.Groups[1].Value + "]";
                    if (!s[(longOpen.Index + longOpen.Length)..].Contains(close, StringComparison.Ordinal)) longClose = close;
                    comment = null;
                    continue;
                }
            }
            if (s.StartsWith('#') || s.StartsWith(';') || s.StartsWith("--"))
            { comment = (comment is null ? "" : comment + " ") + s.TrimStart('#', ';', '-', ' '); continue; }
            if (lua)
            {
                var group = Regex.Match(lines[i], @"^\s*(\w+)\s*=\s*\{\s*(?:--.*)?$");
                if (group.Success) { if (group.Groups[1].Value != "SandboxVars") groups.Push(group.Groups[1].Value); continue; }
                if (Regex.IsMatch(s, @"^\},?\s*(?:--.*)?$")) { if (groups.Count > 0) groups.Pop(); continue; }
            }
            var m = lua
                ? Regex.Match(lines[i], "^(\\s*([A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*)(true|false|-?[0-9]+(?:\\.[0-9]+)?|\"(?:[^\"\\\\]|\\\\.)*\"|'(?:[^'\\\\]|\\\\.)*')(\\s*,?\\s*(?:--.*)?)$")
                : Regex.Match(lines[i], @"^(\s*([^#;=\s][^=]*?)\s*=)(.*)$");
            if (!m.Success) { if (s.Length > 0) comment = null; continue; }
            string key = m.Groups[2].Value.Trim();
            if (lua && groups.Count > 0) key = string.Join('.', groups.Reverse()) + "." + key;
            var info = SettingCatalog.Get(key);
            Entries.Add(new ConfigEntry { Key = key, Value = m.Groups[3].Value, Original = m.Groups[3].Value,
                Category = info.Category, Description = (comment is { Length: > 0 } ? comment + "\n\n" : "") + info.Description,
                Line = i, Prefix = m.Groups[1].Value, Suffix = lua ? m.Groups[4].Value : "", Lua = lua });
            comment = null;
        }
    }
    public string Render()
    {
        var output = (string[])lines.Clone();
        foreach (var e in Entries)
        {
            if (e.Value == e.Original) continue;
            Validate(e);
            output[e.Line] = e.Prefix + e.Value + e.Suffix;
        }
        return string.Join(newline, output) + (trailing ? newline : "");
    }
    private static void Validate(ConfigEntry e)
    {
        if (e.Value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new InvalidOperationException(e.Key + ": single-line values only.");
        if (e.Lua && !Regex.IsMatch(e.Value, "^(true|false|-?[0-9]+(?:\\.[0-9]+)?|\"(?:[^\"\\\\]|\\\\.)*\"|'(?:[^'\\\\]|\\\\.)*')$"))
            throw new InvalidOperationException(e.Key + ": use a Lua boolean, number or quoted string. Complex expressions belong in raw editing.");
        if (e.Original is "true" or "false" && e.Value is not ("true" or "false"))
            throw new InvalidOperationException(e.Key + ": expected true or false.");
        var info = SettingCatalog.Get(e.Key);
        if (info.Min.HasValue && (!double.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < info.Min || n > info.Max))
            throw new InvalidOperationException($"{e.Key}: expected a number from {info.Min} to {info.Max}.");
    }
}

public static class SettingCatalog
{
    private static readonly Dictionary<string, SettingInfo> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PublicName"] = new("Identity", "Server name shown in the public browser. This is different from the profile filename."),
        ["PublicDescription"] = new("Identity", "Description shown in the server browser. Preserve game-supported formatting."),
        ["Public"] = new("Identity", "Controls the game's public listing. Steam visibility may behave differently."),
        ["Password"] = new("Access", "Password players use to join. Stored locally in the server INI; do not share this file."),
        ["Open"] = new("Access", "When true, new accounts can join. When false, accounts must be added to the whitelist."),
        ["MaxPlayers"] = new("Access", "Maximum concurrent players. CPU, memory and mods determine practical capacity.", 1, 100),
        ["DefaultPort"] = new("Network", "Main game UDP port. Router forwarding and Windows Firewall must permit inbound traffic.", 1, 65535),
        ["UDPPort"] = new("Network", "Direct-connection UDP port. Must not conflict with the main game port.", 1, 65535),
        ["RCONPort"] = new("Network", "Remote-console TCP port. Keep it private; this manager uses the local process console.", 1, 65535),
        ["RCONPassword"] = new("Network", "Remote-console password. Leave RCON unexposed to the internet. Never include this in support uploads."),
        ["PVP"] = new("Gameplay", "Whether players can damage one another. Other safety and faction settings also affect combat."),
        ["PauseEmpty"] = new("Gameplay", "Pauses world simulation while no players are connected, where supported by the selected build."),
        ["Mods"] = new("Mods", "Internal mod IDs in load order, separated by semicolons. These are not Workshop item numbers."),
        ["WorkshopItems"] = new("Mods", "Steam Workshop item numbers separated by semicolons. One item may provide several internal mod IDs."),
        ["Map"] = new("Mods", "Map folder names in load order, separated by semicolons. Map changes can require a new world; back up first."),
        ["DoLuaChecksum"] = new("Security", "Checks client Lua files against the server. Keep enabled unless you understand a documented compatibility requirement."),
        ["PopulationMultiplier"] = new("Zombies", "Scales zombie population. Higher values increase server load; other population controls also apply.", 0, 4),
        ["DayLength"] = new("Time", "Enumeration selecting day duration, not a literal count of minutes. Consult the targeted Build 42 settings."),
        ["WaterShut"] = new("World", "Enumeration selecting the water shutoff period; numeric values are game-defined choices."),
        ["ElecShut"] = new("World", "Enumeration selecting electricity shutoff timing; numeric values are game-defined choices.")
    };
    public static SettingInfo Get(string key)
    {
        if (Known.TryGetValue(key, out var x)) return x;
        var leaf = key.Split('.').Last();
        if (Known.TryGetValue(leaf, out x)) return x;
        return new(key.Contains('.') ? key[..key.LastIndexOf('.')] : "Other", "No verified description is bundled for this setting. See the comments supplied by your installed Build 42 server or mod. Values are preserved without interpretation; use raw editing for complex Lua. Restart unless the game explicitly supports reloading this option.");
    }
}
