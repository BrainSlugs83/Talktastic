using System.Diagnostics.CodeAnalysis;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;

namespace Talktastic.Tests;

public sealed class FileDownloaderTests : IDisposable
{
	private readonly string _artifactRoot;

	public FileDownloaderTests()
	{
		_artifactRoot = Path.Combine
		(
			AppContext.BaseDirectory,
			"TestArtifacts",
			nameof(FileDownloaderTests),
			Guid.NewGuid().ToString("N")
		);

		Directory.CreateDirectory(_artifactRoot);
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_artifactRoot))
			{
				Directory.Delete(_artifactRoot, recursive: true);
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task ResolveAsync_NoResolvers_ReturnsSameUrlAndNoIntermediateUrls()
	{
		const string url = "https://example.test/model.onnx";
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, "model"u8.ToArray())
		);
		var downloader = new FileDownloader(http);

		var resolved = await downloader.ResolveAsync(url);

		Assert.Equal(url, resolved.FinalUrl);
		Assert.Equal(ResolvedUrlSourceType.SingleFile, resolved.SourceType);
		Assert.Empty(resolved.IntermediateUrls);
		Assert.Empty(resolved.Names);
		Assert.Null(resolved.CompanionUrls);
	}

	[Fact]
	public async Task ResolveAsync_SingleResolverTransform_ReturnsResolvedUrl()
	{
		const string inputUrl = "https://example.test/input";
		const string outputUrl = "https://example.test/output.onnx";
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, "payload"u8.ToArray())
		);
		var downloader = new FileDownloader(http);
		downloader.Resolvers.Add(new TestResolver((inputUrl, outputUrl, null)));

		var resolved = await downloader.ResolveAsync(inputUrl);

		Assert.Equal(outputUrl, resolved.FinalUrl);
		Assert.Equal([inputUrl], resolved.IntermediateUrls);
	}

	[Fact]
	public async Task ResolveAsync_ChainedResolvers_ReturnsFinalUrlAndIntermediates()
	{
		const string inputUrl = "https://example.test/A";
		const string secondUrl = "https://example.test/B";
		const string finalUrl = "https://example.test/C.onnx";
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, "payload"u8.ToArray())
		);
		var downloader = new FileDownloader(http);
		downloader.Resolvers.Add(new TestResolver((inputUrl, secondUrl, null)));
		downloader.Resolvers.Add(new TestResolver((secondUrl, finalUrl, null)));

		var resolved = await downloader.ResolveAsync(inputUrl);

		Assert.Equal(finalUrl, resolved.FinalUrl);
		Assert.Equal([inputUrl, secondUrl], resolved.IntermediateUrls);
	}

	[Fact]
	public async Task ResolveAsync_CycleDetection_StopsAtLastUniqueUrl()
	{
		const string inputUrl = "https://example.test/A";
		const string secondUrl = "https://example.test/B.onnx";
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, "payload"u8.ToArray())
		);
		var downloader = new FileDownloader(http);
		downloader.Resolvers.Add
		(
			new TestResolver
			(
				(inputUrl, secondUrl, null),
				(secondUrl, inputUrl, null)
			)
		);

		var resolved = await downloader.ResolveAsync(inputUrl);

		Assert.Equal(secondUrl, resolved.FinalUrl);
		Assert.Equal([inputUrl], resolved.IntermediateUrls);
	}

	[Fact]
	public async Task ResolveAsync_MaxDepth_StopsAfterTenIterations()
	{
		const string inputUrl = "https://example.test/A0";
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, "payload"u8.ToArray())
		);
		var downloader = new FileDownloader(http);
		downloader.Resolvers.Add(new TestResolver(CreateMaxDepthMappings()));

		var resolved = await downloader.ResolveAsync(inputUrl);

		Assert.Equal("https://example.test/A10.onnx", resolved.FinalUrl);
		Assert.Equal
		(
			[
				"https://example.test/A0",
				"https://example.test/A1",
				"https://example.test/A2",
				"https://example.test/A3",
				"https://example.test/A4",
				"https://example.test/A5",
				"https://example.test/A6",
				"https://example.test/A7",
				"https://example.test/A8",
				"https://example.test/A9",
			],
			resolved.IntermediateUrls
		);
	}

	[Fact]
	public async Task ResolveAsync_CollectsNames_SortsByLengthAndDeduplicates()
	{
		const string inputUrl = "https://example.test/A";
		const string secondUrl = "https://example.test/B";
		const string thirdUrl = "https://example.test/C";
		const string finalUrl = "https://example.test/D.onnx";
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, "payload"u8.ToArray())
		);
		var downloader = new FileDownloader(http);
		downloader.Resolvers.Add
		(
			new TestResolver
			(
				(inputUrl, secondUrl, "Banana"),
				(secondUrl, thirdUrl, "fig"),
				(thirdUrl, finalUrl, "FIG")
			)
		);

		var resolved = await downloader.ResolveAsync(inputUrl);

		Assert.Equal(["fig", "Banana"], resolved.Names);
	}

	[Fact]
	public async Task ResolveAsync_CollectsCompanionUrlsAcrossResolvers()
	{
		const string inputUrl = "https://example.test/start";
		const string secondUrl = "https://example.test/next";
		const string finalUrl = "https://example.test/model.onnx";
		const string configUrl = "https://example.test/model.onnx.json";
		const string tokensUrl = "https://example.test/tokens.txt";
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, "payload"u8.ToArray())
		);
		var downloader = new FileDownloader(http);
		downloader.Resolvers.Add
		(
			new TestResolver
			(
				(inputUrl, new UrlResolverResult(secondUrl, null, [configUrl, finalUrl])),
				(secondUrl, new UrlResolverResult(finalUrl, null, [tokensUrl, configUrl]))
			)
		);

		var resolved = await downloader.ResolveAsync(inputUrl);

		Assert.Equal([configUrl, tokensUrl], resolved.CompanionUrls);
	}

	[Theory]
	[InlineData("https://example.test/model.zip")]
	[InlineData("https://example.test/model.tar.gz")]
	public async Task ResolveAsync_ArchiveUrl_ReturnsArchiveSourceType(string url)
	{
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, "archive"u8.ToArray())
		);
		var downloader = new FileDownloader(http);

		var resolved = await downloader.ResolveAsync(url);

		Assert.Equal(ResolvedUrlSourceType.Archive, resolved.SourceType);
	}

	[Fact]
	public async Task ResolveAsync_NonArchiveUrl_ReturnsSingleFileSourceType()
	{
		const string url = "https://example.test/model.bin";
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, "binary"u8.ToArray())
		);
		var downloader = new FileDownloader(http);

		var resolved = await downloader.ResolveAsync(url);

		Assert.Equal(ResolvedUrlSourceType.SingleFile, resolved.SourceType);
	}

	[Fact]
	public async Task DownloadAsync_SingleFile_WritesFileAndReturnsResult()
	{
		const string url = "https://example.test/model.onnx";
		var content = Encoding.UTF8.GetBytes("test content");
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, content)
		);
		var downloader = new FileDownloader(http);
		var resolved = new ResolvedUrl
		{
			FinalUrl = url,
			FileName = "voice.onnx",
			ContentLength = content.Length,
			SourceType = ResolvedUrlSourceType.SingleFile,
		};
		var destFolder = CreateArtifactDirectory(nameof(DownloadAsync_SingleFile_WritesFileAndReturnsResult));

		var result = await downloader.DownloadAsync(resolved, destFolder);

		var downloadedFile = Path.Combine(destFolder, "voice.onnx");
		Assert.Single(result.Files);
		Assert.Single(result.AllFiles);
		Assert.Equal(downloadedFile, result.Files[0]);
		Assert.True(File.Exists(downloadedFile));
		Assert.Equal("test content", await File.ReadAllTextAsync(downloadedFile));
		Assert.Equal(content.Length, result.TotalBytes);
		Assert.Equal(destFolder, result.LocalPath);
	}

	[Fact]
	public async Task DownloadAsync_Archive_ExtractsFilesAndDeletesTempArchive()
	{
		const string url = "https://example.test/archive.zip";
		var archiveBytes = CreateZipArchiveBytes
		(
			("model.onnx", "fake onnx"),
			("config.json", "{\"voice\":\"ryan\"}")
		);
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, archiveBytes, "application/zip")
		);
		var downloader = new FileDownloader(http);
		var resolved = new ResolvedUrl
		{
			FinalUrl = url,
			FileName = "archive.zip",
			ContentLength = archiveBytes.Length,
			SourceType = ResolvedUrlSourceType.Archive,
		};
		var destFolder = CreateArtifactDirectory(nameof(DownloadAsync_Archive_ExtractsFilesAndDeletesTempArchive));

		var result = await downloader.DownloadAsync(resolved, destFolder);

		Assert.Equal(2, result.AllFiles.Count);
		Assert.Contains(result.AllFiles, path => Path.GetFileName(path) == "model.onnx");
		Assert.Contains(result.AllFiles, path => Path.GetFileName(path) == "config.json");
		Assert.Empty(Directory.GetFiles(destFolder, "download-*.zip", SearchOption.TopDirectoryOnly));
	}

	[Fact]
	public async Task DownloadAsync_FileFilter_FiltersFilesButRetainsAllFiles()
	{
		const string url = "https://example.test/archive.zip";
		var archiveBytes = CreateZipArchiveBytes
		(
			("model.onnx", "fake onnx"),
			("notes.txt", "ignore me"),
			("subdir/metadata.json", "{}")
		);
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, archiveBytes, "application/zip")
		);
		var downloader = new FileDownloader(http)
		{
			FileFilter = relativePath => relativePath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase),
		};
		var resolved = new ResolvedUrl
		{
			FinalUrl = url,
			FileName = "archive.zip",
			ContentLength = archiveBytes.Length,
			SourceType = ResolvedUrlSourceType.Archive,
		};
		var destFolder = CreateArtifactDirectory(nameof(DownloadAsync_FileFilter_FiltersFilesButRetainsAllFiles));

		var result = await downloader.DownloadAsync(resolved, destFolder);

		Assert.Single(result.Files);
		Assert.EndsWith("model.onnx", result.Files[0], StringComparison.Ordinal);
		Assert.Equal(3, result.AllFiles.Count);
		Assert.Contains(result.AllFiles, path => path.EndsWith("notes.txt", StringComparison.Ordinal));
	}

	[Fact]
	public async Task DownloadAsync_CompanionFiles_DownloadsMainAndCompanions()
	{
		const string mainUrl = "https://example.test/model.onnx";
		const string configUrl = "https://example.test/model.onnx.json";
		const string tokensUrl = "https://example.test/tokens.txt";
		using var http = CreateHttpClient
		(
			request =>
			{
				return request.RequestUri?.ToString() switch
				{
					mainUrl => CreateStaticResponseAsync(request, Encoding.UTF8.GetBytes("main")),
					configUrl => CreateStaticResponseAsync(request, Encoding.UTF8.GetBytes("{\"ok\":true}")),
					tokensUrl => CreateStaticResponseAsync(request, Encoding.UTF8.GetBytes("a\nb\n")),
					_ => CreateNotFoundResponseAsync(request),
				};
			}
		);
		var downloader = new FileDownloader(http);
		var resolved = new ResolvedUrl
		{
			FinalUrl = mainUrl,
			FileName = "model.onnx",
			ContentLength = 4,
			SourceType = ResolvedUrlSourceType.SingleFile,
			CompanionUrls = [configUrl, tokensUrl],
		};
		var destFolder = CreateArtifactDirectory(nameof(DownloadAsync_CompanionFiles_DownloadsMainAndCompanions));

		var result = await downloader.DownloadAsync(resolved, destFolder);

		Assert.Equal(3, result.Files.Count);
		Assert.Contains(result.AllFiles, path => Path.GetFileName(path) == "model.onnx");
		Assert.Contains(result.AllFiles, path => Path.GetFileName(path) == "model.onnx.json");
		Assert.Contains(result.AllFiles, path => Path.GetFileName(path) == "tokens.txt");
	}

	[Fact]
	public async Task DownloadAsync_OneShotConvenienceMethod_Works()
	{
		const string url = "https://example.test/voice.bin";
		var content = Encoding.UTF8.GetBytes("one shot");
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, content)
		);
		var downloader = new FileDownloader(http);
		var destFolder = CreateArtifactDirectory(nameof(DownloadAsync_OneShotConvenienceMethod_Works));

		var result = await downloader.DownloadAsync(url, destFolder);

		Assert.Single(result.Files);
		var downloadedFile = result.Files[0];
		Assert.True(File.Exists(downloadedFile));
		Assert.Equal("one shot", await File.ReadAllTextAsync(downloadedFile));
	}

	[Fact]
	public async Task DownloadAsync_ReportsProgressPhasesInOrder()
	{
		const string url = "https://example.test/progress.onnx";
		var content = Encoding.UTF8.GetBytes("progress");
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, content)
		);
		var downloader = new FileDownloader(http);
		var destFolder = CreateArtifactDirectory(nameof(DownloadAsync_ReportsProgressPhasesInOrder));
		var updates = new List<FileDownloadProgress>();
		var progress = new Progress<FileDownloadProgress>(update => updates.Add(update));

		await downloader.DownloadAsync(url, destFolder, progress);

		Assert.NotEmpty(updates);
		Assert.Equal(DownloadPhase.Resolving, updates[0].Phase);
		Assert.Contains(updates, update => update.Phase == DownloadPhase.Downloading);
		Assert.Equal(DownloadPhase.Complete, updates[^1].Phase);
	}

	[Fact]
	public async Task DownloadAsync_SearchRecursivelyFalse_ExcludesSubdirectoryFiles()
	{
		const string url = "https://example.test/archive.zip";
		var archiveBytes = CreateZipArchiveBytes
		(
			("root.onnx", "root model"),
			("subdir/nested.onnx", "nested model")
		);
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, archiveBytes, "application/zip")
		);
		var downloader = new FileDownloader(http) { SearchRecursively = false };
		var resolved = new ResolvedUrl
		{
			FinalUrl = url,
			FileName = "archive.zip",
			ContentLength = archiveBytes.Length,
			SourceType = ResolvedUrlSourceType.Archive,
		};
		var destFolder = CreateArtifactDirectory(nameof(DownloadAsync_SearchRecursivelyFalse_ExcludesSubdirectoryFiles));

		var result = await downloader.DownloadAsync(resolved, destFolder);

		Assert.Single(result.Files);
		Assert.EndsWith("root.onnx", result.Files[0], StringComparison.Ordinal);
		Assert.Equal(2, result.AllFiles.Count);
	}

	[Fact]
	public async Task DownloadAsync_HttpError_ReportsFailedPhaseAndThrows()
	{
		const string url = "https://example.test/broken.onnx";
		using var http = CreateHttpClient
		(
			request => Task.FromResult
			(
				new HttpResponseMessage(HttpStatusCode.InternalServerError)
				{
					RequestMessage = request,
					Content = new ByteArrayContent([]),
				}
			)
		);
		var downloader = new FileDownloader(http);
		var resolved = new ResolvedUrl
		{
			FinalUrl = url,
			FileName = "broken.onnx",
			SourceType = ResolvedUrlSourceType.SingleFile,
		};
		var destFolder = CreateArtifactDirectory(nameof(DownloadAsync_HttpError_ReportsFailedPhaseAndThrows));
		var updates = new List<FileDownloadProgress>();
		var progress = new Progress<FileDownloadProgress>(update => updates.Add(update));

		await Assert.ThrowsAsync<InvalidOperationException>
		(
			() => downloader.DownloadAsync(resolved, destFolder, progress)
		);

		Assert.Contains(updates, update => update.Phase == DownloadPhase.Failed);
	}

	[Fact]
	public async Task DownloadAsync_NoContentLength_DownloadsSuccessfully()
	{
		const string url = "https://example.test/unknown-size.bin";
		var content = Encoding.UTF8.GetBytes("unknown size payload");
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, content)
		);
		var downloader = new FileDownloader(http);
		var resolved = new ResolvedUrl
		{
			FinalUrl = url,
			FileName = "unknown-size.bin",
			ContentLength = null,
			SourceType = ResolvedUrlSourceType.SingleFile,
		};
		var destFolder = CreateArtifactDirectory(nameof(DownloadAsync_NoContentLength_DownloadsSuccessfully));
		var updates = new List<FileDownloadProgress>();
		var progress = new Progress<FileDownloadProgress>(update => updates.Add(update));

		var result = await downloader.DownloadAsync(resolved, destFolder, progress);

		Assert.Single(result.Files);
		Assert.Equal(content.Length, result.TotalBytes);
	}

	[Fact]
	public async Task ResolveAsync_NonHttpResolverReturnsSameUrl_SkipsProbeAndReturns()
	{
		const string inputUrl = "https://example.test/voice";
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, "payload"u8.ToArray())
		);
		var downloader = new FileDownloader(http);
		downloader.Resolvers.Add
		(
			new TestResolver
			(
				(inputUrl, new UrlResolverResult(inputUrl, "Voice Model"))
			)
		);

		var resolved = await downloader.ResolveAsync(inputUrl);

		Assert.Equal(inputUrl, resolved.FinalUrl);
		Assert.Equal(["Voice Model"], resolved.Names);
	}

	[Fact]
	public async Task ResolveAsync_PreferredNameSortedFirst()
	{
		const string inputUrl = "https://example.test/A";
		const string secondUrl = "https://example.test/B";
		const string finalUrl = "https://example.test/C.onnx";
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, "payload"u8.ToArray())
		);
		var downloader = new FileDownloader(http);
		downloader.Resolvers.Add
		(
			new TestResolver
			(
				(inputUrl, new UrlResolverResult(secondUrl, "zzz-long-name")),
				(secondUrl, new UrlResolverResult(finalUrl, "ab") { IsPreferredName = true })
			)
		);

		var resolved = await downloader.ResolveAsync(inputUrl);

		Assert.Equal("ab", resolved.Names[0]);
	}

	[Fact]
	public async Task DownloadAsync_DuplicateCompanionUrls_DownloadsEachOnce()
	{
		const string mainUrl = "https://example.test/model.onnx";
		const string companionUrl = "https://example.test/config.json";
		using var http = CreateHttpClient
		(
			request =>
			{
				return request.RequestUri?.ToString() switch
				{
					mainUrl => CreateStaticResponseAsync(request, "main"u8.ToArray()),
					companionUrl => CreateStaticResponseAsync(request, "{}"u8.ToArray()),
					_ => CreateNotFoundResponseAsync(request),
				};
			}
		);
		var downloader = new FileDownloader(http);
		var resolved = new ResolvedUrl
		{
			FinalUrl = mainUrl,
			FileName = "model.onnx",
			ContentLength = 4,
			SourceType = ResolvedUrlSourceType.SingleFile,
			CompanionUrls = [companionUrl, companionUrl, companionUrl],
		};
		var destFolder = CreateArtifactDirectory(nameof(DownloadAsync_DuplicateCompanionUrls_DownloadsEachOnce));

		var result = await downloader.DownloadAsync(resolved, destFolder);

		Assert.Equal(2, result.AllFiles.Count);
	}

	[Fact]
	public async Task DownloadAsync_NullProgress_DoesNotThrow()
	{
		const string url = "https://example.test/model.bin";
		var content = "data"u8.ToArray();
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, content)
		);
		var downloader = new FileDownloader(http);
		var resolved = new ResolvedUrl
		{
			FinalUrl = url,
			FileName = "model.bin",
			ContentLength = content.Length,
			SourceType = ResolvedUrlSourceType.SingleFile,
		};
		var destFolder = CreateArtifactDirectory(nameof(DownloadAsync_NullProgress_DoesNotThrow));

		var result = await downloader.DownloadAsync(resolved, destFolder, progress: null);

		Assert.Single(result.Files);
		Assert.Equal(content.Length, result.TotalBytes);
	}

	[Fact]
	public async Task ResolveAsync_ThrowsOnNullOrWhitespaceUrl()
	{
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, []));
		var downloader = new FileDownloader(http);

		await Assert.ThrowsAsync<ArgumentException>
		(
			() => downloader.ResolveAsync("")
		);

		await Assert.ThrowsAsync<ArgumentException>
		(
			() => downloader.ResolveAsync("   ")
		);
	}

	[Fact]
	public async Task DownloadAsync_ThrowsOnNullOrWhitespaceDestFolder()
	{
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, [])
		);
		var downloader = new FileDownloader(http);
		var resolved = new ResolvedUrl
		{
			FinalUrl = "https://example.test/file.bin",
			FileName = "file.bin",
			SourceType = ResolvedUrlSourceType.SingleFile,
		};

		await Assert.ThrowsAsync<ArgumentException>
		(
			() => downloader.DownloadAsync(resolved, "")
		);
	}

	[Fact]
	public async Task DownloadAsync_ZipWithTraversalEntry_ThrowsInvalidDataException()
	{
		const string url = "https://example.test/evil.zip";
		using var memory = new MemoryStream();
		using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
		{
			var entry = archive.CreateEntry("../../../etc/passwd");
				var stream = await entry.OpenAsync(CancellationToken.None);
				await using (stream.ConfigureAwait(false))
				{
					stream.Write("evil"u8);
				}
		}

		var archiveBytes = memory.ToArray();
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, archiveBytes, "application/zip")
		);
		var downloader = new FileDownloader(http);
		var resolved = new ResolvedUrl
		{
			FinalUrl = url,
			FileName = "evil.zip",
			ContentLength = archiveBytes.Length,
			SourceType = ResolvedUrlSourceType.Archive,
		};
		var destFolder = CreateArtifactDirectory(nameof(DownloadAsync_ZipWithTraversalEntry_ThrowsInvalidDataException));

		await Assert.ThrowsAsync<InvalidDataException>
		(
			() => downloader.DownloadAsync(resolved, destFolder)
		);
	}

	[Fact]
	public async Task DownloadAsync_TarGzArchive_StreamsAndExtractsFiles()
	{
		const string url = "https://example.test/archive.tar.gz";
		var archiveBytes = CreateTarGzBytes
		(
			("voices/ryan.onnx", "fake onnx"),
			("voices/config.json", "{\"voice\":\"ryan\"}")
		);
		using var http = CreateHttpClient
		(
			request => CreateSlowStreamResponseAsync
			(
				request,
				archiveBytes,
				mediaType: "application/gzip",
				chunkSize: 64,
				delayMilliseconds: 120
			)
		);
		var downloader = new FileDownloader(http)
		{
			BufferSize = 64,
		};
		var resolved = new ResolvedUrl
		{
			FinalUrl = url,
			FileName = "archive.tar.gz",
			SourceType = ResolvedUrlSourceType.Archive,
		};
		var destFolder = CreateArtifactDirectory(nameof(DownloadAsync_TarGzArchive_StreamsAndExtractsFiles));
		var updates = new List<FileDownloadProgress>();
		var progress = new Progress<FileDownloadProgress>(update => updates.Add(update));

		var result = await downloader.DownloadAsync(resolved, destFolder, progress);

		var modelPath = Path.Combine(destFolder, "voices", "ryan.onnx");
		var configPath = Path.Combine(destFolder, "voices", "config.json");
		Assert.Equal(2, result.AllFiles.Count);
		Assert.Contains(modelPath, result.AllFiles);
		Assert.Contains(configPath, result.AllFiles);
		Assert.Equal("fake onnx", await File.ReadAllTextAsync(modelPath));
		Assert.Equal("{\"voice\":\"ryan\"}", await File.ReadAllTextAsync(configPath));

		var streamingUpdates = updates
			.Where(static update => update.Phase == DownloadPhase.Streaming)
			.ToArray();
		Assert.NotEmpty(streamingUpdates);
		Assert.Contains
		(
			streamingUpdates,
			static update =>
				update.BytesTransferred > 0
				&& update.TotalBytes is null
				&& update.OverallPercent is null
				&& update.Eta is null
		);
		Assert.Equal(2, streamingUpdates[^1].FilesCompleted);
	}

	[Fact]
	public async Task DownloadAsync_UnknownLengthSlowContent_ReportsThrottledProgressWithoutPercentOrEta()
	{
		const string url = "https://example.test/slow.bin";
		var content = Enumerable.Repeat((byte)'x', 24 * 1024).ToArray();
		using var http = CreateHttpClient
		(
			request => CreateSlowStreamResponseAsync
			(
				request,
				content,
				chunkSize: 1024,
				delayMilliseconds: 120
			)
		);
		var downloader = new FileDownloader(http)
		{
			BufferSize = 1024,
		};
		var resolved = new ResolvedUrl
		{
			FinalUrl = url,
			FileName = "slow.bin",
			ContentLength = null,
			SourceType = ResolvedUrlSourceType.SingleFile,
		};
		var destFolder = CreateArtifactDirectory
		(
			nameof(DownloadAsync_UnknownLengthSlowContent_ReportsThrottledProgressWithoutPercentOrEta)
		);
		var updates = new List<FileDownloadProgress>();
		var progress = new Progress<FileDownloadProgress>(update => updates.Add(update));

		var result = await downloader.DownloadAsync(resolved, destFolder, progress);

		Assert.Single(result.Files);
		Assert.Equal(content.Length, result.TotalBytes);

		var downloadUpdates = updates
			.Where(static update => update.Phase == DownloadPhase.Downloading)
			.ToArray();
		Assert.True(downloadUpdates.Length >= 3);
		Assert.Contains
		(
			downloadUpdates,
			static update =>
				update.BytesTransferred > 0
				&& update.TotalBytes is null
				&& update.OverallPercent is null
				&& update.Eta is null
		);
		Assert.Null(downloadUpdates[^1].OverallPercent);
		Assert.Equal(TimeSpan.Zero, downloadUpdates[^1].Eta);
	}

	[Fact]
	public async Task DownloadAsync_Head405Probe_FallsBackToGetMetadata()
	{
		const string url = "https://example.test/fallback";
		var content = Encoding.UTF8.GetBytes("fallback payload");
		var headRequests = 0;
		var getRequests = 0;
		using var http = CreateHttpClient
		(
			request =>
			{
				if (request.Method == HttpMethod.Head)
				{
					headRequests++;
					return CreateMethodNotAllowedResponseAsync(request);
				}

				getRequests++;
				return CreateStaticResponseAsync(request, content, fileName: "fallback model.onnx");
			}
		);
		var downloader = new FileDownloader(http);
		var destFolder = CreateArtifactDirectory(nameof(DownloadAsync_Head405Probe_FallsBackToGetMetadata));

		var result = await downloader.DownloadAsync(url, destFolder);

		var downloadedFile = Path.Combine(destFolder, "fallback model.onnx");
		Assert.Equal(2, headRequests);
		Assert.Equal(3, getRequests);
		Assert.Contains(downloadedFile, result.AllFiles);
		Assert.Equal("fallback payload", await File.ReadAllTextAsync(downloadedFile));
	}

	[Fact]
	public async Task DownloadAsync_HttpRequestException_WrapsInInvalidOperationException()
	{
		const string url = "https://example.test/network-failure.onnx";
		using var http = CreateHttpClient
		(
			static _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("kaboom"))
		);
		var downloader = new FileDownloader(http);
		var resolved = new ResolvedUrl
		{
			FinalUrl = url,
			FileName = "network-failure.onnx",
			SourceType = ResolvedUrlSourceType.SingleFile,
		};
		var destFolder = CreateArtifactDirectory(nameof(DownloadAsync_HttpRequestException_WrapsInInvalidOperationException));

		var ex = await Assert.ThrowsAsync<InvalidOperationException>
		(
			() => downloader.DownloadAsync(resolved, destFolder)
		);

		Assert.Contains(url, ex.Message, StringComparison.Ordinal);
		Assert.IsType<HttpRequestException>(ex.InnerException);
		Assert.Equal("kaboom", ex.InnerException?.Message);
	}

	[Theory]
	[InlineData("https://example.test/file.zip?download=1", "https://example.test/file.zip")]
	[InlineData("https://example.test/file.zip#section", "https://example.test/file.zip")]
	[InlineData("https://example.test/file.zip?download=1#section", "https://example.test/file.zip")]
	[InlineData("https://example.test/file.zip", "https://example.test/file.zip")]
	public void StripUrlSuffix_SuffixVariants_ReturnBaseUrl(string input, string expected)
	{
		var actual = InvokePrivateStatic<string>(nameof(FileDownloader), "StripUrlSuffix", input);

		Assert.Equal(expected, actual);
	}

	[Fact]
	public void GetCompanionFileName_ValidAndInvalidUrls_ReturnExpectedNames()
	{
		var valid = InvokePrivateStatic<string>
		(
			nameof(FileDownloader),
			"GetCompanionFileName",
			"https://example.test/files/config%20file.json?download=1#fragment"
		);
		var invalid = InvokePrivateStatic<string>(nameof(FileDownloader), "GetCompanionFileName", "not a valid absolute url");

		Assert.Equal("config file.json", valid);
		Assert.StartsWith("companion-", invalid, StringComparison.Ordinal);
		Assert.Equal(42, invalid.Length);
	}

	[Fact]
	public void GetResolvedFileName_PreferredNameAndFallbacks_ReturnExpectedNames()
	{
		var preferred = InvokePrivateStatic<string>
		(
			nameof(FileDownloader),
			"GetResolvedFileName",
			"https://example.test/models/original.bin",
			@"folder\voice.onnx"
		);
		var fromUrl = InvokePrivateStatic<string>
		(
			nameof(FileDownloader),
			"GetResolvedFileName",
			"https://example.test/models/voice%20model.onnx",
			string.Empty
		);
		var fallback = InvokePrivateStatic<string>
		(
			nameof(FileDownloader),
			"GetResolvedFileName",
			"not an absolute url",
			null
		);

		Assert.Equal("voice.onnx", preferred);
		Assert.Equal("voice model.onnx", fromUrl);
		Assert.Equal("download", fallback);
	}

	[Fact]
	public void NormalizeResolverUrl_AbsoluteAndRelativeInputs_ReturnNormalizedValues()
	{
		var absolute = InvokePrivateStatic<string>
		(
			nameof(FileDownloader),
			"NormalizeResolverUrl",
			"https://Example.TEST/path/"
		);
		var relative = InvokePrivateStatic<string>
		(
			nameof(FileDownloader),
			"NormalizeResolverUrl",
			"  relative/path/  "
		);

		Assert.Equal("https://example.test/path", absolute);
		Assert.Equal("relative/path", relative);
	}

	[Fact]
	public async Task DownloadAsync_UnknownArchiveFormat_UsesTempFileExtractionPath()
	{
		const string url = "https://example.test/archive.data";
		var archiveBytes = CreateZipArchiveBytes
		(
			("model.onnx", "fake onnx"),
			("notes.txt", "hello")
		);
		using var http = CreateHttpClient
		(
			request => CreateStaticResponseAsync(request, archiveBytes, "application/octet-stream")
		);
		var downloader = new FileDownloader(http);
		var resolved = new ResolvedUrl
		{
			FinalUrl = url,
			FileName = "archive.data",
			ContentLength = archiveBytes.Length,
			SourceType = ResolvedUrlSourceType.Archive,
		};
		var destFolder = CreateArtifactDirectory(nameof(DownloadAsync_UnknownArchiveFormat_UsesTempFileExtractionPath));

		var result = await downloader.DownloadAsync(resolved, destFolder);

		Assert.Equal(2, result.AllFiles.Count);
		Assert.Contains(result.AllFiles, path => Path.GetFileName(path) == "model.onnx");
		Assert.Contains(result.AllFiles, path => Path.GetFileName(path) == "notes.txt");
		Assert.Empty(Directory.GetFiles(destFolder, "download-*.data", SearchOption.TopDirectoryOnly));
	}

	private string CreateArtifactDirectory(string name)
	{
		var path = Path.Combine(_artifactRoot, name);
		Directory.CreateDirectory(path);
		return path;
	}

	private static (string Input, string Output, string? Name)[] CreateMaxDepthMappings()
	{
		var mappings = new (string Input, string Output, string? Name)[10];

		for (var i = 0; i < mappings.Length; i++)
		{
			var input = $"https://example.test/A{i}";
			var output = i == mappings.Length - 1
				? "https://example.test/A10.onnx"
				: $"https://example.test/A{i + 1}";
			mappings[i] = (input, output, null);
		}

		return mappings;
	}

	private static byte[] CreateZipArchiveBytes(params (string Path, string Content)[] entries)
	{
		using var memory = new MemoryStream();

		using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach (var (path, content) in entries)
			{
				var entry = archive.CreateEntry(path);
				using var stream = entry.Open();
				var bytes = Encoding.UTF8.GetBytes(content);
				stream.Write(bytes);
			}
		}

		return memory.ToArray();
	}

	private static byte[] CreateTarGzBytes(params (string Path, string Content)[] entries)
	{
		using var memory = new MemoryStream();

		using (var gz = new GZipStream(memory, CompressionLevel.SmallestSize, leaveOpen: true))
		{
			using var tarWriter = new TarWriter(gz, leaveOpen: true);
			foreach (var (path, content) in entries)
			{
				using var dataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
				var entry = new PaxTarEntry(TarEntryType.RegularFile, path)
				{
					DataStream = dataStream,
				};
				tarWriter.WriteEntry(entry);
			}
		}

		return memory.ToArray();
	}

	private static Task<HttpResponseMessage> CreateStaticResponseAsync
	(
		HttpRequestMessage request,
		byte[] content,
		string? mediaType = null,
		string? fileName = null
	)
	{
		var response = new HttpResponseMessage(HttpStatusCode.OK)
		{
			RequestMessage = request,
			Content = new ByteArrayContent(content),
		};

		if (!string.IsNullOrWhiteSpace(mediaType))
		{
			response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
		}

		if (!string.IsNullOrWhiteSpace(fileName))
		{
			response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
			{
				FileName = fileName,
			};
		}

		return Task.FromResult(response);
	}

	private static Task<HttpResponseMessage> CreateNotFoundResponseAsync(HttpRequestMessage request)
	{
		return Task.FromResult
		(
			new HttpResponseMessage(HttpStatusCode.NotFound)
			{
				RequestMessage = request,
				Content = new ByteArrayContent([]),
			}
		);
	}

	private static Task<HttpResponseMessage> CreateMethodNotAllowedResponseAsync(HttpRequestMessage request)
	{
		return Task.FromResult
		(
			new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
			{
				RequestMessage = request,
				Content = new ByteArrayContent([]),
			}
		);
	}

	private static Task<HttpResponseMessage> CreateSlowStreamResponseAsync
	(
		HttpRequestMessage request,
		byte[] content,
		string? mediaType = null,
		string? fileName = null,
		int chunkSize = 4096,
		int delayMilliseconds = 0
	)
	{
		var response = new HttpResponseMessage(HttpStatusCode.OK)
		{
			RequestMessage = request,
			Content = new StreamContent(new SlowReadStream(content, chunkSize, delayMilliseconds)),
		};

		if (!string.IsNullOrWhiteSpace(mediaType))
		{
			response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
		}

		if (!string.IsNullOrWhiteSpace(fileName))
		{
			response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
			{
				FileName = fileName,
			};
		}

		return Task.FromResult(response);
	}

	[SuppressMessage
	(
		"Reliability",
		"CA2000:Dispose objects before losing scope",
		Justification = "HttpClient owns and disposes the handler via disposeHandler: true."
	)]
	private static HttpClient CreateHttpClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
	{
		return new HttpClient(new MockHttpHandler(handler), disposeHandler: true);
	}

	private static T InvokePrivateStatic<T>(string typeName, string methodName, params object?[] args)
	{
		Assert.Equal(nameof(FileDownloader), typeName);
		var method = typeof(FileDownloader).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(method);
		var result = method!.Invoke(null, args);
		return Assert.IsType<T>(result);
	}

	private sealed class MockHttpHandler : HttpMessageHandler
	{
		private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

		public MockHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
		{
			_handler = handler;
		}

		protected override Task<HttpResponseMessage> SendAsync
		(
			HttpRequestMessage request,
			CancellationToken ct
		)
		{
			return _handler(request);
		}
	}

	private sealed class SlowReadStream : Stream
	{
		private readonly byte[] _content;
		private readonly int _chunkSize;
		private readonly int _delayMilliseconds;
		private int _position;

		public SlowReadStream(byte[] content, int chunkSize, int delayMilliseconds)
		{
			_content = content;
			_chunkSize = Math.Max(1, chunkSize);
			_delayMilliseconds = Math.Max(0, delayMilliseconds);
		}

		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => false;

		public override long Length => _content.Length;

		public override long Position
		{
			get => _position;
			set => throw new NotSupportedException();
		}

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			if (_delayMilliseconds > 0 && _position < _content.Length)
			{
				Thread.Sleep(_delayMilliseconds);
			}

			return ReadCore(buffer.AsSpan(offset, count));
		}

		public override int Read(Span<byte> buffer)
		{
			if (_delayMilliseconds > 0 && _position < _content.Length)
			{
				Thread.Sleep(_delayMilliseconds);
			}

			return ReadCore(buffer);
		}

		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			return ReadAsyncCore(buffer, cancellationToken);
		}

		public override Task<int> ReadAsync
		(
			byte[] buffer,
			int offset,
			int count,
			CancellationToken cancellationToken
		)
		{
			return ReadAsyncCore(buffer.AsMemory(offset, count), cancellationToken).AsTask();
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
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

		private int ReadCore(Span<byte> buffer)
		{
			if (_position >= _content.Length)
			{
				return 0;
			}

			var remaining = _content.Length - _position;
			var bytesToCopy = Math.Min(Math.Min(buffer.Length, _chunkSize), remaining);
			_content.AsSpan(_position, bytesToCopy).CopyTo(buffer);
			_position += bytesToCopy;
			return bytesToCopy;
		}

		private async ValueTask<int> ReadAsyncCore
		(
			Memory<byte> buffer,
			CancellationToken cancellationToken
		)
		{
			if (_delayMilliseconds > 0 && _position < _content.Length)
			{
				await Task.Delay(_delayMilliseconds, cancellationToken).ConfigureAwait(false);
			}

			return ReadCore(buffer.Span);
		}
	}

	private sealed class TestResolver : IUrlResolver
	{
		private readonly Dictionary<string, UrlResolverResult> _mappings = new(StringComparer.OrdinalIgnoreCase);

		public TestResolver(params (string Input, string Output, string? Name)[] mappings)
		{
			foreach (var (input, output, name) in mappings)
			{
				_mappings[input] = new UrlResolverResult(output, name);
			}
		}

		public TestResolver(params (string Input, UrlResolverResult Result)[] mappings)
		{
			foreach (var (input, result) in mappings)
			{
				_mappings[input] = result;
			}
		}

		public bool CanResolve(string url)
		{
			return _mappings.ContainsKey(url);
		}

		public Task<UrlResolverResult> ResolveAsync
		(
			HttpClient http,
			string url,
			CancellationToken ct
		)
		{
			return Task.FromResult(_mappings[url]);
		}
	}
}
