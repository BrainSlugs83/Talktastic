using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Talktastic;

[ExcludeFromCodeCoverage]
sealed partial class GoogleDriveResolver : IUrlResolver
{
	public bool CanResolve(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
		{
			return false;
		}

		var host = uri.Host;
		return host.EndsWith("drive.google.com", StringComparison.OrdinalIgnoreCase)
			|| host.EndsWith("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase);
	}

	public async Task<UrlResolverResult> ResolveAsync
	(
		HttpClient http,
		string url,
		CancellationToken cancellationToken
	)
	{
		var fileId = ExtractDriveFileId(url);
		if (fileId is null)
		{
			throw new ArgumentException($"Could not extract Google Drive file ID from URL: {url}");
		}

		Diagnostics.LogPerf
		(
			string.Create
			(
				System.Globalization.CultureInfo.InvariantCulture,
				$"[resolve] google-drive file id={fileId}"
			)
		);

		var downloadUrl = $"https://drive.usercontent.google.com/download?id={fileId}&export=download&authuser=0";
		var (finalUrl, fileName) = await ProbeDriveDownloadAsync
		(
			http,
			downloadUrl,
			cancellationToken
		).ConfigureAwait(false);

		var (modelName, _) = DeriveDriveNameAndType(fileName, fileId);
		return new UrlResolverResult(finalUrl, modelName);
	}

	private static string? ExtractDriveFileId(string url)
	{
		var fileMatch = DriveFileIdPathPattern().Match(url);
		if (fileMatch.Success)
		{
			return fileMatch.Groups[1].Value;
		}

		var queryMatch = DriveFileIdQueryPattern().Match(url);
		if (queryMatch.Success)
		{
			return queryMatch.Groups[1].Value;
		}

		return null;
	}

	private static async Task<(string FinalUrl, string? FileName)> ProbeDriveDownloadAsync
	(
		HttpClient http,
		string downloadUrl,
		CancellationToken cancellationToken
	)
	{
		using var firstProbe = await http.GetAsync
		(
			new Uri(downloadUrl),
			HttpCompletionOption.ResponseHeadersRead,
			cancellationToken
		).ConfigureAwait(false);

		if (!firstProbe.IsSuccessStatusCode)
		{
			throw new InvalidOperationException
			(
				$"Google Drive probe failed: HTTP {(int)firstProbe.StatusCode} for {downloadUrl}"
			);
		}

		var firstContentType = firstProbe.Content.Headers.ContentType?.MediaType ?? string.Empty;
		var firstFileName = ModelDownloader.ExtractFilenameFromContentDisposition
		(
			firstProbe.Content.Headers.ContentDisposition?.ToString()
		);

		if (!firstContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
		{
			return (downloadUrl, firstFileName);
		}

		var html = await firstProbe.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		var confirmUrl = BuildDriveConfirmUrl(html, downloadUrl)
			?? throw new InvalidOperationException
			(
				"Google Drive returned a virus-scan-warning page but no confirm form could be parsed. "
				+ "The file may be too large for unattended download; try a direct .zip URL instead."
			);

		using var secondProbe = await http.GetAsync
		(
			new Uri(confirmUrl),
			HttpCompletionOption.ResponseHeadersRead,
			cancellationToken
		).ConfigureAwait(false);

		if (!secondProbe.IsSuccessStatusCode)
		{
			throw new InvalidOperationException
			(
				$"Google Drive confirm fetch failed: HTTP {(int)secondProbe.StatusCode}"
			);
		}

		var secondFileName = ModelDownloader.ExtractFilenameFromContentDisposition
		(
			secondProbe.Content.Headers.ContentDisposition?.ToString()
		);
		return (confirmUrl, secondFileName ?? firstFileName);
	}

	private static string? BuildDriveConfirmUrl(string html, string fallbackBaseUrl)
	{
		var formMatch = DriveConfirmFormPattern().Match(html);
		if (!formMatch.Success)
		{
			return null;
		}

		var action = WebUtility.HtmlDecode(formMatch.Groups[1].Value) ?? string.Empty;
		var actionUri =
			Uri.TryCreate(action, UriKind.Absolute, out var absoluteActionUri)
			&& absoluteActionUri is not null
				? absoluteActionUri
				: new Uri(new Uri(fallbackBaseUrl), action);

		var query = new StringBuilder();
		foreach (Match input in DriveConfirmInputPattern().Matches(html))
		{
			var name = WebUtility.HtmlDecode(input.Groups[1].Value);
			var value = WebUtility.HtmlDecode(input.Groups[2].Value);
			if (query.Length > 0)
			{
				query.Append('&');
			}

			query.Append(Uri.EscapeDataString(name));
			query.Append('=');
			query.Append(Uri.EscapeDataString(value));
		}

		if (query.Length == 0)
		{
			return null;
		}

		var actionUrl = actionUri.ToString();
		var separator = actionUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
		return string.Create
		(
			System.Globalization.CultureInfo.InvariantCulture,
			$"{actionUrl}{separator}{query}"
		);
	}

	private static (string ModelName, bool IsZip) DeriveDriveNameAndType(string? fileName, string fileId)
	{
		if (!string.IsNullOrWhiteSpace(fileName))
		{
			var nameNoExt = Path.GetFileNameWithoutExtension(fileName);
			var isZip = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
			var cleaned = ModelDownloader.CleanModelName(nameNoExt);
			if (cleaned is not null && ModelDownloader.IsUsableName(cleaned))
			{
				return (cleaned, isZip);
			}
		}

		return ($"drive_{fileId}", IsZip: true);
	}

	[GeneratedRegex(@"/file/d/([A-Za-z0-9_-]+)")]
	private static partial Regex DriveFileIdPathPattern();

	[GeneratedRegex(@"[?&]id=([A-Za-z0-9_-]+)")]
	private static partial Regex DriveFileIdQueryPattern();

	[GeneratedRegex(@"<form[^>]+action=""([^""]+)""[^>]*>", RegexOptions.IgnoreCase)]
	private static partial Regex DriveConfirmFormPattern();

	[GeneratedRegex(@"<input[^>]+name=""([^""]+)""[^>]+value=""([^""]*)""", RegexOptions.IgnoreCase)]
	private static partial Regex DriveConfirmInputPattern();
}
