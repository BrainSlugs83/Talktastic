using System.Diagnostics.CodeAnalysis;
using System.Security;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace Talktastic;

/// <summary>
/// Provides speech synthesis operations.
/// </summary>
internal static partial class SpeechEngine
{
	/// <summary>
	/// Synthesizes speech from a request.
	/// </summary>
	/// <param name="request">The synthesis request.</param>
	/// <param name="voice">The voice.</param>
	/// <param name="rvc">The RVC model.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	[ExcludeFromCodeCoverage]
	public static async Task<string> SynthesizeAsync
	(
		SynthesisRequest request,
		InstalledVoice voice,
		(string Path, string DisplayName)? rvc,
		CancellationToken cancellationToken = default
	)
	{
		return ResolveSynthesisRoute(voice, rvc is not null) switch
		{
			SynthesisRoute.Rvc => await SynthesizeWithRvcAsync(request, voice, rvc!.Value, cancellationToken).ConfigureAwait(false),
			SynthesisRoute.Piper => await SynthesizePiperAsync(request, voice, cancellationToken).ConfigureAwait(false),
			SynthesisRoute.Legacy => await SynthesizeLegacyAsync(request, voice).ConfigureAwait(false),
			_ => await SynthesizeNeuralAsync(request, voice).ConfigureAwait(false),
		};
	}

	/// <summary>
	/// Resolves the synthesis route.
	/// </summary>
	/// <param name="voice">The voice.</param>
	/// <param name="useRvc">Whether to use RVC.</param>
	/// <returns>The resolved route.</returns>
	internal static SynthesisRoute ResolveSynthesisRoute(InstalledVoice voice, bool useRvc)
	{
		if (useRvc)
		{
			return SynthesisRoute.Rvc;
		}

		return voice.VoiceType switch
		{
			VoiceType.Piper => SynthesisRoute.Piper,
			VoiceType.Legacy => SynthesisRoute.Legacy,
			_ => SynthesisRoute.Neural,
		};
	}

	/// <summary>
	/// Synthesizes text with any TTS engine, then applies RVC voice conversion.
	/// All engines produce WAV bytes first, then RVC converts, then output to file/device.
	/// </summary>
	[ExcludeFromCodeCoverage]
	private static async Task<string> SynthesizeWithRvcAsync
	(
		SynthesisRequest request,
		InstalledVoice voice,
		(string Path, string DisplayName) rvc,
		CancellationToken cancellationToken
	)
	{
		var (rvcModelPath, rvcDisplayName) = rvc;
		var sourceVoiceName = voice.VoiceType == VoiceType.Piper ? voice.LocalName : voice.Name;

		// Start loading RVC sessions in the background NOW, before any TTS work runs. By the
		// time the source voice (SAPI / Neural / Piper) finishes synthesizing wav bytes, the
		// ~1.8s RVC model load has already happened in parallel.
		RvcEngine.Prewarm(rvcModelPath);

		byte[] wavBytes;
		if (voice.VoiceType == VoiceType.Piper)
		{
			var text = request.TreatInputAsSsml
				? StripSsmlTags(request.Text)
				: request.Text;

			var lengthScale = RateToPiperLengthScale(request.Rate);
			wavBytes = await PiperEngine.SynthesizeToWavAsync(text, voice.VoicePath, lengthScale, cancellationToken).ConfigureAwait(false);
		}
		else if (voice.VoiceType == VoiceType.Legacy)
		{
			wavBytes = await SynthesizeLegacyToWavAsync(request, voice).ConfigureAwait(false);
		}
		else
		{
			wavBytes = await SynthesizeNeuralToWavAsync(request, voice, cancellationToken).ConfigureAwait(false);
		}

		// Step 2: Apply RVC voice conversion
		var accel = Settings.Cli.NoGpu ? "CPU" : "DirectML";
		await Console.Error.WriteLineAsync
		(
			$"Applying RVC voice conversion with {accel} ({rvcDisplayName})..."
		).ConfigureAwait(false);

		var displayName = $"{sourceVoiceName} → {rvcDisplayName}";

		// Step 3: Output the converted audio.
		// When targeting the default device (no --output, no --device), stream each converted
		// segment to the default device as it is produced so playback starts before the whole
		// clip finishes converting. RVC still needs the full source clip up front (f0/feature
		// extraction span the whole signal), so only the converted output streams, not the source.
		if (request.OutputPath is null && string.IsNullOrWhiteSpace(request.DeviceQuery))
		{
			await StreamRvcToDefaultDeviceAsync(wavBytes, rvcModelPath, request.RvcPitchShift, cancellationToken).ConfigureAwait(false);
			return $"Spoke with {displayName}.";
		}

		wavBytes = await RvcEngine.ConvertAsync(wavBytes, rvcModelPath, request.RvcPitchShift, null, null, cancellationToken).ConfigureAwait(false);

		if (request.OutputPath is null)
		{
			await AudioOutput.PlayToDeviceAsync(wavBytes, request.DeviceQuery).ConfigureAwait(false);
			return $"Spoke with {displayName}.";
		}

		AudioOutput.EnsureDirectoryExists(request.OutputPath);
		var meta = new AudioMetadata(displayName, request.Text);

		if (HasExtension(request.OutputPath, ".wav"))
		{
			await File.WriteAllBytesAsync(request.OutputPath, wavBytes, cancellationToken).ConfigureAwait(false);
			return $"Wrote WAV file '{request.OutputPath}' with {displayName}.";
		}

		if (HasExtension(request.OutputPath, ".mp3"))
		{
			await AudioOutput.WriteMp3FromWavAsync(wavBytes, request.OutputPath, meta, cancellationToken).ConfigureAwait(false);
			return $"Wrote MP3 file '{request.OutputPath}' with {displayName}.";
		}

		if (HasExtension(request.OutputPath, ".ogg"))
		{
			await AudioOutput.WriteOggOpusFromWavAsync(wavBytes, request.OutputPath, meta, cancellationToken).ConfigureAwait(false);
			return $"Wrote OGG file '{request.OutputPath}' with {displayName}.";
		}

		throw new InvalidOperationException($"Unsupported output file extension for '{request.OutputPath}'. Use .wav, .mp3, or .ogg.");
	}

