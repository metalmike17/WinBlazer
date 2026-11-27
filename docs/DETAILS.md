# WinBlazer: details

How each feature behaves, and notes for developers. Start with the [README](../README.md).

## Developing
Needs Windows and the .NET 10 SDK. The output folder is still named `net8.0-windows` so that existing shortcuts and
scheduled tasks keep working.
```
dotnet run -c Release
```
Self-test, in development builds only (throwaway settings and files; writes a log and page images). It also briefly
registers and removes a harmless scheduled task dated 2099.
```
bin\Release\net8.0-windows\PCOptimizer.exe --selftest C:\some\output\folder
```
The program file and internal names still say `PCOptimizer`, the app's earlier name, so that saved settings and
backups keep working.

## Layout
- `Core/` module discovery, settings, scan scheduler and the safety measures
- `Modules/` the app's pages
- `Services/` the work behind each page
- `Controls/`, `Themes/` shared interface pieces and styling
- `scripts/` install and uninstall
## Adding a module
Add a class in `Modules/` that implements `IModule`. It appears in the sidebar and on the Settings page
without edits to existing code. Also implement `IScannableModule` to get a schedule option; its `ScanAsync`
must not change anything. Modules are compiled into the app; external plugin DLLs are not loaded.
(The self-test checks the number of modules, so update it when you add one.)

Modules with background work implement `IBackgroundModule`.

## Disk Space Cleaner
- Scanning is read-only and nothing is pre-ticked. **Clean selected** asks first; deletion is permanent.
- Links are never followed, and each file's path is checked again just before deletion.
- Folders are left in place; only files are removed. Files in use are skipped.
- The minimum age setting applies to the two temp categories only.
- Browser categories remove only Cache, Code Cache and GPUCache (Firefox: cache2) in standard profile locations.
- Windows.old is report-only; remove it through Windows Storage settings.
- A size marked "at least" or "Unknown" means some folders could not be read.
- If you elevate with a different administrator account, per-user categories refer to that account.
- Disk Analyzer is read-only. It shows file lengths (not allocated size; hard links may count twice), skips linked
  folders and cloud placeholders, and lists the largest 40 files per folder with the rest grouped.

## Behavior
- Settings: `%LOCALAPPDATA%\PCOptimizerNet\settings.json`
- Scheduled scans run only while the app is open or in the tray, and never delete anything. A newly
  enabled schedule runs within about a minute.
- Closing the window exits. Minimizing hides to the tray (can be turned off).
- The glass look uses translucent panels over soft color glows; it does not blur the desktop behind the window.
- GPU readings need an NVIDIA GPU.

## Browser Data Cleaner
- All switches start off. Cookies have a separate warning. A running browser is skipped, never force-closed.
- A choice applies to every standard profile of that browser.
- History and downloads are removed by editing the database in one transaction; an unrecognized layout is left
  unchanged. Firefox bookmarks are preserved. Other browser stores (such as site icons or site storage) may
  still show traces. Chromium "cookies" removes cookie files only, not other site storage.
- Tracks Eraser covers recent file shortcuts, jump lists (keeps Quick Access pins), a fixed list of Explorer MRU
  registry keys, clipboard (pinned history stays), UserAssist and RecentApps. Location history and Windows Activity
  history are manual, through Settings buttons.
- Deletion is not forensic erasure, and sync can restore data.
- `nuget.config` adds nuget.org for this project. `SQLitePCLRaw.bundle_e_sqlite3` is pinned to a newer version
  because the default one had a high-severity advisory.

## Registry Cleaner
- Registry cleaning rarely improves speed. Nothing is pre-ticked; Risky items need an extra confirmation.
- Checked locations: Uninstall, App Paths, SharedDLLs, Run/RunOnce, and empty or dangling file type keys.
  "Leftovers" means startup entries whose program is missing; it does not guess at vendor software keys.
- Before deletion, a `.reg` file of exactly what will be removed is saved and verified. If that fails, nothing
  is deleted. Backups do not include registry key permissions.
- User backups: `%LOCALAPPDATA%\PCOptimizerNet\RegistryBackups`. Administrator backups:
  `%ProgramData%\PCOptimizerNet-RegistryBackups` (administrators only).
- **Restore** re-applies only the newest backup, adding what is missing without overwriting existing values.
  Older backups are kept. Importing a `.reg` file by hand in Registry Editor **does** overwrite values.
- HKLM entries need administrator rights.

## Temp File & Cache Cleaner
- **Quick Clean:** old user temp files and the Recycle Bin. **Deep Clean:** user and system temp, browser caches,
  GPU shader caches, Windows Update downloads, Delivery Optimization, IIS logs, memory dumps, error reports and
  `.log` files in `Windows\Logs`. It does not search drives for other log files.
