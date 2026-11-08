# Validation and Windows acceptance

## Executed in the Linux development environment

- Official .NET SDK 8.0.427 downloaded from Microsoft's distribution endpoint and verified against the SHA-512 in Microsoft's release metadata. Normal TLS verification retained.
- WPF cross-compilation and Windows x64 self-contained single-file publishing: succeeded with zero warnings/errors.
- Original integration harness result: **32 passed, 0 failed** (expanded results below). SteamCMD behavior is exercised using a local test double, not the actual Steam client.
- Dependency-free C# harness: INI round trips, targeted edits, sensitive-field identification, range/newline rejection, Lua round trips/nesting/comments/long strings, expression rejection, launcher normalization, player-input rejection, backup/restore, profile isolation (including overlapping name prefixes), stale world cleanup, safety backup, tamper rejection, traversal rejection, rollback after destination conflict, profile/path validation, and a real child-process fake server exercising readiness, roster parsing and graceful shutdown.

Run the harness with `dotnet run --project tests/ProjZomb.Tests`. A nonzero exit indicates failure; this is an executable test harness rather than an xUnit/VSTest project. `dotnet test` is not a substitute.

## Not executed here

This environment is Linux with no Windows runtime or actual Project Zomboid installation. The Windows GUI/tray, batch execution, SteamCMD installation and actual Build 42 console commands are not runtime-validated. The fake server tests exercise the orchestration logic, not the game protocol's future compatibility. No claim of tested game compatibility is made.

## Required Windows acceptance before relying on a live world

Use a disposable Build 42 world, with independent backups of anything important:

1. Extract the ZIP into a writable directory. Launch `ProjZomb.exe` without installing .NET. Verify Setup, Console, Configuration, Mods, Players and Backups tabs render at normal and high DPI. Launching a second copy should show an already-running message.
2. Select a trusted Valve SteamCMD executable. Choose a new install folder containing spaces. Review installation details and confirm the selected branch is Build 42. Verify SteamCMD confirms app 380870 and the launcher exists. Test an invalid branch and ensure the app reports failure.
3. Select a separate disposable data root with spaces. Start a profile with a unique name. Complete any initial password prompts with private input. Confirm generated files appear in the selected data root, not an unintended default folder. Check that readiness appears only after server-start output. Check the original batch file remains unchanged.
4. Join using a compatible game client. Test Save, graceful Stop and Restart. Confirm the game saves, exits, and no Java child remains after Stop. Verify the temporary launcher is removed. Confirm a bind/startup failure stays visible in the console.
5. Close X, reopen from tray, and verify the server remains available. Exit explicitly and confirm the server gracefully stops. Test that a deliberately hung shutdown reports timeout without killing the process or closing the manager.
6. While stopped, reload config. Edit a simple INI setting, a nested sandbox value, spawn regions in Raw and an unfamiliar setting. Save and restart to verify the game accepts changes. Check source comments/order/unknown values survive. Invalid ports and structured Lua expressions should be refused. External edits after load must cause a save conflict.
7. Try an installation/data folder or profile change while configuration has unsaved edits; it must require saving/reloading first. Changes to the selected data root/profile should clear the previous editor and mod state.
8. Load mod lists, change their order and inspect the resulting semicolon lists. Confirm Workshop IDs and mod IDs remain independent. Try duplicate IDs and invalid Workshop numbers. Use a small known-compatible mod and inspect game startup.
9. With a test player connected, request the roster, kick, ban, unban and set/remove an access level. Inspect actual server responses. Confirm manual usernames work when output parsing cannot identify a roster. Do not assume Build 42 accepts every historical access level.
10. Stop every copy of the server. Create backup A, change the disposable world/config and create B. Restore A and verify game state plus settings. Confirm the safety backup contains the previous state and another profile is untouched. Restart and join again.
11. Read the notices around raw configuration, game updates and restore. No operation should upload source, configuration, logs, credentials or player data. SteamCMD/game network behavior is separate from this manager.

