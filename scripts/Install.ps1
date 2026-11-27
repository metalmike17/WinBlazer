<#
.SYNOPSIS
  Builds WinBlazer and installs it in Program Files. Use -Uninstall to remove it.
.DESCRIPTION
  Run from a normal PowerShell window. The build runs without administrator rights; Windows then asks for
  permission to copy the files. Settings, backups and history in your user folder are never removed.
  Before uninstalling, turn off automatic cleaning and Game Mode in the app.
#>
param(
    [switch]$Uninstall,
    # Used by the script itself for the administrator step.
    [switch]$AdminStep,
    [string]$Source
)
# Load PowerShell modules from Windows' own folder only, not from user folders.
$env:PSModulePath = "$PSHOME\Modules"
$ErrorActionPreference = 'Stop'

$programFiles = [Environment]::GetFolderPath('ProgramFiles')
$target = Join-Path $programFiles 'WinBlazer'
$exe = Join-Path $target 'PCOptimizer.exe'
$startMenu = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'WinBlazer.lnk'

function New-AppShortcut([string]$Path) {
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($Path)
    $link.TargetPath = $exe
    $link.WorkingDirectory = $target
    $link.IconLocation = "$exe,0"
    $link.Description = 'WinBlazer'
    $link.Save()
}

function Assert-NotRunning {
    $open = Get-Process PCOptimizer -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase) }
    if ($open) { throw 'WinBlazer is open. Close it, then run this again.' }
}

# Only a folder that is clearly this app is ever removed.
function Remove-Install {
    if (-not (Test-Path -LiteralPath $target)) { return }
    $item = Get-Item -LiteralPath $target -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$target is a link, so it was left alone." }
    if (-not (Test-Path -LiteralPath $exe)) { throw "$target does not look like WinBlazer, so it was left alone." }
    Remove-Item -LiteralPath $target -Recurse -Force
}

if ($AdminStep) {
    try {
        Assert-NotRunning
        if ($Uninstall) {
            Remove-Install
            if (Test-Path -LiteralPath $startMenu) { Remove-Item -LiteralPath $startMenu -Force }
        }
        else {
            if (-not (Test-Path -LiteralPath (Join-Path $Source 'PCOptimizer.exe'))) { throw 'The build output is missing.' }
            Remove-Install
            New-Item -ItemType Directory -Path $target | Out-Null
            Copy-Item -Path (Join-Path $Source '*') -Destination $target -Recurse -Force
            New-AppShortcut $startMenu
        }
        exit 0
    }
    catch {
        Write-Host $_.Exception.Message -ForegroundColor Red
        Start-Sleep -Seconds 15
        exit 1
    }
}

$project = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $project 'artifacts\publish'
if (-not $Uninstall) {
    if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
    # Self-contained: the .NET files are installed with the app, inside Program Files.
    & (Join-Path $programFiles 'dotnet\dotnet.exe') publish (Join-Path $project 'PCOptimizerNet.csproj') `
        -c Release -r win-x64 --self-contained true -p:WithoutSelfTest=true -o $publish --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'The build failed, so nothing was installed.' }
}

$arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -AdminStep"
if ($Uninstall) { $arguments += ' -Uninstall' } else { $arguments += " -Source `"$publish`"" }
$windowsPowerShell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
$step = Start-Process $windowsPowerShell -ArgumentList $arguments -Verb RunAs -Wait -PassThru
if ($step.ExitCode -ne 0) { throw 'The administrator step did not finish. See its message.' }

$desktop = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'WinBlazer.lnk'
if ($Uninstall) {
    if (Test-Path -LiteralPath $desktop) { Remove-Item -LiteralPath $desktop -Force }
    Write-Host 'WinBlazer was removed. Your settings and backups were kept.'
}
else {
    New-AppShortcut $desktop
    Write-Host "WinBlazer was installed in $target."
}
