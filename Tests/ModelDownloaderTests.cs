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
	[InlineData("download")]
	[InlineData("Download")]
	[InlineData("DOWNLOAD")]
	[InlineData("file")]
	[InlineData("archive")]
	[InlineData("drive_abc123")]
	[InlineData("drive_XyZ")]
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
	public void ResolveModelName_PrefersPreferredName_WhenUsable()
	{
		// Repro of the Drive bug: temp zip is "download-{guid}.zip" (unusable),
		// internal model file is "model.pth" (generic), but the resolver gave us
		// a curated name from the upstream page (e.g. voice-models.com H3 title or
		// Drive Content-Disposition filename).
		var resolved = ModelDownloader.ResolveModelName
		(
			extractedPath: "C:\\models\\model.pth",
			fallbackName: $"download-{Guid.NewGuid():N}",
			preferredName: "Danica Fujiko"
		);

		Assert.Equal("Danica Fujiko", resolved);
	}

	[Fact]
	public void ResolveModelName_PrefersPreferredName_OverUsableInternalName()
	{
		// Resolver-curated names are always more trustworthy than zip-internal names
		// (which are often the trainer's working filename, e.g. "G_3200.pth" or
		// "added_IVF8000_Flat_nprobe_1_v2.index").
		var resolved = ModelDownloader.ResolveModelName
		(
			extractedPath: "C:\\models\\G_3200.pth",
			fallbackName: "fallback",
			preferredName: "Danica Fujiko"
		);

		Assert.Equal("Danica Fujiko", resolved);
	}

	[Fact]
	public void ResolveModelName_IgnoresPreferredName_WhenGeneric()
	{
		var resolved = ModelDownloader.ResolveModelName
		(
			extractedPath: "C:\\models\\BartSimpson_e230_s7360.pth",
			fallbackName: "fallback",
			preferredName: "download"
		);

		Assert.Equal("BartSimpson e230 s7360", resolved);
	}

	[Fact]
	public void ResolveModelName_NullPreferredName_BehavesLikeTwoArgOverload()
	{
		var twoArg = ModelDownloader.ResolveModelName("C:\\models\\BartSimpson.pth", "egirl");
		var threeArgNull = ModelDownloader.ResolveModelName("C:\\models\\BartSimpson.pth", "egirl", preferredName: null);

		Assert.Equal(twoArg, threeArgNull);
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
	public void ExtractVoiceModelsDownloadLink_ReturnsHrefFromDownloadLinkAnchor()
	{
		var html = """
			<p>
				<b>Download Link:</b> <a href="https://huggingface.co/Ryanham1lton/Angelmouse/resolve/main/Angelmouse.zip?download=true" target="_blank">Download</a>
				<span class="badge host-badge huggingface ms-2">Hosted on Hugging Face</span>
			</p>
			""";

		var href = ModelDownloader.ExtractVoiceModelsDownloadLink(html);

		Assert.Equal
		(
			"https://huggingface.co/Ryanham1lton/Angelmouse/resolve/main/Angelmouse.zip?download=true",
			href
		);
	}

	[Fact]
	public void ExtractVoiceModelsDownloadLink_ReturnsFirstHrefAfterDownloadLink()
	{
		var html = """
			<p>
				<b>Download Link:</b>
				<a class="first" target="_blank" href="https://example.com/first.zip">First</a>
				<a href="https://example.com/second.zip">Second</a>
			</p>
			""";

		var href = ModelDownloader.ExtractVoiceModelsDownloadLink(html);

		Assert.Equal("https://example.com/first.zip", href);
	}

	[Fact]
	public void ExtractVoiceModelsDownloadLink_ThrowsWhenDownloadLinkIsMissing()
	{
		var exception = Assert.Throws<InvalidOperationException>
		(
			() => ModelDownloader.ExtractVoiceModelsDownloadLink("<p>No model here.</p>")
		);

		Assert.Contains
		(
			"voice-models.com page has no Download Link",
			exception.Message,
			StringComparison.Ordinal
		);
	}

	[Theory]
	[InlineData("Angelmouse - David Jason (Angelmouse) (English) [RVC v2]", "Angelmouse - David Jason (Angelmouse) (English) [RVC v2]")]
	[InlineData("Princess Peach (2007 - 2024) (Super Mario) (English) [RVC v2] [RMVPE] [300 Epochs]", "Princess Peach (2007 - 2024) (Super Mario) (English) [RVC v2] [RMVPE] [300 Epochs]")]
	[InlineData("Samantha Coleman (Wii Deleted You) (rvmpe, RVC v2, 200 Epochs)", "Samantha Coleman (Wii Deleted You) (rvmpe, RVC v2, 200 Epochs)")]
	[InlineData("Open The Noor Guy [Samantha's Father] (RVC v2 | 700 Epochs)", "Open The Noor Guy [Samantha's Father] (RVC v2 700 Epochs)")]
	[InlineData("Danica Fujiko (RVC v2 | 400 Epochs)", "Danica Fujiko (RVC v2 400 Epochs)")]
	[InlineData("FooBar (Eng)", "FooBar (Eng)")]
	[InlineData("JustOneName", "JustOneName")]
	[InlineData("Trailing whitespace   ", "Trailing whitespace")]
	public void ExtractVoiceModelsShortName_ReturnsShortDisplayName(string h3Text, string expected)
	{
		var shortName = ModelDownloader.ExtractVoiceModelsShortName(h3Text);

		Assert.Equal(expected, shortName);
	}

	[Fact]
	public void ExtractVoiceModelsShortNameFromHtml_PrefersTitleOverNavChromeH3()
	{
		// Real-world voice-models.com page structure: the first <h3> on the page
		// is sidebar/nav chrome ("Main / VM Models"), while the actual model name
		// lives in <title> with a " AI Voice Model" trailer. We keep ALL the
		// useful metadata (character context, epoch counts, training algorithm)
		// and only strip the marketing trailer.
		const string html = """
		<html>
		<head>
			<title>Open The Noor Guy [Samantha&#039;s Father] (RVC v2 | 700 Epochs) AI Voice Model</title>
			<meta property="og:title" content="Open The Noor Guy [Samantha&#039;s Father] (RVC v2 | 700 Epochs) AI Voice Model" />
		</head>
		<body>
			<aside><h3>Main / VM Models</h3></aside>
			<main>
				<h1>Open The Noor Guy [Samantha's Father] (RVC v2 | 700 Epochs)</h1>
			</main>
		</body>
		</html>
		""";

		var shortName = ModelDownloader.ExtractVoiceModelsShortNameFromHtml(html);

		// "|" is an invalid filename character so SanitizeFileName strips it,
		// leaving "RVC v2  700 Epochs" (two spaces) which CleanModelName
		// collapses to a single space.
		Assert.Equal("Open The Noor Guy [Samantha's Father] (RVC v2 700 Epochs)", shortName);
	}

	[Fact]
	public void ExtractVoiceModelsShortNameFromHtml_StripsAiVoiceModelTrailer()
	{
		const string html = "<html><head><title>Danica Fujiko (RVC v2 | 400 Epochs) AI Voice Model</title></head><body></body></html>";

		var shortName = ModelDownloader.ExtractVoiceModelsShortNameFromHtml(html);

		Assert.Equal("Danica Fujiko (RVC v2 400 Epochs)", shortName);
	}

	[Fact]
	public void ExtractVoiceModelsShortNameFromHtml_IgnoresChromeH3_WhenTitleIsGeneric()
	{
		// Title might be missing the model name entirely (e.g. just "Voice Models").
		// In that case we should still skip the chrome H3 and return null rather than
		// pollute the cache with "Main-VMModels" or similar.
		const string html = """
		<html>
		<head><title>Voice Models</title></head>
		<body><aside><h3>Main / VM Models</h3></aside></body>
		</html>
		""";

		var shortName = ModelDownloader.ExtractVoiceModelsShortNameFromHtml(html);

		Assert.Null(shortName);
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

	[Theory]
	[InlineData("https://drive.google.com/file/d/10Z7VmvRH0QxWQbuH6OPfUl79NGkkI26m/view?usp=drive_link", "10Z7VmvRH0QxWQbuH6OPfUl79NGkkI26m")]
	[InlineData("https://drive.google.com/file/d/abcDEF123_-/view", "abcDEF123_-")]
	[InlineData("https://drive.google.com/open?id=abcDEF123", "abcDEF123")]
	[InlineData("https://drive.google.com/uc?id=xyz789&export=download", "xyz789")]
	[InlineData("https://drive.usercontent.google.com/download?id=qrs456&export=download&authuser=0", "qrs456")]
	[InlineData("https://drive.google.com/?id=fromQuery", "fromQuery")]
	public void ExtractDriveFileId_ParsesSupportedShapes(string url, string expectedId)
	{
		var id = ModelDownloader.ExtractDriveFileId(url);

		Assert.Equal(expectedId, id);
	}

	[Theory]
	[InlineData("https://drive.google.com/")]
	[InlineData("https://drive.google.com/drive/folders/abc123")]
	[InlineData("https://example.com/file/d/notdrive/view")]
	public void ExtractDriveFileId_UnsupportedShapes_ReturnFromPathOrNull(string url)
	{
		ArgumentNullException.ThrowIfNull(url);

		var id = ModelDownloader.ExtractDriveFileId(url);

		// The path-shape regex matches `/file/d/{id}` anywhere; the third URL legitimately
		// triggers it. The first two have no recognizable id and return null.
		if (url.Contains("/file/d/", StringComparison.Ordinal))
		{
			Assert.Equal("notdrive", id);
		}
		else
		{
			Assert.Null(id);
		}
	}

	[Fact]
	public void BuildDriveConfirmUrl_VirusScanForm_ReturnsConfirmedUrl()
	{
		const string html = """
			<html><body>
			<form id="download-form" action="https://drive.usercontent.google.com/download">
				<input type="hidden" name="id" value="abc123">
				<input type="hidden" name="export" value="download">
				<input type="hidden" name="authuser" value="0">
				<input type="hidden" name="confirm" value="t-1234567890">
				<input type="hidden" name="uuid" value="some-uuid-here">
				<input type="submit" value="Download anyway">
			</form>
			</body></html>
			""";

		var url = ModelDownloader.BuildDriveConfirmUrl(html, fallbackBaseUrl: "https://drive.google.com/");

		Assert.NotNull(url);
		Assert.StartsWith("https://drive.usercontent.google.com/download?", url, StringComparison.Ordinal);
		Assert.Contains("id=abc123", url, StringComparison.Ordinal);
		Assert.Contains("confirm=t-1234567890", url, StringComparison.Ordinal);
		Assert.Contains("uuid=some-uuid-here", url, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildDriveConfirmUrl_NoForm_ReturnsNull()
	{
		const string html = "<html><body>No form here, just normal content.</body></html>";

		var url = ModelDownloader.BuildDriveConfirmUrl(html, fallbackBaseUrl: "https://drive.google.com/");

		Assert.Null(url);
	}

	[Fact]
	public void BuildDriveConfirmUrl_FormWithNoInputs_ReturnsNull()
	{
		const string html = """<form action="https://example.com/">no inputs</form>""";

		var url = ModelDownloader.BuildDriveConfirmUrl(html, fallbackBaseUrl: "https://drive.google.com/");

		Assert.Null(url);
	}

	[Theory]
	[InlineData("attachment; filename=\"Angelmouse.zip\"", "Angelmouse.zip")]
	[InlineData("attachment; filename=Angelmouse.zip", "Angelmouse.zip")]
	[InlineData("attachment; filename*=UTF-8''Angelmouse%20RVC.zip", "Angelmouse RVC.zip")]
	[InlineData("inline; filename=\"weird name with spaces.zip\"", "weird name with spaces.zip")]
	public void ExtractFilenameFromContentDisposition_ReturnsFilename(string header, string expected)
	{
		var name = ModelDownloader.ExtractFilenameFromContentDisposition(header);

		Assert.Equal(expected, name);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("attachment")]
	[InlineData("inline")]
	public void ExtractFilenameFromContentDisposition_NoFilename_ReturnsNull(string? header)
	{
		var name = ModelDownloader.ExtractFilenameFromContentDisposition(header);

		Assert.Null(name);
	}

	[Theory]
	[InlineData("Angelmouse.zip", "Angelmouse", true)]
	[InlineData("Rihanna.pth", "Rihanna", false)]
	[InlineData("Model_v2.onnx", "Model v2", false)]
	public void DeriveDriveNameAndType_WithFilename_UsesFilename(string fileName, string expectedName, bool expectedZip)
	{
		var (name, isZip) = ModelDownloader.DeriveDriveNameAndType(fileName, fileId: "ignored");

		Assert.Equal(expectedName, name);
		Assert.Equal(expectedZip, isZip);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void DeriveDriveNameAndType_WithoutFilename_FallsBackToFileId(string? fileName)
	{
		var (name, isZip) = ModelDownloader.DeriveDriveNameAndType(fileName, fileId: "abc123");

		Assert.Equal("drive_abc123", name);
		Assert.True(isZip);
	}

	[Theory]
	[InlineData("download-b80c517d514a487b805605c9cbc49c55", true)]
	[InlineData("Angelmouse", false)]
	[InlineData("Rihanna2009", false)]
	[InlineData("Voice Model v2", false)]
	[InlineData("Some Specific Name", false)]
	public void IsUsableName_HandlesResolverPlaceholdersAndRealNames(string name, bool expectedGeneric)
	{
		// Inverse: IsUsableName == !LooksGeneric. This test covers the leftover
		// cases from the (now-deleted) LooksLikeGenericResolvedName helper.
		Assert.Equal(expectedGeneric, !ModelDownloader.IsUsableName(name));
	}

	[Fact]
	public void ChooseVoiceModelsName_PageTitleWins_OverHuggingFaceRepoSlug()
	{
		// Repro of the 9q8 (Princess Peach) bug: voice-models.com page title
		// is "Princess Peach", but the page links to a HuggingFace zip whose
		// repo is "Princess-Peach-Samantha-Kelly". The HF resolver returns the
		// repo slug, which IS usable per IsUsableName, so the old override
		// (which only fired when the inner name was unusable) let the slug
		// leak through. The curated voice-models title must always win.
		var chosen = ModelDownloader.ChooseVoiceModelsName
		(
			innerResolvedName: "Princess-Peach-Samantha-Kelly",
			pageShortName: "Princess Peach"
		);

		Assert.Equal("Princess Peach", chosen);
	}

	[Fact]
	public void ChooseVoiceModelsName_FallsBackToInner_WhenPageTitleMissing()
	{
		var chosen = ModelDownloader.ChooseVoiceModelsName
		(
			innerResolvedName: "Princess-Peach-Samantha-Kelly",
			pageShortName: null
		);

		Assert.Equal("Princess-Peach-Samantha-Kelly", chosen);
	}

	[Fact]
	public void ChooseVoiceModelsName_FallsBackToInner_WhenPageTitleIsGeneric()
	{
		var chosen = ModelDownloader.ChooseVoiceModelsName
		(
			innerResolvedName: "Princess-Peach-Samantha-Kelly",
			pageShortName: "download"
		);

		Assert.Equal("Princess-Peach-Samantha-Kelly", chosen);
	}

	[Fact]
	public void ChooseVoiceModelsName_PageTitleWins_OverDriveFallback()
	{
		// 9Bj (Danica Fujiko) flow: inner Drive resolver couldn't recover the
		// filename, so it returned "drive_{fileId}" (unusable). Page title
		// "Danica Fujiko" must win. This was the case the original override
		// handled correctly.
		var chosen = ModelDownloader.ChooseVoiceModelsName
		(
			innerResolvedName: "drive_10Z7VmvRH0QxWQbuH6OPfUl79NGkkI26m",
			pageShortName: "Danica Fujiko"
		);

		Assert.Equal("Danica Fujiko", chosen);
	}

	[Fact]
	public async Task DownloadFileAsync_DnsFailure_ThrowsFriendlyErrorWithUrl()
	{
		// Repro of the models.weights.gg DNS death: HttpClient throws
		// HttpRequestException wrapping a SocketException. The user sees a
		// huge stack trace instead of a clean "this CDN is dead" message.
		using var handler = new ThrowingHttpMessageHandler
		(
			static _ => throw new HttpRequestException
			(
				"The requested name is valid, but no data of the requested type was found. (models.weights.gg:443)",
				new System.Net.Sockets.SocketException(11004)
			)
		);
		using var http = new HttpClient(handler);

		var url = "https://models.weights.gg/cln39enqr02vqws4h3gc407bb.zip";
		var dest = Path.Combine(Path.GetTempPath(), $"dl-test-{Guid.NewGuid():N}.zip");

		var ex = await Assert.ThrowsAsync<InvalidOperationException>
		(
			() => ModelDownloader.DownloadFileAsync(http, url, dest, CancellationToken.None)
		);

		Assert.Contains(url, ex.Message, StringComparison.Ordinal);
		Assert.NotNull(ex.InnerException);
		// The message should NOT echo the cryptic Windows DNS error verbatim --
		// users see "name is valid, but no data of the requested type was found"
		// and think their URL is malformed. Translate to plain English.
		Assert.DoesNotContain
		(
			"name is valid, but no data of the requested type was found",
			ex.Message,
			StringComparison.OrdinalIgnoreCase
		);
		// Should mention the host so the user knows which source is dead.
		Assert.Contains("models.weights.gg", ex.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData(System.Net.Sockets.SocketError.HostNotFound, "DNS lookup", "dead.example.com")]
	[InlineData(System.Net.Sockets.SocketError.NoData, "DNS lookup", "dead.example.com")]
	[InlineData(System.Net.Sockets.SocketError.NoRecovery, "DNS lookup", "dead.example.com")]
	[InlineData(System.Net.Sockets.SocketError.TryAgain, "timed out", "dead.example.com")]
	[InlineData(System.Net.Sockets.SocketError.ConnectionRefused, "refused", "dead.example.com")]
	[InlineData(System.Net.Sockets.SocketError.TimedOut, "timed out", "dead.example.com")]
	public void DescribeNetworkFailure_TranslatesSocketErrorsToPlainEnglish(System.Net.Sockets.SocketError code, string expectedSubstring, string expectedHost)
	{
		var inner = new System.Net.Sockets.SocketException((int)code);
		var ex = new HttpRequestException("cryptic windows text", inner);

		var message = ModelDownloader.DescribeNetworkFailure("https://dead.example.com/foo.zip", ex);

		Assert.Contains(expectedSubstring, message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains(expectedHost, message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void DescribeNetworkFailure_UnknownSocketError_IncludesErrorCodeAndHost()
	{
		var inner = new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AccessDenied);
		var ex = new HttpRequestException("denied", inner);

		var message = ModelDownloader.DescribeNetworkFailure("https://h.example.com/x", ex);

		Assert.Contains("h.example.com", message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("AccessDenied", message, StringComparison.Ordinal);
	}

	[Fact]
	public void DescribeNetworkFailure_NoSocketException_FallsBackToHttpMessage()
	{
		var ex = new HttpRequestException("plain http failure");

		var message = ModelDownloader.DescribeNetworkFailure("https://h.example.com/x", ex);

		Assert.Equal("plain http failure", message);
	}

	[Fact]
	public void DescribeNetworkFailure_MalformedUrl_FallsBackToGenericHostText()
	{
		var inner = new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
		var ex = new HttpRequestException("dns fail", inner);

		var message = ModelDownloader.DescribeNetworkFailure("not a url at all", ex);

		Assert.Contains("the remote host", message, StringComparison.OrdinalIgnoreCase);
	}

	private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
	{
		private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

		public ThrowingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
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