	/// <summary>
	/// Synthesizes a legacy (SAPI) voice to raw WAV bytes using direct SAPI COM.
	/// </summary>
	/// <remarks>
	/// Drives the classic SAPI5 <c>ISpVoice</c> engine via raw COM rather than the WinRT
	/// <c>Windows.Media.SpeechSynthesis</c> API. WinRT depends on Media Foundation, which is
	/// absent on Windows "N"/"KN" editions without the Media Feature Pack; classic SAPI does not.
	/// Raw COM (no <c>System.Speech</c>, no <c>[ComImport]</c>) keeps this NativeAOT-compatible.
	/// </remarks>
	/// <param name="request">The synthesis request.</param>
	/// <param name="voice">The voice.</param>
	/// <returns>A task that yields the synthesized audio bytes.</returns>
	[ExcludeFromCodeCoverage]
	private static Task<byte[]> SynthesizeLegacyStreamAsync(SynthesisRequest request, InstalledVoice voice)
	{
		var (text, isXml) = BuildLegacyInput(request);

		// SAPI COM is fully synchronous; run off the calling thread.
		return Task.Run(() => SapiEngine.Synthesize(voice.VoicePath, text, isXml));
	}

	/// <summary>
	/// Builds the text/SSML payload for a legacy (SAPI) voice, wrapping in SSML only when needed
	/// (explicit SSML input or a requested speaking rate).
	/// </summary>
	/// <param name="request">The synthesis request.</param>
	/// <returns>The payload text and whether it is XML/SSML.</returns>
	private static (string Text, bool IsXml) BuildLegacyInput(SynthesisRequest request)
	{
		var hasProsody = !string.IsNullOrWhiteSpace(request.Rate);
		var useSsml = request.TreatInputAsSsml || hasProsody;

		return useSsml
			? (request.TreatInputAsSsml
				? EnsureSsmlWrapped(request.Text)
				: BuildLegacySsml(request.Text, request.Rate, null), true)
			: (request.Text, false);
	}

	/// <summary>
	/// Synthesizes text using a legacy (SAPI) voice and returns raw WAV bytes.
	/// </summary>
	[ExcludeFromCodeCoverage]
	private static async Task<byte[]> SynthesizeLegacyToWavAsync(SynthesisRequest request, InstalledVoice voice)
	{
		return await SynthesizeLegacyStreamAsync(request, voice).ConfigureAwait(false);
	}

