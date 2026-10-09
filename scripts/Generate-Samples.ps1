#requires -Version 7
<#
.SYNOPSIS
Regenerates the README / GitHub Pages audio samples in Samples\.

.DESCRIPTION
Runs each README usage example through dist\say.exe with -o, then converts the
resulting WAV to MP3 with ffmpeg. The RVC file-conversion example uses
dist\before.wav as its input (provide your own recording there).

.PARAMETER FFmpeg
Path to ffmpeg.exe. Defaults to whatever is on PATH, then C:\Utils\Media\ffmpeg\bin.
#>
[CmdletBinding()]
param(
	[string]$FFmpeg
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$say = Join-Path $root 'dist\say.exe'
$samples = Join-Path $root 'Samples'
$work = Join-Path ([IO.Path]::GetTempPath()) "talktastic-samples-$([guid]::NewGuid().ToString('N'))"

if (-not $FFmpeg)
{
	$FFmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue)?.Source ?? 'C:\Utils\Media\ffmpeg\bin\ffmpeg.exe'
}

foreach ($required in $say, $FFmpeg)
{
	if (-not (Test-Path $required))
	{
		throw "Required file not found: $required"
	}
}

New-Item -ItemType Directory -Force -Path $samples, $work | Out-Null

function ConvertTo-Mp3([string]$Wav, [string]$Name)
{
	& $FFmpeg -hide_banner -loglevel error -y -i $Wav -codec:a libmp3lame -q:a 2 (Join-Path $samples "$Name.mp3")
	if ($LASTEXITCODE -ne 0)
	{
		throw "ffmpeg failed converting $Wav"
	}
}

function Invoke-Sample([string]$Name, [string[]]$SayArgs, [string]$StdIn)
{
	$wav = Join-Path $work "$Name.wav"
	Write-Host "  $Name" -ForegroundColor Cyan
	if ($StdIn)
	{
		$StdIn | & $say @SayArgs -o $wav
	}
	else
	{
		& $say @SayArgs -o $wav
	}

	if ($LASTEXITCODE -ne 0 -or -not (Test-Path $wav))
	{
		throw "say.exe failed for sample '$Name'"
	}

	ConvertTo-Mp3 $wav $Name
}

try
{
	Write-Host 'Generating samples...'

	Invoke-Sample '01-default-voice' @('Hello, from Talktastic!')
	Invoke-Sample '02-neural-ryan' @('This is Microsoft Ryan, a Windows 11 neural voice.', '-v', 'Microsoft Ryan')
	Invoke-Sample '03-piper-url' @('Piper voices can be downloaded by URL!', '-v', 'https://huggingface.co/rhasspy/piper-voices/tree/v1.0.0/en/en_US/amy/medium')
	Invoke-Sample '04-piper-by-name' @('Once a Piper voice is installed, you can use it by name!', '-v', 'Amy')

	# Download-only example (no audio); needed so piper:Ryan below resolves.
	& $say -v 'https://huggingface.co/rhasspy/piper-voices/tree/v1.0.0/en/en_US/ryan/high'

	Invoke-Sample '05-piper-prefix' @('This is how you disambiguate between two voices with the same name.', '-v', 'piper:Ryan')
	Invoke-Sample '06-rvc-url' @('RVC models can also be downloaded by URL.', '--rvc', 'https://huggingface.co/0x3e9/Darth_Vader_RVC')
	Invoke-Sample '07-piper-plus-rvc' @("This is Piper Alan's voice, converted to sound like Darth Vader.", '-v', 'https://huggingface.co/rhasspy/piper-voices/tree/v1.0.0/en/en_GB/alan/medium', '--rvc', 'Vader')
	Invoke-Sample '08-stdin' @('-') 'You can also pipe text in from stdin!'

	$before = Join-Path $root 'dist\before.wav'
	if (Test-Path $before)
	{
		ConvertTo-Mp3 $before '09-rvc-file-before'
		Invoke-Sample '09-rvc-file-after' @('-i', $before, '--rvc', 'https://huggingface.co/0x3e9/Darth_Vader_RVC')
	}
	else
	{
		Write-Warning "Skipping RVC file sample: $before not found."
	}

	Get-ChildItem $samples -Filter *.mp3 | Format-Table Name, Length -AutoSize
}
finally
{
	Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
