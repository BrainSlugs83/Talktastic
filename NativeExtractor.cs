using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Talktastic;

/// <summary>
/// Defines native DLL group values.
/// </summary>
[Flags]
internal enum DllGroup
{
	None = 0,
	SpeechSdk = 1,
	Lame = 2,
	OnnxRuntime = 4,
}

/// <summary>
/// Provides native DLL extraction operations.
/// </summary>
internal static class NativeExtractor
{
	private const string ResourcePrefix = "Talktastic.Native.";
	private const string ManifestResourceName = ResourcePrefix + "manifest.json";

	private static readonly string AppDataDir = Path.Combine
	(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Talktastic",
		"native"
	);

	private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "Talktastic");

	private static readonly Dictionary<DllGroup, string[]> GroupDllNames = new()
	{
		[DllGroup.SpeechSdk] =
		[
			"Microsoft.CognitiveServices.Speech.core.dll",
			"Microsoft.CognitiveServices.Speech.extension.audio.sys.dll",
			"Microsoft.CognitiveServices.Speech.extension.embedded.tts.dll",
			"Microsoft.CognitiveServices.Speech.extension.onnxruntime.dll",
		],
		[DllGroup.Lame] =
		[
			"libmp3lame.dll",
		],
		[DllGroup.OnnxRuntime] =
		[
			"onnxruntime.dll",
			"onnxruntime_providers_shared.dll",
			"DirectML.dll",
			"sherpa-onnx-c-api.dll",
		],
	};

	private static readonly object SyncRoot = new();
	private static readonly HashSet<string> ConfiguredDirs = new(StringComparer.OrdinalIgnoreCase);
	private static readonly ConcurrentBag<string> CwdExtractions = new();
	private static DllGroup _resolvedGroups = DllGroup.None;
	private static NativePayloadManifestEntry[]? _cachedManifest;
	private static bool _staleCleaned;

	/// <summary>
	/// Ensures that all native DLLs in the requested groups are available and loadable.
	/// Only resolves DLLs for the specified groups; others are ignored entirely.
	/// </summary>
	public static void EnsureAvailable(DllGroup groups)
	{
		var needed = groups & ~_resolvedGroups;
		if (needed == DllGroup.None)
		{
			return;
		}

		var sw = Settings.Cli.ShowPerf ? System.Diagnostics.Stopwatch.StartNew() : null;

		lock (SyncRoot)
		{
			needed = groups & ~_resolvedGroups;
			if (needed == DllGroup.None)
			{
				return;
			}

			var manifest = GetManifest();
			var neededNames = GetDllNames(needed);
			var entries = manifest.Where(e => neededNames.Contains(e.Name)).ToArray();
			var assembly = typeof(NativeExtractor).Assembly;

			using var mutex = new Mutex(false, @"Local\Talktastic.NativeDlls");
			var mutexAcquired = false;

			try
			{
				try
				{
					mutexAcquired = mutex.WaitOne(TimeSpan.FromMinutes(1));
				}
				catch (AbandonedMutexException)
				{
					mutexAcquired = true;
				}

				if (!mutexAcquired)
				{
					throw new InvalidOperationException("Timed out waiting for native DLL extraction lock.");
				}

				var resolved = new ConcurrentBag<ResolvedDll>();
				var cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
				var perDll = Settings.Cli.ShowPerf
					? new System.Collections.Concurrent.ConcurrentDictionary<string, long>()
					: null;

				Parallel.ForEach
				(
					entries,
					entry =>
					{
						var dllSw = Settings.Cli.ShowPerf
							? System.Diagnostics.Stopwatch.StartNew()
							: null;
						var result = FindOrExtract(assembly, entry, cwd);
						resolved.Add(result);
						if (dllSw is not null)
						{
							perDll![entry.Name] = dllSw.ElapsedMilliseconds;
						}
					}
				);

				ConfigureSearchPaths(resolved);

				if (!_staleCleaned)
				{
					CleanStaleFiles(manifest);
					_staleCleaned = true;
				}

				_resolvedGroups |= needed;

				if (sw is not null && perDll is not null)
				{
					foreach (var kvp in perDll.OrderByDescending(x => x.Value))
					{
						Diagnostics.LogPerf
						(
							string.Create
							(
								System.Globalization.CultureInfo.InvariantCulture,
								$"[dll] {kvp.Key}: {kvp.Value}ms"
							)
						);
					}
					Diagnostics.LogPerf
					(
						string.Create
						(
							System.Globalization.CultureInfo.InvariantCulture,
							$"[dll] total: {sw.ElapsedMilliseconds}ms ({entries.Length} DLLs)"
						)
					);
				}
			}
			finally
			{
				if (mutexAcquired)
				{
					mutex.ReleaseMutex();
				}
			}
		}
	}

	/// <summary>
	/// Deletes any DLLs we extracted to the current working directory this session.
	/// Best-effort; silently ignores files that are locked or already removed.
	/// </summary>
	public static void CleanupCwdExtractions()
	{
		while (CwdExtractions.TryTake(out var path))
		{
			try
			{
				File.Delete(path);
			}
			catch (IOException)
			{
				// DLL may still be loaded -- nothing we can do
			}
			catch (UnauthorizedAccessException)
			{
				// Best effort
			}
		}
	}

	/// <summary>
	/// Gets the DLL names.
	/// </summary>
	/// <param name="groups">The DLL groups.</param>
	/// <returns>The matching names.</returns>
	internal static HashSet<string> GetDllNames(DllGroup groups)
	{
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var flag in Enum.GetValues<DllGroup>())
		{
			if (flag != DllGroup.None && groups.HasFlag(flag) && GroupDllNames.TryGetValue(flag, out var dlls))
			{
				foreach (var dll in dlls)
				{
					names.Add(dll);
				}
			}
		}

		return names;
	}

	/// <summary>
	/// Gets the native payload manifest.
	/// </summary>
	/// <returns>The manifest entries.</returns>
	private static NativePayloadManifestEntry[] GetManifest()
	{
		if (_cachedManifest is not null)
		{
			return _cachedManifest;
		}

		var assembly = typeof(NativeExtractor).Assembly;

		using var stream = assembly.GetManifestResourceStream(ManifestResourceName)
			?? throw new InvalidOperationException($"Embedded resource '{ManifestResourceName}' not found.");

		_cachedManifest = JsonSerializer.Deserialize
		(
			stream,
			NativeExtractorJsonContext.Default.NativePayloadManifestEntryArray
		) ?? throw new InvalidOperationException("Native payload manifest could not be deserialized.");

		return _cachedManifest;
	}

	/// <summary>
	/// Finds or extracts a DLL.
	/// </summary>
	/// <param name="assembly">The assembly.</param>
	/// <param name="entry">The manifest entry.</param>
	/// <param name="cwd">The current working directory.</param>
	/// <returns>The resolved DLL.</returns>
	private static ResolvedDll FindOrExtract(Assembly assembly, NativePayloadManifestEntry entry, string cwd)
	{
		string[] searchDirs = [AppDataDir, TempDir, cwd];

		// Search for an existing valid copy
		foreach (var dir in searchDirs)
		{
			var path = Path.Combine(dir, entry.Name);

			if (IsValid(path, entry))
			{
				return new ResolvedDll(entry.Name, path);
			}
		}

		// Extract to the first writable location
		foreach (var dir in searchDirs)
		{
			try
			{
				Directory.CreateDirectory(dir);
				var path = Path.Combine(dir, entry.Name);
				ExtractResource(assembly, entry, path);

				if (IsCwd(dir, cwd))
				{
					CwdExtractions.Add(Path.GetFullPath(path));
				}

				return new ResolvedDll(entry.Name, path);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Try next location
			}
		}

		throw new InvalidOperationException
		(
			$"Failed to extract native DLL '{entry.Name}' to any location " +
			$"({AppDataDir}, {TempDir}, {cwd})."
		);
	}

	/// <summary>
	/// Determines whether the directory is the current working directory.
	/// </summary>
	/// <param name="dir">The d.</param>
	/// <param name="cwd">The c.</param>
	/// <returns><c>true</c> if the condition is met; otherwise, <c>false</c>.</returns>
	private static bool IsCwd(string dir, string cwd)
	{
		return string.Equals
		(
			Path.GetFullPath(dir),
			cwd,
			StringComparison.OrdinalIgnoreCase
		);
	}

	/// <summary>
	/// Determines whether the extracted DLL is valid.
	/// </summary>
	/// <param name="targetPath">The target path.</param>
	/// <param name="entry">The manifest entry.</param>
	/// <returns><c>true</c> if the condition is met; otherwise, <c>false</c>.</returns>
	private static bool IsValid(string targetPath, NativePayloadManifestEntry entry)
	{
		if (!File.Exists(targetPath))
		{
			return false;
		}

		var fileInfo = new FileInfo(targetPath);

		if (fileInfo.Length != entry.Size)
		{
			return false;
		}

		return string.Equals(ComputeMd5(targetPath), entry.Md5, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Extracts the embedded resource.
	/// </summary>
	/// <param name="assembly">The assembly.</param>
	/// <param name="entry">The manifest entry.</param>
	/// <param name="targetPath">The target path.</param>
	private static void ExtractResource(Assembly assembly, NativePayloadManifestEntry entry, string targetPath)
	{
		var resourceName = ResourcePrefix + entry.Name + ".br";

		using var compressedStream = assembly.GetManifestResourceStream(resourceName)
			?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");

		var tempPath = targetPath + ".tmp";

		try
		{
			if (File.Exists(tempPath))
			{
				File.Delete(tempPath);
			}

			using (var brotliStream = new BrotliStream(compressedStream, CompressionMode.Decompress))
			using (var outputStream = File.Create(tempPath))
			{
				brotliStream.CopyTo(outputStream);
				outputStream.Flush(true);
			}

			if (!IsValid(tempPath, entry))
			{
				throw new InvalidOperationException($"Extracted native DLL '{entry.Name}' failed validation.");
			}

			File.Move(tempPath, targetPath, true);
		}
		finally
		{
			if (File.Exists(tempPath))
			{
				File.Delete(tempPath);
			}
		}
	}

	/// <summary>
	/// Removes DLLs from the dedicated LOCALAPPDATA directory that are no longer
	/// in the embedded manifest. Only operates on our own directory -- never
	/// touches %TEMP% or CWD.
	/// </summary>
	private static void CleanStaleFiles(NativePayloadManifestEntry[] manifest)
	{
		if (!Directory.Exists(AppDataDir))
		{
			return;
		}

		var allManifestNames = new HashSet<string>
		(
			manifest.Select(static e => e.Name),
			StringComparer.OrdinalIgnoreCase
		);

		try
		{
			foreach (var file in Directory.EnumerateFiles(AppDataDir, "*.dll"))
			{
				var fileName = Path.GetFileName(file);

				if (!allManifestNames.Contains(fileName))
				{
					try
					{
						File.Delete(file);
					}
					catch (IOException)
					{
						// Locked by another process
					}
					catch (UnauthorizedAccessException)
					{
						// Best effort
					}
				}
			}
		}
		catch (IOException)
		{
			// Directory enumeration failed -- best effort
		}
		catch (UnauthorizedAccessException)
		{
			// Best effort
		}
	}

	/// <summary>
	/// Configures the native DLL search paths.
	/// </summary>
	/// <param name="resolved">The resolved DLLs.</param>
	private static void ConfigureSearchPaths(IEnumerable<ResolvedDll> resolved)
	{
		var newDirs = resolved
			.Select(static r => Path.GetDirectoryName(Path.GetFullPath(r.Path))!)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Where(d => !ConfiguredDirs.Contains(d))
			.ToArray();

		if (newDirs.Length == 0)
		{
			return;
		}

		// SetDllDirectory for the first directory (highest priority in DLL search order)
		if (ConfiguredDirs.Count == 0)
		{
			if (!SetDllDirectory(newDirs[0]))
			{
				throw new InvalidOperationException
				(
					$"Failed to set native DLL directory '{newDirs[0]}'. " +
					$"Win32 error {Marshal.GetLastWin32Error()}."
				);
			}
		}

		// Add all new directories to PATH
		var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		var pathEntries = new HashSet<string>
		(
			path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
			StringComparer.OrdinalIgnoreCase
		);

		var additions = newDirs.Where(d => !pathEntries.Contains(d)).ToArray();

		if (additions.Length > 0)
		{
			var prefix = string.Join(';', additions);

			Environment.SetEnvironmentVariable
			(
				"PATH",
				string.IsNullOrEmpty(path) ? prefix : $"{prefix};{path}"
			);
		}

		foreach (var dir in newDirs)
		{
			ConfiguredDirs.Add(dir);
		}
	}

	/// <summary>
	/// Computes the MD5 hash.
	/// </summary>
	/// <param name="path">The path.</param>
	/// <returns>The resulting string.</returns>
	[SuppressMessage
	(
		"Security",
		"CA5351:Do Not Use Broken Cryptographic Algorithms",
		Justification = "MD5 is used for non-security file identity checks against the embedded manifest."
	)]
	private static string ComputeMd5(string path)
	{
		using var stream = File.OpenRead(path);
		return Convert.ToHexString(MD5.HashData(stream));
	}

	/// <summary>
	/// Sets the DLL search directory.
	/// </summary>
	/// <param name="lpPathName">The DLL search path.</param>
	/// <returns><c>true</c> if the condition is met; otherwise, <c>false</c>.</returns>
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool SetDllDirectory(string lpPathName);
}

/// <summary>
/// Represents a resolved DLL.
/// </summary>
/// <param name="Name">The name.</param>
/// <param name="Path">The path.</param>
internal sealed record ResolvedDll(string Name, string Path);

/// <summary>
/// Represents a native payload manifest entry.
/// </summary>
/// <param name="Name">The name.</param>
/// <param name="Size">The S.</param>
/// <param name="Md5">The MD5.</param>
internal sealed record NativePayloadManifestEntry
(
	string Name,
	long Size,
	string Md5
);

/// <summary>
/// Provides native extractor JSON serialization metadata.
/// </summary>
[JsonSerializable(typeof(NativePayloadManifestEntry[]))]
internal sealed partial class NativeExtractorJsonContext : JsonSerializerContext
{
}
