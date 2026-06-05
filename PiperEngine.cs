using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Talktastic;

/// <summary>
/// Synthesizes speech using Piper TTS via process invocation.
/// Runtime and voice models live in a .piper-tts directory (LOCALAPPDATA → TEMP → CWD).
/// Voice models are downloaded on demand from HuggingFace.
/// </summary>
static partial class PiperEngine
{
	private const string PiperDirName = ".piper-tts";
	private const string VoicesSubDir = "voices";
	private const string RegistryFileName = "voices.json";

	private const string HuggingFaceBaseUrl =
		"https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0";

	// piper:en_US-ryan-high → lang=en, locale=en_US, name=ryan, quality=high
	[GeneratedRegex(@"^([a-z]{2})_([A-Z]{2})-(.+)-(\w+)$")]
	private static partial Regex ModelNamePattern();

	private static string? _resolvedPiperDir;

	// ── Location resolution ──

	private static readonly string[] SearchBases =
	[
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Talktastic"),
		Path.GetTempPath(),
		Environment.CurrentDirectory,
	];

	/// <summary>
	/// Finds or creates the .piper-tts directory. Searches LOCALAPPDATA, TEMP, CWD.
	/// If the runtime isn't found anywhere, extracts the embedded zip to the first writable location.
	/// </summary>
	public static string EnsureRuntimeAvailable()
	{
		if (_resolvedPiperDir is not null)
			return _resolvedPiperDir;

		// Check existing installations
		foreach (var basePath in SearchBases)
		{
			var candidate = Path.Combine(basePath, PiperDirName);
			if (File.Exists(Path.Combine(candidate, "piper.exe")))
			{
				_resolvedPiperDir = candidate;
				return candidate;
			}
		}

		// Not found -- extract embedded runtime
		_resolvedPiperDir = ExtractRuntime();
		return _resolvedPiperDir;
	}

	/// <summary>
	/// Extracts the embedded piper-runtime.zip to the first writable .piper-tts location.
	/// </summary>
	private static string ExtractRuntime()
	{
		var assembly = typeof(PiperEngine).Assembly;
		var resourceName = "Talktastic.Piper.piper-runtime.zip";

		using var stream = assembly.GetManifestResourceStream(resourceName);
		if (stream is null)
			throw new InvalidOperationException
			(
				"Piper runtime not embedded. Piper voices are not available in this build."
			);

		foreach (var basePath in SearchBases)
		{
			var targetDir = Path.Combine(basePath, PiperDirName);
			try
			{
				Directory.CreateDirectory(targetDir);

				using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
				zip.ExtractToDirectory(targetDir, overwriteFiles: true);

				if (File.Exists(Path.Combine(targetDir, "piper.exe")))
					return targetDir;
			}
			catch (UnauthorizedAccessException) { }
			catch (IOException) { }
		}

		throw new InvalidOperationException
		(
			"Failed to extract Piper runtime. Could not write to LOCALAPPDATA, TEMP, or CWD."
		);
	}

	// ── Public API ──

