using Microsoft.ML.OnnxRuntime;

namespace Talktastic.Tests;

/// <summary>
/// Tests for RvcEngine: skeleton loading, session creation, model enumeration,
/// and the full .pth → ONNX patching → ORT session pipeline.
/// </summary>
public sealed class RvcEngineTests : IDisposable
{
	private readonly string _tempDir;

	public RvcEngineTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"rvc-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			Directory.Delete(_tempDir, recursive: true);
		}

		
	}

	// ── Skeleton loading ──────────────────────────────────────────────

	[Theory]
	[InlineData("32k")]
	[InlineData("40k")]
	[InlineData("48k")]
	public void LoadEmbeddedSkeleton_ReturnsNonEmptyBytes(string srKey)
	{
		var bytes = RvcEngine.LoadEmbeddedSkeleton(srKey);
		Assert.NotNull(bytes);
		Assert.True(bytes.Length > 1_000_000, $"Skeleton {srKey} should be >1MB, was {bytes.Length}");
	}

	[Fact]
	public void LoadEmbeddedSkeleton_InvalidKey_Throws()
	{
		Assert.Throws<InvalidOperationException>(() => RvcEngine.LoadEmbeddedSkeleton("99k"));
	}

	[Theory]
	[InlineData("32k")]
	[InlineData("40k")]
	[InlineData("48k")]
	public void LoadEmbeddedManifest_ReturnsValidManifest(string srKey)
	{
		var manifest = RvcEngine.LoadEmbeddedManifest(srKey);
		Assert.NotNull(manifest);
		Assert.NotNull(manifest.Initializers);
		Assert.NotEmpty(manifest.Initializers);
		Assert.NotNull(manifest.PthToOnnx);
	}

	[Fact]
	public void LoadEmbeddedManifest_InvalidKey_Throws()
	{
		Assert.Throws<InvalidOperationException>(() => RvcEngine.LoadEmbeddedManifest("99k"));
	}

	// ── Skeleton ORT session creation (unpatched) ─────────────────────

	[Theory]
	[InlineData("40k")]
	[InlineData("48k")]
	public void UnpatchedSkeleton_CanLoadInOrt(string srKey)
	{
		var bytes = RvcEngine.LoadEmbeddedSkeleton(srKey);
		using var options = new SessionOptions();
		options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
		using var session = new InferenceSession(bytes, options);

		Assert.NotEmpty(session.InputMetadata);
		Assert.NotEmpty(session.OutputMetadata);
	}

	[Fact]
	public void UnpatchedSkeleton32k_CanLoadInOrt()
	{
		// This is the critical test -- if the 32k skeleton can't even load
		// unpatched, the skeleton itself is corrupt/incompatible.
		var bytes = RvcEngine.LoadEmbeddedSkeleton("32k");
		using var options = new SessionOptions();
		options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
		using var session = new InferenceSession(bytes, options);

		Assert.NotEmpty(session.InputMetadata);
		Assert.NotEmpty(session.OutputMetadata);
	}

	// ── Skeleton patching consistency ──────────────────────────────────

	[Theory]
	[InlineData("32k")]
	[InlineData("40k")]
	[InlineData("48k")]
	public void Skeleton_InitializerOffsets_MatchManifest(string srKey)
	{
		var bytes = RvcEngine.LoadEmbeddedSkeleton(srKey);
		var manifest = RvcEngine.LoadEmbeddedManifest(srKey);
		var offsets = OnnxPatcher.FindInitializerOffsets(bytes);

		// Every manifest initializer should have a corresponding offset in the ONNX
		foreach (var (name, _) in manifest.Initializers)
		{
			Assert.True
			(
				offsets.ContainsKey(name),
				$"Manifest initializer '{name}' not found in ONNX offsets for {srKey}"
			);
		}
	}

	[Theory]
	[InlineData("32k")]
	[InlineData("40k")]
	[InlineData("48k")]
	public void Skeleton_ManifestShapes_MatchOnnxSizes(string srKey)
	{
		var bytes = RvcEngine.LoadEmbeddedSkeleton(srKey);
		var manifest = RvcEngine.LoadEmbeddedManifest(srKey);
		var offsets = OnnxPatcher.FindInitializerOffsets(bytes);

		foreach (var (name, shape) in manifest.Initializers)
		{
			if (!offsets.TryGetValue(name, out var region))
			{
				continue;
			}

			// Calculate expected size from manifest shape (float16 = 2 bytes per element)
			long expectedElements = 1;
			foreach (var dim in shape)
			{
				expectedElements *= dim;
			}

			// float16 = 2 bytes
			var expectedBytes = expectedElements * 2;
			Assert.True
			(
				region.Length == expectedBytes,
				$"Size mismatch for '{name}' in {srKey}: manifest says {expectedBytes} bytes "
				+ $"({string.Join("x", shape)} × 2), ONNX region is {region.Length} bytes"
			);
		}
	}

	// ── CreatePthSession with real 32k model ──────────────────────────

	[Fact]
	public void CreatePthSession_32kModel_DoesNotCrash()
	{
		// Find a cached 32k model (Bart Simpson)
		var bartPath = Path.Combine
		(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			@"Talktastic\.rvc\voices\BartSimpson_e230_s7360\BartSimpson_e230_s7360.pth"
		);

		if (!File.Exists(bartPath))
		{
			// Skip if model not cached locally
			return;
		}

		// This should NOT crash with 0xC0000005
		var (session, sampleRate) = RvcEngine.CreatePthSession(bartPath);
		using (session)
		{
			Assert.NotNull(session);
			Assert.True(sampleRate > 0);
			Assert.NotEmpty(session.InputMetadata);
		}
	}

	// ── EnumerateCachedModels ─────────────────────────────────────────

	[Fact]
	public void EnumerateCachedModels_EmptyDir_ReturnsNothing()
	{
		var results = RvcEngine.EnumerateCachedModels(_tempDir).ToList();
		Assert.Empty(results);
	}

	[Fact]
	public void EnumerateCachedModels_NonexistentDir_ReturnsNothing()
	{
		var results = RvcEngine.EnumerateCachedModels(Path.Combine(_tempDir, "nope")).ToList();
		Assert.Empty(results);
	}

	[Fact]
	public void EnumerateCachedModels_SubdirWithPth_Found()
	{
		var modelDir = Path.Combine(_tempDir, "testmodel");
		Directory.CreateDirectory(modelDir);
		File.WriteAllBytes(Path.Combine(modelDir, "testmodel.pth"), [0x80]);

		var results = RvcEngine.EnumerateCachedModels(_tempDir).ToList();
		Assert.Single(results);
		Assert.Equal("testmodel", results[0].Name);
		Assert.EndsWith(".pth", results[0].Path, StringComparison.Ordinal);
	}

	[Fact]
	public void EnumerateCachedModels_SubdirWithOnnx_Found()
	{
		var modelDir = Path.Combine(_tempDir, "myvoice");
		Directory.CreateDirectory(modelDir);
		File.WriteAllBytes(Path.Combine(modelDir, "myvoice.onnx"), [0x08]);

		var results = RvcEngine.EnumerateCachedModels(_tempDir).ToList();
		Assert.Single(results);
		Assert.Equal("myvoice", results[0].Name);
		Assert.EndsWith(".onnx", results[0].Path, StringComparison.Ordinal);
	}

	[Fact]
	public void EnumerateCachedModels_SubdirWithOnnxAndPth_PrefersOnnx()
	{
		var modelDir = Path.Combine(_tempDir, "dual");
		Directory.CreateDirectory(modelDir);
		File.WriteAllBytes(Path.Combine(modelDir, "model.onnx"), [0x08]);
		File.WriteAllBytes(Path.Combine(modelDir, "model.pth"), [0x80]);

		var results = RvcEngine.EnumerateCachedModels(_tempDir).ToList();
		Assert.Single(results);
		Assert.EndsWith(".onnx", results[0].Path, StringComparison.Ordinal);
	}

	[Fact]
	public void EnumerateCachedModels_SubdirWithNoModel_Skipped()
	{
		var modelDir = Path.Combine(_tempDir, "junk");
		Directory.CreateDirectory(modelDir);
		File.WriteAllBytes(Path.Combine(modelDir, "readme.txt"), "hi"u8.ToArray());

		var results = RvcEngine.EnumerateCachedModels(_tempDir).ToList();
		Assert.Empty(results);
	}

	[Fact]
	public void EnumerateCachedModels_LegacyFlatPth_Found()
	{
		File.WriteAllBytes(Path.Combine(_tempDir, "legacy.pth"), [0x80]);

		var results = RvcEngine.EnumerateCachedModels(_tempDir).ToList();
		Assert.Single(results);
		Assert.Equal("legacy", results[0].Name);
	}

	[Fact]
	public void EnumerateCachedModels_LegacyFlatNonModel_Ignored()
	{
		File.WriteAllBytes(Path.Combine(_tempDir, "notes.txt"), "hi"u8.ToArray());

		var results = RvcEngine.EnumerateCachedModels(_tempDir).ToList();
		Assert.Empty(results);
	}

	[Fact]
	public void EnumerateCachedModels_MultipleModels_AllFound()
	{
		// Two subdirs + one legacy flat file
		var dir1 = Path.Combine(_tempDir, "alpha");
		Directory.CreateDirectory(dir1);
		File.WriteAllBytes(Path.Combine(dir1, "alpha.pth"), [0x80]);

		var dir2 = Path.Combine(_tempDir, "beta");
		Directory.CreateDirectory(dir2);
		File.WriteAllBytes(Path.Combine(dir2, "beta.onnx"), [0x08]);

		File.WriteAllBytes(Path.Combine(_tempDir, "gamma.pth"), [0x80]);

		var results = RvcEngine.EnumerateCachedModels(_tempDir).ToList();
		Assert.Equal(3, results.Count);
		var names = results.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
		Assert.Equal(["alpha", "beta", "gamma"], names);
	}
}

