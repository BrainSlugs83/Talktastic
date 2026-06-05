using Microsoft.CognitiveServices.Speech;
using Windows.Management.Deployment;
using Windows.Media.SpeechSynthesis;

using SpeechSdk = Microsoft.CognitiveServices.Speech;

namespace Talktastic;

internal enum VoiceType
{
	Neural,
	Legacy,
}

internal static class VoiceEnumerator
{
	public static async Task<IReadOnlyList<InstalledVoice>> GetVoicesAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var voices = new List<InstalledVoice>();

		// Neural voices from Embedded Speech SDK
		voices.AddRange(await GetNeuralVoicesAsync(cancellationToken).ConfigureAwait(false));

		// Legacy SAPI/OneCore voices from WinRT
		voices.AddRange(GetLegacyVoices());

		return voices
			.OrderBy(static v => v.VoiceType) // Neural first
			.ThenBy(static v => v.Name, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	public static async Task<InstalledVoice> ResolveVoiceAsync(string? query, CancellationToken cancellationToken = default)
	{
		var voices = await GetVoicesAsync(cancellationToken).ConfigureAwait(false);
		if (voices.Count == 0)
		{
			throw new InvalidOperationException("No voices found.");
		}

		if (string.IsNullOrWhiteSpace(query))
		{
			return ResolveDefaultVoice(voices);
		}

		// Exact match
		var exactMatch = voices.FirstOrDefault
		(
			v =>
				string.Equals(v.Name, query, StringComparison.OrdinalIgnoreCase) ||
				string.Equals(v.ShortName, query, StringComparison.OrdinalIgnoreCase) ||
				string.Equals(v.LocalName, query, StringComparison.OrdinalIgnoreCase)
		);

		if (exactMatch is not null)
		{
			return exactMatch;
		}

		// Substring match -- favor neural over legacy
		var partialMatches = voices
			.Where
			(
				v =>
					ContainsIgnoreCase(v.Name, query) ||
					ContainsIgnoreCase(v.ShortName, query) ||
					ContainsIgnoreCase(v.LocalName, query)
			)
			.OrderBy(static v => v.VoiceType) // Neural first
			.ThenBy(static v => v.Name, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		if (partialMatches.Length == 0)
		{
			throw new InvalidOperationException($"No voice matched '{query}'.");
		}

		// If there's at least one match, take the first (neural preferred)
		return partialMatches[0];
	}

	private static InstalledVoice ResolveDefaultVoice(IReadOnlyList<InstalledVoice> voices)
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
					v => v.VoiceType == VoiceType.Neural && ContainsIgnoreCase(v.Name, personName)
				);

				if (neuralMatch is not null)
				{
					return neuralMatch;
				}

				// Fall back to any voice matching that person
				var anyMatch = voices.FirstOrDefault(v => ContainsIgnoreCase(v.Name, personName));
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
				v => v.VoiceType == VoiceType.Neural && ContainsIgnoreCase(v.Name, defaultName)
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

	private static string? ExtractPersonName(string? displayName)
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

	private static async Task<InstalledVoice[]> GetNeuralVoicesAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var voicePaths = GetInstalledVoicePackagePaths();
		if (voicePaths.Length == 0)
		{
			return [];
		}

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

	private static bool ContainsIgnoreCase(string? source, string value)
	{
		return source?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false;
	}

	private static string NormalizePath(string path)
	{
		return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
	}
}

internal sealed record InstalledVoice
(
	string Name,
	string ShortName,
	string LocalName,
	string Locale,
	string Gender,
	string VoicePath,
	VoiceType VoiceType
);
