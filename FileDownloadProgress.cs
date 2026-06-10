namespace Talktastic;

/// <summary>
/// Progress snapshot reported during file download operations.
/// </summary>
internal sealed record FileDownloadProgress
{
	/// <summary>
	/// Gets the current phase.
	/// </summary>
	internal DownloadPhase Phase { get; init; }

	/// <summary>
	/// Gets the bytes transferred so far.
	/// </summary>
	internal long BytesTransferred { get; init; }

	/// <summary>
	/// Gets the total bytes expected, if known.
	/// </summary>
	internal long? TotalBytes { get; init; }

	/// <summary>
	/// Gets the best 0-100 estimate of overall progress.
	/// </summary>
	internal double? OverallPercent { get; init; }

	/// <summary>
	/// Gets the elapsed time.
	/// </summary>
	internal TimeSpan Elapsed { get; init; }

	/// <summary>
	/// Gets the smoothed transfer rate in bytes per second.
	/// </summary>
	internal double TransferRate { get; init; }

	/// <summary>
	/// Gets the estimated time remaining, if known.
	/// </summary>
	internal TimeSpan? Eta { get; init; }

	/// <summary>
	/// Gets the file currently being written or extracted.
	/// </summary>
	internal string? CurrentFile { get; init; }

	/// <summary>
	/// Gets the number of files completed so far.
	/// </summary>
	internal int FilesCompleted { get; init; }

	/// <summary>
	/// Gets the total entry count, if known.
	/// </summary>
	internal int? EntryCount { get; init; }
}

/// <summary>
/// Phase of a file download operation.
/// </summary>
internal enum DownloadPhase
{
	/// <summary>
	/// The URL is being resolved.
	/// </summary>
	Resolving,

	/// <summary>
	/// Bytes are being downloaded to disk.
	/// </summary>
	Downloading,

	/// <summary>
	/// An archive is being extracted.
	/// </summary>
	Extracting,

	/// <summary>
	/// A streamable archive is being extracted while downloading.
	/// </summary>
	Streaming,

	/// <summary>
	/// The operation completed successfully.
	/// </summary>
	Complete,

	/// <summary>
	/// The operation failed.
	/// </summary>
	Failed,
}
