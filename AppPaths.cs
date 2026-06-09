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

	/// <summary>
	/// Returns <c>true</c> when two paths refer to the same filesystem entry
	/// after normalization (resolving relative segments and ignoring case on
	/// Windows). Used by rename operations to allow case-only renames like
	/// <c>homer</c> -&gt; <c>Homer</c>, which otherwise trip
	/// <c>Directory.Exists</c>/<c>File.Exists</c> conflict checks on
	/// case-insensitive filesystems.
	/// </summary>
	internal static bool IsSamePath(string a, string b)
	{
		if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
		{
			return false;
		}

		var fullA = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var fullB = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

		return string.Equals(fullA, fullB, StringComparison.OrdinalIgnoreCase);
	}
}
