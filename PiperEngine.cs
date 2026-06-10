using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Talktastic;

/// <summary>
/// Manages Piper TTS voice models and synthesis via sherpa-onnx.
/// Voice models live in a .piper-tts/voices directory (LOCALAPPDATA → TEMP → CWD).
/// Models are downloaded on demand from HuggingFace.
/// </summary>
static partial class PiperEngine
{
	private const string PiperDirName = ".piper-tts";
	private const string VoicesSubDir = "voices";
	private const string UrlMapFileName = "voices.json";

	private const string HuggingFaceBaseUrl =
		"https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0";

	// piper:en_US-ryan-high → lang=en, locale=en_US, name=ryan, quality=high
	[GeneratedRegex(@"^([a-z]{2})_([A-Z]{2})-(.+)-(\w+)$")]
	private static partial Regex ModelNamePattern();

	private static string? _resolvedPiperDir;

	// ── Location resolution ──

	/// <summary>
	/// Returns the Piper voices directory path, if it exists.
	/// </summary>
	internal static string? FindVoicesDir()
	{
		return AppPaths.FindExistingDir(Path.Combine(PiperDirName, VoicesSubDir));
	}

	/// <summary>
	/// Enumerates all cached Piper voices as (name, onnxPath) pairs.
	/// </summary>
	internal static IEnumerable<(string Name, string OnnxPath)> EnumerateCachedVoices(string voicesDir)
	{
		if (!Directory.Exists(voicesDir))
		{
			yield break;
		}

		foreach (var file in Directory.GetFiles(voicesDir, "*.onnx"))
		{
			var configPath = file + ".json";
			if (File.Exists(configPath))
			{
				yield return (Path.GetFileNameWithoutExtension(file), file);
			}
		}
	}


	/// <summary>
	/// Ensures the .piper-tts directory exists. Returns its path.
	/// </summary>
	public static string EnsurePiperDir()
	{
		if (_resolvedPiperDir is not null)
			return _resolvedPiperDir;

		// Check existing installations
		foreach (var basePath in AppPaths.SearchBases)
		{
			var candidate = Path.Combine(basePath, PiperDirName);
			if (Directory.Exists(Path.Combine(candidate, VoicesSubDir)))
			{
				_resolvedPiperDir = candidate;
				return candidate;
			}
		}

		// Not found -- create in default location
		var dir = Path.Combine(AppPaths.SearchBases[0], PiperDirName);
		Directory.CreateDirectory(Path.Combine(dir, VoicesSubDir));
		_resolvedPiperDir = dir;
		return dir;
	}

	// ── Public API ──

