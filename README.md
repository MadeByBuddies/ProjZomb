# ProjZomb

A portable Windows desktop manager for a **local Project Zomboid Build 42 dedicated server**, built with C#/.NET 8, WPF and Windows Forms notification-area integration. Version 0.1.5.

## Version 1

- Automatically detect SteamCMD, install it directly from Valve with in-app progress, or select an existing folder. Install/update a dedicated server into your chosen folder (app 380870, anonymous login, optional branch).
- Start, save, gracefully stop and restart; readiness detection from server output; bounded live console and separate password input.
- Close to the notification area. Explicit exit gracefully stops the managed server. A named mutex prevents multiple managers from controlling the same local machine.
- Discover profile INI and the standard sandbox/spawn Lua files; searchable structured scalar editing, source comments and curated descriptions, plus full raw editing. Preserve unknown settings, ordering, comments and line endings. Lua is **never executed** by the editor.
- Edit semicolon-separated Workshop item IDs and internal mod IDs through ordered line lists.
- Online player list via `players`; request kick, username ban/unban, and access-level changes. Console responses remain authoritative.
- Manual local backups and restore of profile configuration, world and server database. SHA-256 manifest checks, traversal/duplicate entry rejection, safety backup and rollback on application errors.
- Configuration saves keep the previous file outside the server data directory and reject external-edit conflicts.

## Fluent interface

WPF UI supplies the flat controls and integrated title bar. Use the left sidebar to navigate; choose **Dark**, **Light** or **System** under **Appearance**. Dark is the default. The choice is saved locally in `data/appearance.json`; System follows Windows changes, and Windows high contrast takes precedence over normal appearance preferences. Opaque surfaces avoid wallpaper-dependent contrast. The custom title bar retains window controls, and closing still hides to the system tray.

Fields, dropdowns, list selection, the configuration grid and primary buttons use WPF UI theme resources rather than the old fixed foreground/background palette. Console command and private input now have separate rows so they remain usable at the minimum window size. Mods and backups scroll when space is limited.

## Run the portable release

On Windows 10/11 x64, extract the portable ZIP to a user-writable folder and run `ProjZomb.exe`. Keep its `data` subfolder when moving it. The game server, downloaded mods, worlds and backups remain separate files; a single executable does not bundle them.

1. In **Setup**, select the directory containing `StartServer64.bat`, your Zomboid data root, server profile name and separate backup folder. The data root contains `Server`, `Saves` and `db`; select the same root used by an existing server.
2. In **Install / update server**, The default SteamCMD installation folder is `C:\SteamCMD`. Detection checks your saved location first, then `C:\SteamCMD`, followed by the older portable `tools/steamcmd` location and the system drive's `steamcmd` folder. Existing saved selections remain unchanged. If it is missing, choose **Install SteamCMD** or **Select Folder**. Installation downloads [Valve's official Windows archive](https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip), verifies its Valve Authenticode signature with Windows trust/revocation checks, runs `steamcmd.exe +quit` in the background to initialize its components, verifies the executable again, and selects the completed installation automatically. A bootstrap exit code of 7 triggers a bounded retry (up to three runs), with the executable signature rechecked each time; code 7 is never treated as installation success. Persistent errors include recent SteamCMD output for diagnosis. No setup script or manual extraction is needed. The app does not request elevation; installation requires write access to `C:\SteamCMD`. The URL and default installation folder are shown before clicking. Download progress is measured; signature checks and SteamCMD self-update use an indeterminate progress bar, with live output in Console. **Cancel installation** stays available below the pages. Partial setup files are cleaned up on failure/cancellation; if Windows holds a file open, Console identifies the temporary folder for manual removal. Existing nonempty folders are never overwritten. **Select Folder** validates that the selected folder contains `steamcmd.exe` and remembers it independently of your server paths. Only select an existing executable you trust.
3. Choose the game-server installation folder and branch, then **Install / update game server**. The button becomes available once SteamCMD is detected or installed. A blank branch uses Steam's public branch; confirm that this supplies Build 42. Branch names and available versions can change. Password-protected branches are not supported.
4. Save the profile and start. Watch **Console**. If the game asks for its initial administrator password, use **private input**; repeat if the game asks for confirmation. The manager does not echo entered commands, though the server itself can print sensitive output.
5. Wait for **Server Start Complete** in the status header and Console. The manager recognizes the final `SERVER STARTED` banner in General or Network output and automatically sends `help` once. A recognized help response upgrades the status to **Server Start Complete — console verified**. If no recognizable reply arrives within 30 seconds, it shows **help check unconfirmed** and keeps the server-reported startup state; a late reply can still verify it. A running process, Steam initialization and mod/world loading alone are not readiness. Restarting, stopping or an unexpected exit clears readiness. This is the server's readiness report; it does not test external router/firewall reachability. If the server's output format changes, inspect the console rather than assuming a startup hang.
6. Stop the server before saving configuration, installing updates, backups or restore. Configuration can be loaded and edited while running; edits stay in memory until you save after stopping. Confirm there is no independently started copy of that server. The app cannot supervise a server it did not launch.
7. Configuration loads automatically when the server starts and again when its startup banner appears, so first-run generated files become available. Automatic refresh does not discard unsaved edits. You can also use **Reload files** while running. Edit settings or Raw at any time; **Validate and save locally** is disabled until the server stops. Save after stopping, then start again to apply changes. Existing external-file-change checks still prevent overwriting newer on-disk content. Raw editing deliberately permits arbitrary game configuration; it is not a Lua syntax validator.
8. Load the **Mods** lists before editing. Reorder lines to reorder mods. Dependency resolution, Workshop browsing and compatibility validation are manual. Map settings remain available in Configuration.
9. Refresh **Players** while running. If your game build emits another roster format, enter an exact username or use the console. Actions are requests; check game responses. This version bans usernames, not Steam IDs.
10. Backups require a stopped server. A restore replaces only the named profile, takes a safety snapshot first, validates archived file hashes and removes stale world files. SHA-256 detects corruption, not the authenticity of an untrusted archive. Keep backups private: they contain server passwords and player data.

