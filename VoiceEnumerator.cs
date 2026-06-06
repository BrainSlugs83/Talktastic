using System.Diagnostics.CodeAnalysis;
using Microsoft.CognitiveServices.Speech;
using Windows.Management.Deployment;
using Windows.Media.SpeechSynthesis;

using SpeechSdk = Microsoft.CognitiveServices.Speech;

namespace Talktastic;

/// <summary>
/// Defines voice type values.
/// </summary>
internal enum VoiceType
{
	Neural,
	Piper,
	Legacy,
}

/// <summary>
/// Provides voice enumeration operations.
/// </summary>
internal static class VoiceEnumerator
{
	/// <summary>
	/// Recognized voice type prefixes for filtering (e.g. "piper:Aru", "sapi:Mark").
	/// </summary>
	private static readonly (string Prefix, VoiceType Type)[] TypePrefixes =
	[
		("piper:", VoiceType.Piper),
		("sapi:", VoiceType.Legacy),
		("legacy:", VoiceType.Legacy),
		("neural:", VoiceType.Neural),
		("winrt:", VoiceType.Neural),
	];

	/// <summary>
	/// Returns ALL installed voices: neural, legacy, and Piper.
	/// One list, used for both display and search.
	/// </summary>
	public static async Task<IReadOnlyList<InstalledVoice>> GetVoicesAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var voices = new List<InstalledVoice>();

		// Neural voices from Embedded Speech SDK
		voices.AddRange(await GetNeuralVoicesAsync(cancellationToken).ConfigureAwait(false));

		// Legacy SAPI/OneCore voices from WinRT
		voices.AddRange(GetLegacyVoices());

		// Piper TTS voices from local cache
		voices.AddRange(GetPiperVoices());

