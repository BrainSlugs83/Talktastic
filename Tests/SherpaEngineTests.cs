using System.Buffers.Binary;
using System.Text;

namespace Talktastic.Tests;

#pragma warning disable CA1814
#pragma warning disable CA1861

[Collection("AppPaths")]
public sealed class SherpaEngineTests : IDisposable
{
	private readonly string _artifactRoot;
	private readonly string[] _originalSearchBases;

	public SherpaEngineTests()
	{
		_artifactRoot = Path.Combine(AppContext.BaseDirectory, nameof(SherpaEngineTests), Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_artifactRoot);
		_originalSearchBases = [.. AppPaths.SearchBases];
	}

	public void Dispose()
	{
		Array.Copy(_originalSearchBases, AppPaths.SearchBases, _originalSearchBases.Length);

		if (Directory.Exists(_artifactRoot))
		{
			Directory.Delete(_artifactRoot, recursive: true);
		}
	}

	[Theory]
	[InlineData("needle in haystack", "needle", true)]
	[InlineData("look for a needle here", "needle", true)]
	[InlineData("find-at-end", "end", true)]
	[InlineData("abcdef", "gh", false)]
	[InlineData("abc", "abcdef", false)]
	[InlineData("exact", "exact", true)]
	[InlineData("abc", "", true)]
	public void ContainsSequence_ReturnsExpectedResult(string haystackText, string needleText, bool expected)
	{
		var haystack = Encoding.UTF8.GetBytes(haystackText);
		var needle = Encoding.UTF8.GetBytes(needleText);

		var result = SherpaEngine.ContainsSequence(haystack, needle);

		Assert.Equal(expected, result);
	}

	[Fact]
	public void ContainsSequence_FindsNeedleAtMiddleOfBinaryPayload()
	{
		var haystack = new byte[] { 0xFF, 0x10, 0x20, 0x30, 0x40, 0xAA };
		var needle = new byte[] { 0x20, 0x30 };

		var result = SherpaEngine.ContainsSequence(haystack, needle);

		Assert.True(result);
	}

	[Fact]
	public void HasSherpaMetadata_WhenModelTypePresent_ReturnsTrue()
	{
		var data = Encoding.UTF8.GetBytes("prefix:model_type:suffix");

		var result = SherpaEngine.HasSherpaMetadata(data);

		Assert.True(result);
	}

	[Fact]
	public void HasSherpaMetadata_WhenModelTypeMissing_ReturnsFalse()
	{
		var data = Encoding.UTF8.GetBytes("voice=ryan;sample_rate=22050");

		var result = SherpaEngine.HasSherpaMetadata(data);

		Assert.False(result);
	}

	[Fact]
	public void HasSherpaMetadata_WhenDataEmpty_ReturnsFalse()
	{
		var result = SherpaEngine.HasSherpaMetadata([]);

		Assert.False(result);
	}

	[Fact]
	public void EncodeMetadataProps_SinglePair_ProducesNonEmptyBytes()
	{
		var metadata = new Dictionary<string, string>
		{
			["model_type"] = "vits",
		};

		var encoded = SherpaEngine.EncodeMetadataProps(metadata);

		Assert.NotEmpty(encoded);
	}

	[Fact]
	public void EncodeMetadataProps_MultiplePairs_ContainsAllKeysAndValues()
	{
		var metadata = new Dictionary<string, string>
		{
			["model_type"] = "vits",
			["voice"] = "en-us",
			["sample_rate"] = "22050",
		};

		var encoded = SherpaEngine.EncodeMetadataProps(metadata);

		AssertContainsUtf8(encoded, "model_type");
		AssertContainsUtf8(encoded, "vits");
		AssertContainsUtf8(encoded, "voice");
		AssertContainsUtf8(encoded, "en-us");
		AssertContainsUtf8(encoded, "sample_rate");
		AssertContainsUtf8(encoded, "22050");
	}

	[Fact]
	public void EncodeMetadataProps_EmptyDictionary_ReturnsEmptyArray()
	{
		var encoded = SherpaEngine.EncodeMetadataProps([]);

		Assert.Empty(encoded);
	}

	[Fact]
	public void EncodeMetadataProps_RoundTripPayloadContainsUtf8KeyAndValueStrings()
	{
		var metadata = new Dictionary<string, string>
		{
			["language"] = "English",
		};

		var encoded = SherpaEngine.EncodeMetadataProps(metadata);

		AssertContainsUtf8(encoded, "language");
		AssertContainsUtf8(encoded, "English");
	}

