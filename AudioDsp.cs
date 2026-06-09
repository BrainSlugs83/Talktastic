using System.Buffers.Binary;
using System.Numerics;

#pragma warning disable CA1814

namespace Talktastic;

/// <summary>
/// Provides audio DSP operations.
/// </summary>
internal static class AudioDsp
{
	private const int TargetSampleRate = 16000;
	private const int RmvpeFftSize = 1024;
	private const int RmvpeHopLength = 160;
	private const int RmvpeWindowLength = 1024;
	private const int RmvpeMelCount = 128;
	private const float RmvpeMelMin = 30.0f;
	private const float RmvpeMelMax = 8000.0f;
	private const float LogClamp = 1e-5f;
	private const int WaveFormatPcm = 0x0001;
	private const int WaveFormatIeeeFloat = 0x0003;
	private const int WaveFormatExtensible = 0xFFFE;
	private const int RiffHeaderSize = 44;
	private const int HighPassPadLength = 18;

	private static readonly double[] HighPassB =
	[
		0.9699606451838447,
		-4.849803225919223,
		9.699606451838447,
		-9.699606451838447,
		4.849803225919223,
		-0.9699606451838447,
	];

	private static readonly double[] HighPassA =
	[
		1.0,
		-4.939001819168364,
		9.757863526739543,
		-9.639544849413458,
		4.761506797356209,
		-0.9408236532054606,
	];

