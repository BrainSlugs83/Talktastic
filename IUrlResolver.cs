namespace Talktastic;

/// <summary>
/// Resolves a URL to a potentially different URL with optional metadata.
/// Implementations handle specific URL patterns (e.g. Google Drive, HuggingFace).
/// </summary>
interface IUrlResolver
{
	/// <summary>
	/// Returns true if this resolver can handle the given URL.
	/// </summary>
	bool CanResolve(string url);

	/// <summary>
	/// Resolves the URL. May return a transformed URL, a display name, and companion URLs.
	/// </summary>
	Task<UrlResolverResult> ResolveAsync(HttpClient http, string url, CancellationToken cancellationToken);
}

/// <summary>
/// Result of a single URL resolution step.
/// </summary>
/// <param name="Url">The resolved URL (may be the same as input if no transformation).</param>
/// <param name="DisplayName">Optional display name discovered during resolution.</param>
/// <param name="CompanionUrls">Optional companion URLs (e.g. .onnx.json config files).</param>
/// <param name="IsPreferredName">Whether <paramref name="DisplayName"/> should outrank later discovered names.</param>
record UrlResolverResult
(
	string Url,
	string? DisplayName = null,
	string[]? CompanionUrls = null,
	bool IsPreferredName = false
);
