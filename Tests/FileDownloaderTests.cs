using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
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
