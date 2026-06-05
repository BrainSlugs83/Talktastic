#requires -Version 7
<#
.SYNOPSIS
Fast staleness check for a built/published artifact.

.DESCRIPTION
Compares a sentinel "stamp" file's timestamp against the last-write time of
every file the working tree considers "live" -- tracked files plus
untracked-but-not-ignored ones. If any file is newer than the stamp, the
artifact is stale.

If the repo is not a git checkout, or the stamp file is missing, returns $true.

.PARAMETER StampPath
Absolute path to the sentinel file written after a successful publish.

.PARAMETER RepoRoot
Absolute path to the repository root.

.OUTPUTS
[bool] $true if the caller should rebuild, $false if it can skip.
#>
function Test-NeedsBuild
{
	[CmdletBinding()]
	[OutputType([bool])]
	param(
		[Parameter(Mandatory)][string]$StampPath,
		[Parameter(Mandatory)][string]$RepoRoot
	)

	if (-not (Test-Path -LiteralPath $StampPath))
	{
		return $true
	}

	$stampTime = (Get-Item -LiteralPath $StampPath).LastWriteTimeUtc

	$null = & git -C $RepoRoot rev-parse --show-toplevel 2>$null
	if ($LASTEXITCODE -ne 0)
	{
		return $true
	}

	$files = & git -C $RepoRoot -c core.quotePath=false ls-files -co --exclude-standard 2>$null
	if ($LASTEXITCODE -ne 0)
	{
		return $true
	}

	foreach ($rel in $files)
	{
		if ([string]::IsNullOrWhiteSpace($rel))
		{
			continue
		}
		$full = Join-Path $RepoRoot $rel
		if (-not (Test-Path -LiteralPath $full))
		{
			continue
		}
		$mtime = [IO.File]::GetLastWriteTimeUtc($full)
		if ($mtime -gt $stampTime)
		{
			return $true
		}
	}

	return $false
}