	/// <summary>
	/// Synthesizes text using a neural (embedded) voice and returns raw WAV bytes.
	/// </summary>
	[ExcludeFromCodeCoverage]
	private static async Task<byte[]> SynthesizeNeuralToWavAsync
	(
		SynthesisRequest request,
		InstalledVoice voice,
		CancellationToken cancellationToken
	)
	{
		var license = LicenseProvider.GetLicenseText();

		// Force WAV output format for the intermediate step
		var wavRequest = request with
		{
			OutputFormat = SpeechSynthesisOutputFormat.Riff24Khz16BitMonoPcm,
		};

		using var pullStream = AudioOutputStream.CreatePullStream();
		using var audioConfig = AudioConfig.FromStreamOutput(pullStream);
		var config = CreateConfig(wavRequest, voice, license);
		using var synthesizer = new SpeechSynthesizer(config, audioConfig);

		var result = await SpeakAsync(synthesizer, request, voice).ConfigureAwait(false);
		EnsureSuccess(result);

		return result.AudioData;
	}

	/// <summary>
	/// Synthesizes the Legacy.
	/// </summary>
	/// <param name="request">The synthesis request.</param>
	/// <param name="voice">The voice.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	[ExcludeFromCodeCoverage]
	private static async Task<string> SynthesizeLegacyAsync(SynthesisRequest request, InstalledVoice voice)
	{
		// Legacy voices ignore SSML pitch, so it is applied via a post-synthesis WAV header rewrite.
		var pitchShift = PitchToPiperShift(request.Pitch);

		// Default case (no file, default device, no pitch post-processing): speak straight to the
		// default audio device via ISpVoice, streaming the audio as SAPI generates it (no buffer).
		if (request.OutputPath is null
			&& string.IsNullOrWhiteSpace(request.DeviceQuery)
			&& pitchShift is null)
		{
			var (text, isXml) = BuildLegacyInput(request);
			await Task.Run(() => SapiEngine.Speak(voice.VoicePath, text, isXml)).ConfigureAwait(false);
			return $"Spoke with {voice.Name} (sapi).";
		}

		var audioBytes = await SynthesizeLegacyStreamAsync(request, voice).ConfigureAwait(false);

		if (pitchShift is not null)
		{
			ApplyWavPitch(audioBytes, pitchShift.Value);
		}

		var meta = new AudioMetadata(voice.Name, request.Text);

		if (request.OutputPath is null)
		{
			await AudioOutput.PlayToDeviceAsync(audioBytes, request.DeviceQuery).ConfigureAwait(false);
			return $"Spoke with {voice.Name} (sapi).";
		}

		if (HasExtension(request.OutputPath, ".wav"))
		{
			AudioOutput.EnsureDirectoryExists(request.OutputPath);
			await File.WriteAllBytesAsync(request.OutputPath, audioBytes).ConfigureAwait(false);
			return $"Wrote WAV file '{request.OutputPath}' with {voice.Name} (sapi).";
		}

		if (HasExtension(request.OutputPath, ".mp3"))
		{
			AudioOutput.EnsureDirectoryExists(request.OutputPath);
			await AudioOutput.WriteMp3Async(audioBytes, request.OutputFormat, request.OutputPath, meta).ConfigureAwait(false);
			return $"Wrote MP3 file '{request.OutputPath}' with {voice.Name} (sapi).";
		}

		if (HasExtension(request.OutputPath, ".ogg"))
		{
			AudioOutput.EnsureDirectoryExists(request.OutputPath);
			await AudioOutput.WriteOggOpusAsync(audioBytes, request.OutputFormat, request.OutputPath, meta).ConfigureAwait(false);
			return $"Wrote OGG file '{request.OutputPath}' with {voice.Name} (sapi).";
		}

		throw new InvalidOperationException($"Unsupported output file extension for '{request.OutputPath}'. Use .wav, .mp3, or .ogg.");
	}

