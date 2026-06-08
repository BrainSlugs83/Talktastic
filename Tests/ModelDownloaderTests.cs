using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Talktastic.Tests;

public sealed class ModelDownloaderTests : IDisposable
{
	private readonly string _artifactRoot;

	public ModelDownloaderTests()
	{
		_artifactRoot = Path.Combine
		(
			AppContext.BaseDirectory,
			"TestArtifacts",
			nameof(ModelDownloaderTests),
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

	[Theory]
	[InlineData("http://example.com/model.onnx")]
	[InlineData("https://example.com/model.onnx")]
	public void IsUrl_ReturnsTrue_ForHttpAndHttpsUrls(string query)
	{
		Assert.True(ModelDownloader.IsUrl(query));
	}

	[Theory]
	[InlineData("ftp://example.com/model.onnx")]
	[InlineData("models\\voice.onnx")]
	[InlineData("")]
	[InlineData("piper:en_US-ryan-high")]
	[InlineData("C:\\foo.txt")]
	public void IsUrl_ReturnsFalse_ForNonHttpUrlsAndPaths(string query)
	{
		Assert.False(ModelDownloader.IsUrl(query));
	}

	[Fact]
	public void IsUrl_ReturnsFalse_ForNull()
	{
		Assert.False(ModelDownloader.IsUrl(null!));
	}

	[Fact]
	public void NormalizeUrl_RemovesTrailingSlash()
	{
		var normalized = ModelDownloader.NormalizeUrl("https://Example.COM/models/");

		Assert.Equal("https://example.com/models", normalized);
	}

	[Fact]
	public void NormalizeUrl_LowercasesSchemeAndHost_PreservesPathCaseAndQuery()
	{
		var normalized = ModelDownloader.NormalizeUrl("HTTPS://ExAmPle.COM/Models/KeepCase?Voice=Ryan");

		Assert.Equal("https://example.com/Models/KeepCase?Voice=Ryan", normalized);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(" \t ")]
	[InlineData("model")]
	[InlineData("MODEL")]
	[InlineData("weights")]
	[InlineData("checkpoint")]
	[InlineData("voice")]
	[InlineData("rvc")]
	public void IsUsableName_ReturnsFalse_ForNullWhitespaceAndGenericNames(string? name)
	{
		Assert.False(ModelDownloader.IsUsableName(name!));
	}

	[Fact]
	public void IsUsableName_ReturnsFalse_ForGuid()
	{
		Assert.False(ModelDownloader.IsUsableName(Guid.NewGuid().ToString("D")));
	}

	[Fact]
	public void IsUsableName_ReturnsFalse_ForDownloadGuidPattern()
	{
		var name = $"download-{Guid.NewGuid():D}";

		Assert.False(ModelDownloader.IsUsableName(name));
	}

	[Theory]
	[InlineData("BartSimpson_e230_s7360")]
	[InlineData("egirl")]
	[InlineData("homer")]
	public void IsUsableName_ReturnsTrue_ForNormalNames(string name)
	{
		Assert.True(ModelDownloader.IsUsableName(name));
	}

	[Fact]
	public void SanitizeFileName_LeavesNormalNameUnchanged()
	{
		Assert.Equal("egirl", ModelDownloader.SanitizeFileName("egirl"));
	}

	[Fact]
	public void SanitizeFileName_StripsInvalidFileNameCharacters()
	{
		var sanitized = ModelDownloader.SanitizeFileName("bad<>:\"/\\|?*name");

		Assert.Equal("badname", sanitized);
	}

	[Fact]
	public void SanitizeFileName_StripsDownloadGuidPrefix()
	{
		var name = $"download-{Guid.NewGuid():N}-egirl";

		Assert.Equal("egirl", ModelDownloader.SanitizeFileName(name));
	}

	[Theory]
	[InlineData("chief_keef_v2", "chief keef v2")]
	[InlineData("BartSimpson_e230_s7360", "BartSimpson e230 s7360")]
	[InlineData("SpongeBob_SquarePants__RVC_v2_", "SpongeBob SquarePants RVC v2")]
	public void CleanModelName_ReplacesUnderscoresWithSpaces(string rawName, string expected)
	{
		Assert.Equal(expected, ModelDownloader.CleanModelName(rawName));
	}

	[Theory]
	[InlineData("Peter  Griffin", "Peter Griffin")]
	[InlineData("Peter__Griffin", "Peter Griffin")]
	[InlineData("Peter_  Griffin", "Peter Griffin")]
	public void CleanModelName_CollapsesMultipleSpaces(string rawName, string expected)
	{
		Assert.Equal(expected, ModelDownloader.CleanModelName(rawName));
	}

	[Theory]
	[InlineData("  homer  ", "homer")]
	[InlineData(" chief_keef_v2 ", "chief keef v2")]
	public void CleanModelName_TrimsWhitespace(string rawName, string expected)
	{
		Assert.Equal(expected, ModelDownloader.CleanModelName(rawName));
	}

	[Theory]
	[InlineData(null, null)]
	[InlineData("", "")]
	public void CleanModelName_ReturnsEmptyForNullOrEmpty(string? rawName, string? expected)
	{
		Assert.Equal(expected, ModelDownloader.CleanModelName(rawName));
	}

	[Theory]
	[InlineData("homer")]
	[InlineData("Hank Hill")]
	[InlineData("Vonv2")]
	public void CleanModelName_PreservesAlreadyCleanNames(string rawName)
	{
		Assert.Equal(rawName, ModelDownloader.CleanModelName(rawName));
	}

	[Fact]
	public void ResolveModelName_PrefersUsableInternalName()
	{
		var resolved = ModelDownloader.ResolveModelName("C:\\models\\BartSimpson_e230_s7360.pth", "fallback");

		Assert.Equal("BartSimpson e230 s7360", resolved);
	}

	[Fact]
	public void ResolveModelName_FallsBackWhenInternalNameIsGeneric()
	{
		var resolved = ModelDownloader.ResolveModelName("C:\\models\\model.pth", "egirl");

		Assert.Equal("egirl", resolved);
	}

	[Fact]
	public void ResolveModelName_ReturnsInternalNameAsIs_WhenBothNamesAreGeneric()
	{
		var resolved = ModelDownloader.ResolveModelName("C:\\models\\model.pth", "voice");

		Assert.Equal("model", resolved);
	}

	[Fact]
	public void ResolveModelName_SanitizesFallbackName()
	{
		var resolved = ModelDownloader.ResolveModelName("C:\\models\\model.pth", "egirl<>:\"/\\|?*");

		Assert.Equal("egirl", resolved);
	}

	[Fact]
	public void DeriveNameFromDirectUrl_UsesFilename_WhenItIsUsable()
	{
		var uri = new Uri("https://example.com/models/egirl.pth");

		Assert.Equal("egirl", ModelDownloader.DeriveNameFromDirectUrl(uri));
	}

	[Fact]
	public void DeriveNameFromDirectUrl_UsesUsableParentSegment_WhenFilenameIsGeneric()
	{
		var uri = new Uri("https://huggingface.co/binant/BartSimpson_e230_s7360/resolve/main/model.pth");

		Assert.Equal("BartSimpson e230 s7360", ModelDownloader.DeriveNameFromDirectUrl(uri));
	}

	[Fact]
	public void DeriveNameFromDirectUrl_DecodesUrlEncodedFileNames()
	{
		var uri = new Uri("https://example.com/models/egirl%20v2.pth");

		Assert.Equal("egirl v2", ModelDownloader.DeriveNameFromDirectUrl(uri));
	}

	[Fact]
	public void DeriveModelNameFromRepo_UsesPiperSubPathComponents()
	{
		var derived = ModelDownloader.DeriveModelNameFromRepo("ignored", "en/en_US/ryan/high");

		Assert.Equal("en US-ryan-high", derived);
	}

	[Fact]
	public void DeriveModelNameFromRepo_StripsPiperPrefix_WhenSubPathIsEmpty()
	{
		var derived = ModelDownloader.DeriveModelNameFromRepo("piper-en_US-ryan", "");

		Assert.Equal("en US-ryan", derived);
	}

	[Fact]
	public void DeriveModelNameFromRepo_ReturnsRepoName_ForNonPiperRepo()
	{
		var derived = ModelDownloader.DeriveModelNameFromRepo("cool-voice-pack", "");

		Assert.Equal("cool-voice-pack", derived);
	}

	[Theory]
	[InlineData("KingVonv2", "", "KingVonv2")]
	[InlineData("Peter_Griffin__Family_Guy___RVC_V2__300_Epoch", "", "Peter Griffin Family Guy RVC V2 300 Epoch")]
	public void DeriveModelNameFromRepo_AtRoot_ReturnsRepoName(string repo, string subPath, string expected)
	{
		var derived = ModelDownloader.DeriveModelNameFromRepo(repo, subPath);

		Assert.Equal(expected, derived);
	}

	[Fact]
	public async Task ResolveModelUrlAsync_HuggingFaceRootPth_PrefersRepoNameOverUsableInternalName()
	{
		using var handler = new StubHttpMessageHandler
		(
			req =>
			{
				Assert.Equal
				(
					"https://huggingface.co/api/models/tester/KingVonv2/tree/main",
					req.RequestUri?.ToString()
				);

				return new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent("""[{ "path": "Vonv2.pth" }]"""),
				};
			}
		);
		using var http = new HttpClient(handler);

		var resolved = await ModelDownloader.ResolveModelUrlAsync
		(
			http,
			"https://huggingface.co/tester/KingVonv2/tree/main",
			CancellationToken.None
		);

		Assert.Equal("KingVonv2", resolved.ModelName);
		Assert.Equal("https://huggingface.co/tester/KingVonv2/resolve/main/Vonv2.pth", resolved.FileUrl);
	}

	[Fact]
	public async Task ResolveModelUrlAsync_GitHubReleaseDownload_CleansModelName()
	{
		using var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("No HTTP call expected."));
		using var http = new HttpClient(handler);

		var resolved = await ModelDownloader.ResolveModelUrlAsync
		(
			http,
			"https://github.com/tester/models/releases/download/v1/chief_keef_v2.pth",
			CancellationToken.None
		);

		Assert.Equal("chief keef v2", resolved.ModelName);
		Assert.Equal("https://github.com/tester/models/releases/download/v1/chief_keef_v2.pth", resolved.FileUrl);
	}

