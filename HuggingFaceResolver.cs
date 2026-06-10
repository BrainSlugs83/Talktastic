using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Talktastic;

/// <summary>
/// Resolves Hugging Face model and file URLs to downloadable assets.
/// </summary>
[ExcludeFromCodeCoverage]
sealed partial class HuggingFaceResolver : IUrlResolver
{
	/// <summary>
	/// Determines whether the URL points to Hugging Face.
	/// </summary>
	/// <param name="url">The URL to test.</param>
	/// <returns><see langword="true"/> when the resolver can handle the URL.</returns>
	public bool CanResolve(string url)
	{
		return Uri.TryCreate(url, UriKind.Absolute, out var uri)
			&& uri.Host.Contains("huggingface.co", StringComparison.OrdinalIgnoreCase);
	}

	#pragma warning disable CA1502 // Resolver intentionally keeps supported HuggingFace URL branches in one place.
	/// <summary>
	/// Resolves a Hugging Face URL to the preferred downloadable asset.
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
		var uri = new Uri(url);
		var segments = uri.LocalPath.Trim('/').Split('/');
		if (segments.Length < 2)
		{
			throw new ArgumentException($"Cannot parse HuggingFace URL: {uri}");
		}

		var owner = segments[0];
		var repo = segments[1];
		var directResult = TryResolveDirectFile(repo, owner, segments);
		if (directResult is not null)
		{
			return directResult;
		}

		var (branch, subPath) = GetTreeLocation(segments);
		var apiUrl = string.IsNullOrEmpty(subPath)
			? $"https://huggingface.co/api/models/{owner}/{repo}/tree/{branch}"
			: $"https://huggingface.co/api/models/{owner}/{repo}/tree/{branch}/{subPath}";

		using var response = await http.GetAsync(new Uri(apiUrl), cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException
			(
				$"Failed to list HuggingFace folder: HTTP {(int)response.StatusCode} from {apiUrl}"
			);
		}

