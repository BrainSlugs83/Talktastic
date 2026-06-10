#requires -Version 7
<#
.SYNOPSIS
Unconditionally rebuild say.exe into dist\.

.DESCRIPTION
Thin wrapper -- calls build.ps1 -Force to skip the stamp check
and always republish.
#>
& (Join-Path $PSScriptRoot 'build.ps1') -Force
exit $LASTEXITCODE
