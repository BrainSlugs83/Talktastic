using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Talktastic;

internal static class NativeExtractor
{
	private const string ResourcePrefix = "Talktastic.Native.";
	private const string ManifestResourceName = ResourcePrefix + "manifest.json";
	private static readonly object SyncRoot = new();
	private static readonly string ExtractDirectory = Path.Combine
	(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Talktastic",
		"native"
	);
	private static bool _initialized;

	public static void EnsureExtracted()
	{
		if (_initialized)
		{
			return;
		}

		lock (SyncRoot)
		{
			if (_initialized)
			{
				return;
			}

			var mutex = new Mutex(false, @"Local\Talktastic.NativeSpeechSdk");
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
					throw new InvalidOperationException("Timed out waiting for the native Speech SDK extraction lock.");
				}

				Directory.CreateDirectory(ExtractDirectory);

				var assembly = typeof(NativeExtractor).Assembly;

				foreach (var entry in LoadManifest(assembly))
				{
					var targetPath = Path.Combine(ExtractDirectory, entry.Name);

					if (!IsValid(targetPath, entry))
					{
						ExtractResource(assembly, entry, targetPath);
					}
				}

				ConfigureSearchPath();
				_initialized = true;
			}
			finally
			{
				if (mutexAcquired)
				{
					mutex.ReleaseMutex();
				}

				mutex.Dispose();
			}
		}
	}

	private static NativePayloadManifestEntry[] LoadManifest(Assembly assembly)
	{
		using var stream = assembly.GetManifestResourceStream(ManifestResourceName)
			?? throw new InvalidOperationException($"Embedded resource '{ManifestResourceName}' was not found.");

		return JsonSerializer.Deserialize(stream, NativeExtractorJsonContext.Default.NativePayloadManifestEntryArray)
			?? throw new InvalidOperationException("Embedded native payload manifest could not be deserialized.");
	}

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

	private static void ExtractResource
	(
		Assembly assembly,
		NativePayloadManifestEntry entry,
		string targetPath
	)
	{
		var resourceName = ResourcePrefix + entry.Name + ".gz";
		using var compressedStream = assembly.GetManifestResourceStream(resourceName)
			?? throw new InvalidOperationException($"Embedded resource '{resourceName}' was not found.");

		var tempPath = targetPath + ".tmp";

		try
		{
			if (File.Exists(tempPath))
			{
				File.Delete(tempPath);
			}

			using (var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress))
			using (var outputStream = File.Create(tempPath))
			{
				gzipStream.CopyTo(outputStream);
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

	private static void ConfigureSearchPath()
	{
		if (!SetDllDirectory(ExtractDirectory))
		{
			throw new InvalidOperationException
			(
				$"Failed to register native DLL directory '{ExtractDirectory}'. Win32 error {Marshal.GetLastWin32Error()}."
			);
		}

		var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		var pathEntries = path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		if (!pathEntries.Contains(ExtractDirectory, StringComparer.OrdinalIgnoreCase))
		{
			var updatedPath = string.IsNullOrEmpty(path)
				? ExtractDirectory
				: $"{ExtractDirectory};{path}";

			Environment.SetEnvironmentVariable("PATH", updatedPath);
		}
	}

	[SuppressMessage
	(
		"Security",
		"CA5351:Do Not Use Broken Cryptographic Algorithms",
		Justification = "MD5 is required here for non-security file identity checks against the embedded manifest."
	)]
	private static string ComputeMd5(string path)
	{
		using var stream = File.OpenRead(path);
		return Convert.ToHexString(MD5.HashData(stream));
	}

	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool SetDllDirectory(string lpPathName);
}

internal sealed record NativePayloadManifestEntry
(
	string Name,
	long Size,
	string Md5
);

[JsonSerializable(typeof(NativePayloadManifestEntry[]))]
internal sealed partial class NativeExtractorJsonContext : JsonSerializerContext
{
}
