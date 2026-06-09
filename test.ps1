#Requires -Version 7.0
<#
.SYNOPSIS
	Runs all Talktastic tests with code coverage and reports results.
.PARAMETER Html
	Generate and open an HTML coverage report.
.PARAMETER Threshold
	Minimum line coverage percentage to pass (default: 80; values below 80 still use 80).
.PARAMETER Stt
	Also run the Whisper speech-to-text integration tests against dist\say.exe.
	If dist\say.exe is missing or older than any source file, build.bat is invoked first.
	A conda env is auto-created on first run at $env:STT_ENV
	(default: $env:LOCALAPPDATA\talktastic\sttenv) and faster-whisper / soundcard / numpy
	are installed into it. Requires conda on PATH and local audio hardware. Plays audio aloud.
#>
[CmdletBinding()]
param(
	[switch]$Html,
	[double]$Threshold = 0,
	[switch]$Stt
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$testProject = Join-Path $root 'Tests'
$resultsDir = Join-Path $testProject 'TestResults'
$coverageDir = Join-Path $resultsDir 'coverage'

# -- Banner
Write-Host ''
Write-Host '  ============================================' -ForegroundColor Cyan
Write-Host '   Talktastic Test Suite' -ForegroundColor Cyan
Write-Host '  ============================================' -ForegroundColor Cyan
Write-Host ''

# -- Clean previous results
if (Test-Path $resultsDir)
{
	Remove-Item $resultsDir -Recurse -Force
}

# -- Run tests with coverage
Write-Host '  Running tests...' -ForegroundColor DarkGray
Write-Host ''

$testArgs = @(
	'test', $testProject, '--nologo',
	"--settings:$testProject\.runsettings",
	'--collect:XPlat Code Coverage',
	"--results-directory:$resultsDir",
	'--logger:trx;LogFileName=results.trx',
	'--logger:console;verbosity=minimal'
)

& dotnet @testArgs 2>&1 | ForEach-Object { Write-Host "  $_" }
$testExit = $LASTEXITCODE
Write-Host ''

# -- Parse TRX results
$trxFile = Get-ChildItem $resultsDir -Filter 'results.trx' -Recurse -ErrorAction SilentlyContinue |
	Select-Object -First 1

$passed = 0; $failed = 0; $skipped = 0; $total = 0

if ($trxFile)
{
	[xml]$trx = Get-Content $trxFile.FullName
	$counters = $trx.TestRun.ResultSummary.Counters
	$passed = [int]$counters.passed
	$failed = [int]$counters.failed
	$skipped = [int]$counters.notExecuted
	$total = [int]$counters.total
}

$passPct = if ($total -gt 0) { [math]::Round(($passed / $total) * 100, 1) } else { 0 }

$testColor = if ($failed -gt 0) { 'Red' } else { 'Green' }

Write-Host '  ============================================' -ForegroundColor Cyan
Write-Host '   Test Results' -ForegroundColor Cyan
Write-Host '  ============================================' -ForegroundColor Cyan
Write-Host "   Total:    $total"
Write-Host "   Passed:   $passed" -ForegroundColor Green
if ($failed -gt 0) { Write-Host "   Failed:   $failed" -ForegroundColor Red }
else { Write-Host "   Failed:   $failed" }
if ($skipped -gt 0) { Write-Host "   Skipped:  $skipped" -ForegroundColor Yellow }
else { Write-Host "   Skipped:  $skipped" }
Write-Host "   Pass %:   $passPct%" -ForegroundColor $testColor
Write-Host '  ============================================' -ForegroundColor Cyan
Write-Host ''

# -- Parse coverage
$coberturaFile = Get-ChildItem $resultsDir -Filter 'coverage.cobertura.xml' -Recurse -ErrorAction SilentlyContinue |
	Select-Object -First 1

$lineCov = $null; $branchCov = $null

if ($coberturaFile)
{
	[xml]$cob = Get-Content $coberturaFile.FullName
	$lineCov = [math]::Round([double]$cob.coverage.'line-rate' * 100, 1)
	$branchCov = [math]::Round([double]$cob.coverage.'branch-rate' * 100, 1)

	$covColor = if ($lineCov -ge 80) { 'Green' } elseif ($lineCov -ge 50) { 'Yellow' } else { 'Red' }

	Write-Host '  ============================================' -ForegroundColor Cyan
	Write-Host '   Code Coverage' -ForegroundColor Cyan
	Write-Host '  ============================================' -ForegroundColor Cyan
	Write-Host "   Line:     $lineCov%" -ForegroundColor $covColor
	Write-Host "   Branch:   $branchCov%"
	Write-Host '  ============================================' -ForegroundColor Cyan
	Write-Host ''

	# -- HTML report
	if ($Html)
	{
		Write-Host '  Generating HTML coverage report...' -ForegroundColor DarkGray
		& reportgenerator `
			"-reports:$($coberturaFile.FullName)" `
			"-targetdir:$coverageDir" `
			'-reporttypes:Html' `
			'-verbosity:Warning'

		if ($LASTEXITCODE -eq 0)
		{
			$htmlPath = Join-Path $coverageDir 'index.html'
			Write-Host "  Report: $htmlPath" -ForegroundColor DarkGray
			Start-Process $htmlPath
		}
		else
		{
			Write-Host '  [!] reportgenerator failed.' -ForegroundColor Red
			Write-Host '      Install: dotnet tool install -g dotnet-reportgenerator-globaltool' -ForegroundColor DarkGray
		}
		Write-Host ''
	}
}
else
{
	Write-Host '  [!] No coverage data found.' -ForegroundColor Yellow
	Write-Host ''
}

# -- Static analysis summary
Write-Host '  ============================================' -ForegroundColor Cyan
Write-Host '   Static Analysis' -ForegroundColor Cyan
Write-Host '  ============================================' -ForegroundColor Cyan
Write-Host '   Roslyn AnalysisLevel=latest-all'
Write-Host '   TreatWarningsAsErrors=true'
Write-Host '   CA1502 (cyclomatic complexity) = error'
Write-Host '   Build success = zero analyzer violations' -ForegroundColor Green
Write-Host '  ============================================' -ForegroundColor Cyan
Write-Host ''

# -- Whisper STT integration tests (opt-in: -Stt)
$sttExit = 0

if ($Stt)
{
	Write-Host '  ============================================' -ForegroundColor Cyan
	Write-Host '   Whisper STT Integration' -ForegroundColor Cyan
	Write-Host '  ============================================' -ForegroundColor Cyan

	$sayExe = Join-Path $root 'dist\say.exe'
	$sttScript = Join-Path $root 'scripts\stt-integration.py'
	$defaultSttEnv = if ($env:LOCALAPPDATA)
	{
		Join-Path $env:LOCALAPPDATA 'talktastic\sttenv'
	}
	else
	{
		Join-Path $env:TEMP 'talktastic-sttenv'
	}
	$sttEnv = if ($env:STT_ENV) { $env:STT_ENV } else { $defaultSttEnv }
	$envPython = Join-Path $sttEnv 'python.exe'

	# Auto-build dist\say.exe if it is missing -- test.bat owns the toolchain.
	if (-not (Test-Path $sayExe))
	{
		Write-Host "  $sayExe not found; running build.bat..." -ForegroundColor DarkGray
		$buildBat = Join-Path $root 'build.bat'
		& $buildBat | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
		if ($LASTEXITCODE -ne 0)
		{
			Write-Host "  [FAIL] build.bat failed (exit code $LASTEXITCODE)." -ForegroundColor Red
			$sttExit = 1
		}
	}

	if ($sttExit -eq 0)
	{
		$conda = (Get-Command conda -ErrorAction SilentlyContinue).Source

		if (-not (Test-Path $envPython))
		{
			if (-not $conda)
			{
				Write-Host '  [FAIL] conda not found on PATH and STT env missing.' -ForegroundColor Red
				Write-Host '         Install conda (or set $env:STT_ENV to point at an existing Python 3.11+ env).' -ForegroundColor DarkGray
				$sttExit = 1
			}
			else
			{
				$envParent = Split-Path $sttEnv -Parent
				if ($envParent -and -not (Test-Path $envParent))
				{
					New-Item -ItemType Directory -Path $envParent -Force | Out-Null
				}

				Write-Host "  Creating conda env at $sttEnv (python 3.11)..." -ForegroundColor DarkGray
				& $conda create -y -p $sttEnv python=3.11 *>&1 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
				if (-not (Test-Path $envPython))
				{
					Write-Host "  [FAIL] conda create did not produce $envPython." -ForegroundColor Red
					$sttExit = 1
				}
			}
		}

		if ($sttExit -eq 0)
		{
			# Install any missing python deps into the env.
			& $envPython -c 'import faster_whisper, soundcard, numpy' 2>$null
			if ($LASTEXITCODE -ne 0)
			{
				Write-Host '  Installing missing deps (faster-whisper soundcard numpy)...' -ForegroundColor DarkGray
				& $envPython -m pip install --quiet faster-whisper soundcard numpy |
					ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }

				# Re-verify after install so we fail fast if pip did not actually deliver.
				& $envPython -c 'import faster_whisper, soundcard, numpy' 2>$null
				if ($LASTEXITCODE -ne 0)
				{
					Write-Host '  [FAIL] STT deps still missing after pip install.' -ForegroundColor Red
					$sttExit = 1
				}
			}
		}

		if ($sttExit -eq 0)
		{
			Write-Host '  Running STT integration (plays audio aloud)...' -ForegroundColor DarkGray
			Write-Host ''
			& $envPython $sttScript 2>&1 | ForEach-Object { Write-Host "  $_" }
			$sttExit = $LASTEXITCODE
		}
	}

	Write-Host ''
	if ($sttExit -eq 0)
	{
		Write-Host '  [PASS] STT integration tests passed.' -ForegroundColor Green
	}
	else
	{
		Write-Host "  [FAIL] STT integration tests failed (exit code $sttExit)." -ForegroundColor Red
	}
	Write-Host '  ============================================' -ForegroundColor Cyan
	Write-Host ''
}

# -- Final verdict
$exitCode = 0
$lineThreshold = [math]::Max(80.0, $Threshold)
$branchThreshold = 80.0
$coverageFailures = @()

if ($lineCov -ne $null -and $lineCov -lt $lineThreshold)
{
	$coverageFailures += "line $lineCov% < $lineThreshold%"
}

if ($branchCov -ne $null -and $branchCov -lt $branchThreshold)
{
	$coverageFailures += "branch $branchCov% < $branchThreshold%"
}

if ($testExit -ne 0)
{
	Write-Host "  [FAIL] Tests failed (exit code $testExit)." -ForegroundColor Red
	$exitCode = $testExit
}
elseif ($sttExit -ne 0)
{
	Write-Host "  [FAIL] STT integration failed (exit code $sttExit)." -ForegroundColor Red
	$exitCode = $sttExit
}
elseif ($coverageFailures.Count -gt 0)
{
	Write-Host "  [FAIL] Coverage below threshold: $($coverageFailures -join ' / ')." -ForegroundColor Red
	$exitCode = 1
}
else
{
	Write-Host "  [PASS] All $total tests passed." -ForegroundColor Green
}

Write-Host ''
exit $exitCode
