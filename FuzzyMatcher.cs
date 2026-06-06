using System.Fuzzy.Linq;

namespace Talktastic;

/// <summary>
/// Shared fuzzy name matching for voice and model lookups.
/// Uses BetterStringExtensions for ContainsIgnoreCase and Levenshtein distance.
/// </summary>
internal static class FuzzyMatcher
{
	/// <summary>
	/// Finds the best match for a query in a list of candidates.
	/// Returns <c>default(T)</c> if the query is null/empty or no match is found.
	/// Prefers a single ContainsIgnoreCase match; falls back to Levenshtein distance.
	/// </summary>
	public static T? FindBestMatch<T>(
		IEnumerable<T> candidates,
		string query,
		Func<T, string> converter
	) where T : class
	{
		if (string.IsNullOrWhiteSpace(query))
		{
			return default;
		}

		var items = candidates.ToArray();
		if (items.Length == 0)
		{
			return default;
		}

		var containsMatches = items
			.Where(c => converter(c).ContainsIgnoreCase(query))
			.ToArray();

		if (containsMatches.Length == 1)
		{
			return containsMatches[0];
		}

		// 0 or multiple contains matches -- use Levenshtein distance
		var pool = containsMatches.Length > 1 ? containsMatches : items;
		var best = pool
			.FuzzySearch(query, 0.5, converter)
			.FirstOrDefault();

		return best?.Value;
	}

	/// <summary>
	/// String-only convenience overload.
	/// </summary>
	public static string FindBestMatch(IEnumerable<string> candidates, string query)
	{
		return FindBestMatch(candidates, query, static s => s) ?? string.Empty;
	}
}