	/// <summary>
	/// Synthesizes the Piper.
	/// </summary>
	/// <param name="request">The synthesis request.</param>
	/// <param name="voice">The voice.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	[ExcludeFromCodeCoverage]
	private static async Task<string> SynthesizePiperAsync
	(
		SynthesisRequest request,
		InstalledVoice voice,
		CancellationToken cancellationToken
	)
	{
		var modelPath = voice.VoicePath;
		var displayName = voice.LocalName;

		var text = request.TreatInputAsSsml
			? StripSsmlTags(request.Text)
			: request.Text;

		var lengthScale = RateToPiperLengthScale(request.Rate);
		var pitchShift = PitchToPiperShift(request.Pitch);

		// Pitch shifting changes playback speed, so compensate length_scale
		if (pitchShift is not null)
		{
			var compensation = 1.0 + pitchShift.Value;
			lengthScale = (lengthScale ?? 1.0) * compensation;
		}

		// Default case (no file, default device, no pitch post-processing): stream PCM straight to
		// the default device as Piper generates it, so playback starts before synthesis finishes.
		if (request.OutputPath is null
			&& string.IsNullOrWhiteSpace(request.DeviceQuery)
			&& pitchShift is null)
		{
			await StreamPiperToDefaultDeviceAsync(text, modelPath, lengthScale, cancellationToken).ConfigureAwait(false);
			return $"Spoke with {displayName}.";
		}

		var wavBytes = await PiperEngine.SynthesizeToWavAsync(text, modelPath, lengthScale, cancellationToken).ConfigureAwait(false);

		// Apply pitch by rewriting the WAV header sample rate
		if (pitchShift is not null)
		{
			ApplyWavPitch(wavBytes, pitchShift.Value);
		}

		// Piper outputs 22050 Hz 16-bit mono WAV -- use Raw22Khz for downstream format hints
		var piperFormat = SpeechSynthesisOutputFormat.Raw22050Hz16BitMonoPcm;
		var meta = new AudioMetadata(displayName, request.Text);

		if (request.OutputPath is null)
		{
			await AudioOutput.PlayToDeviceAsync(wavBytes, request.DeviceQuery).ConfigureAwait(false);
			return $"Spoke with {displayName}.";
		}

		AudioOutput.EnsureDirectoryExists(request.OutputPath);

		if (HasExtension(request.OutputPath, ".wav"))
		{
			await File.WriteAllBytesAsync(request.OutputPath, wavBytes, cancellationToken).ConfigureAwait(false);
			return $"Wrote WAV file '{request.OutputPath}' with {displayName}.";
		}

		if (HasExtension(request.OutputPath, ".mp3"))
		{
			await AudioOutput.WriteMp3Async(wavBytes, piperFormat, request.OutputPath, meta, cancellationToken).ConfigureAwait(false);
			return $"Wrote MP3 file '{request.OutputPath}' with {displayName}.";
		}

		if (HasExtension(request.OutputPath, ".ogg"))
		{
			await AudioOutput.WriteOggOpusAsync(wavBytes, piperFormat, request.OutputPath, meta, cancellationToken).ConfigureAwait(false);
			return $"Wrote OGG file '{request.OutputPath}' with {displayName}.";
		}

		throw new InvalidOperationException($"Unsupported output file extension for '{request.OutputPath}'. Use .wav, .mp3, or .ogg.");
	}

	/// <summary>
	/// Streams Piper-generated PCM to the default audio device as it is produced, using a FIFO
	/// playback sink so speech begins before synthesis completes.
	/// </summary>
	/// <param name="text">The plain text to speak.</param>
	/// <param name="modelPath">The Piper ONNX model path.</param>
	/// <param name="lengthScale">The optional length scale (speaking rate).</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	[ExcludeFromCodeCoverage]
	private static async Task StreamPiperToDefaultDeviceAsync
	(
		string text,
		string modelPath,
		double? lengthScale,
		CancellationToken cancellationToken
	)
	{
		WaveOut.StreamingPlayer? player = null;

		try
		{
			await PiperEngine.SynthesizeStreamingAsync
			(
				text,
				modelPath,
				lengthScale,
				sampleRate => player = new WaveOut.StreamingPlayer(sampleRate, 1, 16, null),
				samples => player!.Write(samples),
				cancellationToken
			).ConfigureAwait(false);
		}
		finally
		{
			player?.Dispose();
		}
	}

	/// <summary>
	/// Applies RVC voice conversion and streams each converted segment to the default device as
	/// it is produced, so playback starts before the whole clip finishes converting. RVC requires
	/// the full source clip up front, so only the converted output streams, not the source.
	/// </summary>
	[ExcludeFromCodeCoverage]
	private static async Task StreamRvcToDefaultDeviceAsync
	(
		byte[] sourceWavBytes,
		string rvcModelPath,
		float pitchShiftSemitones,
		CancellationToken cancellationToken
	)
	{
		WaveOut.StreamingPlayer? player = null;

		try
		{
			await RvcEngine.ConvertAsync
			(
				sourceWavBytes,
				rvcModelPath,
				pitchShiftSemitones,
				sampleRate => player = new WaveOut.StreamingPlayer(sampleRate, 1, 16, null),
				segment => player!.Write(segment),
				cancellationToken
			).ConfigureAwait(false);
		}
		finally
		{
			player?.Dispose();
		}
	}

