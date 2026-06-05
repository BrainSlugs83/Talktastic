using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

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
		CancellationToken cancellationToken = default
	)
	{
		var formatInfo = GetFormatInfo(outputFormat);
		EnsureDirectoryExists(outputPath);

		var pcmBytes = formatInfo.HasRiffHeader ? StripWaveHeader(audioBytes) : audioBytes;
		var mp3Bytes = LameEncoder.EncodePcmToMp3(pcmBytes, formatInfo.SampleRate, 1);

		await File.WriteAllBytesAsync(outputPath, mp3Bytes, cancellationToken).ConfigureAwait(false);
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
				$"MP3 output only supports 16-bit mono PCM synthesis formats. Got {outputFormat}."
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
