namespace Talktastic;

/// <summary>
/// Result of a completed file download operation.
/// </summary>
internal sealed record FileDownloadResult
{
	/// <summary>
	/// Files that matched the FileFilter predicate.
	/// </summary>
	internal required IReadOnlyList<string> Files { get; init; }

	/// <summary>
	/// All files written to the destination, regardless of filter.
	/// </summary>
	internal required IReadOnlyList<string> AllFiles { get; init; }

	/// <summary>
	/// Total bytes transferred during download.
	/// </summary>
	internal long TotalBytes { get; init; }

	/// <summary>
	/// Total wall time (download + any extraction).
	/// </summary>
	internal TimeSpan Elapsed { get; init; }

	/// <summary>
	/// The local path where files were written (destination folder or subfolder).
	/// </summary>
	internal required string LocalPath { get; init; }
}