		return voices
			.OrderBy(static v => v.VoiceType) // Neural → Legacy → Piper
			.ThenBy(static v => v.Name, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	/// <summary>
	/// Resolves a voice query to an InstalledVoice. Supports:
	/// - null/empty → default voice (neural preferred)
	/// - Prefix filtering: "piper:Aru", "sapi:Mark", "neural:Jenny"
	/// - URL voices: downloads and caches Piper voices from URLs
	/// - Piper shorthand: "piper:en_US-ryan-high" downloads from HuggingFace
	/// - Plain text: fuzzy-matches across ALL voice types
	/// </summary>
	[ExcludeFromCodeCoverage]
	public static async Task<InstalledVoice> ResolveVoiceAsync(string? query, CancellationToken cancellationToken = default)
	{
		var (typeFilter, cleanQuery) = ParseVoicePrefix(query);

		// No query → default voice
		if (string.IsNullOrWhiteSpace(cleanQuery))
		{
			var voices = await GetVoicesAsync(cancellationToken).ConfigureAwait(false);
			var pool = FilterByType(voices, typeFilter);

			if (pool.Length == 0)
			{
				throw new InvalidOperationException("No voices found.");
			}

			return ResolveDefaultVoice(pool);
		}

		// URL → download/cache first, then resolve from the refreshed list
		if (PiperEngine.IsUrlVoice(cleanQuery))
		{
			await PiperEngine.EnsureVoiceModelAsync(query!, cancellationToken).ConfigureAwait(false);
			var voices = FilterByType(await GetVoicesAsync(cancellationToken).ConfigureAwait(false), typeFilter);
			var modelName = PiperEngine.GetModelName(query!);
			var match = FindExactMatch(voices, modelName) ?? FindFuzzy(voices, modelName);
			if (match is not null)
			{
				return match;
			}

			// Fallback: return the first Piper voice (just downloaded)
			return voices.FirstOrDefault(static v => v.VoiceType == VoiceType.Piper)
				?? throw new InvalidOperationException($"Downloaded voice from URL but could not find it in cache.");
		}

		// Search the unified voice list
		var allVoices = await GetVoicesAsync(cancellationToken).ConfigureAwait(false);
		var searchPool = FilterByType(allVoices, typeFilter);

		var exactMatch = FindExactMatch(searchPool, cleanQuery);
		if (exactMatch is not null)
		{
			return exactMatch;
		}

		var fuzzyMatch = FindFuzzy(searchPool, cleanQuery);
		if (fuzzyMatch is not null)
		{
			return fuzzyMatch;
		}

		// Not found in cache. If Piper-filtered (or no filter), try downloading.
		if (typeFilter is null or VoiceType.Piper)
		{
			var downloaded = await TryDownloadPiperVoiceAsync
			(
				typeFilter == VoiceType.Piper ? $"piper:{cleanQuery}" : cleanQuery,
				cancellationToken
			).ConfigureAwait(false);

			if (downloaded is not null)
			{
				return downloaded;
			}
		}

		throw new InvalidOperationException($"No voice matched '{query}'.");
	}

	/// <summary>
	/// Parses a "prefix:query" voice string into a type filter and clean query.
	/// </summary>
	internal static (VoiceType? Filter, string? Query) ParseVoicePrefix(string? query)
	{
		if (string.IsNullOrWhiteSpace(query))
		{
			return (null, null);
		}

		foreach (var (prefix, type) in TypePrefixes)
		{
			if (query.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				return (type, query[prefix.Length..]);
			}
		}

		return (null, query);
	}

	/// <summary>
	/// Filters the voices by type.
	/// </summary>
	/// <param name="voices">The voices.</param>
	/// <param name="typeFilter">The type filter.</param>
	/// <returns>The matching voices.</returns>
	internal static InstalledVoice[] FilterByType(IReadOnlyList<InstalledVoice> voices, VoiceType? typeFilter)
	{
		return typeFilter is null
			? [.. voices]
			: voices.Where(v => v.VoiceType == typeFilter).ToArray();
	}

	/// <summary>
	/// Attempts to download a Piper voice by shorthand (e.g. "piper:en_US-ryan-high" or "piper:Amy").
	/// Returns the InstalledVoice if successful, null if the query doesn't look like a Piper shorthand.
	/// </summary>
	[ExcludeFromCodeCoverage]
	private static async Task<InstalledVoice?> TryDownloadPiperVoiceAsync
	(
		string query,
		CancellationToken cancellationToken
	)
	{
		// Only attempt if it has the piper: prefix (explicit download intent)
		if (!query.StartsWith("piper:", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		try
		{
			await PiperEngine.EnsureVoiceModelAsync(query, cancellationToken).ConfigureAwait(false);

			// Re-enumerate and find the newly downloaded voice
			var piperVoices = GetPiperVoices();
			var cleanQuery = query["piper:".Length..];
			return FindExactMatch(piperVoices, cleanQuery)
				?? FindFuzzy(piperVoices, cleanQuery);
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException)
		{
			// Download failed -- fall through to "no voice matched"
			return null;
		}
	}

	/// <summary>
	/// Finds the exact match.
	/// </summary>
	/// <param name="voices">The voices.</param>
	/// <param name="query">The query.</param>
	/// <returns>The matching voice, or <c>null</c> if no match is found.</returns>
	internal static InstalledVoice? FindExactMatch(InstalledVoice[] voices, string query)
	{
		return voices.FirstOrDefault
		(
			v =>
				v.Name.EqualsIgnoreCase(query) ||
				v.ShortName.EqualsIgnoreCase(query) ||
				v.LocalName.EqualsIgnoreCase(query) ||
				v.FriendlyName.EqualsIgnoreCase(query)
		);
	}

	/// <summary>
	/// Finds the fuzzy match.
	/// </summary>
	/// <param name="voices">The voices.</param>
	/// <param name="query">The query.</param>
	/// <returns>The matching voice, or <c>null</c> if no match is found.</returns>
	internal static InstalledVoice? FindFuzzy(InstalledVoice[] voices, string query)
	{
		return FuzzyMatcher.FindBestMatch(voices, query, static v => v.FriendlyName)
			?? FuzzyMatcher.FindBestMatch(voices, query, static v => v.Name)
			?? FuzzyMatcher.FindBestMatch(voices, query, static v => v.ShortName);
	}

	/// <summary>
	/// Resolves the default voice.
	/// </summary>
	/// <param name="voices">The voices.</param>
	/// <returns>The resolved voice.</returns>
	internal static InstalledVoice ResolveDefaultVoice(InstalledVoice[] voices)
	{
		// Try the Narrator voice setting first
		var narratorVoiceName = GetNarratorVoiceName();
		if (!string.IsNullOrWhiteSpace(narratorVoiceName))
		{
			// Exact match against Narrator's display name
			var narratorMatch = voices.FirstOrDefault
			(
				v => string.Equals(v.Name, narratorVoiceName, StringComparison.OrdinalIgnoreCase)
			);

			if (narratorMatch is not null)
			{
				return narratorMatch;
			}

			// Extract the person name (e.g. "Aria" from "Microsoft Aria (Natural) - English (United States)")
			var personName = ExtractPersonName(narratorVoiceName);
			if (!string.IsNullOrWhiteSpace(personName))
			{
				// Prefer neural voice matching that person
				var neuralMatch = voices.FirstOrDefault
				(
					v => v.VoiceType == VoiceType.Neural && v.Name.ContainsIgnoreCase(personName)
				);

				if (neuralMatch is not null)
				{
					return neuralMatch;
				}

				// Fall back to any voice matching that person
				var anyMatch = voices.FirstOrDefault(v => v.Name.ContainsIgnoreCase(personName));
				if (anyMatch is not null)
				{
					return anyMatch;
				}
			}
		}

		// Fall back to WinRT system default
		var systemDefault = Windows.Media.SpeechSynthesis.SpeechSynthesizer.DefaultVoice;
		var defaultName = ExtractPersonName(systemDefault.DisplayName);

		if (!string.IsNullOrWhiteSpace(defaultName))
		{
			var neuralUpgrade = voices.FirstOrDefault
			(
				v => v.VoiceType == VoiceType.Neural && v.Name.ContainsIgnoreCase(defaultName)
			);

			if (neuralUpgrade is not null)
			{
				return neuralUpgrade;
			}

			var legacyMatch = voices.FirstOrDefault
			(
				v =>
					string.Equals(v.ShortName, systemDefault.Id, StringComparison.OrdinalIgnoreCase) ||
					string.Equals(v.Name, systemDefault.DisplayName, StringComparison.OrdinalIgnoreCase)
			);

			if (legacyMatch is not null)
			{
				return legacyMatch;
			}
		}

		// Last resort: first available (neural preferred by sort order)
		return voices[0];
	}

	/// <summary>
	/// Gets the Narrator voice name.
	/// </summary>
	/// <returns>The resulting string, or <c>null</c> if no value is available.</returns>
	[ExcludeFromCodeCoverage]
	private static string? GetNarratorVoiceName()
	{
		try
		{
			using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Narrator\NoRoam");
			return key?.GetValue("SpeechVoice") as string;
		}
		catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	/// Extracts the person name.
	/// </summary>
	/// <param name="displayName">The display name.</param>
	/// <returns>The resulting string, or <c>null</c> if no value is available.</returns>
	internal static string? ExtractPersonName(string? displayName)
	{
		if (string.IsNullOrWhiteSpace(displayName))
		{
			return null;
		}

		// "Microsoft Aria (Natural) - English (United States)" → "Aria"
		// "Microsoft David" → "David"
		// "Microsoft Ava (Natural HD) - English (United States)" → "Ava"
		var name = displayName;

		if (name.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase))
		{
			name = name["Microsoft ".Length..];
		}

		// Take first word (the person name) before any parenthetical or dash
		var spaceIndex = name.IndexOf(' ', StringComparison.Ordinal);
		if (spaceIndex > 0)
		{
			name = name[..spaceIndex];
		}

		return name.Trim();
	}

	/// <summary>
	/// Gets the neural voices.
	/// </summary>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	[ExcludeFromCodeCoverage]
	private static async Task<InstalledVoice[]> GetNeuralVoicesAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var voicePaths = GetInstalledVoicePackagePaths();
		if (voicePaths.Length == 0)
		{
			return [];
		}

		NativeExtractor.EnsureAvailable(DllGroup.SpeechSdk);

		var config = EmbeddedSpeechConfig.FromPaths([.. voicePaths]);
		using var synthesizer = new SpeechSdk.SpeechSynthesizer(config, audioConfig: null);
		var voicesResult = await synthesizer.GetVoicesAsync(string.Empty).ConfigureAwait(false);

		if (voicesResult.Reason != ResultReason.VoicesListRetrieved)
		{
			return [];
		}

		return voicesResult.Voices
			.Select
			(
				static voice => new InstalledVoice
				(
					Name: voice.Name,
					ShortName: voice.ShortName,
					LocalName: voice.LocalName,
					Locale: voice.Locale,
					Gender: voice.Gender.ToString(),
					VoicePath: NormalizePath(voice.VoicePath),
					VoiceType: VoiceType.Neural
				)
			)
			.ToArray();
	}

	/// <summary>
	/// Gets the legacy voices.
	/// </summary>
	/// <returns>The matching voices.</returns>
	private static InstalledVoice[] GetLegacyVoices()
	{
		return Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices
			.Select
			(
				static voice => new InstalledVoice
				(
					Name: voice.DisplayName,
					ShortName: voice.Id,
					LocalName: voice.DisplayName,
					Locale: voice.Language,
					Gender: voice.Gender.ToString(),
					VoicePath: voice.Id,
					VoiceType: VoiceType.Legacy
				)
			)
			.ToArray();
	}

	/// <summary>
	/// Enumerates cached Piper TTS voices as InstalledVoice records.
	/// </summary>
	internal static InstalledVoice[] GetPiperVoices()
	{
		var voicesDir = PiperEngine.FindVoicesDir();
		if (voicesDir is null)
		{
			return [];
		}

		return PiperEngine.EnumerateCachedVoices(voicesDir)
			.Select
			(
				static v =>
				{
					var displayName = PiperEngine.GetDisplayName(v.Name);
					var friendlyName = ExtractPiperFriendlyName(v.Name);
					var locale = ExtractPiperLocale(v.Name);

					return new InstalledVoice
					(
						Name: v.Name,
						ShortName: v.Name,
						LocalName: displayName,
						Locale: locale,
						Gender: "",
						VoicePath: v.OnnxPath,
						VoiceType: VoiceType.Piper
					);
				}
			)
			.ToArray();
	}

	/// <summary>
	/// Extracts the voice person name from a Piper model name.
	/// E.g. "en_GB-aru-medium" → "Aru", "en_US-ryan-high" → "Ryan".
	/// </summary>
	internal static string ExtractPiperFriendlyName(string modelName)
	{
		// Format: lang_COUNTRY-name-quality
		var firstDash = modelName.IndexOf('-', StringComparison.Ordinal);
		if (firstDash < 0)
		{
			return modelName;
		}

		var afterLocale = modelName[(firstDash + 1)..];
		var lastDash = afterLocale.LastIndexOf('-');

		var voiceName = lastDash > 0 ? afterLocale[..lastDash] : afterLocale;

		return voiceName.Length > 0
			? char.ToUpperInvariant(voiceName[0]) + voiceName[1..]
			: modelName;
	}

	/// <summary>
	/// Extracts locale from a Piper model name.
	/// E.g. "en_GB-aru-medium" → "en-GB".
	/// </summary>
	internal static string ExtractPiperLocale(string modelName)
	{
		var dashIdx = modelName.IndexOf('-', StringComparison.Ordinal);
		return dashIdx > 0
			? modelName[..dashIdx].Replace('_', '-')
			: "";
	}

	/// <summary>
	/// Gets the installed voice package paths.
	/// </summary>
	/// <returns>The matching paths.</returns>
	[ExcludeFromCodeCoverage]
	private static string[] GetInstalledVoicePackagePaths()
	{
		var packageManager = new PackageManager();

		return packageManager
			.FindPackagesForUser(string.Empty)
			.Where
			(
				static package =>
					package.Id.Name.StartsWith("MicrosoftWindows.Voice.", StringComparison.OrdinalIgnoreCase)
			)
			.Select(static package => NormalizePath(package.InstalledLocation.Path))
			.Where(static path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	/// <summary>
	/// Normalizes the path.
	/// </summary>
	/// <param name="path">The path.</param>
	/// <returns>The resulting string.</returns>
	internal static string NormalizePath(string path)
	{
		return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
	}
}

/// <summary>
/// Represents an installed voice.
/// </summary>
/// <param name="Name">The name.</param>
/// <param name="ShortName">The short name.</param>
/// <param name="LocalName">The local name.</param>
/// <param name="Locale">The locale.</param>
/// <param name="Gender">The gender.</param>
/// <param name="VoicePath">The voice path.</param>
/// <param name="VoiceType">The voice type.</param>
internal sealed record InstalledVoice
(
	string Name,
	string ShortName,
	string LocalName,
	string Locale,
	string Gender,
	string VoicePath,
	VoiceType VoiceType
)
{
	private const string MicrosoftPrefix = "Microsoft ";

	/// <summary>
	/// Short friendly name for display and fuzzy matching.
	/// Neural/Legacy: strips "Microsoft " prefix and parenthetical suffixes.
	/// Piper: extracts voice name from model name (e.g. "en_GB-aru-medium" → "Aru").
	/// </summary>
	public string FriendlyName { get; } = VoiceType == VoiceType.Piper
		? VoiceEnumerator.ExtractPiperFriendlyName(Name)
		: ExtractWindowsFriendlyName(string.IsNullOrWhiteSpace(LocalName) ? Name : LocalName);

	/// <summary>
	/// Extracts the Windows-friendly name.
	/// </summary>
	/// <param name="fullName">The full name.</param>
	/// <returns>The resulting string.</returns>
	internal static string ExtractWindowsFriendlyName(string fullName)
	{
		var name = fullName.StartsWith(MicrosoftPrefix, StringComparison.OrdinalIgnoreCase)
			? fullName[MicrosoftPrefix.Length..]
			: fullName;

		// Trim parenthetical/dash suffixes: "Jenny(Natural) - English (US)" → "Jenny"
		var parenIdx = name.IndexOf('(', StringComparison.Ordinal);
		var dashIdx = name.IndexOf(" - ", StringComparison.Ordinal);
		var cutAt = (parenIdx, dashIdx) switch
		{
			( >= 0, >= 0) => Math.Min(parenIdx, dashIdx),
			( >= 0, _) => parenIdx,
			(_, >= 0) => dashIdx,
			_ => -1,
		};

		return cutAt > 0 ? name[..cutAt].Trim() : name.Trim();
	}
}