	/// <summary>
	/// Returns true if the voice query is a Piper voice.
	/// Matches: "piper:model-name", direct .onnx URLs, HuggingFace tree/model URLs,
	/// and GitHub release URLs.
	/// </summary>
	public static bool IsPiperVoice(string? voiceQuery)
	{
		if (voiceQuery is null)
			return false;

		if (voiceQuery.StartsWith("piper:", StringComparison.OrdinalIgnoreCase))
			return true;

		if (!IsUrlVoice(voiceQuery))
			return false;

		// Direct .onnx URL
		if (voiceQuery.Contains(".onnx", StringComparison.OrdinalIgnoreCase))
			return true;

		// HuggingFace tree/model page (folder containing .onnx files)
		if (voiceQuery.Contains("huggingface.co", StringComparison.OrdinalIgnoreCase))
			return true;

		// GitHub release page
		if
		(
			voiceQuery.Contains("github.com", StringComparison.OrdinalIgnoreCase)
			&& voiceQuery.Contains("/releases/", StringComparison.OrdinalIgnoreCase)
		)
		{
			return true;
		}

		return false;
	}

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
	private static string GetModelNameFromUrl(string url)
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
	/// and GitHub release URLs. Uses a local voices.json registry to avoid re-downloading.
	/// </summary>
	public static async Task<string> EnsureVoiceModelAsync
	(
		string voiceQuery,
		CancellationToken cancellationToken
	)
	{
		var piperDir = EnsureRuntimeAvailable();
		var voicesDir = Path.Combine(piperDir, VoicesSubDir);
		Directory.CreateDirectory(voicesDir);

		// For URL voices, check the registry first
		if (IsUrlVoice(voiceQuery))
		{
			var cached = LookupRegistry(piperDir, voiceQuery);
			if (cached is not null)
				return cached;
		}

		using var http = new HttpClient();
		http.DefaultRequestHeaders.Add("User-Agent", "Talktastic");

		// Resolve the voice query to concrete download URLs + config URL
		var resolved = IsUrlVoice(voiceQuery)
			? await ResolveUrlVoiceAsync(http, voiceQuery, cancellationToken).ConfigureAwait(false)
			: ResolvePiperShorthand(voiceQuery);

		var modelPath = Path.Combine(voicesDir, $"{resolved.ModelName}.onnx");
		var configPath = Path.Combine(voicesDir, $"{resolved.ModelName}.onnx.json");

		if (File.Exists(modelPath) && File.Exists(configPath))
		{
			// Already downloaded but wasn't in registry (piper: shorthand, or registry lost)
			if (IsUrlVoice(voiceQuery))
				WriteRegistry(piperDir, voiceQuery, resolved.ModelName);

			return modelPath;
		}

		await Console.Error.WriteLineAsync
		(
			$"Downloading Piper voice '{resolved.ModelName}'..."
		).ConfigureAwait(false);

		await DownloadFileAsync(http, resolved.OnnxUrl, modelPath, cancellationToken).ConfigureAwait(false);
		await DownloadFileAsync(http, resolved.ConfigUrl, configPath, cancellationToken).ConfigureAwait(false);

		var sizeMb = new FileInfo(modelPath).Length / 1024 / 1024;
		await Console.Error.WriteLineAsync
		(
			$"Downloaded {resolved.ModelName} ({sizeMb} MB)."
		).ConfigureAwait(false);

		// Register the URL → model name mapping
		if (IsUrlVoice(voiceQuery))
			WriteRegistry(piperDir, voiceQuery, resolved.ModelName);

		return modelPath;
	}

	// ── Voice registry ──

	/// <summary>
	/// Looks up a URL in the voice registry and returns the local .onnx path if cached,
	/// or null if not found.
	/// </summary>
	private static string? LookupRegistry(string piperDir, string url)
	{
		var registryPath = Path.Combine(piperDir, RegistryFileName);
		if (!File.Exists(registryPath))
			return null;

		var normalizedUrl = NormalizeUrl(url);
		var lines = File.ReadAllLines(registryPath);

		foreach (var line in lines)
		{
			// Format: <url>\t<modelName>
			var tab = line.IndexOf('\t', StringComparison.Ordinal);
			if (tab < 0)
				continue;

			var entryUrl = line[..tab];
			var modelName = line[(tab + 1)..];

			if (!string.Equals(entryUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase))
				continue;

			var modelPath = Path.Combine(piperDir, VoicesSubDir, $"{modelName}.onnx");
			var configPath = Path.Combine(piperDir, VoicesSubDir, $"{modelName}.onnx.json");

			if (File.Exists(modelPath) && File.Exists(configPath))
				return modelPath;
		}

		return null;
	}

	/// <summary>
	/// Registers a URL → model name mapping in voices.json.
	/// </summary>
	private static void WriteRegistry(string piperDir, string url, string modelName)
	{
		var registryPath = Path.Combine(piperDir, RegistryFileName);
		var normalizedUrl = NormalizeUrl(url);
		var newEntry = $"{normalizedUrl}\t{modelName}";

		// Read existing entries, replace if URL already present
		var lines = File.Exists(registryPath)
			? File.ReadAllLines(registryPath).ToList()
			: [];

		var replaced = false;
		for (var i = 0; i < lines.Count; i++)
		{
			var tab = lines[i].IndexOf('\t', StringComparison.Ordinal);
			if (tab < 0)
				continue;

			var entryUrl = lines[i][..tab];
			if (string.Equals(entryUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase))
			{
				lines[i] = newEntry;
				replaced = true;
				break;
			}
		}

		if (!replaced)
			lines.Add(newEntry);

		File.WriteAllLines(registryPath, lines);
	}

