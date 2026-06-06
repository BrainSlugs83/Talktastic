#Requires -Version 7.0
<#
.SYNOPSIS
	Runs all Talktastic tests with code coverage and reports results.
.PARAMETER Html
	Generate and open an HTML coverage report.
.PARAMETER Threshold
	Minimum line coverage percentage to pass (default: 0 = no gate).
#>
[CmdletBinding()]
param(
	[switch]$Html,
	[double]$Threshold = 0
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

# -- Final verdict
$exitCode = 0

if ($testExit -ne 0)
{
	Write-Host "  [FAIL] Tests failed (exit code $testExit)." -ForegroundColor Red
	$exitCode = $testExit
}
elseif ($Threshold -gt 0 -and $lineCov -ne $null -and $lineCov -lt $Threshold)
{
	Write-Host "  [FAIL] Line coverage $lineCov% is below threshold $Threshold%." -ForegroundColor Red
	$exitCode = 1
}
else
{
	Write-Host "  [PASS] All $total tests passed." -ForegroundColor Green
}

Write-Host ''
exit $exitCode
