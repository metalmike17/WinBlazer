; Built by scripts\Build-Release.ps1, which passes Version, Source and Output.
#ifndef Version
  #error Build this with scripts\Build-Release.ps1
#endif

[Setup]
AppId={{7C1E4B7A-3D52-4F0B-9B1D-5A4E2C8F6A31}
AppName=WinBlazer
AppVersion={#Version}
AppVerName=WinBlazer {#Version} (beta)
AppPublisher=metalmike17
AppPublisherURL=https://github.com/metalmike17/WinBlazer
AppSupportURL=https://github.com/metalmike17/WinBlazer/issues
; Always Program Files: other programs cannot change files there, which administrator restart relies on.
DefaultDirName={autopf}\WinBlazer
DisableDirPage=yes
UsePreviousAppDir=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; The app's one-copy marker: setup and uninstall wait until WinBlazer is closed.
AppMutex=WinBlazer.Window
LicenseFile=..\LICENSE
InfoBeforeFile=beta-notice.txt
SetupIconFile=..\Assets\WinBlazer.ico
UninstallDisplayIcon={app}\PCOptimizer.exe
UninstallDisplayName=WinBlazer (beta)
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
OutputDir={#Output}
OutputBaseFilename=WinBlazer-{#Version}-beta-Setup

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\WinBlazer"; Filename: "{app}\PCOptimizer.exe"
Name: "{autodesktop}\WinBlazer"; Filename: "{app}\PCOptimizer.exe"; Tasks: desktopicon

[Run]
; Started without administrator rights.
Filename: "{app}\PCOptimizer.exe"; Description: "Open WinBlazer"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
function InitializeUninstall(): Boolean;
begin
  Result := SuppressibleMsgBox(
    'Uninstalling WinBlazer does not undo changes it made.' + #13#10#13#10 +
    'Before continuing, open WinBlazer to turn off automatic cleaning and Game Mode, and restore any ' +
    'changes you want to undo. Your settings, logs and backups will be kept.' + #13#10#13#10 +
    'Continue uninstalling?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDYES) = IDYES;
end;