	/// <summary>
	/// Reads the sample rate from a RIFF/WAV header without fully parsing the file.
	/// </summary>
	internal static int ReadWavSampleRate(byte[] wavBytes)
	{
		// Standard WAV layout: RIFF(4) size(4) WAVE(4) fmt_(4) fmtSize(4) tag(2) ch(2) rate(4)
		const int minSize = 28;
		if (wavBytes is null || wavBytes.Length < minSize)
		{
			throw new InvalidDataException("WAV data is too short to read sample rate.");
		}

		ReadOnlySpan<byte> data = wavBytes;
		if (!data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WAVE"u8))
		{
			throw new InvalidDataException("Input is not a RIFF/WAVE file.");
		}

		return BinaryPrimitives.ReadInt32LittleEndian(data.Slice(24, 4));
	}

	/// <summary>
	/// Parses a WAV file from raw bytes and returns normalized float samples.
	/// </summary>
	/// <param name="wavBytes">The WAV payload.</param>
	/// <returns>The interleaved samples, sample rate, and channel count.</returns>
	internal static
	(
		float[] Samples,
		int SampleRate,
		int Channels
	) ParseWavToFloat
	(
		byte[] wavBytes
	)
	{
		ArgumentNullException.ThrowIfNull(wavBytes);

		ReadOnlySpan<byte> data = wavBytes;
		if (data.Length < RiffHeaderSize)
		{
			throw new InvalidDataException("WAV data is too short to contain a valid RIFF header.");
		}

		if (!data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WAVE"u8))
		{
			throw new InvalidDataException("Input is not a RIFF/WAVE file.");
		}

		var offset = 12;
		var formatTag = 0;
		var channels = 0;
		var sampleRate = 0;
		var bitsPerSample = 0;
		var blockAlign = 0;
		var dataOffset = -1;
		var dataLength = 0;

		while (offset + 8 <= data.Length)
		{
			var chunkId = data.Slice(offset, 4);
			var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 4, 4));
			if (chunkSize < 0)
			{
				throw new InvalidDataException("WAV chunk size was negative.");
			}

			offset += 8;
			if (offset + chunkSize > data.Length)
			{
				throw new InvalidDataException("WAV chunk extends beyond the end of the file.");
			}

			if (chunkId.SequenceEqual("fmt "u8))
			{
				if (chunkSize < 16)
				{
					throw new InvalidDataException("WAV fmt chunk is too small.");
				}

				formatTag = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
				channels = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 2, 2));
				sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 4, 4));
				blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 12, 2));
				bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 14, 2));

				if (formatTag == WaveFormatExtensible)
				{
					if (chunkSize < 40)
					{
						throw new InvalidDataException("WAV extensible fmt chunk is too small.");
					}

					formatTag = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 24, 2));
				}
			}
			else if (chunkId.SequenceEqual("data"u8))
			{
				dataOffset = offset;
				dataLength = chunkSize;
			}

			offset += chunkSize;
			if ((chunkSize & 1) != 0 && offset < data.Length)
			{
				offset++;
			}
		}

		if (channels <= 0)
		{
			throw new InvalidDataException("WAV file did not contain a valid channel count.");
		}

		if (sampleRate <= 0)
		{
			throw new InvalidDataException("WAV file did not contain a valid sample rate.");
		}

		if (blockAlign <= 0)
		{
			throw new InvalidDataException("WAV file did not contain a valid block alignment.");
		}

		if (dataOffset < 0)
		{
			throw new InvalidDataException("WAV file did not contain a data chunk.");
		}

		var payload = data.Slice(dataOffset, dataLength);

		return formatTag switch
		{
			WaveFormatPcm when bitsPerSample == 16 => ParsePcm16(payload, sampleRate, channels, blockAlign),
			WaveFormatIeeeFloat when bitsPerSample == 32 => ParseFloat32(payload, sampleRate, channels, blockAlign),
			_ => throw new NotSupportedException
			(
				$"Unsupported WAV format: formatTag={formatTag}, bitsPerSample={bitsPerSample}."
			),
		};
	}

	/// <summary>
	/// Converts interleaved audio to mono and resamples it to 16 kHz with linear interpolation.
	/// </summary>
	/// <param name="samples">The interleaved source samples.</param>
	/// <param name="sourceSampleRate">The source sample rate.</param>
	/// <param name="sourceChannels">The source channel count.</param>
	/// <returns>The resampled mono waveform.</returns>
	internal static float[] ResampleToMono16k
	(
		float[] samples,
		int sourceSampleRate,
		int sourceChannels
	)
	{
		ArgumentNullException.ThrowIfNull(samples);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSampleRate);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceChannels);

		if (samples.Length == 0)
		{
			return [];
		}

		if (samples.Length % sourceChannels != 0)
		{
			throw new ArgumentException("Sample count must be divisible by the channel count.", nameof(samples));
		}

		var frameCount = samples.Length / sourceChannels;
		var mono = new float[frameCount];
		if (sourceChannels == 1)
		{
			Array.Copy(samples, mono, frameCount);
		}
		else
		{
			for (var frame = 0; frame < frameCount; frame++)
			{
				double sum = 0;
				var baseIndex = frame * sourceChannels;
				for (var channel = 0; channel < sourceChannels; channel++)
				{
					sum += samples[baseIndex + channel];
				}

				mono[frame] = (float)(sum / sourceChannels);
			}
		}

		if (sourceSampleRate == TargetSampleRate)
		{
			return mono;
		}

		var targetLength = Math.Max
		(
			1,
			checked((int)Math.Round(frameCount * (double)TargetSampleRate / sourceSampleRate, MidpointRounding.AwayFromZero))
		);

		if (targetLength == 1)
		{
			return [mono[0]];
		}

		var result = new float[targetLength];
		var ratio = (double)sourceSampleRate / TargetSampleRate;
		for (var index = 0; index < targetLength; index++)
		{
			var sourcePosition = index * ratio;
			var left = (int)sourcePosition;
			if (left >= mono.Length - 1)
			{
				result[index] = mono[^1];
				continue;
			}

			var fraction = sourcePosition - left;
			var start = mono[left];
			var end = mono[left + 1];
			result[index] = (float)(start + ((end - start) * fraction));
		}

		return result;
	}

	/// <summary>
	/// Encodes a mono float waveform as a 16-bit PCM WAV file.
	/// </summary>
	/// <param name="samples">The input samples.</param>
	/// <param name="sampleRate">The sample rate.</param>
	/// <returns>The encoded WAV bytes.</returns>
	internal static byte[] EncodeWav
	(
		float[] samples,
		int sampleRate
	)
	{
		ArgumentNullException.ThrowIfNull(samples);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

		var channelCount = 1;
		var bitsPerSample = 16;
		var blockAlign = channelCount * (bitsPerSample / 8);
		var byteRate = sampleRate * blockAlign;
		var dataSize = checked(samples.Length * blockAlign);
		var wavBytes = new byte[RiffHeaderSize + dataSize];
		var span = wavBytes.AsSpan();

		"RIFF"u8.CopyTo(span);
		BinaryPrimitives.WriteInt32LittleEndian(span[4..], checked(36 + dataSize));
		"WAVE"u8.CopyTo(span[8..]);
		"fmt "u8.CopyTo(span[12..]);
		BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
		BinaryPrimitives.WriteUInt16LittleEndian(span[20..], WaveFormatPcm);
		BinaryPrimitives.WriteUInt16LittleEndian(span[22..], (ushort)channelCount);
		BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
		BinaryPrimitives.WriteInt32LittleEndian(span[28..], byteRate);
		BinaryPrimitives.WriteUInt16LittleEndian(span[32..], (ushort)blockAlign);
		BinaryPrimitives.WriteUInt16LittleEndian(span[34..], (ushort)bitsPerSample);
		"data"u8.CopyTo(span[36..]);
		BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataSize);

		FloatToPcm16(samples, span[44..]);

		return wavBytes;
	}

	/// <summary>
	/// Computes the root-mean-square amplitude of the samples.
	/// </summary>
	/// <param name="samples">The input samples.</param>
	/// <returns>The RMS amplitude, or 0 for an empty span.</returns>
	internal static double Rms(ReadOnlySpan<float> samples)
	{
		if (samples.Length == 0)
		{
			return 0.0;
		}

		var sum = 0.0;
		var i = 0;

		// SIMD path: sum of squares accumulates in a Vector<float> (one lane per CPU vector
		// element). For typical normalized speech (|x| <= 1) the per-frame sum stays well
		// inside float32 precision; reduced to double at the end for the final mean+sqrt.
		if (Vector.IsHardwareAccelerated && samples.Length >= Vector<float>.Count)
		{
			var vSum = Vector<float>.Zero;
			var width = Vector<float>.Count;
			for (; i <= samples.Length - width; i += width)
			{
				var v = new Vector<float>(samples.Slice(i, width));
				vSum += v * v;
			}

			sum = Vector.Sum(vSum);
		}

		for (; i < samples.Length; i++)
		{
			sum += (double)samples[i] * samples[i];
		}

		return Math.Sqrt(sum / samples.Length);
	}

	/// <summary>
	/// Computes the zero-crossing rate of the samples, in crossings per second. This is a cheap
	/// spectral-tilt proxy: real speech crosses zero far more often than a low-frequency drone.
	/// </summary>
	/// <param name="samples">The input samples.</param>
	/// <param name="sampleRate">The sample rate in Hz.</param>
	/// <returns>The zero-crossing rate in crossings per second.</returns>
	internal static double ZeroCrossingRate(ReadOnlySpan<float> samples, int sampleRate)
	{
		if (samples.Length < 2 || sampleRate <= 0)
		{
			return 0.0;
		}

		long crossings = 0;
		for (var i = 1; i < samples.Length; i++)
		{
			if ((samples[i - 1] < 0f) != (samples[i] < 0f))
			{
				crossings++;
			}
		}

		return crossings * (double)sampleRate / (samples.Length - 1);
	}

	// Real RVC speech (David/homer) runs a zero-crossing rate of roughly 2000-3500/s thanks to
	// fricatives and sibilants; corrupt DirectML output collapses into a ~300 Hz drone (~600/s).
	// Thresholds live in Settings.OutputValidation.

	/// <summary>
	/// Detects corrupt RVC output: audible energy paired with a zero-crossing rate far below
	/// real speech (typical signature of a continuous low-frequency drone produced by an
	/// intermittent DirectML inference failure on memory-constrained GPUs). Silence and short
	/// clips are never flagged.
	/// </summary>
	/// <param name="samples">The produced audio samples.</param>
	/// <param name="sampleRate">The sample rate in Hz.</param>
	/// <returns><c>true</c> if the audio looks corrupt; otherwise <c>false</c>.</returns>
	internal static bool IsLikelyCorruptOutput(ReadOnlySpan<float> samples, int sampleRate)
	{
		if (sampleRate <= 0 || samples.Length < sampleRate * Settings.OutputValidation.MinSeconds)
		{
			return false;
		}

		if (Rms(samples) < Settings.OutputValidation.MinRms)
		{
			return false;
		}

		return ZeroCrossingRate(samples, sampleRate) < Settings.OutputValidation.MaxZeroCrossingRate;
	}

	/// <summary>
	/// Converts normalized float samples (-1.0..1.0) to little-endian 16-bit PCM bytes.
	/// </summary>
	/// <param name="samples">The input samples.</param>
	/// <returns>The 16-bit PCM byte buffer (two bytes per sample).</returns>
	internal static byte[] FloatToPcm16(ReadOnlySpan<float> samples)
	{
		var bytes = new byte[checked(samples.Length * 2)];
		FloatToPcm16(samples, bytes);
		return bytes;
	}

	/// <summary>
	/// Converts normalized float samples (-1.0..1.0) to little-endian 16-bit PCM, writing into
	/// <paramref name="destination"/> (which must be at least <c>samples.Length * 2</c> bytes).
	/// </summary>
	/// <param name="samples">The input samples.</param>
	/// <param name="destination">The destination span for the PCM bytes.</param>
	internal static void FloatToPcm16
	(
		ReadOnlySpan<float> samples,
		Span<byte> destination
	)
	{
		for (var index = 0; index < samples.Length; index++)
		{
			var sample = Math.Clamp(samples[index], -1.0f, 1.0f);
			short pcm = sample switch
			{
				<= -1.0f => short.MinValue,
				>= 1.0f => short.MaxValue,
				_ => (short)Math.Round(sample * short.MaxValue, MidpointRounding.AwayFromZero),
			};

			BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(index * 2, 2), pcm);
		}
	}

	/// <summary>
	/// Applies the hardcoded zero-phase 48 Hz Butterworth high-pass filter used by the RVC pipeline.
	/// </summary>
	/// <param name="audio">The input waveform.</param>
	/// <returns>The filtered waveform.</returns>
	internal static float[] ButterworthHighPass
	(
		float[] audio
	)
	{
		ArgumentNullException.ThrowIfNull(audio);

		if (audio.Length == 0)
		{
			return [];
		}

		if (audio.Length <= HighPassPadLength)
		{
			throw new ArgumentException
			(
				$"Input length must be greater than the filtfilt pad length ({HighPassPadLength}).",
				nameof(audio)
			);
		}

		return ApplyFiltFilt(audio, HighPassB, HighPassA, HighPassPadLength);
	}

	/// <summary>
	/// Pads both sides of the waveform using NumPy-style reflection that excludes the edge sample.
	/// </summary>
	/// <param name="audio">The input waveform.</param>
	/// <param name="padSize">The number of samples to pad on each side.</param>
	/// <returns>The padded waveform.</returns>
	internal static float[] ReflectPad
	(
		float[] audio,
		int padSize
	)
	{
		ArgumentNullException.ThrowIfNull(audio);
		ArgumentOutOfRangeException.ThrowIfNegative(padSize);

		if (padSize == 0)
		{
			return [.. audio];
		}

		if (audio.Length == 0)
		{
			throw new ArgumentException("Cannot reflect-pad an empty signal.", nameof(audio));
		}

		if (audio.Length == 1)
		{
			var repeated = new float[audio.Length + (padSize * 2)];
			Array.Fill(repeated, audio[0]);
			return repeated;
		}

		var result = new float[audio.Length + (padSize * 2)];
		for (var index = 0; index < padSize; index++)
		{
			result[index] = audio[ReflectIndex(index - padSize, audio.Length)];
		}

		Array.Copy(audio, 0, result, padSize, audio.Length);

		for (var index = 0; index < padSize; index++)
		{
			result[padSize + audio.Length + index] = audio[ReflectIndex(audio.Length + index, audio.Length)];
		}

		return result;
	}

	/// <summary>
	/// Computes an STFT magnitude spectrogram using a periodic Hann window.
	/// </summary>
	/// <param name="audio">The input waveform.</param>
	/// <param name="nFft">The FFT size.</param>
	/// <param name="hopLength">The hop size in samples.</param>
	/// <param name="winLength">The window size in samples.</param>
	/// <param name="center">Whether to zero-pad by <c>nFft / 2</c> on each side.</param>
	/// <returns>A magnitude spectrogram with shape <c>[nFft / 2 + 1, numFrames]</c>.</returns>
	internal static float[,] ComputeStft
	(
		float[] audio,
		int nFft,
		int hopLength,
		int winLength,
		bool center
	)
	{
		ArgumentNullException.ThrowIfNull(audio);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nFft);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hopLength);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(winLength);

		if (winLength > nFft)
		{
			throw new ArgumentOutOfRangeException(nameof(winLength), "winLength must be less than or equal to nFft.");
		}

		var signal = center ? ZeroPad(audio, nFft / 2) : [.. audio];
		var frameLength = nFft;
		var numFrames = signal.Length <= frameLength
			? 1
			: 1 + ((signal.Length - frameLength) / hopLength);
		var outputBins = (nFft / 2) + 1;
		var spectrogram = new float[outputBins, Math.Max(0, numFrames)];
		var fftSize = NextPowerOfTwo(nFft);
		var window = BuildCenteredHannWindow(nFft, winLength);

		var real = new double[fftSize];
		var imag = new double[fftSize];

		for (var frame = 0; frame < numFrames; frame++)
		{
			Array.Clear(real);
			Array.Clear(imag);

			var start = frame * hopLength;
			var available = Math.Min(frameLength, Math.Max(0, signal.Length - start));
			for (var index = 0; index < available; index++)
			{
				real[index] = signal[start + index] * window[index];
			}

			FftInPlace(real, imag);

			for (var bin = 0; bin < outputBins; bin++)
			{
				spectrogram[bin, frame] = (float)Math.Sqrt((real[bin] * real[bin]) + (imag[bin] * imag[bin]));
			}
		}

		return spectrogram;
	}

	/// <summary>
	/// Creates a Librosa-compatible HTK mel filterbank with Slaney normalization.
	/// </summary>
	/// <param name="sampleRate">The audio sample rate.</param>
	/// <param name="nFft">The FFT size.</param>
	/// <param name="nMels">The number of mel bands.</param>
	/// <param name="fMin">The minimum frequency in Hz.</param>
	/// <param name="fMax">The maximum frequency in Hz.</param>
	/// <returns>A mel filterbank matrix with shape <c>[nMels, nFft / 2 + 1]</c>.</returns>
	internal static float[,] ComputeMelFilterbank
	(
		int sampleRate,
		int nFft,
		int nMels,
		float fMin,
		float fMax
	)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nFft);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nMels);
		ArgumentOutOfRangeException.ThrowIfNegative(fMin);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fMax);

		if (fMax <= fMin)
		{
			throw new ArgumentOutOfRangeException(nameof(fMax), "fMax must be greater than fMin.");
		}

		var fftBins = (nFft / 2) + 1;
		var weights = new float[nMels, fftBins];
		var fftFrequencies = new double[fftBins];
		for (var bin = 0; bin < fftBins; bin++)
		{
			fftFrequencies[bin] = sampleRate * (double)bin / nFft;
		}

		var melPoints = new double[nMels + 2];
		var melMin = HzToMel(fMin);
		var melMax = HzToMel(fMax);
		for (var index = 0; index < melPoints.Length; index++)
		{
			var mel = melMin + (((melMax - melMin) * index) / (melPoints.Length - 1));
			melPoints[index] = MelToHz(mel);
		}

		for (var melIndex = 0; melIndex < nMels; melIndex++)
		{
			var leftHz = melPoints[melIndex];
			var centerHz = melPoints[melIndex + 1];
			var rightHz = melPoints[melIndex + 2];
			var leftWidth = centerHz - leftHz;
			var rightWidth = rightHz - centerHz;
			var areaScale = 2.0 / (rightHz - leftHz);

			for (var bin = 0; bin < fftBins; bin++)
			{
				var frequency = fftFrequencies[bin];
				var lower = leftWidth == 0 ? 0.0 : (frequency - leftHz) / leftWidth;
				var upper = rightWidth == 0 ? 0.0 : (rightHz - frequency) / rightWidth;
				var weight = Math.Max(0.0, Math.Min(lower, upper)) * areaScale;
				weights[melIndex, bin] = (float)weight;
			}
		}

		return weights;
	}

	/// <summary>
	/// Computes the RMVPE log-mel spectrogram.
	/// </summary>
	/// <param name="audio">The input waveform at 16 kHz.</param>
	/// <param name="center">Whether to center frames during STFT.</param>
	/// <returns>A log-mel spectrogram with shape <c>[128, numFrames]</c>.</returns>
	internal static float[,] ComputeMelSpectrogram
	(
		float[] audio,
		bool center
	)
	{
		ArgumentNullException.ThrowIfNull(audio);

		var magnitude = ComputeStft(audio, RmvpeFftSize, RmvpeHopLength, RmvpeWindowLength, center);
		var melBasis = ComputeMelFilterbank
		(
			TargetSampleRate,
			RmvpeFftSize,
			RmvpeMelCount,
			RmvpeMelMin,
			RmvpeMelMax
		);

		var frames = magnitude.GetLength(1);
		var melSpectrogram = new float[RmvpeMelCount, frames];
		for (var mel = 0; mel < RmvpeMelCount; mel++)
		{
			for (var frame = 0; frame < frames; frame++)
			{
				double sum = 0;
				for (var bin = 0; bin < magnitude.GetLength(0); bin++)
				{
					sum += melBasis[mel, bin] * magnitude[bin, frame];
				}

				melSpectrogram[mel, frame] = MathF.Log(MathF.Max((float)sum, LogClamp));
			}
		}

		return melSpectrogram;
	}

	/// <summary>
	/// Computes centered, windowed RMS energy to match <c>librosa.feature.rms</c>.
	/// </summary>
	/// <param name="audio">The input waveform.</param>
	/// <param name="frameLength">The analysis frame length.</param>
	/// <param name="hopLength">The hop size in samples.</param>
	/// <returns>One RMS value per frame.</returns>
	internal static float[] ComputeRms
	(
		float[] audio,
		int frameLength,
		int hopLength
	)
	{
		ArgumentNullException.ThrowIfNull(audio);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameLength);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hopLength);

		var padded = ZeroPad(audio, frameLength / 2);
		var numFrames = padded.Length <= frameLength
			? 1
			: 1 + ((padded.Length - frameLength) / hopLength);
		var rms = new float[numFrames];

		for (var frame = 0; frame < numFrames; frame++)
		{
			var start = frame * hopLength;
			double sumSquares = 0;
			var index = 0;

			// SIMD inner sum-of-squares.
			if (Vector.IsHardwareAccelerated && frameLength >= Vector<float>.Count)
			{
				var vSum = Vector<float>.Zero;
				var width = Vector<float>.Count;
				for (; index <= frameLength - width; index += width)
				{
					var v = new Vector<float>(padded.AsSpan(start + index, width));
					vSum += v * v;
				}

				sumSquares = Vector.Sum(vSum);
			}

			for (; index < frameLength; index++)
			{
				var sample = padded[start + index];
				sumSquares += sample * sample;
			}

			rms[frame] = (float)Math.Sqrt(sumSquares / frameLength);
		}

		return rms;
	}

	/// <summary>
	/// Resizes a 1D array using linear interpolation matching the NumPy expression used by the reference code.
	/// </summary>
	/// <param name="values">The source values.</param>
	/// <param name="targetLength">The target output length.</param>
	/// <returns>The interpolated values.</returns>
	internal static float[] InterpolateLinear
	(
		float[] values,
		int targetLength
	)
	{
		ArgumentNullException.ThrowIfNull(values);
		ArgumentOutOfRangeException.ThrowIfNegative(targetLength);

		if (targetLength == 0 || values.Length == 0)
		{
			return [];
		}

		if (values.Length == 1)
		{
			var repeated = new float[targetLength];
			Array.Fill(repeated, values[0]);
			return repeated;
		}

		var result = new float[targetLength];
		for (var index = 0; index < targetLength; index++)
		{
			var position = index * (values.Length - 1.0) / targetLength;
			var left = (int)Math.Floor(position);
			if (left >= values.Length - 1)
			{
				result[index] = values[^1];
				continue;
			}

			var fraction = position - left;
			var start = values[left];
			var end = values[left + 1];
			result[index] = (float)(start + ((end - start) * fraction));
		}

		return result;
	}

	/// <summary>
	/// Parses PCM16 samples.
	/// </summary>
	/// <param name="payload">The payload.</param>
	/// <param name="sampleRate">The sample rate.</param>
	/// <param name="channels">The channel count.</param>
	/// <param name="blockAlign">The block alignment.</param>
	/// <returns>The samples, sample rate, and channel count.</returns>
	private static
	(
		float[] Samples,
		int SampleRate,
		int Channels
	) ParsePcm16
	(
		ReadOnlySpan<byte> payload,
		int sampleRate,
		int channels,
		int blockAlign
	)
	{
		if (payload.Length % blockAlign != 0)
		{
			throw new InvalidDataException("PCM16 data chunk is not aligned to whole frames.");
		}

		var bytesPerSample = 2;
		var sampleCount = payload.Length / bytesPerSample;
		var samples = new float[sampleCount];
		for (var index = 0; index < sampleCount; index++)
		{
			var value = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(index * bytesPerSample, bytesPerSample));
			samples[index] = value / 32768.0f;
		}

		return (samples, sampleRate, channels);
	}

	/// <summary>
	/// Parses float32 samples.
	/// </summary>
	/// <param name="payload">The payload.</param>
	/// <param name="sampleRate">The sample rate.</param>
	/// <param name="channels">The channel count.</param>
	/// <param name="blockAlign">The block alignment.</param>
	/// <returns>The samples, sample rate, and channel count.</returns>
	private static
	(
		float[] Samples,
		int SampleRate,
		int Channels
	) ParseFloat32
	(
		ReadOnlySpan<byte> payload,
		int sampleRate,
		int channels,
		int blockAlign
	)
	{
		if (payload.Length % blockAlign != 0)
		{
			throw new InvalidDataException("Float32 data chunk is not aligned to whole frames.");
		}

		var bytesPerSample = 4;
		var sampleCount = payload.Length / bytesPerSample;
		var samples = new float[sampleCount];
		for (var index = 0; index < sampleCount; index++)
		{
			var bits = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(index * bytesPerSample, bytesPerSample));
			samples[index] = Math.Clamp(BitConverter.Int32BitsToSingle(bits), -1.0f, 1.0f);
		}

		return (samples, sampleRate, channels);
	}

	/// <summary>
	/// Applies zero-phase filtering.
	/// </summary>
	/// <param name="audio">The audio samples.</param>
	/// <param name="b">The numerator coefficients.</param>
	/// <param name="a">The denominator coefficients.</param>
	/// <param name="padLength">The pad length.</param>
	/// <returns>The resulting samples.</returns>
	private static float[] ApplyFiltFilt
	(
		float[] audio,
		double[] b,
		double[] a,
		int padLength
	)
	{
		var extended = CreateOddExtension(audio, padLength);
		var zi = ComputeLFilterZi(b, a);

		var forward = ApplyIirFilter(b, a, extended, ScaleState(zi, extended[0]));
		Array.Reverse(forward);
		var backward = ApplyIirFilter(b, a, forward, ScaleState(zi, forward[0]));
		Array.Reverse(backward);

		var result = new float[audio.Length];
		Array.Copy(backward, padLength, result, 0, audio.Length);
		return result;
	}

	/// <summary>
	/// Creates the odd extension.
	/// </summary>
	/// <param name="audio">The audio samples.</param>
	/// <param name="padLength">The pad length.</param>
	/// <returns>The resulting samples.</returns>
	private static float[] CreateOddExtension
	(
		float[] audio,
		int padLength
	)
	{
		if (padLength == 0)
		{
			return [.. audio];
		}

		if (padLength > audio.Length - 1)
		{
			throw new ArgumentException
			(
				$"Pad length {padLength} must not exceed input length - 1 ({audio.Length - 1}).",
				nameof(padLength)
			);
		}

		var result = new float[audio.Length + (padLength * 2)];
		var leftEnd = audio[0];
		var rightEnd = audio[^1];

		for (var index = 0; index < padLength; index++)
		{
			result[index] = (2 * leftEnd) - audio[padLength - index];
			result[result.Length - padLength + index] = (2 * rightEnd) - audio[audio.Length - 2 - index];
		}

		Array.Copy(audio, 0, result, padLength, audio.Length);
		return result;
	}

	/// <summary>
	/// Applies the IIR filter.
	/// </summary>
	/// <param name="b">The numerator coefficients.</param>
	/// <param name="a">The denominator coefficients.</param>
	/// <param name="input">The input values.</param>
	/// <param name="initialState">The initial filter state.</param>
	/// <returns>The resulting samples.</returns>
	private static float[] ApplyIirFilter
	(
		double[] b,
		double[] a,
		float[] input,
		double[] initialState
	)
	{
		var order = Math.Max(a.Length, b.Length) - 1;
		var paddedB = new double[order + 1];
		var paddedA = new double[order + 1];
		Array.Copy(b, paddedB, b.Length);
		Array.Copy(a, paddedA, a.Length);

		var a0 = paddedA[0];
		if (a0 == 0)
		{
			throw new ArgumentException("Filter denominator must have a non-zero leading coefficient.", nameof(a));
		}

		if (a0 != 1.0)
		{
			for (var index = 0; index < paddedB.Length; index++)
			{
				paddedB[index] /= a0;
				paddedA[index] /= a0;
			}
		}

		var state = new double[order];
		Array.Copy(initialState, state, Math.Min(initialState.Length, state.Length));
		var output = new float[input.Length];

		for (var sampleIndex = 0; sampleIndex < input.Length; sampleIndex++)
		{
			var inputValue = input[sampleIndex];
			var outputValue = paddedB[0] * inputValue;
			if (order > 0)
			{
				outputValue += state[0];
			}

			for (var stateIndex = 0; stateIndex < order - 1; stateIndex++)
			{
				state[stateIndex] =
					state[stateIndex + 1] +
					(paddedB[stateIndex + 1] * inputValue) -
					(paddedA[stateIndex + 1] * outputValue);
			}

			if (order > 0)
			{
				state[order - 1] = (paddedB[order] * inputValue) - (paddedA[order] * outputValue);
			}

			output[sampleIndex] = (float)outputValue;
		}

		return output;
	}

	/// <summary>
	/// Computes the filter initial state.
	/// </summary>
	/// <param name="b">The numerator coefficients.</param>
	/// <param name="a">The denominator coefficients.</param>
	/// <returns>The initial filter state.</returns>
	private static double[] ComputeLFilterZi
	(
		double[] b,
		double[] a
	)
	{
		var order = Math.Max(a.Length, b.Length);
		var paddedB = new double[order];
		var paddedA = new double[order];
		Array.Copy(b, paddedB, b.Length);
		Array.Copy(a, paddedA, a.Length);

		var a0 = paddedA[0];
		if (a0 == 0)
		{
			throw new ArgumentException("Filter denominator must have a non-zero leading coefficient.", nameof(a));
		}

		if (a0 != 1.0)
		{
			for (var index = 0; index < order; index++)
			{
				paddedB[index] /= a0;
				paddedA[index] /= a0;
			}
		}

		double sumA = 0;
		double sumB = 0;
		for (var index = 0; index < order; index++)
		{
			sumA += paddedA[index];
			sumB += paddedB[index];
		}

		if (sumA == 0)
		{
			throw new ArgumentException("Filter is unstable because sum(a) is zero.", nameof(a));
		}

		var steadyState = sumB / sumA;
		var zi = new double[order - 1];
		double cumulative = 0;
		for (var index = order - 1; index >= 1; index--)
		{
			cumulative += paddedB[index] - (steadyState * paddedA[index]);
			zi[index - 1] = cumulative;
		}

		return zi;
	}

	/// <summary>
	/// Scales the filter state.
	/// </summary>
	/// <param name="state">The filter state.</param>
	/// <param name="scale">The scale factor.</param>
	/// <returns>The scaled state.</returns>
	private static double[] ScaleState
	(
		double[] state,
		float scale
	)
	{
		var scaled = new double[state.Length];
		for (var index = 0; index < state.Length; index++)
		{
			scaled[index] = state[index] * scale;
		}

		return scaled;
	}

	/// <summary>
	/// Zero-pads the waveform.
	/// </summary>
	/// <param name="audio">The audio samples.</param>
	/// <param name="pad">The padding size.</param>
	/// <returns>The resulting samples.</returns>
	private static float[] ZeroPad
	(
		float[] audio,
		int pad
	)
	{
		if (pad <= 0)
		{
			return [.. audio];
		}

		var result = new float[audio.Length + (pad * 2)];
		Array.Copy(audio, 0, result, pad, audio.Length);
		return result;
	}

	/// <summary>
	/// Builds the centered Hann window.
	/// </summary>
	/// <param name="nFft">The FFT size.</param>
	/// <param name="winLength">The window length.</param>
	/// <returns>The resulting samples.</returns>
	private static float[] BuildCenteredHannWindow
	(
		int nFft,
		int winLength
	)
	{
		var window = new float[nFft];
		var offset = (nFft - winLength) / 2;
		for (var index = 0; index < winLength; index++)
		{
			window[offset + index] = (float)(0.5 - (0.5 * Math.Cos((2.0 * Math.PI * index) / winLength)));
		}

		return window;
	}

	/// <summary>
	/// Reflects the index.
	/// </summary>
	/// <param name="index">The i.</param>
	/// <param name="length">The sequence length.</param>
	/// <returns>The resulting integer value.</returns>
	private static int ReflectIndex
	(
		int index,
		int length
	)
	{
		if (length == 1)
		{
			return 0;
		}

		var period = (length * 2) - 2;
		var wrapped = index % period;
		if (wrapped < 0)
		{
			wrapped += period;
		}

		return wrapped < length ? wrapped : period - wrapped;
	}

	/// <summary>
	/// Converts Hz to mel.
	/// </summary>
	/// <param name="frequency">The frequency.</param>
	/// <returns>The resulting floating-point value.</returns>
	private static double HzToMel
	(
		double frequency
	)
	{
		return 2595.0 * Math.Log10(1.0 + (frequency / 700.0));
	}

	/// <summary>
	/// Converts mel to Hz.
	/// </summary>
	/// <param name="mel">The mel value.</param>
	/// <returns>The resulting floating-point value.</returns>
	private static double MelToHz
	(
		double mel
	)
	{
		return 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);
	}

	/// <summary>
	/// Computes the next power of two.
	/// </summary>
	/// <param name="value">The value.</param>
	/// <returns>The resulting integer value.</returns>
	private static int NextPowerOfTwo
	(
		int value
	)
	{
		if (value <= 1)
		{
			return 1;
		}

		var power = 1;
		while (power < value)
		{
			power <<= 1;
		}

		return power;
	}

	/// <summary>
	/// Performs an in-place radix-2 FFT on separate real and imaginary buffers.
	/// </summary>
	/// <param name="real">The real component buffer.</param>
	/// <param name="imaginary">The imaginary component buffer.</param>
	private static void FftInPlace
	(
		double[] real,
		double[] imaginary
	)
	{
		if (real.Length != imaginary.Length)
		{
			throw new ArgumentException("FFT buffers must have the same length.", nameof(imaginary));
		}

		var n = real.Length;
		if ((n & (n - 1)) != 0)
		{
			throw new ArgumentException("FFT buffer length must be a power of two.", nameof(real));
		}

		var j = 0;
		for (var i = 1; i < n; i++)
		{
			var bit = n >> 1;
			while ((j & bit) != 0)
			{
				j ^= bit;
				bit >>= 1;
			}

			j ^= bit;
			if (i >= j)
			{
				continue;
			}

			(real[i], real[j]) = (real[j], real[i]);
			(imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
		}

		for (var length = 2; length <= n; length <<= 1)
		{
			var angle = (-2.0 * Math.PI) / length;
			var wLengthReal = Math.Cos(angle);
			var wLengthImaginary = Math.Sin(angle);

			for (var start = 0; start < n; start += length)
			{
				var wReal = 1.0;
				var wImaginary = 0.0;
				var halfLength = length >> 1;

				for (var offset = 0; offset < halfLength; offset++)
				{
					var evenIndex = start + offset;
					var oddIndex = evenIndex + halfLength;

					var tempReal = (real[oddIndex] * wReal) - (imaginary[oddIndex] * wImaginary);
					var tempImaginary = (real[oddIndex] * wImaginary) + (imaginary[oddIndex] * wReal);

					real[oddIndex] = real[evenIndex] - tempReal;
					imaginary[oddIndex] = imaginary[evenIndex] - tempImaginary;
					real[evenIndex] += tempReal;
					imaginary[evenIndex] += tempImaginary;

					var nextWReal = (wReal * wLengthReal) - (wImaginary * wLengthImaginary);
					wImaginary = (wReal * wLengthImaginary) + (wImaginary * wLengthReal);
					wReal = nextWReal;
				}
			}
		}
	}

	/// <summary>
	/// Reads an audio file (.wav, .mp3, or .ogg) and returns its content as
	/// WAV bytes suitable for <see cref="ParseWavToFloat"/> or RVC processing.
	/// </summary>
	internal static byte[] ReadAudioFileToWav(string filePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

		if (!File.Exists(filePath))
		{
			throw new FileNotFoundException
			(
				$"Audio file not found: '{filePath}'",
				filePath
			);
		}

		var ext = Path.GetExtension(filePath);

		if (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase))
		{
			return File.ReadAllBytes(filePath);
		}

		if (ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
		{
			return DecodeMp3ToWav(filePath);
		}

		if (ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
		{
			return DecodeOggToWav(filePath);
		}

		throw new InvalidOperationException
		(
			$"Unsupported input audio format '{ext}'. Use .wav, .mp3, or .ogg."
		);
	}

	/// <summary>
	/// Decodes an MP3 file to WAV bytes.
	/// </summary>
	/// <param name="filePath">The file path.</param>
	/// <returns>The resulting bytes.</returns>
	private static byte[] DecodeMp3ToWav(string filePath)
	{
		using var fileStream = File.OpenRead(filePath);
		using var reader = new NLayer.MpegFile(fileStream);

		var sampleRate = reader.SampleRate;
		var channels = reader.Channels;

		// Read all samples (interleaved float)
		var buffer = new float[sampleRate * channels * 10]; // 10 sec initial
		var totalRead = 0;

		while (true)
		{
			if (totalRead >= buffer.Length)
			{
				Array.Resize(ref buffer, buffer.Length * 2);
			}

			var read = reader.ReadSamples(buffer, totalRead, buffer.Length - totalRead);
			if (read <= 0)
			{
				break;
			}

			totalRead += read;
		}

		// Downmix to mono if stereo
		float[] mono;
		if (channels > 1)
		{
			var frameCount = totalRead / channels;
			mono = new float[frameCount];
			for (var i = 0; i < frameCount; i++)
			{
				var sum = 0f;
				for (var ch = 0; ch < channels; ch++)
				{
					sum += buffer[i * channels + ch];
				}

				mono[i] = sum / channels;
			}
		}
		else
		{
			mono = buffer[..totalRead];
		}

		return EncodeWav(mono, sampleRate);
	}

	/// <summary>
	/// Decodes an OGG file to WAV bytes.
	/// </summary>
	/// <param name="filePath">The file path.</param>
	/// <returns>The resulting bytes.</returns>
	private static byte[] DecodeOggToWav(string filePath)
	{
		using var fileStream = File.OpenRead(filePath);
		using var decoder = Concentus.OpusCodecFactory.CreateDecoder(48000, 1);
		var oggReader = new Concentus.Oggfile.OpusOggReadStream(decoder, fileStream);

		var samples = new List<float>();
		while (oggReader.HasNextPacket)
		{
			var packet = oggReader.DecodeNextPacket();
			if (packet is not null)
			{
				for (var i = 0; i < packet.Length; i++)
				{
					samples.Add(packet[i] / 32768f);
				}
			}
		}

		return EncodeWav(samples.ToArray(), 48000);
	}
}
