namespace Talktastic;

/// <summary>
/// Process-wide diagnostic switches, set from CLI flags at startup.
/// </summary>
internal static class Diagnostics
{
	/// <summary>
	/// Gets or sets a value indicating whether verbose stderr instrumentation is enabled
	/// (set by <c>--verbose</c> or <c>TALKTASTIC_VERBOSE=1</c>).
	/// </summary>
	internal static bool Verbose { get; set; } =
		Environment.GetEnvironmentVariable("TALKTASTIC_VERBOSE") is "1" or "true";

	/// <summary>
	/// Writes a line to stderr when <see cref="Verbose"/> is enabled; otherwise does nothing.
	/// </summary>
	/// <param name="message">The message to write.</param>
	internal static void Log(string message)
	{
		if (Verbose)
		{
			Console.Error.WriteLine(message);
		}
	}
}
