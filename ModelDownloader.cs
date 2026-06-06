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
		return query.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
			|| query.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Downloads a file with temp-file-then-move for atomicity.
	/// </summary>
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
	public static async Task<(string OnnxPath, string ModelName)> DownloadAndExtractZipAsync
	(
		HttpClient http,
		string url,
		string destDir,
		CancellationToken cancellationToken
	)
	{
		Directory.CreateDirectory(destDir);
		var tempZip = Path.Combine(destDir, $"download-{Guid.NewGuid():N}.zip");
		var tempExtract = Path.Combine(destDir, $"extract-{Guid.NewGuid():N}");

		try
		{
			await DownloadFileAsync(http, url, tempZip, cancellationToken).ConfigureAwait(false);

			Directory.CreateDirectory(tempExtract);
			await ZipFile.ExtractToDirectoryAsync(tempZip, tempExtract, overwriteFiles: true, cancellationToken).ConfigureAwait(false);

			// Find .onnx files in the extracted contents
			var onnxFiles = Directory.GetFiles(tempExtract, "*.onnx", SearchOption.AllDirectories);
			if (onnxFiles.Length == 0)
			{
				// Check if there are .pth files (common RVC format) -- give a helpful message
				var pthFiles = Directory.GetFiles(tempExtract, "*.pth", SearchOption.AllDirectories);
				if (pthFiles.Length > 0)
				{
					throw new InvalidOperationException
					(
						$"The zip archive contains .pth (PyTorch) files but no .onnx files. "
						+ "RVC .pth models must be converted to ONNX format first. "
						+ "Use the convert-rvc.py script to export: "
						+ $"found {string.Join(", ", pthFiles.Select(Path.GetFileName))}"
					);
				}

				throw new InvalidOperationException
				(
					$"No .onnx model file found in zip archive from {url}"
				);
			}

			// Take the first (or only) .onnx file
			var sourceOnnx = onnxFiles[0];
			var modelName = Path.GetFileNameWithoutExtension(sourceOnnx);
			var finalPath = Path.Combine(destDir, $"{modelName}.onnx");

			File.Move(sourceOnnx, finalPath, overwrite: true);

			// Also grab any companion files (.onnx.json, .index, etc.)
			var sourceDir = Path.GetDirectoryName(sourceOnnx)!;
			foreach (var companion in Directory.GetFiles(sourceDir))
			{
				var ext = Path.GetExtension(companion);
				if (string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(ext, ".index", StringComparison.OrdinalIgnoreCase))
				{
					var destFile = Path.Combine(destDir, Path.GetFileName(companion));
					File.Move(companion, destFile, overwrite: true);
				}
			}

			return (finalPath, modelName);
		}
		finally
		{
			try { File.Delete(tempZip); } catch (IOException) { }
			try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); }
			catch (IOException) { }
		}
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
	private static async Task<(string FileUrl, string ModelName, bool IsZip)> ResolveHuggingFaceModelAsync
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
				var name = Path.GetFileNameWithoutExtension(filePath);
				var isZip = filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
				return (fileUrl, name, isZip);
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

		// Prefer .onnx files
		var onnxPath = FindPathInJson(json, HfOnnxPathPattern(), ".onnx.json");
		if (onnxPath is not null)
		{
			var fileUrl = $"https://huggingface.co/{owner}/{repo}/resolve/{branch}/{onnxPath}";
			var onnxFilename = Path.GetFileNameWithoutExtension(onnxPath);
			var modelName = onnxFilename.Equals("model", StringComparison.OrdinalIgnoreCase)
				? DeriveModelNameFromRepo(repo, subPath)
				: onnxFilename;
			return (fileUrl, modelName, false);
		}

		// Fall back to .zip files
		var zipPath = FindPathInJson(json, HfZipPathPattern(), null);
		if (zipPath is not null)
		{
			var fileUrl = $"https://huggingface.co/{owner}/{repo}/resolve/{branch}/{zipPath}";
			var modelName = Path.GetFileNameWithoutExtension(zipPath);
			return (fileUrl, modelName, true);
		}

		throw new InvalidOperationException($"No .onnx or .zip model file found in {apiUrl}");
	}

	/// <summary>
	/// Resolves a GitHub releases URL to a model file (.onnx or .zip) asset URL.
	/// Prefers .onnx files, falls back to .zip archives.
	/// </summary>
	private static async Task<(string FileUrl, string ModelName, bool IsZip)> ResolveGitHubReleaseModelAsync
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
				return (uri.ToString(), name, false);
			}
			if (filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
			{
				var name = filename[..^".zip".Length];
				return (uri.ToString(), name, true);
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
			return (onnxUrl, onnxFilename[..^".onnx".Length], false);
		}

		// Fall back to .zip assets
		var zipUrl = FindAssetUrlInJson(json, GhZipAssetPattern(), null);
		if (zipUrl is not null)
		{
			var zipFilename = Path.GetFileName(new Uri(zipUrl).LocalPath);
			return (zipUrl, zipFilename[..^".zip".Length], true);
		}

		throw new InvalidOperationException
		(
			$"No .onnx or .zip model file found in GitHub release '{tag}'"
		);
	}

	// ── Shared URL dispatch ──

	/// <summary>
	/// Resolves any supported URL pattern to a direct download URL + model name.
	/// Supports .onnx direct links, .zip archives, HuggingFace folders, and GitHub releases.
	/// The returned FileUrl may be .onnx or .zip -- caller should check <see cref="IsZipUrl"/>.
	/// </summary>
	public static async Task<(string FileUrl, string ModelName, bool IsZip)> ResolveModelUrlAsync
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
			var modelName = Path.GetFileNameWithoutExtension(uri.LocalPath);
			return (url, modelName, false);
		}

		// Direct .zip file URL
		if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
		{
			var uri = new Uri(url);
			var modelName = Path.GetFileNameWithoutExtension(uri.LocalPath);
			return (url, modelName, true);
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

	[GeneratedRegex(@"""path""\s*:\s*""([^""]+\.onnx)""")]
	private static partial Regex HfOnnxPathPattern();

	[GeneratedRegex(@"""path""\s*:\s*""([^""]+\.zip)""")]
	private static partial Regex HfZipPathPattern();

	[GeneratedRegex(@"""browser_download_url""\s*:\s*""([^""]+\.onnx)""")]
	private static partial Regex GhOnnxAssetPattern();

	[GeneratedRegex(@"""browser_download_url""\s*:\s*""([^""]+\.zip)""")]
	private static partial Regex GhZipAssetPattern();

	[GeneratedRegex(@"(?:vits-)?piper-(.+)$", RegexOptions.IgnoreCase)]
	private static partial Regex RepoNamePattern();

	/// <summary>
	/// Finds the first file path matching a pattern in a JSON listing.
	/// Optionally excludes paths ending with a specific suffix.
	/// </summary>
	private static string? FindPathInJson(string json, Regex pattern, string? excludeSuffix)
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
	private static string? FindAssetUrlInJson(string json, Regex pattern, string? excludeSuffix)
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

	private static string DeriveModelNameFromRepo(string repo, string subPath)
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
					return $"{locale}-{name}-{quality}";
			}
		}

		var match = RepoNamePattern().Match(repo);
		return match.Success ? match.Groups[1].Value : repo;
	}
}