	[Fact]
	public void WriteTag_Field1WireType2_WritesSingleByte0A()
	{
		using var stream = new MemoryStream();

		SherpaEngine.WriteTag(stream, fieldNumber: 1, wireType: 2);

		Assert.Equal(new byte[] { 0x0A }, stream.ToArray());
	}

	[Fact]
	public void WriteTag_Field14WireType2_WritesSingleByte72()
	{
		using var stream = new MemoryStream();

		SherpaEngine.WriteTag(stream, fieldNumber: 14, wireType: 2);

		Assert.Equal(new byte[] { 0x72 }, stream.ToArray());
	}

	[Fact]
	public void WriteTag_Field16WireType2_WritesTwoVarintBytes()
	{
		using var stream = new MemoryStream();

		SherpaEngine.WriteTag(stream, fieldNumber: 16, wireType: 2);

		Assert.Equal(new byte[] { 0x82, 0x01 }, stream.ToArray());
	}

	[Fact]
	public void WriteVarint_SmallValue_WritesSingleByte()
	{
		using var stream = new MemoryStream();

		SherpaEngine.WriteVarint(stream, 127);

		Assert.Equal(new byte[] { 0x7F }, stream.ToArray());
	}

	[Fact]
	public void WriteVarint_Value128_WritesExpectedBytes()
	{
		using var stream = new MemoryStream();

		SherpaEngine.WriteVarint(stream, 128);

		Assert.Equal(new byte[] { 0x80, 0x01 }, stream.ToArray());
	}

	[Fact]
	public void WriteVarint_Value300_WritesExpectedBytes()
	{
		using var stream = new MemoryStream();

		SherpaEngine.WriteVarint(stream, 300);

		Assert.Equal(new byte[] { 0xAC, 0x02 }, stream.ToArray());
	}

	[Fact]
	public void WriteVarint_Zero_WritesSingleZeroByte()
	{
		using var stream = new MemoryStream();

		SherpaEngine.WriteVarint(stream, 0);

		Assert.Equal(new byte[] { 0x00 }, stream.ToArray());
	}

	[Fact]
	public void BuildWav_EmptySamples_ReturnsHeaderOnly()
	{
		var wav = SherpaEngine.BuildWav([], 22050);

		Assert.Equal(44, wav.Length);
		Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
		Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
		Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
		Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
		Assert.Equal(0, ReadInt32LittleEndian(wav, 40));
	}

	[Fact]
	public void BuildWav_KnownSamples_WritesExpectedHeaderFields()
	{
		var samples = new[] { -1.0f, 0.0f, 1.0f };

		var wav = SherpaEngine.BuildWav(samples, 24000);

		Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
		Assert.Equal(36 + (samples.Length * 2), ReadInt32LittleEndian(wav, 4));
		Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
		Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
		Assert.Equal(16, ReadInt32LittleEndian(wav, 16));
		Assert.Equal(1, ReadInt16LittleEndian(wav, 20));
		Assert.Equal(1, ReadInt16LittleEndian(wav, 22));
		Assert.Equal(24000, ReadInt32LittleEndian(wav, 24));
		Assert.Equal(24000 * 2, ReadInt32LittleEndian(wav, 28));
		Assert.Equal(2, ReadInt16LittleEndian(wav, 32));
		Assert.Equal(16, ReadInt16LittleEndian(wav, 34));
		Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
	}

	[Fact]
	public void BuildWav_SampleRate_IsEncodedAtOffset24()
	{
		var sampleRate = 44100;

		var wav = SherpaEngine.BuildWav([0.25f], sampleRate);

		Assert.Equal(sampleRate, ReadInt32LittleEndian(wav, 24));
	}

	[Fact]
	public void BuildWav_DataSize_EqualsSampleCountTimesTwo()
	{
		var samples = new[] { 0.1f, 0.2f, 0.3f, 0.4f };

		var wav = SherpaEngine.BuildWav(samples, 16000);

		Assert.Equal(samples.Length * 2, ReadInt32LittleEndian(wav, 40));
		Assert.Equal(44 + (samples.Length * 2), wav.Length);
	}

