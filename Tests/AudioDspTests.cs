using System.Buffers.Binary;
using System.Text;

#pragma warning disable CA1814

namespace Talktastic.Tests;

public sealed class AudioDspTests
{
	[Fact]
	public void ReadWavSampleRate_Valid44100Wave_ReturnsSampleRate()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 44100,
			channels: 1,
			bitsPerSample: 16,
			rawSamples: BuildPcm16Bytes(0, 32767)
		);

		var sampleRate = AudioDsp.ReadWavSampleRate(wavBytes);

		Assert.Equal(44100, sampleRate);
	}

	[Fact]
	public void ReadWavSampleRate_Valid16000Wave_ReturnsSampleRate()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 16000,
			channels: 2,
			bitsPerSample: 16,
			rawSamples: BuildPcm16Bytes(0, 32767, -32768, 1024)
		);

		var sampleRate = AudioDsp.ReadWavSampleRate(wavBytes);

		Assert.Equal(16000, sampleRate);
	}

	[Fact]
	public void ReadWavSampleRate_Null_ThrowsInvalidDataException()
	{
		Assert.Throws<InvalidDataException>
		(
			() => AudioDsp.ReadWavSampleRate(null!)
		);
	}

	[Fact]
	public void ReadWavSampleRate_TooShort_ThrowsInvalidDataException()
	{
		Assert.Throws<InvalidDataException>
		(
			() => AudioDsp.ReadWavSampleRate(new byte[27])
		);
	}

	[Fact]
	public void ReadWavSampleRate_NotRiff_ThrowsInvalidDataException()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 44100,
			channels: 1,
			bitsPerSample: 16,
			rawSamples: BuildPcm16Bytes(0)
		);
		wavBytes[0] = (byte)'N';

		Assert.Throws<InvalidDataException>
		(
			() => AudioDsp.ReadWavSampleRate(wavBytes)
		);
	}

	[Fact]
	public void ReadWavSampleRate_NotWave_ThrowsInvalidDataException()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 44100,
			channels: 1,
			bitsPerSample: 16,
			rawSamples: BuildPcm16Bytes(0)
		);
		Encoding.ASCII.GetBytes("NOPE").CopyTo(wavBytes, 8);

		Assert.Throws<InvalidDataException>
		(
			() => AudioDsp.ReadWavSampleRate(wavBytes)
		);
	}

	[Fact]
	public void ParseWavToFloat_Valid16BitMonoPcm_ReturnsNormalizedSamples()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 44100,
			channels: 1,
			bitsPerSample: 16,
			rawSamples: BuildPcm16Bytes(-32768, -16384, 0, 16384, 32767)
		);

		var (samples, sampleRate, channels) = AudioDsp.ParseWavToFloat(wavBytes);

		Assert.Equal(44100, sampleRate);
		Assert.Equal(1, channels);
		AssertEqualWithinTolerance
		(
			[-1.0f, -0.5f, 0.0f, 0.5f, 32767.0f / 32768.0f],
			samples
		);
	}

	[Fact]
	public void ParseWavToFloat_Valid16BitStereoPcm_ReturnsInterleavedSamples()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 48000,
			channels: 2,
			bitsPerSample: 16,
			rawSamples: BuildPcm16Bytes(-32768, 32767, 0, 16384)
		);

		var (samples, sampleRate, channels) = AudioDsp.ParseWavToFloat(wavBytes);

		Assert.Equal(48000, sampleRate);
		Assert.Equal(2, channels);
		AssertEqualWithinTolerance
		(
			[-1.0f, 32767.0f / 32768.0f, 0.0f, 0.5f],
			samples
		);
	}

	[Fact]
	public void ParseWavToFloat_Valid32BitFloatMono_ReturnsSamples()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 16000,
			channels: 1,
			bitsPerSample: 32,
			rawSamples: BuildFloat32Bytes(-1.0f, -0.25f, 0.0f, 0.75f, 1.0f),
			formatTag: 3
		);

		var (samples, sampleRate, channels) = AudioDsp.ParseWavToFloat(wavBytes);

		Assert.Equal(16000, sampleRate);
		Assert.Equal(1, channels);
		AssertEqualWithinTolerance([-1.0f, -0.25f, 0.0f, 0.75f, 1.0f], samples);
	}

	[Fact]
	public void ParseWavToFloat_ExtensibleFloatFormat_UsesSubFormatTag()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 22050,
			channels: 1,
			bitsPerSample: 32,
			rawSamples: BuildFloat32Bytes(0.1f, -0.2f, 0.3f),
			formatTag: 0xFFFE,
			extraFormatBytes: BuildExtensibleFormatBytes(3)
		);

		var (samples, sampleRate, channels) = AudioDsp.ParseWavToFloat(wavBytes);

		Assert.Equal(22050, sampleRate);
		Assert.Equal(1, channels);
		AssertEqualWithinTolerance([0.1f, -0.2f, 0.3f], samples);
	}

	[Fact]
	public void ParseWavToFloat_SkipsUnknownOddSizedChunkAndPadding()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 16000,
			channels: 1,
			bitsPerSample: 16,
			rawSamples: BuildPcm16Bytes(0, 16384),
			extraChunks:
			[
				("JUNK", new byte[] { 1, 2, 3 }),
			]
		);

		var (samples, sampleRate, channels) = AudioDsp.ParseWavToFloat(wavBytes);

		Assert.Equal(16000, sampleRate);
		Assert.Equal(1, channels);
		AssertEqualWithinTolerance([0.0f, 0.5f], samples);
	}

	[Fact]
	public void ParseWavToFloat_NullInput_ThrowsArgumentNullException()
	{
		Assert.Throws<ArgumentNullException>
		(
			() => AudioDsp.ParseWavToFloat(null!)
		);
	}

	[Fact]
	public void ParseWavToFloat_TooShort_ThrowsInvalidDataException()
	{
		Assert.Throws<InvalidDataException>
		(
			() => AudioDsp.ParseWavToFloat(new byte[43])
		);
	}

	[Fact]
	public void ParseWavToFloat_NotRiff_ThrowsInvalidDataException()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 16000,
			channels: 1,
			bitsPerSample: 16,
			rawSamples: BuildPcm16Bytes(0)
		);
		wavBytes[0] = (byte)'N';

		Assert.Throws<InvalidDataException>
		(
			() => AudioDsp.ParseWavToFloat(wavBytes)
		);
	}

	[Fact]
	public void ParseWavToFloat_MissingDataChunk_ThrowsInvalidDataException()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 16000,
			channels: 1,
			bitsPerSample: 16,
			rawSamples: BuildPcm16Bytes(0),
			includeDataChunk: false
		);

		Assert.Throws<InvalidDataException>
		(
			() => AudioDsp.ParseWavToFloat(wavBytes)
		);
	}

	[Fact]
	public void ParseWavToFloat_Unsupported24BitPcm_ThrowsNotSupportedException()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 16000,
			channels: 1,
			bitsPerSample: 24,
			rawSamples: BuildPcm24Bytes(0x7FFFFF, -0x800000)
		);

		Assert.Throws<NotSupportedException>
		(
			() => AudioDsp.ParseWavToFloat(wavBytes)
		);
	}

	[Fact]
	public void ResampleToMono16k_SameRateMono_ReturnsOriginalSamples()
	{
		var samples = new float[] { -1.0f, -0.25f, 0.25f, 1.0f };

		var result = AudioDsp.ResampleToMono16k(samples, 16000, 1);

		AssertEqualWithinTolerance(samples, result);
		Assert.NotSame(samples, result);
	}

	[Fact]
	public void ResampleToMono16k_DownsamplesAndMixesStereoToMono()
	{
		float[] samples =
		[
			1.0f, 3.0f,
			5.0f, 7.0f,
			9.0f, 11.0f,
			13.0f, 15.0f,
		];

		var result = AudioDsp.ResampleToMono16k(samples, 32000, 2);

		Assert.Equal(2, result.Length);
		AssertEqualWithinTolerance([2.0f, 10.0f], result);
	}

	[Fact]
	public void ResampleToMono16k_InvalidInterleavedLength_ThrowsArgumentException()
	{
		Assert.Throws<ArgumentException>
		(
			() => AudioDsp.ResampleToMono16k([1.0f, 2.0f, 3.0f], 16000, 2)
		);
	}

	[Fact]
	public void EncodeWav_RoundTripsThroughParser()
	{
		var samples = new float[] { -1.0f, -0.5f, 0.0f, 0.5f, 1.0f };

		var wavBytes = AudioDsp.EncodeWav(samples, 22050);
		var (parsedSamples, sampleRate, channels) = AudioDsp.ParseWavToFloat(wavBytes);

		Assert.Equal(22050, sampleRate);
		Assert.Equal(1, channels);
		Assert.Equal(22050, AudioDsp.ReadWavSampleRate(wavBytes));
		AssertEqualWithinTolerance
		(
			[-1.0f, -16384.0f / 32768.0f, 0.0f, 16384.0f / 32768.0f, 32767.0f / 32768.0f],
			parsedSamples
		);
	}

	[Fact]
	public void EncodeWav_ClampsSamplesOutsideUnitRange()
	{
		var wavBytes = AudioDsp.EncodeWav([-2.0f, 2.0f], 16000);

		var (samples, _, _) = AudioDsp.ParseWavToFloat(wavBytes);

		AssertEqualWithinTolerance([-1.0f, 32767.0f / 32768.0f], samples);
	}

	[Fact]
	public void FloatToPcm16_EmptyInput_ReturnsEmptyBuffer()
	{
		var pcm = AudioDsp.FloatToPcm16(ReadOnlySpan<float>.Empty);

		Assert.Empty(pcm);
	}

	[Fact]
	public void FloatToPcm16_ConvertsKnownSamples_PositiveAndNegative()
	{
		var pcm = AudioDsp.FloatToPcm16([0.0f, 0.5f, -0.5f, 1.0f, -1.0f]);

		Assert.Equal(10, pcm.Length);
		Assert.Equal((short)0, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(0, 2)));
		Assert.Equal((short)16384, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(2, 2)));
		Assert.Equal((short)-16384, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(4, 2)));
		Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(6, 2)));
		Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(8, 2)));
	}

	[Fact]
	public void FloatToPcm16_ClampsSamplesOutsideUnitRange()
	{
		var pcm = AudioDsp.FloatToPcm16([2.0f, -2.0f]);

		Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(0, 2)));
		Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(2, 2)));
	}

	[Fact]
	public void FloatToPcm16_MatchesEncodeWavPayload()
	{
		var samples = new float[] { -1.0f, -0.25f, 0.0f, 0.333f, 0.75f, 1.0f };

		var pcm = AudioDsp.FloatToPcm16(samples);
		var wav = AudioDsp.EncodeWav(samples, 16000);

		Assert.Equal(wav.AsSpan(44).ToArray(), pcm);
	}

	[Fact]
	public void Rms_EmptyInput_ReturnsZero()
	{
		Assert.Equal(0.0, AudioDsp.Rms(ReadOnlySpan<float>.Empty));
	}

	[Fact]
	public void Rms_ConstantSignal_ReturnsMagnitude()
	{
		Assert.Equal(0.5, AudioDsp.Rms([0.5f, -0.5f, 0.5f, -0.5f]), 5);
	}

	[Fact]
	public void ZeroCrossingRate_FullScaleAlternating_ApproachesNyquist()
	{
		var samples = new float[1000];
		for (var i = 0; i < samples.Length; i++)
		{
			samples[i] = (i % 2 == 0) ? 1.0f : -1.0f;
		}

		// Alternating every sample crosses once per sample = ~sampleRate crossings/second.
		Assert.Equal(48000.0, AudioDsp.ZeroCrossingRate(samples, 48000), 0);
	}

	[Fact]
	public void ZeroCrossingRate_SineMatchesTwiceFrequency()
	{
		var samples = MakeSine(frequency: 300, sampleRate: 48000, seconds: 1.0, amplitude: 0.5f);

		// A sine of f Hz crosses zero 2*f times per second.
		Assert.InRange(AudioDsp.ZeroCrossingRate(samples, 48000), 595.0, 605.0);
	}

	[Fact]
	public void IsLikelyCorruptOutput_LowFrequencyDrone_IsFlagged()
	{
		var drone = MakeSine(frequency: 300, sampleRate: 48000, seconds: 1.0, amplitude: 0.5f);

		Assert.True(AudioDsp.IsLikelyCorruptOutput(drone, 48000));
	}

	[Fact]
	public void IsLikelyCorruptOutput_SpeechLikeHighFrequency_IsNotFlagged()
	{
		var speech = MakeSine(frequency: 2500, sampleRate: 48000, seconds: 1.0, amplitude: 0.5f);

		Assert.False(AudioDsp.IsLikelyCorruptOutput(speech, 48000));
	}

	[Fact]
	public void IsLikelyCorruptOutput_Silence_IsNotFlagged()
	{
		var silence = new float[48000];

		Assert.False(AudioDsp.IsLikelyCorruptOutput(silence, 48000));
	}

	[Fact]
	public void IsLikelyCorruptOutput_TooShortClip_IsNotFlagged()
	{
		var shortDrone = MakeSine(frequency: 300, sampleRate: 48000, seconds: 0.2, amplitude: 0.5f);

		Assert.False(AudioDsp.IsLikelyCorruptOutput(shortDrone, 48000));
	}

	private static float[] MakeSine(double frequency, int sampleRate, double seconds, float amplitude)
	{
		var count = (int)(sampleRate * seconds);
		var samples = new float[count];
		for (var i = 0; i < count; i++)
		{
			samples[i] = amplitude * (float)Math.Sin(2.0 * Math.PI * frequency * i / sampleRate);
		}

		return samples;
	}

	[Fact]
	public void ButterworthHighPass_ZeroSignal_ReturnsZeroSignal()
	{
		var samples = new float[64];

		var filtered = AudioDsp.ButterworthHighPass(samples);

		Assert.All(filtered, sample => Assert.Equal(0.0f, sample));
	}

	[Fact]
	public void ButterworthHighPass_InputTooShort_ThrowsArgumentException()
	{
		Assert.Throws<ArgumentException>
		(
			() => AudioDsp.ButterworthHighPass(new float[18])
		);
	}

	[Fact]
	public void ReflectPad_PadZero_ReturnsCopy()
	{
		var samples = new float[] { 1.0f, 2.0f, 3.0f };

		var padded = AudioDsp.ReflectPad(samples, 0);

		Assert.Equal(samples, padded);
		Assert.NotSame(samples, padded);
	}

	[Fact]
	public void ReflectPad_MultiSampleSignal_ReflectsExcludingEdge()
	{
		var padded = AudioDsp.ReflectPad([1.0f, 2.0f, 3.0f], 2);

		AssertEqualWithinTolerance([3.0f, 2.0f, 1.0f, 2.0f, 3.0f, 2.0f, 1.0f], padded);
	}

	[Fact]
	public void ComputeStft_Silence_ReturnsZeroSpectrogram()
	{
		var spectrogram = AudioDsp.ComputeStft
		(
			audio: [0.0f, 0.0f, 0.0f, 0.0f],
			nFft: 4,
			hopLength: 2,
			winLength: 4,
			center: false
		);

		Assert.Equal(3, spectrogram.GetLength(0));
		Assert.Equal(1, spectrogram.GetLength(1));
		Assert.All(Enumerate(spectrogram), value => Assert.Equal(0.0f, value));
	}

	[Fact]
	public void ComputeMelFilterbank_ReturnsExpectedShapeAndNonNegativeWeights()
	{
		var filterbank = AudioDsp.ComputeMelFilterbank
		(
			sampleRate: 16000,
			nFft: 512,
			nMels: 4,
			fMin: 30.0f,
			fMax: 4000.0f
		);

		Assert.Equal(4, filterbank.GetLength(0));
		Assert.Equal(257, filterbank.GetLength(1));

		for (var mel = 0; mel < filterbank.GetLength(0); mel++)
		{
			var hasPositiveWeight = false;
			for (var bin = 0; bin < filterbank.GetLength(1); bin++)
			{
				Assert.True(filterbank[mel, bin] >= 0.0f);
				hasPositiveWeight |= filterbank[mel, bin] > 0.0f;
			}

			Assert.True(hasPositiveWeight, $"Mel row {mel} should contain a positive weight.");
		}
	}

	[Fact]
	public void ComputeMelSpectrogram_Silence_ReturnsClampedLogMel()
	{
		var mel = AudioDsp.ComputeMelSpectrogram(new float[160], center: false);
		var expected = MathF.Log(1e-5f);

		Assert.Equal(128, mel.GetLength(0));
		Assert.Equal(1, mel.GetLength(1));
		Assert.All
		(
			Enumerate(mel),
			value => Assert.True(MathF.Abs(value - expected) <= 1e-6f)
		);
	}

	[Fact]
	public void ComputeRms_UsesCenteredWindows()
	{
		var rms = AudioDsp.ComputeRms
		(
			audio: [1.0f, 1.0f, 1.0f, 1.0f],
			frameLength: 2,
			hopLength: 2
		);

		AssertEqualWithinTolerance
		(
			[
				MathF.Sqrt(0.5f),
				1.0f,
				MathF.Sqrt(0.5f),
			],
			rms
		);
	}

	[Fact]
	public void InterpolateLinear_SingleValue_RepeatsAcrossTargetLength()
	{
		var interpolated = AudioDsp.InterpolateLinear([3.5f], 4);

		AssertEqualWithinTolerance([3.5f, 3.5f, 3.5f, 3.5f], interpolated);
	}

	[Fact]
	public void InterpolateLinear_MultipleValues_UsesExpectedPositions()
	{
		var interpolated = AudioDsp.InterpolateLinear([0.0f, 10.0f], 4);

		AssertEqualWithinTolerance([0.0f, 2.5f, 5.0f, 7.5f], interpolated);
	}

	private static IEnumerable<float> Enumerate(float[,] matrix)
	{
		for (var row = 0; row < matrix.GetLength(0); row++)
		{
			for (var column = 0; column < matrix.GetLength(1); column++)
			{
				yield return matrix[row, column];
			}
		}
	}

	private static void AssertEqualWithinTolerance
	(
		float[] expected,
		float[] actual,
		float tolerance = 1e-6f
	)
	{
		Assert.Equal(expected.Length, actual.Length);

		for (var index = 0; index < expected.Length; index++)
		{
			Assert.True
			(
				Math.Abs(expected[index] - actual[index]) <= tolerance,
				$"Index {index}: expected {expected[index]}, actual {actual[index]}."
			);
		}
	}

	private static byte[] BuildWavBytes
	(
		int sampleRate,
		short channels,
		short bitsPerSample,
		byte[] rawSamples,
		ushort formatTag = 1,
		byte[]? extraFormatBytes = null,
		bool includeDataChunk = true,
		params (string Id, byte[] Data)[] extraChunks
	)
	{
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

		writer.Write(Encoding.ASCII.GetBytes("RIFF"));
		writer.Write(0);
		writer.Write(Encoding.ASCII.GetBytes("WAVE"));

		var formatExtraBytes = extraFormatBytes ?? [];
		var formatChunkSize = 16 + formatExtraBytes.Length;
		var blockAlign = channels * (bitsPerSample / 8);
		var byteRate = sampleRate * blockAlign;

		WriteChunk
		(
			writer,
			"fmt ",
			payloadWriter =>
			{
				payloadWriter.Write(formatTag);
				payloadWriter.Write(channels);
				payloadWriter.Write(sampleRate);
				payloadWriter.Write(byteRate);
				payloadWriter.Write((short)blockAlign);
				payloadWriter.Write(bitsPerSample);
				payloadWriter.Write(formatExtraBytes);
			},
			formatChunkSize
		);

		foreach (var (id, data) in extraChunks)
		{
			WriteChunk(writer, id, payloadWriter => payloadWriter.Write(data), data.Length);
		}

		if (includeDataChunk)
		{
			WriteChunk(writer, "data", payloadWriter => payloadWriter.Write(rawSamples), rawSamples.Length);
		}

		stream.Position = 4;
		writer.Write(checked((int)(stream.Length - 8)));
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

	private static byte[] BuildFloat32Bytes(params float[] samples)
	{
		var bytes = new byte[samples.Length * sizeof(float)];
		for (var index = 0; index < samples.Length; index++)
		{
			BinaryPrimitives.WriteSingleLittleEndian
			(
				bytes.AsSpan(index * sizeof(float), sizeof(float)),
				samples[index]
			);
		}

		return bytes;
	}

	private static byte[] BuildPcm24Bytes(params int[] samples)
	{
		var bytes = new byte[samples.Length * 3];
		for (var index = 0; index < samples.Length; index++)
		{
			var value = samples[index];
			bytes[(index * 3) + 0] = (byte)(value & 0xFF);
			bytes[(index * 3) + 1] = (byte)((value >> 8) & 0xFF);
			bytes[(index * 3) + 2] = (byte)((value >> 16) & 0xFF);
		}

		return bytes;
	}

	private static byte[] BuildExtensibleFormatBytes(int subFormatTag)
	{
		var bytes = new byte[24];
		BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0, 2), 22);
		BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), 32);
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), (uint)subFormatTag);
		return bytes;
	}

	private static void WriteChunk
	(
		BinaryWriter writer,
		string chunkId,
		Action<BinaryWriter> writePayload,
		int payloadLength
	)
	{
		writer.Write(Encoding.ASCII.GetBytes(chunkId));
		writer.Write(payloadLength);
		writePayload(writer);

		if ((payloadLength & 1) != 0)
		{
			writer.Write((byte)0);
		}
	}

	// ── ReadAudioFileToWav tests ──

	[Fact]
	public void ReadAudioFileToWav_ValidWavFile_ReturnsSameBytes()
	{
		var wavBytes = BuildWavBytes(16000, 1, 16, BuildPcm16Bytes(100, 200, 300));
		var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.wav");
		try
		{
			File.WriteAllBytes(path, wavBytes);
			var result = AudioDsp.ReadAudioFileToWav(path);
			Assert.Equal(wavBytes, result);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void ReadAudioFileToWav_MissingFile_ThrowsFileNotFoundException()
	{
		Assert.Throws<FileNotFoundException>
		(
			() => AudioDsp.ReadAudioFileToWav(@"C:\nonexistent\fake.wav")
		);
	}

	[Fact]
	public void ReadAudioFileToWav_UnsupportedExtension_ThrowsInvalidOperationException()
	{
		var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.flac");
		try
		{
			File.WriteAllBytes(path, [0x00]);
			var ex = Assert.Throws<InvalidOperationException>
			(
				() => AudioDsp.ReadAudioFileToWav(path)
			);
			Assert.Contains(".flac", ex.Message, StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void ReadAudioFileToWav_NullOrWhitespace_ThrowsArgumentException(string? path)
	{
		Assert.ThrowsAny<ArgumentException>
		(
			() => AudioDsp.ReadAudioFileToWav(path!)
		);
	}

	[Fact]
	public void ReadAudioFileToWav_OggExtension_WithInvalidContent_ReturnsEmptyWav()
	{
		// Invalid OGG content has no packets -- decoder returns empty WAV
		var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.ogg");
		try
		{
			File.WriteAllBytes(path, [0x00, 0x01, 0x02]);
			var result = AudioDsp.ReadAudioFileToWav(path);

			// Should produce a valid (empty) WAV header at minimum
			Assert.NotNull(result);
			Assert.True(result.Length >= 44); // WAV header is 44 bytes
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void ReadAudioFileToWav_Mp3Extension_AcceptsFile()
	{
		var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.mp3");
		try
		{
			File.WriteAllBytes(path, [0x00, 0x01, 0x02]);
			Assert.ThrowsAny<Exception>(() => AudioDsp.ReadAudioFileToWav(path));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void ParseWavToFloat_NotWave_ThrowsInvalidDataException()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 16000,
			channels: 1,
			bitsPerSample: 16,
			rawSamples: BuildPcm16Bytes(0)
		);
		Encoding.ASCII.GetBytes("NOPE").CopyTo(wavBytes, 8);

		Assert.Throws<InvalidDataException>
		(
			() => AudioDsp.ParseWavToFloat(wavBytes)
		);
	}

	[Fact]
	public void ParseWavToFloat_WithFactChunk_ReturnsSamples()
	{
		var factBytes = new byte[4];
		BinaryPrimitives.WriteInt32LittleEndian(factBytes, 2);
		var wavBytes = BuildWavBytes
		(
			sampleRate: 24000,
			channels: 1,
			bitsPerSample: 16,
			rawSamples: BuildPcm16Bytes(-8192, 8192),
			extraChunks:
			[
				("fact", factBytes),
			]
		);

		var (samples, sampleRate, channels) = AudioDsp.ParseWavToFloat(wavBytes);

		Assert.Equal(24000, sampleRate);
		Assert.Equal(1, channels);
		AssertEqualWithinTolerance([-0.25f, 0.25f], samples);
	}

	[Fact]
	public void ParseWavToFloat_ExtensiblePcm16Format_UsesSubFormatTag()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 48000,
			channels: 2,
			bitsPerSample: 16,
			rawSamples: BuildPcm16Bytes(-32768, 0, 16384, 32767),
			formatTag: 0xFFFE,
			extraFormatBytes: BuildExtensibleFormatBytes(1)
		);

		var (samples, sampleRate, channels) = AudioDsp.ParseWavToFloat(wavBytes);

		Assert.Equal(48000, sampleRate);
		Assert.Equal(2, channels);
		AssertEqualWithinTolerance
		(
			[-1.0f, 0.0f, 0.5f, 32767.0f / 32768.0f],
			samples
		);
	}

	[Fact]
	public void ParseWavToFloat_Float32ClampsOutOfRangeSamples()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 16000,
			channels: 1,
			bitsPerSample: 32,
			rawSamples: BuildFloat32Bytes(-2.0f, -0.5f, 0.5f, 2.0f),
			formatTag: 3
		);

		var (samples, _, _) = AudioDsp.ParseWavToFloat(wavBytes);

		AssertEqualWithinTolerance([-1.0f, -0.5f, 0.5f, 1.0f], samples);
	}

	[Fact]
	public void ParseWavToFloat_Unsupported8BitPcm_ThrowsNotSupportedException()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 16000,
			channels: 1,
			bitsPerSample: 8,
			rawSamples: [0x00, 0x80, 0xFF]
		);

		Assert.Throws<NotSupportedException>
		(
			() => AudioDsp.ParseWavToFloat(wavBytes)
		);
	}

	[Fact]
	public void ParseWavToFloat_Unsupported32BitPcm_ThrowsNotSupportedException()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 16000,
			channels: 1,
			bitsPerSample: 32,
			rawSamples: BuildFloat32Bytes(0.25f),
			formatTag: 1
		);

		Assert.Throws<NotSupportedException>
		(
			() => AudioDsp.ParseWavToFloat(wavBytes)
		);
	}

	[Fact]
	public void ParseWavToFloat_NegativeChunkSize_ThrowsInvalidDataException()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 16000,
			channels: 1,
			bitsPerSample: 16,
			rawSamples: BuildPcm16Bytes(0)
		);
		BinaryPrimitives.WriteInt32LittleEndian(wavBytes.AsSpan(16, 4), -1);

		Assert.Throws<InvalidDataException>
		(
			() => AudioDsp.ParseWavToFloat(wavBytes)
		);
	}

	[Fact]
	public void ParseWavToFloat_ChunkExtendsBeyondEnd_ThrowsInvalidDataException()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 16000,
			channels: 1,
			bitsPerSample: 16,
			rawSamples: BuildPcm16Bytes(0)
		);
		BinaryPrimitives.WriteInt32LittleEndian(wavBytes.AsSpan(16, 4), 1000);

		Assert.Throws<InvalidDataException>
		(
			() => AudioDsp.ParseWavToFloat(wavBytes)
		);
	}

	[Fact]
	public void ParseWavToFloat_FmtChunkTooSmall_ThrowsInvalidDataException()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 16000,
			channels: 1,
			bitsPerSample: 16,
			rawSamples: BuildPcm16Bytes(0)
		);
		BinaryPrimitives.WriteInt32LittleEndian(wavBytes.AsSpan(16, 4), 15);

		Assert.Throws<InvalidDataException>
		(
			() => AudioDsp.ParseWavToFloat(wavBytes)
		);
	}

	[Fact]
	public void ParseWavToFloat_ExtensibleFmtChunkTooSmall_ThrowsInvalidDataException()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 16000,
			channels: 1,
			bitsPerSample: 16,
			rawSamples: BuildPcm16Bytes(0),
			formatTag: 0xFFFE
		);

		Assert.Throws<InvalidDataException>
		(
			() => AudioDsp.ParseWavToFloat(wavBytes)
		);
	}

	[Fact]
	public void ParseWavToFloat_InvalidChannelCount_ThrowsInvalidDataException()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 16000,
			channels: 1,
			bitsPerSample: 16,
			rawSamples: BuildPcm16Bytes(0)
		);
		BinaryPrimitives.WriteUInt16LittleEndian(wavBytes.AsSpan(22, 2), 0);

		Assert.Throws<InvalidDataException>
		(
			() => AudioDsp.ParseWavToFloat(wavBytes)
		);
	}

	[Fact]
	public void ParseWavToFloat_InvalidSampleRate_ThrowsInvalidDataException()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 16000,
			channels: 1,
			bitsPerSample: 16,
			rawSamples: BuildPcm16Bytes(0)
		);
		BinaryPrimitives.WriteInt32LittleEndian(wavBytes.AsSpan(24, 4), 0);

		Assert.Throws<InvalidDataException>
		(
			() => AudioDsp.ParseWavToFloat(wavBytes)
		);
	}

	[Fact]
	public void ParseWavToFloat_InvalidBlockAlign_ThrowsInvalidDataException()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 16000,
			channels: 1,
			bitsPerSample: 16,
			rawSamples: BuildPcm16Bytes(0)
		);
		BinaryPrimitives.WriteUInt16LittleEndian(wavBytes.AsSpan(32, 2), 0);

		Assert.Throws<InvalidDataException>
		(
			() => AudioDsp.ParseWavToFloat(wavBytes)
		);
	}

	[Fact]
	public void ParseWavToFloat_MisalignedPcm16Payload_ThrowsInvalidDataException()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 16000,
			channels: 1,
			bitsPerSample: 16,
			rawSamples: [0x01]
		);

		Assert.Throws<InvalidDataException>
		(
			() => AudioDsp.ParseWavToFloat(wavBytes)
		);
	}

	[Fact]
	public void ParseWavToFloat_MisalignedFloat32Payload_ThrowsInvalidDataException()
	{
		var wavBytes = BuildWavBytes
		(
			sampleRate: 16000,
			channels: 1,
			bitsPerSample: 32,
			rawSamples: [0x01, 0x02],
			formatTag: 3
		);

		Assert.Throws<InvalidDataException>
		(
			() => AudioDsp.ParseWavToFloat(wavBytes)
		);
	}

	[Fact]
	public void ResampleToMono16k_NullInput_ThrowsArgumentNullException()
	{
		Assert.Throws<ArgumentNullException>
		(
			() => AudioDsp.ResampleToMono16k(null!, 16000, 1)
		);
	}

	[Fact]
	public void ResampleToMono16k_InvalidSampleRate_ThrowsArgumentOutOfRangeException()
	{
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => AudioDsp.ResampleToMono16k([1.0f], 0, 1)
		);
	}

	[Fact]
	public void ResampleToMono16k_InvalidChannelCount_ThrowsArgumentOutOfRangeException()
	{
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => AudioDsp.ResampleToMono16k([1.0f], 16000, 0)
		);
	}

	[Fact]
	public void ResampleToMono16k_EmptyInput_ReturnsEmptyArray()
	{
		var result = AudioDsp.ResampleToMono16k([], 16000, 1);

		Assert.Empty(result);
	}

	[Fact]
	public void ResampleToMono16k_UpsamplesMonoWithLinearInterpolation()
	{
		var result = AudioDsp.ResampleToMono16k([0.0f, 8.0f], 8000, 1);

		AssertEqualWithinTolerance([0.0f, 4.0f, 8.0f, 8.0f], result);
	}

	[Fact]
	public void ResampleToMono16k_SameRateThreeChannel_MixesToMono()
	{
		float[] samples =
		[
			1.0f, 2.0f, 3.0f,
			4.0f, 5.0f, 6.0f,
		];

		var result = AudioDsp.ResampleToMono16k(samples, 16000, 3);

		AssertEqualWithinTolerance([2.0f, 5.0f], result);
	}

	[Fact]
	public void ResampleToMono16k_SingleFrameDifferentRate_ReturnsFirstMonoFrame()
	{
		var result = AudioDsp.ResampleToMono16k([2.0f, 6.0f], 48000, 2);

		AssertEqualWithinTolerance([4.0f], result);
	}

	[Fact]
	public void ButterworthHighPass_NullInput_ThrowsArgumentNullException()
	{
		Assert.Throws<ArgumentNullException>
		(
			() => AudioDsp.ButterworthHighPass(null!)
		);
	}

	[Fact]
	public void ButterworthHighPass_EmptyInput_ReturnsEmptyArray()
	{
		var filtered = AudioDsp.ButterworthHighPass([]);

		Assert.Empty(filtered);
	}

	[Fact]
	public void ButterworthHighPass_MinimalValidLength_ReturnsSameLength()
	{
		var samples = MakeSine(frequency: 250, sampleRate: 16000, seconds: 19.0 / 16000.0, amplitude: 0.25f);

		var filtered = AudioDsp.ButterworthHighPass(samples);

		Assert.Equal(samples.Length, filtered.Length);
		AssertAllFinite(filtered);
	}

	[Fact]
	public void ButterworthHighPass_LargeAmplitudeSignal_ReturnsFiniteSamples()
	{
		var samples = new float[64];
		for (var index = 0; index < samples.Length; index++)
		{
			samples[index] = index % 2 == 0 ? 1000.0f : -1000.0f;
		}

		var filtered = AudioDsp.ButterworthHighPass(samples);

		Assert.Equal(samples.Length, filtered.Length);
		AssertAllFinite(filtered);
	}

	[Fact]
	public void ReflectPad_NegativePad_ThrowsArgumentOutOfRangeException()
	{
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => AudioDsp.ReflectPad([1.0f], -1)
		);
	}

	[Fact]
	public void ReflectPad_EmptySignalWithPositivePad_ThrowsArgumentException()
	{
		Assert.Throws<ArgumentException>
		(
			() => AudioDsp.ReflectPad([], 1)
		);
	}

	[Fact]
	public void ReflectPad_SingleSampleSignal_RepeatsValue()
	{
		var padded = AudioDsp.ReflectPad([4.0f], 3);

		AssertEqualWithinTolerance([4.0f, 4.0f, 4.0f, 4.0f, 4.0f, 4.0f, 4.0f], padded);
	}

	[Fact]
	public void ReflectPad_PadLargerThanSignal_WrapsReflection()
	{
		var padded = AudioDsp.ReflectPad([1.0f, 2.0f, 3.0f], 5);

		AssertEqualWithinTolerance
		(
			[2.0f, 1.0f, 2.0f, 3.0f, 2.0f, 1.0f, 2.0f, 3.0f, 2.0f, 1.0f, 2.0f, 3.0f, 2.0f],
			padded
		);
	}

	[Fact]
	public void ComputeStft_NullInput_ThrowsArgumentNullException()
	{
		Assert.Throws<ArgumentNullException>
		(
			() => AudioDsp.ComputeStft(null!, 4, 2, 4, center: false)
		);
	}

	[Fact]
	public void ComputeStft_InvalidArguments_ThrowArgumentOutOfRangeException()
	{
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => AudioDsp.ComputeStft([1.0f], 0, 1, 1, center: false)
		);
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => AudioDsp.ComputeStft([1.0f], 4, 0, 1, center: false)
		);
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => AudioDsp.ComputeStft([1.0f], 4, 1, 0, center: false)
		);
	}

	[Fact]
	public void ComputeStft_WindowLongerThanFft_ThrowsArgumentOutOfRangeException()
	{
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => AudioDsp.ComputeStft([1.0f], 4, 1, 5, center: false)
		);
	}

	[Fact]
	public void ComputeStft_CenterTrue_AddsPaddingAndExtraFrames()
	{
		var spectrogram = AudioDsp.ComputeStft
		(
			audio: [1.0f, 2.0f, 3.0f, 4.0f],
			nFft: 4,
			hopLength: 2,
			winLength: 4,
			center: true
		);

		Assert.Equal(3, spectrogram.GetLength(0));
		Assert.Equal(3, spectrogram.GetLength(1));
		AssertAllFinite(Enumerate(spectrogram));
	}

	[Fact]
	public void ComputeStft_NonPowerOfTwoFft_ReturnsExpectedShape()
	{
		var spectrogram = AudioDsp.ComputeStft
		(
			audio: [1.0f, 0.5f, -0.5f, -1.0f, 1.0f, 0.5f, -0.5f, -1.0f, 1.0f, 0.5f, -0.5f, -1.0f],
			nFft: 6,
			hopLength: 3,
			winLength: 4,
			center: false
		);

		Assert.Equal(4, spectrogram.GetLength(0));
		Assert.Equal(3, spectrogram.GetLength(1));
		Assert.Contains(Enumerate(spectrogram), value => value > 0.0f);
	}

	[Fact]
	public void ComputeMelFilterbank_FMaxAtOrBelowFMin_ThrowsArgumentOutOfRangeException()
	{
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => AudioDsp.ComputeMelFilterbank(16000, 512, 4, 100.0f, 100.0f)
		);
	}

	[Fact]
	public void ComputeMelFilterbank_InvalidArguments_ThrowArgumentOutOfRangeException()
	{
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => AudioDsp.ComputeMelFilterbank(0, 512, 4, 30.0f, 4000.0f)
		);
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => AudioDsp.ComputeMelFilterbank(16000, 0, 4, 30.0f, 4000.0f)
		);
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => AudioDsp.ComputeMelFilterbank(16000, 512, 0, 30.0f, 4000.0f)
		);
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => AudioDsp.ComputeMelFilterbank(16000, 512, 4, -1.0f, 4000.0f)
		);
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => AudioDsp.ComputeMelFilterbank(16000, 512, 4, 30.0f, 0.0f)
		);
	}

	[Fact]
	public void ComputeMelSpectrogram_NullInput_ThrowsArgumentNullException()
	{
		Assert.Throws<ArgumentNullException>
		(
			() => AudioDsp.ComputeMelSpectrogram(null!, center: false)
		);
	}

	[Fact]
	public void ComputeMelSpectrogram_CenterTrue_ProducesMoreFramesThanUncentered()
	{
		var audio = new float[1024];
		var centered = AudioDsp.ComputeMelSpectrogram(audio, center: true);
		var uncentered = AudioDsp.ComputeMelSpectrogram(audio, center: false);

		Assert.Equal(128, centered.GetLength(0));
		Assert.Equal(128, uncentered.GetLength(0));
		Assert.True(centered.GetLength(1) > uncentered.GetLength(1));
	}

	[Fact]
	public void ComputeMelSpectrogram_NonSilentAudio_ProducesFiniteValuesAboveClamp()
	{
		var audio = MakeSine(frequency: 440, sampleRate: 16000, seconds: 0.1, amplitude: 0.5f);
		var mel = AudioDsp.ComputeMelSpectrogram(audio, center: false);
		var clampedLog = MathF.Log(1e-5f);

		AssertAllFinite(Enumerate(mel));
		Assert.Contains(Enumerate(mel), value => value > clampedLog);
	}

	[Fact]
	public void ComputeRms_NullInput_ThrowsArgumentNullException()
	{
		Assert.Throws<ArgumentNullException>
		(
			() => AudioDsp.ComputeRms(null!, 4, 2)
		);
	}

	[Fact]
	public void ComputeRms_InvalidArguments_ThrowArgumentOutOfRangeException()
	{
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => AudioDsp.ComputeRms([1.0f], 0, 1)
		);
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => AudioDsp.ComputeRms([1.0f], 4, 0)
		);
	}

	[Fact]
	public void ComputeRms_EmptyAudio_ReturnsSingleZeroFrame()
	{
		var rms = AudioDsp.ComputeRms([], 4, 2);

		AssertEqualWithinTolerance([0.0f], rms);
	}

	[Fact]
	public void ComputeRms_EdgeWindowsUseCenteredPadding()
	{
		var rms = AudioDsp.ComputeRms
		(
			audio: [1.0f, 2.0f, 3.0f, 4.0f, 5.0f],
			frameLength: 4,
			hopLength: 3
		);

		AssertEqualWithinTolerance
		(
			[MathF.Sqrt(5.0f / 4.0f), MathF.Sqrt(54.0f / 4.0f)],
			rms
		);
	}

	[Fact]
	public void InterpolateLinear_NullInput_ThrowsArgumentNullException()
	{
		Assert.Throws<ArgumentNullException>
		(
			() => AudioDsp.InterpolateLinear(null!, 1)
		);
	}

	[Fact]
	public void InterpolateLinear_NegativeTargetLength_ThrowsArgumentOutOfRangeException()
	{
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => AudioDsp.InterpolateLinear([1.0f], -1)
		);
	}

	[Fact]
	public void InterpolateLinear_EmptySourceOrZeroTarget_ReturnsEmptyArray()
	{
		Assert.Empty(AudioDsp.InterpolateLinear([], 4));
		Assert.Empty(AudioDsp.InterpolateLinear([1.0f, 2.0f], 0));
	}

	[Fact]
	public void InterpolateLinear_ShorterTarget_InterpolatesAcrossSourceRange()
	{
		var interpolated = AudioDsp.InterpolateLinear([0.0f, 10.0f, 20.0f, 30.0f], 2);

		AssertEqualWithinTolerance([0.0f, 15.0f], interpolated);
	}

	[Fact]
	public void InterpolateLinear_LongerTarget_UsesFractionalSourcePositions()
	{
		var interpolated = AudioDsp.InterpolateLinear([0.0f, 10.0f], 5);

		AssertEqualWithinTolerance([0.0f, 2.0f, 4.0f, 6.0f, 8.0f], interpolated);
	}

	[Fact]
	public void EncodeWav_NullSamples_ThrowsArgumentNullException()
	{
		Assert.Throws<ArgumentNullException>
		(
			() => AudioDsp.EncodeWav(null!, 16000)
		);
	}

	[Fact]
	public void EncodeWav_InvalidSampleRate_ThrowsArgumentOutOfRangeException()
	{
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => AudioDsp.EncodeWav([], 0)
		);
	}

	[Fact]
	public void EncodeWav_EmptySamples_ProducesHeaderOnlyWave()
	{
		var wavBytes = AudioDsp.EncodeWav([], 16000);
		var (samples, sampleRate, channels) = AudioDsp.ParseWavToFloat(wavBytes);

		Assert.Equal(44, wavBytes.Length);
		Assert.Empty(samples);
		Assert.Equal(16000, sampleRate);
		Assert.Equal(1, channels);
	}

	[Fact]
	public void EncodeWav_MultipleSampleRates_RoundTripSampleRate()
	{
		int[] sampleRates = [8000, 16000, 48000];

		foreach (var sampleRate in sampleRates)
		{
			var wavBytes = AudioDsp.EncodeWav([0.25f, -0.25f], sampleRate);
			var (_, parsedSampleRate, channels) = AudioDsp.ParseWavToFloat(wavBytes);

			Assert.Equal(sampleRate, parsedSampleRate);
			Assert.Equal(sampleRate, AudioDsp.ReadWavSampleRate(wavBytes));
			Assert.Equal(1, channels);
		}
	}

	private static void AssertAllFinite(IEnumerable<float> values)
	{
		foreach (var value in values)
		{
			Assert.True(float.IsFinite(value), $"Expected finite value, actual {value}.");
		}
	}
}
