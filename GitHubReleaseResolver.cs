using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Talktastic;

[ExcludeFromCodeCoverage]
sealed partial class GitHubReleaseResolver : IUrlResolver
{
	public bool CanResolve(string url)
	{
		return Uri.TryCreate(url, UriKind.Absolute, out var uri)
			&& uri.Host.Contains("github.com", StringComparison.OrdinalIgnoreCase)
			&& uri.LocalPath.Contains("/releases/", StringComparison.OrdinalIgnoreCase);
	}

	public async Task<UrlResolverResult> ResolveAsync
	(
		HttpClient http,
		string url,
		CancellationToken cancellationToken
	)
	{
		var uri = new Uri(url);
		var segments = uri.LocalPath.Trim('/').Split('/');
		if (segments.Length < 5)
		{
			throw new ArgumentException($"Cannot parse GitHub release URL: {uri}");
		}

		var owner = segments[0];
		var repo = segments[1];
		var kind = segments[3];

		if (string.Equals(kind, "download", StringComparison.Ordinal) && segments.Length >= 6)
		{
			var filename = segments[^1];
			if (filename.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
			{
				var name = filename[..^".onnx".Length];
				return new UrlResolverResult
				(
					uri.ToString(),
					ModelDownloader.CleanModelName(name) ?? name,
					[uri.ToString() + ".json"]
				);
			}

			if (filename.EndsWith(".pth", StringComparison.OrdinalIgnoreCase))
			{
				var name = filename[..^".pth".Length];
				return new UrlResolverResult(uri.ToString(), ModelDownloader.CleanModelName(name) ?? name);
			}

			if (ArchiveExtractor.IsArchive(filename))
			{
				var name = ModelDownloader.DeriveNameFromDirectUrl(uri);
				return new UrlResolverResult(uri.ToString(), ModelDownloader.CleanModelName(name) ?? name);
			}
		}

		var tag = segments[4];
		var apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}";

		using var response = await http.GetAsync(new Uri(apiUrl), cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException
			(
				$"Failed to query GitHub release: HTTP {(int)response.StatusCode} from {apiUrl}"
			);
		}

		var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

		var onnxUrl = FindAssetUrlInJson(json, GhOnnxAssetPattern(), ".onnx.json");
		if (onnxUrl is not null)
		{
			var onnxFilename = Path.GetFileName(new Uri(onnxUrl).LocalPath);
			var name = onnxFilename[..^".onnx".Length];
			return new UrlResolverResult
			(
				onnxUrl,
				ModelDownloader.CleanModelName(name) ?? name,
				[onnxUrl + ".json"]
			);
		}

		var zipUrl = FindAssetUrlInJson(json, GhZipAssetPattern(), null);
		if (zipUrl is not null)
		{
			var zipFilename = Path.GetFileName(new Uri(zipUrl).LocalPath);
			var name = zipFilename[..^".zip".Length];
			return new UrlResolverResult(zipUrl, ModelDownloader.CleanModelName(name) ?? name);
		}

		var pthUrl = FindAssetUrlInJson(json, GhPthAssetPattern(), null);
		if (pthUrl is not null)
		{
			var pthFilename = Path.GetFileName(new Uri(pthUrl).LocalPath);
			var pthName = pthFilename[..^".pth".Length];
			var modelName = ModelDownloader.IsUsableName(pthName)
				? pthName
				: repo;

			var companions = new List<string>();
			var indexUrl = FindAssetUrlInJson(json, GhIndexAssetPattern(), null);
			if (indexUrl is not null)
			{
				companions.Add(indexUrl);
			}

			return new UrlResolverResult
			(
				pthUrl,
				ModelDownloader.CleanModelName(modelName) ?? modelName,
				companions.Count > 0 ? [.. companions] : null
			);
		}

		throw new InvalidOperationException($"No .onnx, .zip, or .pth model file found in GitHub release '{tag}'");
	}

	private static string? FindAssetUrlInJson(string json, Regex pattern, string? excludeSuffix)
	{
		foreach (Match match in pattern.Matches(json))
		{
			var url = match.Groups[1].Value;
			if
			(
				excludeSuffix is not null
				&& url.EndsWith(excludeSuffix, StringComparison.OrdinalIgnoreCase)
			)
			{
				continue;
			}

			return url;
		}

		return null;
	}

	[GeneratedRegex(@"""browser_download_url""\s*:\s*""([^""]+\.onnx)""")]
	private static partial Regex GhOnnxAssetPattern();

	[GeneratedRegex(@"""browser_download_url""\s*:\s*""([^""]+\.zip)""")]
	private static partial Regex GhZipAssetPattern();

	[GeneratedRegex(@"""browser_download_url""\s*:\s*""([^""]+\.pth)""")]
	private static partial Regex GhPthAssetPattern();

	[GeneratedRegex(@"""browser_download_url""\s*:\s*""([^""]+\.index)""")]
	private static partial Regex GhIndexAssetPattern();
}
