namespace Talktastic;

/// <summary>
/// Resolves source-specific URLs into direct download URLs.
/// </summary>
interface IUrlResolver
{
	/// <summary>
	/// Determines whether this resolver can handle the URL.
	/// </summary>
	/// <param name="url">The URL to test.</param>
	/// <returns><see langword="true"/> when the resolver can handle the URL.</returns>
	bool CanResolve(string url);

	/// <summary>
	/// Resolves the URL and returns any discovered metadata.
	/// </summary>
	/// <param name="http">The HTTP client to use.</param>
	/// <param name="url">The URL to resolve.</param>
	/// <param name="cancellationToken">The cancellation token.</param>
	/// <returns>The resolved URL result.</returns>
	Task<UrlResolverResult> ResolveAsync(HttpClient http, string url, CancellationToken cancellationToken);
}

/// <summary>
/// Represents the result of one resolver step.
/// </summary>
/// <param name="Url">The resolved URL.</param>
/// <param name="DisplayName">The discovered display name, if any.</param>
/// <param name="CompanionUrls">Companion URLs to download with the primary file, if any.</param>
/// <param name="IsPreferredName">Whether the display name should outrank later names.</param>
record UrlResolverResult
(
	string Url,
	string? DisplayName = null,
	string[]? CompanionUrls = null,
	bool IsPreferredName = false
);
