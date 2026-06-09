namespace Talktastic;

/// <summary>
/// Process-wide settings: CLI-flag state (mutable, set once at startup) plus the magic
/// numbers that the rest of the codebase used to scatter through file-private constants.
/// Nested by domain so intellisense shows related knobs together.
/// </summary>
internal static class Settings
{
	/// <summary>
	/// Wall-clock time at which this assembly was loaded -- a reasonable proxy for
	/// "the moment the user pressed Enter".
	/// </summary>
	internal static readonly DateTime StartedAt = DateTime.Now;

	/// <summary>
	/// Gets the wall-clock <see cref="TimeSpan"/> since <see cref="StartedAt"/>. Use
	/// for any "how long has this run been going?" instrumentation.
	/// </summary>
	internal static TimeSpan ElapsedTime => DateTime.Now - StartedAt;

	/// <summary>
	/// CLI-flag-derived state. Properties are mutable and set once during startup from
	/// parsed options (and seeded from environment variables for headless / scripted use).
	/// </summary>
	internal static class Cli
	{
		/// <summary>
		/// Gets or sets a value indicating whether GPU (DirectML) execution should be
		/// disabled for RVC inference. Forced on by <c>--no-gpu</c> or
		/// <c>TALKTASTIC_NO_GPU=1</c>.
		/// </summary>
		internal static bool NoGpu { get; set; }
			= Environment.GetEnvironmentVariable("TALKTASTIC_NO_GPU") is "1" or "true";

		/// <summary>
		/// Gets or sets a value indicating whether RVC performance timing should be
		/// emitted to stderr. Forced on by <c>--perf</c>.
		/// </summary>
		internal static bool ShowPerf { get; set; }

		/// <summary>
		/// Gets or sets a value indicating whether verbose diagnostic instrumentation
		/// should be emitted to stderr. Forced on by <c>--verbose</c> or
		/// <c>TALKTASTIC_VERBOSE=1</c>.
		/// </summary>
		internal static bool Verbose { get; set; }
			= Environment.GetEnvironmentVariable("TALKTASTIC_VERBOSE") is "1" or "true";

		/// <summary>
		/// Gets or sets an override for <see cref="Rvc.StreamChunkSeconds"/>; null when
		/// the default applies. Forced by <c>--rvc-chunk-size &lt;seconds&gt;</c>.
		/// </summary>
		internal static double? RvcChunkSecondsOverride { get; set; }

		/// <summary>
		/// Gets or sets an override for <see cref="Rvc.StreamPadSeconds"/>; null when
		/// the default applies. Forced by <c>--rvc-padding-length &lt;seconds&gt;</c>.
		/// </summary>
		internal static double? RvcPadSecondsOverride { get; set; }

		/// <summary>
		/// Gets the effective RVC stream chunk size in seconds (the override when set,
		/// otherwise the default).
		/// </summary>
		internal static double EffectiveRvcChunkSeconds
			=> RvcChunkSecondsOverride ?? Rvc.StreamChunkSeconds;

		/// <summary>
		/// Gets the effective RVC stream pad size in seconds (the override when set,
		/// otherwise the default).
		/// </summary>
		internal static double EffectiveRvcPadSeconds
			=> RvcPadSecondsOverride ?? Rvc.StreamPadSeconds;
	}

	/// <summary>
	/// RVC pipeline tunables. Architecture constants and segmentation/chunking knobs.
	/// </summary>
	internal static class Rvc
	{
		/// <summary>The RVC source-side sample rate in Hz.</summary>
		internal const int InputSampleRate = 16000;

		/// <summary>The RVC inference frame size in samples (one frame per Window samples).</summary>
		internal const int Window = 160;

		/// <summary>The default speaker ID for single-speaker RVC models.</summary>
		internal const int SpeakerId = 0;

		/// <summary>
		/// The RMS-matching mix rate: how strongly the source RMS curve is enforced on
		/// the converted output.
		/// </summary>
		internal const float RmsMixRate = 0.25f;

		/// <summary>
		/// Per-segment reflection-pad length (seconds) for logical segmentation of long
		/// inputs that exceed <see cref="MaxSeconds"/>.
		/// </summary>
		internal const int PadSeconds = 3;

		/// <summary>The query window (seconds) used to find energy minima for logical-segment splits.</summary>
		internal const int QuerySeconds = 10;

		/// <summary>The target spacing (seconds) between logical-segment split points.</summary>
		internal const int CenterSeconds = 50;

		/// <summary>Inputs longer than this many seconds are split into logical segments at energy minima.</summary>
		internal const int MaxSeconds = 50;

		/// <summary>
		/// Default sub-chunk size in seconds for streaming RVC: the producer emits ~this
		/// many seconds of converted audio per inference, so playback starts after the
		/// first chunk instead of after the whole utterance. Overridable via
		/// <see cref="Cli.RvcChunkSecondsOverride"/> / <c>--rvc-chunk-size</c>.
		/// </summary>
		internal const double StreamChunkSeconds = 2.0;

		/// <summary>
		/// Default per-side context padding (seconds) added around each streaming sub-chunk
		/// before inference, then trimmed off the output. Hides decoder/ContentVec
		/// boundary artifacts. Overridable via <see cref="Cli.RvcPadSecondsOverride"/> /
		/// <c>--rvc-padding-length</c>. Relative inference overhead is approximately
		/// <c>2 * StreamPadSeconds / StreamChunkSeconds</c>.
		/// </summary>
		internal const double StreamPadSeconds = 0.3;
	}

	/// <summary>
	/// Streaming audio sink tunables (winmm waveOut FIFO).
	/// </summary>
	internal static class Stream
	{
		/// <summary>
		/// Target playback buffer size in seconds. Oversized single waveOut buffers can
		/// be garbled by some drivers, so the FIFO reader splits its drain into
		/// frame-aligned buffers of approximately this duration.
		/// </summary>
		internal const double ChunkSeconds = 2.0;

		/// <summary>Maximum number of queued-but-unfinished waveOut buffers, bounding driver-side latency.</summary>
		internal const int MaxInFlight = 16;

		/// <summary>Number of float-buffer batches the producer may queue ahead of the reader before it blocks.</summary>
		internal const int FifoCapacity = 8;
	}

	/// <summary>
	/// Heuristic thresholds for the corrupt-output detector. Real RVC speech has a
	/// zero-crossing rate of roughly 2000-3500/s; corrupt DirectML inference collapses
	/// to a continuous low-frequency drone (~300 Hz, ~600 crossings/s). We flag the
	/// latter so we can re-run the chunk on CPU before it reaches playback.
	/// </summary>
	internal static class OutputValidation
	{
		/// <summary>Maximum zero-crossing rate (per second) that still counts as corrupt.</summary>
		internal const double MaxZeroCrossingRate = 900.0;

		/// <summary>Minimum RMS the signal must reach before the detector trusts the ZCR (skip silence).</summary>
		internal const double MinRms = 0.02;

		/// <summary>Minimum clip duration (seconds) before the detector applies (skip short clips).</summary>
		internal const double MinSeconds = 0.5;
	}
}
