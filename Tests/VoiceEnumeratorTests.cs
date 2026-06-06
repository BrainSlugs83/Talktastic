using System.Reflection;

using Windows.Media.SpeechSynthesis;

namespace Talktastic.Tests;

public sealed class VoiceEnumeratorTests
{
	[Theory]
	[InlineData("piper:Aru", (int)VoiceType.Piper, "Aru")]
	[InlineData("sapi:Mark", (int)VoiceType.Legacy, "Mark")]
	[InlineData("legacy:David", (int)VoiceType.Legacy, "David")]
	[InlineData("neural:Jenny", (int)VoiceType.Neural, "Jenny")]
	[InlineData("winrt:Ava", (int)VoiceType.Neural, "Ava")]
	[InlineData("Jenny", null, "Jenny")]
	[InlineData(null, null, null)]
	[InlineData("", null, null)]
	[InlineData(" \t ", null, null)]
	public void ParseVoicePrefix_Query_ReturnsExpectedFilterAndQuery
	(
		string? query,
		int? expectedFilter,
		string? expectedQuery
	)
	{
		var (filter, cleanQuery) = VoiceEnumerator.ParseVoicePrefix(query);

		Assert.Equal(expectedFilter is null ? null : (VoiceType?)expectedFilter.Value, filter);
		Assert.Equal(expectedQuery, cleanQuery);
	}

	[Theory]
	[InlineData("en_GB-aru-medium", "Aru")]
	[InlineData("nodash", "nodash")]
	[InlineData("en_GB-aru", "Aru")]
	[InlineData("en_US-some-voice-name-high", "Some-voice-name")]
	public void ExtractPiperFriendlyName_ModelName_ReturnsExpectedFriendlyName(string modelName, string expected)
	{
		var result = VoiceEnumerator.ExtractPiperFriendlyName(modelName);

		Assert.Equal(expected, result);
	}

	[Theory]
	[InlineData("en_GB-aru-medium", "en-GB")]
	[InlineData("en_GB-aru", "en-GB")]
	[InlineData("nodash", "")]
	public void ExtractPiperLocale_ModelName_ReturnsExpectedLocale(string modelName, string expected)
	{
		var result = InvokePrivateStatic<string>(typeof(VoiceEnumerator), "ExtractPiperLocale", modelName);

		Assert.Equal(expected, result);
	}

	[Fact]
	public void FriendlyName_NeuralVoice_StripsWindowsMetadata()
	{
		var voice = CreateVoice
		(
			VoiceType.Neural,
			name: "ignored",
			localName: "Microsoft Jenny(Natural) - English (US)"
		);

		Assert.Equal("Jenny", voice.FriendlyName);
	}

	[Fact]
	public void FriendlyName_LegacyVoice_StripsMicrosoftPrefix()
	{
		var voice = CreateVoice
		(
			VoiceType.Legacy,
			name: "Microsoft David",
			localName: "Microsoft David"
		);

		Assert.Equal("David", voice.FriendlyName);
	}

	[Fact]
	public void FriendlyName_PiperVoice_UsesPiperFriendlyName()
	{
		var voice = CreateVoice
		(
			VoiceType.Piper,
			name: "en_GB-aru-medium",
			localName: "Aru"
		);

		Assert.Equal("Aru", voice.FriendlyName);
	}

	[Theory]
	[InlineData("Microsoft Jenny(Natural) - English (US)", "Jenny")]
	[InlineData("Microsoft Aria - English (US)", "Aria")]
	[InlineData("Microsoft David", "David")]
	[InlineData("Plain Name", "Plain Name")]
	public void FriendlyName_WindowsVoiceNameVariants_ReturnExpectedFriendlyName(string fullName, string expected)
	{
		var voice = CreateVoice(VoiceType.Neural, name: fullName, localName: fullName);

		Assert.Equal(expected, voice.FriendlyName);
	}

	[Theory]
	[InlineData("Microsoft Aria (Natural) - English (United States)", "Aria")]
	[InlineData("Microsoft David", "David")]
	[InlineData(null, null)]
	[InlineData("", null)]
	public void ExtractPersonName_DisplayName_ReturnsExpectedPersonName(string? displayName, string? expected)
	{
		var result = InvokePrivateStatic<string?>(typeof(VoiceEnumerator), "ExtractPersonName", displayName);

		Assert.Equal(expected, result);
	}

