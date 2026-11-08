using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ProjZomb.Core;
using Forms = System.Windows.Forms;

namespace ProjZomb.App;

public partial class MainWindow : Window
{
    private readonly ServerController server = new();
    private readonly Forms.NotifyIcon tray = null!;
    private readonly string settingsPath = Path.Combine(AppContext.BaseDirectory, "data", "profile.json");
    private ServerProfile profile = new();
    private ConfigDocument? document;
    private string? loadedPath;
    private string? loadedText;
    private bool editorSync;
    private bool loading;
    private bool busy;
    private bool exiting;
    private readonly Mutex instanceMutex;

    public MainWindow()
    {
        instanceMutex = new Mutex(true, "Local\\ProjZomb-LocalManager", out bool first);
        if (!first) { MessageBox.Show("ProjZomb is already running. Check the system tray."); instanceMutex.Dispose(); System.Windows.Application.Current.Shutdown(); return; }
        InitializeComponent();
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "ProjZomb — Stopped", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open ProjZomb", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("Save and stop server", null, (_, _) => Dispatcher.Invoke(async () => await Perform(async () => await server.StopAsync())));
        menu.Items.Add("Exit…", null, (_, _) => Dispatcher.Invoke(async () => await ExitAsync()));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        server.Output += line => Dispatcher.BeginInvoke(() => AppendLog(line));
        server.StateChanged += state => Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = "● " + state;
            tray.Text = ("ProjZomb — " + state)[..Math.Min(63, ("ProjZomb — " + state).Length)];
            if (!server.Running) PlayersList.ItemsSource = Array.Empty<string>();
        });
        server.PlayersChanged += players => Dispatcher.BeginInvoke(() => { PlayersList.ItemsSource = players; Notice.Text = $"Online player response: {players.Count} player(s)."; });
        try { profile = ServerProfile.Load(settingsPath); }
        catch (Exception e) { Alert("Could not load saved profile: " + e.Message); }
        if (string.IsNullOrWhiteSpace(profile.DataDirectory)) profile.DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Zomboid");
        if (string.IsNullOrWhiteSpace(profile.BackupDirectory)) profile.BackupDirectory = Path.Combine(AppContext.BaseDirectory, "backups");
        InstallPath.Text = profile.InstallDirectory; DataPath.Text = profile.DataDirectory;
        BackupPath.Text = profile.BackupDirectory; SteamPath.Text = profile.SteamCmdPath;
        ProfileName.Text = profile.ServerName; BranchName.Text = profile.Branch;
        Closed += (_, _) => { tray.Dispose(); server.Dispose(); instanceMutex.ReleaseMutex(); instanceMutex.Dispose(); };
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void AppendLog(string text)
    {
        // Local display only. Do not persist or upload console output; servers can print secrets.
        if (ConsoleOutput.Text.Length > 500_000) ConsoleOutput.Text = ConsoleOutput.Text[^250_000..];
        ConsoleOutput.AppendText(text + Environment.NewLine); ConsoleOutput.ScrollToEnd();
    }
    private void Alert(string text) { Notice.Text = text; MessageBox.Show(this, text, "ProjZomb", MessageBoxButton.OK, MessageBoxImage.Information); }
    private bool Confirm(string text) => MessageBox.Show(this, text, "Confirm action", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    private async Task Perform(Func<Task> operation)
    {
        if (busy) { Notice.Text = "Wait for the current operation to finish."; return; }
        busy = true;
        SetupPanel.IsEnabled = LifecyclePanel.IsEnabled = ModsPanel.IsEnabled = BackupsPanel.IsEnabled = PlayerActions.IsEnabled = ConfigurationPanel.IsEnabled = false;
        try { await operation(); }
        catch (Exception e) { Alert(e.Message); }
        finally { busy = false; SetupPanel.IsEnabled = LifecyclePanel.IsEnabled = ModsPanel.IsEnabled = BackupsPanel.IsEnabled = PlayerActions.IsEnabled = ConfigurationPanel.IsEnabled = true; }
    }
    private Task Perform(Action operation) => Perform(() => { operation(); return Task.CompletedTask; });
    private ServerProfile ReadProfile()
    {
        return new ServerProfile { InstallDirectory = InstallPath.Text.Trim(), DataDirectory = DataPath.Text.Trim(),
            BackupDirectory = BackupPath.Text.Trim(), SteamCmdPath = SteamPath.Text.Trim(), ServerName = ProfileName.Text.Trim(), Branch = BranchName.Text.Trim() };
    }
    private void SaveProfile()
    {
        if (server.Running) throw new InvalidOperationException("Stop the server before changing its paths or profile.");
        var candidate = ReadProfile(); candidate.Validate(false);
        bool changed = candidate.DataDirectory != profile.DataDirectory || candidate.ServerName != profile.ServerName;
        if (changed && HasDirtyEditor()) throw new InvalidOperationException("Save or reload your current configuration edits before selecting another profile/data folder.");
        candidate.Save(settingsPath); profile = candidate;
        if (changed)
        {
            loading = true;
            try { document = null; loadedPath = null; loadedText = null; modsOriginal = null; modsPath = null; ConfigFiles.ItemsSource = null; SettingsGrid.ItemsSource = null; RawConfig.Clear(); WorkshopIds.Clear(); ModIds.Clear(); BackupsList.ItemsSource = null; }
            finally { loading = false; }
        }
        Notice.Text = "Profile saved locally. No credentials are stored in profile.json.";
    }
    private void RequireStopped()
    {
        if (server.Running) throw new InvalidOperationException("Stop the server before editing, updating or backing up its files.");
    }
    private bool ConfirmStopped() => Confirm("Confirm this server is stopped, including any copy started outside ProjZomb. Continuing while it is running can corrupt a backup or overwrite live data.");
    private void BrowseFolder(TextBox target)
    {
        using var picker = new Forms.FolderBrowserDialog { Description = "Select folder", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (Directory.Exists(target.Text)) picker.SelectedPath = target.Text;
        if (picker.ShowDialog() == Forms.DialogResult.OK) target.Text = picker.SelectedPath;
    }
    private void BrowseInstall(object s, RoutedEventArgs e) => BrowseFolder(InstallPath);
    private void BrowseData(object s, RoutedEventArgs e) => BrowseFolder(DataPath);
    private void BrowseBackups(object s, RoutedEventArgs e) => BrowseFolder(BackupPath);
    private void BrowseSteam(object s, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Valve SteamCMD|steamcmd.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) SteamPath.Text = picker.FileName;
    }
    private async void SaveProfileClick(object s, RoutedEventArgs e) => await Perform(SaveProfile);
    private async void StartClick(object s, RoutedEventArgs e) => await Perform(() =>
    {
        if (HasDirtyEditor()) throw new InvalidOperationException("Save or reload your configuration edits before starting.");
        SaveProfile();
        if (!Confirm("Start this installation as a Build 42 server? Verify your installed build/branch first. Mods may be downloaded by the game. You may need to enter the first-run administrator password in Console.")) return;
        server.Start(profile); Notice.Text = "Starting; watch the Console for readiness or first-run prompts.";
    });
    private async void StopClick(object s, RoutedEventArgs e) => await Perform(async () => await server.StopAsync());
    private async void RestartClick(object s, RoutedEventArgs e) => await Perform(async () =>
    {
        if (HasDirtyEditor()) throw new InvalidOperationException("Save or reload configuration edits first.");
        await server.StopAsync(); server.Start(profile);
    });
    private async void SaveWorldClick(object s, RoutedEventArgs e) => await Perform(() => server.Send("save"));
    private void HideClick(object s, RoutedEventArgs e) { Hide(); }
    private async void SendClick(object s, RoutedEventArgs e) => await SendCommand();
    private async Task SendCommand() => await Perform(() => { server.Send(CommandInput.Text); CommandInput.Clear(); Notice.Text = "Command sent; inspect the console response."; });
    private async void CommandKeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await SendCommand(); } }
    private async void SendSecretClick(object s, RoutedEventArgs e) => await SendPrivate();
    private async Task SendPrivate() => await Perform(() => { server.Send(SecretInput.Password); SecretInput.Clear(); Notice.Text = "Private input sent without echoing it in this manager."; });
    private async void SecretKeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await SendPrivate(); } }
    private async void InstallClick(object s, RoutedEventArgs e) => await Perform(async () =>
    {
        RequireStopped(); SaveProfile();
        if (!ConfirmStopped()) return;
        var branch = profile.Branch.Length == 0 ? "public (verify this is Build 42)" : profile.Branch;
        if (!Confirm($"Run your selected SteamCMD executable?\n\nExecutable: {profile.SteamCmdPath}\nInstall/update: {profile.InstallDirectory}\nApp: 380870\nBranch: {branch}\nLogin: anonymous\n\nSource documentation: https://developer.valvesoftware.com/wiki/SteamCMD\n\nOnly proceed if you obtained this executable from Valve. SteamCMD will download executable game files from Steam. Back up existing data before updating.")) return;
        Notice.Text = "SteamCMD is installing/updating. See Console; do not close the app.";
        await SteamInstaller.RunAsync(profile, line => Dispatcher.BeginInvoke(() => AppendLog(line)), CancellationToken.None);
        Alert("SteamCMD confirmed installation. Check the installed Build 42 version before starting.");
    });
    private bool HasDirtyEditor()
    {
        if (document is null) return false;
        SettingsGrid.CommitEdit(DataGridEditingUnit.Cell, true); SettingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        try { return EditorTabs.SelectedIndex == 1 ? RawConfig.Text != loadedText : document.Render() != loadedText; }
        catch { return true; }
    }
    private void ReloadFiles()
    {
        if (HasDirtyEditor() && !Confirm("Discard unsaved configuration edits and reload?")) return;
        RequireStopped(); SaveProfile();
        loading = true;
        try
        {
            document = null; loadedPath = null; loadedText = null;
            var files = Directory.Exists(profile.ConfigDirectory) ? Directory.GetFiles(profile.ConfigDirectory)
                .Where(p => profile.ConfigFileNames.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
                .Select(Path.GetFileName).OrderBy(x => x).ToArray() : Array.Empty<string>();
            ConfigFiles.ItemsSource = files;
            SettingsGrid.ItemsSource = null; RawConfig.Clear();
            ConfigFiles.SelectedIndex = files.Length > 0 ? 0 : -1;
            Notice.Text = files.Length == 0 ? "No configuration files yet. Start the server once to generate them, then stop and reload." : "Configuration files loaded. Passwords are visible here; do not share screenshots or files.";
        }
        finally { loading = false; }
        LoadSelectedFile();
    }
    private void LoadSelectedFile()
    {
        if (ConfigFiles.SelectedItem is not string filename) return;
        loadedPath = Path.Combine(profile.ConfigDirectory, filename);
        loadedText = File.ReadAllText(loadedPath);
        editorSync = true;
        try
        {
            document = new ConfigDocument(loadedText, filename.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
            SettingsGrid.ItemsSource = document.Entries; RawConfig.Text = loadedText; ApplyFilter();
        }
        finally { editorSync = false; }
    }
    private async void ReloadConfigClick(object s, RoutedEventArgs e) => await Perform(ReloadFiles);
    private async void ConfigFileChanged(object s, SelectionChangedEventArgs e)
    {
        if (loading || editorSync) return;
        await Perform(() =>
        {
            if (HasDirtyEditor() && !Confirm("Discard unsaved changes to the previous file?"))
            {
                loading = true; ConfigFiles.SelectedItem = loadedPath is null ? null : Path.GetFileName(loadedPath); loading = false; return;
            }
            LoadSelectedFile();
        });
    }
    private void ApplyFilter()
    {
        if (document is null) return;
        string query = ConfigSearch.Text.Trim();
        CollectionViewSource.GetDefaultView(SettingsGrid.ItemsSource).Filter = obj => obj is ConfigEntry e &&
            (query.Length == 0 || (e.Key + " " + e.Category + " " + e.Description).Contains(query, StringComparison.OrdinalIgnoreCase));
    }
    private void SearchChanged(object s, TextChangedEventArgs e) { if (IsInitialized) ApplyFilter(); }
    private void SettingSelected(object s, SelectionChangedEventArgs e)
    {
        if (SettingsGrid.SelectedItem is ConfigEntry row) SettingDescription.Text = row.Key + "\n" + row.Description + (row.Sensitive ? "\nSensitive value: stored only in local configuration." : "");
    }
    private void EditorTabChanged(object s, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, EditorTabs) || editorSync || document is null) return;
        editorSync = true;
        try
        {
            if (EditorTabs.SelectedIndex == 1)
            {
                SettingsGrid.CommitEdit(DataGridEditingUnit.Cell, true); SettingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
                RawConfig.Text = document.Render();
            }
            else
            {
                document = new ConfigDocument(RawConfig.Text, loadedPath!.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
                SettingsGrid.ItemsSource = document.Entries; ApplyFilter();
            }
        }
        catch (Exception ex) { Alert(ex.Message); EditorTabs.SelectedIndex = 0; }
        finally { editorSync = false; }
    }
    private void WriteChecked(string path, string original, string content)
    {
        RequireStopped();
        if (!File.Exists(path) || File.ReadAllText(path) != original) throw new IOException("This file changed outside the editor. Reload before saving to avoid losing changes.");
        if (content.Contains('\0')) throw new InvalidDataException("Configuration contains a NUL character.");
        Directory.CreateDirectory(profile.BackupDirectory);
        var snapshot = Path.Combine(profile.BackupDirectory, $"config-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}-{Path.GetFileName(path)}");
        File.Copy(path, snapshot);
        AtomicFile.Write(path, content);
    }
    private async void SaveConfigClick(object s, RoutedEventArgs e) => await Perform(() =>
    {
        RequireStopped();
        if (document is null || loadedPath is null || loadedText is null) throw new InvalidOperationException("Load a configuration file first.");
        SettingsGrid.CommitEdit(DataGridEditingUnit.Cell, true); SettingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var content = EditorTabs.SelectedIndex == 1 ? RawConfig.Text : document.Render();
        if (!ConfirmStopped()) return;
        if (EditorTabs.SelectedIndex == 1 && !Confirm("Raw editing bypasses setting validation. Lua is not executed or syntax-checked by this manager. Save with a local copy of the previous file?")) return;
        WriteChecked(loadedPath, loadedText, content); LoadSelectedFile(); Notice.Text = "Configuration saved with a local previous-file copy. Restart to apply changes.";
    });
    private async void LoadModsClick(object s, RoutedEventArgs e) => await Perform(() =>
    {
        RequireStopped(); SaveProfile();
        if (!File.Exists(profile.IniPath)) throw new FileNotFoundException("Start the server once to generate its INI, then stop it.");
        modsOriginal = File.ReadAllText(profile.IniPath);
        var ini = new ConfigDocument(modsOriginal, false);
        WorkshopIds.Text = string.Join(Environment.NewLine, (ini.Entries.FirstOrDefault(x => x.Key == "WorkshopItems")?.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        ModIds.Text = string.Join(Environment.NewLine, (ini.Entries.FirstOrDefault(x => x.Key == "Mods")?.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        modsPath = profile.IniPath;
    });
    private string? modsOriginal;
    private string? modsPath;
    private async void SaveModsClick(object s, RoutedEventArgs e) => await Perform(() =>
    {
        RequireStopped();
        if (modsOriginal is null || modsPath != profile.IniPath) throw new InvalidOperationException("Load the mod lists for this profile first.");
        if (HasDirtyEditor()) throw new InvalidOperationException("Save or reload your configuration edits first.");
        string[] Items(string text) => text.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var workshop = Items(WorkshopIds.Text); var mods = Items(ModIds.Text);
        if (workshop.Any(x => !Regex.IsMatch(x, @"\A[0-9]+\z"))) throw new InvalidOperationException("Workshop IDs must contain digits only.");
        if (mods.Any(x => x.Contains('=') || x.Contains('\0'))) throw new InvalidOperationException("Mod IDs cannot contain equals or NUL characters.");
        if (mods.Distinct(StringComparer.OrdinalIgnoreCase).Count() != mods.Length || workshop.Distinct().Count() != workshop.Length) throw new InvalidOperationException("Remove duplicate IDs.");
        var ini = new ConfigDocument(modsOriginal, false);
        foreach (var pair in new[] { ("WorkshopItems", string.Join(';', workshop)), ("Mods", string.Join(';', mods)) })
        {
            var entries = ini.Entries.Where(x => x.Key == pair.Item1).ToArray();
            if (entries.Length != 1) throw new InvalidOperationException("Expected exactly one " + pair.Item1 + " setting. Resolve it in raw configuration first.");
            entries[0].Value = pair.Item2;
        }
        if (!ConfirmStopped()) return;
        var result = ini.Render(); WriteChecked(modsPath, modsOriginal, result); modsOriginal = result;
        if (loadedPath == modsPath) LoadSelectedFile();
        Notice.Text = "Mod lists saved. The next server start applies the order.";
    });
    private async void RefreshPlayersClick(object s, RoutedEventArgs e) => await Perform(() => { PlayersList.ItemsSource = Array.Empty<string>(); server.Send("players"); Notice.Text = "Requested online players; inspect Console if no response appears."; });
    private void PlayerSelected(object s, SelectionChangedEventArgs e) { if (PlayersList.SelectedItem is string name) PlayerName.Text = name; }
    private async Task Moderate(string verb, string? suffix = null)
    {
        await Perform(() =>
        {
            var quoted = ServerController.QuotePlayer(PlayerName.Text.Trim());
            if (!Confirm($"Request {verb} for {quoted}" + (suffix is null ? "?" : $" with access level {suffix}?"))) return;
            server.Send(verb + " " + quoted + (suffix is null ? "" : " " + suffix));
            Notice.Text = "Moderation command sent. Check the console response before assuming success.";
        });
    }
    private async void KickClick(object s, RoutedEventArgs e) => await Moderate("kickuser");
    private async void BanClick(object s, RoutedEventArgs e) => await Moderate("banuser");
    private async void UnbanClick(object s, RoutedEventArgs e) => await Moderate("unbanuser");
    private async void AccessClick(object s, RoutedEventArgs e) => await Moderate("setaccesslevel", "\"" + ((ComboBoxItem)AccessLevel.SelectedItem).Content + "\"");
    private void RefreshBackups()
    {
        BackupsList.ItemsSource = Directory.Exists(profile.BackupDirectory) ? Directory.GetFiles(profile.BackupDirectory, profile.ServerName + "-*.zip").OrderByDescending(File.GetLastWriteTimeUtc).ToArray() : Array.Empty<string>();
    }
    private async void RefreshBackupsClick(object s, RoutedEventArgs e) => await Perform(() => { RequireStopped(); SaveProfile(); RefreshBackups(); });
    private async void BackupClick(object s, RoutedEventArgs e) => await Perform(async () =>
    {
        RequireStopped(); SaveProfile(); if (!ConfirmStopped()) return;
        Notice.Text = "Creating backup…";
        string path = await Task.Run(() => BackupService.Create(profile)); RefreshBackups(); Notice.Text = "Backup created: " + path;
    });
    private async void RestoreClick(object s, RoutedEventArgs e) => await Perform(async () =>
    {
        RequireStopped();
        if (HasDirtyEditor()) throw new InvalidOperationException("Save or reload editor changes before restoring.");
        if (BackupsList.SelectedItem is not string archive) throw new InvalidOperationException("Select a backup first.");
        if (!ConfirmStopped() || !Confirm("Replace this profile's world, configuration and server database with the selected backup? A safety backup will be created first.")) return;
        Notice.Text = "Validating and restoring…";
        string safety = await Task.Run(() => BackupService.Restore(profile, archive));
        document = null; loadedPath = null; loadedText = null; SettingsGrid.ItemsSource = null; RawConfig.Clear(); modsOriginal = null;
        RefreshBackups(); Alert("Restore completed. Reload configuration files before editing. Safety backup: " + safety);
    });
    private void WindowClosing(object? s, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true; Hide();
    }
    private async void ExitClick(object s, RoutedEventArgs e) => await ExitAsync();
    private async Task ExitAsync()
    {
        if (busy) { Alert("Wait for the current operation to finish before exiting."); return; }
        if (HasDirtyEditor() && !Confirm("Discard unsaved configuration edits and exit?")) return;
        if (server.Running)
        {
            if (!Confirm("Save and gracefully stop the server, then exit? Choose No to keep it running in the tray.")) return;
            await Perform(async () => await server.StopAsync());
            if (server.Running) return;
        }
        exiting = true; Close();
    }
}
