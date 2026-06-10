param
(
	[Parameter(Mandatory = $true)]
	[string]$OutputDirectory,

	[Parameter(Mandatory = $true)]
	[string]$BasePackageDirectory,

	[Parameter(Mandatory = $true)]
	[string]$EmbeddedTtsPackageDirectory,

	[Parameter(Mandatory = $true)]
	[string]$OnnxRuntimePackageDirectory,

	[Parameter(Mandatory = $true)]
	[string]$LamePackageDirectory,

	[Parameter(Mandatory = $true)]
	[string]$SherpaOnnxPackageDirectory,

	[Parameter(Mandatory = $true)]
	[string]$DmlOnnxRuntimePackageDirectory,

	[Parameter(Mandatory = $true)]
	[string]$DirectMlPackageDirectory,

	[string]$RuntimeIdentifier = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Compress-Brotli
{
	param
	(
		[Parameter(Mandatory = $true)]
		[string]$SourcePath,

		[Parameter(Mandatory = $true)]
		[string]$TargetPath
	)

	if (Test-Path -LiteralPath $TargetPath)
	{
		return
	}

	$compressionLevel = [System.IO.Compression.CompressionLevel]::SmallestSize
	$sourceStream = [System.IO.File]::OpenRead($SourcePath)

	try
	{
		$targetStream = [System.IO.File]::Create($TargetPath)

		try
		{
			$brotliStream = New-Object System.IO.Compression.BrotliStream($targetStream, $compressionLevel)

			try
			{
				$sourceStream.CopyTo($brotliStream)
			}
			finally
			{
				$brotliStream.Dispose()
			}
		}
		finally
		{
			$targetStream.Dispose()
		}
	}
	finally
	{
		$sourceStream.Dispose()
	}
}

function Get-Md5Hex
{
	param
	(
		[Parameter(Mandatory = $true)]
		[string]$Path
	)

	$stream = [System.IO.File]::OpenRead($Path)
	$md5 = [System.Security.Cryptography.MD5]::Create()

	try
	{
		$hashBytes = $md5.ComputeHash($stream)
		return [System.BitConverter]::ToString($hashBytes).Replace('-', [string]::Empty)
	}
	finally
	{
		$md5.Dispose()
		$stream.Dispose()
	}
}

function Remove-FileWithRetry
{
	param
	(
		[Parameter(Mandatory = $true)]
		[string]$Path,

		[int]$MaxAttempts = 20,

		[int]$DelayMilliseconds = 250
	)

	for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++)
	{
		try
		{
			if (Test-Path -LiteralPath $Path)
			{
				Remove-Item -LiteralPath $Path -Force
			}

			return
		}
		catch [System.IO.IOException]
		{
			if ($attempt -eq $MaxAttempts)
			{
				throw
			}

			Start-Sleep -Milliseconds $DelayMilliseconds
		}
	}
}

$nativeDlls = @(
	@{
		Name = 'Microsoft.CognitiveServices.Speech.core.dll'
		SourcePath = Join-Path $BasePackageDirectory "runtimes\$RuntimeIdentifier\native\Microsoft.CognitiveServices.Speech.core.dll"
	}
	@{
		Name = 'Microsoft.CognitiveServices.Speech.extension.audio.sys.dll'
		SourcePath = Join-Path $BasePackageDirectory "runtimes\$RuntimeIdentifier\native\Microsoft.CognitiveServices.Speech.extension.audio.sys.dll"
	}
	@{
		Name = 'Microsoft.CognitiveServices.Speech.extension.embedded.tts.dll'
		SourcePath = Join-Path $EmbeddedTtsPackageDirectory "runtimes\$RuntimeIdentifier\native\Microsoft.CognitiveServices.Speech.extension.embedded.tts.dll"
	}
	@{
		Name = 'Microsoft.CognitiveServices.Speech.extension.onnxruntime.dll'
		SourcePath = Join-Path $OnnxRuntimePackageDirectory "runtimes\$RuntimeIdentifier\native\Microsoft.CognitiveServices.Speech.extension.onnxruntime.dll"
	}
	@{
		# NAudio.Lame ships the DLL as build/libmp3lame.64.dll; we embed it as libmp3lame.dll
		Name = 'libmp3lame.dll'
		SourcePath = Join-Path $LamePackageDirectory 'build\libmp3lame.64.dll'
	}
	@{
		# DirectML-enabled ORT (includes CPU fallback)
		Name = 'onnxruntime.dll'
		SourcePath = Join-Path $DmlOnnxRuntimePackageDirectory "runtimes\$RuntimeIdentifier\native\onnxruntime.dll"
	}
	@{
		Name = 'onnxruntime_providers_shared.dll'
		SourcePath = Join-Path $DmlOnnxRuntimePackageDirectory "runtimes\$RuntimeIdentifier\native\onnxruntime_providers_shared.dll"
	}
	@{
		Name = 'DirectML.dll'
		SourcePath = Join-Path $DirectMlPackageDirectory 'bin\x64-win\DirectML.dll'
	}
	@{
		Name = 'sherpa-onnx-c-api.dll'
		SourcePath = Join-Path $SherpaOnnxPackageDirectory "runtimes\$RuntimeIdentifier\native\sherpa-onnx-c-api.dll"
	}
)

$mutex = [System.Threading.Mutex]::new($false, 'Local\Talktastic.BuildResources')
$mutexAcquired = $false

try
{
	try
	{
		$mutexAcquired = $mutex.WaitOne([TimeSpan]::FromMinutes(2))
	}
	catch [System.Threading.AbandonedMutexException]
	{
		$mutexAcquired = $true
	}

	if (-not $mutexAcquired)
	{
		throw 'Timed out waiting for native resource generation lock.'
	}

	[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null


	$manifest = foreach ($nativeDll in $nativeDlls)
	{
		$sourcePath = $nativeDll.SourcePath

		if (-not (Test-Path -LiteralPath $sourcePath))
		{
			throw "Missing native DLL: $sourcePath"
		}

		$targetPath = Join-Path $OutputDirectory "$($nativeDll.Name).br"
		Compress-Brotli -SourcePath $sourcePath -TargetPath $targetPath

		[pscustomobject]@{
			Name = $nativeDll.Name
			Size = ([System.IO.FileInfo]::new($sourcePath)).Length
			Md5 = (Get-Md5Hex -Path $sourcePath).ToUpperInvariant()
		}
	}

	$manifestJson = $manifest | Sort-Object Name | ConvertTo-Json -Depth 3
	$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
	[System.IO.File]::WriteAllText((Join-Path $OutputDirectory 'manifest.json'), $manifestJson, $utf8NoBom)
}
finally
{
	if ($mutexAcquired)
	{
		$mutex.ReleaseMutex()
	}

	$mutex.Dispose()
}

