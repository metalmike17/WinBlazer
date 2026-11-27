<#
.SYNOPSIS
  Makes the portable ZIP from the files Build-Release.ps1 prepared and checked. Publishes nothing.
#>
param([string]$Version = '0.1.0')
$env:PSModulePath = "$PSHOME\Modules"
$ErrorActionPreference = 'Stop'

$project = Split-Path $PSScriptRoot -Parent
$output = Join-Path $project 'artifacts\release'
$stage = Join-Path $output 'app'
if (-not (Test-Path -LiteralPath (Join-Path $stage 'PCOptimizer.exe'))) { throw 'Run Build-Release.ps1 first.' }

$name = "WinBlazer-$Version-beta-portable"
$zip = Join-Path $output "$name.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    # Everything goes in one folder, so extracting does not scatter files.
    foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {
        $entry = "$name/" + $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry) | Out-Null
    }
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,
        (Join-Path $project 'installer\PORTABLE-README.txt'), "$name/READ ME FIRST.txt") | Out-Null
}
finally { $archive.Dispose() }

$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $name.zip" -Encoding ascii
Write-Host "Portable:  $zip"
Write-Host "SHA-256:   $hash"
