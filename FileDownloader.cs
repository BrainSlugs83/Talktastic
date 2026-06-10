using System.Diagnostics;
using System.IO.Compression;
using System.Net;

namespace Talktastic;

/// <summary>
/// Downloads files or archives after resolving source-specific URLs.
/// </summary>
sealed class FileDownloader
{
	private const double ZipDownloadWeight = 0.75;
	private const double ZipExtractionWeight = 0.25;

	private readonly HttpClient _http;

	/// <summary>
	/// Initializes a new downloader that uses the specified HTTP client.
	/// </summary>
	/// <param name="http">The HTTP client used for probes and downloads.</param>
	public FileDownloader(HttpClient http)
	{
		_http = http;
	}

	/// <summary>
	/// URL resolvers to run before the built-in redirect resolver.
	/// </summary>
	public List<IUrlResolver> Resolvers { get; } = [];

	/// <summary>
	/// Filters which relative paths appear in <see cref="FileDownloadResult.Files"/>.
	/// </summary>
	public Predicate<string>? FileFilter { get; set; }

	/// <summary>
	/// Gets or sets whether filtered results can include files in subdirectories.
	/// </summary>
	public bool SearchRecursively { get; set; } = true;

	/// <summary>
	/// Gets or sets the buffer size used for download streams.
	/// </summary>
	public int BufferSize { get; set; } = 81920;

	/// <summary>
	/// Resolves a URL and probes its download metadata.
	/// </summary>
	/// <param name="inputUrl">The URL to resolve.</param>
	/// <param name="ct">The cancellation token.</param>
	/// <returns>The resolved URL and discovered metadata.</returns>
	public async Task<ResolvedUrl> ResolveAsync(string inputUrl, CancellationToken ct = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(inputUrl);

		var allResolvers = new List<IUrlResolver>(Resolvers);
		if (!allResolvers.Any(static resolver => resolver is HttpRedirectResolver))
		{
			allResolvers.Add(new HttpRedirectResolver());
		}

		var currentUrl = inputUrl;
		var intermediateUrls = new List<string>();
		var names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
		var companionUrls = new List<string>();
		var shouldProbeMetadata = true;
		var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			NormalizeResolverUrl(inputUrl),
		};

		const int MaxDepth = 10;