The window's X button hides it in the system tray. Use the tray menu or **Exit manager** to shut down intentionally. No automatic restart, automatic game update, telemetry, cloud upload, Discord integration or remote management is included.

### Operating notes

- The app reads the game's `StartServer64.bat` and writes a temporary sibling `.cmd` launcher to forward all arguments and skip interactive `PAUSE`, keeping its classpath and JVM settings. The original batch file is unchanged. The installation folder must be writable. Custom/multi-line launchers and hardcoded profile/data arguments are rejected for review. The helper is removed on normal process exit; after an OS crash a leftover `.projzomb-launch-*.cmd` can be deleted when stopped. It supplies `-servername` and `-cachedir`. The launcher's configured JVM memory applies. Adjust the game's supported launch configuration if the server reports memory allocation failure. Shell metacharacters in installation/data paths are rejected; use plain Windows paths.
- Router forwarding and firewall setup are manual. Typical game ports are UDP 16261 and 16262; use the actual configured ports. Do not expose RCON publicly. This app uses process stdin, not RCON, and needs no RCON password.
- The target is Build 42; version and mod combinations require testing. The app does not silently change game branches or migrate saves.
- Descriptions are taken from comments provided by the server/mod plus a small curated catalog. Unknown settings explicitly have no verified description. Full, authoritative descriptions for every evolving Build 42/mod setting are **not bundled**. Structured editing covers simple assignment lines; arrays, expressions and unusual Lua structures remain raw.
- There is no automatic attachment to already-running servers. Stop an existing server yourself before starting it here.
- Unsigned executables may show a Windows reputation warning. This release is not code-signed.
- Backup restore is protected against normal application failures, but is not power-loss transactional. Keep independent backups. Restore is limited to 100 GiB of expanded contents. Reparse points/symlinks in backed-up data are rejected.
- First-run settings and backup paths default to local disk. Store the portable folder outside Program Files to allow profile writes.

## Develop in VS Code on Windows

Install the official [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and VS Code's Microsoft C# tooling. Open this repository; use Terminal → Run Task or these commands:

```powershell
dotnet build ProjZomb.sln -c Release
dotnet run --project tests/ProjZomb.Tests
dotnet run --project src/ProjZomb.App
```

Publish the portable self-contained executable (the recipient does not need .NET installed):

```powershell
dotnet publish src/ProjZomb.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o artifacts/win-x64
```

The Windows framework may extract bundled native libraries to the user's temporary folder. Build separately for other CPU architectures; this release targets x64. The interface uses the pinned WPF-UI 4.3.0 NuGet package (plus its WPF-UI.Abstractions dependency). Restore also obtains Microsoft WindowsDesktop/.NET targeting/runtime packs. Third-party notices and the upstream MIT license are copied into published distributions; keep them beside the executable.

On Windows, with the normal app closed, run the UI smoke harness:

```powershell
dotnet run --project tests/ProjZomb.UiTests
```

It opens the actual WPF window at its minimum size, resolves the theme resources, visits every section in dark/light themes, exercises configuration editing and dropdown templates, and measures primary/secondary resource text contrast against the application surface (4.5:1 minimum). It does not start a server or modify server data. This supplements manual inspection of actual control states, high contrast, DPI and window behavior; it is not a full visual accessibility audit.

Core tests run on Linux or Windows using only the SDK. The WPF project can be cross-compiled with `EnableWindowsTargeting`, but its UI and Windows batch launcher must run on Windows.

## Validation status

See [VALIDATION.md](VALIDATION.md) for checks executed in the development environment and the required Windows acceptance run. A compile and fake-server harness do not establish real Build 42 compatibility.

## Internet and data boundaries

The manager has no upload or telemetry code. **Install SteamCMD** makes an HTTPS download request to Valve's fixed official archive URL without sending local files, credentials or configuration. Redirects are refused, archive paths/content and download sizes are checked, and Windows verifies a trusted Valve digital signature before execution and after initialization. Trust failures stop setup; certificate/TLS checks are not disabled. Windows certificate verification may contact certificate/revocation services. Setup stages in a sibling temporary folder and publishes the installation only after initialization succeeds. It runs only the verified official executable, not a downloaded setup script. Initial components/self-updates are handled by Valve's SteamCMD. Existing user-selected executables remain the user's trusted selection, with the game installation reviewed separately. SteamCMD and the game contact Steam and have their own network behavior. No local source, server configuration, logs, credentials or player data are uploaded by the manager.
