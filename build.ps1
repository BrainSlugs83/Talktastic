#requires -Version 7
<#
.SYNOPSIS
Build (if needed) say.exe into dist\.

.DESCRIPTION
Smart-rebuild publisher. Compares a sentinel `.publish-stamp` file under
dist/ against the working tree (via Test-NeedsBuild) and only republishes
when something has actually changed. Produces a single NativeAOT-compiled
say.exe in the dist\ folder.

.PARAMETER Force
Republish unconditionally, ignoring the stamp.

.EXAMPLE
.\build.ps1
Build if source changed.

.EXAMPLE
.\build.ps1 -Force
Rebuild unconditionally.
#>
[CmdletBinding()]
param(
	[switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

. (Join-Path $root 'scripts\Test-NeedsBuild.ps1')

$rid = 'win-x64'
$distDir = Join-Path $root 'dist'
$stampPath = Join-Path $root '.publish-stamp'
$exePath = Join-Path $distDir 'say.exe'
$project = Join-Path $root 'Talktastic.csproj'
$publishDir = Join-Path $root "bin\Release\net10.0-windows10.0.19041.0\$rid\publish"

$needsBuild = $Force -or (Test-NeedsBuild -StampPath $stampPath -RepoRoot $root)

if (-not $needsBuild)
{
	Write-Host "✅ dist\say.exe is up-to-date" -ForegroundColor Green
	exit 0
}

Write-Host "🔨 Publishing say.exe ($rid, NativeAOT)..." -ForegroundColor Cyan

& dotnet publish $project `
	-c Release `
	-r $rid `
	/p:PublishAot=true `
	--nologo `
	-v q 2>&1 | Out-Host

if ($LASTEXITCODE -ne 0)
{
	Write-Host "❌ Publish failed (exit $LASTEXITCODE)." -ForegroundColor Red
	exit $LASTEXITCODE
}

New-Item -ItemType Directory -Path $distDir -Force | Out-Null
Copy-Item (Join-Path $publishDir 'say.exe') $exePath -Force

# Copy any native companion DLLs (e.g. onnxruntime.dll), skip DirectML (system-provided)
Get-ChildItem $publishDir -Filter '*.dll' |
	Where-Object { $_.Name -notlike 'DirectML*' } |
	ForEach-Object {
		Copy-Item $_.FullName (Join-Path $distDir $_.Name) -Force
	}

# Clean any stale DirectML DLLs from dist
Get-ChildItem $distDir -Filter 'DirectML*' -ErrorAction SilentlyContinue |
	Remove-Item -Force

# Touch the sentinel so subsequent builds can short-circuit.
[IO.File]::WriteAllText($stampPath, '')

$size = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
Write-Host "✅ dist\say.exe ($size MB)" -ForegroundColor Green
