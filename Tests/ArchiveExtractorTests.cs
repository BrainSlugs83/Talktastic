using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;

namespace Talktastic.Tests;

public sealed class ArchiveExtractorTests : IDisposable
{
	private readonly string _artifactRoot;

	public ArchiveExtractorTests()
	{
		_artifactRoot = Path.Combine
		(
			AppContext.BaseDirectory,
			"TestArtifacts",
			nameof(ArchiveExtractorTests),
			Guid.NewGuid().ToString("N")
		);

		Directory.CreateDirectory(_artifactRoot);
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_artifactRoot))
				Directory.Delete(_artifactRoot, recursive: true);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }

		GC.SuppressFinalize(this);
	}

	// ── IsArchive ────────────────────────────────────────────────────

	[Theory]
	[InlineData("model.zip", true)]
	[InlineData("model.tar.gz", true)]
	[InlineData("model.tgz", true)]
	[InlineData("model.tar", true)]
	[InlineData("model.gz", true)]
	[InlineData("model.br", true)]
	[InlineData("model.zz", true)]
	[InlineData("model.zlib", true)]
	[InlineData("model.tar.br", true)]
	[InlineData("model.tar.zz", true)]
	[InlineData("model.tar.zlib", true)]
	[InlineData("MODEL.ZIP", true)]
	[InlineData("model.TAR.GZ", true)]
	[InlineData("model.TGZ", true)]
	[InlineData("https://example.com/models/voice.zip", true)]
	[InlineData("https://example.com/models/voice.tar.gz", true)]
	[InlineData("https://example.com/models/voice.tar.br", true)]
	[InlineData("https://example.com/models/voice.zlib?token=abc", true)]
	[InlineData("https://example.com/models/voice.tar.gz?token=abc", true)]
	[InlineData("https://example.com/models/voice.zip#section", true)]
	[InlineData("model.onnx", false)]
	[InlineData("model.pth", false)]
	[InlineData("model.json", false)]
	[InlineData("model.onnx.json", false)]
	[InlineData("", false)]
	[InlineData(null, false)]
	public void IsArchive_DetectsFormatsCorrectly(string? path, bool expected)
	{
		Assert.Equal(expected, ArchiveExtractor.IsArchive(path));
	}

	[Theory]
	[InlineData("model.br", "Br")]
	[InlineData("model.tar.br", "TarBr")]
	[InlineData("model.zz", "Zl")]
	[InlineData("model.zlib", "Zl")]
	[InlineData("model.tar.zz", "TarZl")]
	[InlineData("model.tar.zlib", "TarZl")]
	public void GetArchiveFormat_DetectsNewFormats(string path, string expected)
	{
		Assert.Equal(expected, ArchiveExtractor.GetArchiveFormat(path).ToString());
	}

	// ── ZIP extraction ───────────────────────────────────────────────

	[Fact]
	public async Task ExtractAsync_Zip_ExtractsAllFiles()
	{
		var zipPath = Path.Combine(_artifactRoot, "test.zip");
		var extractDir = Path.Combine(_artifactRoot, "zip-out");

		CreateTestZip(zipPath, ("model.onnx", "onnx-data"), ("config.json", "{}"));

		var files = await ArchiveExtractor.ExtractAsync(zipPath, extractDir);

		Assert.Equal(2, files.Length);
		Assert.Contains(files, f => f.EndsWith("model.onnx", StringComparison.Ordinal));
		Assert.Contains(files, f => f.EndsWith("config.json", StringComparison.Ordinal));
		Assert.Equal
		(
			"onnx-data",
			await File.ReadAllTextAsync(files.First(f => f.EndsWith("model.onnx", StringComparison.Ordinal)))
		);
	}

	[Fact]
	public async Task ExtractAsync_Zip_PreservesSubdirectories()
	{
		var zipPath = Path.Combine(_artifactRoot, "nested.zip");
		var extractDir = Path.Combine(_artifactRoot, "nested-out");

		CreateTestZip(zipPath, ("subdir/model.onnx", "data"), ("subdir/config.json", "{}"));

		var files = await ArchiveExtractor.ExtractAsync(zipPath, extractDir);

		Assert.Equal(2, files.Length);
		Assert.All(files, f => Assert.Contains("subdir", f, StringComparison.Ordinal));
	}

	// ── TAR extraction ───────────────────────────────────────────────

	[Fact]
	public async Task ExtractAsync_Tar_ExtractsAllFiles()
	{
		var tarPath = Path.Combine(_artifactRoot, "test.tar");
		var extractDir = Path.Combine(_artifactRoot, "tar-out");

		await CreateTestTarAsync(tarPath, ("model.pth", "pth-data"u8.ToArray()), ("index.index", "idx"u8.ToArray()));

		var files = await ArchiveExtractor.ExtractAsync(tarPath, extractDir);

		Assert.Equal(2, files.Length);
		Assert.Contains(files, f => f.EndsWith("model.pth", StringComparison.Ordinal));
		Assert.Contains(files, f => f.EndsWith("index.index", StringComparison.Ordinal));
		Assert.Equal
		(
			"pth-data",
			await File.ReadAllTextAsync(files.First(f => f.EndsWith("model.pth", StringComparison.Ordinal)))
		);
	}

	[Fact]
	public async Task ExtractAsync_Tar_SkipsDirectoryEntries()
	{
		var tarPath = Path.Combine(_artifactRoot, "withdir.tar");
		var extractDir = Path.Combine(_artifactRoot, "tardir-out");

		await CreateTarWithDirectoryEntryAsync(tarPath);

		var files = await ArchiveExtractor.ExtractAsync(tarPath, extractDir);

		Assert.Single(files);
		Assert.EndsWith("model.onnx", files[0], StringComparison.Ordinal);
	}

	// ── TAR.GZ extraction ────────────────────────────────────────────

	[Fact]
	public async Task ExtractAsync_TarGz_ExtractsAllFiles()
	{
		var tgzPath = Path.Combine(_artifactRoot, "test.tar.gz");
		var extractDir = Path.Combine(_artifactRoot, "tgz-out");

		await CreateTestTarGzAsync(tgzPath, ("model.onnx", "onnx-content"u8.ToArray()), ("model.json", "{}"u8.ToArray()));

		var files = await ArchiveExtractor.ExtractAsync(tgzPath, extractDir);

		Assert.Equal(2, files.Length);
		Assert.Contains(files, f => f.EndsWith("model.onnx", StringComparison.Ordinal));
		Assert.Equal
		(
			"onnx-content",
			await File.ReadAllTextAsync(files.First(f => f.EndsWith("model.onnx", StringComparison.Ordinal)))
		);
	}

	[Fact]
	public async Task ExtractAsync_Tgz_ExtractsAllFiles()
	{
		var tgzPath = Path.Combine(_artifactRoot, "test.tgz");
		var extractDir = Path.Combine(_artifactRoot, "tgz2-out");

		await CreateTestTarGzAsync(tgzPath, ("voice.pth", "pth-bytes"u8.ToArray()));

		var files = await ArchiveExtractor.ExtractAsync(tgzPath, extractDir);

		Assert.Single(files);
		Assert.EndsWith("voice.pth", files[0], StringComparison.Ordinal);
	}

	// ── GZ extraction (single file) ──────────────────────────────────

	[Fact]
	public async Task ExtractAsync_Gz_DecompressesSingleFile()
	{
		var gzPath = Path.Combine(_artifactRoot, "model.onnx.gz");
		var extractDir = Path.Combine(_artifactRoot, "gz-out");

		var content = "gzipped-model-content"u8.ToArray();
		CreateTestGz(gzPath, content);

		var files = await ArchiveExtractor.ExtractAsync(gzPath, extractDir);

		Assert.Single(files);
		Assert.EndsWith("model.onnx", files[0], StringComparison.Ordinal);
		Assert.Equal(content, await File.ReadAllBytesAsync(files[0]));
	}

	[Fact]
	public async Task ExtractAsync_Gz_OutputNameStripsGzExtension()
	{
		var gzPath = Path.Combine(_artifactRoot, "data.bin.gz");
		var extractDir = Path.Combine(_artifactRoot, "gz-name-out");

		CreateTestGz(gzPath, "hello"u8.ToArray());

		var files = await ArchiveExtractor.ExtractAsync(gzPath, extractDir);

		Assert.Single(files);
		Assert.Equal("data.bin", Path.GetFileName(files[0]));
	}

	[Fact]
	public async Task ExtractAsync_Br_DecompressesSingleFile()
	{
		var brPath = Path.Combine(_artifactRoot, "model.onnx.br");
		var extractDir = Path.Combine(_artifactRoot, "br-out");

		var content = "brotli-model-content"u8.ToArray();
		CreateTestBr(brPath, content);

		var files = await ArchiveExtractor.ExtractAsync(brPath, extractDir);

		Assert.Single(files);
		Assert.EndsWith("model.onnx", files[0], StringComparison.Ordinal);
		Assert.Equal(content, await File.ReadAllBytesAsync(files[0]));
	}

	[Fact]
	public async Task ExtractAsync_Zl_DecompressesSingleFile()
	{
		var zlPath = Path.Combine(_artifactRoot, "model.onnx.zlib");
		var extractDir = Path.Combine(_artifactRoot, "zl-out");

		var content = "zlib-model-content"u8.ToArray();
		CreateTestZLib(zlPath, content);

		var files = await ArchiveExtractor.ExtractAsync(zlPath, extractDir);

		Assert.Single(files);
		Assert.EndsWith("model.onnx", files[0], StringComparison.Ordinal);
		Assert.Equal(content, await File.ReadAllBytesAsync(files[0]));
	}

	[Fact]
	public async Task ExtractAsync_TarBr_ExtractsAllFiles()
	{
		var tarBrPath = Path.Combine(_artifactRoot, "test.tar.br");
		var extractDir = Path.Combine(_artifactRoot, "tarbr-out");

		await CreateTestTarBrAsync
		(
			tarBrPath,
			("model.onnx", "onnx-br"u8.ToArray()),
			("model.json", "{}"u8.ToArray())
		);

		var files = await ArchiveExtractor.ExtractAsync(tarBrPath, extractDir);

		Assert.Equal(2, files.Length);
		Assert.Contains(files, f => f.EndsWith("model.onnx", StringComparison.Ordinal));
		Assert.Equal
		(
			"onnx-br",
			await File.ReadAllTextAsync(files.First(f => f.EndsWith("model.onnx", StringComparison.Ordinal)))
		);
	}

	[Fact]
	public async Task ExtractAsync_TarZl_ExtractsAllFiles()
	{
		var tarZlPath = Path.Combine(_artifactRoot, "test.tar.zlib");
		var extractDir = Path.Combine(_artifactRoot, "tarzl-out");

		await CreateTestTarZLibAsync
		(
			tarZlPath,
			("model.onnx", "onnx-zl"u8.ToArray()),
			("model.json", "{}"u8.ToArray())
		);

		var files = await ArchiveExtractor.ExtractAsync(tarZlPath, extractDir);

		Assert.Equal(2, files.Length);
		Assert.Contains(files, f => f.EndsWith("model.onnx", StringComparison.Ordinal));
		Assert.Equal
		(
			"onnx-zl",
			await File.ReadAllTextAsync(files.First(f => f.EndsWith("model.onnx", StringComparison.Ordinal)))
		);
	}

	// ── Streaming extraction ─────────────────────────────────────────

	[Fact]
	public async Task ExtractAsync_StreamTar_ExtractsAllFiles()
	{
		var extractDir = Path.Combine(_artifactRoot, "stream-tar-out");
		var archiveBytes = await CreateTarBytesAsync
		(
			("model.onnx", "onnx-tar-stream"u8.ToArray()),
			("config.json", "{}"u8.ToArray())
		);
		using var stream = new MemoryStream(archiveBytes, writable: false);

		var files = await ArchiveExtractor.ExtractAsync
		(
			stream,
			ArchiveExtractor.ArchiveFormat.Tar,
			extractDir
		);

		Assert.Equal(2, files.Length);
		Assert.Contains(files, f => f.EndsWith("model.onnx", StringComparison.Ordinal));
		Assert.Equal
		(
			"onnx-tar-stream",
			await File.ReadAllTextAsync(files.First(f => f.EndsWith("model.onnx", StringComparison.Ordinal)))
		);
	}

	[Fact]
	public async Task ExtractAsync_StreamTarGz_ExtractsAllFiles()
	{
		var extractDir = Path.Combine(_artifactRoot, "stream-tgz-out");
		var archiveBytes = await CreateTarGzBytesAsync
		(
			("model.onnx", "onnx-stream"u8.ToArray()),
			("config.json", "{}"u8.ToArray())
		);
		using var stream = new MemoryStream(archiveBytes, writable: false);

		var files = await ArchiveExtractor.ExtractAsync
		(
			stream,
			ArchiveExtractor.ArchiveFormat.TarGz,
			extractDir
		);

		Assert.Equal(2, files.Length);
		Assert.Contains(files, f => f.EndsWith("model.onnx", StringComparison.Ordinal));
		Assert.Equal
		(
			"onnx-stream",
			await File.ReadAllTextAsync(files.First(f => f.EndsWith("model.onnx", StringComparison.Ordinal)))
		);
	}

	[Fact]
	public async Task ExtractAsync_StreamTarBr_ExtractsAllFiles()
	{
		var extractDir = Path.Combine(_artifactRoot, "stream-tarbr-out");
		var archiveBytes = await CreateTarBrBytesAsync
		(
			("model.onnx", "onnx-br-stream"u8.ToArray()),
			("config.json", "{}"u8.ToArray())
		);
		using var stream = new MemoryStream(archiveBytes, writable: false);

		var files = await ArchiveExtractor.ExtractAsync
		(
			stream,
			ArchiveExtractor.ArchiveFormat.TarBr,
			extractDir
		);

		Assert.Equal(2, files.Length);
		Assert.Contains(files, f => f.EndsWith("model.onnx", StringComparison.Ordinal));
		Assert.Equal
		(
			"onnx-br-stream",
			await File.ReadAllTextAsync(files.First(f => f.EndsWith("model.onnx", StringComparison.Ordinal)))
		);
	}

	[Fact]
	public async Task ExtractAsync_StreamTarZl_ExtractsAllFiles()
	{
		var extractDir = Path.Combine(_artifactRoot, "stream-tarzl-out");
		var archiveBytes = await CreateTarZLibBytesAsync
		(
			("model.onnx", "onnx-zl-stream"u8.ToArray()),
			("config.json", "{}"u8.ToArray())
		);
		using var stream = new MemoryStream(archiveBytes, writable: false);

		var files = await ArchiveExtractor.ExtractAsync
		(
			stream,
			ArchiveExtractor.ArchiveFormat.TarZl,
			extractDir
		);

		Assert.Equal(2, files.Length);
		Assert.Contains(files, f => f.EndsWith("model.onnx", StringComparison.Ordinal));
		Assert.Equal
		(
			"onnx-zl-stream",
			await File.ReadAllTextAsync(files.First(f => f.EndsWith("model.onnx", StringComparison.Ordinal)))
		);
	}

	[Fact]
	public async Task ExtractAsync_StreamGz_DecompressesSingleFile()
	{
		var extractDir = Path.Combine(_artifactRoot, "stream-gz-out");
		var archiveBytes = CreateGZipBytes("stream-gz-content"u8.ToArray());
		using var stream = new MemoryStream(archiveBytes, writable: false);

		var files = await ArchiveExtractor.ExtractAsync
		(
			stream,
			ArchiveExtractor.ArchiveFormat.Gz,
			extractDir
		);

		Assert.Single(files);
		Assert.Equal("decompressed", Path.GetFileName(files[0]));
		Assert.Equal("stream-gz-content", await File.ReadAllTextAsync(files[0]));
	}

	[Fact]
	public async Task ExtractAsync_StreamBr_DecompressesSingleFile()
	{
		var extractDir = Path.Combine(_artifactRoot, "stream-br-out");
		var archiveBytes = CreateBrotliBytes("stream-br-content"u8.ToArray());
		using var stream = new MemoryStream(archiveBytes, writable: false);

		var files = await ArchiveExtractor.ExtractAsync
		(
			stream,
			ArchiveExtractor.ArchiveFormat.Br,
			extractDir
		);

		Assert.Single(files);
		Assert.Equal("decompressed", Path.GetFileName(files[0]));
		Assert.Equal("stream-br-content", await File.ReadAllTextAsync(files[0]));
	}

	[Fact]
	public async Task ExtractAsync_StreamZl_DecompressesSingleFile()
	{
		var extractDir = Path.Combine(_artifactRoot, "stream-zl-out");
		var archiveBytes = CreateZLibBytes("stream-zl-content"u8.ToArray());
		using var stream = new MemoryStream(archiveBytes, writable: false);

		var files = await ArchiveExtractor.ExtractAsync
		(
			stream,
			ArchiveExtractor.ArchiveFormat.Zl,
			extractDir
		);

		Assert.Single(files);
		Assert.Equal("decompressed", Path.GetFileName(files[0]));
		Assert.Equal("stream-zl-content", await File.ReadAllTextAsync(files[0]));
	}

	[Fact]
	public async Task ExtractAsync_StreamZip_ThrowsNotSupported()
	{
		using var stream = new MemoryStream();

		var exception = await Assert.ThrowsAsync<NotSupportedException>
		(
			() => ArchiveExtractor.ExtractAsync
			(
				stream,
				ArchiveExtractor.ArchiveFormat.Zip,
				Path.Combine(_artifactRoot, "zip-stream-out")
			)
		);

		Assert.Equal("ZIP requires a seekable stream. Use the file-based overload.", exception.Message);
	}

	[Fact]
	public async Task ExtractAsync_StreamUnknown_ThrowsNotSupported()
	{
		using var stream = new MemoryStream();

		var exception = await Assert.ThrowsAsync<NotSupportedException>
		(
			() => ArchiveExtractor.ExtractAsync
			(
				stream,
				ArchiveExtractor.ArchiveFormat.Unknown,
				Path.Combine(_artifactRoot, "unknown-stream-out")
			)
		);

		Assert.Equal("Unsupported archive format: Unknown", exception.Message);
	}

	// ── Error cases ──────────────────────────────────────────────────

	[Fact]
	public async Task ExtractAsync_UnknownFormat_Throws()
	{
		var txtPath = Path.Combine(_artifactRoot, "file.txt");
		await File.WriteAllTextAsync(txtPath, "not an archive");

		await Assert.ThrowsAsync<NotSupportedException>
		(
			() => ArchiveExtractor.ExtractAsync(txtPath, Path.Combine(_artifactRoot, "out"))
		);
	}

	[Fact]
	public async Task ExtractAsync_FileNotFound_Throws()
	{
		await Assert.ThrowsAsync<FileNotFoundException>
		(
			() => ArchiveExtractor.ExtractAsync
			(
				Path.Combine(_artifactRoot, "nonexistent.zip"),
				Path.Combine(_artifactRoot, "out")
			)
		);
	}

	[Fact]
	public async Task ExtractAsync_NullPath_Throws()
	{
		await Assert.ThrowsAsync<ArgumentNullException>
		(
			() => ArchiveExtractor.ExtractAsync(null!, Path.Combine(_artifactRoot, "out"))
		);
	}

	[Theory]
	[InlineData("")]
	[InlineData(" \t ")]
	public async Task ExtractAsync_WhitespacePath_ThrowsArgumentException(string archivePath)
	{
		await Assert.ThrowsAsync<ArgumentException>
		(
			() => ArchiveExtractor.ExtractAsync(archivePath, Path.Combine(_artifactRoot, "out"))
		);
	}

	[Fact]
	public async Task ExtractAsync_EmptyDestDir_Throws()
	{
		var zipPath = Path.Combine(_artifactRoot, "test.zip");
		CreateTestZip(zipPath, ("a.txt", "hi"));

		await Assert.ThrowsAsync<ArgumentException>
		(
			() => ArchiveExtractor.ExtractAsync(zipPath, "")
		);
	}

	// ── Path traversal safety ────────────────────────────────────────

	[Theory]
	[InlineData("../../../etc/passwd", "etc/passwd")]
	[InlineData("/absolute/path/file.txt", "absolute/path/file.txt")]
	[InlineData("normal/path/file.txt", "normal/path/file.txt")]
	[InlineData("./relative/file.txt", "relative/file.txt")]
	[InlineData("..\\..\\windows\\system32\\evil.dll", "windows/system32/evil.dll")]
	[InlineData("//../safe\\mixed/../path\\file.txt", "safe/mixed/path/file.txt")]
	public void SanitizeTarEntryName_PreventsTraversal(string input, string expected)
	{
		var result = ArchiveExtractor.SanitizeTarEntryName(input);
		var normalized = result.Replace('\\', '/');
		Assert.Equal(expected, normalized);
	}

	// ── Magic byte detection ─────────────────────────────────────────

	[Fact]
	public void DetectByMagicBytes_ZipMagicWithUnknownExtension_ReturnsZip()
	{
		var path = Path.Combine(_artifactRoot, "disguised.bin");
		CreateTestZip(path, ("model.onnx", "data"));

		var format = InvokeDetectByMagicBytes(path);

		Assert.Equal(ArchiveExtractor.ArchiveFormat.Zip, format);
	}

	[Fact]
	public async Task DetectByMagicBytes_GzipMagicWithTarContent_ReturnsTarGz()
	{
		var path = Path.Combine(_artifactRoot, "archive.bin");
		var archiveBytes = await CreateTarGzBytesAsync(("model.onnx", "data"u8.ToArray()));
		await File.WriteAllBytesAsync(path, archiveBytes);

		var format = InvokeDetectByMagicBytes(path);

		Assert.Equal(ArchiveExtractor.ArchiveFormat.TarGz, format);
	}

	[Fact]
	public async Task DetectByMagicBytes_GzipMagicWithoutTarContent_ReturnsGz()
	{
		var path = Path.Combine(_artifactRoot, "single.bin");
		await File.WriteAllBytesAsync(path, CreateGZipBytes("data"u8.ToArray()));

		var format = InvokeDetectByMagicBytes(path);

		Assert.Equal(ArchiveExtractor.ArchiveFormat.Gz, format);
	}

	[Fact]
	public async Task DetectByMagicBytes_ZLibHeaderWithTarContent_ReturnsTarZl()
	{
		var path = Path.Combine(_artifactRoot, "archive.z");
		var archiveBytes = await CreateTarZLibBytesAsync(("model.onnx", "data"u8.ToArray()));
		await File.WriteAllBytesAsync(path, archiveBytes);

		var format = InvokeDetectByMagicBytes(path);

		Assert.Equal(ArchiveExtractor.ArchiveFormat.TarZl, format);
	}

	[Fact]
	public async Task DetectByMagicBytes_ZLibHeaderWithSingleFileContent_ReturnsZl()
	{
		var path = Path.Combine(_artifactRoot, "single.z");
		await File.WriteAllBytesAsync(path, CreateZLibBytes("data"u8.ToArray()));

		var format = InvokeDetectByMagicBytes(path);

		Assert.Equal(ArchiveExtractor.ArchiveFormat.Zl, format);
	}

	[Fact]
	public async Task DetectByMagicBytes_TarHeaderAtOffset257_ReturnsTar()
	{
		var path = Path.Combine(_artifactRoot, "archive.dat");
		var archiveBytes = await CreateTarBytesAsync(("model.onnx", "data"u8.ToArray()));
		await File.WriteAllBytesAsync(path, archiveBytes);

		var format = InvokeDetectByMagicBytes(path);

		Assert.Equal(ArchiveExtractor.ArchiveFormat.Tar, format);
	}

	[Fact]
	public async Task DetectByMagicBytes_FileShorterThanTwoBytes_ReturnsUnknown()
	{
		var path = Path.Combine(_artifactRoot, "short.bin");
		await File.WriteAllBytesAsync(path, [0x1F]);

		var format = InvokeDetectByMagicBytes(path);

		Assert.Equal(ArchiveExtractor.ArchiveFormat.Unknown, format);
	}

	[Fact]
	public async Task DetectByMagicBytes_CorruptGzip_ReturnsGz()
	{
		var path = Path.Combine(_artifactRoot, "corrupt.bin");
		await File.WriteAllBytesAsync(path, [0x1F, 0x8B, 0x00, 0x00]);

		var format = InvokeDetectByMagicBytes(path);

		Assert.Equal(ArchiveExtractor.ArchiveFormat.Gz, format);
	}

	[Fact]
	public async Task ExtractAsync_DetectsZipByMagicBytes_WhenExtensionIsWrong()
	{
		var zipPath = Path.Combine(_artifactRoot, "disguised.zip");
		var renamedPath = Path.Combine(_artifactRoot, "disguised.bin");
		var extractDir = Path.Combine(_artifactRoot, "magic-out");

		CreateTestZip(zipPath, ("model.onnx", "data"));
		File.Move(zipPath, renamedPath);

		var files = await ArchiveExtractor.ExtractAsync(renamedPath, extractDir);
		Assert.Single(files);
		Assert.EndsWith("model.onnx", files[0], StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExtractAsync_UnknownFormatWithoutMagicBytes_ThrowsNotSupported()
	{
		var path = Path.Combine(_artifactRoot, "unknown.bin");
		await File.WriteAllTextAsync(path, "plain text");

		var exception = await Assert.ThrowsAsync<NotSupportedException>
		(
			() => ArchiveExtractor.ExtractAsync(path, Path.Combine(_artifactRoot, "unknown-out"))
		);

		Assert.Contains("unknown.bin", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExtractAsync_Tar_PreservesNestedPaths()
	{
		var tarPath = Path.Combine(_artifactRoot, "nested.tar");
		var extractDir = Path.Combine(_artifactRoot, "nested-tar-out");

		await CreateTestTarAsync
		(
			tarPath,
			("models/rvc/voice.pth", "pth"u8.ToArray()),
			("models/rvc/voice.index", "idx"u8.ToArray())
		);

		var files = await ArchiveExtractor.ExtractAsync(tarPath, extractDir);

		Assert.Equal(2, files.Length);
		Assert.All(files, f => Assert.Contains("models", f, StringComparison.Ordinal));
	}

	[Fact]
	public async Task ExtractAsync_Tar_SkipsEntriesWithEmptySanitizedName()
	{
		var tarPath = Path.Combine(_artifactRoot, "empty-name.tar");
		var extractDir = Path.Combine(_artifactRoot, "empty-name-out");

		await CreateTestTarAsync
		(
			tarPath,
			("../", "skip-me"u8.ToArray()),
			("safe/model.onnx", "onnx"u8.ToArray())
		);

		var files = await ArchiveExtractor.ExtractAsync(tarPath, extractDir);

		Assert.Single(files);
		Assert.EndsWith(Path.Combine("safe", "model.onnx"), files[0], StringComparison.Ordinal);
	}

	// ── Single-file naming ───────────────────────────────────────────

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void GetSingleFileOutputName_BlankOrWhitespace_ReturnsDecompressed(string archivePath)
	{
		var outputName = InvokePrivateStatic<string>
		(
			typeof(ArchiveExtractor),
			"GetSingleFileOutputName",
			archivePath
		);

		Assert.Equal("decompressed", outputName);
	}

	[Fact]
	public async Task ExtractSingleFileAsync_WhitespaceOutputName_UsesDecompressedFallback()
	{
		var extractDir = Path.Combine(_artifactRoot, "single-file-fallback");
		Directory.CreateDirectory(extractDir);
		using var stream = new MemoryStream("fallback-content"u8.ToArray(), writable: false);

		var files = await InvokePrivateStaticAsync<string[]>
		(
			typeof(ArchiveExtractor),
			"ExtractSingleFileAsync",
			" \t ",
			stream,
			extractDir,
			CancellationToken.None
		);

		Assert.Single(files);
		Assert.Equal("decompressed", Path.GetFileName(files[0]));
		Assert.Equal("fallback-content", await File.ReadAllTextAsync(files[0]));
	}

	// ── ZLib header heuristics ───────────────────────────────────────

	[Fact]
	public void IsLikelyZLibHeader_LessThanTwoBytes_ReturnsFalse()
	{
		Assert.False(InvokeIsLikelyZLibHeader([0x78], 1));
	}

	[Fact]
	public void IsLikelyZLibHeader_StartsWith0x78AndModuloMatch_ReturnsTrue()
	{
		Assert.True(InvokeIsLikelyZLibHeader([0x78, 0x20], 2));
	}

	[Fact]
	public void IsLikelyZLibHeader_StartsWith0x78AndFailsChecks_ReturnsFalse()
	{
		Assert.False(InvokeIsLikelyZLibHeader([0x78, 0x02], 2));
	}

	[Fact]
	public async Task DetectByMagicBytes_ZLibModuloHeaderWithoutCommonFlag_ReturnsZl()
	{
		var path = Path.Combine(_artifactRoot, "modulo.z");
		await File.WriteAllBytesAsync(path, [0x78, 0x20, 0x03, 0x00]);

		var format = InvokeDetectByMagicBytes(path);

		Assert.Equal(ArchiveExtractor.ArchiveFormat.Zl, format);
	}

	[Fact]
	public async Task DetectByMagicBytes_ZLibLikePrefixFailingChecks_ReturnsUnknown()
	{
		var path = Path.Combine(_artifactRoot, "not-zlib.z");
		await File.WriteAllBytesAsync(path, [0x78, 0x02, 0x03, 0x04]);

		var format = InvokeDetectByMagicBytes(path);

		Assert.Equal(ArchiveExtractor.ArchiveFormat.Unknown, format);
	}

	// ── Helpers ──────────────────────────────────────────────────────

	private static void CreateTestZip(string path, params (string Name, string Content)[] entries)
	{
		using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
		foreach (var (name, content) in entries)
		{
			var entry = zip.CreateEntry(name);
			using var writer = new StreamWriter(entry.Open());
			writer.Write(content);
		}
	}

	private static async Task CreateTestTarAsync(string path, params (string Name, byte[] Content)[] entries)
	{
		var fs = File.Create(path);
		await using var fsDispose = fs.ConfigureAwait(false);
		var writer = new TarWriter(fs);
		await using var writerDispose = writer.ConfigureAwait(false);
		foreach (var (name, content) in entries)
		{
			var entry = new PaxTarEntry(TarEntryType.RegularFile, name);
			entry.DataStream = new MemoryStream(content);
			await writer.WriteEntryAsync(entry).ConfigureAwait(false);
		}
	}

	private static async Task CreateTarWithDirectoryEntryAsync(string path)
	{
		var fs = File.Create(path);
		await using var fsDispose = fs.ConfigureAwait(false);
		var writer = new TarWriter(fs);
		await using var writerDispose = writer.ConfigureAwait(false);

		var dirEntry = new PaxTarEntry(TarEntryType.Directory, "mydir/");
		await writer.WriteEntryAsync(dirEntry).ConfigureAwait(false);

		var fileEntry = new PaxTarEntry(TarEntryType.RegularFile, "mydir/model.onnx");
		fileEntry.DataStream = new MemoryStream("onnx"u8.ToArray());
		await writer.WriteEntryAsync(fileEntry).ConfigureAwait(false);
	}

	private static async Task CreateTestTarGzAsync(string path, params (string Name, byte[] Content)[] entries)
	{
		var fs = File.Create(path);
		await using var fsDispose = fs.ConfigureAwait(false);
		var gz = new GZipStream(fs, CompressionLevel.Fastest);
		await using var gzDispose = gz.ConfigureAwait(false);
		var writer = new TarWriter(gz);
		await using var writerDispose = writer.ConfigureAwait(false);
		foreach (var (name, content) in entries)
		{
			var entry = new PaxTarEntry(TarEntryType.RegularFile, name);
			entry.DataStream = new MemoryStream(content);
			await writer.WriteEntryAsync(entry).ConfigureAwait(false);
		}
	}

	private static void CreateTestGz(string path, byte[] content)
	{
		using var fs = File.Create(path);
		using var gz = new GZipStream(fs, CompressionLevel.Fastest);
		gz.Write(content);
	}

	private static void CreateTestBr(string path, byte[] content)
	{
		using var fs = File.Create(path);
		using var br = new BrotliStream(fs, CompressionLevel.Fastest);
		br.Write(content);
	}

	private static void CreateTestZLib(string path, byte[] content)
	{
		using var fs = File.Create(path);
		using var zl = new ZLibStream(fs, CompressionLevel.Fastest);
		zl.Write(content);
	}

	private static async Task CreateTestTarZLibAsync(string path, params (string Name, byte[] Content)[] entries)
	{
		var tarBytes = await CreateTarBytesAsync(entries).ConfigureAwait(false);
		var fs = File.Create(path);
		await using var fsDispose = fs.ConfigureAwait(false);
		var zl = new ZLibStream(fs, CompressionLevel.Fastest);
		await using var zlDispose = zl.ConfigureAwait(false);
		await zl.WriteAsync(tarBytes).ConfigureAwait(false);
	}

	private static async Task CreateTestTarBrAsync(string path, params (string Name, byte[] Content)[] entries)
	{
		var tarBytes = await CreateTarBytesAsync(entries).ConfigureAwait(false);
		var fs = File.Create(path);
		await using var fsDispose = fs.ConfigureAwait(false);
		var br = new BrotliStream(fs, CompressionLevel.Fastest);
		await using var brDispose = br.ConfigureAwait(false);
		await br.WriteAsync(tarBytes).ConfigureAwait(false);
	}

	private static byte[] CreateGZipBytes(byte[] content)
	{
		using var ms = new MemoryStream();
		using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
		{
			gz.Write(content);
		}

		return ms.ToArray();
	}

	private static byte[] CreateBrotliBytes(byte[] content)
	{
		using var ms = new MemoryStream();
		using (var br = new BrotliStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
		{
			br.Write(content);
		}

		return ms.ToArray();
	}

	private static byte[] CreateZLibBytes(byte[] content)
	{
		using var ms = new MemoryStream();
		using (var zl = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
		{
			zl.Write(content);
		}

		return ms.ToArray();
	}

	private static async Task<byte[]> CreateTarGzBytesAsync(params (string Name, byte[] Content)[] entries)
	{
		using var ms = new MemoryStream();
		{
			var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true);
			await using var gzDispose = gz.ConfigureAwait(false);
			var writer = new TarWriter(gz);
			await using var writerDispose = writer.ConfigureAwait(false);
			foreach (var (name, content) in entries)
			{
				var entry = new PaxTarEntry(TarEntryType.RegularFile, name);
				entry.DataStream = new MemoryStream(content);
				await writer.WriteEntryAsync(entry).ConfigureAwait(false);
			}
		}

		return ms.ToArray();
	}

	private static async Task<byte[]> CreateTarBrBytesAsync(params (string Name, byte[] Content)[] entries)
	{
		var tarBytes = await CreateTarBytesAsync(entries).ConfigureAwait(false);
		return CreateBrotliBytes(tarBytes);
	}

	private static async Task<byte[]> CreateTarZLibBytesAsync(params (string Name, byte[] Content)[] entries)
	{
		var tarBytes = await CreateTarBytesAsync(entries).ConfigureAwait(false);
		return CreateZLibBytes(tarBytes);
	}

	private static async Task<byte[]> CreateTarBytesAsync(params (string Name, byte[] Content)[] entries)
	{
		using var ms = new MemoryStream();
		{
			var writer = new TarWriter(ms, leaveOpen: true);
			await using var writerDispose = writer.ConfigureAwait(false);
			foreach (var (name, content) in entries)
			{
				var entry = new PaxTarEntry(TarEntryType.RegularFile, name);
				entry.DataStream = new MemoryStream(content);
				await writer.WriteEntryAsync(entry).ConfigureAwait(false);
			}
		}

		return ms.ToArray();
	}

	private static ArchiveExtractor.ArchiveFormat InvokeDetectByMagicBytes(string path)
	{
		return InvokePrivateStatic<ArchiveExtractor.ArchiveFormat>
		(
			typeof(ArchiveExtractor),
			"DetectByMagicBytes",
			path
		);
	}

	private static bool InvokeIsLikelyZLibHeader(ReadOnlySpan<byte> header, int bytesRead)
	{
		var method = typeof(ArchiveExtractor).GetMethod("IsLikelyZLibHeader", BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(method);

		var del = method!.CreateDelegate<IsLikelyZLibHeaderDelegate>();
		return del(header, bytesRead);
	}

	private static T InvokePrivateStatic<T>(Type type, string methodName, params object?[] args)
	{
		var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(method);

		var result = method!.Invoke(null, args);
		Assert.NotNull(result);
		return Assert.IsType<T>(result);
	}

	private static async Task<T> InvokePrivateStaticAsync<T>(Type type, string methodName, params object?[] args)
	{
		var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(method);

		var result = method!.Invoke(null, args);
		var task = Assert.IsAssignableFrom<Task<T>>(result);
		return await task.ConfigureAwait(false);
	}

	private delegate bool IsLikelyZLibHeaderDelegate(ReadOnlySpan<byte> header, int bytesRead);
}