	[Fact]
	public void LookupUrlMap_ReturnsNull_WhenFileDoesNotExist()
	{
		var urlMapPath = GetUrlMapPath();

		Assert.Null(ModelDownloader.LookupUrlMap(urlMapPath, "https://example.com/model.onnx"));
	}

	[Fact]
	public void LookupUrlMap_ReturnsMatchingName_WhenUrlExists()
	{
		var urlMapPath = GetUrlMapPath();
		File.WriteAllText
		(
			urlMapPath,
			"""
			{
			  "https://example.com/voice.onnx": "egirl",
			  "https://example.com/other.onnx": "homer"
			}
			"""
		);

		Assert.Equal("egirl", ModelDownloader.LookupUrlMap(urlMapPath, "https://example.com/voice.onnx"));
	}

	[Fact]
	public void LookupUrlMap_ReturnsNull_WhenUrlIsMissing()
	{
		var urlMapPath = GetUrlMapPath();
		File.WriteAllText(urlMapPath, """{ "https://example.com/voice.onnx": "egirl" }""");

		Assert.Null(ModelDownloader.LookupUrlMap(urlMapPath, "https://example.com/missing.onnx"));
	}

	[Fact]
	public void LookupUrlMap_NormalizesUrlBeforeMatching()
	{
		var urlMapPath = GetUrlMapPath();
		File.WriteAllText(urlMapPath, """{ "https://example.com/models": "egirl" }""");

		var result = ModelDownloader.LookupUrlMap
		(
			urlMapPath,
			"HTTPS://EXAMPLE.COM/models/"
		);

		Assert.Equal("egirl", result);
	}

