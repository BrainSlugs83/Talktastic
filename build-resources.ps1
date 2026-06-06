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

	[string]$RuntimeIdentifier = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Compress-Gzip
{
	param
	(
		[Parameter(Mandatory = $true)]
		[string]$SourcePath,

		[Parameter(Mandatory = $true)]
		[string]$TargetPath
	)

	$compressionLevel = [System.IO.Compression.CompressionLevel]::Optimal

	if ([Enum]::GetNames([System.IO.Compression.CompressionLevel]) -contains 'SmallestSize')
	{
		$compressionLevel = [System.IO.Compression.CompressionLevel]::SmallestSize
	}

	$sourceStream = [System.IO.File]::OpenRead($SourcePath)

	try
	{
		$targetStream = [System.IO.File]::Create($TargetPath)

		try
		{
			$gzipStream = [System.IO.Compression.GZipStream]::new($targetStream, $compressionLevel, $false)

			try
			{
				$sourceStream.CopyTo($gzipStream)
			}
			finally
			{
				$gzipStream.Dispose()
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
		Name = 'sherpa-onnx-c-api.dll'
		SourcePath = Join-Path $SherpaOnnxPackageDirectory "runtimes\$RuntimeIdentifier\native\sherpa-onnx-c-api.dll"
	}
)

[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

Get-ChildItem -LiteralPath $OutputDirectory -File -ErrorAction SilentlyContinue |
	Where-Object {
		$_.Name -like '*.dll.gz' -or $_.Name -eq 'manifest.json'
	} |
	Remove-Item -Force

$manifest = foreach ($nativeDll in $nativeDlls)
{
	$sourcePath = $nativeDll.SourcePath

	if (-not (Test-Path -LiteralPath $sourcePath))
	{
		throw "Missing native DLL: $sourcePath"
	}

	$targetPath = Join-Path $OutputDirectory "$($nativeDll.Name).gz"
	Compress-Gzip -SourcePath $sourcePath -TargetPath $targetPath

	[pscustomobject]@{
		Name = $nativeDll.Name
		Size = ([System.IO.FileInfo]::new($sourcePath)).Length
		Md5 = (Get-Md5Hex -Path $sourcePath).ToUpperInvariant()
	}
}

$manifestJson = $manifest | Sort-Object Name | ConvertTo-Json -Depth 3
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText((Join-Path $OutputDirectory 'manifest.json'), $manifestJson, $utf8NoBom)