	[Fact]
	public void BuildWav_SamplesAreClampedToMinusOneThroughOne()
	{
		var wav = SherpaEngine.BuildWav([-2.0f, -1.0f, 0.0f, 1.0f, 2.0f], 22050);
		var pcm = ReadPcm16Samples(wav);

		Assert.Equal((short)-32767, pcm[0]);
		Assert.Equal((short)-32767, pcm[1]);
		Assert.Equal((short)0, pcm[2]);
		Assert.Equal(short.MaxValue, pcm[3]);
		Assert.Equal(short.MaxValue, pcm[4]);
	}

	[Fact]
	public void EnsureTokensFile_ExistingTokensFile_ReturnsExistingPath()
	{
		var modelPath = CreateModelFile("existing", [0x08]);
		var tokensPath = Path.ChangeExtension(modelPath, ".tokens.txt");
		File.WriteAllText(tokensPath, "existing 0\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

		var result = SherpaEngine.EnsureTokensFile(modelPath);

		Assert.Equal(tokensPath, result);
		Assert.Equal("existing 0\n", File.ReadAllText(tokensPath));
	}

	[Fact]
	public void EnsureTokensFile_MissingConfig_ThrowsFileNotFoundException()
	{
		var modelPath = CreateModelFile("missing-config", [0x08]);

		var exception = Assert.Throws<FileNotFoundException>
		(
			() => SherpaEngine.EnsureTokensFile(modelPath)
		);

		Assert.Contains(modelPath + ".json", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void EnsureTokensFile_ConfigMissingPhonemeMap_ThrowsInvalidDataException()
	{
		var modelPath = CreateModelFile("missing-phoneme-map", [0x08]);
		File.WriteAllText(modelPath + ".json", """{"audio":{"sample_rate":22050}}""", Encoding.UTF8);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => SherpaEngine.EnsureTokensFile(modelPath)
		);

		Assert.Contains("phoneme_id_map", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void EnsureTokensFile_GeneratesSortedTokenFileWithoutBom()
	{
		var modelPath = CreateModelFile("generate-tokens", [0x08]);
		File.WriteAllText
		(
			modelPath + ".json",
			"""
			{
				"phoneme_id_map":
				{
					"b": [4, 2],
					"_": [0],
					"a": [3],
					"c": [1]
				}
			}
			""",
			Encoding.UTF8
		);

		var result = SherpaEngine.EnsureTokensFile(modelPath);

		Assert.Equal(Path.ChangeExtension(modelPath, ".tokens.txt"), result);
		Assert.Equal
		(
			"_ 0\nc 1\nb 2\na 3\nb 4\n",
			File.ReadAllText(result)
		);
		Assert.False(File.ReadAllBytes(result).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
	}

	[Fact]
	public void EnsureEspeakData_ModelParentContainsEspeakData_ReturnsNearbyDirectory()
	{
		var modelPath = CreateModelFile(Path.Combine(".piper-tts", "voices", "en_US-ryan-high"), [0x08]);
		var expected = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(modelPath)!)!, "espeak-ng-data");
		Directory.CreateDirectory(expected);
		File.WriteAllText(Path.Combine(expected, "voices.txt"), "hello");

		var result = SherpaEngine.EnsureEspeakData(modelPath);

		Assert.Equal(expected, result);
	}

	[Fact]
	public void EnsureEspeakData_SearchBasesContainEspeakData_ReturnsFirstExistingSearchBase()
	{
		var searchBases = ConfigureSearchBases();
		var modelPath = CreateModelFile("search-base", [0x08]);
		var expected = Path.Combine(searchBases[1], ".piper-tts", "espeak-ng-data");
		Directory.CreateDirectory(expected);
		File.WriteAllText(Path.Combine(expected, "phontab"), "phonemes");

		var result = SherpaEngine.EnsureEspeakData(modelPath);

		Assert.Equal(expected, result);
	}

	[Fact]
	public void EnsureEspeakData_WhenMissing_ExtractsEmbeddedEspeakData()
	{
		var searchBases = ConfigureSearchBases();
		var modelPath = CreateModelFile("extract-espeak", [0x08]);

		var result = SherpaEngine.EnsureEspeakData(modelPath);

		Assert.Equal(Path.Combine(searchBases[0], ".piper-tts", "espeak-ng-data"), result);
		Assert.True(Directory.Exists(result));
		Assert.NotEmpty(Directory.EnumerateFiles(result, "*", SearchOption.AllDirectories));
	}

	[Fact]
	public void EnsureOnnxMetadata_WhenMetadataAlreadyPresent_DoesNotModifyFile()
	{
		var modelPath = CreateModelFile("has-metadata", Encoding.UTF8.GetBytes("prefix model_type suffix"));
		var before = File.ReadAllBytes(modelPath);

		SherpaEngine.EnsureOnnxMetadata(modelPath);

		Assert.Equal(before, File.ReadAllBytes(modelPath));
	}

	[Fact]
	public void EnsureOnnxMetadata_WhenConfigMissing_DoesNotModifyFile()
	{
		var modelPath = CreateModelFile("missing-config-metadata", [0x08, 0x01, 0x02]);
		var before = File.ReadAllBytes(modelPath);

		SherpaEngine.EnsureOnnxMetadata(modelPath);

		Assert.Equal(before, File.ReadAllBytes(modelPath));
		Assert.False(SherpaEngine.HasSherpaMetadata(before));
	}

	[Fact]
	public void EnsureOnnxMetadata_AppendsSherpaMetadataUsingConfigValues()
	{
		var modelPath = CreateModelFile("patch-metadata", [0x08, 0x01, 0x02]);
		File.WriteAllText
		(
			modelPath + ".json",
			"""
			{
				"audio": { "sample_rate": 16000 },
				"espeak": { "voice": "en-gb" },
				"num_speakers": 3
			}
			""",
			Encoding.UTF8
		);

		var beforeLength = new FileInfo(modelPath).Length;

		SherpaEngine.EnsureOnnxMetadata(modelPath);

		var after = File.ReadAllBytes(modelPath);
		Assert.True(after.Length > beforeLength);
		Assert.True(SherpaEngine.HasSherpaMetadata(after));
		AssertContainsUtf8(after, "sample_rate");
		AssertContainsUtf8(after, "16000");
		AssertContainsUtf8(after, "voice");
		AssertContainsUtf8(after, "en-gb");
		AssertContainsUtf8(after, "n_speakers");
		AssertContainsUtf8(after, "3");
	}

	[Fact]
	public void EnsureOnnxMetadata_MissingOptionalConfigFields_UsesDefaults()
	{
		var modelPath = CreateModelFile("patch-default-metadata", [0x08, 0x01, 0x02]);
		File.WriteAllText(modelPath + ".json", """{}""", Encoding.UTF8);

		SherpaEngine.EnsureOnnxMetadata(modelPath);

		var after = File.ReadAllBytes(modelPath);
		Assert.True(SherpaEngine.HasSherpaMetadata(after));
		AssertContainsUtf8(after, "22050");
		AssertContainsUtf8(after, "en-us");
		AssertContainsUtf8(after, "1");
		AssertContainsUtf8(after, "English");
		AssertContainsUtf8(after, "piper");
	}

	private static void AssertContainsUtf8(byte[] haystack, string expectedText)
	{
		var needle = Encoding.UTF8.GetBytes(expectedText);
		Assert.True
		(
			SherpaEngine.ContainsSequence(haystack, needle),
			$"Expected encoded bytes to contain '{expectedText}'."
		);
	}

	private static short[] ReadPcm16Samples(byte[] wav)
	{
		var sampleCount = (wav.Length - 44) / 2;
		var samples = new short[sampleCount];

		for (var i = 0; i < sampleCount; i++)
		{
			samples[i] = ReadInt16LittleEndian(wav, 44 + (i * 2));
		}

		return samples;
	}

	private static short ReadInt16LittleEndian(byte[] data, int offset)
	{
		return BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, sizeof(short)));
	}

	private static int ReadInt32LittleEndian(byte[] data, int offset)
	{
		return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, sizeof(int)));
	}

	private string[] ConfigureSearchBases()
	{
		var searchBases = new[]
		{
			Path.Combine(_artifactRoot, "base-0"),
			Path.Combine(_artifactRoot, "base-1"),
			Path.Combine(_artifactRoot, "base-2"),
		};

		Array.Copy(searchBases, AppPaths.SearchBases, searchBases.Length);
		return searchBases;
	}

	private string CreateModelFile(string name, byte[] bytes)
	{
		var modelPath = Path.Combine(_artifactRoot, name + ".onnx");
		Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
		File.WriteAllBytes(modelPath, bytes);
		return modelPath;
	}
}