	/// <summary>
	/// Synthesizes text using a neural (embedded) voice with direct output routing.
	/// </summary>
	[ExcludeFromCodeCoverage]
	private static async Task<string> SynthesizeNeuralAsync(SynthesisRequest request, InstalledVoice voice)
	{
		var license = LicenseProvider.GetLicenseText();

		return request.OutputPath switch
		{
			null => await SpeakToDeviceAsync(request, voice, license).ConfigureAwait(false),
			var path when HasExtension(path, ".wav") => await WriteWaveAsync(path, request, voice, license).ConfigureAwait(false),
			var path when HasExtension(path, ".mp3") => await WriteMp3Async(path, request, voice, license).ConfigureAwait(false),
			var path when HasExtension(path, ".ogg") => await WriteOggAsync(path, request, voice, license).ConfigureAwait(false),
			var path => throw new InvalidOperationException($"Unsupported output file extension for '{path}'. Use .wav, .mp3, or .ogg."),
		};
	}

	/// <summary>
	/// Strips SSML tags to plain text (Piper doesn't support SSML).
	/// </summary>
	private static string StripSsmlTags(string ssml)
	{
		return StripTagsRegex().Replace(ssml, "").Trim();
	}

	/// <summary>
	/// Creates the tag-stripping regex.
	/// </summary>
	/// <returns>The generated regex.</returns>
	[GeneratedRegex(@"<[^>]+>")]
	private static partial Regex StripTagsRegex();

	/// <summary>
	/// Speaks the To Device.
	/// </summary>
	/// <param name="request">The synthesis request.</param>
	/// <param name="voice">The voice.</param>
	/// <param name="license">The license text.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	[ExcludeFromCodeCoverage]
	private static async Task<string> SpeakToDeviceAsync
	(
		SynthesisRequest request,
		InstalledVoice voice,
		string license
	)
	{
		if (!string.IsNullOrWhiteSpace(request.DeviceQuery))
		{
			return await SpeakToSpecificDeviceAsync(request, voice, license).ConfigureAwait(false);
		}

		var config = CreateConfig(request, voice, license);
		using var audioConfig = AudioConfig.FromDefaultSpeakerOutput();
		using var synthesizer = new SpeechSynthesizer(config, audioConfig);

		Diagnostics.MarkFirstAudio("neural-default");
		var result = await SpeakAsync(synthesizer, request, voice).ConfigureAwait(false);
		EnsureSuccess(result);
		return $"Spoke {result.AudioData.Length} bytes with {voice.Name}.";
	}

	/// <summary>
	/// Speaks the To Specific Device.
	/// </summary>
	/// <param name="request">The synthesis request.</param>
	/// <param name="voice">The voice.</param>
	/// <param name="license">The license text.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	[ExcludeFromCodeCoverage]
	private static async Task<string> SpeakToSpecificDeviceAsync
	(
		SynthesisRequest request,
		InstalledVoice voice,
		string license
	)
	{
		using var stream = AudioOutputStream.CreatePullStream();
		using var audioConfig = AudioConfig.FromStreamOutput(stream);
		var config = CreateConfig(request, voice, license);
		using var synthesizer = new SpeechSynthesizer(config, audioConfig);

		var result = await SpeakAsync(synthesizer, request, voice).ConfigureAwait(false);
		EnsureSuccess(result);

		await AudioOutput.PlayToDeviceAsync(result.AudioData, request.DeviceQuery).ConfigureAwait(false);
		return $"Spoke {result.AudioData.Length} bytes with {voice.Name}.";
	}

	/// <summary>
	/// Writes the Wave.
	/// </summary>
	/// <param name="outputPath">The output path.</param>
	/// <param name="request">The synthesis request.</param>
	/// <param name="voice">The voice.</param>
	/// <param name="license">The license text.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	[ExcludeFromCodeCoverage]
	private static async Task<string> WriteWaveAsync
	(
		string outputPath,
		SynthesisRequest request,
		InstalledVoice voice,
		string license
	)
	{
		AudioOutput.EnsureDirectoryExists(outputPath);

		var config = CreateConfig(request, voice, license);
		using var audioConfig = AudioConfig.FromWavFileOutput(outputPath);
		using var synthesizer = new SpeechSynthesizer(config, audioConfig);

		var result = await SpeakAsync(synthesizer, request, voice).ConfigureAwait(false);
		EnsureSuccess(result);
		return $"Wrote WAV file '{outputPath}' with {voice.Name}.";
	}

