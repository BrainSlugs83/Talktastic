namespace Talktastic;

/// <summary>
/// Result of a completed file download operation.
/// </summary>
internal sealed record FileDownloadResult
{
	/// <summary>
	/// Gets the files that matched the filter.
	/// </summary>
	internal required IReadOnlyList<string> Files { get; init; }

	/// <summary>
	/// Gets all files written to the destination.
	/// </summary>
	internal required IReadOnlyList<string> AllFiles { get; init; }

	/// <summary>
	/// Gets the total bytes transferred.
	/// </summary>
	internal long TotalBytes { get; init; }

	/// <summary>
	/// Gets the total elapsed time.
	/// </summary>
	internal TimeSpan Elapsed { get; init; }

	/// <summary>
	/// Gets the local path that received the files.
	/// </summary>
	internal required string LocalPath { get; init; }
}
