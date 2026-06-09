using Microsoft.ML.OnnxRuntime;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Talktastic.Tests;

#pragma warning disable CA1814
#pragma warning disable CA1861

[Collection("AppPaths")]
public sealed class RvcEngineTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string[] _originalSearchBases;

	public RvcEngineTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"rvc-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_originalSearchBases = [.. GetSearchBases()];
	}

	public void Dispose()
	{
		var searchBases = GetSearchBases();
		Array.Copy(_originalSearchBases, searchBases, _originalSearchBases.Length);

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
		Assert.True
		(
			HasEmbeddedRvcResource($"Talktastic.Rvc.skeleton_v2_{srKey}.onnx.gz"),
			$"Required embedded skeleton resource missing for {srKey}. Rebuild with skeletons in native-resources/."
		);

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
		Assert.True
		(
			HasEmbeddedRvcResource($"Talktastic.Rvc.skeleton_v2_{srKey}_manifest.json.gz"),
			$"Required embedded manifest resource missing for {srKey}. Rebuild with skeletons in native-resources/."
		);

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
		Assert.True
		(
			HasEmbeddedRvcResource($"Talktastic.Rvc.skeleton_v2_{srKey}.onnx.gz"),
			$"Required embedded skeleton resource missing for {srKey}."
		);

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
		Assert.True
		(
			HasEmbeddedRvcResource("Talktastic.Rvc.skeleton_v2_32k.onnx.gz"),
			"Required embedded 32k skeleton resource missing."
		);

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
		Assert.True
		(
			HasEmbeddedRvcResource($"Talktastic.Rvc.skeleton_v2_{srKey}.onnx.gz")
			&& HasEmbeddedRvcResource($"Talktastic.Rvc.skeleton_v2_{srKey}_manifest.json.gz"),
			$"Required embedded skeleton/manifest resources missing for {srKey}."
		);

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
		Assert.True
		(
			HasEmbeddedRvcResource($"Talktastic.Rvc.skeleton_v2_{srKey}.onnx.gz")
			&& HasEmbeddedRvcResource($"Talktastic.Rvc.skeleton_v2_{srKey}_manifest.json.gz"),
			$"Required embedded skeleton/manifest resources missing for {srKey}."
		);

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

		Assert.True
		(
			HasEmbeddedRvcResource("Talktastic.Rvc.skeleton_v2_32k.onnx.gz")
			&& HasEmbeddedRvcResource("Talktastic.Rvc.skeleton_v2_32k_manifest.json.gz"),
			"Required embedded 32k skeleton/manifest resources missing."
		);

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

	// ── Display names and cache lookup helpers ─────────────────────────

	[Fact]
	public void GetDisplayName_ModelInsideVoiceSubdir_UsesFolderName()
	{
		var baseDir = Path.Combine(_tempDir, "cache-root");
		var voicesDir = CreateVoicesDir(baseDir);
		var modelDir = Path.Combine(voicesDir, "MyVoice");
		Directory.CreateDirectory(modelDir);
		var modelPath = Path.Combine(modelDir, "model.onnx");
		File.WriteAllBytes(modelPath, [0x08]);
		SetSearchBases(baseDir);

		var displayName = RvcEngine.GetDisplayName(modelPath);

		Assert.Equal("MyVoice", displayName);
	}

	[Fact]
	public void GetDisplayName_ModelDirectlyInVoicesDir_UsesFileName()
	{
		var baseDir = Path.Combine(_tempDir, "cache-root");
		var voicesDir = CreateVoicesDir(baseDir);
		var modelPath = Path.Combine(voicesDir, "flat-model.onnx");
		File.WriteAllBytes(modelPath, [0x08]);
		SetSearchBases(baseDir);

		var displayName = RvcEngine.GetDisplayName(modelPath);

		Assert.Equal("flat-model", displayName);
	}

	[Fact]
	public void GetDisplayName_NoVoicesDir_UsesFileName()
	{
		SetSearchBases(Path.Combine(_tempDir, "missing-root"));
		var modelPath = Path.Combine(_tempDir, "LooseModel.pth");

		var displayName = RvcEngine.GetDisplayName(modelPath);

		Assert.Equal("LooseModel", displayName);
	}

	[Theory]
	[InlineData(@"C:\models\voice.onnx", true)]
	[InlineData(@"models\voice.pth", true)]
	[InlineData("voice.onnx", true)]
	[InlineData("voice.pth", true)]
	[InlineData("just-a-name", false)]
	[InlineData("https://example.com/model.onnx", false)]
	[InlineData("bart simpson", false)]
	public void LooksLikeLocalPath_ClassifiesCorrectly(string query, bool expected)
	{
		var result = RvcEngine.LooksLikeLocalPath(query);

		Assert.Equal(expected, result);
	}

	[Fact]
	public void FindCachedModelExact_ExactNameMatch_ReturnsModelFile()
	{
		var voicesDir = CreateVoicesDir(Path.Combine(_tempDir, "cached-exact"));
		var modelDir = Path.Combine(voicesDir, "BartSimpson_e230_s7360");
		Directory.CreateDirectory(modelDir);
		var modelPath = Path.Combine(modelDir, "BartSimpson_e230_s7360.pth");
		File.WriteAllBytes(modelPath, [0x80]);

		var result = RvcEngine.FindCachedModelExact(voicesDir, "BartSimpson_e230_s7360");

		Assert.Equal(modelPath, result);
	}

	[Fact]
	public void FindCachedModelExact_NoMatch_ReturnsNull()
	{
		var voicesDir = CreateVoicesDir(Path.Combine(_tempDir, "cached-exact-miss"));
		var modelDir = Path.Combine(voicesDir, "alpha");
		Directory.CreateDirectory(modelDir);
		File.WriteAllBytes(Path.Combine(modelDir, "alpha.pth"), [0x80]);

		var result = RvcEngine.FindCachedModelExact(voicesDir, "missing");

		Assert.Null(result);
	}

	[Fact]
	public void FindCachedModel_FuzzyMatch_FindsModel()
	{
		var voicesDir = CreateVoicesDir(Path.Combine(_tempDir, "cached-fuzzy"));
		var modelDir = Path.Combine(voicesDir, "en_US-ryan-high");
		Directory.CreateDirectory(modelDir);
		var modelPath = Path.Combine(modelDir, "en_US-ryan-high.onnx");
		File.WriteAllBytes(modelPath, [0x08]);

		var result = RvcEngine.FindCachedModel(voicesDir, "ryan");

		Assert.Equal(modelPath, result);
	}

	[Fact]
	public void FindCachedModel_NoMatch_ReturnsNull()
	{
		var voicesDir = CreateVoicesDir(Path.Combine(_tempDir, "cached-fuzzy-miss"));
		var modelDir = Path.Combine(voicesDir, "en_US-ryan-high");
		Directory.CreateDirectory(modelDir);
		File.WriteAllBytes(Path.Combine(modelDir, "en_US-ryan-high.onnx"), [0x08]);

		var result = RvcEngine.FindCachedModel(voicesDir, "definitely-not-here");

		Assert.Null(result);
	}

	[Fact]
	public void FindModelFileInDir_PrefersOnnxOverPth()
	{
		var modelDir = Path.Combine(_tempDir, "mixed");
		Directory.CreateDirectory(modelDir);
		var onnxPath = Path.Combine(modelDir, "voice.onnx");
		var pthPath = Path.Combine(modelDir, "voice.pth");
		File.WriteAllBytes(onnxPath, [0x08]);
		File.WriteAllBytes(pthPath, [0x80]);

		var result = RvcEngine.FindModelFileInDir(modelDir);

		Assert.Equal(onnxPath, result);
	}

	[Fact]
	public void FindModelFileInDir_SkipsCachedOnnx_ReturnsPth()
	{
		var modelDir = Path.Combine(_tempDir, "cached-onnx");
		Directory.CreateDirectory(modelDir);
		File.WriteAllBytes(Path.Combine(modelDir, "voice.cached.onnx"), [0x08]);
		File.WriteAllBytes(Path.Combine(modelDir, "voice.pth"), [0x80]);

		var result = RvcEngine.FindModelFileInDir(modelDir);

		// .pth preferred over .cached.onnx when both exist
		Assert.Equal(Path.Combine(modelDir, "voice.pth"), result);
	}

	[Fact]
	public void FindModelFileInDir_CachedOnnxWithValidMeta_UsedAsLastResort()
	{
		var modelDir = Path.Combine(_tempDir, "only-cached-valid");
		Directory.CreateDirectory(modelDir);
		File.WriteAllBytes(Path.Combine(modelDir, "voice.cached.onnx"), [0x08]);
		File.WriteAllText(Path.Combine(modelDir, "voice.cached.meta"), "32000");

		var result = RvcEngine.FindModelFileInDir(modelDir);

		Assert.Equal(Path.Combine(modelDir, "voice.cached.onnx"), result);
	}

	[Fact]
	public void FindModelFileInDir_CachedOnnxWithoutMeta_ReturnsNull()
	{
		var modelDir = Path.Combine(_tempDir, "only-cached-no-meta");
		Directory.CreateDirectory(modelDir);
		File.WriteAllBytes(Path.Combine(modelDir, "voice.cached.onnx"), [0x08]);

		var result = RvcEngine.FindModelFileInDir(modelDir);

		Assert.Null(result);
	}

	[Fact]
	public void FindModelFileInDir_CachedOnnxWithBadMeta_ReturnsNull()
	{
		var modelDir = Path.Combine(_tempDir, "only-cached-bad-meta");
		Directory.CreateDirectory(modelDir);
		File.WriteAllBytes(Path.Combine(modelDir, "voice.cached.onnx"), [0x08]);
		File.WriteAllText(Path.Combine(modelDir, "voice.cached.meta"), "garbage");

		var result = RvcEngine.FindModelFileInDir(modelDir);

		Assert.Null(result);
	}

	[Fact]
	public void FindModelFileInDir_WithoutModelFiles_ReturnsNull()
	{
		var modelDir = Path.Combine(_tempDir, "empty-model-dir");
		Directory.CreateDirectory(modelDir);
		File.WriteAllText(Path.Combine(modelDir, "readme.txt"), "hi");

		var result = RvcEngine.FindModelFileInDir(modelDir);

		Assert.Null(result);
	}

	[Fact]
	public void FindCompanionIndex_WithSupportedIndex_ReturnsIndexPath()
	{
		var modelDir = Path.Combine(_tempDir, "voice");
		Directory.CreateDirectory(modelDir);
		var modelPath = Path.Combine(modelDir, "voice.pth");
		var indexPath = Path.Combine(modelDir, "voice.index");
		File.WriteAllBytes(modelPath, [0x80]);
		File.WriteAllBytes(indexPath, BuildValidIndexBytes());

		var result = RvcEngine.FindCompanionIndex(modelPath);

		Assert.Equal(indexPath, result);
	}

	[Fact]
	public void FindCompanionIndex_WithUnsupportedIndex_ReturnsNull()
	{
		var modelDir = Path.Combine(_tempDir, "voice-bad-idx");
		Directory.CreateDirectory(modelDir);
		var modelPath = Path.Combine(modelDir, "voice.pth");
		var indexPath = Path.Combine(modelDir, "voice.index");
		File.WriteAllBytes(modelPath, [0x80]);
		File.WriteAllBytes(indexPath, [0xDE, 0xAD, 0xBE, 0xEF]);

		var result = RvcEngine.FindCompanionIndex(modelPath);

		Assert.Null(result);
	}

	[Fact]
	public void FindCompanionIndex_MissingDirectory_ReturnsNull()
	{
		var modelPath = Path.Combine(_tempDir, "missing", "ghost.onnx");

		var result = RvcEngine.FindCompanionIndex(modelPath);

		Assert.Null(result);
	}

	[Fact]
	public void GetCachedModels_NoVoicesDirectory_ReturnsEmpty()
	{
		SetSearchBases(Path.Combine(_tempDir, "missing-a"), Path.Combine(_tempDir, "missing-b"));

		var models = RvcEngine.GetCachedModels();

		Assert.Empty(models);
	}

	[Fact]
	public void GetCachedModels_UsesFirstExistingVoicesDirectoryOnly()
	{
		var primaryRoot = Path.Combine(_tempDir, "primary");
		var secondaryRoot = Path.Combine(_tempDir, "secondary");
		var primaryVoices = CreateVoicesDir(primaryRoot);
		var secondaryVoices = CreateVoicesDir(secondaryRoot);

		var primaryModelDir = Path.Combine(primaryVoices, "alpha");
		Directory.CreateDirectory(primaryModelDir);
		var primaryModelPath = Path.Combine(primaryModelDir, "alpha.pth");
		CreateSizedFile(primaryModelPath, (2 * 1024 * 1024) + 123);
		File.WriteAllBytes(Path.Combine(primaryModelDir, "alpha.index"), BuildValidIndexBytes());

		File.WriteAllBytes(Path.Combine(primaryVoices, "beta.onnx"), new byte[1024]);

		var secondaryModelDir = Path.Combine(secondaryVoices, "ignored");
		Directory.CreateDirectory(secondaryModelDir);
		File.WriteAllBytes(Path.Combine(secondaryModelDir, "ignored.pth"), [0x80]);

		SetSearchBases(primaryRoot, secondaryRoot, Path.Combine(_tempDir, "fallback"));

		var models = RvcEngine.GetCachedModels()
			.OrderBy(m => m.Name, StringComparer.Ordinal)
			.ToList();

		Assert.Equal(2, models.Count);

		Assert.Equal("alpha", models[0].Name);
		Assert.Equal("pth", models[0].Extension);
		Assert.Equal(2, models[0].SizeMb);
		Assert.True(models[0].HasIndex);

		Assert.Equal("beta", models[1].Name);
		Assert.Equal("onnx", models[1].Extension);
		Assert.Equal(0, models[1].SizeMb);
		Assert.False(models[1].HasIndex);
	}

	[Fact]
	public void GetCachedModels_UnsupportedIndexFormat_ReportsNoIndex()
	{
		var root = Path.Combine(_tempDir, "unsupported-idx");
		var voicesDir = CreateVoicesDir(root);

		var modelDir = Path.Combine(voicesDir, "gamma");
		Directory.CreateDirectory(modelDir);
		CreateSizedFile(Path.Combine(modelDir, "gamma.pth"), 1024 * 1024);
		File.WriteAllBytes(Path.Combine(modelDir, "gamma.index"), [0xBA, 0xAD, 0xF0, 0x0D]);

		SetSearchBases(root);

		var models = RvcEngine.GetCachedModels();

		Assert.Single(models);
		Assert.Equal("gamma", models[0].Name);
		Assert.False(models[0].HasIndex);
	}

	[Theory]
	[InlineData("voice.pth", true)]
	[InlineData("VOICE.PTH", true)]
	[InlineData("voice.onnx", false)]
	[InlineData("voice.pth.bak", false)]
	public void IsPthFile_MatchesExpectedPaths(string path, bool expected)
	{
		var result = RvcEngine.IsPthFile(path);

		Assert.Equal(expected, result);
	}

	[Theory]
	[InlineData("voice.cached.onnx", true)]
	[InlineData("VOICE.CACHED.ONNX", true)]
	[InlineData("voice.onnx", false)]
	[InlineData("voice.pth", false)]
	[InlineData("cached.onnx", false)]
	public void IsCachedOnnxFile_MatchesExpectedPaths(string path, bool expected)
	{
		var result = RvcEngine.IsCachedOnnxFile(path);

		Assert.Equal(expected, result);
	}

	[Theory]
	[InlineData(@"C:\models\voice.cached.onnx", @"C:\models\voice.cached.meta")]
	[InlineData(@"D:\foo\bar.cached.onnx", @"D:\foo\bar.cached.meta")]
	public void GetCachedMetaPath_ReturnsCorrectPath(string input, string expected)
	{
		var result = RvcEngine.GetCachedMetaPath(input);

		Assert.Equal(expected, result);
	}

	[Fact]
	public void HasValidCachedMeta_WithValidMeta_ReturnsTrue()
	{
		var onnxPath = Path.Combine(_tempDir, "test.cached.onnx");
		var metaPath = Path.Combine(_tempDir, "test.cached.meta");
		File.WriteAllBytes(onnxPath, [0x00]);
		File.WriteAllText(metaPath, "32000");

		Assert.True(RvcEngine.HasValidCachedMeta(onnxPath));
	}

	[Fact]
	public void HasValidCachedMeta_WithMissingMeta_ReturnsFalse()
	{
		var onnxPath = Path.Combine(_tempDir, "missing-meta.cached.onnx");
		File.WriteAllBytes(onnxPath, [0x00]);

		Assert.False(RvcEngine.HasValidCachedMeta(onnxPath));
	}

	[Theory]
	[InlineData("garbage")]
	[InlineData("")]
	[InlineData("0")]
	[InlineData("-1")]
	public void HasValidCachedMeta_WithInvalidMeta_ReturnsFalse(string content)
	{
		var name = $"bad-meta-{Guid.NewGuid():N}";
		var onnxPath = Path.Combine(_tempDir, $"{name}.cached.onnx");
		var metaPath = Path.Combine(_tempDir, $"{name}.cached.meta");
		File.WriteAllBytes(onnxPath, [0x00]);
		File.WriteAllText(metaPath, content);

		Assert.False(RvcEngine.HasValidCachedMeta(onnxPath));
	}

	[Fact]
	public void ReadCachedMetaSampleRate_WithValidMeta_ReturnsRate()
	{
		var onnxPath = Path.Combine(_tempDir, "sr-test.cached.onnx");
		var metaPath = Path.Combine(_tempDir, "sr-test.cached.meta");
		File.WriteAllBytes(onnxPath, [0x00]);
		File.WriteAllText(metaPath, "48000");

		var sr = RvcEngine.ReadCachedMetaSampleRate(onnxPath);

		Assert.Equal(48000, sr);
	}

	[Fact]
	public void ReadCachedMetaSampleRate_WithMissingMeta_ReturnsDefault()
	{
		var onnxPath = Path.Combine(_tempDir, "no-meta.cached.onnx");
		File.WriteAllBytes(onnxPath, [0x00]);

		var sr = RvcEngine.ReadCachedMetaSampleRate(onnxPath);

		Assert.Equal(40000, sr); // DefaultTargetSampleRate
	}

	[Fact]
	public void GetCachedModels_CachedOnnxWithMeta_IncludesModel()
	{
		var root = Path.Combine(_tempDir, "cached-model");
		var voicesDir = CreateVoicesDir(root);
		var modelDir = Path.Combine(voicesDir, "delta");
		Directory.CreateDirectory(modelDir);
		CreateSizedFile(Path.Combine(modelDir, "delta.cached.onnx"), 1024 * 1024);
		File.WriteAllText(Path.Combine(modelDir, "delta.cached.meta"), "32000");

		SetSearchBases(root);

		var models = RvcEngine.GetCachedModels();

		Assert.Single(models);
		Assert.Equal("delta", models[0].Name);
		Assert.EndsWith(".cached.onnx", models[0].ModelPath, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void GetCachedModels_CachedOnnxWithoutMeta_ExcludesModel()
	{
		var root = Path.Combine(_tempDir, "orphan-cached");
		var voicesDir = CreateVoicesDir(root);
		var modelDir = Path.Combine(voicesDir, "orphan");
		Directory.CreateDirectory(modelDir);
		CreateSizedFile(Path.Combine(modelDir, "orphan.cached.onnx"), 1024 * 1024);

		SetSearchBases(root);

		var models = RvcEngine.GetCachedModels();

		Assert.Empty(models);
	}

	// ── Pure DSP / tensor helpers ──────────────────────────────────────

	[Fact]
	public void NormalizePeak_WhenAlreadyBelowTarget_ReturnsSameArray()
	{
		var samples = new[] { -0.4f, 0.2f, 0.4f };

		var normalized = RvcEngine.NormalizePeak(samples, 0.95f);

		Assert.Same(samples, normalized);
		Assert.Equal(samples, normalized);
	}

	[Fact]
	public void NormalizePeak_WhenPeakExceedsTarget_ScalesDown()
	{
		var samples = new[] { -2.0f, 0.5f, 1.0f };

		var normalized = RvcEngine.NormalizePeak(samples, 0.5f);

		Assert.NotSame(samples, normalized);
		AssertEqualWithin([-0.5f, 0.125f, 0.25f], normalized);
	}

	[Fact]
	public void NormalizePeak_EmptyInput_ReturnsSameEmptyArray()
	{
		var samples = Array.Empty<float>();

		var normalized = RvcEngine.NormalizePeak(samples, 0.75f);

		Assert.Same(samples, normalized);
		Assert.Empty(normalized);
	}

	[Theory]
	[InlineData(16000, 16000)]
	[InlineData(16000, 32000)]
	public void MatchRms_WhenSignalsDifferByConstantScale_AppliesExpectedGain(int sourceSampleRate, int outputSampleRate)
	{
		var sourceLength = sourceSampleRate / 5;
		var outputLength = outputSampleRate / 5;
		var sourceAudio = Enumerable.Repeat(0.8f, sourceLength).ToArray();
		var outputAudio = Enumerable.Repeat(0.2f, outputLength).ToArray();
		var expectedValue = 0.2f * MathF.Pow(4.0f, 1.0f - 0.25f);

		var matched = RvcEngine.MatchRms(sourceAudio, sourceSampleRate, outputAudio, outputSampleRate);

		Assert.All
		(
			matched,
			sample => Assert.InRange(sample, expectedValue - 1.0e-4f, expectedValue + 1.0e-4f)
		);
	}

	[Fact]
	public void MatchRms_EmptyOutput_ReturnsEmpty()
	{
		var matched = RvcEngine.MatchRms(new float[] { 1.0f, 1.0f }, 16000, Array.Empty<float>(), 16000);

		Assert.Empty(matched);
	}

	[Theory]
	[InlineData(0.0f, 0.0f)]
	[InlineData(1200.0f, 20.0f)]
	[InlineData(2400.0f, 40.0f)]
	[InlineData(-1200.0f, 5.0f)]
	public void DecodeF0_ConvertsCentsToExpectedHz(float centsValue, float expectedHz)
	{
		var decoded = RvcEngine.DecodeF0([centsValue]);

		AssertEqualWithin([expectedHz], decoded);
	}

	[Fact]
	public void DecodeF0_EmptyInput_ReturnsEmpty()
	{
		var decoded = RvcEngine.DecodeF0(Array.Empty<float>());

		Assert.Empty(decoded);
	}

	[Fact]
	public void DecodeLocalAverageCents_BelowThreshold_ReturnsZero()
	{
		var salience = new float[,]
		{
			{ 0.01f, 0.02f, 0.03f },
		};

		var cents = RvcEngine.DecodeLocalAverageCents(salience, 0.03f);

		AssertEqualWithin([0.0f], cents);
	}

	[Fact]
	public void DecodeLocalAverageCents_UsesWeightedAverageAroundPeak()
	{
		var salience = new float[,]
		{
			{ 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.2f, 1.0f, 0.8f, 0.0f },
		};
		var expected = ((0.2f * MapCents(9)) + (1.0f * MapCents(10)) + (0.8f * MapCents(11))) / 2.0f;

		var cents = RvcEngine.DecodeLocalAverageCents(salience, 0.03f);

		AssertEqualWithin([expected], cents);
	}

	[Fact]
	public void QuantizePitch_ClampsAndRoundsToExpectedBuckets()
	{
		var quantized = RvcEngine.QuantizePitch([0.0f, 50.0f, 440.0f, 1100.0f, 5000.0f]);

		Assert.Equal([1L, 1L, QuantizeExpected(440.0f), 255L, 255L], quantized);
	}

	[Fact]
	public void FindOptimalTimestamps_ShortAudio_ReturnsNoSegmentsAndPadsForInference()
	{
		var filtered = Enumerable.Range(0, 1000).Select(static i => (float)MathF.Sin(i)).ToArray();
		var analysisPad = AudioDsp.ReflectPad(filtered, 80);

		var result = InvokePrivateStatic<(List<int> OptimalTimestamps, float[] InferenceAudio)>("FindOptimalTimestamps", filtered, analysisPad);

		Assert.Empty(result.OptimalTimestamps);
		Assert.Equal(filtered.Length + (2 * 16000 * 3), result.InferenceAudio.Length);
		AssertEqualWithin(AudioDsp.ReflectPad(filtered, 16000 * 3), result.InferenceAudio);
	}

	[Fact]
	public void FindOptimalTimestamps_LongAudio_FindsMinimumEnergyNearCenter()
	{
		var filtered = Enumerable.Repeat(1.0f, 1_000_000).ToArray();
		for (var i = 720_000; i < 721_000; i++)
		{
			filtered[i] = 0.0f;
		}
		var analysisPad = AudioDsp.ReflectPad(filtered, 80);

		var result = InvokePrivateStatic<(List<int> OptimalTimestamps, float[] InferenceAudio)>("FindOptimalTimestamps", filtered, analysisPad);

		var timestamp = Assert.Single(result.OptimalTimestamps);
		Assert.InRange(timestamp, 719_841, 721_000);
		Assert.Equal(filtered.Length + (2 * 16000 * 3), result.InferenceAudio.Length);
	}

	[Fact]
	public void ComputeSegmentSlices_WithoutTimestamps_ReturnsSingleFullSlice()
	{
		var audioPad = Enumerable.Range(0, 320).Select(static i => (float)i).ToArray();

		var slices = InvokeSegmentSlices(audioPad, [], CancellationToken.None);

		var slice = Assert.Single(slices.Cast<object>());
		AssertEqualWithin(audioPad, GetSliceAudio(slice));
		Assert.Equal(0, GetSlicePitchStart(slice));
		Assert.Equal(2, GetSlicePitchEnd(slice));
	}

	[Fact]
	public void ComputeSegmentSlices_AlignsTimestampsAndCalculatesPitchRanges()
	{
		var audioPad = new float[220_000];
		var optTs = new List<int> { 12_345, 54_321 };

		var slices = InvokeSegmentSlices(audioPad, optTs, CancellationToken.None);

		Assert.Equal(3, slices.Length);
		var slice0 = slices.GetValue(0) ?? throw new InvalidOperationException("Missing slice 0.");
		var slice1 = slices.GetValue(1) ?? throw new InvalidOperationException("Missing slice 1.");
		var slice2 = slices.GetValue(2) ?? throw new InvalidOperationException("Missing slice 2.");

		Assert.Equal(108_480, GetSliceAudio(slice0).Length);
		Assert.Equal(0, GetSlicePitchStart(slice0));
		Assert.Equal(677, GetSlicePitchEnd(slice0));

		Assert.Equal(138_080, GetSliceAudio(slice1).Length);
		Assert.Equal(77, GetSlicePitchStart(slice1));
		Assert.Equal(939, GetSlicePitchEnd(slice1));

		Assert.Equal(165_760, GetSliceAudio(slice2).Length);
		Assert.Equal(339, GetSlicePitchStart(slice2));
		Assert.Equal(1375, GetSlicePitchEnd(slice2));
	}

	[Fact]
	public void SliceFeatures_ReturnsRequestedLeadingFrames()
	{
		var features = new float[,]
		{
			{ 1.0f, 2.0f },
			{ 3.0f, 4.0f },
			{ 5.0f, 6.0f },
		};

		var sliced = RvcEngine.SliceFeatures(features, 2);

		Assert.Equal(2, sliced.GetLength(0));
		Assert.Equal(2, sliced.GetLength(1));
		Assert.Equal(1.0f, sliced[0, 0]);
		Assert.Equal(4.0f, sliced[1, 1]);
	}

	[Fact]
	public void ApplyProtect_ScalesOnlyUnvoicedFrames()
	{
		var features = new float[,]
		{
			{ 1.0f, 2.0f },
			{ 3.0f, 4.0f },
			{ 5.0f, 6.0f },
		};

		RvcEngine.ApplyProtect(features, [120.0f, 0.0f]);

		Assert.Equal(1.0f, features[0, 0]);
		Assert.Equal(2.0f, features[0, 1]);
		Assert.InRange(features[1, 0], 0.989f, 0.991f);
		Assert.InRange(features[1, 1], 1.319f, 1.321f);
		Assert.Equal(5.0f, features[2, 0]);
		Assert.Equal(6.0f, features[2, 1]);
	}

	[Fact]
	public void CreatePhoneArray_DoublesFramesAndTruncatesToTargetFrames()
	{
		var features = new float[,]
		{
			{ 1.0f, 2.0f },
			{ 3.0f, 4.0f },
		};

		var result = RvcEngine.CreatePhoneArray(features, 3);

		Assert.Equal([1L, 3L, 2L], result.Dimensions);
		Assert.Equal(6, result.Data.Length);
		AssertEqualWithin([1.0f, 2.0f, 1.0f, 2.0f, 3.0f, 4.0f], result.Data.Select(static value => value.ToFloat()).ToArray());
	}

	[Fact]
	public void CreatePhoneArray_ZeroTargetFrames_ReturnsEmptyPayload()
	{
		var features = new float[,] { { 1.0f, 2.0f } };

		var result = RvcEngine.CreatePhoneArray(features, 0);

		Assert.Equal([1L, 0L, 2L], result.Dimensions);
		Assert.Empty(result.Data);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(3)]
	public void CreateNoiseArray_ReturnsExpectedShape(int frameCount)
	{
		var result = RvcEngine.CreateNoiseArray(frameCount);

		Assert.Equal([1L, 192L, frameCount], result.Dimensions);
		Assert.Equal(192 * frameCount, result.Data.Length);
		if (frameCount > 0)
		{
			Assert.Contains(result.Data, value => value.ToFloat() != 0.0f);
		}
	}

	[Fact]
	public void Concatenate_JoinsAllSegmentsIncludingEmptyOnes()
	{
		var combined = RvcEngine.Concatenate
		(
			new List<float[]>
			{
				new float[] { 1.0f, 2.0f },
				Array.Empty<float>(),
				new float[] { 3.0f },
			}
		);

		AssertEqualWithin([1.0f, 2.0f, 3.0f], combined);
	}

	[Fact]
	public void Concatenate_WithNoSegments_ReturnsEmpty()
	{
		var combined = RvcEngine.Concatenate(new List<float[]>());

		Assert.Empty(combined);
	}

	[Fact]
	public void ClampPitchRange_ClampsToArrayBounds()
	{
		var slice = new RvcEngine.SegmentSlice(new float[100], 7, 20);
		var pitch = new long[5];
		var pitchf = new float[3];

		var result = RvcEngine.ClampPitchRange(slice, pitch, pitchf);

		Assert.Equal(3, result.PitchStart);
		Assert.Equal(3, result.PitchEnd);
	}

	[Fact]
	public void ClampPitchRange_EmptyArrays_ReturnsZeroRange()
	{
		var slice = new RvcEngine.SegmentSlice(new float[100], 4, 10);

		var result = RvcEngine.ClampPitchRange(slice, Array.Empty<long>(), Array.Empty<float>());

		Assert.Equal(0, result.PitchStart);
		Assert.Equal(0, result.PitchEnd);
	}

	// PlanStreamChunks: pure subdivision function. AbsorbThreshold = 2*chunk + 2*pad.
	// All assertions use defaults chunk=2.0, pad=0.3 (absorbThreshold=4.6) unless noted.

	[Fact]
	public void PlanStreamChunks_ZeroInput_ReturnsEmpty()
	{
		var chunks = RvcEngine.PlanStreamChunks(0.0, 2.0, 0.3);

		Assert.Empty(chunks);
	}

	[Fact]
	public void PlanStreamChunks_NegativeTotal_ReturnsEmpty()
	{
		var chunks = RvcEngine.PlanStreamChunks(-1.5, 2.0, 0.3);

		Assert.Empty(chunks);
	}

	[Theory]
	[InlineData(0.5)]
	[InlineData(1.0)]
	[InlineData(2.0)]
	[InlineData(2.5)]
	[InlineData(4.0)]
	[InlineData(4.5)]
	public void PlanStreamChunks_BelowAbsorbThreshold_ReturnsSingleChunk(double totalSeconds)
	{
		var chunks = RvcEngine.PlanStreamChunks(totalSeconds, 2.0, 0.3);

		Assert.Single(chunks);
		Assert.Equal(0.0, chunks[0].StartSeconds);
		Assert.Equal(totalSeconds, chunks[0].EndSeconds);
	}

	[Fact]
	public void PlanStreamChunks_ExactlyAbsorbThreshold_SplitsBecauseStrictLessThan()
	{
		// remaining < threshold uses strict less-than, so at EXACTLY the absorb threshold
		// we still split into two chunks: a full chunk plus an absorbed tail. This
		// matches the algorithm's invariant that the first chunk is always full when
		// the input is large enough to fit two minimum inferences.
		var chunks = RvcEngine.PlanStreamChunks(totalSeconds: 4.6, chunkSeconds: 2.0, padSeconds: 0.3);

		Assert.Equal(2, chunks.Count);
		Assert.Equal(0.0, chunks[0].StartSeconds);
		Assert.Equal(2.0, chunks[0].EndSeconds);
		Assert.Equal(2.0, chunks[1].StartSeconds);
		Assert.Equal(4.6, chunks[1].EndSeconds, 6);
	}

	[Fact]
	public void PlanStreamChunks_JustUnderAbsorbThreshold_StaysSingleChunk()
	{
		// Just below the threshold, the whole input collapses to one chunk.
		var chunks = RvcEngine.PlanStreamChunks(totalSeconds: 4.599, chunkSeconds: 2.0, padSeconds: 0.3);

		Assert.Single(chunks);
		Assert.Equal(0.0, chunks[0].StartSeconds);
		Assert.Equal(4.599, chunks[0].EndSeconds, 6);
	}

	[Fact]
	public void PlanStreamChunks_JustOverAbsorbThreshold_SplitsIntoTwo()
	{
		var chunks = RvcEngine.PlanStreamChunks(totalSeconds: 4.7, chunkSeconds: 2.0, padSeconds: 0.3);

		Assert.Equal(2, chunks.Count);
		Assert.Equal(0.0, chunks[0].StartSeconds);
		Assert.Equal(2.0, chunks[0].EndSeconds);
		Assert.Equal(2.0, chunks[1].StartSeconds);
		Assert.Equal(4.7, chunks[1].EndSeconds, 6);
	}

	[Fact]
	public void PlanStreamChunks_FiveSeconds_TailAbsorbedIntoSecondChunk()
	{
		var chunks = RvcEngine.PlanStreamChunks(totalSeconds: 5.0, chunkSeconds: 2.0, padSeconds: 0.3);

		Assert.Equal(2, chunks.Count);
		Assert.Equal(3.0, chunks[1].LengthSeconds, 6);
	}

	[Fact]
	public void PlanStreamChunks_ThirteenSeconds_ProducesSixChunksWithThreeSecondTail()
	{
		var chunks = RvcEngine.PlanStreamChunks(totalSeconds: 13.0, chunkSeconds: 2.0, padSeconds: 0.3);

		Assert.Equal(6, chunks.Count);
		Assert.All(chunks.Take(5), c => Assert.Equal(2.0, c.LengthSeconds, 6));
		Assert.Equal(3.0, chunks[^1].LengthSeconds, 6);
		Assert.Equal(0.0, chunks[0].StartSeconds);
		Assert.Equal(13.0, chunks[^1].EndSeconds);
	}

	[Fact]
	public void PlanStreamChunks_FiftySeconds_ProducesTwentyFourChunksLastIsFour()
	{
		var chunks = RvcEngine.PlanStreamChunks(totalSeconds: 50.0, chunkSeconds: 2.0, padSeconds: 0.3);

		Assert.Equal(24, chunks.Count);
		Assert.Equal(4.0, chunks[^1].LengthSeconds, 6);
	}

	[Fact]
	public void PlanStreamChunks_FiftyOneSeconds_ProducesTwentyFiveChunksLastIsThree()
	{
		var chunks = RvcEngine.PlanStreamChunks(totalSeconds: 51.0, chunkSeconds: 2.0, padSeconds: 0.3);

		Assert.Equal(25, chunks.Count);
		Assert.Equal(3.0, chunks[^1].LengthSeconds, 6);
	}

	[Fact]
	public void PlanStreamChunks_ChunksAreContiguousAndCoverFullInput()
	{
		const double total = 17.3;
		var chunks = RvcEngine.PlanStreamChunks(total, 2.0, 0.3);

		Assert.Equal(0.0, chunks[0].StartSeconds);
		Assert.Equal(total, chunks[^1].EndSeconds, 6);
		for (var i = 1; i < chunks.Count; i++)
		{
			Assert.Equal(chunks[i - 1].EndSeconds, chunks[i].StartSeconds, 6);
		}
	}

	[Fact]
	public void PlanStreamChunks_LastChunkIsAlwaysAtLeastChunkSeconds()
	{
		// For every total > absorbThreshold, the LAST chunk must be in [chunkSec, 2*(chunkSec+padSec)).
		const double chunk = 2.0;
		const double pad = 0.3;
		var absorbThreshold = (2.0 * chunk) + (2.0 * pad);

		var totals = new[] { 4.7, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0, 13.0, 50.0, 51.0, 100.0, 123.456 };
		foreach (var total in totals)
		{
			var chunks = RvcEngine.PlanStreamChunks(total, chunk, pad);
			var tail = chunks[^1].LengthSeconds;
			Assert.True
			(
				tail >= chunk - 1e-9 && tail < absorbThreshold,
				$"total={total}: tail={tail} not in [chunk={chunk}, absorbThreshold={absorbThreshold})"
			);
		}
	}

	[Theory]
	[InlineData(1.0, 0.5, 10.0)]
	[InlineData(3.0, 0.2, 50.0)]
	[InlineData(0.5, 0.1, 4.0)]
	[InlineData(5.0, 1.0, 100.0)]
	public void PlanStreamChunks_NonDefaultParameters_StillSatisfyInvariants(double chunkSec, double padSec, double totalSec)
	{
		var chunks = RvcEngine.PlanStreamChunks(totalSec, chunkSec, padSec);
		var absorbThreshold = (2.0 * chunkSec) + (2.0 * padSec);

		Assert.NotEmpty(chunks);
		Assert.Equal(0.0, chunks[0].StartSeconds);
		Assert.Equal(totalSec, chunks[^1].EndSeconds, 6);

		if (totalSec >= absorbThreshold)
		{
			Assert.True(chunks[^1].LengthSeconds >= chunkSec - 1e-9);
		}

		for (var i = 0; i < chunks.Count - 1; i++)
		{
			Assert.Equal(chunkSec, chunks[i].LengthSeconds, 6);
		}
	}

	[Fact]
	public void PlanStreamChunks_ZeroPadding_StillWorks()
	{
		// padSec=0 is allowed; absorbThreshold collapses to 2*chunkSec.
		var chunks = RvcEngine.PlanStreamChunks(totalSeconds: 5.0, chunkSeconds: 2.0, padSeconds: 0.0);

		// absorbThreshold = 4.0; remaining starts at 5.0 (>=4.0 take chunk; pos=2, remaining=3 <4 absorb)
		Assert.Equal(2, chunks.Count);
		Assert.Equal(2.0, chunks[0].LengthSeconds, 6);
		Assert.Equal(3.0, chunks[1].LengthSeconds, 6);
	}

	[Fact]
	public void PlanStreamChunks_ChunkSecondsZero_Throws()
	{
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => RvcEngine.PlanStreamChunks(totalSeconds: 5.0, chunkSeconds: 0.0, padSeconds: 0.3)
		);
	}

	[Fact]
	public void PlanStreamChunks_NegativeChunkSeconds_Throws()
	{
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => RvcEngine.PlanStreamChunks(totalSeconds: 5.0, chunkSeconds: -1.0, padSeconds: 0.3)
		);
	}

	[Fact]
	public void PlanStreamChunks_NegativePadSeconds_Throws()
	{
		Assert.Throws<ArgumentOutOfRangeException>
		(
			() => RvcEngine.PlanStreamChunks(totalSeconds: 5.0, chunkSeconds: 2.0, padSeconds: -0.1)
		);
	}

	[Fact]
	public void StreamChunk_LengthSeconds_ReturnsEndMinusStart()
	{
		var chunk = new RvcEngine.StreamChunk(StartSeconds: 4.5, EndSeconds: 6.7);

		Assert.Equal(2.2, chunk.LengthSeconds, 6);
	}

	private static string CreateVoicesDir(string baseDir)
	{
		Directory.CreateDirectory(baseDir);
		var voicesDir = Path.Combine(baseDir, ".rvc", "voices");
		Directory.CreateDirectory(voicesDir);
		return voicesDir;
	}

	private static void CreateSizedFile(string path, int bytes)
	{
		File.WriteAllBytes(path, new byte[bytes]);
	}

	private static float MapCents(int bin)
	{
		return (20.0f * bin) + 1997.3794f;
	}

	private static long QuantizeExpected(float pitch)
	{
		const float f0Min = 50.0f;
		const float f0Max = 1100.0f;
		var melMin = 1127.0f * MathF.Log(1.0f + (f0Min / 700.0f));
		var melMax = 1127.0f * MathF.Log(1.0f + (f0Max / 700.0f));
		var mel = 1127.0f * MathF.Log(1.0f + (pitch / 700.0f));
		if (mel > 0.0f)
		{
			mel = ((mel - melMin) * 254.0f / (melMax - melMin)) + 1.0f;
		}

		mel = Math.Clamp(mel, 1.0f, 255.0f);
		return (long)MathF.Round(mel);
	}

	private static Array InvokeSegmentSlices(float[] audioPad, List<int> optTs, CancellationToken ct)
	{
		return (Array)(InvokePrivateStatic("ComputeSegmentSlices", audioPad, optTs, ct)
			?? throw new InvalidOperationException("Method 'ComputeSegmentSlices' returned null."));
	}

	private static float[] GetSliceAudio(object slice)
	{
		return GetSliceProperty<float[]>(slice, "Audio");
	}

	private static int GetSlicePitchStart(object slice)
	{
		return GetSliceProperty<int>(slice, "PitchStart");
	}

	private static int GetSlicePitchEnd(object slice)
	{
		return GetSliceProperty<int>(slice, "PitchEnd");
	}

	private static T GetSliceProperty<T>(object slice, string propertyName)
	{
		return (T)(slice.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(slice)
			?? throw new InvalidOperationException($"Missing property '{propertyName}'."));
	}

	private static string[] GetSearchBases()
	{
		return AppPaths.SearchBases;
	}

	private static void SetSearchBases(params string[] searchBases)
	{
		var field = GetSearchBases();
		for (var i = 0; i < field.Length; i++)
		{
			field[i] = i < searchBases.Length ? searchBases[i] : Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		}
	}

	private static bool HasEmbeddedRvcResource(string resourceName)
	{
		using var stream = typeof(RvcEngine).Assembly.GetManifestResourceStream(resourceName);
		return stream is not null;
	}

	private static object? InvokePrivateStatic(string methodName, params object?[]? args)
	{
		var method = typeof(RvcEngine).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new InvalidOperationException($"Method '{methodName}' not found.");

		try
		{
			return method.Invoke(null, args);
		}
		catch (TargetInvocationException ex) when (ex.InnerException is not null)
		{
			ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
			throw;
		}
	}

	private static T InvokePrivateStatic<T>(string methodName, params object?[]? args)
	{
		return (T)(InvokePrivateStatic(methodName, args)
			?? throw new InvalidOperationException($"Method '{methodName}' returned null."));
	}

	private static void AssertEqualWithin(float[] expected, float[] actual, float tolerance = 1.0e-4f)
	{
		Assert.Equal(expected.Length, actual.Length);
		for (var i = 0; i < expected.Length; i++)
		{
			Assert.InRange(actual[i], expected[i] - tolerance, expected[i] + tolerance);
		}
	}

	/// <summary>
	/// Builds a minimal valid IwFl FAISS index (1 list, 1 vector, dim=2)
	/// that passes FaissIndex.IsSupportedFormat.
	/// </summary>
	private static byte[] BuildValidIndexBytes()
	{
		var bytes = new List<byte>();

		void WriteU32(uint v) { bytes.AddRange(BitConverter.GetBytes(v)); }
		void WriteI32(int v) { bytes.AddRange(BitConverter.GetBytes(v)); }
		void WriteI64(long v) { bytes.AddRange(BitConverter.GetBytes(v)); }
		void WriteF32(float v) { bytes.AddRange(BitConverter.GetBytes(v)); }

		const int dim = 2;
		const long ntotal = 1;
		const int nlist = 1;

		// IVF header
		WriteU32(0x6C46_7749); // IwFl
		WriteI32(dim);
		WriteI64(ntotal);
		WriteI64(0); // dummy
		WriteI64(0); // dummy
		bytes.Add(1); // is_trained
		WriteI32(0); // metric

		WriteI64(nlist); // nlist
		WriteI64(1);     // nprobe

		// Quantizer (IxF2)
		WriteU32(0x3246_7849); // IxF2
		WriteI32(dim);
		WriteI64(nlist);
		WriteI64(0);
		WriteI64(0);
		bytes.Add(1);
		WriteI32(0);
		WriteI64(nlist * dim); // xb count
		for (var i = 0; i < nlist * dim; i++) WriteF32(0f); // centroids

		// Direct map
		bytes.Add(0); // no direct map
		WriteI64(0);

		// Inverted lists (ilar)
		WriteU32(0x7261_6C69); // ilar
		WriteI64(nlist);
		WriteI64(dim * 4L); // code_size
		WriteU32(0x6C6C_7566); // "full"
		WriteI64(nlist); // list count

		WriteI64(ntotal); // list 0 has 1 vector

		// list 0 codes
		WriteF32(1f);
		WriteF32(2f);
		// list 0 ids
		WriteI64(0);

		return bytes.ToArray();
	}
}