	/// <summary>
	/// Returns true if the voice query is an HTTP(S) URL.
	/// </summary>
	public static bool IsUrlVoice(string voiceQuery)
	{
		return voiceQuery.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
			|| voiceQuery.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Extracts the model name from a voice query.
	/// </summary>
	public static string GetModelName(string voiceQuery)
	{
		if (IsUrlVoice(voiceQuery))
			return GetModelNameFromUrl(voiceQuery);

		return voiceQuery["piper:".Length..];
	}

	/// <summary>
	/// Derives a cache-friendly model name from a URL.
	/// For direct .onnx URLs: extract filename.
	/// For folder/release URLs: uses a placeholder until resolved.
	/// </summary>
	internal static string GetModelNameFromUrl(string url)
	{
		var uri = new Uri(url);
		var filename = Path.GetFileName(uri.LocalPath);

		// Direct .onnx file link
		if (filename.EndsWith(".onnx.json", StringComparison.OrdinalIgnoreCase))
			return filename[..^".onnx.json".Length];
		if (filename.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
			return filename[..^".onnx".Length];

		// Folder URL -- model name will be resolved at download time
		// Use last path segments as a temporary name
		var segments = uri.LocalPath.Trim('/').Split('/');
		return string.Join('-', segments[^Math.Min(2, segments.Length)..]);
	}

	/// <summary>
	/// Ensures the voice model is downloaded and returns the path to the .onnx file.
	/// Supports: "piper:en_US-ryan-high", direct .onnx URLs, HuggingFace folder URLs,
	/// and GitHub release URLs. Uses a local voices.json URL map to avoid re-downloading.
	/// </summary>
	[ExcludeFromCodeCoverage]
	public static async Task<string> EnsureVoiceModelAsync
	(
		string voiceQuery,
		CancellationToken cancellationToken
	)
	{
		var piperDir = EnsurePiperDir();
		var voicesDir = Path.Combine(piperDir, VoicesSubDir);
		Directory.CreateDirectory(voicesDir);

		// For URL voices, check the URL map first
		if (IsUrlVoice(voiceQuery))
		{
			var cached = LookupUrlMap(piperDir, voiceQuery);
			if (cached is not null)
				return cached;
		}

		using var http = new HttpClient();
		http.DefaultRequestHeaders.Add("User-Agent", "Talktastic");

		// Archive URL -- download, extract, find .onnx + .onnx.json
		if (IsUrlVoice(voiceQuery) && ArchiveExtractor.IsArchive(voiceQuery))
		{
			return await DownloadAndExtractArchiveVoiceAsync
			(
				http, voiceQuery, voicesDir, piperDir, cancellationToken
			).ConfigureAwait(false);
		}

		// Resolve the voice query to concrete download URLs + config URL
		if (IsUrlVoice(voiceQuery))
		{
			var resolved = await ModelDownloader.ResolveModelUrlAsync(http, voiceQuery, cancellationToken).ConfigureAwait(false);

			// If the resolved URL is an archive, download + extract
			if (resolved.IsArchive)
			{
				return await DownloadAndExtractArchiveVoiceAsync
				(
					http, resolved.FileUrl, voicesDir, piperDir, cancellationToken
				).ConfigureAwait(false);
			}

			var modelPath = Path.Combine(voicesDir, $"{resolved.ModelName}.onnx");
			var configPath = modelPath + ".json";

			var existing = EnumerateCachedVoices(voicesDir)
				.FirstOrDefault(v => v.Name.Equals(resolved.ModelName, StringComparison.OrdinalIgnoreCase));

			if (existing.OnnxPath is not null)
			{
				WriteUrlMapEntry(piperDir, voiceQuery, resolved.ModelName);
				return existing.OnnxPath;
			}

			await Console.Error.WriteLineAsync
			(
				$"Downloading Piper voice '{resolved.ModelName}'..."
			).ConfigureAwait(false);

			await DownloadFileAsync(http, resolved.FileUrl, modelPath, cancellationToken).ConfigureAwait(false);

			// Download companion files (typically .onnx.json config)
			if (resolved.CompanionUrls is not null)
			{
				foreach (var companionUrl in resolved.CompanionUrls)
				{
					var companionName = Uri.UnescapeDataString
					(
						Path.GetFileName(new Uri(companionUrl).LocalPath)
					);
					var companionPath = Path.Combine(voicesDir, companionName);
					if (!File.Exists(companionPath))
					{
						try
						{
							await DownloadFileAsync(http, companionUrl, companionPath, cancellationToken).ConfigureAwait(false);
						}
						catch (HttpRequestException)
						{
							// Config file is optional -- some models don't have one
						}
					}
				}
			}

			var sizeMb = new FileInfo(modelPath).Length / 1024 / 1024;
			await Console.Error.WriteLineAsync
			(
				$"Downloaded {resolved.ModelName} ({sizeMb} MB)."
			).ConfigureAwait(false);

			WriteUrlMapEntry(piperDir, voiceQuery, resolved.ModelName);
			return modelPath;
		}

		// Piper shorthand (piper:en_US-ryan-high)
		var shorthand = ResolvePiperShorthand(voiceQuery);

		var shorthandModelPath = Path.Combine(voicesDir, $"{shorthand.ModelName}.onnx");
		var shorthandConfigPath = shorthandModelPath + ".json";

		var existingShorthand = EnumerateCachedVoices(voicesDir)
			.FirstOrDefault(v => v.Name.Equals(shorthand.ModelName, StringComparison.OrdinalIgnoreCase));

		if (existingShorthand.OnnxPath is not null)
			return existingShorthand.OnnxPath;

		await Console.Error.WriteLineAsync
		(
			$"Downloading Piper voice '{shorthand.ModelName}'..."
		).ConfigureAwait(false);

		await DownloadFileAsync(http, shorthand.OnnxUrl, shorthandModelPath, cancellationToken).ConfigureAwait(false);
		await DownloadFileAsync(http, shorthand.ConfigUrl, shorthandConfigPath, cancellationToken).ConfigureAwait(false);

		var shorthandSizeMb = new FileInfo(shorthandModelPath).Length / 1024 / 1024;
		await Console.Error.WriteLineAsync
		(
			$"Downloaded {shorthand.ModelName} ({shorthandSizeMb} MB)."
		).ConfigureAwait(false);

		return shorthandModelPath;
	}

	[ExcludeFromCodeCoverage]
	private static async Task<string> DownloadAndExtractArchiveVoiceAsync
	(
		HttpClient http,
		string archiveUrl,
		string voicesDir,
		string piperDir,
		CancellationToken cancellationToken
	)
	{
		await Console.Error.WriteLineAsync
		(
			$"Downloading and extracting Piper voice from archive..."
		).ConfigureAwait(false);

		var tempArchive = Path.Combine(voicesDir, $"download-{Guid.NewGuid():N}{ModelDownloader.GetArchiveExtensionPublic(archiveUrl)}");
		var tempExtract = Path.Combine(voicesDir, $"extract-{Guid.NewGuid():N}");

		try
		{
			await DownloadFileAsync(http, archiveUrl, tempArchive, cancellationToken).ConfigureAwait(false);

			Directory.CreateDirectory(tempExtract);
			await ArchiveExtractor.ExtractAsync(tempArchive, tempExtract, cancellationToken).ConfigureAwait(false);

			var onnxFiles = Directory.GetFiles(tempExtract, "*.onnx", SearchOption.AllDirectories);
			if (onnxFiles.Length == 0)
			{
				throw new InvalidOperationException
				(
					$"No .onnx model file found in archive from {archiveUrl}"
				);
			}

			var sourceOnnx = onnxFiles[0];
			var modelName = Path.GetFileNameWithoutExtension(sourceOnnx);
			var finalOnnxPath = Path.Combine(voicesDir, $"{modelName}.onnx");

			File.Move(sourceOnnx, finalOnnxPath, overwrite: true);

			// Look for companion .onnx.json config
			var sourceDir = Path.GetDirectoryName(sourceOnnx)!;
			var configFiles = Directory.GetFiles(sourceDir, "*.onnx.json", SearchOption.TopDirectoryOnly);
			if (configFiles.Length > 0)
			{
				var finalConfigPath = Path.Combine(voicesDir, $"{modelName}.onnx.json");
				File.Move(configFiles[0], finalConfigPath, overwrite: true);
			}

			var jsonCandidate = Path.ChangeExtension(sourceOnnx, ".onnx.json");
			if (File.Exists(jsonCandidate))
			{
				File.Move(jsonCandidate, Path.Combine(voicesDir, $"{modelName}.onnx.json"), overwrite: true);
			}

			var sizeMb = new FileInfo(finalOnnxPath).Length / 1024 / 1024;
			await Console.Error.WriteLineAsync
			(
				$"Extracted {modelName} ({sizeMb} MB)."
			).ConfigureAwait(false);

			WriteUrlMapEntry(piperDir, archiveUrl, modelName);
			return finalOnnxPath;
		}
		finally
		{
			try { File.Delete(tempArchive); } catch (IOException) { }
			try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); }
			catch (IOException) { }
		}
	}

