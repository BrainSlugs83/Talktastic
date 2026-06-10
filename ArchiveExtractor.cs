using System.Formats.Tar;
using System.IO.Compression;

namespace Talktastic;

/// <summary>
/// Unified archive extraction for all BCL-supported formats:
/// .zip, .tar, .tar.gz/.tgz, .tar.br, .tar.zz/.tar.zlib,
/// and single-file .gz, .br, .zz/.zlib.
/// Format detection uses file extensions with magic-byte fallback.
/// </summary>
static class ArchiveExtractor
{
	// Extension-based detection is handled in GetArchiveFormat via EndsWith checks.

	/// <summary>
	/// Returns true if the path or URL refers to a supported archive format
	/// based on file extension.
	/// </summary>
	internal static bool IsArchive([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? pathOrUrl)
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
			ArchiveFormat.TarBr => await ExtractTarBrAsync(archivePath, destDir, cancellationToken).ConfigureAwait(false),
			ArchiveFormat.TarZl => await ExtractTarZlAsync(archivePath, destDir, cancellationToken).ConfigureAwait(false),
			ArchiveFormat.Tar => await ExtractTarAsync(archivePath, destDir, cancellationToken).ConfigureAwait(false),
			ArchiveFormat.Gz => await ExtractGzAsync(archivePath, destDir, cancellationToken).ConfigureAwait(false),
			ArchiveFormat.Br => await ExtractBrAsync(archivePath, destDir, cancellationToken).ConfigureAwait(false),
			ArchiveFormat.Zl => await ExtractZlAsync(archivePath, destDir, cancellationToken).ConfigureAwait(false),
			_ => throw new NotSupportedException($"Unsupported archive format: {format}"),
		};
	}

	/// <summary>
	/// Extracts an archive from a stream using the supplied format.
	/// Supports TAR, TAR.GZ, TAR.BR, TAR.ZL, GZ, BR, ZL formats.
	/// ZIP is not supported for streaming because it requires a seekable stream.
	/// </summary>
	public static async Task<string[]> ExtractAsync
	(
		Stream stream,
		ArchiveFormat format,
		string destDir,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(stream);
		ArgumentException.ThrowIfNullOrWhiteSpace(destDir);

		Directory.CreateDirectory(destDir);

		switch (format)
		{
			case ArchiveFormat.Zip:
				throw new NotSupportedException("ZIP requires a seekable stream. Use the file-based overload.");

			case ArchiveFormat.TarGz:
			{
				var gz = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
				await using var gzDispose = gz.ConfigureAwait(false);
				return await ExtractTarStreamAsync(gz, destDir, cancellationToken).ConfigureAwait(false);
			}

			case ArchiveFormat.TarBr:
			{
				var br = new BrotliStream(stream, CompressionMode.Decompress, leaveOpen: true);
				await using var brDispose = br.ConfigureAwait(false);
				return await ExtractTarStreamAsync(br, destDir, cancellationToken).ConfigureAwait(false);
			}

			case ArchiveFormat.TarZl:
			{
				var zl = new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: true);
				await using var zlDispose = zl.ConfigureAwait(false);
				return await ExtractTarStreamAsync(zl, destDir, cancellationToken).ConfigureAwait(false);
			}

			case ArchiveFormat.Tar:
				return await ExtractTarStreamAsync(stream, destDir, cancellationToken).ConfigureAwait(false);

			case ArchiveFormat.Gz:
			{
				var gz = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
				await using var gzDispose = gz.ConfigureAwait(false);
				return await ExtractSingleFileAsync("decompressed", gz, destDir, cancellationToken).ConfigureAwait(false);
			}

			case ArchiveFormat.Br:
			{
				var br = new BrotliStream(stream, CompressionMode.Decompress, leaveOpen: true);
				await using var brDispose = br.ConfigureAwait(false);
				return await ExtractSingleFileAsync("decompressed", br, destDir, cancellationToken).ConfigureAwait(false);
			}

			case ArchiveFormat.Zl:
			{
				var zl = new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: true);
				await using var zlDispose = zl.ConfigureAwait(false);
				return await ExtractSingleFileAsync("decompressed", zl, destDir, cancellationToken).ConfigureAwait(false);
			}

			case ArchiveFormat.Unknown:
			default:
				throw new NotSupportedException($"Unsupported archive format: {format}");
		}
	}

	// ── Format detection ─────────────────────────────────────────────

	internal enum ArchiveFormat
	{
		Unknown,
		Zip,
		TarGz,
		Tar,
		Gz,
		TarBr,
		Br,
		TarZl,
		Zl,
	}

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

		if (name.EndsWith(".tar.br", StringComparison.OrdinalIgnoreCase))
			return ArchiveFormat.TarBr;

		if (name.EndsWith(".tar.zz", StringComparison.OrdinalIgnoreCase)
			|| name.EndsWith(".tar.zlib", StringComparison.OrdinalIgnoreCase))
		{
			return ArchiveFormat.TarZl;
		}

		if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
			return ArchiveFormat.Tar;

		if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
			return ArchiveFormat.Zip;

		if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
			return ArchiveFormat.Gz;

		if (name.EndsWith(".br", StringComparison.OrdinalIgnoreCase))
			return ArchiveFormat.Br;

		if (name.EndsWith(".zz", StringComparison.OrdinalIgnoreCase)
			|| name.EndsWith(".zlib", StringComparison.OrdinalIgnoreCase))
		{
			return ArchiveFormat.Zl;
		}

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
		if (bytesRead < 2)
			return ArchiveFormat.Unknown;

		// ZIP: PK\x03\x04
		if (bytesRead >= 4
			&& header[0] == 0x50 && header[1] == 0x4B
			&& header[2] == 0x03 && header[3] == 0x04)
		{
			return ArchiveFormat.Zip;
		}

		// Gzip: \x1f\x8b
		if (header[0] == 0x1F && header[1] == 0x8B)
		{
			// Could be .gz or .tar.gz -- peek inside the gzip stream
			// to check if content starts with a tar header.
			return IsTarInsideGzip(path) ? ArchiveFormat.TarGz : ArchiveFormat.Gz;
		}

		// Brotli has no stable magic number. Extension-based detection only.

		// ZLib starts with a two-byte header, commonly 78 01 / 78 5E / 78 9C / 78 DA.
		// This is still heuristic and may false-positive on arbitrary binary data.
		if (IsLikelyZLibHeader(header, bytesRead))
		{
			return IsTarInsideZLib(path) ? ArchiveFormat.TarZl : ArchiveFormat.Zl;
		}

		// Tar: "ustar" at offset 257
		if (IsTarHeader(header, bytesRead))
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

			return IsTarHeader(header, totalRead);
		}
		catch (InvalidDataException)
		{
			return false;
		}
	}

	private static bool IsTarInsideZLib(string path)
	{
		try
		{
			using var fs = File.OpenRead(path);
			using var zl = new ZLibStream(fs, CompressionMode.Decompress);
			var header = new byte[263];
			var totalRead = 0;
			while (totalRead < header.Length)
			{
				var read = zl.Read(header, totalRead, header.Length - totalRead);
				if (read == 0)
					break;

				totalRead += read;
			}

			return IsTarHeader(header, totalRead);
		}
		catch (InvalidDataException)
		{
			return false;
		}
	}

	private static bool IsTarHeader(ReadOnlySpan<byte> header, int bytesRead)
	{
		return bytesRead >= 263
			&& header[257] == 'u' && header[258] == 's'
			&& header[259] == 't' && header[260] == 'a'
			&& header[261] == 'r';
	}

	private static bool IsLikelyZLibHeader(ReadOnlySpan<byte> header, int bytesRead)
	{
		if (bytesRead < 2 || header[0] != 0x78)
			return false;

		var commonCompressionFlags = header[1] is 0x01 or 0x5E or 0x9C or 0xDA;
		var headerValue = (header[0] << 8) | header[1];
		return commonCompressionFlags || headerValue % 31 == 0;
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

	private static async Task<string[]> ExtractTarBrAsync
	(
		string archivePath,
		string destDir,
		CancellationToken cancellationToken
	)
	{
		var fs = File.OpenRead(archivePath);
		await using var fsDispose = fs.ConfigureAwait(false);
		var br = new BrotliStream(fs, CompressionMode.Decompress);
		await using var brDispose = br.ConfigureAwait(false);
		return await ExtractTarStreamAsync(br, destDir, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<string[]> ExtractTarZlAsync
	(
		string archivePath,
		string destDir,
		CancellationToken cancellationToken
	)
	{
		var fs = File.OpenRead(archivePath);
		await using var fsDispose = fs.ConfigureAwait(false);
		var zl = new ZLibStream(fs, CompressionMode.Decompress);
		await using var zlDispose = zl.ConfigureAwait(false);
		return await ExtractTarStreamAsync(zl, destDir, cancellationToken).ConfigureAwait(false);
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

	private static async Task<string[]> ExtractGzAsync
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
		return await ExtractSingleFileAsync
		(
			GetSingleFileOutputName(archivePath),
			gz,
			destDir,
			cancellationToken
		).ConfigureAwait(false);
	}

	private static async Task<string[]> ExtractBrAsync
	(
		string archivePath,
		string destDir,
		CancellationToken cancellationToken
	)
	{
		var fs = File.OpenRead(archivePath);
		await using var fsDispose = fs.ConfigureAwait(false);
		var br = new BrotliStream(fs, CompressionMode.Decompress);
		await using var brDispose = br.ConfigureAwait(false);
		return await ExtractSingleFileAsync
		(
			GetSingleFileOutputName(archivePath),
			br,
			destDir,
			cancellationToken
		).ConfigureAwait(false);
	}

	private static async Task<string[]> ExtractZlAsync
	(
		string archivePath,
		string destDir,
		CancellationToken cancellationToken
	)
	{
		var fs = File.OpenRead(archivePath);
		await using var fsDispose = fs.ConfigureAwait(false);
		var zl = new ZLibStream(fs, CompressionMode.Decompress);
		await using var zlDispose = zl.ConfigureAwait(false);
		return await ExtractSingleFileAsync
		(
			GetSingleFileOutputName(archivePath),
			zl,
			destDir,
			cancellationToken
		).ConfigureAwait(false);
	}

	private static async Task<string[]> ExtractSingleFileAsync
	(
		string outputName,
		Stream stream,
		string destDir,
		CancellationToken cancellationToken
	)
	{
		var safeOutputName = string.IsNullOrWhiteSpace(outputName)
			? "decompressed"
			: outputName;

		var destPath = Path.Combine(destDir, safeOutputName);
		var output = File.Create(destPath);
		await using var outputDispose = output.ConfigureAwait(false);
		await stream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
		return [destPath];
	}

	private static string GetSingleFileOutputName(string archivePath)
	{
		var outputName = Path.GetFileNameWithoutExtension(archivePath);
		return string.IsNullOrWhiteSpace(outputName)
			? "decompressed"
			: outputName;
	}
}

