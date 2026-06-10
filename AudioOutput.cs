using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

using ConcentusApplication = Concentus.Enums.OpusApplication;
using OpusCodecFactory = Concentus.OpusCodecFactory;

namespace Talktastic;

/// <summary>
/// Provides audio output operations.
/// </summary>
internal static class AudioOutput
{
	/// <summary>
	/// Whether Media Foundation is available on this machine. It is absent on Windows "N"/"KN"
	/// editions without the Media Feature Pack, which breaks the WinRT media playback APIs.
	/// </summary>
	private static readonly bool MediaFoundationAvailable =
		File.Exists(Path.Combine(Environment.SystemDirectory, "mfplat.dll"));

	/// <summary>
	/// Enumerates the system audio render (output) devices.
	/// One source, used for both listing and selection.
	/// </summary>
	/// <returns>The render device information.</returns>
	[ExcludeFromCodeCoverage]
	private static DeviceInformation[] EnumerateRenderDevices()
	{
		var selector = MediaDevice.GetAudioRenderSelector();
		return DeviceInformation.FindAllAsync(selector).GetAwaiter().GetResult().ToArray();
	}

	/// <summary>
	/// Gets the available speakers.
	/// </summary>
	/// <returns>The available speakers.</returns>
	[ExcludeFromCodeCoverage]
	public static IReadOnlyList<AudioDeviceInfo> GetSpeakers()
	{
		return EnumerateRenderDevices()
			.Select
			(
				static device => new AudioDeviceInfo
				(
					Id: device.Id,
					FriendlyName: NormalizeDeviceName(device.Name)
				)
			)
			.OrderBy(static device => device.FriendlyName, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	/// <summary>
	/// Resolves a device query against a list of candidates by friendly name.
	/// </summary>
	/// <remarks>
	/// The single matching algorithm shared by every output backend (WinRT and winmm): an exact
	/// (whitespace-normalized, case-insensitive) name match wins; otherwise a unique substring
	/// match is used; zero or multiple substring matches are reported as errors. An optional
	/// secondary key (e.g. a device id) is matched verbatim. Mirrors the voice resolver's
	/// exact-then-substring semantics so device selection behaves consistently everywhere.
	/// </remarks>
	/// <typeparam name="T">The candidate device type.</typeparam>
	/// <param name="devices">The candidate devices.</param>
	/// <param name="nameSelector">Selects the friendly name to match and display.</param>
	/// <param name="keySelector">Selects an optional secondary key (matched verbatim), or <c>null</c>.</param>
	/// <param name="query">The device query.</param>
	/// <returns>The single matching device.</returns>
	internal static T ResolveDevice<T>
	(
		IReadOnlyList<T> devices,
		Func<T, string> nameSelector,
		Func<T, string?>? keySelector,
		string query
	)
	{
		var normalizedQuery = NormalizeDeviceName(query);

		bool MatchesKey(T device) =>
			keySelector?.Invoke(device) is { } key &&
			(key.EqualsIgnoreCase(query) || key.ContainsIgnoreCase(query));

		var exact = devices
			.Where(device => NormalizeDeviceName(nameSelector(device)).EqualsIgnoreCase(normalizedQuery)
				|| (keySelector?.Invoke(device)?.EqualsIgnoreCase(query) ?? false))
			.ToArray();

		if (exact.Length > 0)
		{
			return exact[0];
		}

		var partials = devices
			.Where(device => NormalizeDeviceName(nameSelector(device)).ContainsIgnoreCase(normalizedQuery)
				|| MatchesKey(device))
			.ToArray();

		return partials.Length switch
		{
			1 => partials[0],
			0 => throw new InvalidOperationException($"No speaker matched '{query}'."),
			_ => throw new InvalidOperationException
			(
				$"Speaker '{query}' is ambiguous. Matches: " +
				string.Join(", ", partials.Select(device => NormalizeDeviceName(nameSelector(device))))
			),
		};
	}

	/// <summary>
	/// Normalizes a device friendly name for display and matching.
	/// </summary>
	/// <remarks>
	/// Some drivers (notably AMD HDMI/DisplayPort audio) bake control characters and fixed-width
	/// EDID padding into the endpoint name stored in the registry, e.g. <c>"1 - H32T13       "</c>.
	/// This strips control characters and collapses every run of whitespace to a single space,
	/// then trims the ends, so names render cleanly and match consistently.
	/// </remarks>
	/// <param name="name">The raw device name.</param>
	/// <returns>The normalized device name.</returns>
	internal static string NormalizeDeviceName(string? name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return string.Empty;
		}

		var builder = new StringBuilder(name.Length);
		var pendingSpace = false;

		foreach (var ch in name)
		{
			if (char.IsWhiteSpace(ch) || char.IsControl(ch))
			{
				pendingSpace = true;
				continue;
			}

			if (pendingSpace && builder.Length > 0)
			{
				builder.Append(' ');
			}

			pendingSpace = false;
			builder.Append(ch);
		}

		return builder.ToString();
	}

	/// <summary>
	/// Plays WAV audio to a device.
	/// </summary>
	/// <param name="wavData">The WAV data.</param>
	/// <param name="deviceQuery">The device query.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	[ExcludeFromCodeCoverage]
	public static async Task PlayToDeviceAsync(byte[] wavData, string? deviceQuery)
	{
		// Windows N/KN editions without the Media Feature Pack lack Media Foundation, so the
		// WinRT MediaPlayer path below cannot load (ERROR_MOD_NOT_FOUND). Fall back to the
		// classic winmm waveOut API, which has no Media Foundation dependency. The WinRT path is
		// preferred everywhere else because it streams immediately without buffering the whole clip.
		if (!MediaFoundationAvailable)
		{
			await Task.Run(() => WaveOut.Play(wavData, deviceQuery)).ConfigureAwait(false);
			return;
		}

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
			var device = ResolveWinRTDevice(deviceQuery);
			player.AudioDevice = device;
		}

		player.Source = Windows.Media.Core.MediaSource.CreateFromStream(memStream, "audio/wav");

		var tcs = new TaskCompletionSource();
		player.MediaEnded += (_, _) => tcs.TrySetResult();
		player.MediaFailed += (_, e) => tcs.TrySetException(new InvalidOperationException(e.ErrorMessage));
		player.Play();
		await tcs.Task.ConfigureAwait(false);
	}

