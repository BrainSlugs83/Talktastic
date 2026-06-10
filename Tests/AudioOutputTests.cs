using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.CognitiveServices.Speech;

namespace Talktastic.Tests;

public sealed class AudioOutputTests : IDisposable
{
	private readonly string _artifactRoot = Path.Combine
	(
		AppContext.BaseDirectory,
		"TestArtifacts",
		nameof(AudioOutputTests),
		Guid.NewGuid().ToString("N")
	);

	[Theory]
	[InlineData(SpeechSynthesisOutputFormat.Riff8Khz16BitMonoPcm, 8000, true)]
	[InlineData(SpeechSynthesisOutputFormat.Raw8Khz16BitMonoPcm, 8000, false)]
	[InlineData(SpeechSynthesisOutputFormat.Riff16Khz16BitMonoPcm, 16000, true)]
	[InlineData(SpeechSynthesisOutputFormat.Raw16Khz16BitMonoPcm, 16000, false)]
	[InlineData(SpeechSynthesisOutputFormat.Riff24Khz16BitMonoPcm, 24000, true)]
	[InlineData(SpeechSynthesisOutputFormat.Raw24Khz16BitMonoPcm, 24000, false)]
	[InlineData(SpeechSynthesisOutputFormat.Riff22050Hz16BitMonoPcm, 22050, true)]
	[InlineData(SpeechSynthesisOutputFormat.Raw22050Hz16BitMonoPcm, 22050, false)]
	[InlineData(SpeechSynthesisOutputFormat.Riff44100Hz16BitMonoPcm, 44100, true)]
	[InlineData(SpeechSynthesisOutputFormat.Raw44100Hz16BitMonoPcm, 44100, false)]
	[InlineData(SpeechSynthesisOutputFormat.Riff48Khz16BitMonoPcm, 48000, true)]
	[InlineData(SpeechSynthesisOutputFormat.Raw48Khz16BitMonoPcm, 48000, false)]
	public void GetFormatInfo_SupportedFormat_ReturnsExpectedInfo
	(
		SpeechSynthesisOutputFormat outputFormat,
		int expectedSampleRate,
		bool expectedHasRiffHeader
	)
	{
		var result = AudioOutput.GetFormatInfo(outputFormat);

		Assert.Equal(expectedSampleRate, result.SampleRate);
		Assert.Equal(expectedHasRiffHeader, result.HasRiffHeader);
	}