	/// <summary>
	/// Writes MP3 audio.
	/// </summary>
	/// <param name="outputPath">The output path.</param>
	/// <param name="request">The synthesis request.</param>
	/// <param name="voice">The voice.</param>
	/// <param name="license">The license text.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	[ExcludeFromCodeCoverage]
	private static async Task<string> WriteMp3Async
	(
		string outputPath,
		SynthesisRequest request,
		InstalledVoice voice,
		string license
	)
	{
		using var stream = AudioOutputStream.CreatePullStream();
		using var audioConfig = AudioConfig.FromStreamOutput(stream);
		var config = CreateConfig(request, voice, license);
		using var synthesizer = new SpeechSynthesizer(config, audioConfig);

		var result = await SpeakAsync(synthesizer, request, voice).ConfigureAwait(false);
		EnsureSuccess(result);

		var meta = new AudioMetadata(voice.Name, request.Text);
		await AudioOutput.WriteMp3Async(result.AudioData, request.OutputFormat, outputPath, meta).ConfigureAwait(false);
		return $"Wrote MP3 file '{outputPath}' with {voice.Name}.";
	}

	/// <summary>
	/// Writes the OGG.
	/// </summary>
	/// <param name="outputPath">The output path.</param>
	/// <param name="request">The synthesis request.</param>
	/// <param name="voice">The voice.</param>
	/// <param name="license">The license text.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	[ExcludeFromCodeCoverage]
	private static async Task<string> WriteOggAsync
	(
		string outputPath,
		SynthesisRequest request,
		InstalledVoice voice,
		string license
	)
	{
		using var stream = AudioOutputStream.CreatePullStream();
		using var audioConfig = AudioConfig.FromStreamOutput(stream);
		var config = CreateConfig(request, voice, license);
		using var synthesizer = new SpeechSynthesizer(config, audioConfig);

		var result = await SpeakAsync(synthesizer, request, voice).ConfigureAwait(false);
		EnsureSuccess(result);

		var meta = new AudioMetadata(voice.Name, request.Text);
		await AudioOutput.WriteOggOpusAsync(result.AudioData, request.OutputFormat, outputPath, meta).ConfigureAwait(false);
		return $"Wrote OGG file '{outputPath}' with {voice.Name}.";
	}

	/// <summary>
	/// Creates the embedded speech configuration.
	/// </summary>
	/// <param name="request">The synthesis request.</param>
	/// <param name="voice">The voice.</param>
	/// <param name="license">The license text.</param>
	/// <returns>The embedded speech configuration.</returns>
	private static EmbeddedSpeechConfig CreateConfig
	(
		SynthesisRequest request,
		InstalledVoice voice,
		string license
	)
	{
		var config = EmbeddedSpeechConfig.FromPath(voice.VoicePath);
		config.SetSpeechSynthesisOutputFormat(request.OutputFormat);
		config.SetSpeechSynthesisVoice(voice.Name, license);
		return config;
	}

	/// <summary>
	/// Speaks the request.
	/// </summary>
	/// <param name="synthesizer">The synthesizer.</param>
	/// <param name="request">The synthesis request.</param>
	/// <param name="voice">The voice.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	[ExcludeFromCodeCoverage]
	private static async Task<SpeechSynthesisResult> SpeakAsync
	(
		SpeechSynthesizer synthesizer,
		SynthesisRequest request,
		InstalledVoice voice
	)
	{
		if (request.TreatInputAsSsml)
		{
			var ssml = EnsureSsmlWrapped(request.Text);
			return await synthesizer.SpeakSsmlAsync(ssml).ConfigureAwait(false);
		}

		var hasProsody = !string.IsNullOrWhiteSpace(request.Rate) || !string.IsNullOrWhiteSpace(request.Pitch);
		if (!hasProsody)
		{
			return await synthesizer.SpeakTextAsync(request.Text).ConfigureAwait(false);
		}

		var ssmlText = BuildSsml(request.Text, voice, request.Rate, request.Pitch);
		return await synthesizer.SpeakSsmlAsync(ssmlText).ConfigureAwait(false);
	}

	/// <summary>
	/// Ensures the synthesis result succeeded.
	/// </summary>
	/// <param name="result">The result map.</param>
	[ExcludeFromCodeCoverage]
	private static void EnsureSuccess(SpeechSynthesisResult result)
	{
		if (result.Reason == ResultReason.SynthesizingAudioCompleted)
		{
			return;
		}

		if (result.Reason == ResultReason.Canceled)
		{
			var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
			throw new InvalidOperationException
			(
				$"Speech synthesis canceled: {cancellation.ErrorCode}. {cancellation.ErrorDetails}".Trim()
			);
		}

		throw new InvalidOperationException($"Speech synthesis failed: {result.Reason}.");
	}

