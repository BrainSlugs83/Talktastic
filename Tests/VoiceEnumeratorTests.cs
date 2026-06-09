using System.Reflection;
using System.Runtime.InteropServices;

using Windows.Media.SpeechSynthesis;

namespace Talktastic.Tests;

[Collection("AppPaths")]
public sealed class VoiceEnumeratorTests : IDisposable
{
	private readonly string _artifactRoot;
	private readonly string[] _originalSearchBases;

	public VoiceEnumeratorTests()
	{
		_artifactRoot = Path.Combine(AppContext.BaseDirectory, nameof(VoiceEnumeratorTests), Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_artifactRoot);
		_originalSearchBases = [.. AppPaths.SearchBases];
	}

	public void Dispose()
	{
		Array.Copy(_originalSearchBases, AppPaths.SearchBases, _originalSearchBases.Length);

		if (Directory.Exists(_artifactRoot))
		{
			Directory.Delete(_artifactRoot, recursive: true);
		}
	}

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
	[InlineData("fr_FR-siwis-medium", "fr-FR")]
	[InlineData("en_GB-aru", "en-GB")]
	[InlineData("nodash", "")]
	public void ExtractPiperLocale_ExtractsCorrectly(string modelName, string expected)
	{
		var result = VoiceEnumerator.ExtractPiperLocale(modelName);

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
	[InlineData("Microsoft Ava (Natural HD) - English (United States)", "Ava")]
	[InlineData(null, null)]
	[InlineData("", null)]
	[InlineData("   ", null)]
	[InlineData("SomeThirdPartyVoice", "SomeThirdPartyVoice")]
	public void ExtractPersonName_ExtractsFirstWord(string? displayName, string? expected)
	{
		var result = VoiceEnumerator.ExtractPersonName(displayName);

		Assert.Equal(expected, result);
	}

	[Fact]
	public void FilterByType_NullFilter_ReturnsAll()
	{
		var voices = CreateTestVoices();

		var result = VoiceEnumerator.FilterByType(voices, null);

		Assert.Equal(voices.Length, result.Length);
		Assert.Equal(voices.Select(static voice => voice.Name), result.Select(static voice => voice.Name));
	}

	[Theory]
	[InlineData((int)VoiceType.Neural, "Neural Voice")]
	[InlineData((int)VoiceType.Legacy, "Legacy Voice")]
	[InlineData((int)VoiceType.Piper, "en_US-ryan-high")]
	public void FilterByType_TypeFilter_ReturnsOnlyMatchingType(int filter, string expectedName)
	{
		var voices = CreateTestVoices();

		var result = VoiceEnumerator.FilterByType(voices, (VoiceType?)filter);

		var voice = Assert.Single(result);
		Assert.Equal(expectedName, voice.Name);
	}

	[Theory]
	[InlineData("Microsoft Jenny(Natural) - English (US)", "Jenny")]
	[InlineData("Microsoft David", "David")]
	[InlineData("Some Voice - French", "Some Voice")]
	[InlineData("Plain Name", "Plain Name")]
	public void ExtractWindowsFriendlyName_ExtractsCorrectly(string fullName, string expected)
	{
		var result = InstalledVoice.ExtractWindowsFriendlyName(fullName);

		Assert.Equal(expected, result);
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

		var result = VoiceEnumerator.FindExactMatch([voice], query);

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

		var result = VoiceEnumerator.FindExactMatch([voice], "jen");

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

		var result = VoiceEnumerator.FindFuzzy(voices, query);

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

		var result = VoiceEnumerator.FindFuzzy(voices, "zzzzzz");

		Assert.Null(result);
	}

	[Fact]
	public void ResolveDefaultVoice_NoPreferredMatches_ReturnsFirstVoice()
	{
		if (!TryGetPreferredPersonNameForEnvironment(out _))
		{
			return;
		}

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

		var result = VoiceEnumerator.ResolveDefaultVoice(voices);

		Assert.Same(voices[0], result);
	}

	[Fact]
	public void ResolveDefaultVoice_PreferredPersonHasNeuralOption_ReturnsNeuralVoice()
	{
		if (!TryGetPreferredPersonNameForEnvironment(out var preferredPersonName))
		{
			return;
		}

		var neuralVoice = CreateVoice
		(
			VoiceType.Neural,
			name: $"Microsoft {preferredPersonName!} (Natural) - Test Locale",
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

		var result = VoiceEnumerator.ResolveDefaultVoice(voices);

		Assert.Same(neuralVoice, result);
	}

	[Theory]
	[InlineData(@"C:\path\", @"C:\path")]
	[InlineData(@"C:\path", @"C:\path")]
	public void NormalizePath_TrimsTrailingSeparators(string path, string expected)
	{
		var result = VoiceEnumerator.NormalizePath(path);

		Assert.Equal(expected, result);
	}

	[Fact]
	public void GetPiperVoices_WhenVoicesDirectoryMissing_ReturnsEmptyArray()
	{
		ConfigureSearchBases();

		var result = VoiceEnumerator.GetPiperVoices();

		Assert.Empty(result);
	}

	[Fact]
	public void GetPiperVoices_TransformsCachedVoicesIntoInstalledVoices()
	{
		var searchBases = ConfigureSearchBases();
		var voicesDir = Path.Combine(searchBases[0], ".piper-tts", "voices");
		Directory.CreateDirectory(voicesDir);
		File.WriteAllBytes(Path.Combine(voicesDir, "en_US-ryan-high.onnx"), [0x08]);
		File.WriteAllText(Path.Combine(voicesDir, "en_US-ryan-high.onnx.json"), "{}");
		File.WriteAllBytes(Path.Combine(voicesDir, "orphan.onnx"), [0x08]);

		var result = VoiceEnumerator.GetPiperVoices();

		var voice = Assert.Single(result);
		Assert.Equal("en_US-ryan-high", voice.Name);
		Assert.Equal("en_US-ryan-high", voice.ShortName);
		Assert.Equal("Piper Ryan (high) - en-US", voice.LocalName);
		Assert.Equal("en-US", voice.Locale);
		Assert.Equal(string.Empty, voice.Gender);
		Assert.Equal(Path.Combine(voicesDir, "en_US-ryan-high.onnx"), voice.VoicePath);
		Assert.Equal(VoiceType.Piper, voice.VoiceType);
		Assert.Equal("Ryan", voice.FriendlyName);
	}

	[Fact]
	public async Task GetVoicesAsync_CanceledToken_ThrowsOperationCanceledException()
	{
		using var cancellationTokenSource = new CancellationTokenSource();
		await cancellationTokenSource.CancelAsync();

		await Assert.ThrowsAsync<OperationCanceledException>
		(
			() => VoiceEnumerator.GetVoicesAsync(cancellationTokenSource.Token)
		);
	}

	[Theory]
	[InlineData("PIPER:Ryan", (int)VoiceType.Piper, "Ryan")]
	[InlineData("SAPI:", (int)VoiceType.Legacy, "")]
	[InlineData("WinRT:Ava", (int)VoiceType.Neural, "Ava")]
	public void ParseVoicePrefix_CaseInsensitivePrefix_ReturnsExpectedFilterAndQuery
	(
		string query,
		int expectedFilter,
		string expectedQuery
	)
	{
		var (filter, cleanQuery) = VoiceEnumerator.ParseVoicePrefix(query);

		Assert.Equal((VoiceType)expectedFilter, filter);
		Assert.Equal(expectedQuery, cleanQuery);
	}

	[Fact]
	public void ParseVoicePrefix_UnknownPrefixLikeQuery_ReturnsUnfilteredOriginalQuery()
	{
		var (filter, cleanQuery) = VoiceEnumerator.ParseVoicePrefix("custom:Jenny");

		Assert.Null(filter);
		Assert.Equal("custom:Jenny", cleanQuery);
	}

	[Fact]
	public void FilterByType_NoMatchingType_ReturnsEmptyArray()
	{
		InstalledVoice[] voices =
		[
			CreateVoice(VoiceType.Neural, name: "Neural Voice", shortName: "neural-short", localName: "Microsoft Neural"),
			CreateVoice(VoiceType.Legacy, name: "Legacy Voice", shortName: "legacy-short", localName: "Microsoft Legacy"),
		];

		var result = VoiceEnumerator.FilterByType(voices, VoiceType.Piper);

		Assert.Empty(result);
	}

	[Fact]
	public void FindContainsMatch_QueryMatchesSingleVoiceName_ReturnsVoice()
	{
		var expected = CreateVoice
		(
			VoiceType.Neural,
			name: "Contoso Avalanche",
			shortName: "avalanche-short",
			localName: "Contoso Avalanche"
		);
		InstalledVoice[] voices =
		[
			CreateVoice(VoiceType.Legacy, name: "Microsoft David", shortName: "david-short", localName: "Microsoft David"),
			expected,
		];

		var result = VoiceEnumerator.FindContainsMatch(voices, "aval");

		Assert.Same(expected, result);
	}

	[Fact]
	public void FindContainsMatch_QueryMatchesSingleShortName_ReturnsVoice()
	{
		var expected = CreateVoice
		(
			VoiceType.Legacy,
			name: "Microsoft David",
			shortName: "contoso-special-short",
			localName: "Microsoft David"
		);
		InstalledVoice[] voices =
		[
			CreateVoice(VoiceType.Neural, name: "Microsoft Jenny", shortName: "jenny-short", localName: "Microsoft Jenny"),
			expected,
		];

		var result = VoiceEnumerator.FindContainsMatch(voices, "SPECIAL");

		Assert.Same(expected, result);
	}

	[Fact]
	public void FindContainsMatch_QueryMatchesMultipleVoices_ReturnsNull()
	{
		InstalledVoice[] voices =
		[
			CreateVoice(VoiceType.Neural, name: "Microsoft Jenny", shortName: "jenny-short", localName: "Microsoft Jenny"),
			CreateVoice(VoiceType.Legacy, name: "Microsoft Jane", shortName: "jane-short", localName: "Microsoft Jane"),
		];

		var result = VoiceEnumerator.FindContainsMatch(voices, "Microsoft");

		Assert.Null(result);
	}

	[Fact]
	public void FindContainsMatch_QueryMatchesNoVoices_ReturnsNull()
	{
		var result = VoiceEnumerator.FindContainsMatch(CreateTestVoices(), "missing-voice-name");

		Assert.Null(result);
	}

	[Fact]
	public void FindContainsMatch_EmptyVoiceList_ReturnsNull()
	{
		var result = VoiceEnumerator.FindContainsMatch([], "anything");

		Assert.Null(result);
	}

	[Fact]
	public void FindFuzzy_EmptyVoiceList_ReturnsNull()
	{
		var result = VoiceEnumerator.FindFuzzy([], "Jenny");

		Assert.Null(result);
	}

	[Fact]
	public void FindFuzzy_QueryMatchesLocalNameFallback_ReturnsVoice()
	{
		var expected = CreateVoice
		(
			VoiceType.Neural,
			name: "Unexpected Name",
			shortName: "unexpected-short",
			localName: "Microsoft Zorblax"
		);

		var result = VoiceEnumerator.FindFuzzy([expected], "Microsoft");

		Assert.Same(expected, result);
	}

	[Fact]
	public void FindFuzzy_QueryMatchesNameFallback_ReturnsVoice()
	{
		var expected = CreateVoice
		(
			VoiceType.Legacy,
			name: "AzureGargantua",
			shortName: "unexpected-short",
			localName: "Completely Different"
		);

		var result = VoiceEnumerator.FindFuzzy([expected], "Azure");

		Assert.Same(expected, result);
	}

	[Fact]
	public void FindFuzzy_QueryMatchesShortNameFallback_ReturnsVoice()
	{
		var expected = CreateVoice
		(
			VoiceType.Legacy,
			name: "Completely Different",
			shortName: "astro-voice",
			localName: "Nothing Similar"
		);

		var result = VoiceEnumerator.FindFuzzy([expected], "astro");

		Assert.Same(expected, result);
	}

	[Theory]
	[InlineData("en_US-", "en_US-")]
	[InlineData("en_US--high", "-high")]
	public void ExtractPiperFriendlyName_UnusualModelName_ReturnsExpectedFriendlyName(string modelName, string expected)
	{
		var result = VoiceEnumerator.ExtractPiperFriendlyName(modelName);

		Assert.Equal(expected, result);
	}

	[Fact]
	public void ExtractPiperLocale_DashAtStart_ReturnsEmptyString()
	{
		var result = VoiceEnumerator.ExtractPiperLocale("-custom");

		Assert.Equal(string.Empty, result);
	}

	[Theory]
	[InlineData("microsoft Zira (Desktop)", "Zira")]
	[InlineData("Contoso Ava (Preview)", "Contoso")]
	public void ExtractPersonName_NameVariants_ReturnExpectedPersonName(string displayName, string expected)
	{
		var result = VoiceEnumerator.ExtractPersonName(displayName);

		Assert.Equal(expected, result);
	}

	[Theory]
	[InlineData("Microsoft Zira (Desktop)", "Zira")]
	[InlineData("(System Voice)", "(System Voice)")]
	[InlineData("microsoft Mark - English (US)", "Mark")]
	public void ExtractWindowsFriendlyName_BoundarySuffixes_ReturnExpectedFriendlyName(string fullName, string expected)
	{
		var result = InstalledVoice.ExtractWindowsFriendlyName(fullName);

		Assert.Equal(expected, result);
	}

	[Fact]
	public void FriendlyName_WindowsVoiceWithBlankLocalName_UsesName()
	{
		var voice = CreateVoice
		(
			VoiceType.Neural,
			name: "Microsoft Aria (Natural) - English (United States)",
			localName: " "
		);

		Assert.Equal("Aria", voice.FriendlyName);
	}

	[Fact]
	public void GetPiperVoices_CustomModelNameWithConfig_UsesFallbackDisplayAndEmptyLocale()
	{
		var searchBases = ConfigureSearchBases();
		var voicesDir = Path.Combine(searchBases[0], ".piper-tts", "voices");
		Directory.CreateDirectory(voicesDir);
		File.WriteAllBytes(Path.Combine(voicesDir, "custommodel.onnx"), [0x08]);
		File.WriteAllText(Path.Combine(voicesDir, "custommodel.onnx.json"), "{}");

		var result = VoiceEnumerator.GetPiperVoices();

		var voice = Assert.Single(result);
		Assert.Equal("custommodel", voice.Name);
		Assert.Equal("Piper (custommodel)", voice.LocalName);
		Assert.Equal(string.Empty, voice.Locale);
		Assert.Equal("custommodel", voice.FriendlyName);
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

	private static bool TryGetPreferredPersonNameForEnvironment(out string? personName)
	{
		try
		{
			var narratorVoiceName = InvokePrivateStatic<string?>(typeof(VoiceEnumerator), "GetNarratorVoiceName");
			var displayName = string.IsNullOrWhiteSpace(narratorVoiceName)
				? SpeechSynthesizer.DefaultVoice.DisplayName
				: narratorVoiceName;
			personName = VoiceEnumerator.ExtractPersonName(displayName);
			return !string.IsNullOrWhiteSpace(personName);
		}
		catch (COMException)
		{
			personName = null;
			return false;
		}
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

	private string[] ConfigureSearchBases()
	{
		var searchBases = new[]
		{
			Path.Combine(_artifactRoot, "base-0"),
			Path.Combine(_artifactRoot, "base-1"),
			Path.Combine(_artifactRoot, "base-2"),
		};

		Array.Copy(searchBases, AppPaths.SearchBases, searchBases.Length);
		return searchBases;
	}
}
