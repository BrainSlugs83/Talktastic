using System.Net;

namespace Talktastic;

/// <summary>
/// Resolves HTTP redirects and captures response filenames.
/// </summary>
sealed class HttpRedirectResolver : IUrlResolver
{
	/// <summary>
	/// Determines whether the URL uses HTTP or HTTPS.
	/// </summary>
	/// <param name="url">The URL to test.</param>
	/// <returns><see langword="true"/> when the URL can be probed over HTTP.</returns>
	public bool CanResolve(string url)
	{
		return Uri.TryCreate(url, UriKind.Absolute, out var uri)
			&& (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
	}

	/// <summary>
	/// Resolves the final HTTP URL and any filename from response headers.
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
		using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
		using var headResponse = await http.SendAsync
		(
			headRequest,
			HttpCompletionOption.ResponseHeadersRead,
			cancellationToken
		).ConfigureAwait(false);

		if (headResponse.StatusCode == HttpStatusCode.MethodNotAllowed)
		{
			return await ResolveWithGetAsync(http, url, cancellationToken).ConfigureAwait(false);
		}

		return CreateResult(url, headResponse);
	}

	private static async Task<UrlResolverResult> ResolveWithGetAsync
	(
		HttpClient http,
		string url,
		CancellationToken cancellationToken
	)
	{
		using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
		using var getResponse = await http.SendAsync
		(
			getRequest,
			HttpCompletionOption.ResponseHeadersRead,
			cancellationToken
		).ConfigureAwait(false);

		return CreateResult(url, getResponse);
	}

	private static UrlResolverResult CreateResult(string originalUrl, HttpResponseMessage response)
	{
		var resolvedUrl = GetResolvedUrl(originalUrl, response);
		var displayName = ModelDownloader.ExtractFilenameFromContentDisposition
		(
			response.Content.Headers.ContentDisposition?.ToString()
		);

		return new UrlResolverResult(resolvedUrl, displayName);
	}

	private static string GetResolvedUrl(string originalUrl, HttpResponseMessage response)
	{
		var requestUri = response.RequestMessage?.RequestUri;
		var requestUrl = requestUri?.ToString();

		if
		(
			!string.IsNullOrWhiteSpace(requestUrl)
			&& !string.Equals(requestUrl, originalUrl, StringComparison.Ordinal)
		)
		{
			return requestUrl;
		}

		if
		(
			IsRedirectStatusCode(response.StatusCode)
			&& response.Headers.Location is Uri location
		)
		{
			return location.IsAbsoluteUri
				? location.ToString()
				: requestUri is null
					? originalUrl
					: new Uri(requestUri, location).ToString();
		}

		return requestUrl ?? originalUrl;
	}

	private static bool IsRedirectStatusCode(HttpStatusCode statusCode)
	{
		return statusCode is HttpStatusCode.Moved
			or HttpStatusCode.Redirect
			or HttpStatusCode.TemporaryRedirect
			or HttpStatusCode.PermanentRedirect;
	}
}
