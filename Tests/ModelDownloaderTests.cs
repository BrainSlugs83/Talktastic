using System.IO.Compression;

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

	[Fact]
	public void ResolveModelName_PrefersUsableInternalName()
	{
		var resolved = ModelDownloader.ResolveModelName("C:\\models\\BartSimpson_e230_s7360.pth", "fallback");

		Assert.Equal("BartSimpson_e230_s7360", resolved);
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

		Assert.Equal("BartSimpson_e230_s7360", ModelDownloader.DeriveNameFromDirectUrl(uri));
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

		Assert.Equal("en_US-ryan-high", derived);
	}

	[Fact]
	public void DeriveModelNameFromRepo_StripsPiperPrefix_WhenSubPathIsEmpty()
	{
		var derived = ModelDownloader.DeriveModelNameFromRepo("piper-en_US-ryan", "");

		Assert.Equal("en_US-ryan", derived);
	}

	[Fact]
	public void DeriveModelNameFromRepo_ReturnsRepoName_ForNonPiperRepo()
	{
		var derived = ModelDownloader.DeriveModelNameFromRepo("cool-voice-pack", "");

		Assert.Equal("cool-voice-pack", derived);
	}

	[Fact]
	public void LookupRegistry_ReturnsNull_WhenFileDoesNotExist()
	{
		var registryPath = GetRegistryPath();

		Assert.Null(ModelDownloader.LookupRegistry(registryPath, "https://example.com/model.onnx"));
	}

	[Fact]
	public void LookupRegistry_ReturnsMatchingName_WhenUrlExists()
	{
		var registryPath = GetRegistryPath();
		File.WriteAllLines
		(
			registryPath,
			[
				"https://example.com/voice.onnx\tegirl",
				"https://example.com/other.onnx\thomer",
			]
		);

		Assert.Equal("egirl", ModelDownloader.LookupRegistry(registryPath, "https://example.com/voice.onnx"));
	}

	[Fact]
	public void LookupRegistry_ReturnsNull_WhenUrlIsMissing()
	{
		var registryPath = GetRegistryPath();
		File.WriteAllText(registryPath, "https://example.com/voice.onnx\tegirl");

		Assert.Null(ModelDownloader.LookupRegistry(registryPath, "https://example.com/missing.onnx"));
	}

	[Fact]
	public void LookupRegistry_NormalizesUrlBeforeMatching()
	{
		var registryPath = GetRegistryPath();
		File.WriteAllText(registryPath, "https://example.com/models\tegirl");

		var result = ModelDownloader.LookupRegistry
		(
			registryPath,
			"HTTPS://EXAMPLE.COM/models/"
		);

		Assert.Equal("egirl", result);
	}

	[Fact]
	public void WriteRegistry_CreatesNewFile()
	{
		var registryPath = GetRegistryPath();

		ModelDownloader.WriteRegistry(registryPath, "https://example.com/model.onnx", "egirl");

		Assert.Equal
		(
			"https://example.com/model.onnx\tegirl",
			File.ReadAllText(registryPath).TrimEnd('\r', '\n')
		);
	}

	[Fact]
	public void WriteRegistry_ReplacesExistingEntryForSameUrl()
	{
		var registryPath = GetRegistryPath();
		File.WriteAllLines
		(
			registryPath,
			[
				"https://example.com/model.onnx\told-name",
				"https://example.com/other.onnx\thomer",
			]
		);

		ModelDownloader.WriteRegistry(registryPath, "https://example.com/model.onnx", "new-name");

		var lines = File.ReadAllLines(registryPath);
		Assert.Equal(2, lines.Length);
		Assert.Contains("https://example.com/model.onnx\tnew-name", lines, StringComparer.Ordinal);
	}

	[Fact]
	public void WriteRegistry_AppendsNewEntry_WhenUrlDoesNotExist()
	{
		var registryPath = GetRegistryPath();
		File.WriteAllText(registryPath, "https://example.com/model.onnx\tegirl");

		ModelDownloader.WriteRegistry(registryPath, "https://example.com/other.onnx", "homer");

		var lines = File.ReadAllLines(registryPath);
		Assert.Equal(2, lines.Length);
		Assert.Contains("https://example.com/model.onnx\tegirl", lines, StringComparer.Ordinal);
		Assert.Contains("https://example.com/other.onnx\thomer", lines, StringComparer.Ordinal);
	}

	[Fact]
	public void WriteRegistry_AndLookupRegistry_RoundTripNormalizedUrls()
	{
		var registryPath = GetRegistryPath();

		ModelDownloader.WriteRegistry
		(
			registryPath,
			"HTTPS://EXAMPLE.COM/Models/KeepCase?Voice=Ryan",
			"egirl"
		);

		var result = ModelDownloader.LookupRegistry
		(
			registryPath,
			"https://example.com/Models/KeepCase?Voice=Ryan"
		);

		Assert.Equal("egirl", result);
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

	private string GetRegistryPath()
	{
		return Path.Combine(_artifactRoot, $"{Guid.NewGuid():N}.tsv");
	}
}
