using System.Reflection;
using System.Globalization;
using System.Xml.Linq;

using Microsoft.CognitiveServices.Speech;

namespace Talktastic.Tests;

public sealed class SpeechEngineTests
{
	[Fact]
	public void SynthesisRequest_Constructor_PreservesAllProperties()
	{
		var request = new SynthesisRequest
		(
			Text: "Hello <world>",
			VoiceQuery: "jenny",
			OutputPath: "voice.wav",
			DeviceQuery: "speaker",
			Rate: "fast",
			Pitch: "+10%",
			RvcModel: "robot",
			RvcPitchShift: 1.25f,
			OutputFormat: SpeechSynthesisOutputFormat.Audio24Khz48KBitRateMonoMp3,
			TreatInputAsSsml: true
		);

		Assert.Equal("Hello <world>", request.Text);
		Assert.Equal("jenny", request.VoiceQuery);
		Assert.Equal("voice.wav", request.OutputPath);
		Assert.Equal("speaker", request.DeviceQuery);
		Assert.Equal("fast", request.Rate);
		Assert.Equal("+10%", request.Pitch);
		Assert.Equal("robot", request.RvcModel);
		Assert.Equal(1.25f, request.RvcPitchShift);
		Assert.Equal(SpeechSynthesisOutputFormat.Audio24Khz48KBitRateMonoMp3, request.OutputFormat);
		Assert.True(request.TreatInputAsSsml);
	}

	[Fact]
	public void SynthesisRequest_Equality_WithIdenticalValues_IsEqual()
	{
		var left = new SynthesisRequest
		(
			Text: "Hello",
			VoiceQuery: "jenny",
			OutputPath: "voice.wav",
			DeviceQuery: "speaker",
			Rate: "fast",
			Pitch: "+10%",
			RvcModel: "robot",
			RvcPitchShift: 1.25f,
			OutputFormat: SpeechSynthesisOutputFormat.Audio24Khz48KBitRateMonoMp3,
			TreatInputAsSsml: true
		);
		var right = new SynthesisRequest
		(
			Text: "Hello",
			VoiceQuery: "jenny",
			OutputPath: "voice.wav",
			DeviceQuery: "speaker",
			Rate: "fast",
			Pitch: "+10%",
			RvcModel: "robot",
			RvcPitchShift: 1.25f,
			OutputFormat: SpeechSynthesisOutputFormat.Audio24Khz48KBitRateMonoMp3,
			TreatInputAsSsml: true
		);

		Assert.Equal(left, right);
		Assert.Equal(left.GetHashCode(), right.GetHashCode());
	}

	[Theory]
	[InlineData("<speak>Hello</speak>", "Hello")]
	[InlineData("  <speak><prosody rate=\"fast\">Hi</prosody></speak>  ", "Hi")]
	[InlineData("<speak>Hello<break time=\"1s\"/>world</speak>", "Helloworld")]
	[InlineData("   <speak>   </speak>   ", "")]
	public void StripSsmlTags_RemovesTagsAndTrimsResult(string input, string expected)
	{
		var result = InvokePrivateStatic<string>(typeof(SpeechEngine), "StripSsmlTags", input);

		Assert.Equal(expected, result);
	}