	[Fact]
	public void FilterByType_NullFilter_ReturnsAllVoices()
	{
		var voices = CreateTestVoices();

		var result = InvokePrivateStatic<InstalledVoice[]>(typeof(VoiceEnumerator), "FilterByType", voices, null);

		Assert.Equal(voices.Length, result.Length);
		Assert.Equal(voices.Select(static voice => voice.Name), result.Select(static voice => voice.Name));
	}

	[Theory]
	[InlineData((int)VoiceType.Neural, "Neural Voice")]
	[InlineData((int)VoiceType.Legacy, "Legacy Voice")]
	[InlineData((int)VoiceType.Piper, "en_US-ryan-high")]
	public void FilterByType_TypeFilter_ReturnsOnlyMatchingVoices(int filter, string expectedName)
	{
		var voices = CreateTestVoices();

		var result = InvokePrivateStatic<InstalledVoice[]>
		(
			typeof(VoiceEnumerator),
			"FilterByType",
			voices,
			(VoiceType?)filter
		);

		var voice = Assert.Single(result);
		Assert.Equal(expectedName, voice.Name);
	}

	[Theory]
	[InlineData("neural voice")]
	[InlineData("NEURAL-SHORT")]
	[InlineData("MICROSOFT JENNY (NATURAL) - ENGLISH (US)")]
	[InlineData("jenny")]
	public void FindExactMatch_QueryMatchesKnownField_ReturnsVoice(string query)
	{
		var voice = CreateVoice
		(
			VoiceType.Neural,
			name: "Neural Voice",
			shortName: "neural-short",
			localName: "Microsoft Jenny (Natural) - English (US)"
		);

		var result = InvokePrivateStatic<InstalledVoice?>(typeof(VoiceEnumerator), "FindExactMatch", new[] { voice }, query);

		Assert.Same(voice, result);
	}

	[Fact]
	public void FindExactMatch_NoExactFieldMatch_ReturnsNull()
	{
		var voice = CreateVoice
		(
			VoiceType.Neural,
			name: "Neural Voice",
			shortName: "neural-short",
			localName: "Microsoft Jenny (Natural) - English (US)"
		);

		var result = InvokePrivateStatic<InstalledVoice?>(typeof(VoiceEnumerator), "FindExactMatch", new[] { voice }, "jen");

		Assert.Null(result);
	}

	[Theory]
	[InlineData("jen", "Microsoft Jenny (Natural) - English (US)")]
	[InlineData("zorblax", "Voice of Zorblax Prime")]
	[InlineData("astro", "Completely Different")]
	public void FindFuzzy_PartialQuery_ReturnsBestMatch(string query, string expectedName)
	{
		InstalledVoice[] voices =
		[
			CreateVoice
			(
				VoiceType.Neural,
				name: "Microsoft Jenny (Natural) - English (US)",
				shortName: "jenny-short",
				localName: "Microsoft Jenny (Natural) - English (US)"
			),
			CreateVoice
			(
				VoiceType.Legacy,
				name: "Voice of Zorblax Prime",
				shortName: "legacy-short",
				localName: "Friendly Alias"
			),
			CreateVoice
			(
				VoiceType.Legacy,
				name: "Completely Different",
				shortName: "astro-voice",
				localName: "Nothing Similar"
			),
		];

		var result = InvokePrivateStatic<InstalledVoice?>(typeof(VoiceEnumerator), "FindFuzzy", voices, query);

		Assert.NotNull(result);
		Assert.Equal(expectedName, result.Name);
	}

	[Fact]
	public void FindFuzzy_NoReasonableMatch_ReturnsNull()
	{
		InstalledVoice[] voices =
		[
			CreateVoice(VoiceType.Neural, name: "Alpha", shortName: "alpha", localName: "Microsoft Alpha"),
			CreateVoice(VoiceType.Legacy, name: "Beta", shortName: "beta", localName: "Microsoft Beta"),
		];

		var result = InvokePrivateStatic<InstalledVoice?>(typeof(VoiceEnumerator), "FindFuzzy", voices, "zzzzzz");

		Assert.Null(result);
	}