	// ── Voice URL map ──

	/// <summary>
	/// Looks up a URL in the voice URL map and returns the local .onnx path if cached,
	/// or null if not found.
	/// </summary>
	[ExcludeFromCodeCoverage]
	private static string? LookupUrlMap(string piperDir, string url)
	{
		var urlMapPath = Path.Combine(piperDir, UrlMapFileName);
		if (!File.Exists(urlMapPath))
			return null;

		var normalizedUrl = NormalizeUrl(url);
		var voicesDir = Path.Combine(piperDir, VoicesSubDir);
		var cached = EnumerateCachedVoices(voicesDir)
			.ToDictionary(v => v.Name, v => v.OnnxPath, StringComparer.OrdinalIgnoreCase);
		var urlMap = ModelDownloader.ReadUrlMap(urlMapPath);

		if
		(
			urlMap.TryGetValue(normalizedUrl, out var modelName)
			&& cached.TryGetValue(modelName, out var onnxPath)
		)
		{
			return onnxPath;
		}

		return null;
	}

	/// <summary>
	/// Registers a URL → model name mapping in the voices.json URL map.
	/// </summary>
	[ExcludeFromCodeCoverage]
	private static void WriteUrlMapEntry(string piperDir, string url, string modelName)
	{
		var urlMapPath = Path.Combine(piperDir, UrlMapFileName);
		var normalizedUrl = NormalizeUrl(url);
		var urlMap = ModelDownloader.ReadUrlMap(urlMapPath);
		urlMap[normalizedUrl] = modelName;
		ModelDownloader.WriteUrlMap(urlMapPath, urlMap);
	}

