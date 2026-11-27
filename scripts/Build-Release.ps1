<#
.SYNOPSIS
  Builds the downloadable installer in artifacts\release. Publishes nothing.
.DESCRIPTION
  Needs the .NET 10 SDK and Inno Setup 6. Debugging data is left out because it records folder paths, which
  contain the Windows user name. The build stops if personal details are still found in the files to be packaged.
#>
param(
    [string]$Version = '0.1.0',
    # Extra words that must not appear in the packaged files, such as an email name.
    [string[]]$Private = @()
)
$env:PSModulePath = "$PSHOME\Modules"
$ErrorActionPreference = 'Stop'

$project = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $project 'artifacts\release\app'
$output = Join-Path $project 'artifacts\release'
$dotnet = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'dotnet\dotnet.exe'
$compiler = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw 'Inno Setup 6 is not installed.' }

if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
# Rebuilt from nothing, so that no earlier output with debugging data is reused.
foreach ($old in 'obj\Release\win-x64', 'bin\Release\net8.0-windows\win-x64') {
    $path = Join-Path $project $old
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
& $dotnet publish (Join-Path $project 'PCOptimizerNet.csproj') -c Release -r win-x64 --self-contained true `
    -p:WithoutSelfTest=true -p:DebugType=none -p:DebugSymbols=false "-p:PathMap=$project=/src/WinBlazer" `
    "-p:Version=$Version" -o $stage --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
Get-ChildItem -LiteralPath $stage -Recurse -File -Filter *.pdb | Remove-Item -Force

# Licenses of everything that is packaged.
$licenses = New-Item -ItemType Directory -Force (Join-Path $stage 'licenses')
$packages = Join-Path $env:USERPROFILE '.nuget\packages'
$runtime = (Get-Content -LiteralPath (Join-Path $stage 'PCOptimizer.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions
$core = ($runtime.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
$desktop = ($runtime.includedFrameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App').version
$copies = @{
    'WinBlazer-LICENSE.txt'            = Join-Path $project 'LICENSE'
    'dotnet-LICENSE.txt'               = Join-Path $packages "microsoft.netcore.app.runtime.win-x64\$core\LICENSE.TXT"
    'dotnet-THIRD-PARTY-NOTICES.txt'   = Join-Path $packages "microsoft.netcore.app.runtime.win-x64\$core\THIRD-PARTY-NOTICES.TXT"
    'windows-desktop-LICENSE.txt'      = Join-Path $packages "microsoft.windowsdesktop.app.runtime.win-x64\$desktop\LICENSE"
    'microsoft-dotnet-library-LICENSE.txt' = Join-Path (Split-Path $dotnet) 'LICENSE.txt'
    'Apache-2.0.txt'                   = Join-Path $project 'installer\licenses\Apache-2.0.txt'
}
foreach ($name in $copies.Keys) { Copy-Item -LiteralPath $copies[$name] -Destination (Join-Path $licenses $name) }
Copy-Item -LiteralPath (Join-Path $project 'installer\THIRD-PARTY.txt') -Destination $licenses

# Stop if anything personal is in the files, as plain bytes or as UTF-16.
$terms = @($env:USERNAME, $env:COMPUTERNAME) + $Private | Where-Object { $_ }
$found = foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    $views = @([Text.Encoding]::Latin1.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes))
    if ($bytes.Length -gt 1) { $views += [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1) }
    foreach ($term in $terms) {
        if ($views.Where({ $_.IndexOf([string]$term, [StringComparison]::OrdinalIgnoreCase) -ge 0 }, 'First').Count) {
            # The license names its author on purpose.
            if ($file.Name -ne 'WinBlazer-LICENSE.txt') { $file.FullName.Substring($stage.Length + 1) }
        }
    }
}
if ($found) { throw "Personal details were found, so no installer was made: $(($found | Sort-Object -Unique) -join ', ')" }

& $compiler /Qp "/DVersion=$Version" "/DSource=$stage" "/DOutput=$output" (Join-Path $project 'installer\WinBlazer.iss')
if ($LASTEXITCODE -ne 0) { throw 'The installer could not be built.' }

$setup = Join-Path $output "WinBlazer-$Version-beta-Setup.exe"
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$setup.sha256" -Value "$hash  $(Split-Path $setup -Leaf)" -Encoding ascii
Write-Host "Installer: $setup"
Write-Host "SHA-256:   $hash"

& (Join-Path $PSScriptRoot 'Make-Portable.ps1') -Version $Version