	/// <summary>
	/// Builds SSML.
	/// </summary>
	/// <param name="text">The text.</param>
	/// <param name="voice">The voice.</param>
	/// <param name="rate">The rate.</param>
	/// <param name="pitch">The pitch.</param>
	/// <returns>The resulting string.</returns>
	private static string BuildSsml(string text, InstalledVoice voice, string? rate, string? pitch)
	{
		var escapedText = SecurityElement.Escape(text) ?? string.Empty;
		var attrs = BuildProsodyAttributes(rate, pitch);

		return
			$"""
			<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="{voice.Locale}">
				<voice name="{voice.Name}">
					<prosody{attrs}>{escapedText}</prosody>
				</voice>
			</speak>
			""";
	}

	/// <summary>
	/// Builds SSML for legacy voices (no voice element needed -- voice is set on the synthesizer).
	/// </summary>
	private static string BuildLegacySsml(string text, string? rate, string? pitch)
	{
		var escapedText = SecurityElement.Escape(text) ?? string.Empty;
		var attrs = BuildProsodyAttributes(rate, pitch);

		return
			$"""
			<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="en-US">
				<prosody{attrs}>{escapedText}</prosody>
			</speak>
			""";
	}

	/// <summary>
	/// Builds the prosody attributes.
	/// </summary>
	/// <param name="rate">The rate.</param>
	/// <param name="pitch">The pitch.</param>
	/// <returns>The resulting string.</returns>
	private static string BuildProsodyAttributes(string? rate, string? pitch)
	{
		var attrs = "";
		if (!string.IsNullOrWhiteSpace(rate))
			attrs += $" rate=\"{NormalizeRate(rate)}\"";
		if (!string.IsNullOrWhiteSpace(pitch))
			attrs += $" pitch=\"{NormalizePitch(pitch)}\"";
		return attrs;
	}

	/// <summary>
	/// Normalizes the rate.
	/// </summary>
	/// <param name="rate">The rate.</param>
	/// <returns>The resulting string.</returns>
	private static string NormalizeRate(string? rate)
	{
		if (string.IsNullOrWhiteSpace(rate))
		{
			return "0%";
		}

		return double.TryParse(rate, out var numericRate)
			? $"{numericRate:+0;-0;0}%"
			: rate;
	}

	/// <summary>
	/// Converts a rate string (e.g. "fast", "+50%", "50", "slow") to piper's --length_scale.
	/// Piper: 1.0 = normal, lower = faster, higher = slower.
	/// </summary>
	private static double? RateToPiperLengthScale(string? rate)
	{
		if (string.IsNullOrWhiteSpace(rate))
			return null;

		// Named rates
		var scale = rate.ToUpperInvariant() switch
		{
			"X-SLOW" => 2.0,
			"SLOW" => 1.5,
			"MEDIUM" => 1.0,
			"DEFAULT" => 1.0,
			"FAST" => 0.7,
			"X-FAST" => 0.5,
			_ => (double?)null,
		};

		if (scale is not null)
			return scale;

		// Percentage: "+50%" means 50% faster → length_scale = 1 / 1.5 = 0.667
		var cleaned = rate.TrimEnd('%');
		if (double.TryParse(cleaned, out var pct))
		{
			// pct=50 means 50% faster, pct=-25 means 25% slower
			var factor = 1.0 + (pct / 100.0);
			if (factor <= 0.1) factor = 0.1; // clamp
			return 1.0 / factor;
		}

		return null;
	}

	/// <summary>
	/// Normalizes a pitch string for SSML prosody (neural + legacy voices).
	/// Named: x-low, low, medium, high, x-high. Numeric treated as percentage.
	/// </summary>
	private static string NormalizePitch(string? pitch)
	{
		if (string.IsNullOrWhiteSpace(pitch))
			return "0%";

		// Named values pass through directly
		var upper = pitch.ToUpperInvariant();
		if (upper is "X-LOW" or "LOW" or "MEDIUM" or "DEFAULT" or "HIGH" or "X-HIGH")
			return upper switch
			{
				"X-LOW" => "x-low",
				"X-HIGH" => "x-high",
				_ => pitch,
			};

		// Already has unit (%, st, Hz) -- pass through
		if (pitch.EndsWith('%') || pitch.EndsWith("st", StringComparison.OrdinalIgnoreCase)
			|| pitch.EndsWith("Hz", StringComparison.OrdinalIgnoreCase))
			return pitch;

		// Bare number → percentage
		return double.TryParse(pitch, out var numericPitch)
			? $"{numericPitch:+0;-0;0}%"
			: pitch;
	}

