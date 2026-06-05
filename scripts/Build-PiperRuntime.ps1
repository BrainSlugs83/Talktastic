#requires -Version 7
<#
.SYNOPSIS
Creates a piper-runtime.zip with English-only espeak-ng data for embedding.

.DESCRIPTION
Takes the piper Windows distribution and creates a minimal zip containing:
  - piper.exe, espeak-ng.dll, piper_phonemize.dll, onnxruntime.dll
  - English-only espeak-ng-data (en_dict, phoneme data, English lang files, voices)
Uses maximum compression (-9 equivalent via .NET CompressionLevel.SmallestSize).
#>
[CmdletBinding()]
param(
	[string]$PiperSourceDir = "C:\projects.temp\Copilot\piper-test\piper",
	[string]$OutputPath = (Join-Path $PSScriptRoot "native-resources\piper-runtime.zip")
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path "$PiperSourceDir\piper.exe"))
{
	Write-Error "Piper source not found at '$PiperSourceDir'. Download piper_windows_amd64.zip first."
	return
}

# Create output directory
$outDir = Split-Path $OutputPath -Parent
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# Remove existing zip
if (Test-Path $OutputPath) { Remove-Item $OutputPath -Force }

# Build the zip with max compression
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zipStream = [System.IO.File]::Create($OutputPath)
$zip = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)

function Add-FileToZip([string]$sourcePath, [string]$entryName)
{
	$entry = $zip.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::SmallestSize)
	$entryStream = $entry.Open()
	$fileStream = [System.IO.File]::OpenRead($sourcePath)
	$fileStream.CopyTo($entryStream)
	$fileStream.Close()
	$entryStream.Close()
	Write-Verbose "  Added: $entryName"
}

# Core binaries
$binaries = @("piper.exe", "espeak-ng.dll", "piper_phonemize.dll", "onnxruntime.dll")
foreach ($bin in $binaries)
{
	$src = Join-Path $PiperSourceDir $bin
	if (Test-Path $src) { Add-FileToZip $src $bin }
	else { Write-Warning "Missing: $bin" }
}

# espeak-ng-data: core phoneme files (required for all languages)
$coreFiles = @("phondata", "phondata-manifest", "phonindex", "phontab", "intonations")
foreach ($f in $coreFiles)
{
	Add-FileToZip (Join-Path $PiperSourceDir "espeak-ng-data\$f") "espeak-ng-data\$f"
}

# espeak-ng-data: English dictionary only
Add-FileToZip (Join-Path $PiperSourceDir "espeak-ng-data\en_dict") "espeak-ng-data\en_dict"

# espeak-ng-data: English language definitions
$langDir = Join-Path $PiperSourceDir "espeak-ng-data\lang\gmw"
Get-ChildItem $langDir -File | Where-Object { $_.Name -match '^en' } | ForEach-Object {
	Add-FileToZip $_.FullName "espeak-ng-data/lang/gmw/$($_.Name)"
}

# espeak-ng-data: all voice definitions (they're tiny)
$voiceDir = Join-Path $PiperSourceDir "espeak-ng-data\voices\!v"
Get-ChildItem $voiceDir -File | ForEach-Object {
	Add-FileToZip $_.FullName "espeak-ng-data/voices/!v/$($_.Name)"
}

$zip.Dispose()
$zipStream.Close()

$zipSize = [math]::Round((Get-Item $OutputPath).Length / 1MB, 2)
Write-Host "✅ Created piper-runtime.zip ($zipSize MB)" -ForegroundColor Green