	/// <summary>
	/// Normalizes a URL for URL map lookup: trims trailing slashes, lowercases scheme+host.
	/// </summary>
	internal static string NormalizeUrl(string url)
	{
		var uri = new Uri(url.TrimEnd('/'));
#pragma warning disable CA1308 // URLs are conventionally lowercase
		return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{uri.PathAndQuery}";
#pragma warning restore CA1308
	}

	/// <summary>
	/// Resolves a "piper:en_US-ryan-high" or "piper:Cori" shorthand to HuggingFace download URLs.
	/// Friendly names (no locale/quality) are looked up in the built-in voice catalog.
	/// </summary>
	internal static (string OnnxUrl, string ConfigUrl, string ModelName) ResolvePiperShorthand
	(
		string voiceQuery
	)
	{
		var input = voiceQuery["piper:".Length..];
		var match = ModelNamePattern().Match(input);

		string modelName;

		if (match.Success)
		{
			// Full model name: piper:en_US-ryan-high
			modelName = input;
		}
		else
		{
			// Friendly name: piper:Cori, piper:Alan, piper:Amy
			modelName = ResolveFriendlyName(input)
				?? throw new ArgumentException
				(
					$"Unknown Piper voice '{input}'. " +
					"Use piper:{{lang}}_{{COUNTRY}}-{{name}}-{{quality}} (e.g. piper:en_US-ryan-high) " +
					"or a known name like piper:Amy, piper:Cori, piper:Alan. " +
					"See --help-piper for the full list."
				);
		}

		var m = ModelNamePattern().Match(modelName);
		var lang = m.Groups[1].Value;
		var locale = $"{m.Groups[1].Value}_{m.Groups[2].Value}";
		var name = m.Groups[3].Value;
		var quality = m.Groups[4].Value;

		var onnxUrl = $"{HuggingFaceBaseUrl}/{lang}/{locale}/{name}/{quality}/{modelName}.onnx";
		return (onnxUrl, onnxUrl + ".json", modelName);
	}

	/// <summary>
	/// Resolves a friendly voice name (e.g. "Cori", "Alan", "Amy") to a full model name.
	/// Prefers highest quality available. Returns null if not found.
	/// </summary>
	internal static string? ResolveFriendlyName(string friendlyName)
	{
		// Case-insensitive search through the catalog
		foreach (var (name, modelName) in VoiceCatalog)
		{
			if (name.EqualsIgnoreCase(friendlyName))
				return modelName;
		}
		return null;
	}

