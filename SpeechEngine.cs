using System.Security;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace Talktastic;

internal static partial class SpeechEngine
{
	public static async Task<string> SynthesizeAsync(SynthesisRequest request, CancellationToken cancellationToken = default)
	{
		// Piper voices bypass the normal voice resolution
		if (PiperEngine.IsPiperVoice(request.VoiceQuery))
		{
			return await SynthesizePiperAsync(request, cancellationToken).ConfigureAwait(false);
		}

		var voice = await VoiceEnumerator.ResolveVoiceAsync(request.VoiceQuery, cancellationToken).ConfigureAwait(false);

		if (voice.VoiceType == VoiceType.Legacy)
		{
			return await SynthesizeLegacyAsync(request, voice).ConfigureAwait(false);
		}

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

	private static async Task<string> SynthesizeLegacyAsync(SynthesisRequest request, InstalledVoice voice)
	{
		using var synth = new Windows.Media.SpeechSynthesis.SpeechSynthesizer();

		var winrtVoice = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices
			.FirstOrDefault(v => string.Equals(v.Id, voice.VoicePath, StringComparison.OrdinalIgnoreCase));

		if (winrtVoice is not null)
		{
			synth.Voice = winrtVoice;
		}

		var text = request.TreatInputAsSsml ? EnsureSsmlWrapped(request.Text) : request.Text;
		var stream = request.TreatInputAsSsml
			? await synth.SynthesizeSsmlToStreamAsync(text)
			: await synth.SynthesizeTextToStreamAsync(text);

		var audioBytes = await ReadStreamAsync(stream).ConfigureAwait(false);

		var meta = new AudioMetadata(voice.Name, request.Text);

		if (request.OutputPath is null)
		{
			await AudioOutput.PlayToDeviceAsync(audioBytes, request.DeviceQuery).ConfigureAwait(false);
			return $"Spoke with {voice.Name} (legacy).";
		}

		if (HasExtension(request.OutputPath, ".wav"))
		{
			AudioOutput.EnsureDirectoryExists(request.OutputPath);
			await File.WriteAllBytesAsync(request.OutputPath, audioBytes).ConfigureAwait(false);
			return $"Wrote WAV file '{request.OutputPath}' with {voice.Name} (legacy).";
		}

		if (HasExtension(request.OutputPath, ".mp3"))
		{
			AudioOutput.EnsureDirectoryExists(request.OutputPath);
			await AudioOutput.WriteMp3Async(audioBytes, request.OutputFormat, request.OutputPath, meta).ConfigureAwait(false);
			return $"Wrote MP3 file '{request.OutputPath}' with {voice.Name} (legacy).";
		}

		if (HasExtension(request.OutputPath, ".ogg"))
		{
			AudioOutput.EnsureDirectoryExists(request.OutputPath);
			await AudioOutput.WriteOggOpusAsync(audioBytes, request.OutputFormat, request.OutputPath, meta).ConfigureAwait(false);
			return $"Wrote OGG file '{request.OutputPath}' with {voice.Name} (legacy).";
		}

		throw new InvalidOperationException($"Unsupported output file extension for '{request.OutputPath}'. Use .wav, .mp3, or .ogg.");
	}

	private static async Task<string> SynthesizePiperAsync(SynthesisRequest request, CancellationToken cancellationToken)
	{
		var voiceQuery = request.VoiceQuery!;
		var modelPath = await PiperEngine.EnsureVoiceModelAsync(voiceQuery, cancellationToken).ConfigureAwait(false);

		// Derive display name from the resolved model file (not the URL placeholder)
		var modelName = Path.GetFileNameWithoutExtension(modelPath);
		var displayName = PiperEngine.GetDisplayName(modelName);

		var text = request.TreatInputAsSsml
			? StripSsmlTags(request.Text)
			: request.Text;

		var wavBytes = await PiperEngine.SynthesizeToWavAsync(text, modelPath, cancellationToken).ConfigureAwait(false);

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
	/// Strips SSML tags to plain text (Piper doesn't support SSML).
	/// </summary>
	private static string StripSsmlTags(string ssml)
	{
		return StripTagsRegex().Replace(ssml, "").Trim();
	}

	[GeneratedRegex(@"<[^>]+>")]
	private static partial Regex StripTagsRegex();

	private static async Task<byte[]> ReadStreamAsync(Windows.Media.SpeechSynthesis.SpeechSynthesisStream stream)
	{
		using var ms = new MemoryStream();
		var inputStream = stream.AsStreamForRead();
		await inputStream.CopyToAsync(ms).ConfigureAwait(false);
		return ms.ToArray();
	}

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

		var result = await SpeakAsync(synthesizer, request, voice).ConfigureAwait(false);
		EnsureSuccess(result);
		return $"Spoke {result.AudioData.Length} bytes with {voice.Name}.";
	}

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

	private static async Task<SpeechSynthesisResult> SpeakAsync
	(
		SpeechSynthesizer synthesizer,
		SynthesisRequest request,
		InstalledVoice voice
	)
	{
		if (request.TreatInputAsSsml)
		{
			var ssml = ApplyRateToSsml(request.Text, request.Rate);
			return await synthesizer.SpeakSsmlAsync(ssml).ConfigureAwait(false);
		}

		if (string.IsNullOrWhiteSpace(request.Rate))
		{
			return await synthesizer.SpeakTextAsync(request.Text).ConfigureAwait(false);
		}

		var ssmlText = BuildSsml(request.Text, voice, request.Rate);
		return await synthesizer.SpeakSsmlAsync(ssmlText).ConfigureAwait(false);
	}

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

	private static string BuildSsml(string text, InstalledVoice voice, string? rate)
	{
		var escapedText = SecurityElement.Escape(text) ?? string.Empty;
		var normalizedRate = NormalizeRate(rate);

		return
			$"""
			<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="{voice.Locale}">
				<voice name="{voice.Name}">
					<prosody rate="{normalizedRate}">{escapedText}</prosody>
				</voice>
			</speak>
			""";
	}

	private static string ApplyRateToSsml(string ssml, string? rate)
	{
		ssml = EnsureSsmlWrapped(ssml);

		if (string.IsNullOrWhiteSpace(rate))
		{
			return ssml;
		}

		var document = XDocument.Parse(ssml, LoadOptions.PreserveWhitespace);
		var root = document.Root;
		if (root is null || !string.Equals(root.Name.LocalName, "speak", StringComparison.Ordinal))
		{
			throw new InvalidOperationException("SSML input must have a <speak> root element.");
		}

		var namespaceName = root.Name.Namespace;
		var nodes = root.Nodes().ToArray();
		root.ReplaceNodes(new XElement(namespaceName + "prosody", new XAttribute("rate", NormalizeRate(rate)), nodes));
		return document.ToString(SaveOptions.DisableFormatting);
	}

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

	private static bool HasExtension(string path, string extension)
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

internal sealed record SynthesisRequest
(
	string Text,
	string? VoiceQuery,
	string? OutputPath,
	string? DeviceQuery,
	string? Rate,
	SpeechSynthesisOutputFormat OutputFormat,
	bool TreatInputAsSsml
);