	/// <summary>
	/// Converts a pitch string to a fractional shift for Piper WAV header rewriting.
	/// Returns null if no pitch adjustment requested.
	/// E.g. "high" → +0.15, "+20%" → +0.20, "-10%" → -0.10
	/// </summary>
	private static double? PitchToPiperShift(string? pitch)
	{
		if (string.IsNullOrWhiteSpace(pitch))
			return null;

		// Named pitches → fractional shift
		var shift = pitch.ToUpperInvariant() switch
		{
			"X-LOW" => -0.30,
			"LOW" => -0.15,
			"MEDIUM" or "DEFAULT" => 0.0,
			"HIGH" => 0.15,
			"X-HIGH" => 0.30,
			_ => (double?)null,
		};

		if (shift is not null)
			return shift == 0.0 ? null : shift;

		// Strip semitone suffix (treat as approx percentage)
		var cleaned = pitch;
		if (cleaned.EndsWith("st", StringComparison.OrdinalIgnoreCase))
		{
			cleaned = cleaned[..^2];
			if (double.TryParse(cleaned, out var semitones))
			{
				// 1 semitone ≈ 5.946% frequency change
				return Math.Pow(2.0, semitones / 12.0) - 1.0;
			}
		}

		// Strip Hz suffix (treat as relative Hz; assume base 220 Hz for rough mapping)
		if (cleaned.EndsWith("Hz", StringComparison.OrdinalIgnoreCase))
		{
			cleaned = cleaned[..^2];
			if (double.TryParse(cleaned, out var hz))
				return hz / 220.0;
		}

		// Percentage
		cleaned = pitch.TrimEnd('%');
		if (double.TryParse(cleaned, out var pct))
		{
			var result = pct / 100.0;
			return Math.Abs(result) < 0.001 ? null : result;
		}

		return null;
	}

	/// <summary>
	/// Rewrites the WAV header sample rate (and byte rate) to shift pitch.
	/// pitchShift is fractional: 0.10 = 10% higher, -0.15 = 15% lower.
	/// Clamps to ±50% to avoid garbled audio.
	/// </summary>
	private static void ApplyWavPitch(byte[] wav, double pitchShift)
	{
		if (wav.Length < 44)
			return;

		var factor = 1.0 + Math.Clamp(pitchShift, -0.50, 0.50);
		var originalRate = BitConverter.ToUInt32(wav, 24);
		var blockAlign = BitConverter.ToUInt16(wav, 32);

		var newRate = (uint)(originalRate * factor);
		var newByteRate = newRate * blockAlign;

		BitConverter.TryWriteBytes(wav.AsSpan(24), newRate);
		BitConverter.TryWriteBytes(wav.AsSpan(28), newByteRate);
	}

	/// <summary>
	/// Determines whether the path has the specified extension.
	/// </summary>
	/// <param name="path">The path.</param>
	/// <param name="extension">The extension.</param>
	/// <returns><c>true</c> if the condition is met; otherwise, <c>false</c>.</returns>
	internal static bool HasExtension(string path, string extension)
	{
		return string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// If the input doesn't start with a &lt;speak&gt; element, wrap it in one.
	/// </summary>
	private static string EnsureSsmlWrapped(string ssml)
	{
		var trimmed = ssml.AsSpan().TrimStart();
		if (trimmed.StartsWith("<speak", StringComparison.OrdinalIgnoreCase))
			return ssml;

		return
			$"""
			<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="en-US">{ssml}</speak>
			""";
	}
}

/// <summary>
/// Represents a synthesis request.
/// </summary>
/// <param name="Text">The text.</param>
/// <param name="VoiceQuery">The voice query.</param>
/// <param name="OutputPath">The output path.</param>
/// <param name="DeviceQuery">The device query.</param>
/// <param name="Rate">The rate.</param>
/// <param name="Pitch">The pitch.</param>
/// <param name="RvcModel">The RVC model.</param>
/// <param name="RvcPitchShift">The RVC pitch shift.</param>
/// <param name="OutputFormat">The output format.</param>
/// <param name="TreatInputAsSsml">Whether to treat the input as SSML.</param>
internal sealed record SynthesisRequest
(
	string Text,
	string? VoiceQuery,
	string? OutputPath,
	string? DeviceQuery,
	string? Rate,
	string? Pitch,
	string? RvcModel,
	float RvcPitchShift,
	SpeechSynthesisOutputFormat OutputFormat,
	bool TreatInputAsSsml
);

/// <summary>
/// Defines synthesis route values.
/// </summary>
internal enum SynthesisRoute
{
	Neural,
	Piper,
	Legacy,
	Rvc,
}
