using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.RegularExpressions;

namespace Talktastic;

/// <summary>
/// Resolves voice-models.com pages to their direct download links.
/// </summary>
[ExcludeFromCodeCoverage]
sealed partial class VoiceModelsComResolver : IUrlResolver
{
	/// <summary>
	/// Determines whether the URL points to voice-models.com.
	/// </summary>
	/// <param name="url">The URL to test.</param>
	/// <returns><see langword="true"/> when the resolver can handle the URL.</returns>
	public bool CanResolve(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
		{
			return false;
		}

		var host = uri.Host;
		return string.Equals(host, "voice-models.com", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(host, "www.voice-models.com", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Resolves a voice-models.com page to its direct download URL.
	/// </summary>
	/// <param name="http">The HTTP client to use.</param>
	/// <param name="url">The URL to resolve.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>The resolved URL result.</returns>
	public async Task<UrlResolverResult> ResolveAsync
	(
		HttpClient http,
		string url,
		CancellationToken cancellationToken
	)
	{
		using var response = await http.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);

		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException
			(
				$"Failed to fetch voice-models.com page: HTTP {(int)response.StatusCode} from {url}"
			);
		}

		var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		var extractedHref = ExtractVoiceModelsDownloadLink(html);
		if (!ModelDownloader.IsUrl(extractedHref))
		{
			throw new InvalidOperationException
			(
				$"voice-models.com Download Link is not an HTTP(S) URL: {extractedHref}"
			);
		}

		Diagnostics.LogPerf($"[resolve] voice-models.com -> {extractedHref}");

		var shortName = ExtractVoiceModelsShortNameFromHtml(html);
		return new UrlResolverResult
		(
			extractedHref,
			shortName,
			IsPreferredName: shortName is not null
		);
	}

	private static string ExtractVoiceModelsDownloadLink(string html)
	{
		var match = VoiceModelsDownloadLinkPattern().Match(html);
		if (!match.Success)
		{
			throw new InvalidOperationException("voice-models.com page has no Download Link.");
		}

		var rawHref = match.Groups[1].Success
			? match.Groups[1].Value
			: match.Groups[2].Success
				? match.Groups[2].Value
				: match.Groups[3].Value;

		return (WebUtility.HtmlDecode(rawHref) ?? rawHref).Trim();
	}

	private static string ExtractVoiceModelsShortName(string h3Text)
	{
		var decoded = WebUtility.HtmlDecode(h3Text) ?? h3Text;
		var title = StripHtmlTagsPattern().Replace(decoded, string.Empty).Trim();
		if (title.Length == 0)
		{
			return title;
		}

		var sanitized = ModelDownloader.SanitizeFileName(title);
		return ModelDownloader.CleanModelName(sanitized) ?? sanitized;
	}

	private static string ChooseVoiceModelsName(string innerResolvedName, string? pageShortName)
	{
		if (pageShortName is not null && ModelDownloader.IsUsableName(pageShortName))
		{
			return pageShortName;
		}

		return innerResolvedName;
	}

	private static string? ExtractVoiceModelsShortNameFromHtml(string html)
	{
		var titleMatch = HtmlTitlePattern().Match(html);
		if (!titleMatch.Success)
		{
			return null;
		}

		var (stripped, hadTrailer) = StripVoiceModelsTitleTrailer(titleMatch.Groups[1].Value);
		if (!hadTrailer)
		{
			return null;
		}

		var shortName = ExtractVoiceModelsShortName(stripped);
		return !string.IsNullOrWhiteSpace(shortName) && ModelDownloader.IsUsableName(shortName)
			? shortName
			: null;
	}

	private static (string Stripped, bool HadTrailer) StripVoiceModelsTitleTrailer(string title)
	{
		var decoded = (WebUtility.HtmlDecode(title) ?? title).Trim();
		const string Trailer = " AI Voice Model";
		if (decoded.EndsWith(Trailer, StringComparison.OrdinalIgnoreCase))
		{
			return (decoded[..^Trailer.Length].TrimEnd(), HadTrailer: true);
		}

		return (decoded, HadTrailer: false);
	}

	[GeneratedRegex(@"<b\b[^>]*>\s*Download\s+Link:\s*</b>\s*<a\b[^>]*\bhref\s*=\s*(?:""([^""]+)""|'([^']+)'|([^\s>]+))", RegexOptions.IgnoreCase)]
	private static partial Regex VoiceModelsDownloadLinkPattern();

	[GeneratedRegex(@"<h3\b[^>]*>([\s\S]*?)</h3>", RegexOptions.IgnoreCase)]
	private static partial Regex VoiceModelsTitlePattern();

	[GeneratedRegex(@"<title\b[^>]*>([\s\S]*?)</title>", RegexOptions.IgnoreCase)]
	private static partial Regex HtmlTitlePattern();

	[GeneratedRegex(@"<[^>]+>", RegexOptions.IgnoreCase)]
	private static partial Regex StripHtmlTagsPattern();
}