		var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		var baseResolve = $"https://huggingface.co/{owner}/{repo}/resolve/{branch}";
		return ResolveFromListing(json, repo, subPath, baseResolve, apiUrl);
	}

	private static UrlResolverResult? TryResolveDirectFile(string repo, string owner, string[] segments)
	{
		if (segments.Length < 5 || !string.Equals(segments[2], "resolve", StringComparison.Ordinal))
		{
			return null;
		}

		var branch = segments[3];
		var filePath = string.Join('/', segments[4..]);
		var fileUrl = $"https://huggingface.co/{owner}/{repo}/resolve/{branch}/{filePath}";
		var resolvedSubPath = Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? string.Empty;
		var fileName = Path.GetFileNameWithoutExtension(filePath);
		var name = string.IsNullOrEmpty(resolvedSubPath)
			? ModelDownloader.DeriveModelNameFromRepo(repo, resolvedSubPath)
			: ModelDownloader.IsUsableName(fileName)
				? fileName
				: ModelDownloader.DeriveModelNameFromRepo(repo, resolvedSubPath);

		return new UrlResolverResult(fileUrl, ModelDownloader.CleanModelName(name) ?? name);
	}

	private static (string Branch, string SubPath) GetTreeLocation(string[] segments)
	{
		if (segments.Length >= 4 && string.Equals(segments[2], "tree", StringComparison.Ordinal))
		{
			var subPath = segments.Length > 4
				? string.Join('/', segments[4..])
				: string.Empty;
			return (segments[3], subPath);
		}

		return ("main", string.Empty);
	}

	private static UrlResolverResult ResolveFromListing
	(
		string json,
		string repo,
		string subPath,
		string baseResolve,
		string apiUrl
	)
	{
		var onnxResult = TryResolveOnnxFromListing(json, repo, subPath, baseResolve);
		if (onnxResult is not null)
		{
			return onnxResult;
		}

		var zipResult = TryResolveZipFromListing(json, repo, subPath, baseResolve);
		if (zipResult is not null)
		{
			return zipResult;
		}

		var pthResult = TryResolvePthFromListing(json, repo, subPath, baseResolve);
		if (pthResult is not null)
		{
			return pthResult;
		}

		throw new InvalidOperationException($"No .onnx, .zip, or .pth model file found in {apiUrl}");
	}

	private static UrlResolverResult? TryResolveOnnxFromListing
	(
		string json,
		string repo,
		string subPath,
		string baseResolve
	)
	{
		var onnxPath = FindPathInJson(json, HfOnnxPathPattern(), ".onnx.json");
		if (onnxPath is null)
		{
			return null;
		}

		var fileUrl = $"{baseResolve}/{onnxPath}";
		var onnxFilename = Path.GetFileNameWithoutExtension(onnxPath);
		var modelName = string.IsNullOrEmpty(subPath)
			? ModelDownloader.DeriveModelNameFromRepo(repo, subPath)
			: onnxFilename.Equals("model", StringComparison.OrdinalIgnoreCase)
				? ModelDownloader.DeriveModelNameFromRepo(repo, subPath)
				: onnxFilename;
		var companions = FindOnnxConfigCompanions(json, onnxPath, baseResolve);
		return new UrlResolverResult
		(
			fileUrl,
			ModelDownloader.CleanModelName(modelName) ?? modelName,
			companions
		);
	}

	private static UrlResolverResult? TryResolveZipFromListing
	(
		string json,
		string repo,
		string subPath,
		string baseResolve
	)
	{
		var zipPath = FindPathInJson(json, HfZipPathPattern(), null);
		if (zipPath is null)
		{
			return null;
		}

		var fileUrl = $"{baseResolve}/{zipPath}";
		var zipFilename = Path.GetFileNameWithoutExtension(zipPath);
		var modelName = string.IsNullOrEmpty(subPath)
			? ModelDownloader.DeriveModelNameFromRepo(repo, subPath)
			: zipFilename;
		return new UrlResolverResult(fileUrl, ModelDownloader.CleanModelName(modelName) ?? modelName);
	}

	private static UrlResolverResult? TryResolvePthFromListing
	(
		string json,
		string repo,
		string subPath,
		string baseResolve
	)
	{
		var pthPath = FindPathInJson(json, HfPthPathPattern(), null);
		if (pthPath is null)
		{
			return null;
		}

		var fileUrl = $"{baseResolve}/{pthPath}";
		var pthFilename = Path.GetFileNameWithoutExtension(pthPath);
		var modelName = string.IsNullOrEmpty(subPath)
			? ModelDownloader.DeriveModelNameFromRepo(repo, subPath)
			: ModelDownloader.IsUsableName(pthFilename)
				? pthFilename
				: ModelDownloader.DeriveModelNameFromRepo(repo, subPath);

		var companions = new List<string>();
		var indexPath = FindPathInJson(json, HfIndexPathPattern(), null);
		if (indexPath is not null)
		{
			companions.Add($"{baseResolve}/{indexPath}");
		}

		var jsonPath = FindPathInJson(json, HfConfigJsonPathPattern(), null);
		if (jsonPath is not null)
		{
			companions.Add($"{baseResolve}/{jsonPath}");
		}

		return new UrlResolverResult
		(
			fileUrl,
			ModelDownloader.CleanModelName(modelName) ?? modelName,
			companions.Count > 0 ? [.. companions] : null
		);
	}

	#pragma warning restore CA1502

	private static string[]? FindOnnxConfigCompanions(string json, string onnxPath, string baseResolve)
	{
		var companions = new List<string>();
		var configJsonPath = onnxPath + ".json";
		if (json.Contains(configJsonPath, StringComparison.OrdinalIgnoreCase))
		{
			companions.Add($"{baseResolve}/{configJsonPath}");
		}
		else
		{
			var cfgPath = FindPathInJson(json, HfConfigJsonPathPattern(), null);
			if (cfgPath is not null)
			{
				companions.Add($"{baseResolve}/{cfgPath}");
			}
		}

		return companions.Count > 0 ? [.. companions] : null;
	}

	private static string? FindPathInJson(string json, Regex pattern, string? excludeSuffix)
	{
		foreach (Match match in pattern.Matches(json))
		{
			var path = match.Groups[1].Value;
			if
			(
				excludeSuffix is not null
				&& path.EndsWith(excludeSuffix, StringComparison.OrdinalIgnoreCase)
			)
			{
				continue;
			}

			return path;
		}

		return null;
	}

	[GeneratedRegex(@"""path""\s*:\s*""([^""]+\.onnx)""")]
	private static partial Regex HfOnnxPathPattern();

	[GeneratedRegex(@"""path""\s*:\s*""([^""]+\.zip)""")]
	private static partial Regex HfZipPathPattern();

	[GeneratedRegex(@"""path""\s*:\s*""([^""]+\.pth)""")]
	private static partial Regex HfPthPathPattern();

	[GeneratedRegex(@"""path""\s*:\s*""([^""]+\.index)""")]
	private static partial Regex HfIndexPathPattern();

	[GeneratedRegex(@"""path""\s*:\s*""((?:[^""]+/)?(?:config\.json|metadata\.json))""")]
	private static partial Regex HfConfigJsonPathPattern();
}