	/// <summary>
	/// Built-in catalog of standard Piper voices from rhasspy/piper-voices.
	/// Maps friendly names to their best-quality full model names.
	/// English voices only (US + GB). Add more as needed.
	/// </summary>
	private static readonly (string Name, string ModelName)[] VoiceCatalog =
	[
		// English (US) -- prefer highest quality available
		("Amy", "en_US-amy-medium"),
		("Arctic", "en_US-arctic-medium"),
		("Bryce", "en_US-bryce-medium"),
		("Danny", "en_US-danny-low"),
		("HFC_Female", "en_US-hfc_female-medium"),
		("HFC_Male", "en_US-hfc_male-medium"),
		("Joe", "en_US-joe-medium"),
		("John", "en_US-john-medium"),
		("Kathleen", "en_US-kathleen-low"),
		("Kristin", "en_US-kristin-medium"),
		("Kusal", "en_US-kusal-medium"),
		("L2arctic", "en_US-l2arctic-medium"),
		("Lessac", "en_US-lessac-high"),
		("Libritts", "en_US-libritts-high"),
		("Libritts_R", "en_US-libritts_r-medium"),
		("Ljspeech", "en_US-ljspeech-high"),
		("Norman", "en_US-norman-medium"),
		("Ryan", "en_US-ryan-high"),

		// English (GB)
		("Alan", "en_GB-alan-medium"),
		("Alba", "en_GB-alba-medium"),
		("Aru", "en_GB-aru-medium"),
		("Cori", "en_GB-cori-high"),
		("Jenny_Dioco", "en_GB-jenny_dioco-medium"),
		("Jenny", "en_GB-jenny_dioco-medium"),
		("Northern_English_Male", "en_GB-northern_english_male-medium"),
		("Semaine", "en_GB-semaine-medium"),
		("Southern_English_Female", "en_GB-southern_english_female-low"),
		("Vctk", "en_GB-vctk-medium"),
	];

	/// <summary>
	/// Synthesizes text to WAV bytes using a Piper ONNX model via sherpa-onnx (in-process).
	/// </summary>
	[ExcludeFromCodeCoverage]
	public static Task<byte[]> SynthesizeToWavAsync
	(
		string text,
		string modelPath,
		double? lengthScale,
		CancellationToken cancellationToken
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var wavBytes = SherpaEngine.SynthesizeToWav(text, modelPath, lengthScale);
		return Task.FromResult(wavBytes);
	}

	/// <summary>
	/// Synthesizes text with a Piper ONNX model and streams float sample blocks as they are
	/// generated. <paramref name="onStart"/> is invoked once with the sample rate;
	/// <paramref name="onChunk"/> is invoked for each generated block of float samples.
	/// </summary>
	[ExcludeFromCodeCoverage]
	public static Task SynthesizeStreamingAsync
	(
		string text,
		string modelPath,
		double? lengthScale,
		Action<int> onStart,
		Action<float[]> onChunk,
		CancellationToken cancellationToken
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Task.Run
		(
			() => SherpaEngine.SynthesizeStreaming(text, modelPath, lengthScale, onStart, onChunk),
			cancellationToken
		);
	}

	/// <summary>
	/// Returns a friendly display name for a Piper voice model.
	/// </summary>
	public static string GetDisplayName(string modelName)
	{
		var match = ModelNamePattern().Match(modelName);
		if (!match.Success)
			return $"Piper ({modelName})";

		var locale = $"{match.Groups[1].Value}-{match.Groups[2].Value}";
		var name = match.Groups[3].Value;
		var quality = match.Groups[4].Value;

		var displayName = char.ToUpperInvariant(name[0]) + name[1..];
		return $"Piper {displayName} ({quality}) - {locale}";
	}

	// ── Internals ──

	[ExcludeFromCodeCoverage]
	private static Task DownloadFileAsync
	(
		HttpClient http,
		string url,
		string destPath,
		CancellationToken cancellationToken
	)
	{
		// Single source of truth for downloads; gives us the same `[download]` timing line
		// that ModelDownloader emits for RVC zips.
		return ModelDownloader.DownloadFileAsync(http, url, destPath, cancellationToken);
	}
}
