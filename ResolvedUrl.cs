namespace Talktastic;

/// <summary>
/// Result of resolving an input URL through the resolver chain.
/// </summary>
internal sealed record ResolvedUrl
{
	/// <summary>
	/// Gets the final download URL.
	/// </summary>
	internal required string FinalUrl { get; init; }

	/// <summary>
	/// Gets the resolved file name, if known.
	/// </summary>
	internal string? FileName { get; init; }

	/// <summary>
	/// Gets the content length reported by the server, if any.
	/// </summary>
	internal long? ContentLength { get; init; }

	/// <summary>
	/// Gets the MIME type from response headers, if any.
	/// </summary>
	internal string? ContentType { get; init; }

	/// <summary>
	/// Gets the resolved source kind.
	/// </summary>
	internal ResolvedUrlSourceType SourceType { get; init; }

	/// <summary>
	/// Gets the URLs visited before the final URL.
	/// </summary>
	internal IReadOnlyList<string> IntermediateUrls { get; init; } = Array.Empty<string>();

	/// <summary>
	/// Gets any companion URLs discovered during resolution.
	/// </summary>
	internal IReadOnlyList<string>? CompanionUrls { get; init; }

	/// <summary>
	/// Gets the candidate display names discovered during resolution.
	/// </summary>
	internal IReadOnlyList<string> Names { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Identifies the kind of resource a resolved URL points to.
/// </summary>
internal enum ResolvedUrlSourceType
{
	/// <summary>
	/// The URL points to a single file.
	/// </summary>
	SingleFile,

	/// <summary>
	/// The URL points to an archive file.
	/// </summary>
	Archive,

	/// <summary>
	/// The URL points to a folder-like listing.
	/// </summary>
	Folder,
}