		for (var depth = 0; depth < MaxDepth; depth++)
		{
			var changed = false;

			foreach (var resolver in allResolvers)
			{
				ct.ThrowIfCancellationRequested();

				if (!resolver.CanResolve(currentUrl))
				{
					continue;
				}

				var result = await resolver.ResolveAsync(_http, currentUrl, ct).ConfigureAwait(false);

				if (!string.IsNullOrWhiteSpace(result.DisplayName))
				{
					if (names.TryGetValue(result.DisplayName, out var isPreferredName))
					{
						names[result.DisplayName] = isPreferredName || result.IsPreferredName;
					}
					else
					{
						names.Add(result.DisplayName, result.IsPreferredName);
					}
				}

				if (result.CompanionUrls is not null)
				{
					companionUrls.AddRange
					(
						result.CompanionUrls.Where(static url => !string.IsNullOrWhiteSpace(url))
					);
				}

				var normalizedCurrentUrl = NormalizeResolverUrl(currentUrl);
				var normalizedResultUrl = NormalizeResolverUrl(result.Url);

				if
				(
					string.Equals
					(
						normalizedResultUrl,
						normalizedCurrentUrl,
						StringComparison.OrdinalIgnoreCase
					)
				)
				{
					if (resolver is not HttpRedirectResolver)
					{
						shouldProbeMetadata = false;
						goto done;
					}

					continue;
				}

				if (!seenUrls.Add(normalizedResultUrl))
				{
					goto done;
				}

				intermediateUrls.Add(currentUrl);
				currentUrl = result.Url;
				changed = true;
				break;
			}

			if (!changed)
			{
				break;
			}
		}

done:
		var metadata = shouldProbeMetadata
			? await ProbeMetadataAsync(currentUrl, ct).ConfigureAwait(false)
			: new ProbeResult
			{
				FinalUrl = currentUrl,
			};
		var finalUrl = metadata.FinalUrl ?? currentUrl;
		var sourceType = ArchiveExtractor.IsArchive(finalUrl)
			? ResolvedUrlSourceType.Archive
			: ResolvedUrlSourceType.SingleFile;
		var uniqueNames = names
			.OrderByDescending(static pair => pair.Value)
			.ThenBy(static pair => pair.Key.Length)
			.ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
			.Select(static pair => pair.Key)
			.ToArray();
		var distinctCompanions = companionUrls
			.Where(url => !string.Equals(url, finalUrl, StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();

		return new ResolvedUrl
		{
			FinalUrl = finalUrl,
			FileName = GetResolvedFileName(finalUrl, metadata.FileName),
			ContentLength = metadata.ContentLength,
			ContentType = metadata.ContentType,
			SourceType = sourceType,
			IntermediateUrls = intermediateUrls,
			CompanionUrls = distinctCompanions.Length > 0 ? distinctCompanions : null,
			Names = uniqueNames,
		};
	}

	/// <summary>
	/// Downloads a resolved URL into the destination folder.
	/// </summary>
	/// <param name="resolved">The resolved URL to download.</param>
	/// <param name="destFolder">The destination folder.</param>
	/// <param name="progress">Receives progress updates during the operation.</param>
	/// <param name="ct">The cancellation token.</param>
	/// <returns>The downloaded files and aggregate transfer details.</returns>
	public async Task<FileDownloadResult> DownloadAsync
	(
		ResolvedUrl resolved,
		string destFolder,
		IProgress<FileDownloadProgress>? progress = null,
		CancellationToken ct = default
	)
	{
		ArgumentNullException.ThrowIfNull(resolved);
		ArgumentException.ThrowIfNullOrWhiteSpace(destFolder);

		Directory.CreateDirectory(destFolder);
		var stopwatch = Stopwatch.StartNew();

		try
		{
			var allFiles = new List<string>();
			long totalBytes;

			if (resolved.SourceType == ResolvedUrlSourceType.Archive)
			{
				totalBytes = await DownloadArchiveAsync
				(
					resolved,
					destFolder,
					allFiles,
					progress,
					stopwatch,
					ct
				).ConfigureAwait(false);
			}
			else
			{
				totalBytes = await DownloadSingleFileAsync
				(
					resolved,
					destFolder,
					allFiles,
					progress,
					stopwatch,
					ct
				).ConfigureAwait(false);
			}

			totalBytes += await DownloadCompanionFilesAsync
			(
				resolved.CompanionUrls,
				destFolder,
				allFiles,
				progress,
				stopwatch,
				ct
			).ConfigureAwait(false);

			var distinctAllFiles = allFiles
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
				.ToArray();
			var filteredFiles = FilterFiles(destFolder, distinctAllFiles);
			var result = new FileDownloadResult
			{
				Files = filteredFiles,
				AllFiles = distinctAllFiles,
				TotalBytes = totalBytes,
				Elapsed = stopwatch.Elapsed,
				LocalPath = destFolder,
			};

			progress?.Report
			(
				new FileDownloadProgress
				{
					Phase = DownloadPhase.Complete,
					BytesTransferred = result.TotalBytes,
					TotalBytes = result.TotalBytes,
					OverallPercent = 100,
					Elapsed = result.Elapsed,
					FilesCompleted = result.AllFiles.Count,
					CurrentFile = result.LocalPath,
				}
			);

			return result;
		}
		catch
		{
			progress?.Report
			(
				new FileDownloadProgress
				{
					Phase = DownloadPhase.Failed,
					Elapsed = stopwatch.Elapsed,
				}
			);

			throw;
		}
	}

	/// <summary>
	/// Resolves and downloads a URL in one call.
	/// </summary>
	/// <param name="inputUrl">The URL to resolve and download.</param>
	/// <param name="destFolder">The destination folder.</param>
	/// <param name="progress">Receives progress updates during the operation.</param>
	/// <param name="ct">The cancellation token.</param>
	/// <returns>The downloaded files and aggregate transfer details.</returns>
	public async Task<FileDownloadResult> DownloadAsync
	(
		string inputUrl,
		string destFolder,
		IProgress<FileDownloadProgress>? progress = null,
		CancellationToken ct = default
	)
	{
		progress?.Report
		(
			new FileDownloadProgress
			{
				Phase = DownloadPhase.Resolving,
			}
		);

		var resolved = await ResolveAsync(inputUrl, ct).ConfigureAwait(false);
		return await DownloadAsync(resolved, destFolder, progress, ct).ConfigureAwait(false);
	}

	private string[] FilterFiles(string rootPath, IReadOnlyList<string> allFiles)
	{
		return allFiles
			.Where
			(
				path =>
				{
					var relativePath = Path.GetRelativePath(rootPath, path);
					if (!this.SearchOptionMatches(relativePath))
					{
						return false;
					}

					return this.FileFilterMatches(relativePath);
				}
			)
			.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static bool HasDirectoryComponent(string relativePath)
	{
		return relativePath.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
			|| relativePath.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
	}

	private static string NormalizeResolverUrl(string url)
	{
		if (Uri.TryCreate(url, UriKind.Absolute, out _))
		{
			return ModelDownloader.NormalizeUrl(url);
		}

		return url.Trim().TrimEnd('/', '\\');
	}

	private static string GetResolvedFileName(string url, string? preferredFileName)
	{
		if (!string.IsNullOrWhiteSpace(preferredFileName))
		{
			var preferredName = Path.GetFileName(preferredFileName);
			var sanitizedPreferredName = ModelDownloader.SanitizeFileName(preferredName);
			if (!string.IsNullOrWhiteSpace(sanitizedPreferredName))
			{
				return sanitizedPreferredName;
			}
		}

		if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
		{
			var candidate = Uri.UnescapeDataString(Path.GetFileName(uri.LocalPath));
			var sanitizedCandidate = ModelDownloader.SanitizeFileName(candidate);
			if (!string.IsNullOrWhiteSpace(sanitizedCandidate))
			{
				return sanitizedCandidate;
			}
		}

		return "download";
	}

	private async Task<ProbeResult> ProbeMetadataAsync(string url, CancellationToken ct)
	{
		using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
		using var headResponse = await SendProbeRequestAsync(headRequest, ct).ConfigureAwait(false);
		if (headResponse.StatusCode == HttpStatusCode.MethodNotAllowed)
		{
			using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
			using var getResponse = await SendProbeRequestAsync(getRequest, ct).ConfigureAwait(false);
			return CreateProbeResult(url, getResponse);
		}

		return CreateProbeResult(url, headResponse);
	}

	private static ProbeResult CreateProbeResult(string originalUrl, HttpResponseMessage response)
	{
		var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? originalUrl;
		var fileName = ModelDownloader.ExtractFilenameFromContentDisposition
		(
			response.Content.Headers.ContentDisposition?.ToString()
		);

		return new ProbeResult
		{
			FinalUrl = finalUrl,
			FileName = fileName,
			ContentLength = response.Content.Headers.ContentLength,
			ContentType = response.Content.Headers.ContentType?.MediaType,
		};
	}

	private Task<HttpResponseMessage> SendProbeRequestAsync
	(
		HttpRequestMessage request,
		CancellationToken ct
	)
	{
		return request.RequestUri is null
			? Task.FromException<HttpResponseMessage>(new InvalidOperationException("Request URI is required."))
			: _http.SendAsync
			(
				request,
				HttpCompletionOption.ResponseHeadersRead,
				ct
			);
	}

	private async Task<long> DownloadSingleFileAsync
	(
		ResolvedUrl resolved,
		string destFolder,
		List<string> allFiles,
		IProgress<FileDownloadProgress>? progress,
		Stopwatch stopwatch,
		CancellationToken ct
	)
	{
		var fileName = GetResolvedFileName(resolved.FinalUrl, resolved.FileName);
		var destPath = Path.Combine(destFolder, fileName);
		ReportPhaseTransition
		(
			progress,
			DownloadPhase.Downloading,
			stopwatch,
			0,
			resolved.ContentLength,
			0,
			currentFile: fileName
		);

		var bytesDownloaded = await DownloadFileInternalAsync
		(
			resolved.FinalUrl,
			destPath,
			resolved.ContentLength,
			progress,
			stopwatch,
			currentFile: fileName,
			ct: ct
		).ConfigureAwait(false);
		allFiles.Add(destPath);
		return bytesDownloaded;
	}

	private async Task<long> DownloadArchiveAsync
	(
		ResolvedUrl resolved,
		string destFolder,
		List<string> allFiles,
		IProgress<FileDownloadProgress>? progress,
		Stopwatch stopwatch,
		CancellationToken ct
	)
	{
		var format = ArchiveExtractor.GetArchiveFormat(StripUrlSuffix(resolved.FinalUrl));
		if (format is not (ArchiveExtractor.ArchiveFormat.Unknown or ArchiveExtractor.ArchiveFormat.Zip))
		{
			return await DownloadStreamableArchiveAsync
			(
				resolved,
				format,
				destFolder,
				allFiles,
				progress,
				stopwatch,
				ct
			).ConfigureAwait(false);
		}

		var tempArchive = Path.Combine
		(
			destFolder,
			$"download-{Guid.NewGuid():N}{ModelDownloader.GetArchiveExtensionPublic(resolved.FinalUrl)}"
		);
		var tempArchiveName = Path.GetFileName(tempArchive);

		try
		{
			ReportPhaseTransition
			(
				progress,
				DownloadPhase.Downloading,
				stopwatch,
				0,
				resolved.ContentLength,
				0,
				currentFile: tempArchiveName
			);

			var bytesDownloaded = await DownloadFileInternalAsync
			(
				resolved.FinalUrl,
				tempArchive,
				resolved.ContentLength,
				progress,
				stopwatch,
				currentFile: tempArchiveName,
				overallPercentScale: ZipDownloadWeight,
				ct: ct
			).ConfigureAwait(false);

			var extractedFiles = await ExtractZipArchiveAsync
			(
				format,
				tempArchive,
				destFolder,
				progress,
				stopwatch,
				bytesDownloaded,
				ct
			).ConfigureAwait(false);
			allFiles.AddRange
			(
				extractedFiles.Where
				(
					path => !string.Equals(path, tempArchive, StringComparison.OrdinalIgnoreCase)
				)
			);
			return bytesDownloaded;
		}
		finally
		{
			TryDeleteFile(tempArchive);
		}
	}

	private async Task<long> DownloadStreamableArchiveAsync
	(
		ResolvedUrl resolved,
		ArchiveExtractor.ArchiveFormat format,
		string destFolder,
		List<string> allFiles,
		IProgress<FileDownloadProgress>? progress,
		Stopwatch stopwatch,
		CancellationToken ct
	)
	{
		var archiveName = GetResolvedFileName(resolved.FinalUrl, resolved.FileName);
		using var response = await SendDownloadRequestAsync(resolved.FinalUrl, ct).ConfigureAwait(false);
		EnsureSuccessStatusCode(resolved.FinalUrl, response);

		var contentLength = response.Content.Headers.ContentLength;
		ReportPhaseTransition
		(
			progress,
			DownloadPhase.Streaming,
			stopwatch,
			0,
			contentLength,
			0,
			currentFile: archiveName
		);

		var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
		var trackingStream = new ProgressTrackingReadStream
		(
			responseStream,
			contentLength,
			progress,
			stopwatch,
			DownloadPhase.Streaming,
			archiveName
		);
		await using var trackingStreamDispose = trackingStream.ConfigureAwait(false);
		var extractedFiles = await ArchiveExtractor.ExtractAsync(trackingStream, format, destFolder, ct).ConfigureAwait(false);
		trackingStream.ReportFinal(filesCompleted: extractedFiles.Length);
		allFiles.AddRange(extractedFiles);
		LogDownloadCompletion(archiveName, trackingStream.BytesTransferred, stopwatch.Elapsed);
		return trackingStream.BytesTransferred;
	}

	private async Task<string[]> ExtractZipArchiveAsync
	(
		ArchiveExtractor.ArchiveFormat format,
		string archivePath,
		string destFolder,
		IProgress<FileDownloadProgress>? progress,
		Stopwatch stopwatch,
		long bytesDownloaded,
		CancellationToken ct
	)
	{
		if (format != ArchiveExtractor.ArchiveFormat.Zip)
		{
			ReportPhaseTransition
			(
				progress,
				DownloadPhase.Extracting,
				stopwatch,
				bytesDownloaded,
				bytesDownloaded,
				ZipDownloadWeight * 100,
				currentFile: Path.GetFileName(archivePath)
			);

			return await ArchiveExtractor.ExtractAsync(archivePath, destFolder, ct).ConfigureAwait(false);
		}

		using var archive = await ZipFile.OpenReadAsync(archivePath, ct).ConfigureAwait(false);
		var fileEntries = archive.Entries
			.Where(static entry => !string.IsNullOrEmpty(entry.Name))
			.ToArray();
		var totalEntries = fileEntries.Length;
		ReportPhaseTransition
		(
			progress,
			DownloadPhase.Extracting,
			stopwatch,
			bytesDownloaded,
			bytesDownloaded,
			ZipDownloadWeight * 100,
			currentFile: Path.GetFileName(archivePath),
			entryCount: totalEntries
		);

		var extractedFiles = new List<string>(totalEntries);
		var destinationRoot = EnsureTrailingDirectorySeparator(Path.GetFullPath(destFolder));
		var filesCompleted = 0;

		foreach (var entry in fileEntries)
		{
			ct.ThrowIfCancellationRequested();

			var destPath = GetSafeZipDestinationPath(destinationRoot, entry.FullName);
			var destSubDir = Path.GetDirectoryName(destPath);
			if (!string.IsNullOrEmpty(destSubDir))
			{
				Directory.CreateDirectory(destSubDir);
			}

			var source = await entry.OpenAsync(ct).ConfigureAwait(false);
			await using var sourceDispose = source.ConfigureAwait(false);
			var destination = new FileStream
			(
				destPath,
				new FileStreamOptions
				{
					Mode = FileMode.Create,
					Access = FileAccess.Write,
					Share = FileShare.None,
					BufferSize = BufferSize,
					Options = FileOptions.Asynchronous,
				}
			);
			await using var destinationDispose = destination.ConfigureAwait(false);
			await source.CopyToAsync(destination, ct).ConfigureAwait(false);

			extractedFiles.Add(destPath);
			filesCompleted++;
			var extractionPercent = totalEntries == 0
				? 100
				: (double)filesCompleted / totalEntries * 100;
			progress?.Report
			(
				new FileDownloadProgress
				{
					Phase = DownloadPhase.Extracting,
					BytesTransferred = bytesDownloaded,
					TotalBytes = bytesDownloaded,
					OverallPercent = (ZipDownloadWeight * 100) + (extractionPercent * ZipExtractionWeight),
					Elapsed = stopwatch.Elapsed,
					CurrentFile = destPath,
					FilesCompleted = filesCompleted,
					EntryCount = totalEntries,
				}
			);
		}

		return [.. extractedFiles];
	}

	private async Task<long> DownloadCompanionFilesAsync
	(
		IReadOnlyList<string>? companionUrls,
		string destFolder,
		List<string> allFiles,
		IProgress<FileDownloadProgress>? progress,
		Stopwatch stopwatch,
		CancellationToken ct
	)
	{
		if (companionUrls is null || companionUrls.Count == 0)
		{
			return 0;
		}

		long totalBytes = 0;
		var writtenPaths = new HashSet<string>(allFiles, StringComparer.OrdinalIgnoreCase);

		foreach (var companionUrl in companionUrls.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			ct.ThrowIfCancellationRequested();

			var fileName = GetCompanionFileName(companionUrl);
			var destPath = Path.Combine(destFolder, fileName);
			if (!writtenPaths.Add(destPath))
			{
				continue;
			}

			var metadata = await ProbeMetadataAsync(companionUrl, ct).ConfigureAwait(false);
			var contentLength = metadata.ContentLength;
			ReportPhaseTransition
			(
				progress,
				DownloadPhase.Downloading,
				stopwatch,
				0,
				contentLength,
				0,
				currentFile: fileName,
				filesCompleted: allFiles.Count
			);

			var bytesDownloaded = await DownloadFileInternalAsync
			(
				companionUrl,
				destPath,
				contentLength,
				progress,
				stopwatch,
				currentFile: fileName,
				filesCompleted: allFiles.Count,
				ct: ct
			).ConfigureAwait(false);
			allFiles.Add(destPath);
			totalBytes += bytesDownloaded;
		}

		return totalBytes;
	}

	private async Task<long> DownloadFileInternalAsync
	(
		string url,
		string destPath,
		long? totalBytes,
		IProgress<FileDownloadProgress>? progress,
		Stopwatch stopwatch,
		string? currentFile,
		double overallPercentScale = 1,
		double overallPercentOffset = 0,
		int filesCompleted = 0,
		CancellationToken ct = default
	)
	{
		using var response = await SendDownloadRequestAsync(url, ct).ConfigureAwait(false);
		EnsureSuccessStatusCode(url, response);

		var contentLength = response.Content.Headers.ContentLength ?? totalBytes;
		var tempPath = destPath + ".tmp";
		try
		{
			long bytesWritten;
			{
				var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
				await using var sourceDispose = source.ConfigureAwait(false);
				var destination = new FileStream
				(
					tempPath,
					new FileStreamOptions
					{
						Mode = FileMode.Create,
						Access = FileAccess.Write,
						Share = FileShare.None,
						BufferSize = BufferSize,
						Options = FileOptions.Asynchronous,
					}
				);
				await using var destinationDispose = destination.ConfigureAwait(false);
				bytesWritten = await DownloadWithProgressAsync
				(
					source,
					destination,
					contentLength,
					CreateTransferProgress
					(
						progress,
						stopwatch,
						DownloadPhase.Downloading,
						currentFile,
						overallPercentScale,
						overallPercentOffset,
						filesCompleted,
						null
					),
					stopwatch,
					ct
				).ConfigureAwait(false);

				await destination.FlushAsync(ct).ConfigureAwait(false);
			}

			File.Move(tempPath, destPath, overwrite: true);
			LogDownloadCompletion(currentFile ?? Path.GetFileName(destPath), bytesWritten, stopwatch.Elapsed);
			return bytesWritten;
		}
		catch (IOException)
		{
			TryDeleteFile(tempPath);
			throw;
		}
	}

	private async Task<long> DownloadWithProgressAsync
	(
		Stream source,
		Stream destination,
		long? totalBytes,
		IProgress<FileDownloadProgress>? progress,
		Stopwatch stopwatch,
		CancellationToken ct
	)
	{
		var buffer = new byte[BufferSize];
		long bytesTransferred = 0;
		double smoothedRate = 0;
		const double Alpha = 0.3;
		var lastReportTime = stopwatch.Elapsed;
		var lastReportBytes = 0L;

		int bytesRead;
		while ((bytesRead = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
		{
			await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
			bytesTransferred += bytesRead;

			var now = stopwatch.Elapsed;
			if (progress is not null && (now - lastReportTime).TotalMilliseconds >= 100)
			{
				var intervalBytes = bytesTransferred - lastReportBytes;
				var intervalSeconds = (now - lastReportTime).TotalSeconds;
				var instantRate = intervalSeconds > 0 ? intervalBytes / intervalSeconds : 0;
				smoothedRate = smoothedRate == 0 ? instantRate : (Alpha * instantRate) + ((1 - Alpha) * smoothedRate);

				var percent = totalBytes > 0 ? (double)bytesTransferred / totalBytes.Value * 100 : (double?)null;
				var eta = smoothedRate > 0 && totalBytes > 0
					? TimeSpan.FromSeconds((totalBytes.Value - bytesTransferred) / smoothedRate)
					: (TimeSpan?)null;

				progress.Report
				(
					new FileDownloadProgress
					{
						Phase = DownloadPhase.Downloading,
						BytesTransferred = bytesTransferred,
						TotalBytes = totalBytes,
						OverallPercent = percent,
						Elapsed = now,
						TransferRate = smoothedRate,
						Eta = eta,
					}
				);

				lastReportTime = now;
				lastReportBytes = bytesTransferred;
			}
		}

		if (progress is not null)
		{
			var now = stopwatch.Elapsed;
			var intervalBytes = bytesTransferred - lastReportBytes;
			var intervalSeconds = (now - lastReportTime).TotalSeconds;
			var instantRate = intervalSeconds > 0 ? intervalBytes / intervalSeconds : 0;
			smoothedRate = smoothedRate == 0 ? instantRate : (Alpha * instantRate) + ((1 - Alpha) * smoothedRate);
			var percent = totalBytes > 0 ? (double)bytesTransferred / totalBytes.Value * 100 : (double?)null;
			progress.Report
			(
				new FileDownloadProgress
				{
					Phase = DownloadPhase.Downloading,
					BytesTransferred = bytesTransferred,
					TotalBytes = totalBytes,
					OverallPercent = percent,
					Elapsed = now,
					TransferRate = smoothedRate,
					Eta = TimeSpan.Zero,
				}
			);
		}

		return bytesTransferred;
	}

	private static DelegatingProgress<FileDownloadProgress>? CreateTransferProgress
	(
		IProgress<FileDownloadProgress>? progress,
		Stopwatch stopwatch,
		DownloadPhase phase,
		string? currentFile,
		double overallPercentScale,
		double overallPercentOffset,
		int filesCompleted,
		int? entryCount
	)
	{
		if (progress is null)
		{
			return null;
		}

		return new DelegatingProgress<FileDownloadProgress>
		(
			update =>
			{
				progress.Report
				(
					new FileDownloadProgress
					{
						Phase = phase,
						BytesTransferred = update.BytesTransferred,
						TotalBytes = update.TotalBytes,
						OverallPercent = update.OverallPercent is { } percent
							? overallPercentOffset + (percent * overallPercentScale)
							: null,
						Elapsed = update.Elapsed == default ? stopwatch.Elapsed : update.Elapsed,
						TransferRate = update.TransferRate,
						Eta = update.Eta,
						CurrentFile = currentFile,
						FilesCompleted = filesCompleted,
						EntryCount = entryCount,
					}
				);
			}
		);
	}

	private static HttpResponseMessage EnsureSuccessStatusCode(string url, HttpResponseMessage response)
	{
		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException($"Failed to download {url}: HTTP {(int)response.StatusCode}");
		}

		return response;
	}

	private async Task<HttpResponseMessage> SendDownloadRequestAsync(string url, CancellationToken ct)
	{
		try
		{
			return await _http.GetAsync
			(
				new Uri(url),
				HttpCompletionOption.ResponseHeadersRead,
				ct
			).ConfigureAwait(false);
		}
		catch (HttpRequestException ex)
		{
			throw new InvalidOperationException
			(
				$"Failed to download {url}: {ModelDownloader.DescribeNetworkFailure(url, ex)}",
				ex
			);
		}
	}

	private static void ReportPhaseTransition
	(
		IProgress<FileDownloadProgress>? progress,
		DownloadPhase phase,
		Stopwatch stopwatch,
		long bytesTransferred,
		long? totalBytes,
		double? overallPercent,
		string? currentFile = null,
		int filesCompleted = 0,
		int? entryCount = null
	)
	{
		progress?.Report
		(
			new FileDownloadProgress
			{
				Phase = phase,
				BytesTransferred = bytesTransferred,
				TotalBytes = totalBytes,
				OverallPercent = overallPercent,
				Elapsed = stopwatch.Elapsed,
				CurrentFile = currentFile,
				FilesCompleted = filesCompleted,
				EntryCount = entryCount,
			}
		);
	}

	private static string GetSafeZipDestinationPath(string destinationRoot, string entryFullName)
	{
		var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, entryFullName));
		if (!destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException($"Archive entry escapes destination directory: {entryFullName}");
		}

		return destinationPath;
	}

	private static string EnsureTrailingDirectorySeparator(string path)
	{
		return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
			? path
			: path + Path.DirectorySeparatorChar;
	}

	private static string StripUrlSuffix(string url)
	{
		var queryIndex = url.IndexOf('?', StringComparison.Ordinal);
		var fragmentIndex = url.IndexOf('#', StringComparison.Ordinal);
		var endIndex = queryIndex switch
		{
			< 0 => fragmentIndex,
			_ when fragmentIndex < 0 => queryIndex,
			_ => Math.Min(queryIndex, fragmentIndex),
		};

		return endIndex < 0 ? url : url[..endIndex];
	}

	private static void TryDeleteFile(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch (IOException)
		{
		}
	}

	private static void LogDownloadCompletion(string fileName, long bytesTransferred, TimeSpan elapsed)
	{
		var elapsedMs = (long)elapsed.TotalMilliseconds;
		var rateBytesPerSecond = elapsed.TotalSeconds > 0
			? (long)Math.Round(bytesTransferred / elapsed.TotalSeconds, MidpointRounding.AwayFromZero)
			: 0;
		Diagnostics.LogPerf
		(
			$"[download] {fileName}: {bytesTransferred.ToHumanReadableFileSize(true)} in {elapsedMs}ms ({rateBytesPerSecond.ToHumanReadableFileSize(true)}/s)"
		);
	}

	private static string GetCompanionFileName(string companionUrl)
	{
		if (Uri.TryCreate(companionUrl, UriKind.Absolute, out var uri))
		{
			var fileName = Uri.UnescapeDataString(Path.GetFileName(uri.LocalPath));
			var sanitizedFileName = ModelDownloader.SanitizeFileName(fileName);
			if (!string.IsNullOrWhiteSpace(sanitizedFileName))
			{
				return sanitizedFileName;
			}
		}

		return $"companion-{Guid.NewGuid():N}";
	}

	private bool SearchOptionMatches(string relativePath)
	{
		return SearchRecursively || !HasDirectoryComponent(relativePath);
	}

	private bool FileFilterMatches(string relativePath)
	{
		return FileFilter is null || FileFilter(relativePath);
	}

	/// <summary>
	/// Metadata discovered during a probe request.
	/// </summary>
	private sealed record ProbeResult
	{
		public string? FinalUrl { get; init; }

		public string? FileName { get; init; }

		public long? ContentLength { get; init; }

		public string? ContentType { get; init; }
	}

	/// <summary>
	/// Adapts a delegate to <see cref="IProgress{T}"/>.
	/// </summary>
	private sealed class DelegatingProgress<T>(Action<T> report) : IProgress<T>
	{
		private readonly Action<T> _report = report;

		public void Report(T value)
		{
			_report(value);
		}
	}

	/// <summary>
	/// Wraps a readable stream and reports transfer progress.
	/// </summary>
	private sealed class ProgressTrackingReadStream : Stream
	{
		private const double Alpha = 0.3;

		private readonly Stream _inner;
		private readonly bool _leaveOpen;
		private readonly IProgress<FileDownloadProgress>? _progress;
		private readonly Stopwatch _stopwatch;
		private readonly DownloadPhase _phase;
		private readonly string? _currentFile;
		private readonly long? _totalBytes;
		private TimeSpan _lastReportTime;
		private long _lastReportBytes;
		private double _smoothedRate;

		public ProgressTrackingReadStream
		(
			Stream inner,
			long? totalBytes,
			IProgress<FileDownloadProgress>? progress,
			Stopwatch stopwatch,
			DownloadPhase phase,
			string? currentFile,
			bool leaveOpen = false
		)
		{
			_inner = inner;
			_totalBytes = totalBytes;
			_progress = progress;
			_stopwatch = stopwatch;
			_phase = phase;
			_currentFile = currentFile;
			_leaveOpen = leaveOpen;
			_lastReportTime = stopwatch.Elapsed;
		}

		public long BytesTransferred { get; private set; }

		public override bool CanRead => _inner.CanRead;

		public override bool CanSeek => _inner.CanSeek;

		public override bool CanWrite => false;

		public override long Length => _inner.Length;

		public override long Position
		{
			get => _inner.Position;
			set => _inner.Position = value;
		}

		public override void Flush()
		{
			_inner.Flush();
		}

		public override Task FlushAsync(CancellationToken cancellationToken)
		{
			return _inner.FlushAsync(cancellationToken);
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			var bytesRead = _inner.Read(buffer, offset, count);
			OnBytesRead(bytesRead, force: false);
			return bytesRead;
		}

		public override int Read(Span<byte> buffer)
		{
			var bytesRead = _inner.Read(buffer);
			OnBytesRead(bytesRead, force: false);
			return bytesRead;
		}

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			var bytesRead = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
			OnBytesRead(bytesRead, force: false);
			return bytesRead;
		}

		public override async Task<int> ReadAsync
		(
			byte[] buffer,
			int offset,
			int count,
			CancellationToken cancellationToken
		)
		{
			var bytesRead = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
			OnBytesRead(bytesRead, force: false);
			return bytesRead;
		}

		public void ReportFinal(int filesCompleted = 0, int? entryCount = null)
		{
			OnBytesRead(0, force: true, filesCompleted, entryCount);
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			return _inner.Seek(offset, origin);
		}

		public override void SetLength(long value)
		{
			_inner.SetLength(value);
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}

		public override void Write(ReadOnlySpan<byte> buffer)
		{
			throw new NotSupportedException();
		}

		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
		{
			throw new NotSupportedException();
		}

		public override Task WriteAsync
		(
			byte[] buffer,
			int offset,
			int count,
			CancellationToken cancellationToken
		)
		{
			throw new NotSupportedException();
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing && !_leaveOpen)
			{
				_inner.Dispose();
			}

			base.Dispose(disposing);
		}