- Both check first and ask once before deleting. The progress bar and bytes freed update during file deletion;
  Recycle Bin and Delivery Optimization update when each finishes. Cancel does not restore deleted files.
- Temp, report, IIS and Windows log files respect the minimum age setting; caches and dumps do not.
- Delivery Optimization is cleared with Windows' `Delete-DeliveryOptimizationCache` command.
- **Automatic cleaning** creates one Task Scheduler task, `PCOptimizer Auto Clean (user)`. It always runs without
  elevation, so protected items are skipped. Automatic Recycle Bin
  emptying is opt-in. Results: `%LOCALAPPDATA%\PCOptimizerNet\auto-clean.log`. Remove the schedule before deleting
  the app. This is separate from the read-only scan schedules on the Settings page.

## Memory Optimizer
- Chart: in use, modified and standby over the last 2 minutes (history starts when the app starts).
- Actions show free and available memory before and after. Standby memory is reusable cache, so purging it
  raises "free" without necessarily making anything faster. Flush Modified, Purge Standby and Flush File Cache
  need administrator rights. Without them, Empty Working Sets trims only processes it can access.
- The memory list commands use an undocumented Windows interface (as used by tools such as RAMMap).
- **Smart Standby Cleaner** (off by default): purges when free memory is below the threshold, standby is at least
  256 MB, and 60 seconds have passed. Requires the app to be running as administrator.
- **Background App Limiter:** idle priority plus Efficiency mode, matched by full program path. Restored on exit;
  after a crash, restart the limited apps. An app updated into a new folder must be selected again.

## Startup Manager
- Lists Run keys, RunOnce and policy Run keys, both Startup folders, and tasks with a boot or sign-in trigger
  (hidden ones included). Not covered: services, Store app startup items, other Windows autostart mechanisms.
  Without administrator rights, Windows may not show every task.
- Turning an entry off uses the same `StartupApproved` values as Task Manager, or disables the scheduled task
  (which also stops its other triggers). Nothing is deleted.