## Known scope limits

- The curated description catalog is intentionally small. Existing server/mod comments and raw editing supply broader access; authoritative detailed descriptions of every Build 42 or mod setting are not shipped.
- Standard profile files are `<name>.ini`, `<name>_SandboxVars.lua`, `<name>_spawnpoints.lua`, and `<name>_spawnregions.lua`. Arbitrary separate mod configuration files outside these are not managed or backed up.
- There is no live-map/character editor, remote agent, automatic scheduling, mod dependency solver, code signing or automatic force-kill.
- Backups require downtime and store local passwords/player data. SHA-256 is an integrity check, not a signature. Restore is not power-loss transactional.

## WPF UI migration (0.1.1)

- Pinned WPF-UI and its WPF-UI.Abstractions dependency to 4.3.0 through an exact WPF-UI NuGet version reference (the package resolves WPF-UI.Abstractions 4.3.0). Upstream release and package identity were checked against the official project and NuGet listing. The MIT notice is shipped with the executable.
- Removed fixed control foreground/background colors and retained WPF UI's templates for hover, focus, selection and disabled states. Added opaque FluentWindow/TitleBar chrome, sidebar navigation, persisted dark/light/system preference, Windows high-contrast precedence and scrolling for constrained page layouts.
- Linux cross-build includes a Windows-only UI smoke harness. This harness is **compiled, not executed here**; no actual Windows screenshot or live UI accessibility audit has been performed. Existing 32 core integration checks remain separate from UI checks.
- On Windows, close the normal app and run `dotnet run --project tests/ProjZomb.UiTests`. It resolves the actual resource dictionaries, visits all pages in dark/light, opens the access dropdown, enters a configuration cell, switches raw/settings, renders disabled actions and measures primary/secondary brush contrast against the application surface. It does not run a game server. Test profile paths are confined to its own output directory.
- Manual acceptance: inspect hover/pressed/disabled states, dropdown items, selected/unfocused grid rows and active cell text, password caret, raw editor selection, tooltips, and notices in dark/light/system and Windows high contrast. Check keyboard navigation, 100%/150%/200% DPI, resizing/maximize/snap, and close-to-tray. Windows high contrast overrides the stored normal theme; confirm it restores correctly when high contrast is turned off.

## SteamCMD setup (0.1.2)

- Final expanded integration harness result: **61 passed, 0 failed**.

- Checked Valve's official HTTPS archive URL and its current single `steamcmd.exe` entry. Inspected the public signature certificate data: the bootstrap's publisher is `Valve`. This is not a Windows Authenticode validation result; that API must be exercised on Windows.
- Added saved/default/common-folder detection, explicit existing-folder validation and persistence, direct official download, measured download progress, indeterminate verification/initialization progress and cancellation outside disabled setup controls.
- Setup restricts the archive URL/content/size, checks Valve Authenticode trust (including revocation) before execution and after self-update, initializes with `+quit`, checks for the installed Windows component, and publishes a staged installation without replacing an existing nonempty folder.
- Core checks use an injected HTTP handler and platform double: detection precedence, selected/missing folders, official URL restrictions, unsafe/corrupt archives, signature gating, initialization completeness, preservation of existing folders, progress and cancellation cleanup. Actual WinVerifyTrust and SteamCMD startup were not executed on Linux.
- Windows acceptance: on a clean writable portable folder, confirm the two missing-tool options; select an invalid and then a valid existing folder; restart and confirm persistence. On a separate clean copy, click Install SteamCMD, check progress while browsing Console, cancel during download/initialization, retry, and confirm the final default folder is automatically selected and game installation enabled. Confirm Valve signature errors fail closed without a running executable. Test download/network failure and a nonempty default folder, and verify existing files remain intact. Finally install a disposable game server through the automatically selected SteamCMD.
