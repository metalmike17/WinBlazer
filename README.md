# WinBlazer (Beta)

A Windows app for cleaning up, checking and adjusting your PC, with backups or undo for many of its changes.

> ## ⚠️ BETA: still in testing
> **This is an early beta. Expect bugs, and use it at your own risk.** WinBlazer can permanently delete files and change Windows settings. So far
> it has been tested on one PC, running an evaluation edition of Windows 11 IoT Enterprise LTSC. It has not had an
> independent security audit. **Back up important files before using it.**

WinBlazer does not promise a faster PC. Many tweaks make little or no measurable difference, and registry cleaning
rarely improves speed.

## What it does

| Area | What you can do |
|---|---|
| **Dashboard and System Monitor** | See processor, memory, disk, network and NVIDIA graphics activity, and end a program |
| **Cleanup** | Clear temporary files, caches and the Recycle Bin, see what uses disk space, and set up automatic cleaning |
| **Browser Cleaner** | Clear selected browser data and some Windows activity traces |
| **Game Mode** | Apply temporary settings while you play, then restore them |
| **System Tweaks** | Turn individual Windows settings on or off |
| **Startup Manager** | Choose what starts with Windows |
| **Memory Optimizer** | View memory use, free cached memory and limit background apps |
| **Network Tools** | Test ping, DNS and download speed, and change network settings |
| **Disk Repair** | Run Windows' repair and drive tools, and view drive health data |
| **Driver Manager** | List drivers, check for updates and back up third-party drivers |
| **Bloatware Remover** | Uninstall selected built-in apps |
| **Shortcut Fixer** | Find and repair broken shortcuts |
| **Registry Cleaner** | Remove certain broken registry entries, after saving a backup |
| **History** | See a record of what the app has done |

Each feature has limits. See [the details](docs/DETAILS.md).