	[Fact]
	public void LookupUrlMap_ParsesLegacyTabSeparatedUrlMap()
	{
		var urlMapPath = GetUrlMapPath();
		File.WriteAllLines
		(
			urlMapPath,
			[
				"https://example.com/voice.onnx\tegirl",
				"https://example.com/other.onnx\thomer",
			]
		);

		Assert.Equal("egirl", ModelDownloader.LookupUrlMap(urlMapPath, "https://example.com/voice.onnx"));
	}

	[Fact]
	public void WriteUrlMapEntry_CreatesNewFile()
	{
		var urlMapPath = GetUrlMapPath();

		ModelDownloader.WriteUrlMapEntry(urlMapPath, "https://example.com/model.onnx", "egirl");

		var urlMap = ReadUrlMapJson(urlMapPath);
		Assert.Equal("egirl", urlMap["https://example.com/model.onnx"]);
	}

	[Fact]
	public void WriteUrlMapEntry_ReplacesExistingEntryForSameUrl()
	{
		var urlMapPath = GetUrlMapPath();
		File.WriteAllText
		(
			urlMapPath,
			"""
			{
			  "https://example.com/model.onnx": "old-name",
			  "https://example.com/other.onnx": "homer"
			}
			"""
		);

		ModelDownloader.WriteUrlMapEntry(urlMapPath, "https://example.com/model.onnx", "new-name");

		var urlMap = ReadUrlMapJson(urlMapPath);
		Assert.Equal(2, urlMap.Count);
		Assert.Equal("new-name", urlMap["https://example.com/model.onnx"]);
	}

