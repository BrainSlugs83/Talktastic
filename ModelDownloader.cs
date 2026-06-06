using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Talktastic;

/// <summary>
/// Shared HTTP download + URL resolution logic for voice/model downloads.
/// Supports direct URLs, HuggingFace folder/tree URLs, GitHub release URLs, and .zip archives.
/// Includes a tab-separated registry file for caching URL → model name mappings.
/// </summary>
static partial class ModelDownloader
{
	/// <summary>
	/// Returns true if the query is an HTTP(S) URL.
	/// </summary>
	public static bool IsUrl(string query)
	{
		return Uri.TryCreate(query, UriKind.Absolute, out var uri)
			&& (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
	}

	/// <summary>
	/// Downloads a file with temp-file-then-move for atomicity.
	/// </summary>
	[ExcludeFromCodeCoverage]
	public static async Task DownloadFileAsync
	(
		HttpClient http,
		string url,
		string destPath,
		CancellationToken cancellationToken
	)
	{
		using var response = await http.GetAsync
		(
			new Uri(url),
			HttpCompletionOption.ResponseHeadersRead,
			cancellationToken
		).ConfigureAwait(false);

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

	/// <summary>
	/// Downloads a zip archive, extracts it, and returns the path to the first .onnx file found.
	/// The .onnx file is moved to <paramref name="destDir"/> and the temp zip is cleaned up.
	/// </summary>
	[ExcludeFromCodeCoverage]
	public static async Task<(string ModelPath, string ModelName)> DownloadAndExtractZipAsync
	(
		HttpClient http,
		string url,
		string destDir,
		CancellationToken cancellationToken
	)
	{
		Directory.CreateDirectory(destDir);
		var tempZip = Path.Combine(destDir, $"download-{Guid.NewGuid():N}.zip");

		// Derive a hint name from the URL's filename (e.g. "BartSimpson_e230_s7360")
		var urlFileName = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
		var hintName = Uri.UnescapeDataString(urlFileName ?? string.Empty);

		try
		{
			await DownloadFileAsync(http, url, tempZip, cancellationToken).ConfigureAwait(false);
			return await ExtractZipAsync(tempZip, destDir, hintName, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			try { File.Delete(tempZip); } catch (IOException) { }
		}
	}

	/// <summary>
	/// Extracts a local .zip file into a named subdirectory of destDir.
	/// All files from the zip (.pth, .index, .json, etc.) live together.
	/// Returns the path to the first .onnx or .pth found and the model name.
	/// </summary>
	[ExcludeFromCodeCoverage]
	public static async Task<(string ModelPath, string ModelName)> ExtractZipAsync
	(
		string zipPath,
		string destDir,
		string? hintName = null,
		CancellationToken cancellationToken = default
	)
	{
		Directory.CreateDirectory(destDir);
		var tempExtract = Path.Combine(destDir, $"extract-{Guid.NewGuid():N}");
		var fallbackName = !string.IsNullOrWhiteSpace(hintName)
			? hintName
			: Path.GetFileNameWithoutExtension(zipPath);

		try
		{
			Directory.CreateDirectory(tempExtract);
			await ZipFile.ExtractToDirectoryAsync(zipPath, tempExtract, overwriteFiles: true, cancellationToken).ConfigureAwait(false);

			// Find the primary model file
			var modelFile = Directory.GetFiles(tempExtract, "*.onnx", SearchOption.AllDirectories).FirstOrDefault()
				?? Directory.GetFiles(tempExtract, "*.pth", SearchOption.AllDirectories).FirstOrDefault()
				?? throw new InvalidOperationException
				(
					$"No .onnx or .pth model file found in zip archive: {Path.GetFileName(zipPath)}"
				);

			var modelName = ResolveModelName(modelFile, fallbackName);
			var modelDir = Path.Combine(destDir, modelName);

			// If the model dir already exists, nuke it for a clean re-download
			if (Directory.Exists(modelDir))
			{
				Directory.Delete(modelDir, recursive: true);
			}

			// Move the directory containing the model file into place
			var sourceDir = Path.GetDirectoryName(modelFile)!;
			Directory.Move(sourceDir, modelDir);

			// Rescue any .pth, .index, and .json files from elsewhere
			// in the zip tree. Many RVC zips scatter files across sibling
			// directories (e.g. weights/ vs logs/). Copy everything
			// worth keeping -- models may be taken down later.
			if (Directory.Exists(tempExtract))
			{
				foreach (var pattern in new[] { "*.pth", "*.index", "*.json" })
				{
					foreach (var file in Directory.GetFiles(tempExtract, pattern, SearchOption.AllDirectories))
					{
						var destFile = Path.Combine(modelDir, Path.GetFileName(file));
						if (!File.Exists(destFile))
						{
							File.Copy(file, destFile);
						}
					}
				}
			}

			// Find the model file in its new home
			var ext = Path.GetExtension(modelFile);
			var finalPath = Directory.GetFiles(modelDir, $"*{ext}", SearchOption.TopDirectoryOnly)
				.First();

			return (finalPath, modelName);
		}
		finally
		{
			try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); }
			catch (IOException) { }
		}
	}

	/// <summary>
	/// Generic names that should be replaced by the zip filename to avoid collisions.
	/// </summary>
	private static readonly string[] GenericModelNames =
	[
		"model", "weights", "checkpoint", "voice", "rvc",
	];

	/// <summary>
	/// Returns the best model name from the extracted file and the fallback hint.
	/// Favors whichever name isn't generic ("model", "weights", etc.) and isn't a GUID.
	/// If both are usable, prefers the internal filename.
	/// </summary>
	internal static string ResolveModelName(string extractedPath, string fallbackName)
	{
		var internalName = Path.GetFileNameWithoutExtension(extractedPath);
		var internalUsable = IsUsableName(internalName);
		var fallbackUsable = IsUsableName(fallbackName);

		if (internalUsable)
		{
			return CleanModelName(internalName)!;
		}

		if (fallbackUsable)
		{
			return CleanModelName(SanitizeFileName(fallbackName))!;
		}

		// Both are garbage -- return internal as-is (shouldn't happen in practice)
		return CleanModelName(internalName)!;
	}

	/// <summary>
	/// Cleans the model name.
	/// </summary>
	/// <param name="rawName">The raw name.</param>
	/// <returns>The resulting string, or <c>null</c> if no value is available.</returns>
	internal static string? CleanModelName(string? rawName)
	{
		if (string.IsNullOrEmpty(rawName))
		{
			return rawName;
		}

		var cleaned = rawName.Replace('_', ' ');
		cleaned = MultipleSpacesPattern().Replace(cleaned, " ");
		return cleaned.Trim();
	}

	/// <summary>
	/// Determines whether the name is usable.
	/// </summary>
	/// <param name="name">The name.</param>
	/// <returns><c>true</c> if the condition is met; otherwise, <c>false</c>.</returns>
	internal static bool IsUsableName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return false;
		}

