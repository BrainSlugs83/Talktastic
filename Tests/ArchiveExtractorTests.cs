using System.Formats.Tar;
using System.IO.Compression;

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
	[InlineData("MODEL.ZIP", true)]
	[InlineData("model.TAR.GZ", true)]
	[InlineData("model.TGZ", true)]
	[InlineData("https://example.com/models/voice.zip", true)]
	[InlineData("https://example.com/models/voice.tar.gz", true)]
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
	public void SanitizeTarEntryName_PreventsTraversal(string input, string expected)
	{
		var result = ArchiveExtractor.SanitizeTarEntryName(input);
		var normalized = result.Replace('\\', '/');
		Assert.Equal(expected, normalized);
	}

	// ── Magic byte detection ─────────────────────────────────────────

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
}
