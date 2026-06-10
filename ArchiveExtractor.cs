using System.Formats.Tar;
using System.IO.Compression;

namespace Talktastic;

/// <summary>
/// Unified archive extraction for all BCL-supported formats:
/// .zip, .tar, .tar.gz/.tgz, and single-file .gz.
/// Format detection uses file extensions with magic-byte fallback.
/// </summary>
static class ArchiveExtractor
{
	// Extension-based detection is handled in GetArchiveFormat via EndsWith checks.

	/// <summary>
	/// Returns true if the path or URL refers to a supported archive format
	/// based on file extension.
	/// </summary>
	public static bool IsArchive([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? pathOrUrl)
	{
		if (string.IsNullOrWhiteSpace(pathOrUrl))
			return false;

		// Strip query string / fragment for URL support
		var clean = pathOrUrl;
		var queryIdx = clean.IndexOf('?', StringComparison.Ordinal);
		if (queryIdx >= 0)
			clean = clean[..queryIdx];
		var fragIdx = clean.IndexOf('#', StringComparison.Ordinal);
		if (fragIdx >= 0)
			clean = clean[..fragIdx];

		return GetArchiveFormat(clean) != ArchiveFormat.Unknown;
	}

	/// <summary>
	/// Extracts an archive into <paramref name="destDir"/>, auto-detecting the format.
	/// Returns the list of extracted file paths (absolute).
	/// </summary>
	public static async Task<string[]> ExtractAsync
	(
		string archivePath,
		string destDir,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(destDir);

		if (!File.Exists(archivePath))
			throw new FileNotFoundException("Archive not found.", archivePath);

		var format = GetArchiveFormat(archivePath);

		// Fall back to magic bytes if extension is ambiguous
		if (format == ArchiveFormat.Unknown)
			format = DetectByMagicBytes(archivePath);

		if (format == ArchiveFormat.Unknown)
		{
			throw new NotSupportedException
			(
				$"Unsupported or unrecognized archive format: {Path.GetFileName(archivePath)}"
			);
		}

		Directory.CreateDirectory(destDir);

		return format switch
		{
			ArchiveFormat.Zip => await ExtractZipAsync(archivePath, destDir, cancellationToken).ConfigureAwait(false),
			ArchiveFormat.TarGz => await ExtractTarGzAsync(archivePath, destDir, cancellationToken).ConfigureAwait(false),
			ArchiveFormat.Tar => await ExtractTarAsync(archivePath, destDir, cancellationToken).ConfigureAwait(false),
			ArchiveFormat.Gz => ExtractGz(archivePath, destDir),
			_ => throw new NotSupportedException($"Unsupported archive format: {format}"),
		};
	}

	// ── Format detection ─────────────────────────────────────────────

	internal enum ArchiveFormat { Unknown, Zip, TarGz, Tar, Gz }

	/// <summary>
	/// Determines the archive format from the file extension.
	/// Checks compound extensions (.tar.gz) before simple ones (.gz).
	/// </summary>
	internal static ArchiveFormat GetArchiveFormat(string path)
	{
		// Normalize: work with the filename only, lowercase
		var name = Path.GetFileName(path);

		if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
			|| name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
		{
			return ArchiveFormat.TarGz;
		}

		if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
			return ArchiveFormat.Tar;

		if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
			return ArchiveFormat.Zip;

		if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
			return ArchiveFormat.Gz;

		return ArchiveFormat.Unknown;
	}

	/// <summary>
	/// Detects archive format from magic bytes when extension is ambiguous.
	/// </summary>
	private static ArchiveFormat DetectByMagicBytes(string path)
	{
		Span<byte> header = stackalloc byte[512];
		using var fs = File.OpenRead(path);
		var bytesRead = fs.Read(header);
		if (bytesRead < 4)
			return ArchiveFormat.Unknown;

		// ZIP: PK\x03\x04
		if (header[0] == 0x50 && header[1] == 0x4B
			&& header[2] == 0x03 && header[3] == 0x04)
		{
			return ArchiveFormat.Zip;
		}

		// Gzip: \x1f\x8b
		if (header[0] == 0x1F && header[1] == 0x8B)
		{
			// Could be .gz or .tar.gz — peek inside the gzip stream
			// to check if content starts with a tar header.
			if (bytesRead >= 512)
			{
				return IsTarInsideGzip(path) ? ArchiveFormat.TarGz : ArchiveFormat.Gz;
			}

			// If we can't read enough, assume plain gz
			return ArchiveFormat.Gz;
		}

		// Tar: "ustar" at offset 257
		if (bytesRead >= 263
			&& header[257] == 'u' && header[258] == 's'
			&& header[259] == 't' && header[260] == 'a'
			&& header[261] == 'r')
		{
			return ArchiveFormat.Tar;
		}

		return ArchiveFormat.Unknown;
	}

	/// <summary>
	/// Checks if a gzip file contains a tar archive by decompressing
	/// the first 263 bytes and looking for the "ustar" magic.
	/// </summary>
	private static bool IsTarInsideGzip(string path)
	{
		try
		{
			using var fs = File.OpenRead(path);
			using var gz = new GZipStream(fs, CompressionMode.Decompress);
			var header = new byte[263];
			var totalRead = 0;
			while (totalRead < header.Length)
			{
				var read = gz.Read(header, totalRead, header.Length - totalRead);
				if (read == 0) break;
				totalRead += read;
			}

			return totalRead >= 263
				&& header[257] == 'u' && header[258] == 's'
				&& header[259] == 't' && header[260] == 'a'
				&& header[261] == 'r';
		}
		catch (InvalidDataException)
		{
			return false;
		}
	}

	// ── Extractors ───────────────────────────────────────────────────

	private static async Task<string[]> ExtractZipAsync
	(
		string archivePath,
		string destDir,
		CancellationToken cancellationToken
	)
	{
		await ZipFile.ExtractToDirectoryAsync
		(
			archivePath, destDir,
			overwriteFiles: true, cancellationToken
		).ConfigureAwait(false);

		return Directory.GetFiles(destDir, "*", SearchOption.AllDirectories);
	}

	private static async Task<string[]> ExtractTarGzAsync
	(
		string archivePath,
		string destDir,
		CancellationToken cancellationToken
	)
	{
		var fs = File.OpenRead(archivePath);
		await using var fsDispose = fs.ConfigureAwait(false);
		var gz = new GZipStream(fs, CompressionMode.Decompress);
		await using var gzDispose = gz.ConfigureAwait(false);
		return await ExtractTarStreamAsync(gz, destDir, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<string[]> ExtractTarAsync
	(
		string archivePath,
		string destDir,
		CancellationToken cancellationToken
	)
	{
		var fs = File.OpenRead(archivePath);
		await using var fsDispose = fs.ConfigureAwait(false);
		return await ExtractTarStreamAsync(fs, destDir, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<string[]> ExtractTarStreamAsync
	(
		Stream stream,
		string destDir,
		CancellationToken cancellationToken
	)
	{
		var extracted = new List<string>();
		var reader = new TarReader(stream);
		await using var readerDispose = reader.ConfigureAwait(false);

		while (await reader.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false) is { } entry)
		{
			if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
				continue;

			// Sanitize the entry name to prevent path traversal
			var entryName = SanitizeTarEntryName(entry.Name);
			if (string.IsNullOrEmpty(entryName))
				continue;

			var destPath = Path.Combine(destDir, entryName);
			var destSubDir = Path.GetDirectoryName(destPath);
			if (!string.IsNullOrEmpty(destSubDir))
				Directory.CreateDirectory(destSubDir);

			await entry.ExtractToFileAsync(destPath, overwrite: true, cancellationToken).ConfigureAwait(false);
			extracted.Add(destPath);
		}

		return [.. extracted];
	}

	/// <summary>
	/// Sanitizes a tar entry name, stripping leading slashes and ../ segments
	/// to prevent path traversal.
	/// </summary>
	internal static string SanitizeTarEntryName(string name)
	{
		// Strip leading / or \
		var sanitized = name.TrimStart('/', '\\');

		// Remove any ../ or ..\ segments
		var parts = sanitized.Split('/', '\\');
		var safe = parts.Where(p => p != ".." && p != ".").ToArray();
		return Path.Combine(safe);
	}

	private static string[] ExtractGz(string archivePath, string destDir)
	{
		// Single-file gzip: decompress to a file named after the archive minus .gz
		var outputName = Path.GetFileNameWithoutExtension(archivePath);
		if (string.IsNullOrWhiteSpace(outputName))
			outputName = "decompressed";

		var destPath = Path.Combine(destDir, outputName);

		using var fs = File.OpenRead(archivePath);
		using var gz = new GZipStream(fs, CompressionMode.Decompress);
		using var output = File.Create(destPath);
		gz.CopyTo(output);

		return [destPath];
	}
}
