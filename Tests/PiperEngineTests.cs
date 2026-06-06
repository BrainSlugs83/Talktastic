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