		public override async ValueTask DisposeAsync()
		{
			if (!_leaveOpen)
			{
				await _inner.DisposeAsync().ConfigureAwait(false);
			}

			await base.DisposeAsync().ConfigureAwait(false);
		}

		private void OnBytesRead(int bytesRead, bool force, int filesCompleted = 0, int? entryCount = null)
		{
			if (bytesRead > 0)
			{
				BytesTransferred += bytesRead;
			}

			if (_progress is null)
			{
				return;
			}

			var now = _stopwatch.Elapsed;
			if (!force && (now - _lastReportTime).TotalMilliseconds < 100)
			{
				return;
			}

			var intervalBytes = BytesTransferred - _lastReportBytes;
			var intervalSeconds = (now - _lastReportTime).TotalSeconds;
			var instantRate = intervalSeconds > 0 ? intervalBytes / intervalSeconds : 0;
			_smoothedRate = _smoothedRate == 0
				? instantRate
				: (Alpha * instantRate) + ((1 - Alpha) * _smoothedRate);

			double? overallPercent = null;
			TimeSpan? eta = null;
			if (_totalBytes > 0)
			{
				overallPercent = (double)BytesTransferred / _totalBytes.Value * 100;
				if (_smoothedRate > 0)
				{
					eta = TimeSpan.FromSeconds((_totalBytes.Value - BytesTransferred) / _smoothedRate);
				}
			}

			_progress.Report
			(
				new FileDownloadProgress
				{
					Phase = _phase,
					BytesTransferred = BytesTransferred,
					TotalBytes = _totalBytes,
					OverallPercent = overallPercent,
					Elapsed = now,
					TransferRate = _smoothedRate,
					Eta = eta,
					CurrentFile = _currentFile,
					FilesCompleted = filesCompleted,
					EntryCount = entryCount,
				}
			);

			_lastReportTime = now;
			_lastReportBytes = BytesTransferred;
		}
	}
}