	[Fact]
	public void WriteUrlMapEntry_AppendsNewEntry_WhenUrlDoesNotExist()
	{
		var urlMapPath = GetUrlMapPath();
		File.WriteAllText(urlMapPath, """{ "https://example.com/model.onnx": "egirl" }""");

		ModelDownloader.WriteUrlMapEntry(urlMapPath, "https://example.com/other.onnx", "homer");

		var urlMap = ReadUrlMapJson(urlMapPath);
		Assert.Equal(2, urlMap.Count);
		Assert.Equal("egirl", urlMap["https://example.com/model.onnx"]);
		Assert.Equal("homer", urlMap["https://example.com/other.onnx"]);
	}

	[Fact]
	public void WriteUrlMapEntry_RewritesLegacyTabSeparatedUrlMapAsJson()
	{
		var urlMapPath = GetUrlMapPath();
		File.WriteAllText(urlMapPath, "https://example.com/model.onnx\tegirl");

		ModelDownloader.WriteUrlMapEntry(urlMapPath, "https://example.com/other.onnx", "homer");

		var content = File.ReadAllText(urlMapPath);
		Assert.StartsWith("{", content.TrimStart(), StringComparison.Ordinal);
		var urlMap = ReadUrlMapJson(urlMapPath);
		Assert.Equal("egirl", urlMap["https://example.com/model.onnx"]);
		Assert.Equal("homer", urlMap["https://example.com/other.onnx"]);
	}

	[Fact]
	public void WriteUrlMapEntry_AndLookupUrlMap_RoundTripNormalizedUrls()
	{
		var urlMapPath = GetUrlMapPath();

		ModelDownloader.WriteUrlMapEntry
		(
			urlMapPath,
			"HTTPS://EXAMPLE.COM/Models/KeepCase?Voice=Ryan",
			"egirl"
		);

		var result = ModelDownloader.LookupUrlMap
		(
			urlMapPath,
			"https://example.com/Models/KeepCase?Voice=Ryan"
		);

		Assert.Equal("egirl", result);
	}

