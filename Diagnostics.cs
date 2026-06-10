namespace Talktastic;

using System.Threading;

/// <summary>
/// Process-wide diagnostic logging. State lives in <see cref="Settings.Cli.Verbose"/> and
/// <see cref="Settings.Cli.ShowPerf"/>. Every emitted line is prefixed with the wall-clock
/// seconds since <see cref="Settings.StartedAt"/> so a user reading <c>--perf</c> /
/// <c>--verbose</c> output can see a running timeline (not just per-step deltas).
/// </summary>
internal static class Diagnostics
{
	private static int _firstAudioMarked;

	private static string Prefix()
	{
		// Clamp at zero -- static-init ordering can occasionally produce a tiny negative
		// elapsed on the very first call; that's a curio, not a useful signal.
		var totalSeconds = Math.Max(0.0, Settings.ElapsedTime.TotalSeconds);
		var minutes = (int)(totalSeconds / 60);
		var seconds = totalSeconds - (minutes * 60);
		return string.Create
		(
			System.Globalization.CultureInfo.InvariantCulture,
			$"[{minutes:D2}:{seconds:00.000}]"
		);
	}

	/// <summary>
	/// Writes a wall-time-prefixed line to stderr when <see cref="Settings.Cli.Verbose"/>
	/// is enabled; otherwise does nothing.
	/// </summary>
	/// <param name="message">The message body (the wall-time prefix is added automatically).</param>
	internal static void Log(string message)
	{
		if (Settings.Cli.Verbose)
		{
			Console.Error.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Prefix()} {message}"));
		}
	}

	/// <summary>
	/// Writes a wall-time-prefixed line to stderr when <see cref="Settings.Cli.ShowPerf"/>
	/// is enabled; otherwise does nothing. Use this for timing-related output so that
	/// <c>--perf</c> traces always show a running cumulative timeline.
	/// </summary>
	/// <param name="message">The message body (the wall-time prefix is added automatically).</param>
	internal static void LogPerf(string message)
	{
		if (Settings.Cli.ShowPerf)
		{
			Console.Error.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Prefix()} {message}"));
		}
	}

	/// <summary>
	/// Records the moment the first PCM chunk is handed to the audio device, exactly
	/// ONCE per process (subsequent calls are silent no-ops). Backend-agnostic -- intended
	/// to be called from each playback entry point (FIFO sink, buffered <c>waveOut.Play</c>,
	/// SAPI <c>ISpVoice::Speak</c>, the Speech SDK player, etc.) so that <c>--perf</c>
	/// surfaces a true wall-clock time-to-first-audio regardless of which engine ran.
	/// </summary>
	/// <param name="source">Short tag naming the backend (e.g. <c>"stream"</c>, <c>"sapi"</c>, <c>"waveout"</c>).</param>
	internal static void MarkFirstAudio(string source)
	{
		if (Interlocked.CompareExchange(ref _firstAudioMarked, 1, 0) != 0)
		{
			return;
		}

		var elapsed = Settings.ElapsedTime;
		LogPerf
		(
			string.Create
			(
				System.Globalization.CultureInfo.InvariantCulture,
				$"[first-audio] {elapsed.TotalMilliseconds:F0}ms (source={source})"
			)
		);
	}
}
