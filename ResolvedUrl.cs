namespace Talktastic;

/// <summary>
/// Result of resolving an input URL through the resolver chain.
/// </summary>
internal sealed record ResolvedUrl
{
	/// <summary>
	/// The actual download URL after full resolution.
	/// </summary>
	internal required string FinalUrl { get; init; }

	/// <summary>
	/// Content-Disposition or URL-derived filename.
	/// </summary>
	internal string? FileName { get; init; }

	/// <summary>
	/// File size if server reports it (from Content-Length header).
	/// </summary>
	internal long? ContentLength { get; init; }

	/// <summary>
	/// MIME type from response headers.
	/// </summary>
	internal string? ContentType { get; init; }

	/// <summary>
	/// Whether this is a single file, archive, or folder listing.
	/// </summary>
	internal ResolvedUrlSourceType SourceType { get; init; }

	/// <summary>
	/// All URLs visited during resolution (for registry). Does NOT include FinalUrl.
	/// </summary>
	internal IReadOnlyList<string> IntermediateUrls { get; init; } = Array.Empty<string>();

	/// <summary>
	/// Companion URLs discovered during resolution (e.g. .onnx.json config files).
	/// </summary>
	internal IReadOnlyList<string>? CompanionUrls { get; init; }

	/// <summary>
	/// All unique display names collected from resolvers, sorted by length (shortest first).
	/// </summary>
	internal IReadOnlyList<string> Names { get; init; } = Array.Empty<string>();
}

internal enum ResolvedUrlSourceType
{
	SingleFile,
	Archive,
	Folder,
}