	private static Dictionary<string, string> ReadUrlMapJson(string urlMapPath)
	{
		return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(urlMapPath))
			?? throw new InvalidOperationException("URL map JSON should deserialize.");
	}

	[Theory]
	[InlineData("https://example.com/model.zip")]
	[InlineData("https://example.com/model.ZIP")]
	public void IsZipUrl_ReturnsTrue_ForZipUrls(string url)
	{
		Assert.True(ModelDownloader.IsZipUrl(url));
	}

	[Theory]
	[InlineData("https://example.com/model.onnx")]
	[InlineData("https://example.com/model.pth")]
	public void IsZipUrl_ReturnsFalse_ForNonZipUrls(string url)
	{
		Assert.False(ModelDownloader.IsZipUrl(url));
	}

	// ── Zip extraction tests ──────────────────────────────────────────

	private string CreateTestZip(string zipName, params (string Path, byte[] Data)[] entries)
	{
		var zipPath = Path.Combine(_artifactRoot, zipName);
		using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
		foreach (var (entryPath, data) in entries)
		{
			var entry = archive.CreateEntry(entryPath);
			using var stream = entry.Open();
			stream.Write(data);
		}

		return zipPath;
	}

	[Fact]
	public async Task ExtractZip_FindsPthInSubdirectory()
	{
		var zipPath = CreateTestZip
		(
			"test.zip",
			("subdir/weights/model.pth", new byte[64])
		);
		var destDir = Path.Combine(_artifactRoot, "extract1");

		var (modelPath, modelName) = await ModelDownloader.ExtractZipAsync
		(
			zipPath, destDir, "TestModel"
		);

		Assert.True(File.Exists(modelPath), $"Model not found at {modelPath}");
		Assert.EndsWith(".pth", modelPath, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExtractZip_NoModelFile_Throws()
	{
		var zipPath = CreateTestZip
		(
			"empty.zip",
			("readme.txt", "hello"u8.ToArray())
		);
		var destDir = Path.Combine(_artifactRoot, "extract2");

		await Assert.ThrowsAsync<InvalidOperationException>
		(
			() => ModelDownloader.ExtractZipAsync(zipPath, destDir, "NoModel")
		);
	}

	[Fact]
	public async Task ExtractZip_IndexInSameDir_Preserved()
	{
		// Index alongside .pth in same directory → should be extracted together
		var zipPath = CreateTestZip
		(
			"same_dir.zip",
			("weights/model.pth", new byte[64]),
			("weights/added_IVF_v2.index", new byte[128])
		);
		var destDir = Path.Combine(_artifactRoot, "extract3");

		var (modelPath, _) = await ModelDownloader.ExtractZipAsync
		(
			zipPath, destDir, "SameDir"
		);
		var modelDir = Path.GetDirectoryName(modelPath)!;

		var indexFiles = Directory.GetFiles(modelDir, "*.index");
		Assert.Single(indexFiles);
	}

	[Fact]
	public async Task ExtractZip_IndexInSiblingDir_StillExtracted()
	{
		// This is the Hank Hill bug: .pth is in weights/, .index is in logs/
		var zipPath = CreateTestZip
		(
			"sibling.zip",
			("HankHillv2/weights/model.pth", new byte[64]),
			("HankHillv2/logs/HankHillv2/model.index", new byte[256])
		);
		var destDir = Path.Combine(_artifactRoot, "extract4");

		var (modelPath, _) = await ModelDownloader.ExtractZipAsync
		(
			zipPath, destDir, "Hank Hill"
		);
		var modelDir = Path.GetDirectoryName(modelPath)!;

		// The index should be in the same output directory as the model
		var indexFiles = Directory.GetFiles(modelDir, "*.index");
		Assert.True
		(
			indexFiles.Length > 0,
			$"No .index files found in {modelDir}. "
			+ $"Contents: [{string.Join(", ", Directory.GetFiles(modelDir).Select(Path.GetFileName))}]"
		);
	}

	[Fact]
	public async Task ExtractZip_MultipleIndexFiles_AllExtracted()
	{
		// Some zips have both a small trained_IVF and a large model.index
		var zipPath = CreateTestZip
		(
			"multi_idx.zip",
			("voice/weights/model.pth", new byte[64]),
			("voice/logs/voice/model.index", new byte[1024]),
			("extra_files/voice/logs/voice/trained_IVF.index", new byte[128])
		);
		var destDir = Path.Combine(_artifactRoot, "extract5");

		var (modelPath, _) = await ModelDownloader.ExtractZipAsync
		(
			zipPath, destDir, "MultiIdx"
		);
		var modelDir = Path.GetDirectoryName(modelPath)!;

		var indexFiles = Directory.GetFiles(modelDir, "*.index");
		// Both index files should be present
		Assert.Equal(2, indexFiles.Length);
	}

	[Fact]
	public async Task ExtractZip_UsesHintNameForGenericModel()
	{
		var zipPath = CreateTestZip
		(
			"generic.zip",
			("subdir/model.pth", new byte[64])
		);
		var destDir = Path.Combine(_artifactRoot, "extract6");

		var (_, modelName) = await ModelDownloader.ExtractZipAsync
		(
			zipPath, destDir, "BetterName"
		);

		// "model" is a generic name, so it should use the hint
		Assert.Equal("BetterName", modelName);
	}

	[Fact]
	public async Task ExtractZip_MultiplePthFiles_AllExtracted()
	{
		var zipPath = CreateTestZip
		(
			"multi_pth.zip",
			("voices/main/model.pth", new byte[64]),
			("voices/backup/model_v2.pth", new byte[96])
		);
		var destDir = Path.Combine(_artifactRoot, "extract7");

		var (modelPath, _) = await ModelDownloader.ExtractZipAsync
		(
			zipPath, destDir, "MultiPth"
		);
		var modelDir = Path.GetDirectoryName(modelPath)!;

		var pthFiles = Directory.GetFiles(modelDir, "*.pth");
		Assert.Equal(2, pthFiles.Length);
	}

	[Fact]
	public async Task ExtractZip_JsonMetadata_AlsoExtracted()
	{
		var zipPath = CreateTestZip
		(
			"with_json.zip",
			("model/weights/model.pth", new byte[64]),
			("model/metadata.json", "{}"u8.ToArray())
		);
		var destDir = Path.Combine(_artifactRoot, "extract8");

		var (modelPath, _) = await ModelDownloader.ExtractZipAsync
		(
			zipPath, destDir, "WithJson"
		);
		var modelDir = Path.GetDirectoryName(modelPath)!;

		var jsonFiles = Directory.GetFiles(modelDir, "*.json");
		Assert.True(jsonFiles.Length > 0, "metadata.json should be extracted");
	}

	private string GetUrlMapPath()
	{
		return Path.Combine(_artifactRoot, $"{Guid.NewGuid():N}.tsv");
	}

	private sealed class StubHttpMessageHandler : HttpMessageHandler
	{
		private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

		public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
		{
			_handler = handler;
		}

		protected override Task<HttpResponseMessage> SendAsync
		(
			HttpRequestMessage request,
			CancellationToken cancellationToken
		)
		{
			return Task.FromResult(_handler(request));
		}
	}
}