	/// <summary>
	/// Writes MP3 audio.
	/// </summary>
	/// <param name="audioBytes">The audio bytes.</param>
	/// <param name="outputFormat">The output format.</param>
	/// <param name="outputPath">The output path.</param>
	/// <param name="metadata">The metadata.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	[ExcludeFromCodeCoverage]
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

	/// <summary>
	/// Writes OGG Opus audio.
	/// </summary>
	/// <param name="audioBytes">The audio bytes.</param>
	/// <param name="outputFormat">The output format.</param>
	/// <param name="outputPath">The output path.</param>
	/// <param name="metadata">The metadata.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	[ExcludeFromCodeCoverage]
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

	/// <summary>
	/// Writes MP3 from raw WAV bytes, reading the sample rate from the RIFF header.
	/// Use this for RVC output where the sample rate may differ from the TTS format.
	/// </summary>
	[ExcludeFromCodeCoverage]
	public static async Task WriteMp3FromWavAsync
	(
		byte[] wavBytes,
		string outputPath,
		AudioMetadata? metadata = null,
		CancellationToken cancellationToken = default
	)
	{
		var sampleRate = AudioDsp.ReadWavSampleRate(wavBytes);
		EnsureDirectoryExists(outputPath);

		var pcmBytes = StripWaveHeader(wavBytes);
		var mp3Bytes = LameEncoder.EncodePcmToMp3(pcmBytes, sampleRate, 1);

		using var output = File.Create(outputPath);

		if (metadata is not null)
		{
			var id3 = Id3Writer.CreateTag(metadata);
			await output.WriteAsync(id3, cancellationToken).ConfigureAwait(false);
		}

		await output.WriteAsync(mp3Bytes, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Writes OGG Opus from raw WAV bytes, reading the sample rate from the RIFF header.
	/// </summary>
	[ExcludeFromCodeCoverage]
	public static async Task WriteOggOpusFromWavAsync
	(
		byte[] wavBytes,
		string outputPath,
		AudioMetadata? metadata = null,
		CancellationToken cancellationToken = default
	)
	{
		var sampleRate = AudioDsp.ReadWavSampleRate(wavBytes);
		EnsureDirectoryExists(outputPath);

		var pcmBytes = StripWaveHeader(wavBytes);

		await Task.Run
		(
			() => OggOpusEncoder.EncodeToFile
			(
				pcmBytes,
				sampleRate,
				1,
				outputPath,
				metadata
			),
			cancellationToken
		).ConfigureAwait(false);
	}

	/// <summary>
	/// Reads all bytes from the stream.
	/// </summary>
	/// <param name="stream">The stream.</param>
	/// <returns>The resulting bytes.</returns>
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

	/// <summary>
	/// Ensures the output directory exists.
	/// </summary>
	/// <param name="path">The path.</param>
	public static void EnsureDirectoryExists(string path)
	{
		var directory = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrWhiteSpace(directory))
		{
			Directory.CreateDirectory(directory);
		}
	}

	/// <summary>
	/// Resolves a WinRT audio device by friendly name or id.
	/// </summary>
	/// <param name="deviceQuery">The device query.</param>
	/// <returns>The matching device.</returns>
	[ExcludeFromCodeCoverage]
	private static DeviceInformation ResolveWinRTDevice(string deviceQuery)
	{
		return ResolveDevice(EnumerateRenderDevices(), static d => d.Name, static d => d.Id, deviceQuery);
	}

	/// <summary>
	/// Gets the PCM format information.
	/// </summary>
	/// <param name="outputFormat">The output format.</param>
	/// <returns>The PCM format information.</returns>
	internal static PcmFormatInfo GetFormatInfo(SpeechSynthesisOutputFormat outputFormat)
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

	/// <summary>
	/// Strips the WAV header.
	/// </summary>
	/// <param name="waveBytes">The WAV bytes.</param>
	/// <returns>The resulting bytes.</returns>
	internal static byte[] StripWaveHeader(byte[] waveBytes)
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

/// <summary>
/// AOT-safe audio playback via the classic winmm <c>waveOut</c> API, used as a fallback on
/// Windows N/KN editions that lack Media Foundation (and therefore the WinRT media APIs).
/// Plays 16-bit PCM RIFF/WAVE data and supports output-device selection by name. win-x64 only.
/// </summary>
#pragma warning disable CA5392 // winmm.dll is a known System32 library; default search path is fine.
internal static partial class WaveOut
{
	private const uint MmsyserrNoerror = 0;
	private const uint WaveMapper = 0xFFFFFFFF;
	private const uint CallbackNull = 0;
	private const int WhdrDone = 0x00000001;

	// WAVEHDR field offsets (x64). lpData(8) | dwBufferLength(4) | dwBytesRecorded(4) | dwUser(8) | dwFlags(4)...
	private const int WaveHdrSize = 48;
	private const int WaveHdrBufferLengthOffset = 8;
	private const int WaveHdrFlagsOffset = 24;

	// WAVEOUTCAPSW: wMid(2) wPid(2) vDriverVersion(4) szPname[32 wchar = 64] ...
	private const int WaveOutCapsSize = 84;
	private const int WaveOutCapsNameOffset = 8;

	/// <summary>
	/// Plays 16-bit PCM RIFF/WAVE data to the named device (or the default device when
	/// <paramref name="deviceQuery"/> is null/empty), blocking until playback completes.
	/// </summary>
	/// <param name="wavData">The RIFF/WAVE audio.</param>
	/// <param name="deviceQuery">An optional output-device name (exact or substring match).</param>
	[ExcludeFromCodeCoverage]
	public static void Play(byte[] wavData, string? deviceQuery)
	{
		var (sampleRate, channels, bitsPerSample, dataOffset, dataLength) = ParseWav(wavData);
		var deviceId = ResolveDeviceId(deviceQuery);

		var blockAlign = (ushort)(channels * (bitsPerSample / 8));
		var format = new WaveFormatEx
		{
			wFormatTag = 1, // WAVE_FORMAT_PCM
			nChannels = channels,
			nSamplesPerSec = (uint)sampleRate,
			nAvgBytesPerSec = (uint)(sampleRate * blockAlign),
			nBlockAlign = blockAlign,
			wBitsPerSample = bitsPerSample,
			cbSize = 0,
		};

		Check(waveOutOpen(out var hwo, deviceId, ref format, 0, 0, CallbackNull), nameof(waveOutOpen));

		var pcm = Marshal.AllocHGlobal(dataLength);
		var header = Marshal.AllocHGlobal(WaveHdrSize);
		try
		{
			Marshal.Copy(new byte[WaveHdrSize], 0, header, WaveHdrSize); // zero-fill the WAVEHDR
			Marshal.Copy(wavData, dataOffset, pcm, dataLength);
			Marshal.WriteIntPtr(header, 0, pcm);
			Marshal.WriteInt32(header, WaveHdrBufferLengthOffset, dataLength);

			Check(waveOutPrepareHeader(hwo, header, WaveHdrSize), nameof(waveOutPrepareHeader));
			Check(waveOutWrite(hwo, header, WaveHdrSize), nameof(waveOutWrite));

			while ((Marshal.ReadInt32(header, WaveHdrFlagsOffset) & WhdrDone) == 0)
			{
				Thread.Sleep(10);
			}

			_ = waveOutUnprepareHeader(hwo, header, WaveHdrSize);
		}
		finally
		{
			_ = waveOutClose(hwo);
			Marshal.FreeHGlobal(header);
			Marshal.FreeHGlobal(pcm);
		}
	}

	/// <summary>
	/// Resolves a device name to a winmm device id using the shared device matcher.
	/// </summary>
	[ExcludeFromCodeCoverage]
	private static uint ResolveDeviceId(string? deviceQuery)
	{
		if (string.IsNullOrWhiteSpace(deviceQuery))
		{
			return WaveMapper;
		}

		var devices = EnumerateDevices();
		return AudioOutput.ResolveDevice(devices, static d => d.Name, null, deviceQuery).Id;
	}

	/// <summary>
	/// Enumerates the winmm waveOut output devices as (id, name) pairs.
	/// </summary>
	[ExcludeFromCodeCoverage]
	private static List<(uint Id, string Name)> EnumerateDevices()
	{
		var count = waveOutGetNumDevs();
		var devices = new List<(uint Id, string Name)>();
		var caps = Marshal.AllocHGlobal(WaveOutCapsSize);
		try
		{
			for (uint i = 0; i < count; i++)
			{
				if (waveOutGetDevCaps(i, caps, WaveOutCapsSize) != MmsyserrNoerror)
				{
					continue;
				}

				var name = Marshal.PtrToStringUni(caps + WaveOutCapsNameOffset) ?? string.Empty;
				devices.Add((i, name));
			}
		}
		finally
		{
			Marshal.FreeHGlobal(caps);
		}

		return devices;
	}

	/// <summary>
	/// Extracts the PCM format and data range from a RIFF/WAVE buffer.
	/// </summary>
	[ExcludeFromCodeCoverage]
	private static (int SampleRate, ushort Channels, ushort BitsPerSample, int DataOffset, int DataLength) ParseWav(byte[] wav)
	{
		if (wav.Length < 12
			|| !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8)
			|| !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
		{
			throw new InvalidOperationException("Expected RIFF/WAVE data but got a different payload.");
		}

		int sampleRate = 0;
		ushort channels = 0;
		ushort bitsPerSample = 0;
		int dataOffset = -1;
		int dataLength = 0;

		var pos = 12;
		while (pos + 8 <= wav.Length)
		{
			var chunkId = wav.AsSpan(pos, 4);
			var chunkSize = BitConverter.ToInt32(wav, pos + 4);
			var body = pos + 8;

			if (chunkId.SequenceEqual("fmt "u8) && body + 16 <= wav.Length)
			{
				channels = BitConverter.ToUInt16(wav, body + 2);
				sampleRate = BitConverter.ToInt32(wav, body + 4);
				bitsPerSample = BitConverter.ToUInt16(wav, body + 14);
			}
			else if (chunkId.SequenceEqual("data"u8))
			{
				dataOffset = body;
				dataLength = Math.Min(chunkSize, wav.Length - body);
			}

			if (chunkSize < 0)
			{
				break;
			}

			pos = body + chunkSize + (chunkSize & 1); // chunks are word-aligned
		}

		if (dataOffset < 0 || dataLength <= 0 || sampleRate == 0 || channels == 0 || bitsPerSample == 0)
		{
			throw new InvalidOperationException("RIFF/WAVE payload was missing a valid fmt or data chunk.");
		}

		return (sampleRate, channels, bitsPerSample, dataOffset, dataLength);
	}

	[ExcludeFromCodeCoverage]
	private static void Check(uint result, string function)
	{
		if (result != MmsyserrNoerror)
		{
			throw new InvalidOperationException($"{function} failed with winmm error {result}.");
		}
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	private struct WaveFormatEx
	{
		public ushort wFormatTag;
		public ushort nChannels;
		public uint nSamplesPerSec;
		public uint nAvgBytesPerSec;
		public ushort nBlockAlign;
		public ushort wBitsPerSample;
		public ushort cbSize;
	}

	[LibraryImport("winmm.dll")]
	private static partial uint waveOutOpen(out nint phwo, uint uDeviceID, ref WaveFormatEx pwfx, nint dwCallback, nint dwInstance, uint fdwOpen);

	[LibraryImport("winmm.dll")]
	private static partial uint waveOutPrepareHeader(nint hwo, nint pwh, uint cbwh);

	[LibraryImport("winmm.dll")]
	private static partial uint waveOutWrite(nint hwo, nint pwh, uint cbwh);

	[LibraryImport("winmm.dll")]
	private static partial uint waveOutUnprepareHeader(nint hwo, nint pwh, uint cbwh);

	[LibraryImport("winmm.dll")]
	private static partial uint waveOutClose(nint hwo);

	[LibraryImport("winmm.dll")]
	private static partial uint waveOutGetNumDevs();

	[LibraryImport("winmm.dll", EntryPoint = "waveOutGetDevCapsW")]
	private static partial uint waveOutGetDevCaps(nuint uDeviceID, nint pwoc, uint cbwoc);

	/// <summary>
	/// Streams 16-bit PCM to an output device as audio is produced. A single producer pushes float
	/// samples via <see cref="Write"/> into a bounded FIFO; a dedicated reader thread drains the
	/// FIFO, converts to PCM, and feeds the device in ~<see cref="ChunkSeconds"/> buffers, keeping
	/// the device fed ahead of the drain. <see cref="Dispose"/> completes the FIFO, waits for
	/// playback to finish, and closes the device.
	/// </summary>
	[ExcludeFromCodeCoverage]
	internal sealed class StreamingPlayer : IDisposable
	{
		// Cap of queued-but-unfinished waveOut buffers, bounding driver-side latency.
		private const int MaxInFlight = 16;

		// Number of float buffers the producer may queue ahead of the reader before it blocks.
		private const int FifoCapacity = 8;

		// Target size of each waveOut buffer, in seconds of audio. Oversized single buffers can be
		// garbled or played at the wrong rate by some drivers, so the reader splits its output into
		// frame-aligned buffers of about this duration.
		private const double ChunkSeconds = 2.0;

		private readonly nint _hwo;
		private readonly int _chunkBytes;
		private readonly BlockingCollection<float[]> _fifo = new(FifoCapacity);
		private readonly Queue<(nint Header, nint Data)> _inFlight = new();
		private readonly Thread _reader;
		private Exception? _readerError;
		private bool _disposed;

		private static readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
		private static void D(string m)
		{
			if (Diagnostics.Verbose)
			{
				Console.Error.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[strm {_sw.ElapsedMilliseconds,7}ms] {m}"));
			}
		}

		/// <summary>
		/// Opens the named device (or the default device when <paramref name="deviceQuery"/> is
		/// null/empty) and starts the background reader that feeds it.
		/// </summary>
		public StreamingPlayer(int sampleRate, ushort channels, ushort bitsPerSample, string? deviceQuery)
		{
			var deviceId = ResolveDeviceId(deviceQuery);
			var blockAlign = (ushort)(channels * (bitsPerSample / 8));

			// Frame-aligned chunk of roughly ChunkSeconds of audio (blockAlign divides byteRate).
			var byteRate = sampleRate * blockAlign;
			_chunkBytes = Math.Max(blockAlign, (int)(byteRate * ChunkSeconds));

			var format = new WaveFormatEx
			{
				wFormatTag = 1, // WAVE_FORMAT_PCM
				nChannels = channels,
				nSamplesPerSec = (uint)sampleRate,
				nAvgBytesPerSec = (uint)byteRate,
				nBlockAlign = blockAlign,
				wBitsPerSample = bitsPerSample,
				cbSize = 0,
			};

			Check(waveOutOpen(out _hwo, deviceId, ref format, 0, 0, CallbackNull), nameof(waveOutOpen));

			_reader = new Thread(ReadLoop) { IsBackground = true, Name = "StreamingPlayer.Reader" };
			_reader.Start();
		}

		/// <summary>
		/// Pushes a block of float samples (-1.0..1.0) into the FIFO, blocking while the FIFO is
		/// full so the producer cannot outrun playback without bound. Takes ownership of the array.
		/// </summary>
		public void Write(float[] samples)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);

			if (samples is null || samples.Length == 0)
			{
				return;
			}

			try
			{
				_fifo.Add(samples);
			}
			catch (InvalidOperationException) when (_readerError is not null)
			{
				throw new InvalidOperationException("Streaming playback failed.", _readerError);
			}
		}

		/// <summary>
		/// Reader thread: drains the FIFO, converts each block to PCM, and submits it to the device
		/// in frame-aligned chunks; once the FIFO is complete, waits for all audio to finish.
		/// </summary>
		[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The reader thread must capture any synthesis or device fault and surface it to the producer thread.")]
		private void ReadLoop()
		{
			try
			{
				var blocks = 0;
				var chunks = 0;
				long bytes = 0;
				foreach (var samples in _fifo.GetConsumingEnumerable())
				{
					var pcm = AudioDsp.FloatToPcm16(samples);
					blocks++;
					bytes += pcm.Length;
					for (var offset = 0; offset < pcm.Length; offset += _chunkBytes)
					{
						var count = Math.Min(_chunkBytes, pcm.Length - offset);
						QueueBuffer(pcm, offset, count);
						chunks++;
					}
				}

				D($"fifo drained: blocks={blocks} chunks={chunks} bytes={bytes} chunkBytes={_chunkBytes}; waiting for {_inFlight.Count} in-flight");
				while (_inFlight.Count > 0)
				{
					WaitForOldest();
				}

				D("playback complete");
			}
			catch (Exception ex)
			{
				_readerError = ex;
				D($"reader FAULT: {ex.GetType().Name}: {ex.Message}");

				// Unblock any producer waiting on a full FIFO so it observes the failure.
				_fifo.CompleteAdding();
			}
		}

		/// <summary>
		/// Copies a single sub-buffer into unmanaged memory and submits it to the device, blocking
		/// only while the in-flight pool is full.
		/// </summary>
		private void QueueBuffer(byte[] pcm, int offset, int count)
		{
			ReclaimCompleted();

			while (_inFlight.Count >= MaxInFlight)
			{
				WaitForOldest();
			}

			var header = Marshal.AllocHGlobal(WaveHdrSize);
			var data = Marshal.AllocHGlobal(count);
			Marshal.Copy(new byte[WaveHdrSize], 0, header, WaveHdrSize); // zero-fill the WAVEHDR
			Marshal.Copy(pcm, offset, data, count);
			Marshal.WriteIntPtr(header, 0, data);
			Marshal.WriteInt32(header, WaveHdrBufferLengthOffset, count);

			Check(waveOutPrepareHeader(_hwo, header, WaveHdrSize), nameof(waveOutPrepareHeader));
			Check(waveOutWrite(_hwo, header, WaveHdrSize), nameof(waveOutWrite));
			_inFlight.Enqueue((header, data));
		}

		/// <summary>
		/// Signals end-of-stream, waits for the reader to drain and playback to finish, then closes
		/// the device. Rethrows any error the reader encountered.
		/// </summary>
		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;

			if (!_fifo.IsAddingCompleted)
			{
				_fifo.CompleteAdding();
			}

			_reader.Join();
			var closeResult = waveOutClose(_hwo);
			D($"disposed: waveOutClose={closeResult}");
			_fifo.Dispose();
			GC.SuppressFinalize(this);

			if (_readerError is not null)
			{
				throw new InvalidOperationException("Streaming playback failed.", _readerError);
			}
		}

		/// <summary>
		/// Frees any buffers at the front of the queue that have finished playing.
		/// </summary>
		private void ReclaimCompleted()
		{
			while (_inFlight.Count > 0
				&& (Marshal.ReadInt32(_inFlight.Peek().Header, WaveHdrFlagsOffset) & WhdrDone) != 0)
			{
				Free(_inFlight.Dequeue());
			}
		}

		/// <summary>
		/// Blocks until the oldest in-flight buffer finishes playing, then frees it.
		/// </summary>
		private void WaitForOldest()
		{
			var oldest = _inFlight.Peek();
			while ((Marshal.ReadInt32(oldest.Header, WaveHdrFlagsOffset) & WhdrDone) == 0)
			{
				Thread.Sleep(5);
			}

			Free(_inFlight.Dequeue());
		}

		/// <summary>
		/// Unprepares and frees a single buffer's WAVEHDR and PCM allocation.
		/// </summary>
		private void Free((nint Header, nint Data) buffer)
		{
			_ = waveOutUnprepareHeader(_hwo, buffer.Header, WaveHdrSize);
			Marshal.FreeHGlobal(buffer.Header);
			Marshal.FreeHGlobal(buffer.Data);
		}
	}
}
#pragma warning restore CA5392

/// <summary>
/// Represents an audio output device.
/// </summary>
/// <param name="Id">The device ID.</param>
/// <param name="FriendlyName">The friendly name.</param>
internal readonly record struct AudioDeviceInfo(string Id, string FriendlyName);

/// <summary>
/// Represents PCM format information.
/// </summary>
/// <param name="SampleRate">The sample rate.</param>
/// <param name="HasRiffHeader">Whether the format includes a RIFF header.</param>
internal readonly record struct PcmFormatInfo(int SampleRate, bool HasRiffHeader);

/// <summary>
/// Direct P/Invoke bindings to libmp3lame.dll for AOT-compatible MP3 encoding.
/// </summary>
#pragma warning disable CA5392 // DLL search path controlled by NativeExtractor.SetDllDirectory
internal static partial class LameEncoder
{
	private const int VBR_OFF = 0;

