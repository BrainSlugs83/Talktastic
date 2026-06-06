namespace Talktastic;

/// <summary>
/// Shared directory search paths for Talktastic data (voices, models, native DLLs).
/// Search order: LocalAppData → Temp → CWD.
/// </summary>
internal static class AppPaths
{
	internal static readonly string[] SearchBases =
	[
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Talktastic"),
		Path.GetTempPath(),
		Environment.CurrentDirectory,
	];

	/// <summary>
	/// Finds the first existing directory matching <paramref name="subPath"/> under any search base.
	/// </summary>
	internal static string? FindExistingDir(string subPath)
	{
		foreach (var basePath in SearchBases)
		{
			var dir = Path.Combine(basePath, subPath);
			if (Directory.Exists(dir))
			{
				return dir;
			}
		}

		return null;
	}

	/// <summary>
	/// Ensures <paramref name="subPath"/> exists under the first search base.
	/// Returns the full path.
	/// </summary>
	internal static string EnsureDir(string subPath)
	{
		var dir = Path.Combine(SearchBases[0], subPath);
		Directory.CreateDirectory(dir);
		return dir;
	}
}