	[Fact]
	public void ResolveDefaultVoice_NoPreferredMatches_ReturnsFirstVoice()
	{
		InstalledVoice[] voices =
		[
			CreateVoice
			(
				VoiceType.Neural,
				name: "Microsoft Zorgblat (Natural) - Fictional Locale",
				shortName: "zorgblat-neural",
				localName: "Microsoft Zorgblat (Natural) - Fictional Locale"
			),
			CreateVoice
			(
				VoiceType.Legacy,
				name: "Microsoft Quendor",
				shortName: "quendor-legacy",
				localName: "Microsoft Quendor"
			),
			CreateVoice
			(
				VoiceType.Piper,
				name: "zz_ZZ-blorb-medium",
				shortName: "zz_ZZ-blorb-medium",
				localName: "Blorb"
			),
		];

		var result = InvokePrivateStatic<InstalledVoice>
		(
			typeof(VoiceEnumerator),
			"ResolveDefaultVoice",
			new object?[] { voices }
		);

		Assert.Same(voices[0], result);
	}

	[Fact]
	public void ResolveDefaultVoice_PreferredPersonHasNeuralOption_ReturnsNeuralVoice()
	{
		var preferredPersonName = GetPreferredPersonNameForEnvironment();
		var neuralVoice = CreateVoice
		(
			VoiceType.Neural,
			name: $"Microsoft {preferredPersonName} (Natural) - Test Locale",
			shortName: $"{preferredPersonName}-neural",
			localName: $"Microsoft {preferredPersonName} (Natural) - Test Locale"
		);
		var legacyVoice = CreateVoice
		(
			VoiceType.Legacy,
			name: $"Microsoft {preferredPersonName}",
			shortName: $"{preferredPersonName}-legacy",
			localName: $"Microsoft {preferredPersonName}"
		);
		InstalledVoice[] voices =
		[
			CreateVoice(VoiceType.Piper, name: "en_US-random-medium", shortName: "en_US-random-medium", localName: "Random"),
			legacyVoice,
			neuralVoice,
		];

		var result = InvokePrivateStatic<InstalledVoice>
		(
			typeof(VoiceEnumerator),
			"ResolveDefaultVoice",
			new object?[] { voices }
		);

		Assert.Same(neuralVoice, result);
	}

	private static InstalledVoice[] CreateTestVoices()
	{
		return
		[
			CreateVoice(VoiceType.Neural, name: "Neural Voice", shortName: "neural-short", localName: "Microsoft Neural"),
			CreateVoice(VoiceType.Legacy, name: "Legacy Voice", shortName: "legacy-short", localName: "Microsoft Legacy"),
			CreateVoice(VoiceType.Piper, name: "en_US-ryan-high", shortName: "en_US-ryan-high", localName: "Ryan"),
		];
	}

	private static InstalledVoice CreateVoice
	(
		VoiceType voiceType,
		string name = "Test Voice",
		string shortName = "test-short",
		string localName = "Test Local",
		string locale = "en-US"
	)
	{
		return new InstalledVoice
		(
			Name: name,
			ShortName: shortName,
			LocalName: localName,
			Locale: locale,
			Gender: "Female",
			VoicePath: "voice-path",
			VoiceType: voiceType
		);
	}

	private static string GetPreferredPersonNameForEnvironment()
	{
		var narratorVoiceName = InvokePrivateStatic<string?>(typeof(VoiceEnumerator), "GetNarratorVoiceName");
		var displayName = string.IsNullOrWhiteSpace(narratorVoiceName)
			? SpeechSynthesizer.DefaultVoice.DisplayName
			: narratorVoiceName;
		var personName = InvokePrivateStatic<string?>(typeof(VoiceEnumerator), "ExtractPersonName", displayName);

		return personName ?? throw new InvalidOperationException("Could not determine a preferred voice person name.");
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

		return Assert.IsAssignableFrom<T>(result);
	}
}