	/// <summary>
	/// Initializes the encoder handle.
	/// </summary>
	/// <returns>The encoder handle.</returns>
	[LibraryImport("libmp3lame", EntryPoint = "lame_init")]
	private static partial nint Init();

	/// <summary>
	/// Sets the In Samplerate.
	/// </summary>
	/// <param name="gfp">The encoder handle.</param>
	/// <param name="sampleRate">The sample rate.</param>
	/// <returns>The resulting integer value.</returns>
	[LibraryImport("libmp3lame", EntryPoint = "lame_set_in_samplerate")]
	private static partial int SetInSamplerate(nint gfp, int sampleRate);

	/// <summary>
	/// Sets the Num Channels.
	/// </summary>
	/// <param name="gfp">The encoder handle.</param>
	/// <param name="channels">The channel count.</param>
	/// <returns>The resulting integer value.</returns>
	[LibraryImport("libmp3lame", EntryPoint = "lame_set_num_channels")]
	private static partial int SetNumChannels(nint gfp, int channels);

	/// <summary>
	/// Sets the Brate.
	/// </summary>
	/// <param name="gfp">The encoder handle.</param>
	/// <param name="bitrate">The bitrate.</param>
	/// <returns>The resulting integer value.</returns>
	[LibraryImport("libmp3lame", EntryPoint = "lame_set_brate")]
	private static partial int SetBrate(nint gfp, int bitrate);