	[Fact]
	public void GetFormatInfo_UnsupportedFormat_ThrowsInvalidOperationException()
	{
		var exception = Assert.Throws<InvalidOperationException>
		(
			() => AudioOutput.GetFormatInfo((SpeechSynthesisOutputFormat)int.MaxValue)
		);

		Assert.Contains("16-bit mono PCM", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void StripWaveHeader_ValidWave_ReturnsPcmPayload()
	{
		var expectedPayload = BuildPcm16Bytes(0, 16384, -16384);
		var wavBytes = BuildWavBytes(16000, 1, 16, expectedPayload);

		var actual = AudioOutput.StripWaveHeader(wavBytes);

		Assert.Equal(expectedPayload, actual);
	}

	[Fact]
	public void StripWaveHeader_HeaderOnlyWave_ReturnsEmptyPayload()
	{
		var wavBytes = BuildWavBytes(16000, 1, 16, []);

		var actual = AudioOutput.StripWaveHeader(wavBytes);

		Assert.Empty(actual);
	}

	[Fact]
	public void StripWaveHeader_TooShort_ThrowsInvalidOperationException()
	{
		Assert.Throws<InvalidOperationException>
		(
			() => AudioOutput.StripWaveHeader(new byte[43])
		);
	}

	[Theory]
	[InlineData(true, false)]
	[InlineData(false, true)]
	public void StripWaveHeader_InvalidSignature_ThrowsInvalidOperationException(bool corruptRiff, bool corruptWave)
	{
		var wavBytes = BuildWavBytes(16000, 1, 16, BuildPcm16Bytes(0, 1));

		if (corruptRiff)
		{
			Encoding.ASCII.GetBytes("NOPE").CopyTo(wavBytes, 0);
		}

		if (corruptWave)
		{
			Encoding.ASCII.GetBytes("NOPE").CopyTo(wavBytes, 8);
		}

		Assert.Throws<InvalidOperationException>
		(
			() => AudioOutput.StripWaveHeader(wavBytes)
		);
	}

	[Fact]
	public void EnsureDirectoryExists_NestedOutputPath_CreatesDirectory()
	{
		var outputPath = Path.Combine(_artifactRoot, "nested", "audio", "output.ogg");

		AudioOutput.EnsureDirectoryExists(outputPath);

		Assert.True(Directory.Exists(Path.GetDirectoryName(outputPath)));
	}

	[Fact]
	public void EnsureDirectoryExists_NullPath_ThrowsArgumentNullException()
	{
		Assert.Throws<ArgumentNullException>
		(
			() => AudioOutput.EnsureDirectoryExists(null!)
		);
	}

	[Fact]
	public async Task WriteOggOpusAsync_RiffInput_WritesOggFileAndMetadata()
	{
		var outputPath = Path.Combine(_artifactRoot, "encoded", "speech.ogg");
		var wavBytes = BuildWavBytes
		(
			16000,
			1,
			16,
			BuildPcm16Bytes(0, 4096, -4096, 2048, -2048, 1024)
		);
		var metadata = new AudioMetadata("Ada", "Synthetic greeting");

		await AudioOutput.WriteOggOpusAsync
		(
			wavBytes,
			SpeechSynthesisOutputFormat.Riff16Khz16BitMonoPcm,
			outputPath,
			metadata
		);

		var fileBytes = await File.ReadAllBytesAsync(outputPath);

		Assert.True(fileBytes.Length > 0);
		Assert.True(fileBytes.AsSpan(0, 4).SequenceEqual("OggS"u8));
		Assert.True(ContainsUtf8(fileBytes, "Ada"));
		Assert.True(ContainsUtf8(fileBytes, "Synthetic greeting"));
	}

	[Fact]
	public async Task WriteOggOpusAsync_UnsupportedFormat_ThrowsInvalidOperationException()
	{
		await Assert.ThrowsAsync<InvalidOperationException>
		(
			() => AudioOutput.WriteOggOpusAsync
			(
				BuildPcm16Bytes(0, 1),
				(SpeechSynthesisOutputFormat)int.MaxValue,
				Path.Combine(_artifactRoot, "bad", "output.ogg")
			)
		);
	}

	public void Dispose()
	{
		if (Directory.Exists(_artifactRoot))
		{
			Directory.Delete(_artifactRoot, recursive: true);
		}
	}

	private static byte[] BuildWavBytes(int sampleRate, short channels, short bitsPerSample, byte[] pcmBytes)
	{
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

		var blockAlign = channels * (bitsPerSample / 8);
		var byteRate = sampleRate * blockAlign;

		writer.Write("RIFF"u8);
		writer.Write(36 + pcmBytes.Length);
		writer.Write("WAVE"u8);
		writer.Write("fmt "u8);
		writer.Write(16);
		writer.Write((short)1);
		writer.Write(channels);
		writer.Write(sampleRate);
		writer.Write(byteRate);
		writer.Write((short)blockAlign);
		writer.Write(bitsPerSample);
		writer.Write("data"u8);
		writer.Write(pcmBytes.Length);
		writer.Write(pcmBytes);
		writer.Flush();

		return stream.ToArray();
	}

	private static byte[] BuildPcm16Bytes(params short[] samples)
	{
		var bytes = new byte[samples.Length * sizeof(short)];

		for (var index = 0; index < samples.Length; index++)
		{
			BinaryPrimitives.WriteInt16LittleEndian
			(
				bytes.AsSpan(index * sizeof(short), sizeof(short)),
				samples[index]
			);
		}

		return bytes;
	}

	[Theory]
	[InlineData("1 - H32T13       ", "1 - H32T13")]
	[InlineData("H32T13        (AMD High Definition Audio Device)", "H32T13 (AMD High Definition Audio Device)")]
	[InlineData("  Speakers (Yeti Nano)  ", "Speakers (Yeti Nano)")]
	[InlineData("Speakers\r\n(High Definition Audio Device)", "Speakers (High Definition Audio Device)")]
	[InlineData("Tab\tSeparated\tName", "Tab Separated Name")]
	[InlineData("", "")]
	[InlineData(null, "")]
	public void NormalizeDeviceName_CollapsesWhitespaceAndStripsControlChars(string? input, string expected)
	{
		Assert.Equal(expected, AudioOutput.NormalizeDeviceName(input));
	}

	[Fact]
	public void ResolveDevice_ExactNameMatch_IgnoresPaddingAndCase()
	{
		var devices = new (string Name, string? Id)[]
		{
			("1 - H32T13       ", "id-1"),
			("Speakers (Yeti Nano)", "id-2"),
		};

		var match = AudioOutput.ResolveDevice(devices, static d => d.Name, static d => d.Id, "1 - h32t13");

		Assert.Equal("id-1", match.Id);
	}

	[Fact]
	public void ResolveDevice_UniqueSubstring_ReturnsMatch()
	{
		var devices = new (string Name, string? Id)[]
		{
			("1 - H32T13 (AMD High Definition Audio Device)", "id-1"),
			("Speakers (Yeti Nano)", "id-2"),
		};

		var match = AudioOutput.ResolveDevice(devices, static d => d.Name, static d => d.Id, "Yeti");

		Assert.Equal("id-2", match.Id);
	}

	[Fact]
	public void ResolveDevice_ExactNameWins_OverSubstringAmbiguity()
	{
		var devices = new (string Name, string? Id)[]
		{
			("H32T13", "id-1"),
			("H32T13 Extended", "id-2"),
		};

		var match = AudioOutput.ResolveDevice(devices, static d => d.Name, static d => d.Id, "H32T13");

		Assert.Equal("id-1", match.Id);
	}

	[Fact]
	public void ResolveDevice_MatchesSecondaryKeyVerbatim()
	{
		var devices = new (string Name, string? Id)[]
		{
			("Speakers", "{0.0.0.00000000}.{abc}"),
			("Headphones", "{0.0.0.00000000}.{def}"),
		};

		var match = AudioOutput.ResolveDevice(devices, static d => d.Name, static d => d.Id, "{0.0.0.00000000}.{def}");

		Assert.Equal("Headphones", match.Name);
	}

	[Fact]
	public void ResolveDevice_NoMatch_ThrowsInvalidOperationException()
	{
		var devices = new (string Name, string? Id)[] { ("Speakers (Yeti Nano)", "id-1") };

		var exception = Assert.Throws<InvalidOperationException>
		(
			() => AudioOutput.ResolveDevice(devices, static d => d.Name, static d => d.Id, "Bose")
		);

		Assert.Contains("No speaker matched 'Bose'", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ResolveDevice_AmbiguousSubstring_ThrowsWithNormalizedNames()
	{
		var devices = new (string Name, string? Id)[]
		{
			("1 - H32T13       (AMD High Definition Audio Device)", "id-1"),
			("2 - H32T13 (AMD High Definition Audio Device)", "id-2"),
		};

		var exception = Assert.Throws<InvalidOperationException>
		(
			() => AudioOutput.ResolveDevice(devices, static d => d.Name, static d => d.Id, "H32T13")
		);

		Assert.Contains("ambiguous", exception.Message, StringComparison.Ordinal);
		Assert.Contains("1 - H32T13 (AMD High Definition Audio Device)", exception.Message, StringComparison.Ordinal);
		Assert.Contains("2 - H32T13 (AMD High Definition Audio Device)", exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("  ", exception.Message, StringComparison.Ordinal);
	}

	private static bool ContainsUtf8(byte[] bytes, string value)
	{
		return bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) >= 0;
	}
}

public sealed class Id3WriterTests
{
	[Fact]
	public void CreateTag_Metadata_WritesId3HeaderAndFrames()
	{
		var metadata = new AudioMetadata("Ada", "Hello from Talktastic");

		var tag = Id3Writer.CreateTag(metadata);
		var parsed = ParseId3Tag(tag);

		Assert.Equal("ID3", Encoding.ASCII.GetString(tag, 0, 3));
		Assert.Equal(3, tag[3]);
		Assert.Equal(0, tag[4]);
		Assert.Equal(0, tag[5]);
		Assert.Equal(tag.Length - 10, parsed.TagSize);
		Assert.Equal(3, parsed.Frames.Count);
		Assert.Equal("Ada", parsed.Frames["TPE1"].Text);
		Assert.Equal("Hello from Talktastic", parsed.Frames["TIT2"].Text);
		Assert.Equal("Talktastic", parsed.Frames["TSSE"].Text);
	}

	[Fact]
	public void CreateTag_EmptyFields_WritesEmptyTextFrames()
	{
		var metadata = new AudioMetadata(string.Empty, string.Empty);

		var parsed = ParseId3Tag(Id3Writer.CreateTag(metadata));

		Assert.Equal(string.Empty, parsed.Frames["TPE1"].Text);
		Assert.Equal(string.Empty, parsed.Frames["TIT2"].Text);
		Assert.Equal("Talktastic", parsed.Frames["TSSE"].Text);
		Assert.All(parsed.Frames.Values, frame => Assert.Equal(3, frame.EncodingByte));
		Assert.Equal(1, parsed.Frames["TPE1"].Size);
		Assert.Equal(1, parsed.Frames["TIT2"].Size);
	}

	[Fact]
	public void CreateTag_LongSpokenText_TruncatesTitleFrame()
	{
		var spokenText = new string('x', 300);
		var metadata = new AudioMetadata("Ada", spokenText);

		var parsed = ParseId3Tag(Id3Writer.CreateTag(metadata));
		var expectedTitle = new string('x', 256) + "...";

		Assert.Equal(expectedTitle, parsed.Frames["TIT2"].Text);
	}

	[Fact]
	public void CreateTag_NullMetadata_ThrowsNullReferenceException()
	{
		Assert.Throws<NullReferenceException>
		(
			() => Id3Writer.CreateTag(null!)
		);
	}

	private static Id3TagInfo ParseId3Tag(byte[] tagBytes)
	{
		Assert.True(tagBytes.Length >= 10);

		var tagSize =
			((tagBytes[6] & 0x7F) << 21) |
			((tagBytes[7] & 0x7F) << 14) |
			((tagBytes[8] & 0x7F) << 7) |
			(tagBytes[9] & 0x7F);

		var frames = new Dictionary<string, Id3FrameInfo>(StringComparer.Ordinal);
		var position = 10;
		var end = position + tagSize;

		while ((position + 10) <= end)
		{
			var frameId = Encoding.ASCII.GetString(tagBytes, position, 4);
			if (frameId == "\0\0\0\0")
			{
				break;
			}

			var frameSize = BinaryPrimitives.ReadInt32BigEndian(tagBytes.AsSpan(position + 4, 4));
			var encodingByte = tagBytes[position + 10];
			var text = Encoding.UTF8.GetString(tagBytes, position + 11, frameSize - 1);

			frames[frameId] = new Id3FrameInfo(frameSize, encodingByte, text);
			position += 10 + frameSize;
		}

		return new Id3TagInfo(tagSize, frames);
	}

	private sealed record Id3TagInfo(int TagSize, IReadOnlyDictionary<string, Id3FrameInfo> Frames);

	private sealed record Id3FrameInfo(int Size, byte EncodingByte, string Text);
}

public sealed class LameEncoderTests
{
	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(42)]
	public void CheckLameResult_NonNegativeResult_DoesNotThrow(int result)
	{
		AudioOutputTestsInvokePrivateStatic(typeof(LameEncoder), "CheckLameResult", result, "SetQuality");
	}

	[Fact]
	public void CheckLameResult_NegativeResult_ThrowsInvalidOperationException()
	{
		var exception = Assert.Throws<InvalidOperationException>
		(
			() => AudioOutputTestsInvokePrivateStatic(typeof(LameEncoder), "CheckLameResult", -7, "SetQuality")
		);

		Assert.Contains("SetQuality", exception.Message, StringComparison.Ordinal);
		Assert.Contains("-7", exception.Message, StringComparison.Ordinal);
	}

	private static object? AudioOutputTestsInvokePrivateStatic(Type declaringType, string methodName, params object?[] parameters)
	{
		try
		{
			var method = declaringType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
			Assert.NotNull(method);
			return method.Invoke(null, parameters);
		}
		catch (TargetInvocationException exception) when (exception.InnerException is not null)
		{
			ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
			throw;
		}
	}
}

public sealed class OggOpusEncoderTests : IDisposable
{
	private readonly string _artifactRoot;

	public OggOpusEncoderTests()
	{
		_artifactRoot = Path.Combine
		(
			AppContext.BaseDirectory,
			"TestArtifacts",
			nameof(OggOpusEncoderTests),
			Guid.NewGuid().ToString("N")
		);
		Directory.CreateDirectory(_artifactRoot);
	}

	[Fact]
	public void Truncate_ShortValue_ReturnsOriginalString()
	{
		var value = "short";

		var result = InvokePrivateStatic<string>(typeof(OggOpusEncoder), "Truncate", value, 256);

		Assert.Equal(value, result);
	}

	[Fact]
	public void Truncate_LongValue_AppendsEllipsis()
	{
		var value = new string('y', 300);

		var result = InvokePrivateStatic<string>(typeof(OggOpusEncoder), "Truncate", value, 256);

		Assert.Equal(new string('y', 256) + "...", result);
	}

	[Fact]
	public void EncodeToFile_WithMetadata_WritesOggContainerAndTags()
	{
		var outputPath = Path.Combine(_artifactRoot, "with-metadata.ogg");
		var longTitle = new string('z', 300);
		Directory.CreateDirectory(_artifactRoot);

		OggOpusEncoder.EncodeToFile
		(
			BuildPcm16Bytes(0, 4096, -4096, 2048, -2048, 1024),
			16000,
			1,
			outputPath,
			new AudioMetadata("Ada", longTitle)
		);

		var fileBytes = File.ReadAllBytes(outputPath);

		Assert.True(fileBytes.Length > 0);
		Assert.True(fileBytes.AsSpan(0, 4).SequenceEqual("OggS"u8));
		Assert.True(ContainsUtf8(fileBytes, "OpusTags"));
		Assert.True(ContainsUtf8(fileBytes, "ARTIST"));
		Assert.True(ContainsUtf8(fileBytes, "Ada"));
		Assert.True(ContainsUtf8(fileBytes, "TITLE"));
		Assert.True(ContainsUtf8(fileBytes, new string('z', 256) + "..."));
		Assert.True(ContainsUtf8(fileBytes, "ENCODER"));
		Assert.True(ContainsUtf8(fileBytes, "Talktastic"));
	}

	[Fact]
	public void EncodeToFile_NullMetadata_WritesValidOggContainer()
	{
		var outputPath = Path.Combine(_artifactRoot, "without-metadata.ogg");
		Directory.CreateDirectory(_artifactRoot);

		OggOpusEncoder.EncodeToFile
		(
			BuildPcm16Bytes(0, 1024, -1024, 2048),
			16000,
			1,
			outputPath,
			metadata: null
		);

		var fileBytes = File.ReadAllBytes(outputPath);

		Assert.True(fileBytes.Length > 0);
		Assert.True(fileBytes.AsSpan(0, 4).SequenceEqual("OggS"u8));
		Assert.True(ContainsUtf8(fileBytes, "OpusHead"));
	}

	public void Dispose()
	{
		if (Directory.Exists(_artifactRoot))
		{
			Directory.Delete(_artifactRoot, recursive: true);
		}
	}

	private static T InvokePrivateStatic<T>(Type declaringType, string methodName, params object?[] parameters)
	{
		try
		{
			var method = declaringType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
			Assert.NotNull(method);
			return (T)method.Invoke(null, parameters)!;
		}
		catch (TargetInvocationException exception) when (exception.InnerException is not null)
		{
			ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
			throw;
		}
	}

	private static byte[] BuildPcm16Bytes(params short[] samples)
	{
		var bytes = new byte[samples.Length * sizeof(short)];

		for (var index = 0; index < samples.Length; index++)
		{
			BinaryPrimitives.WriteInt16LittleEndian
			(
				bytes.AsSpan(index * sizeof(short), sizeof(short)),
				samples[index]
			);
		}

		return bytes;
	}

	private static bool ContainsUtf8(byte[] bytes, string value)
	{
		return bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) >= 0;
	}
}
