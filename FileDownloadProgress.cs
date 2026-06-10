namespace Talktastic;

/// <summary>
/// Progress snapshot reported during file download operations.
/// </summary>
internal sealed record FileDownloadProgress
{
	/// <summary>
	/// Current phase of the operation.
	/// </summary>
	internal DownloadPhase Phase { get; init; }

	/// <summary>
	/// Bytes downloaded so far.
	/// </summary>
	internal long BytesTransferred { get; init; }

	/// <summary>
	/// Content-Length if known.
	/// </summary>
	internal long? TotalBytes { get; init; }

	/// <summary>
	/// 0-100 best estimate of total progress.
	/// </summary>
	internal double? OverallPercent { get; init; }

	/// <summary>
	/// Total wall time so far.
	/// </summary>
	internal TimeSpan Elapsed { get; init; }

	/// <summary>
	/// Bytes/sec (smoothed EMA, approx 2s window).
	/// </summary>
	internal double TransferRate { get; init; }

	/// <summary>
	/// Estimated time remaining.
	/// </summary>
	internal TimeSpan? Eta { get; init; }

	/// <summary>
	/// File currently being written/extracted.
	/// </summary>
	internal string? CurrentFile { get; init; }

	/// <summary>
	/// Files extracted/downloaded so far.
	/// </summary>
	internal int FilesCompleted { get; init; }

	/// <summary>
	/// Total entries if known (e.g. ZIP archive entry count).
	/// </summary>
	internal int? EntryCount { get; init; }
}

/// <summary>
/// Phase of a file download operation.
/// </summary>
internal enum DownloadPhase
{
	Resolving,
	Downloading,
	Extracting,
	Streaming,
	Complete,
	Failed,
}