	/// <summary>
	/// Sets the VBR.
	/// </summary>
	/// <param name="gfp">The encoder handle.</param>
	/// <param name="vbrMode">The VBR mode.</param>
	/// <returns>The resulting integer value.</returns>
	[LibraryImport("libmp3lame", EntryPoint = "lame_set_VBR")]
	private static partial int SetVBR(nint gfp, int vbrMode);

	/// <summary>
	/// Sets the Quality.
	/// </summary>
	/// <param name="gfp">The encoder handle.</param>
	/// <param name="quality">The quality level.</param>
	/// <returns>The resulting integer value.</returns>
	[LibraryImport("libmp3lame", EntryPoint = "lame_set_quality")]
	private static partial int SetQuality(nint gfp, int quality);

	/// <summary>
	/// Initializes the Params.
	/// </summary>
	/// <param name="gfp">The encoder handle.</param>
	/// <returns>The resulting integer value.</returns>
	[LibraryImport("libmp3lame", EntryPoint = "lame_init_params")]
	private static partial int InitParams(nint gfp);

	/// <summary>
	/// Encodes the Buffer.
	/// </summary>
	/// <param name="gfp">The encoder handle.</param>
	/// <param name="bufferL">The buffer L.</param>
	/// <param name="bufferR">The buffer R.</param>
	/// <param name="nsamples">The n.</param>
	/// <param name="mp3buf">The MP3 buffer.</param>
	/// <param name="mp3bufSize">The MP3 buffer size.</param>
	/// <returns>The resulting integer value.</returns>
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