		if
		(
			GenericModelNames.Any
			(
				g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase)
			)
		)
		{
			return false;
		}

		// GUIDs (with or without hyphens) and download-{guid} temps
		if (Guid.TryParse(name, out _))
		{
			return false;
		}

		if
		(
			name.StartsWith("download-", StringComparison.OrdinalIgnoreCase)
			&& Guid.TryParse(name.AsSpan(9), out _)
		)
		{
			return false;
		}

		return true;
	}

	/// <summary>
	/// Strips characters that are problematic in filenames and GUIDs from download temps.
	/// </summary>
	internal static string SanitizeFileName(string name)
	{
		// Strip download-{guid} prefix if this is a temp zip
		if (name.StartsWith("download-", StringComparison.OrdinalIgnoreCase) && name.Length > 41)
		{
			name = name[41..].TrimStart('-', '_', ' ');
		}

		return string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
	}

	/// <summary>
	/// Normalizes a URL for registry lookup: trims trailing slashes, lowercases scheme+host.
	/// </summary>
	public static string NormalizeUrl(string url)
	{
		var uri = new Uri(url.TrimEnd('/'));
#pragma warning disable CA1308 // URLs are conventionally lowercase
		return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{uri.PathAndQuery}";
#pragma warning restore CA1308
	}

	// ── Registry I/O ──

	/// <summary>
	/// Looks up a URL in a tab-separated registry file. Returns the model name if found.
	/// </summary>
	public static string? LookupRegistry(string registryPath, string url)
	{
		if (!File.Exists(registryPath))
			return null;

		var normalizedUrl = NormalizeUrl(url);
		var lines = File.ReadAllLines(registryPath);

		foreach (var line in lines)
		{
			var tab = line.IndexOf('\t', StringComparison.Ordinal);
			if (tab < 0) continue;

			var entryUrl = line[..tab];
			if (string.Equals(entryUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase))
				return line[(tab + 1)..];
		}

		return null;
	}

	/// <summary>
	/// Registers a URL → model name mapping in a tab-separated registry file.
	/// </summary>
	public static void WriteRegistry(string registryPath, string url, string modelName)
	{
		var normalizedUrl = NormalizeUrl(url);
		var newEntry = $"{normalizedUrl}\t{modelName}";

		var lines = File.Exists(registryPath)
			? File.ReadAllLines(registryPath).ToList()
			: [];

		var replaced = false;
		for (var i = 0; i < lines.Count; i++)
		{
			var tab = lines[i].IndexOf('\t', StringComparison.Ordinal);
			if (tab < 0) continue;

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

	// ── HuggingFace URL resolution ──

	/// <summary>
	/// Resolves a HuggingFace folder URL to a model file (.onnx or .zip) inside it.
	/// Prefers .onnx files, falls back to .zip archives.
	/// </summary>
	[ExcludeFromCodeCoverage]
	private static async Task<(string FileUrl, string ModelName, bool IsZip, string[]? CompanionUrls)> ResolveHuggingFaceModelAsync
	(
		HttpClient http,
		string url,
		CancellationToken cancellationToken
	)
	{
		var uri = new Uri(url);
		var segments = uri.LocalPath.Trim('/').Split('/');
		if (segments.Length < 2)
			throw new ArgumentException($"Cannot parse HuggingFace URL: {uri}");

		var owner = segments[0];
		var repo = segments[1];
		var branch = "main";
		var subPath = "";

		// Handle /resolve/ URLs (direct file links that don't end in known extension)
		if (segments.Length >= 4 && segments[2] == "resolve")
		{
			branch = segments[3];
			if (segments.Length > 4)
			{
				var filePath = string.Join('/', segments[4..]);
				var fileUrl = $"https://huggingface.co/{owner}/{repo}/resolve/{branch}/{filePath}";
				var resolvedSubPath = Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? "";
				var fileName = Path.GetFileNameWithoutExtension(filePath);
				var name = string.IsNullOrEmpty(resolvedSubPath)
					? DeriveModelNameFromRepo(repo, resolvedSubPath)
					: IsUsableName(fileName)
						? fileName
						: DeriveModelNameFromRepo(repo, resolvedSubPath);
				var isZip = filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
				return (fileUrl, CleanModelName(name)!, isZip, null);
			}
		}

		if (segments.Length >= 4 && segments[2] == "tree")
		{
			branch = segments[3];
			if (segments.Length > 4)
				subPath = string.Join('/', segments[4..]);
		}

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
		var baseResolve = $"https://huggingface.co/{owner}/{repo}/resolve/{branch}";

		// Prefer .onnx files
		var onnxPath = FindPathInJson(json, HfOnnxPathPattern(), ".onnx.json");
		if (onnxPath is not null)
		{
			var fileUrl = $"{baseResolve}/{onnxPath}";
			var onnxFilename = Path.GetFileNameWithoutExtension(onnxPath);
			var modelName = string.IsNullOrEmpty(subPath)
				? DeriveModelNameFromRepo(repo, subPath)
				: onnxFilename.Equals("model", StringComparison.OrdinalIgnoreCase)
					? DeriveModelNameFromRepo(repo, subPath)
					: onnxFilename;
			return (fileUrl, CleanModelName(modelName)!, false, null);
		}

		// Fall back to .zip files
		var zipPath = FindPathInJson(json, HfZipPathPattern(), null);
		if (zipPath is not null)
		{
			var fileUrl = $"{baseResolve}/{zipPath}";
			var zipFilename = Path.GetFileNameWithoutExtension(zipPath);
			var modelName = string.IsNullOrEmpty(subPath)
				? DeriveModelNameFromRepo(repo, subPath)
				: zipFilename;
			return (fileUrl, CleanModelName(modelName)!, true, null);
		}

		// Fall back to .pth files (with companion .index/.json downloads)
		var pthPath = FindPathInJson(json, HfPthPathPattern(), null);
		if (pthPath is not null)
		{
			var fileUrl = $"{baseResolve}/{pthPath}";
			var pthFilename = Path.GetFileNameWithoutExtension(pthPath);
			var modelName = string.IsNullOrEmpty(subPath)
				? DeriveModelNameFromRepo(repo, subPath)
				: IsUsableName(pthFilename)
					? pthFilename
					: DeriveModelNameFromRepo(repo, subPath);

			// Collect companion files (.index, .json but not .gitattributes etc.)
			var companions = new List<string>();
			var indexPath = FindPathInJson(json, HfIndexPathPattern(), null);
			if (indexPath is not null)
			{
				companions.Add($"{baseResolve}/{indexPath}");
			}

			var jsonPath = FindPathInJson(json, HfConfigJsonPathPattern(), null);
			if (jsonPath is not null)
			{
				companions.Add($"{baseResolve}/{jsonPath}");
			}

			return (fileUrl, CleanModelName(modelName)!, false, companions.Count > 0 ? companions.ToArray() : null);
		}

		throw new InvalidOperationException
		(
			$"No .onnx, .zip, or .pth model file found in {apiUrl}"
		);
	}

	/// <summary>
	/// Resolves a GitHub releases URL to a model file (.onnx or .zip) asset URL.
	/// Prefers .onnx files, falls back to .zip archives.
	/// </summary>
	[ExcludeFromCodeCoverage]
	private static async Task<(string FileUrl, string ModelName, bool IsZip, string[]? CompanionUrls)> ResolveGitHubReleaseModelAsync
	(
		HttpClient http,
		string url,
		CancellationToken cancellationToken
	)
	{
		var uri = new Uri(url);
		var segments = uri.LocalPath.Trim('/').Split('/');
		if (segments.Length < 5)
			throw new ArgumentException($"Cannot parse GitHub release URL: {uri}");

		var owner = segments[0];
		var repo = segments[1];
		var kind = segments[3];

		if (kind == "download" && segments.Length >= 6)
		{
			var filename = segments[^1];
			if (filename.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
			{
				var name = filename[..^".onnx".Length];
				return (uri.ToString(), CleanModelName(name)!, false, null);
			}
			if (filename.EndsWith(".pth", StringComparison.OrdinalIgnoreCase))
			{
				var name = filename[..^".pth".Length];
				return (uri.ToString(), CleanModelName(name)!, false, null);
			}
			if (filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
			{
				var name = filename[..^".zip".Length];
				return (uri.ToString(), CleanModelName(name)!, true, null);
			}
		}

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

		// Prefer .onnx assets
		var onnxUrl = FindAssetUrlInJson(json, GhOnnxAssetPattern(), ".onnx.json");
		if (onnxUrl is not null)
		{
			var onnxFilename = Path.GetFileName(new Uri(onnxUrl).LocalPath);
			return (onnxUrl, CleanModelName(onnxFilename[..^".onnx".Length])!, false, null);
		}

		// Fall back to .zip assets
		var zipUrl = FindAssetUrlInJson(json, GhZipAssetPattern(), null);
		if (zipUrl is not null)
		{
			var zipFilename = Path.GetFileName(new Uri(zipUrl).LocalPath);
			return (zipUrl, CleanModelName(zipFilename[..^".zip".Length])!, true, null);
		}

		// Fall back to .pth assets (with companion .index)
		var pthUrl = FindAssetUrlInJson(json, GhPthAssetPattern(), null);
		if (pthUrl is not null)
		{
			var pthFilename = Path.GetFileName(new Uri(pthUrl).LocalPath);
			var modelName = IsUsableName(pthFilename[..^".pth".Length])
				? pthFilename[..^".pth".Length]
				: repo;

			var companions = new List<string>();
			var indexUrl = FindAssetUrlInJson(json, GhIndexAssetPattern(), null);
			if (indexUrl is not null)
			{
				companions.Add(indexUrl);
			}

			return (pthUrl, CleanModelName(modelName)!, false, companions.Count > 0 ? companions.ToArray() : null);
		}

		throw new InvalidOperationException
		(
			$"No .onnx, .zip, or .pth model file found in GitHub release '{tag}'"
		);
	}

	// ── Shared URL dispatch ──

	/// <summary>
	/// Resolves any supported URL pattern to a direct download URL + model name.
	/// Supports .onnx direct links, .zip archives, HuggingFace folders, and GitHub releases.
	/// The returned FileUrl may be .onnx or .zip -- caller should check <see cref="IsZipUrl"/>.
	/// </summary>
	[ExcludeFromCodeCoverage]
	public static async Task<(string FileUrl, string ModelName, bool IsZip, string[]? CompanionUrls)> ResolveModelUrlAsync
	(
		HttpClient http,
		string url,
		CancellationToken cancellationToken
	)
	{
		// Direct .onnx file URL
		if (url.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
		{
			var uri = new Uri(url);
			var modelName = DeriveNameFromDirectUrl(uri);
			return (url, modelName, false, null);
		}

		// Direct .pth file URL
		if (url.EndsWith(".pth", StringComparison.OrdinalIgnoreCase))
		{
			var uri = new Uri(url);
			var modelName = DeriveNameFromDirectUrl(uri);
			return (url, modelName, false, null);
		}

		// Direct .zip file URL
		if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
		{
			var uri = new Uri(url);
			var modelName = DeriveNameFromDirectUrl(uri);
			return (url, modelName, true, null);
		}

		var host = new Uri(url).Host;

		if (host.Contains("huggingface.co", StringComparison.OrdinalIgnoreCase))
			return await ResolveHuggingFaceModelAsync(http, url, cancellationToken).ConfigureAwait(false);

		if (host.Contains("github.com", StringComparison.OrdinalIgnoreCase)
			&& url.Contains("/releases/", StringComparison.OrdinalIgnoreCase))
		{
			return await ResolveGitHubReleaseModelAsync(http, url, cancellationToken).ConfigureAwait(false);
		}

		throw new ArgumentException($"Unsupported URL pattern: {url}");
	}

	/// <summary>
	/// Returns true if a URL points to a .zip archive.
	/// </summary>
	public static bool IsZipUrl(string url) =>
		url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

	// ── JSON helpers (minimal regex-based, AOT-safe) ──

	/// <summary>
	/// Gets the HuggingFace ONNX path regex.
	/// </summary>
	/// <returns>The generated regex.</returns>
	[GeneratedRegex(@"""path""\s*:\s*""([^""]+\.onnx)""")]
	private static partial Regex HfOnnxPathPattern();

	/// <summary>
	/// Gets the HuggingFace ZIP path regex.
	/// </summary>
	/// <returns>The generated regex.</returns>
	[GeneratedRegex(@"""path""\s*:\s*""([^""]+\.zip)""")]
	private static partial Regex HfZipPathPattern();

	/// <summary>
	/// Gets the HuggingFace PTH path regex.
	/// </summary>
	/// <returns>The generated regex.</returns>
	[GeneratedRegex(@"""path""\s*:\s*""([^""]+\.pth)""")]
	private static partial Regex HfPthPathPattern();

	/// <summary>
	/// Gets the HuggingFace index path regex.
	/// </summary>
	/// <returns>The generated regex.</returns>
	[GeneratedRegex(@"""path""\s*:\s*""([^""]+\.index)""")]
	private static partial Regex HfIndexPathPattern();

	/// <summary>
	/// Gets the HuggingFace config JSON path regex.
	/// </summary>
	/// <returns>The generated regex.</returns>
	[GeneratedRegex(@"""path""\s*:\s*""(config\.json|metadata\.json)""")]
	private static partial Regex HfConfigJsonPathPattern();

	/// <summary>
	/// Gets the GitHub ONNX asset regex.
	/// </summary>
	/// <returns>The generated regex.</returns>
	[GeneratedRegex(@"""browser_download_url""\s*:\s*""([^""]+\.onnx)""")]
	private static partial Regex GhOnnxAssetPattern();

	/// <summary>
	/// Gets the GitHub ZIP asset regex.
	/// </summary>
	/// <returns>The generated regex.</returns>
	[GeneratedRegex(@"""browser_download_url""\s*:\s*""([^""]+\.zip)""")]
	private static partial Regex GhZipAssetPattern();

	/// <summary>
	/// Gets the GitHub PTH asset regex.
	/// </summary>
	/// <returns>The generated regex.</returns>
	[GeneratedRegex(@"""browser_download_url""\s*:\s*""([^""]+\.pth)""")]
	private static partial Regex GhPthAssetPattern();

	/// <summary>
	/// Gets the GitHub index asset regex.
	/// </summary>
	/// <returns>The generated regex.</returns>
	[GeneratedRegex(@"""browser_download_url""\s*:\s*""([^""]+\.index)""")]
	private static partial Regex GhIndexAssetPattern();

	/// <summary>
	/// Gets the repository name regex.
	/// </summary>
	/// <returns>The generated regex.</returns>
	[GeneratedRegex(@"(?:vits-)?piper-(.+)$", RegexOptions.IgnoreCase)]
	private static partial Regex RepoNamePattern();

	/// <summary>
	/// Gets the multiple-spaces regex.
	/// </summary>
	/// <returns>The generated regex.</returns>
	[GeneratedRegex(" {2,}")]
	private static partial Regex MultipleSpacesPattern();

	/// <summary>
	/// Finds the first file path matching a pattern in a JSON listing.
	/// Optionally excludes paths ending with a specific suffix.
	/// </summary>
	internal static string? FindPathInJson(string json, Regex pattern, string? excludeSuffix)
	{
		foreach (Match m in pattern.Matches(json))
		{
			var path = m.Groups[1].Value;
			if (excludeSuffix is not null
				&& path.EndsWith(excludeSuffix, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			return path;
		}
		return null;
	}

	/// <summary>
	/// Finds the first asset URL matching a pattern in a GitHub releases JSON response.
	/// </summary>
	internal static string? FindAssetUrlInJson(string json, Regex pattern, string? excludeSuffix)
	{
		foreach (Match m in pattern.Matches(json))
		{
			var url = m.Groups[1].Value;
			if (excludeSuffix is not null
				&& url.EndsWith(excludeSuffix, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			return url;
		}
		return null;
	}

	/// <summary>
	/// Extracts a model name from a direct download URL. Uses the filename if it's
	/// not generic; otherwise walks up the URL path for a usable segment
	/// (e.g. the repo name from a HuggingFace URL).
	/// </summary>
	internal static string DeriveNameFromDirectUrl(Uri uri)
	{
		var fileName = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(uri.LocalPath));
		if (IsUsableName(fileName))
		{
			return CleanModelName(fileName)!;
		}

		// Walk up path segments for something usable
		// e.g. /binant/BartSimpson_e230_s7360/resolve/main/model.pth
		var segments = uri.LocalPath
			.Split('/', StringSplitOptions.RemoveEmptyEntries)
			.Select(Uri.UnescapeDataString)
			.ToArray();

		// Skip the filename (last) and "main"/"resolve" boilerplate
		for (var i = segments.Length - 2; i >= 0; i--)
		{
			var seg = segments[i];
			if
			(
				string.Equals(seg, "main", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(seg, "resolve", StringComparison.OrdinalIgnoreCase)
			)
			{
				continue;
			}

			if (IsUsableName(seg))
			{
				return CleanModelName(seg)!;
			}
		}

		return CleanModelName(fileName)!;
	}

	/// <summary>
	/// Derives the model name from a repository.
	/// </summary>
	/// <param name="repo">The repository name.</param>
	/// <param name="subPath">The subpath.</param>
	/// <returns>The resulting string.</returns>
	internal static string DeriveModelNameFromRepo(string repo, string subPath)
	{
		if (!string.IsNullOrEmpty(subPath))
		{
			var parts = subPath.Split('/');
			if (parts.Length >= 3)
			{
				var locale = parts.Length >= 2 ? parts[1] : parts[0];
				var name = parts.Length >= 3 ? parts[2] : "";
				var quality = parts.Length >= 4 ? parts[3] : "medium";
				if (!string.IsNullOrEmpty(name))
					return CleanModelName($"{locale}-{name}-{quality}")!;
			}
		}

		var match = RepoNamePattern().Match(repo);
		return CleanModelName(match.Success ? match.Groups[1].Value : repo)!;
	}
}
