using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

using ConcentusApplication = Concentus.Enums.OpusApplication;
using OpusCodecFactory = Concentus.OpusCodecFactory;

namespace Talktastic;

internal static class AudioOutput
{
	public static IReadOnlyList<AudioDeviceInfo> GetSpeakers()
	{
		var selector = MediaDevice.GetAudioRenderSelector();
		var devices = DeviceInformation.FindAllAsync(selector).GetAwaiter().GetResult();

		return devices
			.Select
			(
				static device => new AudioDeviceInfo
				(
					Id: device.Id,
					FriendlyName: device.Name
				)
			)
			.OrderBy(static device => device.FriendlyName, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	public static async Task PlayToDeviceAsync(byte[] wavData, string? deviceQuery)
	{
		using var memStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
		using (var writer = new Windows.Storage.Streams.DataWriter(memStream))
		{
			writer.WriteBytes(wavData);
			await writer.StoreAsync();
			writer.DetachStream();
		}
		memStream.Seek(0);

		using var player = new Windows.Media.Playback.MediaPlayer();

		if (!string.IsNullOrWhiteSpace(deviceQuery))
		{
			var device = await ResolveWinRTDeviceAsync(deviceQuery).ConfigureAwait(false);
			player.AudioDevice = device;
		}

		player.Source = Windows.Media.Core.MediaSource.CreateFromStream(memStream, "audio/wav");

		var tcs = new TaskCompletionSource();
		player.MediaEnded += (_, _) => tcs.TrySetResult();
		player.MediaFailed += (_, e) => tcs.TrySetException(new InvalidOperationException(e.ErrorMessage));
		player.Play();
		await tcs.Task.ConfigureAwait(false);
	}

	public static async Task WriteMp3Async
	(
		byte[] audioBytes,
		SpeechSynthesisOutputFormat outputFormat,
		string outputPath,
		AudioMetadata? metadata = null,
		CancellationToken cancellationToken = default
	)
	{
		var formatInfo = GetFormatInfo(outputFormat);
		EnsureDirectoryExists(outputPath);

		var pcmBytes = formatInfo.HasRiffHeader ? StripWaveHeader(audioBytes) : audioBytes;
		var mp3Bytes = LameEncoder.EncodePcmToMp3(pcmBytes, formatInfo.SampleRate, 1);

		using var output = File.Create(outputPath);

		if (metadata is not null)
		{
			var id3 = Id3Writer.CreateTag(metadata);
			await output.WriteAsync(id3, cancellationToken).ConfigureAwait(false);
		}

		await output.WriteAsync(mp3Bytes, cancellationToken).ConfigureAwait(false);
	}

	public static async Task WriteOggOpusAsync
	(
		byte[] audioBytes,
		SpeechSynthesisOutputFormat outputFormat,
		string outputPath,
		AudioMetadata? metadata = null,
		CancellationToken cancellationToken = default
	)
	{
		var formatInfo = GetFormatInfo(outputFormat);
		EnsureDirectoryExists(outputPath);

		var pcmBytes = formatInfo.HasRiffHeader ? StripWaveHeader(audioBytes) : audioBytes;

		await Task.Run
		(
			() => OggOpusEncoder.EncodeToFile
			(
				pcmBytes,
				formatInfo.SampleRate,
				1,
				outputPath,
				metadata
			),
			cancellationToken
		).ConfigureAwait(false);
	}

	public static byte[] ReadAllBytes(PullAudioOutputStream stream)
	{
		using var buffer = new MemoryStream();
		var chunk = new byte[4096];

		while (true)
		{
			var bytesRead = stream.Read(chunk);
			if (bytesRead == 0)
			{
				return buffer.ToArray();
			}

			buffer.Write(chunk, 0, checked((int)bytesRead));
		}
	}

	public static void EnsureDirectoryExists(string path)
	{
		var directory = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrWhiteSpace(directory))
		{
			Directory.CreateDirectory(directory);
		}
	}

	private static async Task<DeviceInformation> ResolveWinRTDeviceAsync(string deviceQuery)
	{
		var selector = MediaDevice.GetAudioRenderSelector();
		var devices = await DeviceInformation.FindAllAsync(selector);
		var culture = CultureInfo.InvariantCulture;

		// Exact match first
		var exact = devices.FirstOrDefault
		(
			device =>
				culture.CompareInfo.Compare(device.Name, deviceQuery, CompareOptions.IgnoreCase) == 0 ||
				culture.CompareInfo.Compare(device.Id, deviceQuery, CompareOptions.IgnoreCase) == 0
		);

		if (exact is not null)
		{
			return exact;
		}

		// Substring match
		var partials = devices
			.Where
			(
				device =>
					culture.CompareInfo.IndexOf(device.Name, deviceQuery, CompareOptions.IgnoreCase) >= 0 ||
					culture.CompareInfo.IndexOf(device.Id, deviceQuery, CompareOptions.IgnoreCase) >= 0
			)
			.ToArray();

		return partials.Length switch
		{
			1 => partials[0],
			0 => throw new InvalidOperationException($"No speaker matched '{deviceQuery}'."),
			_ => throw new InvalidOperationException
			(
				$"Speaker '{deviceQuery}' is ambiguous. Matches: {string.Join(", ", partials.Select(static x => x.Name))}"
			),
		};
	}

	private static PcmFormatInfo GetFormatInfo(SpeechSynthesisOutputFormat outputFormat)
	{
		return outputFormat switch
		{
			SpeechSynthesisOutputFormat.Riff8Khz16BitMonoPcm => new PcmFormatInfo(8000, true),
			SpeechSynthesisOutputFormat.Raw8Khz16BitMonoPcm => new PcmFormatInfo(8000, false),
			SpeechSynthesisOutputFormat.Riff16Khz16BitMonoPcm => new PcmFormatInfo(16000, true),
			SpeechSynthesisOutputFormat.Raw16Khz16BitMonoPcm => new PcmFormatInfo(16000, false),
			SpeechSynthesisOutputFormat.Riff24Khz16BitMonoPcm => new PcmFormatInfo(24000, true),
			SpeechSynthesisOutputFormat.Raw24Khz16BitMonoPcm => new PcmFormatInfo(24000, false),
			SpeechSynthesisOutputFormat.Riff22050Hz16BitMonoPcm => new PcmFormatInfo(22050, true),
			SpeechSynthesisOutputFormat.Raw22050Hz16BitMonoPcm => new PcmFormatInfo(22050, false),
			SpeechSynthesisOutputFormat.Riff44100Hz16BitMonoPcm => new PcmFormatInfo(44100, true),
			SpeechSynthesisOutputFormat.Raw44100Hz16BitMonoPcm => new PcmFormatInfo(44100, false),
			SpeechSynthesisOutputFormat.Riff48Khz16BitMonoPcm => new PcmFormatInfo(48000, true),
			SpeechSynthesisOutputFormat.Raw48Khz16BitMonoPcm => new PcmFormatInfo(48000, false),
			_ => throw new InvalidOperationException
			(
				$"Encoded output only supports 16-bit mono PCM synthesis formats. Got {outputFormat}."
			),
		};
	}

	private static byte[] StripWaveHeader(byte[] waveBytes)
	{
		if (waveBytes.Length < 44)
		{
			throw new InvalidOperationException("WAV output was shorter than a valid RIFF header.");
		}

		if (!waveBytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !waveBytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
		{
			throw new InvalidOperationException("Expected RIFF/WAVE data but got a different payload.");
		}

		return waveBytes[44..];
	}
}

internal readonly record struct AudioDeviceInfo(string Id, string FriendlyName);

internal readonly record struct PcmFormatInfo(int SampleRate, bool HasRiffHeader);

/// <summary>
/// Direct P/Invoke bindings to libmp3lame.dll for AOT-compatible MP3 encoding.
/// </summary>
#pragma warning disable CA5392 // DLL search path controlled by NativeExtractor.SetDllDirectory
internal static partial class LameEncoder
{
	private const int VBR_OFF = 0;

	[LibraryImport("libmp3lame", EntryPoint = "lame_init")]
	private static partial nint Init();

	[LibraryImport("libmp3lame", EntryPoint = "lame_set_in_samplerate")]
	private static partial int SetInSamplerate(nint gfp, int sampleRate);

	[LibraryImport("libmp3lame", EntryPoint = "lame_set_num_channels")]
	private static partial int SetNumChannels(nint gfp, int channels);

	[LibraryImport("libmp3lame", EntryPoint = "lame_set_brate")]
	private static partial int SetBrate(nint gfp, int bitrate);

	[LibraryImport("libmp3lame", EntryPoint = "lame_set_VBR")]
	private static partial int SetVBR(nint gfp, int vbrMode);

	[LibraryImport("libmp3lame", EntryPoint = "lame_set_quality")]
	private static partial int SetQuality(nint gfp, int quality);

	[LibraryImport("libmp3lame", EntryPoint = "lame_init_params")]
	private static partial int InitParams(nint gfp);

	[LibraryImport("libmp3lame", EntryPoint = "lame_encode_buffer")]
	private static partial int EncodeBuffer
	(
		nint gfp,
		nint bufferL,
		nint bufferR,
		int nsamples,
		nint mp3buf,
		int mp3bufSize
	);

	[LibraryImport("libmp3lame", EntryPoint = "lame_encode_flush")]
	private static partial int EncodeFlush(nint gfp, nint mp3buf, int size);

	[LibraryImport("libmp3lame", EntryPoint = "lame_close")]
	private static partial int Close(nint gfp);

	private static void CheckLameResult(int result, string function)
	{
		if (result < 0)
		{
			throw new InvalidOperationException($"LAME {function} failed with error {result}.");
		}
	}

	public static byte[] EncodePcmToMp3(byte[] pcmData, int sampleRate, int channels)
	{
		NativeExtractor.EnsureAvailable(DllGroup.Lame);

		var gfp = Init();
		if (gfp == 0)
		{
			throw new InvalidOperationException("Failed to initialize LAME encoder.");
		}

		try
		{
			CheckLameResult(SetInSamplerate(gfp, sampleRate), nameof(SetInSamplerate));
			CheckLameResult(SetNumChannels(gfp, channels), nameof(SetNumChannels));
			CheckLameResult(SetBrate(gfp, 128), nameof(SetBrate));
			CheckLameResult(SetVBR(gfp, VBR_OFF), nameof(SetVBR));
			CheckLameResult(SetQuality(gfp, 2), nameof(SetQuality));
			CheckLameResult(InitParams(gfp), nameof(InitParams));

			var numSamples = pcmData.Length / (2 * channels);
			// Worst case: 1.25 * numSamples + 7200
			var mp3BufSize = (int)(1.25 * numSamples) + 7200;
			var mp3Buf = new byte[mp3BufSize];

			int bytesEncoded;
			unsafe
			{
				fixed (byte* pcmPtr = pcmData)
				fixed (byte* mp3Ptr = mp3Buf)
				{
					bytesEncoded = EncodeBuffer
					(
						gfp,
						(nint)pcmPtr,
						(nint)pcmPtr,
						numSamples,
						(nint)mp3Ptr,
						mp3BufSize
					);

					if (bytesEncoded < 0)
					{
						throw new InvalidOperationException($"LAME encode failed with error {bytesEncoded}.");
					}

					var flushed = EncodeFlush(gfp, (nint)(mp3Ptr + bytesEncoded), mp3BufSize - bytesEncoded);
					if (flushed > 0)
					{
						bytesEncoded += flushed;
					}
				}
			}

			return mp3Buf[..bytesEncoded];
		}
		finally
		{
			_ = Close(gfp);
		}
	}
}

internal sealed record AudioMetadata(string VoiceName, string SpokenText);

/// <summary>
/// Pure managed Opus encoder using Concentus. No native DLLs needed.
/// </summary>
internal static class OggOpusEncoder
{
	public static void EncodeToFile
	(
			byte[] pcmBytes,
			int sampleRate,
			int channels,
			string outputPath,
			AudioMetadata? metadata
	)
	{
			using var encoder = OpusCodecFactory.CreateEncoder(48000, channels, ConcentusApplication.OPUS_APPLICATION_VOIP);
			encoder.Bitrate = 64000;

			Concentus.Oggfile.OpusTags? tags = null;

			if (metadata is not null)
			{
				tags = new Concentus.Oggfile.OpusTags
				{
					Comment = "Talktastic",
				};
				tags.Fields["ARTIST"] = metadata.VoiceName;
				tags.Fields["TITLE"] = Truncate(metadata.SpokenText, 256);
				tags.Fields["ENCODER"] = "Talktastic";
			}

			// Convert byte[] PCM16 to short[]
			var sampleCount = pcmBytes.Length / 2;
			var samples = new short[sampleCount];

			for (var i = 0; i < sampleCount; i++)
			{
				samples[i] = BitConverter.ToInt16(pcmBytes, i * 2);
			}

			using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
			var oggStream = new Concentus.Oggfile.OpusOggWriteStream(encoder, fileStream, tags, inputSampleRate: sampleRate);
			oggStream.WriteSamples(samples, 0, sampleCount);
			oggStream.Finish();
	}

	private static string Truncate(string value, int maxLength)
	{
			return value.Length <= maxLength ? value : value[..maxLength] + "...";
	}
}

/// <summary>
/// Minimal ID3v2.3 tag writer for MP3 metadata. No external dependencies.
/// </summary>
internal static class Id3Writer
{
	public static byte[] CreateTag(AudioMetadata metadata)
	{
			using var ms = new MemoryStream();
			using var writer = new BinaryWriter(ms);

			// Placeholder for ID3v2.3 header (10 bytes)
			var headerPos = ms.Position;
			writer.Write(new byte[10]);

			WriteTextFrame(writer, "TPE1", metadata.VoiceName);
			WriteTextFrame(writer, "TIT2", Truncate(metadata.SpokenText, 256));
			WriteTextFrame(writer, "TSSE", "Talktastic");

			var totalSize = (int)(ms.Position - headerPos - 10);

			// Go back and write the real header
			ms.Position = headerPos;
			writer.Write("ID3"u8);
			writer.Write((byte)3); // Version 2.3
			writer.Write((byte)0); // Revision
			writer.Write((byte)0); // Flags
			WriteSyncsafeInt(writer, totalSize);

			return ms.ToArray();
	}

	private static void WriteTextFrame(BinaryWriter writer, string frameId, string text)
	{
			var textBytes = System.Text.Encoding.UTF8.GetBytes(text);
			var frameSize = 1 + textBytes.Length; // 1 byte for encoding

			// Frame header: 4-char ID + 4-byte size (big-endian) + 2-byte flags
			writer.Write(System.Text.Encoding.ASCII.GetBytes(frameId));
			WriteBigEndianInt(writer, frameSize);
			writer.Write((short)0); // Flags
			writer.Write((byte)3); // UTF-8 encoding
			writer.Write(textBytes);
	}

	private static void WriteSyncsafeInt(BinaryWriter writer, int value)
	{
			writer.Write((byte)((value >> 21) & 0x7F));
			writer.Write((byte)((value >> 14) & 0x7F));
			writer.Write((byte)((value >> 7) & 0x7F));
			writer.Write((byte)(value & 0x7F));
	}

	private static void WriteBigEndianInt(BinaryWriter writer, int value)
	{
			writer.Write((byte)((value >> 24) & 0xFF));
			writer.Write((byte)((value >> 16) & 0xFF));
			writer.Write((byte)((value >> 8) & 0xFF));
			writer.Write((byte)(value & 0xFF));
	}

	private static string Truncate(string value, int maxLength)
	{
			return value.Length <= maxLength ? value : value[..maxLength] + "...";
	}
}