	/// <summary>
	/// Normalizes a URL for registry lookup: trims trailing slashes, lowercases scheme+host.
	/// </summary>
	private static string NormalizeUrl(string url)
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
	private static (string OnnxUrl, string ConfigUrl, string ModelName) ResolvePiperShorthand
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
	private static string? ResolveFriendlyName(string friendlyName)
	{
		// Case-insensitive search through the catalog
		foreach (var (name, modelName) in VoiceCatalog)
		{
			if (string.Equals(name, friendlyName, StringComparison.OrdinalIgnoreCase))
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
	/// Resolves a URL voice query to concrete download URLs.
	/// Handles: direct .onnx URLs, HuggingFace folder/tree URLs, GitHub release URLs.
	/// </summary>
	private static async Task<(string OnnxUrl, string ConfigUrl, string ModelName)> ResolveUrlVoiceAsync
	(
		HttpClient http,
		string url,
		CancellationToken cancellationToken
	)
	{
		var uri = new Uri(url);
#pragma warning disable CA1308 // URLs are conventionally lowercase
		var host = uri.Host.ToLowerInvariant();
#pragma warning restore CA1308

		// Direct .onnx file URL -- trivial case
		if (url.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
		{
			var modelName = Path.GetFileNameWithoutExtension(uri.LocalPath);
			return (url, url + ".json", modelName);
		}

		if (url.EndsWith(".onnx.json", StringComparison.OrdinalIgnoreCase))
		{
			var onnxUrl = url[..^".json".Length];
			var modelName = Path.GetFileName(uri.LocalPath);
			modelName = modelName[..^".onnx.json".Length];
			return (onnxUrl, url, modelName);
		}

		// HuggingFace folder/tree URL
		if (host.Contains("huggingface.co", StringComparison.OrdinalIgnoreCase))
			return await ResolveHuggingFaceFolderAsync(http, uri, cancellationToken).ConfigureAwait(false);

		// GitHub release URL
		if (host.Contains("github.com", StringComparison.OrdinalIgnoreCase) && url.Contains("/releases/", StringComparison.OrdinalIgnoreCase))
			return await ResolveGitHubReleaseAsync(http, uri, cancellationToken).ConfigureAwait(false);

		throw new ArgumentException($"Unsupported URL pattern: {url}");
	}

	/// <summary>
	/// Resolves a HuggingFace folder URL to the .onnx file inside it.
	/// Supports both /tree/ browsing URLs and /api/models/ API URLs.
	/// Works with any HuggingFace repo structure (rhasspy/piper-voices subfolders,
	/// standalone model repos, etc.)
	/// </summary>
	private static async Task<(string OnnxUrl, string ConfigUrl, string ModelName)> ResolveHuggingFaceFolderAsync
	(
		HttpClient http,
		Uri uri,
		CancellationToken cancellationToken
	)
	{
		// Parse: huggingface.co/{owner}/{repo}/tree/{branch}/{path...}
		// or:   huggingface.co/{owner}/{repo}  (root of repo)
		var segments = uri.LocalPath.Trim('/').Split('/');
		if (segments.Length < 2)
			throw new ArgumentException($"Cannot parse HuggingFace URL: {uri}");

		var owner = segments[0];
		var repo = segments[1];
		var branch = "main";
		var subPath = "";

		if (segments.Length >= 4 && segments[2] == "tree")
		{
			branch = segments[3];
			if (segments.Length > 4)
				subPath = string.Join('/', segments[4..]);
		}

		// Query the HuggingFace API for folder contents
		var apiUrl = string.IsNullOrEmpty(subPath)
			? $"https://huggingface.co/api/models/{owner}/{repo}/tree/{branch}"
			: $"https://huggingface.co/api/models/{owner}/{repo}/tree/{branch}/{subPath}";

		var response = await http.GetAsync(new Uri(apiUrl), cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException
			(
				$"Failed to list HuggingFace folder: HTTP {(int)response.StatusCode} from {apiUrl}"
			);
		}

		var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

		// Find the .onnx model file in the listing
		var onnxPath = FindOnnxPathInJson(json);
		if (onnxPath is null)
			throw new InvalidOperationException($"No .onnx model file found in {apiUrl}");

		// Find the config file -- could be "{name}.onnx.json" or "config.json"
		var configPath = FindConfigPathInJson(json, onnxPath);
		if (configPath is null)
			throw new InvalidOperationException($"No config JSON file found in {apiUrl}");

		var onnxUrl = $"https://huggingface.co/{owner}/{repo}/resolve/{branch}/{onnxPath}";
		var configUrl = $"https://huggingface.co/{owner}/{repo}/resolve/{branch}/{configPath}";

		// Derive model name: prefer the .onnx filename, but if it's generic ("model.onnx"),
		// try to derive from the repo name (e.g. "piper-en_GB-aru-medium" → "en_GB-aru-medium")
		var onnxFilename = Path.GetFileNameWithoutExtension(onnxPath);
		var modelName = onnxFilename.Equals("model", StringComparison.OrdinalIgnoreCase)
			? DeriveModelNameFromRepo(repo, subPath)
			: onnxFilename;

		return (onnxUrl, configUrl, modelName);
	}

	/// <summary>
	/// Resolves a GitHub releases URL to the .onnx asset download URL.
	/// Supports: /releases/tag/{tag} and /releases/download/{tag}/{file}
	/// </summary>
	private static async Task<(string OnnxUrl, string ConfigUrl, string ModelName)> ResolveGitHubReleaseAsync
	(
		HttpClient http,
		Uri uri,
		CancellationToken cancellationToken
	)
	{
		// Parse: github.com/{owner}/{repo}/releases/tag/{tag}
		//    or: github.com/{owner}/{repo}/releases/download/{tag}/{file}
		var segments = uri.LocalPath.Trim('/').Split('/');
		if (segments.Length < 5)
			throw new ArgumentException($"Cannot parse GitHub release URL: {uri}");

		var owner = segments[0];
		var repo = segments[1];
		// segments[2] == "releases"
		var kind = segments[3]; // "tag" or "download"

		if (kind == "download" && segments.Length >= 6)
		{
			// Direct download link -- just find the .onnx
			var filename = segments[^1];
			if (filename.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
			{
				var modelName = filename[..^".onnx".Length];
				return (uri.ToString(), uri + ".json", modelName);
			}
		}

		// Release tag page -- query the API for assets
		var tag = segments[4];
		var apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}";

		var response = await http.GetAsync(new Uri(apiUrl), cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException
			(
				$"Failed to query GitHub release: HTTP {(int)response.StatusCode} from {apiUrl}"
			);
		}

		var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

		// Find the .onnx asset download URL
		var onnxUrl = FindGitHubOnnxAssetUrl(json);
		if (onnxUrl is null)
			throw new InvalidOperationException($"No .onnx model file found in GitHub release '{tag}'");

		var jsonUrl = onnxUrl + ".json";
		var onnxFilename = Path.GetFileName(new Uri(onnxUrl).LocalPath);
		var modelNameResult = onnxFilename[..^".onnx".Length];

		return (onnxUrl, jsonUrl, modelNameResult);
	}

	/// <summary>
	/// Derives a meaningful model name from a HuggingFace repo name when the .onnx file
	/// has a generic name like "model.onnx".
	/// e.g. repo "piper-en_GB-aru-medium" → "en_GB-aru-medium"
	///      repo "vits-piper-en_US-glados-high" → "en_US-glados-high"
	/// Falls back to the repo name itself if no piper pattern is found.
	/// </summary>
	[GeneratedRegex(@"(?:vits-)?piper-(.+)$", RegexOptions.IgnoreCase)]
	private static partial Regex RepoNamePattern();

	private static string DeriveModelNameFromRepo(string repo, string subPath)
	{
		// If there's a subpath (e.g. en/en_US/amy/medium), derive from the last segments
		if (!string.IsNullOrEmpty(subPath))
		{
			var parts = subPath.Split('/');
			if (parts.Length >= 3)
			{
				// subPath like "en/en_US/amy/medium" → en_US-amy-medium
				var locale = parts.Length >= 2 ? parts[1] : parts[0]; // en_US
				var name = parts.Length >= 3 ? parts[2] : "";
				var quality = parts.Length >= 4 ? parts[3] : "medium";
				if (!string.IsNullOrEmpty(name))
					return $"{locale}-{name}-{quality}";
			}
		}

		// Strip piper prefix from repo name
		var match = RepoNamePattern().Match(repo);
		return match.Success ? match.Groups[1].Value : repo;
	}

	// ── JSON helpers (minimal, no System.Text.Json dependency for AOT) ──

	[GeneratedRegex(@"""path""\s*:\s*""([^""]+\.onnx)""")]
	private static partial Regex HfOnnxPathPattern();

	[GeneratedRegex(@"""path""\s*:\s*""([^""]*config\.json)""")]
	private static partial Regex HfConfigJsonPattern();

	[GeneratedRegex(@"""browser_download_url""\s*:\s*""([^""]+\.onnx)""")]
	private static partial Regex GhAssetUrlPattern();

	/// <summary>
	/// Finds the first .onnx file path in a HuggingFace API JSON response.
	/// Excludes .onnx.json matches.
	/// </summary>
	private static string? FindOnnxPathInJson(string json)
	{
		foreach (Match m in HfOnnxPathPattern().Matches(json))
		{
			var path = m.Groups[1].Value;
			if (!path.EndsWith(".onnx.json", StringComparison.OrdinalIgnoreCase))
				return path;
		}
		return null;
	}

	/// <summary>
	/// Finds the config file path in a HuggingFace API JSON response.
	/// Checks for "{onnxName}.json" first (standard piper naming), then "config.json" (speaches-ai naming).
	/// </summary>
	private static string? FindConfigPathInJson(string json, string onnxPath)
	{
		// First: look for the standard "{name}.onnx.json" companion
		var onnxJsonPath = onnxPath + ".json";
		if (json.Contains(onnxJsonPath, StringComparison.OrdinalIgnoreCase))
			return onnxJsonPath;

		// Second: look for "config.json" in the same directory
		foreach (Match m in HfConfigJsonPattern().Matches(json))
		{
			return m.Groups[1].Value;
		}

		return null;
	}

	/// <summary>
	/// Finds the first .onnx asset download URL in a GitHub releases API JSON response.
	/// Excludes .onnx.json matches.
	/// </summary>
	private static string? FindGitHubOnnxAssetUrl(string json)
	{
		foreach (Match m in GhAssetUrlPattern().Matches(json))
		{
			var url = m.Groups[1].Value;
			if (!url.EndsWith(".onnx.json", StringComparison.OrdinalIgnoreCase))
				return url;
		}
		return null;
	}

	/// <summary>
	/// Synthesizes text to WAV bytes using Piper.
	/// </summary>
	public static async Task<byte[]> SynthesizeToWavAsync
	(
		string text,
		string modelPath,
		CancellationToken cancellationToken
	)
	{
		var piperDir = EnsureRuntimeAvailable();
		var piperExe = Path.Combine(piperDir, "piper.exe");
		var espeakData = Path.Combine(piperDir, "espeak-ng-data");

		var outFile = Path.Combine(Path.GetTempPath(), $"talktastic-piper-{Guid.NewGuid():N}.wav");

		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = piperExe,
				Arguments = $"--model \"{modelPath}\" --output_file \"{outFile}\" --espeak_data \"{espeakData}\" --quiet",
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};

			using var process = Process.Start(psi)
				?? throw new InvalidOperationException("Failed to start Piper process.");

			await process.StandardInput.WriteLineAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
			process.StandardInput.Close();

			// Drain stdout and stderr to avoid deadlocks (piper prints the output path to stdout)
			var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
			var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
			await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
			var stderr = await stderrTask.ConfigureAwait(false);

			await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

			if (process.ExitCode != 0)
			{
				throw new InvalidOperationException
				(
					$"Piper exited with code {process.ExitCode}: {stderr.Trim()}"
				);
			}

			return await File.ReadAllBytesAsync(outFile, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			try { File.Delete(outFile); } catch (IOException) { /* best effort */ }
		}
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

	private static async Task DownloadFileAsync
	(
		HttpClient http,
		string url,
		string destPath,
		CancellationToken cancellationToken
	)
	{
		using var response = await http.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
			.ConfigureAwait(false);

		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException
			(
				$"Failed to download {url}: HTTP {(int)response.StatusCode}"
			);
		}

		var tempPath = destPath + ".tmp";
		try
		{
			using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
			using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
			{
				await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
			}

			File.Move(tempPath, destPath, overwrite: true);
		}
		catch (IOException)
		{
			try { File.Delete(tempPath); } catch (IOException) { }
			throw;
		}
	}
}
