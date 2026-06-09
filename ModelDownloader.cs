using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Talktastic;

/// <summary>
/// Shared HTTP download + URL resolution logic for voice/model downloads.
/// Supports direct URLs, HuggingFace folder/tree URLs, GitHub release URLs, and .zip archives.
/// Includes a JSON URL map file for caching URL → model name mappings, with TSV fallback for older files.
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
		var sw = System.Diagnostics.Stopwatch.StartNew();
		HttpResponseMessage response;
		try
		{
			response = await http.GetAsync
			(
				new Uri(url),
				HttpCompletionOption.ResponseHeadersRead,
				cancellationToken
			).ConfigureAwait(false);
		}
		catch (HttpRequestException ex)
		{
			// DNS failures, refused connections, TLS errors, dead CDNs
			// (e.g. models.weights.gg returning NXDOMAIN). Surface a clean
			// message naming the host so the user knows which source is dead,
			// instead of echoing the cryptic Windows DNS error verbatim.
			throw new InvalidOperationException
			(
				$"Failed to download {url}: {DescribeNetworkFailure(url, ex)}",
				ex
			);
		}

		using (response)
		{
			if (!response.IsSuccessStatusCode)
			{
				throw new InvalidOperationException
				(
					$"Failed to download {url}: HTTP {(int)response.StatusCode}"
				);
			}

			var tempPath = destPath + ".tmp";
			long bytesWritten = 0;
			try
			{
				using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
				using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
				{
					await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
					bytesWritten = file.Length;
				}

				File.Move(tempPath, destPath, overwrite: true);
			}
			catch (IOException)
			{
				try { File.Delete(tempPath); } catch (IOException) { }
				throw;
			}

			var elapsedMs = sw.ElapsedMilliseconds;
			var fileName = Path.GetFileName(destPath);
			var mb = bytesWritten / 1_048_576.0;
			var mbps = elapsedMs > 0 ? (bytesWritten * 1000.0 / elapsedMs) / 1_048_576.0 : 0.0;
			Diagnostics.LogPerf
			(
				string.Create
				(
					System.Globalization.CultureInfo.InvariantCulture,
					$"[download] {fileName}: {mb:F1} MB in {elapsedMs}ms ({mbps:F1} MB/s)"
				)
			);
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
		string? preferredName,
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
			return await ExtractZipAsync(tempZip, destDir, hintName, preferredName, cancellationToken).ConfigureAwait(false);
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
		string? preferredName = null,
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

			var modelName = ResolveModelName(modelFile, fallbackName, preferredName);
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
	/// Generic names that should be replaced by a better candidate to avoid collisions.
	/// Covers internal zip filenames ("model.pth", "weights.pth"), URL-derived
	/// placeholders ("download" from Drive's "?id=..." URLs, "file"/"archive" from
	/// other CDN fallbacks), and the temp-file naming patterns we emit ourselves
	/// ("download-{guid}", "drive_{fileId}").
	/// </summary>
	private static readonly string[] GenericModelNames =
	[
		"model", "weights", "checkpoint", "voice", "rvc",
		"download", "file", "archive",
	];

	/// <summary>
	/// Returns the best model name from three candidates, in priority order.
	/// </summary>
	/// <param name="extractedPath">Path to the extracted model file. Its filename
	/// (without extension) is the "internal" name -- often the trainer's working
	/// filename like <c>G_3200.pth</c> or <c>added_IVF8000_Flat.index</c>.</param>
	/// <param name="fallbackName">A last-resort hint, typically derived from the URL
	/// or temp zip filename.</param>
	/// <param name="preferredName">Optional curated name from the URL resolver
	/// (e.g. voice-models.com H3 title, Drive Content-Disposition filename).
	/// When usable, this always wins -- it's the name a human picked.</param>
	internal static string ResolveModelName(string extractedPath, string fallbackName, string? preferredName = null)
	{
		if (preferredName is not null && IsUsableName(preferredName))
		{
			return CleanModelName(SanitizeFileName(preferredName))!;
		}

		var internalName = Path.GetFileNameWithoutExtension(extractedPath);
		if (IsUsableName(internalName))
		{
			return CleanModelName(internalName)!;
		}

		if (IsUsableName(fallbackName))
		{
			return CleanModelName(SanitizeFileName(fallbackName))!;
		}

		// All three are garbage -- return internal as-is (shouldn't happen in practice)
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

		// Drive fallback "drive_{fileId}" -- the resolver emits these when it
		// can't recover the original filename from Content-Disposition.
		if (name.StartsWith("drive_", StringComparison.OrdinalIgnoreCase))
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
	/// Normalizes a URL for URL map lookup: trims trailing slashes, lowercases scheme+host.
	/// </summary>
	public static string NormalizeUrl(string url)
	{
		var uri = new Uri(url.TrimEnd('/'));
#pragma warning disable CA1308 // URLs are conventionally lowercase
		return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{uri.PathAndQuery}";
#pragma warning restore CA1308
	}

	/// <summary>
	/// Produces a human-readable description of a network failure during download.
	/// Translates cryptic Windows DNS errors (SocketError <c>NoData</c>,
	/// <c>HostNotFound</c>, etc.) into plain English that names the offending host,
	/// rather than echoing the raw "The requested name is valid, but no data of the
	/// requested type was found" text that makes users think their URL is malformed.
	/// </summary>
	internal static string DescribeNetworkFailure(string url, HttpRequestException ex)
	{
		string? host = null;
		try
		{
			host = new Uri(url).Host;
		}
		catch (UriFormatException)
		{
		}

		var socket = FindSocketException(ex);
		if (socket is not null)
		{
			var hostPart = string.IsNullOrEmpty(host) ? "the remote host" : host;
			return socket.SocketErrorCode switch
			{
				// All DNS-class failures collapse to the same user-facing message --
				// the practical effect is "this host is unreachable" regardless of
				// whether Windows reports HostNotFound or the technically-distinct
				// "name is valid but no records of the requested type" (NoData),
				// which is what Windows returns for a host that simply doesn't exist.
				System.Net.Sockets.SocketError.HostNotFound
				or System.Net.Sockets.SocketError.NoData
				or System.Net.Sockets.SocketError.NoRecovery
					=> $"DNS lookup for '{hostPart}' failed -- the CDN may be offline or the URL stale.",
				System.Net.Sockets.SocketError.TryAgain
					=> $"DNS lookup for '{hostPart}' timed out -- check your network connection.",
				System.Net.Sockets.SocketError.ConnectionRefused
					=> $"Connection to '{hostPart}' was refused (the server may be offline).",
				System.Net.Sockets.SocketError.TimedOut
					=> $"Connection to '{hostPart}' timed out.",
				_ => $"Network error reaching '{hostPart}': {socket.SocketErrorCode}.",
			};
		}

		return ex.Message;
	}

	private static System.Net.Sockets.SocketException? FindSocketException(Exception? ex)
	{
		while (ex is not null)
		{
			if (ex is System.Net.Sockets.SocketException sock)
			{
				return sock;
			}

			ex = ex.InnerException;
		}

		return null;
	}

	// ── URL map I/O ──

	/// <summary>
	/// Looks up a URL in the URL map file. Returns the model name if found.
	/// </summary>
	public static string? LookupUrlMap(string urlMapPath, string url)
	{
		var normalizedUrl = NormalizeUrl(url);
		var urlMap = ReadUrlMap(urlMapPath);
		return urlMap.TryGetValue(normalizedUrl, out var modelName)
			? modelName
			: null;
	}

	/// <summary>
	/// Reads the URL map file as a URL → model name mapping.
	/// Supports both JSON and the legacy tab-separated format.
	/// </summary>
	internal static Dictionary<string, string> ReadUrlMap(string urlMapPath)
	{
		var urlMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (!File.Exists(urlMapPath))
		{
			return urlMap;
		}

		var content = File.ReadAllText(urlMapPath);
		if (string.IsNullOrWhiteSpace(content))
		{
			return urlMap;
		}

		if (content.TrimStart().StartsWith('{'))
		{
			var jsonUrlMap = JsonSerializer.Deserialize(content, UrlMapJsonContext.Default.DictionaryStringString);
			if (jsonUrlMap is null)
			{
				return urlMap;
			}

			foreach (var (entryUrl, modelName) in jsonUrlMap)
			{
				urlMap[NormalizeUrl(entryUrl)] = modelName;
			}

			return urlMap;
		}

		foreach (var line in File.ReadLines(urlMapPath))
		{
			var tab = line.IndexOf('\t', StringComparison.Ordinal);
			if (tab < 0)
			{
				continue;
			}

			var entryUrl = line[..tab];
			var modelName = line[(tab + 1)..];
			urlMap[NormalizeUrl(entryUrl)] = modelName;
		}

		return urlMap;
	}

	/// <summary>
	/// Writes the URL map as JSON.
	/// </summary>
	internal static void WriteUrlMap
	(
		string urlMapPath,
		Dictionary<string, string> urlMap
	)
	{
		var normalizedUrlMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var (entryUrl, modelName) in urlMap)
		{
			normalizedUrlMap[NormalizeUrl(entryUrl)] = modelName;
		}

		var json = JsonSerializer.Serialize(normalizedUrlMap, UrlMapJsonContext.Default.DictionaryStringString);
		File.WriteAllText(urlMapPath, json);
	}

	/// <summary>
	/// Registers a URL → model name mapping in the URL map file.
	/// </summary>
	public static void WriteUrlMapEntry(string urlMapPath, string url, string modelName)
	{
		var normalizedUrl = NormalizeUrl(url);
		var urlMap = ReadUrlMap(urlMapPath);
		urlMap[normalizedUrl] = modelName;
		WriteUrlMap(urlMapPath, urlMap);
	}

	// ── Google Drive URL resolution ──

	/// <summary>
	/// Resolves a Google Drive share URL (e.g. <c>https://drive.google.com/file/d/{ID}/view</c>)
	/// to a direct download URL. For small files Drive serves the bytes directly; for larger
	/// files Drive interposes a "virus scan warning" HTML page that requires re-fetching with
	/// a confirmation token parsed out of the page form. Returns the post-confirmation URL
	/// plus the original filename (from <c>Content-Disposition</c>) when available.
	/// </summary>
	[ExcludeFromCodeCoverage]
	public static async Task<(string FileUrl, string ModelName, bool IsZip, string[]? CompanionUrls)> ResolveGoogleDriveModelAsync
	(
		HttpClient http,
		string url,
		CancellationToken cancellationToken
	)
	{
		var fileId = ExtractDriveFileId(url);
		if (fileId is null)
		{
			throw new ArgumentException
			(
				$"Could not extract Google Drive file ID from URL: {url}"
			);
		}

		Diagnostics.LogPerf
		(
			string.Create
			(
				System.Globalization.CultureInfo.InvariantCulture,
				$"[resolve] google-drive file id={fileId}"
			)
		);

		// First probe: the standard usercontent download endpoint. Drive returns the file
		// directly for small files; for larger files it returns a virus-scan-warning HTML
		// page that we have to parse for the confirm token.
		var downloadUrl = $"https://drive.usercontent.google.com/download?id={fileId}&export=download&authuser=0";
		var (finalUrl, fileName) = await ProbeDriveDownloadAsync(http, downloadUrl, cancellationToken).ConfigureAwait(false);

		var (modelName, isZip) = DeriveDriveNameAndType(fileName, fileId);
		return (finalUrl, modelName, isZip, null);
	}

	/// <summary>
	/// Extracts the Google Drive file ID from any of the common share URL shapes:
	/// <c>/file/d/{ID}/view</c>, <c>/open?id={ID}</c>, <c>/uc?id={ID}</c>,
	/// <c>/download?id={ID}</c>.
	/// </summary>
	/// <param name="url">The Drive URL.</param>
	/// <returns>The file ID, or null when no recognizable ID could be extracted.</returns>
	internal static string? ExtractDriveFileId(string url)
	{
		// `/file/d/{id}/...`
		var fileMatch = DriveFileIdPathPattern().Match(url);
		if (fileMatch.Success)
		{
			return fileMatch.Groups[1].Value;
		}

		// `?id={id}` (used by /open, /uc, /download)
		var queryMatch = DriveFileIdQueryPattern().Match(url);
		if (queryMatch.Success)
		{
			return queryMatch.Groups[1].Value;
		}

		return null;
	}

	/// <summary>
	/// GETs a Drive download URL, follows the virus-scan-warning HTML interstitial when
	/// present, and returns the final URL (post-confirm) plus the response's reported
	/// filename when it can be read out of <c>Content-Disposition</c>.
	/// </summary>
	[ExcludeFromCodeCoverage]
	private static async Task<(string FinalUrl, string? FileName)> ProbeDriveDownloadAsync
	(
		HttpClient http,
		string downloadUrl,
		CancellationToken cancellationToken
	)
	{
		using var firstProbe = await http.GetAsync
		(
			new Uri(downloadUrl),
			HttpCompletionOption.ResponseHeadersRead,
			cancellationToken
		).ConfigureAwait(false);

		if (!firstProbe.IsSuccessStatusCode)
		{
			throw new InvalidOperationException
			(
				$"Google Drive probe failed: HTTP {(int)firstProbe.StatusCode} for {downloadUrl}"
			);
		}

		var firstContentType = firstProbe.Content.Headers.ContentType?.MediaType ?? "";
		var firstFileName = ExtractFilenameFromContentDisposition
		(
			firstProbe.Content.Headers.ContentDisposition?.ToString()
		);

		if (!firstContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
		{
			return (downloadUrl, firstFileName);
		}

		// Virus-scan warning page. Parse the form to find the action URL + confirm token.
		var html = await firstProbe.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		var confirmUrl = BuildDriveConfirmUrl(html, downloadUrl)
			?? throw new InvalidOperationException
			(
				"Google Drive returned a virus-scan-warning page but no confirm form could be parsed. "
				+ "The file may be too large for unattended download; try a direct .zip URL instead."
			);

		using var secondProbe = await http.GetAsync
		(
			new Uri(confirmUrl),
			HttpCompletionOption.ResponseHeadersRead,
			cancellationToken
		).ConfigureAwait(false);

		if (!secondProbe.IsSuccessStatusCode)
		{
			throw new InvalidOperationException
			(
				$"Google Drive confirm fetch failed: HTTP {(int)secondProbe.StatusCode}"
			);
		}

		var secondFileName = ExtractFilenameFromContentDisposition
		(
			secondProbe.Content.Headers.ContentDisposition?.ToString()
		);
		return (confirmUrl, secondFileName ?? firstFileName);
	}

	/// <summary>
	/// Builds the post-confirm download URL from the virus-scan-warning HTML by extracting
	/// the form's <c>action</c> and hidden input values (notably <c>confirm</c> and
	/// <c>uuid</c>). Returns null when no confirm form is found.
	/// </summary>
	internal static string? BuildDriveConfirmUrl(string html, string fallbackBaseUrl)
	{
		var formMatch = DriveConfirmFormPattern().Match(html);
		if (!formMatch.Success)
		{
			return null;
		}

		var action = WebUtility.HtmlDecode(formMatch.Groups[1].Value);
		var query = new System.Text.StringBuilder();
		foreach (Match input in DriveConfirmInputPattern().Matches(html))
		{
			var name = WebUtility.HtmlDecode(input.Groups[1].Value);
			var value = WebUtility.HtmlDecode(input.Groups[2].Value);
			if (query.Length > 0)
			{
				query.Append('&');
			}
			query.Append(Uri.EscapeDataString(name));
			query.Append('=');
			query.Append(Uri.EscapeDataString(value));
		}

		if (query.Length == 0)
		{
			return null;
		}

		var separator = action.Contains('?', StringComparison.Ordinal) ? "&" : "?";
		return string.Create
		(
			System.Globalization.CultureInfo.InvariantCulture,
			$"{action}{separator}{query}"
		);
	}

	/// <summary>
	/// Reads the <c>filename</c> parameter out of a <c>Content-Disposition</c> header value,
	/// preferring the RFC 5987 <c>filename*=UTF-8''...</c> form when present.
	/// </summary>
	internal static string? ExtractFilenameFromContentDisposition(string? header)
	{
		if (string.IsNullOrWhiteSpace(header))
		{
			return null;
		}

		var starMatch = ContentDispositionFilenameStarPattern().Match(header);
		if (starMatch.Success)
		{
			try
			{
				return Uri.UnescapeDataString(starMatch.Groups[1].Value);
			}
			catch (UriFormatException)
			{
				// fall through to the plain `filename=` parse
			}
		}

		var plainMatch = ContentDispositionFilenamePattern().Match(header);
		if (plainMatch.Success)
		{
			return plainMatch.Groups[1].Value;
		}

		return null;
	}

	/// <summary>
	/// Derives a (modelName, isZip) tuple from a Drive filename + file ID. The filename is
	/// preferred when present; otherwise the file ID is used as a placeholder name. The
	/// archive flag is true for .zip filenames and defaults to true when unknown (Drive
	/// RVC shares are almost always zips).
	/// </summary>
	internal static (string ModelName, bool IsZip) DeriveDriveNameAndType(string? fileName, string fileId)
	{
		if (!string.IsNullOrWhiteSpace(fileName))
		{
			var nameNoExt = Path.GetFileNameWithoutExtension(fileName);
			var isZip = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
			var cleaned = CleanModelName(nameNoExt);
			if (cleaned is not null && IsUsableName(cleaned))
			{
				return (cleaned, isZip);
			}
		}

		return ($"drive_{fileId}", IsZip: true);
	}

	[GeneratedRegex(@"/file/d/([A-Za-z0-9_-]+)")]
	private static partial Regex DriveFileIdPathPattern();

	[GeneratedRegex(@"[?&]id=([A-Za-z0-9_-]+)")]
	private static partial Regex DriveFileIdQueryPattern();

	[GeneratedRegex(@"<form[^>]+action=""([^""]+)""[^>]*>", RegexOptions.IgnoreCase)]
	private static partial Regex DriveConfirmFormPattern();

	[GeneratedRegex(@"<input[^>]+name=""([^""]+)""[^>]+value=""([^""]*)""", RegexOptions.IgnoreCase)]
	private static partial Regex DriveConfirmInputPattern();

	[GeneratedRegex(@"filename\*\s*=\s*UTF-8''([^;]+)", RegexOptions.IgnoreCase)]
	private static partial Regex ContentDispositionFilenameStarPattern();

	[GeneratedRegex(@"filename\s*=\s*""?([^"";]+)""?", RegexOptions.IgnoreCase)]
	private static partial Regex ContentDispositionFilenamePattern();

	// ── voice-models.com URL resolution ──

	/// <summary>
	/// Resolves a voice-models.com landing page to its hosted model download URL.
	/// </summary>
	[ExcludeFromCodeCoverage]
	public static async Task<(string FileUrl, string ModelName, bool IsZip, string[]? CompanionUrls)> ResolveVoiceModelsComModelAsync
	(
		HttpClient http,
		string url,
		CancellationToken cancellationToken
	)
	{
		using var response = await http.GetAsync
		(
			new Uri(url),
			cancellationToken
		).ConfigureAwait(false);

		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException
			(
				$"Failed to fetch voice-models.com page: HTTP {(int)response.StatusCode} from {url}"
			);
		}

		var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		var extractedHref = ExtractVoiceModelsDownloadLink(html);
		if (!IsUrl(extractedHref))
		{
			throw new InvalidOperationException
			(
				$"voice-models.com Download Link is not an HTTP(S) URL: {extractedHref}"
			);
		}

		Diagnostics.LogPerf($"[resolve] voice-models.com -> {extractedHref}");

		var extractedHost = new Uri(extractedHref).Host;
		try
		{
			var resolved = await ResolveModelUrlAsync(http, extractedHref, cancellationToken).ConfigureAwait(false);
			var shortName = ExtractVoiceModelsShortNameFromHtml(html);
			var chosenName = ChooseVoiceModelsName(resolved.ModelName, shortName);
			return (resolved.FileUrl, chosenName, resolved.IsZip, resolved.CompanionUrls);
		}
		catch (ArgumentException ex) when (ex.Message.StartsWith("Unsupported URL pattern:", StringComparison.Ordinal))
		{
			throw new NotSupportedException
			(
				$"voice-models.com Download Link host '{extractedHost}' is not supported yet. "
				+ "Paste a direct .onnx, .pth, or .zip file URL instead.",
				ex
			);
		}
	}

	/// <summary>
	/// Extracts the first Download Link anchor href from a voice-models.com HTML page.
	/// </summary>
	internal static string ExtractVoiceModelsDownloadLink(string html)
	{
		var match = VoiceModelsDownloadLinkPattern().Match(html);
		if (!match.Success)
		{
			throw new InvalidOperationException
			(
				"voice-models.com page has no Download Link."
			);
		}

		var rawHref = match.Groups[1].Success
			? match.Groups[1].Value
			: match.Groups[2].Success
				? match.Groups[2].Value
				: match.Groups[3].Value;

		return (WebUtility.HtmlDecode(rawHref) ?? rawHref).Trim();
	}

	/// <summary>
	/// Extracts a short display name from a voice-models.com title.
	/// Strips HTML tags and sanitizes for use as a filename, but otherwise
	/// keeps the title intact -- including epoch counts, RVC version, and
	/// character context (e.g. "Princess Peach (2007 - 2024) (Super Mario)
	/// [RVC v2] [300 Epochs]"). Useful metadata stays in the model name.
	/// </summary>
	internal static string ExtractVoiceModelsShortName(string h3Text)
	{
		var decoded = WebUtility.HtmlDecode(h3Text) ?? h3Text;
		var title = StripHtmlTagsPattern().Replace(decoded, string.Empty).Trim();
		if (title.Length == 0)
		{
			return title;
		}

		var sanitized = SanitizeFileName(title);
		return CleanModelName(sanitized) ?? sanitized;
	}

	/// <summary>
	/// Chooses the best display name for a voice-models.com-hosted model.
	/// Policy: the voice-models.com page title is the curated layer and wins
	/// whenever it's usable -- even when the underlying CDN URL gives back a
	/// "usable-looking" slug like the HuggingFace repo name
	/// <c>Princess-Peach-Samantha-Kelly</c> (which is the trainer's slug, not
	/// a human display name). The inner-resolver name is only used when the
	/// page title was missing or unusable.
	/// </summary>
	internal static string ChooseVoiceModelsName(string innerResolvedName, string? pageShortName)
	{
		if (pageShortName is not null && IsUsableName(pageShortName))
		{
			return pageShortName;
		}

		return innerResolvedName;
	}

	internal static string? ExtractVoiceModelsShortNameFromHtml(string html)
	{
		// voice-models.com always emits a <title> matching:
		//   "{Model Name} ({extra qualifiers}) AI Voice Model"
		// (mirrored in og:title). The trailer is the only definitive signal
		// that this is a model page rather than the site index, a 404, etc.
		// The first <h3> on the page is sidebar chrome like "Main / VM Models"
		// and must not be used as a fallback -- it would pollute the model
		// cache with names like "Main-VMModels".
		var titleMatch = HtmlTitlePattern().Match(html);
		if (!titleMatch.Success)
		{
			return null;
		}

		var (stripped, hadTrailer) = StripVoiceModelsTitleTrailer(titleMatch.Groups[1].Value);
		if (!hadTrailer)
		{
			return null;
		}

		var shortName = ExtractVoiceModelsShortName(stripped);
		return !string.IsNullOrWhiteSpace(shortName) && IsUsableName(shortName)
			? shortName
			: null;
	}

	/// <summary>
	/// Strips the trailing " AI Voice Model" suffix from a voice-models.com page
	/// title and reports whether the trailer was present. The trailer is the
	/// site's deterministic marker that this is a model page (vs. site index,
	/// 404, etc.), so callers should only trust the stripped title when
	/// <c>hadTrailer</c> is <c>true</c>.
	/// </summary>
	internal static (string Stripped, bool HadTrailer) StripVoiceModelsTitleTrailer(string title)
	{
		var decoded = (WebUtility.HtmlDecode(title) ?? title).Trim();
		const string trailer = " AI Voice Model";
		if (decoded.EndsWith(trailer, StringComparison.OrdinalIgnoreCase))
		{
			return (decoded[..^trailer.Length].TrimEnd(), HadTrailer: true);
		}

		return (decoded, HadTrailer: false);
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

		if
		(
			string.Equals(host, "voice-models.com", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(host, "www.voice-models.com", StringComparison.OrdinalIgnoreCase)
		)
		{
			return await ResolveVoiceModelsComModelAsync(http, url, cancellationToken).ConfigureAwait(false);
		}

		if
		(
			host.EndsWith("drive.google.com", StringComparison.OrdinalIgnoreCase)
			|| host.EndsWith("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase)
		)
		{
			return await ResolveGoogleDriveModelAsync(http, url, cancellationToken).ConfigureAwait(false);
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
	/// Gets the voice-models.com Download Link anchor regex.
	/// </summary>
	/// <returns>The generated regex.</returns>
	[GeneratedRegex(@"<b\b[^>]*>\s*Download\s+Link:\s*</b>\s*<a\b[^>]*\bhref\s*=\s*(?:""([^""]+)""|'([^']+)'|([^\s>]+))", RegexOptions.IgnoreCase)]
	private static partial Regex VoiceModelsDownloadLinkPattern();

	/// <summary>
	/// Gets the voice-models.com H3 title regex.
	/// </summary>
	/// <returns>The generated regex.</returns>
	[GeneratedRegex(@"<h3\b[^>]*>([\s\S]*?)</h3>", RegexOptions.IgnoreCase)]
	private static partial Regex VoiceModelsTitlePattern();

	/// <summary>
	/// Gets the HTML &lt;title&gt; element regex.
	/// </summary>
	/// <returns>The generated regex.</returns>
	[GeneratedRegex(@"<title\b[^>]*>([\s\S]*?)</title>", RegexOptions.IgnoreCase)]
	private static partial Regex HtmlTitlePattern();

	/// <summary>
	/// Gets the HTML tag stripping regex.
	/// </summary>
	/// <returns>The generated regex.</returns>
	[GeneratedRegex(@"<[^>]+>", RegexOptions.IgnoreCase)]
	private static partial Regex StripHtmlTagsPattern();

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

/// <summary>
/// Source-generated JSON context for URL map files (AOT-safe).
/// </summary>
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class UrlMapJsonContext : JsonSerializerContext
{
}