- Tasks under `\Microsoft\`, RunOnce, policy and 32-bit user entries are view-only. Disable All also skips
  Windows security items; it cannot recognize every third-party security tool.
- The previous state is saved before every change (`startup-undo.json`, or the protected folder when elevated; last 20 changes).
- Publisher comes from the program's file details and is **not** a verified signature.
- Boot impact uses Windows' recorded measurements (usually administrator only), rated with Task Manager's
  documented thresholds. It is not a prediction of seconds saved. Without data it shows "Not measured".

## Game Mode
- Steps (each can be unticked): Ultimate Performance plan (reuses an existing one, otherwise creates a copy,
  which stays afterward), Game DVR capture and controller Game Bar button off, notification pop-ups off
  (registry values; may not apply immediately, and this is not Do Not Disturb), NVIDIA global power mode
  "prefer maximum performance" (NVAPI; NVIDIA only; per-game profiles override it), ask chosen apps to close,
  purge standby memory (administrator only).
- Auto-activation is off by default. It polls every 3 seconds, needs two sightings to start, and restores about
  12 seconds after the followed game closes. A manual session also follows a game once one is detected.
- Games: Steam libraries, Epic manifests, GOG registry entries, `XboxGames` folders (and `.GamingRoot`), custom
  programs or folders. Store games installed elsewhere need a custom path. Detection matches program paths and
  skips helper names such as crash handlers and launchers.
- **Restore safety:** originals are saved to `gamemode-session.json` before each change. Restore runs when the
  game closes, on Turn off, on app exit, by a watchdog process if the app is ended, at next sign-in (RunOnce)
  and at next app start. Settings you changed during the session are left alone. Failed items stay recorded.
  Do not move or delete the app while a session is pending. Apps are reopened without their earlier arguments.

## System Tweaks & Speed Optimizer
- 63 tweaks (Performance, Network, Power, Visual Effects, Privacy), defined in `Services/Tweaks/TweakCatalog.cs`.
- **Safe** means a plain setting of your own account: no administrator rights, no policy keys. One-Click Optimize
  applies only Safe tweaks, after a confirmation. Risky toggles ask first.
- The original is saved to `tweaks-journal.json` before each change. **Reset to Defaults** restores those
  originals (what you had before, not factory defaults). Values changed since are left alone. A setting that was
  already in place before the app touched it cannot be turned off here, because there is no original.
- Power tweaks change the plugged-in value of the plan that is active when applied.
- **Flush DNS cache** and **Winsock reset** are one-time repairs. Winsock reset cannot be undone.
- Reverting restores values; empty registry keys created along the way may remain.
- If you elevate with a different administrator account, per-user tweaks refer to that account.

## Disk Repair
- **SFC /scannow**, **DISM /RestoreHealth**, **chkdsk** (read-only `/scan`, or `/f` fix) and **defrag** (analyze, or
  optimize: SSDs get TRIM only, hard disks are defragmented, unknown types use `/O`). All need administrator rights.
- `chkdsk /f` on the Windows drive is scheduled for restart; the result at restart is not collected by the app.
  Other drives are never force-dismounted.
- One tool at a time. Only read-only jobs can be cancelled. Closing the window is blocked while a tool runs.
  Do not shut down or end the app from Task Manager during a repair.
- Results are interpreted from English output and exit codes; anything unrecognized says to read the log.
  Logs: `%LOCALAPPDATA%\PCOptimizerNet\RepairLogs`.
- **Drive Health:** NVMe health log, or SATA SMART attributes and thresholds (administrator only), temperature and
  the drive's failure warning. USB and RAID controllers often hide this data. The score is a simple rule set in
  `Services/Repair/SmartData.cs`, not a failure prediction. No SMART data means no score.

## Shortcut Fixer
- Scans `.lnk` files on your and the public desktop and Start menus. `.url` links, taskbar pins and Store app
  shortcuts (no file target) are not checked.
- **Broken target** needs a definite "not found". Network, unplugged and protected paths are listed as unchecked.
- **Start in folder** is flagged only when it does not exist. **Missing arguments** is flagged only for programs
  that cannot do anything useful without them; lost arguments in other programs cannot be detected.
- **Possibly uninstalled** is a judgment (program folder gone, no match found, no install record), or Windows
  Installer saying the product is not installed.
- Moved files are matched by name; only a single match in a similarly named folder counts as clear. The search
  covers Program Files, local Programs, Documents, Pictures, Desktop, folders near the old location and the same
  path on other drives, with depth limits.
- Each shortcut is backed up (`ShortcutBackups`) before a change. Undo reverses the newest batch without
  overwriting newer files. Only the shortcut is ever removed, never its target.

## Driver Manager
- Lists drivers of present devices (WMI) with version, date, provider and health: Missing (code 28), Error,
  Outdated, Disabled or Working. Identical entries are grouped. "Working" does not mean up to date.
- **Check for updates** (never automatic) searches Windows Update for driver updates and, for NVIDIA cards, uses
  NVIDIA's website lookup (not a documented API; it may stop working). Outdated means Windows Update offers a
  newer-dated driver or NVIDIA lists a newer version. Age alone is never used. Other vendors get official links.
- **Update/Install** works only for Windows Update offers, with confirmation and license terms. Third-party current
  drivers are exported to `DriverSafetyCopies` first. No automatic restart. NVIDIA website drivers are not
  downloaded or installed by the app.
- **Back up drivers** (administrator) uses `pnputil /export-driver *`: third-party packages only, not drivers built
  into Windows. It also writes an inventory, a log and restore instructions.

## Bloatware Remover
- Lists only apps named in `Services/Bloatware/AppCatalog.cs`. Windows components, runtimes, the Store, security,
  winget and unrecognized apps are never shown; packages Windows marks as system or non-removable are hidden too.
- Uninstalls for the current user only (no all-users removal or deprovisioning). Running apps are skipped.
  Local app data is deleted. Windows updates or new accounts may bring apps back.
- Sizes are app files plus your app data, shown as "up to": Windows may keep the app files.
- **Reinstall** first re-registers files still on the PC, otherwise opens the Store page. Without the Store app only
  Microsoft's web store can be opened, and installing from it may not work.
- **Debloat Preset** is this app's own list, inspired by tools such as Chris Titus's Windows Utility but not copied
  from it. It excludes Xbox, everyday tools, mail, Phone Link and third-party apps. It always asks first.

## Network Tools
- The top card shows the selected adapter's local IP, gateway, DNS and IPv6 (read locally; public IP is not looked up).
- **Ping:** one per second, 2-minute graph, jitter and packet loss. Some servers ignore ping.
- **DNS:** times UDP lookups of common sites over IPv4 against Cloudflare, Google, Quad9, OpenDNS, AdGuard and
  CleanBrowsing Security. Switching (administrator) also sets IPv6 when in use and saves the first original.
  This backup is separate from the System Tweaks Cloudflare DNS toggle; use one of the two to avoid confusion.
- **TCP/IP:** auto-tuning, RSS, RSC and the adapter's offload properties (administrator). Originals are saved and can
  be restored. No speed improvement is promised.
- **Winsock reset:** administrator, confirmation, restart required. The catalog is saved for reference only; the
  reset cannot be undone by the app and may affect VPN or security software.
- **Speed:** download only, from speed.cloudflare.com, up to 10 s or about 500 MB.

## System Monitor
- **Performance:** each logical CPU, GPU use and VRAM (first NVIDIA GPU via `nvidia-smi`), memory with the top apps by
  private working set, disk read/write and network down/up. One reading per second, 1-minute smooth charts; sampling
  stops when you leave the page.
- **Process Manager:** processes grouped by program file with CPU, private memory and I/O rate. I/O is all process
  reads and writes (not only disk). **End Task** forces a close after confirmation (default No).
- Locked: core and security processes, anything Windows marks critical, services and other users' processes, most
  programs in the Windows folder, WinBlazer itself, and anything that cannot be verified. Checks are repeated on
  the same process handle that is ended. A lock list cannot know every important third-party program.

## Quality of life
- **Search (Ctrl+K):** pages, features, settings, tweaks and removable app names. It only navigates.
- **Optimization score (dashboard):** junk files 25, system drive space 25, Safe tweaks 20, startup apps 15,
  available memory 15; unreadable areas are left out. A housekeeping checklist, not a benchmark. Junk and tweaks have
  one-click fixes (with confirmation); the others open the page where you decide.
- **Tray menu:** Clean (Quick Clean) and Optimize (Safe tweaks) ask first; Game Mode toggles like its page button.
- **Notifications:** after cleanup, in the window or as a Windows notification (can be turned off in Settings;
  Windows may suppress them). Scheduled auto-clean also notifies.
- **History:** timestamps, results and measured space freed, from when this feature was added (`history.jsonl`).

## Interface
- **Startup:** loading screen, then on first launch a two-step wizard: optional restart as administrator (that
  session only) and which modules to show. Closing the wizard saves nothing and it appears again next time.
- **Theme:** dark or light (sidebar button, Settings or Ctrl+T). Pages recolor in place.
- **Animation:** page slide/fade and counting numbers (dashboard, score, History total, memory, System Monitor CPU
  and memory). They follow the Windows "Animation effects" setting. WPF draws with the graphics card where it can;
  animation timing and layout still run on the window's thread.
- **Right-click** (or Menu / Shift+F10) on cards and list rows: copy text, plus that item's buttons and tick boxes.
  Choosing one is the same as clicking it. Cards with more than 10 controls offer copy and page actions only.
- **Keyboard:** press F1 for the list. F5 and Ctrl+Enter depend on the page; per-row buttons are not shortcuts.
- **Responsiveness:** scans, History, settings saved from pages, and drive, browser, app and adapter reads run in
  the background. Not every call was moved: settings are still read at startup in the background but modules and
  pages are built on the window's thread, and clearing the clipboard has to run there.

## Administrator safety
- Originals for administrator tweaks, DNS and TCP/IP changes, and shortcuts shared by all users are kept in
  `%ProgramData%\PCOptimizerNet-AdminBackups`. Everyone can read it; only administrators can change it. If the
  folder exists but others can change it, the app ignores it and refuses to write there.
- Such originals found in your user folder (including any saved by earlier versions) are not restored.
- As administrator, **Undo** in Shortcut Fixer only reverses shared shortcuts. Open the app normally to undo
  changes to your own.
- DNS addresses are shown before they are restored.
- Windows PowerShell is run so that it loads modules only from the Windows folder.
- An administrator copy turns on Windows' redirection protection as it starts, so it does not follow folder links
  made without administrator rights. If that fails, it closes. This also means such links you made yourself (for
  example to move a folder to another drive) are not followed as administrator. Programs the app starts are not
  covered.
- Startup Manager and Game Mode follow the same rule, with one file per Windows account in the protected folder.
  Game Mode keeps a second copy in the user folder for restores without administrator rights. Deleting or restoring
  that copy ends the session; an administrator copy takes the values only from the protected one.
- These measures have not had an independent security audit.

## One copy and crash log
- Only one WinBlazer window can be open per Windows session. Opening it again shows the open copy; if that copy
  runs as administrator, a message may appear instead. Restarting as administrator waits up to 30 seconds for the
  old copy to close. Automatic cleaning and Game Mode recovery are not limited.
- Unexpected errors are saved to `%LOCALAPPDATA%\PCOptimizerNet\crash.log` (one older file is kept). Nothing is
  sent anywhere. The log can contain file paths and your user name, so check it before sharing it.