	[Fact]
	public void StripSsmlTags_MultilineSsml_PreservesInnerText()
	{
		const string input =
			"""
			<speak>
				First line
				<prosody rate="fast">Second line</prosody>
			</speak>
			""";

		var result = InvokePrivateStatic<string>(typeof(SpeechEngine), "StripSsmlTags", input);

		Assert.Contains("First line", result, StringComparison.Ordinal);
		Assert.Contains("Second line", result, StringComparison.Ordinal);
		Assert.DoesNotContain("<prosody", result, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("voice.wav", ".wav", true)]
	[InlineData("VOICE.WAV", ".wav", true)]
	[InlineData("voice.mp3", ".ogg", false)]
	[InlineData("voice", ".wav", false)]
	[InlineData("voice.backup.ogg", ".ogg", true)]
	public void HasExtension_MatchesCaseInsensitiveExtension(string path, string extension, bool expected)
	{
		var result = InvokePrivateStatic<bool>(typeof(SpeechEngine), "HasExtension", path, extension);

		Assert.Equal(expected, result);
	}

	[Theory]
	[InlineData(null, "0%")]
	[InlineData("", "0%")]
	[InlineData("25", "+25%")]
	[InlineData("-10", "-10%")]
	[InlineData("1.5", "+2%")]
	[InlineData("fast", "fast")]
	[InlineData("+50%", "+50%")]
	public void NormalizeRate_FormatsExpectedValues(string? rate, string expected)
	{
		var result = InvokePrivateStatic<string>(typeof(SpeechEngine), "NormalizeRate", rate);

		Assert.Equal(expected, result);
	}

	[Theory]
	[InlineData("x-slow", 2.0)]
	[InlineData("slow", 1.5)]
	[InlineData("medium", 1.0)]
	[InlineData("fast", 0.7)]
	[InlineData("x-fast", 0.5)]
	[InlineData("default", 1.0)]
	[InlineData("0", 1.0)]
	[InlineData("50", 2.0 / 3.0)]
	[InlineData("+50%", 2.0 / 3.0)]
	[InlineData("-25%", 4.0 / 3.0)]
	[InlineData("-100%", 10.0)]
	[InlineData("-95%", 10.0)]
	public void RateToPiperLengthScale_MapsNamedAndNumericRates(string rate, double expected)
	{
		var result = InvokePrivateStaticNullableDouble(typeof(SpeechEngine), "RateToPiperLengthScale", rate);

		Assert.NotNull(result);
		AssertApproximatelyEqual(expected, result.Value);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("nonsense")]
	public void RateToPiperLengthScale_InvalidOrEmptyInput_ReturnsNull(string? rate)
	{
		var result = InvokePrivateStaticNullableDouble(typeof(SpeechEngine), "RateToPiperLengthScale", rate);

		Assert.Null(result);
	}

	[Theory]
	[InlineData(null, "0%")]
	[InlineData("", "0%")]
	[InlineData("12", "+12%")]
	[InlineData("-7", "-7%")]
	[InlineData("high", "high")]
	[InlineData("X-HIGH", "x-high")]
	[InlineData("default", "default")]
	[InlineData("1.5", "+2%")]
	[InlineData("1st", "1st")]
	[InlineData("220Hz", "220Hz")]
	[InlineData("+15%", "+15%")]
	public void NormalizePitch_FormatsExpectedValues(string? pitch, string expected)
	{
		var result = InvokePrivateStatic<string>(typeof(SpeechEngine), "NormalizePitch", pitch);

		Assert.Equal(expected, result);
	}

	[Theory]
	[InlineData("x-low", -0.30)]
	[InlineData("low", -0.15)]
	[InlineData("high", 0.15)]
	[InlineData("x-high", 0.30)]
	[InlineData("+20%", 0.20)]
	[InlineData("-10%", -0.10)]
	[InlineData("2ST", 0.12246204830937302)]
	[InlineData("-110Hz", -0.5)]
	[InlineData("1st", 0.0594630943592953)]
	[InlineData("110Hz", 0.5)]
	public void PitchToPiperShift_MapsNamedAndNumericInputs(string pitch, double expected)
	{
		var result = InvokePrivateStaticNullableDouble(typeof(SpeechEngine), "PitchToPiperShift", pitch);

		Assert.NotNull(result);
		AssertApproximatelyEqual(expected, result.Value);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("medium")]
	[InlineData("default")]
	[InlineData("0")]
	[InlineData("0%")]
	[InlineData("0.05%")]
	[InlineData("nonsense")]
	public void PitchToPiperShift_NoAdjustmentOrInvalidInput_ReturnsNull(string? pitch)
	{
		var result = InvokePrivateStaticNullableDouble(typeof(SpeechEngine), "PitchToPiperShift", pitch);

		Assert.Null(result);
	}

	[Fact]
	public void BuildProsodyAttributes_NoInputs_ReturnsEmptyString()
	{
		var attributes = InvokePrivateStatic<string>(typeof(SpeechEngine), "BuildProsodyAttributes", null, null);

		Assert.Equal(string.Empty, attributes);
	}

	[Fact]
	public void BuildProsodyAttributes_NormalizesRateAndPitch()
	{
		var attributes = InvokePrivateStatic<string>(typeof(SpeechEngine), "BuildProsodyAttributes", "25", "X-HIGH");

		Assert.Equal(" rate=\"+25%\" pitch=\"x-high\"", attributes);
	}

	[Fact]
	public void EnsureSsmlWrapped_PlainText_WrapsInSpeakElement()
	{
		var ssml = InvokePrivateStatic<string>(typeof(SpeechEngine), "EnsureSsmlWrapped", "Hello goodbye");

		var document = XDocument.Parse(ssml);
		Assert.Equal("speak", document.Root?.Name.LocalName);
		Assert.Equal("Hello goodbye", document.Root?.Value);
	}

	[Fact]
	public void EnsureSsmlWrapped_AlreadyWrapped_ReturnsOriginalInput()
	{
		const string input = "  <speak version=\"1.0\">Hello</speak>";

		var result = InvokePrivateStatic<string>(typeof(SpeechEngine), "EnsureSsmlWrapped", input);

		Assert.Equal(input, result);
	}

	[Fact]
	public void EnsureSsmlWrapped_UppercaseSpeakTag_ReturnsOriginalInput()
	{
		const string input = "\t<SPEAK version=\"1.0\">Hello</SPEAK>";

		var result = InvokePrivateStatic<string>(typeof(SpeechEngine), "EnsureSsmlWrapped", input);

		Assert.Equal(input, result);
	}

	[Fact]
	public void BuildSsml_EscapesTextAndIncludesVoiceMetadata()
	{
		var voice = CreateVoice(VoiceType.Neural, locale: "en-GB", name: "Test Voice");
		var ssml = InvokePrivateStatic<string>(typeof(SpeechEngine), "BuildSsml", "Fish & Chips <3", voice, "25", "high");
		var document = XDocument.Parse(ssml);
		var ns = document.Root!.Name.Namespace;
		var voiceElement = document.Root.Element(ns + "voice");
		var prosody = voiceElement?.Element(ns + "prosody");

		Assert.Equal("en-GB", (string?)document.Root.Attribute(XNamespace.Xml + "lang"));
		Assert.Equal("Test Voice", (string?)voiceElement?.Attribute("name"));
		Assert.Equal("+25%", (string?)prosody?.Attribute("rate"));
		Assert.Equal("high", (string?)prosody?.Attribute("pitch"));
		Assert.Equal("Fish & Chips <3", prosody?.Value);
	}

	[Fact]
	public void BuildLegacySsml_UsesEnUsSpeakElementWithoutVoiceNode()
	{
		var ssml = InvokePrivateStatic<string>(typeof(SpeechEngine), "BuildLegacySsml", "Rock & Roll", "-10", "X-HIGH");
		var document = XDocument.Parse(ssml);
		var ns = document.Root!.Name.Namespace;
		var prosody = document.Root.Element(ns + "prosody");

		Assert.Equal("en-US", (string?)document.Root.Attribute(XNamespace.Xml + "lang"));
		Assert.Null(document.Root.Element(ns + "voice"));
		Assert.Equal("-10%", (string?)prosody?.Attribute("rate"));
		Assert.Equal("x-high", (string?)prosody?.Attribute("pitch"));
		Assert.Equal("Rock & Roll", prosody?.Value);
	}

	[Fact]
	public void ApplyWavPitch_TooShortBuffer_DoesNothing()
	{
		var wav = new byte[12];
		var original = wav.ToArray();

		InvokePrivateStatic<object?>(typeof(SpeechEngine), "ApplyWavPitch", wav, 0.25);

		Assert.Equal(original, wav);
	}

	[Fact]
	public void ApplyWavPitch_RewritesSampleRateAndByteRate()
	{
		var wav = BuildWaveHeader(sampleRate: 22050, blockAlign: 2);

		InvokePrivateStatic<object?>(typeof(SpeechEngine), "ApplyWavPitch", wav, 0.10);

		Assert.Equal((uint)24255, BitConverter.ToUInt32(wav, 24));
		Assert.Equal((uint)48510, BitConverter.ToUInt32(wav, 28));
	}

	[Fact]
	public void ApplyWavPitch_ClampsToFiftyPercent()
	{
		var wav = BuildWaveHeader(sampleRate: 22050, blockAlign: 2);

		InvokePrivateStatic<object?>(typeof(SpeechEngine), "ApplyWavPitch", wav, 2.0);

		Assert.Equal((uint)33075, BitConverter.ToUInt32(wav, 24));
		Assert.Equal((uint)66150, BitConverter.ToUInt32(wav, 28));
	}

	[Theory]
	[InlineData((int)VoiceType.Neural, false, (int)SynthesisRoute.Neural)]
	[InlineData((int)VoiceType.Piper, false, (int)SynthesisRoute.Piper)]
	[InlineData((int)VoiceType.Legacy, false, (int)SynthesisRoute.Legacy)]
	[InlineData((int)VoiceType.Neural, true, (int)SynthesisRoute.Rvc)]
	[InlineData((int)VoiceType.Piper, true, (int)SynthesisRoute.Rvc)]
	[InlineData((int)VoiceType.Legacy, true, (int)SynthesisRoute.Rvc)]
	public void ResolveSynthesisRoute_ReturnsExpectedRoute(int voiceType, bool useRvc, int expected)
	{
		var voice = CreateVoice((VoiceType)voiceType);

		var route = SpeechEngine.ResolveSynthesisRoute(voice, useRvc);

		Assert.Equal((SynthesisRoute)expected, route);
	}

	private static InstalledVoice CreateVoice
	(
		VoiceType voiceType,
		string locale = "en-US",
		string name = "Test Voice"
	)
	{
		return new InstalledVoice
		(
			Name: name,
			ShortName: "test",
			LocalName: "Test Local",
			Locale: locale,
			Gender: "Female",
			VoicePath: "voice-path",
			VoiceType: voiceType
		);
	}

	private static byte[] BuildWaveHeader(uint sampleRate, ushort blockAlign)
	{
		var wav = new byte[44];
		BitConverter.TryWriteBytes(wav.AsSpan(24), sampleRate);
		BitConverter.TryWriteBytes(wav.AsSpan(28), sampleRate * blockAlign);
		BitConverter.TryWriteBytes(wav.AsSpan(32), blockAlign);
		return wav;
	}

	private static void AssertApproximatelyEqual(double expected, double actual, double tolerance = 1e-9)
	{
		Assert.True
		(
			Math.Abs(expected - actual) <= tolerance,
			$"Expected {expected}, actual {actual}."
		);
	}

	private static double? InvokePrivateStaticNullableDouble(Type type, string methodName, params object?[] args)
	{
		var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(method);

		var result = method!.Invoke(null, args);
		if (result is null)
		{
			return null;
		}

		return Assert.IsType<double>(result);
	}

	private static T InvokePrivateStatic<T>(Type type, string methodName, params object?[] args)
	{
		var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(method);

		var result = method!.Invoke(null, args);
		if (result is null)
		{
			return default!;
		}

		if (result is T typed)
		{
			return typed;
		}

		var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
		if (targetType.IsEnum)
		{
			return (T)Enum.ToObject(targetType, result);
		}

		return (T)Convert.ChangeType(result, targetType, CultureInfo.InvariantCulture);
	}
}