	/// <summary>
	/// Encodes the Flush.
	/// </summary>
	/// <param name="gfp">The encoder handle.</param>
	/// <param name="mp3buf">The MP3 buffer.</param>
	/// <param name="size">The buffer size.</param>
	/// <returns>The resulting integer value.</returns>
	[LibraryImport("libmp3lame", EntryPoint = "lame_encode_flush")]
	private static partial int EncodeFlush(nint gfp, nint mp3buf, int size);

	/// <summary>
	/// Closes the encoder handle.
	/// </summary>
	/// <param name="gfp">The encoder handle.</param>
	/// <returns>The resulting integer value.</returns>
	[LibraryImport("libmp3lame", EntryPoint = "lame_close")]
	private static partial int Close(nint gfp);

	/// <summary>
	/// Checks the Lame Result.
	/// </summary>
	/// <param name="result">The result map.</param>
	/// <param name="function">The function name.</param>
	[ExcludeFromCodeCoverage]
	private static void CheckLameResult(int result, string function)
	{
		if (result < 0)
		{
			throw new InvalidOperationException($"LAME {function} failed with error {result}.");
		}
	}

	/// <summary>
	/// Encodes PCM audio to MP3.
	/// </summary>
	/// <param name="pcmData">The PCM data.</param>
	/// <param name="sampleRate">The sample rate.</param>
	/// <param name="channels">The channel count.</param>
	/// <returns>The resulting bytes.</returns>
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

/// <summary>
/// Represents audio metadata.
/// </summary>
/// <param name="VoiceName">The voice name.</param>
/// <param name="SpokenText">The spoken text.</param>
internal sealed record AudioMetadata(string VoiceName, string SpokenText);

/// <summary>
/// Pure managed Opus encoder using Concentus. No native DLLs needed.
/// </summary>
internal static class OggOpusEncoder
{
	/// <summary>
	/// Encodes audio to a file.
	/// </summary>
	/// <param name="pcmBytes">The PCM bytes.</param>
	/// <param name="sampleRate">The sample rate.</param>
	/// <param name="channels">The channel count.</param>
	/// <param name="outputPath">The output path.</param>
	/// <param name="metadata">The metadata.</param>
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