## Requirements
- 64-bit Windows 11 or Windows 10. Only the Windows 11 edition named above has been tested.
- To install: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and an internet connection to
  download the app's build components. The .NET tools collect usage data by default; Microsoft explains
  [how to opt out](https://aka.ms/dotnet-cli-telemetry).
- Administrator permission to install, and for some features.
- NVIDIA graphics readings and settings need an NVIDIA card.

## Install
There is no ready-made download yet, so you build the app on your PC.

1. Download this project (**Code > Download ZIP**) and extract it, or clone it.
2. Read [`scripts\Install.ps1`](scripts/Install.ps1) if you want to see what it does. Only install code you trust.
3. Close WinBlazer if it is open.
4. Open a normal PowerShell window (not as administrator) in the project folder, and run:
   ```
   powershell -ExecutionPolicy Bypass -File scripts\Install.ps1
   ```
   `-ExecutionPolicy Bypass` lets this one run of the script start even if your PC blocks scripts.
5. Approve the Windows administrator prompt.

The app is installed in `Program Files\WinBlazer`, with Start menu and desktop shortcuts. To update, get the new
code and run the same command again.

The program file is named `PCOptimizer.exe`, after the app's earlier name.

## Before you use it
- **Read each confirmation.** If you are not sure what something does, leave it off.
- **Cleanup is permanent.** Deleted files, emptied Recycle Bin items and cleared browser data cannot be restored by
  WinBlazer. Clearing cookies usually signs you out of websites.
- **Removing an app deletes its local data.** Reinstalling may not be possible on every PC.
- **Ending a program can lose unsaved work.**
- **Do not turn off the PC during repairs or driver installs.** Back up first.
- A disk or drive health result is not a guarantee. If you suspect a failing drive, back up your files right away.

## What can be undone

| Change | How to go back | Limits |
|---|---|---|
| System Tweaks | **Restore** or **Reset to Defaults** | Restores what you had before, not factory settings. Values changed since are left alone |
| Game Mode | Restores when you turn it off or the followed game closes | Closed apps reopen without their earlier options. A power plan it created stays |
| Startup Manager | **Undo** | Keeps the last 20 changes |
| Shortcut Fixer | **Undo** | Newest change first. Never overwrites a newer file |
| Registry Cleaner | **Restore** | Newest backup only. Adds back what is missing and does not overwrite existing values |
| DNS and TCP/IP settings | Restore buttons in Network Tools | Restores what was saved before the first change |
| Driver updates | Device Manager, **Roll Back Driver** | Not always available. Third-party drivers are copied first |
| Removed apps | **Reinstall** | May need the Microsoft Store. Deleted app data does not come back |
| Cleanup and browser data | **No undo** | |
| Winsock reset | **No undo** | May affect VPN or security software |
| SFC, DISM, disk repair and drive optimizing | **No undo** | These are Windows' own tools |

**Uninstalling WinBlazer does not undo its changes.** Restore anything you want to restore first.

## Administrator use
Open WinBlazer normally. When a feature needs more permission, use its **Restart as administrator** button.

- Only the installed copy in `Program Files` can restart this way. Do not use **Run as administrator** on a copy in
  any other folder, because other programs may be able to change its files.
- Restore records for changes made as administrator are kept in folders that only administrators can change. An
  administrator copy restores only from those. Game Mode also keeps a copy in your user folder, so that it can
  restore without administrator rights, for example after a restart.
- Startup Manager's **Undo** covers changes made the same way: as administrator, or without. A Game Mode session
  started without administrator rights must be restored without them.
- As administrator, Shortcut Fixer's **Undo** covers shortcuts shared by all users. Open the app normally to undo
  changes to your own.
- An administrator copy does not follow folder links that were created without administrator rights, including
  ones you made yourself. This protection does not extend to other programs the app starts.
- If you enter a different account's password at the prompt, per-user features apply to that account.

## Internet use and privacy
WinBlazer's code has no analytics, advertising or usage tracking, and no feature that uploads your logs. These
features use the network. As with any connection, the service you reach can see your IP address.

| Feature | What it contacts |
|---|---|
| **Ping** (when you start it) | The server you enter; `1.1.1.1` (Cloudflare) is filled in to begin with |
| **DNS speed test** (when you start it) | Sends lookups for common websites to the DNS providers being compared, including your current one |
| **Download speed test** (when you start it) | `speed.cloudflare.com`; can use up to about 500 MB of data |
| **Check for driver updates** (when you start it) | Windows Update. With an NVIDIA card, also NVIDIA's website: the app downloads its product list, then sends numbers identifying your card model, whether you use Windows 10 or 11, and an English language code |
| **Install a driver** | Windows Update |
| **Repair Windows files (DISM)** | Windows may download repair files from Microsoft |
| **Website and Store links, app reinstall** | Opens your browser or the Microsoft Store, or asks Windows to reinstall the app |
| **Changing DNS** | Afterward, Windows sends your website lookups to the provider you chose |

Windows and the services above have their own privacy policies.

## Where your data is kept

| Folder | Contents |
|---|---|
| `%LOCALAPPDATA%\PCOptimizerNet` | Settings, history, logs and your own backups |
| `%ProgramData%\PCOptimizerNet-AdminBackups` | Originals for some administrator changes. Everyone on the PC can read it |
| `%ProgramData%\PCOptimizerNet-RegistryBackups` | Administrator registry backups |

Driver backups go in the folder you choose.

**Logs, backups and screenshots can contain your user name, file names, installed apps and network details.** Check
them before sharing.

## Uninstall
1. In the app, turn off automatic cleaning and Game Mode, and restore any changes you want to undo.
2. Close WinBlazer.
3. In a normal PowerShell window in the project folder, run:
   ```
   powershell -ExecutionPolicy Bypass -File scripts\Install.ps1 -Uninstall
   ```

Your settings, logs and backups are kept. Delete the folders above yourself only when you no longer need to restore
anything.

## Known issues
- When a disk check says an offline repair is needed, the app does not offer `chkdsk /spotfix`. Its **Fix** button
  runs `chkdsk /f`.
- Times in repair logs end in `Z` (universal time), although they may show your local time.
- Disk check, system repair and drive optimizing results are read from English messages. For anything the app does
  not recognize, read the log.
- Another program could stop WinBlazer from opening by imitating its check for an already open copy.

## Reporting problems
Open an issue on this project with what you did, what you expected, what happened and your Windows version. The app
saves unexpected errors to `crash.log`; check it for personal details before attaching it.

**Security problems:** please do not post the details publicly. Use **Report a vulnerability** on the project's
**Security** tab. If that button is missing, open an issue that only asks for a private way to get in touch.

## License
[MIT](LICENSE). The software comes with no warranty.

WinBlazer is an independent project. It is not made or endorsed by Microsoft or NVIDIA. Windows is a trademark of
Microsoft.
