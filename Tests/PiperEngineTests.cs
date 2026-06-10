namespace Talktastic.Tests;

[Collection("AppPaths")]
public sealed class PiperEngineTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string[] _originalSearchBases;

	public PiperEngineTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"piper-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_originalSearchBases = [.. GetSearchBases()];
	}

	public void Dispose()
	{
		var searchBases = GetSearchBases();
		Array.Copy(_originalSearchBases, searchBases, _originalSearchBases.Length);

		if (Directory.Exists(_tempDir))
		{
			Directory.Delete(_tempDir, recursive: true);
		}
	}

	[Theory]
	[InlineData("https://example.com/voices/en_US-ryan-high.onnx", true)]
	[InlineData("HTTP://example.com/voices/en_US-ryan-high.onnx", true)]
	[InlineData("http://example.com/model.onnx?download=1", true)]
	[InlineData("ftp://example.com/model.onnx", false)]
	[InlineData("piper:en_US-ryan-high", false)]
	[InlineData("", false)]
	public void IsUrlVoice_Input_ExpectedResult(string voiceQuery, bool expected)
	{
		var result = PiperEngine.IsUrlVoice(voiceQuery);

		Assert.Equal(expected, result);
	}

	[Fact]
	public void IsUrlVoice_Null_ThrowsNullReferenceException()
	{
		Assert.Throws<NullReferenceException>(() => PiperEngine.IsUrlVoice(null!));
	}

	[Theory]
	[InlineData("piper:en_US-ryan-high", "en_US-ryan-high")]
	[InlineData("https://example.com/voices/en_US-ryan-high.onnx", "en_US-ryan-high")]
	[InlineData("https://example.com/voices/en_US-ryan-high.onnx.json", "en_US-ryan-high")]
	[InlineData("https://huggingface.co/rhasspy/piper-voices/tree/main/en/en_US/ryan/high", "ryan-high")]
	[InlineData("https://github.com/rhasspy/piper/releases/download/v1.0.0/en_GB-alba-medium.onnx?download=1", "en_GB-alba-medium")]
	public void GetModelName_Input_ExtractsExpectedModelName(string voiceQuery, string expected)
	{
		var modelName = PiperEngine.GetModelName(voiceQuery);

		Assert.Equal(expected, modelName);
	}

	[Theory]
	[InlineData("en_US-ryan-high", "Piper Ryan (high) - en-US")]
	[InlineData("fr_FR-siwis-medium", "Piper Siwis (medium) - fr-FR")]
	[InlineData("en_GB-british-female-medium", "Piper British-female (medium) - en-GB")]
	[InlineData("custom-model", "Piper (custom-model)")]
	public void GetDisplayName_ModelName_ReturnsFriendlyDisplayName(string modelName, string expected)
	{
		var displayName = PiperEngine.GetDisplayName(modelName);

		Assert.Equal(expected, displayName);
	}

	[Fact]
	public void FindVoicesDir_WhenVoicesDirectoryExists_ReturnsFirstMatch()
	{
		var firstBase = Path.Combine(_tempDir, "missing");
		var secondBase = Path.Combine(_tempDir, "cache-root");
		var expected = CreateVoicesDir(secondBase);
		SetSearchBases(firstBase, secondBase, Path.Combine(_tempDir, "fallback"));

		var voicesDir = PiperEngine.FindVoicesDir();

		Assert.Equal(expected, voicesDir);
	}

	[Fact]
	public void FindVoicesDir_WhenVoicesDirectoryMissing_ReturnsNull()
	{
		SetSearchBases
		(
			Path.Combine(_tempDir, "missing-a"),
			Path.Combine(_tempDir, "missing-b"),
			Path.Combine(_tempDir, "missing-c")
		);

		var voicesDir = PiperEngine.FindVoicesDir();

		Assert.Null(voicesDir);
	}

	[Fact]
	public void EnumerateCachedVoices_WithMatchingPairs_ReturnsOnlyOnnxFilesWithJson()
	{
		var voicesDir = CreateVoicesDir(Path.Combine(_tempDir, "cache-root"));
		WriteVoicePair(voicesDir, "alpha");
		File.WriteAllBytes(Path.Combine(voicesDir, "beta.onnx"), [0x01]);
		File.WriteAllText(Path.Combine(voicesDir, "gamma.onnx.json"), "{}");

		var nestedDir = Path.Combine(voicesDir, "nested");
		Directory.CreateDirectory(nestedDir);
		WriteVoicePair(nestedDir, "nested-voice");

		var voices = PiperEngine.EnumerateCachedVoices(voicesDir)
			.OrderBy(static voice => voice.Name, StringComparer.Ordinal)
			.ToArray();

		Assert.Single(voices);
		Assert.Equal("alpha", voices[0].Name);
		Assert.Equal(Path.Combine(voicesDir, "alpha.onnx"), voices[0].OnnxPath);
	}

	[Fact]
	public void EnumerateCachedVoices_WhenDirectoryMissing_ReturnsEmptySequence()
	{
		var voices = PiperEngine.EnumerateCachedVoices(Path.Combine(_tempDir, "missing"))
			.ToArray();

		Assert.Empty(voices);
	}

	[Theory]
	[InlineData("https://example.com/voices/en_US-ryan-high.onnx", "en_US-ryan-high")]
	[InlineData("https://example.com/voices/en_US-ryan-high.onnx.json", "en_US-ryan-high")]
	[InlineData("https://huggingface.co/rhasspy/piper-voices/tree/main/en/en_US/ryan/high", "ryan-high")]
	[InlineData("https://example.com/model", "model")]
	[InlineData("https://example.com/a/b/c", "b-c")]
	public void GetModelNameFromUrl_ExtractsModelName(string url, string expected)
	{
		var result = PiperEngine.GetModelNameFromUrl(url);

		Assert.Equal(expected, result);
	}

	[Theory]
	[InlineData("https://Example.COM/path/to/file", "https://example.com/path/to/file")]
	[InlineData("https://huggingface.co/model/", "https://huggingface.co/model")]
	public void NormalizeUrl_NormalizesSchemeHostAndTrailingSlash(string input, string expected)
	{
		var result = PiperEngine.NormalizeUrl(input);

		Assert.Equal(expected, result);
	}

	[Fact]
	public void ResolvePiperShorthand_FullModelName_ReturnsHuggingFaceUrls()
	{
		var result = PiperEngine.ResolvePiperShorthand("piper:en_US-ryan-high");

		Assert.Equal("en_US-ryan-high", result.ModelName);
		Assert.Equal
		(
			"https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/ryan/high/en_US-ryan-high.onnx",
			result.OnnxUrl
		);
		Assert.Equal(result.OnnxUrl + ".json", result.ConfigUrl);
	}

	[Fact]
	public void ResolvePiperShorthand_FriendlyName_ResolvesFromCatalog()
	{
		var result = PiperEngine.ResolvePiperShorthand("piper:Amy");

		Assert.Equal("en_US-amy-medium", result.ModelName);
		Assert.Contains("/en/en_US/amy/medium/en_US-amy-medium.onnx", result.OnnxUrl, StringComparison.Ordinal);
		Assert.Equal(result.OnnxUrl + ".json", result.ConfigUrl);
	}

	[Fact]
	public void ResolvePiperShorthand_UnknownFriendlyName_ThrowsArgumentException()
	{
		Assert.Throws<ArgumentException>(() => PiperEngine.ResolvePiperShorthand("piper:DefinitelyNotARealVoice"));
	}

	[Theory]
	[InlineData("Amy", "en_US-amy-medium")]
	[InlineData("NonexistentVoice", null)]
	public void ResolveFriendlyName_LooksUpCatalog(string name, string? expected)
	{
		var result = PiperEngine.ResolveFriendlyName(name);

		Assert.Equal(expected, result);
	}

	[Theory]
	[InlineData("rhasspy/piper-voices", "en/en_US/amy/medium", "en US-amy-medium")]
	[InlineData("rhasspy/piper-voices", "", "voices")]
	public void DeriveModelNameFromRepo_DerivesCorrectly(string repo, string subPath, string expected)
	{
		var result = ModelDownloader.DeriveModelNameFromRepo(repo, subPath);

		Assert.Equal(expected, result);
	}

	[Fact]
	public void FindPathInJson_FindsOnnxPath()
	{
		const string json = """[{"path":"en_US-ryan-high.onnx","size":123}]""";

		var result = ModelDownloader.FindPathInJson(json, ModelDownloader.HfOnnxPathRegex(), ".onnx.json");

		Assert.Equal("en_US-ryan-high.onnx", result);
	}

	[Fact]
	public void FindPathInJson_NoOnnxFile_ReturnsNull()
	{
		const string json = """[{"path":"README.md","size":123}]""";

		var result = ModelDownloader.FindPathInJson(json, ModelDownloader.HfOnnxPathRegex(), ".onnx.json");

		Assert.Null(result);
	}

	[Fact]
	public void FindPathInJson_ExcludesOnnxJson()
	{
		const string json = """[{"path":"en_US-ryan-high.onnx.json","size":123}]""";

		var result = ModelDownloader.FindPathInJson(json, ModelDownloader.HfOnnxPathRegex(), ".onnx.json");

		Assert.Null(result);
	}

	[Fact]
	public void FindOnnxConfigCompanions_FindsStandardCompanion()
	{
		const string json = """[{"path":"en_US-ryan-high.onnx","size":123},{"path":"en_US-ryan-high.onnx.json","size":456}]""";

		var result = ModelDownloader.FindOnnxConfigCompanionsForTest(json, "en_US-ryan-high.onnx", "https://example.com/resolve/main");

		Assert.NotNull(result);
		Assert.Single(result);
		Assert.Equal("https://example.com/resolve/main/en_US-ryan-high.onnx.json", result[0]);
	}

	[Fact]
	public void FindOnnxConfigCompanions_FallsBackToConfigJson()
	{
		const string json = """[{"path":"models/model.onnx","size":123},{"path":"models/config.json","size":456}]""";

		var result = ModelDownloader.FindOnnxConfigCompanionsForTest(json, "models/model.onnx", "https://example.com/resolve/main");

		Assert.NotNull(result);
		Assert.Single(result);
		Assert.Equal("https://example.com/resolve/main/models/config.json", result[0]);
	}

	[Fact]
	public void FindAssetUrlInJson_FindsOnnxUrl()
	{
		const string json = """{"assets":[{"browser_download_url":"https://example.com/en_US-ryan-high.onnx","size":123}]}""";

		var result = ModelDownloader.FindAssetUrlInJson(json, ModelDownloader.GhOnnxAssetRegex(), ".onnx.json");

		Assert.Equal("https://example.com/en_US-ryan-high.onnx", result);
	}

	[Fact]
	public void FindAssetUrlInJson_ExcludesOnnxJson()
	{
		const string json = """{"assets":[{"browser_download_url":"https://example.com/en_US-ryan-high.onnx.json","size":123}]}""";

		var result = ModelDownloader.FindAssetUrlInJson(json, ModelDownloader.GhOnnxAssetRegex(), ".onnx.json");

		Assert.Null(result);
	}

	private static string[] GetSearchBases()
	{
		return AppPaths.SearchBases;
	}

	private static void SetSearchBases(params string[] searchBases)
	{
		var field = GetSearchBases();
		for (var i = 0; i < field.Length; i++)
		{
			field[i] = i < searchBases.Length ? searchBases[i] : Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		}
	}

	private static string CreateVoicesDir(string baseDir)
	{
		var voicesDir = Path.Combine(baseDir, ".piper-tts", "voices");
		Directory.CreateDirectory(voicesDir);
		return voicesDir;
	}

	private static void WriteVoicePair(string voicesDir, string modelName)
	{
		File.WriteAllBytes(Path.Combine(voicesDir, $"{modelName}.onnx"), [0x08]);
		File.WriteAllText(Path.Combine(voicesDir, $"{modelName}.onnx.json"), "{}");
	}
}