	/// <summary>
	/// Truncates the value.
	/// </summary>
	/// <param name="value">The value.</param>
	/// <param name="maxLength">The maximum length.</param>
	/// <returns>The resulting string.</returns>
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
	/// <summary>
	/// Creates an ID3 tag.
	/// </summary>
	/// <param name="metadata">The metadata.</param>
	/// <returns>The resulting bytes.</returns>
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

	/// <summary>
	/// Writes a text frame.
	/// </summary>
	/// <param name="writer">The writer.</param>
	/// <param name="frameId">The frame ID.</param>
	/// <param name="text">The text.</param>
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

	/// <summary>
	/// Writes a sync-safe integer.
	/// </summary>
	/// <param name="writer">The writer.</param>
	/// <param name="value">The value.</param>
	private static void WriteSyncsafeInt(BinaryWriter writer, int value)
	{
			writer.Write((byte)((value >> 21) & 0x7F));
			writer.Write((byte)((value >> 14) & 0x7F));
			writer.Write((byte)((value >> 7) & 0x7F));
			writer.Write((byte)(value & 0x7F));
	}

	/// <summary>
	/// Writes a big-endian integer.
	/// </summary>
	/// <param name="writer">The writer.</param>
	/// <param name="value">The value.</param>
	private static void WriteBigEndianInt(BinaryWriter writer, int value)
	{
			writer.Write((byte)((value >> 24) & 0xFF));
			writer.Write((byte)((value >> 16) & 0xFF));
			writer.Write((byte)((value >> 8) & 0xFF));
			writer.Write((byte)(value & 0xFF));
	}

	/// <summary>
	/// Truncates the value.
	/// </summary>
	/// <param name="value">The value.</param>
	/// <param name="maxLength">The maximum length.</param>
	/// <returns>The resulting string.</returns>
	private static string Truncate(string value, int maxLength)
	{
			return value.Length <= maxLength ? value : value[..maxLength] + "...";
	}
}
