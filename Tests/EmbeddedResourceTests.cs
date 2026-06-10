using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace Talktastic.Tests;

/// <summary>
/// Verifies ALL required embedded resources are present in the compiled assembly.
/// These tests catch build defects (missing native DLLs, skeletons, manifests)
/// that would otherwise only surface at runtime.
/// </summary>
public sealed class EmbeddedResourceTests
{
	private static readonly Assembly TargetAssembly = typeof(RvcEngine).Assembly;

	// ── Native DLLs (brotli-compressed) ─────────────────────────────

	[Theory]
	[InlineData("Talktastic.Native.libmp3lame.dll.br")]
	[InlineData("Talktastic.Native.Microsoft.CognitiveServices.Speech.core.dll.br")]
	[InlineData("Talktastic.Native.Microsoft.CognitiveServices.Speech.extension.audio.sys.dll.br")]
	[InlineData("Talktastic.Native.Microsoft.CognitiveServices.Speech.extension.embedded.tts.dll.br")]
	[InlineData("Talktastic.Native.Microsoft.CognitiveServices.Speech.extension.onnxruntime.dll.br")]
	[InlineData("Talktastic.Native.onnxruntime.dll.br")]
	[InlineData("Talktastic.Native.onnxruntime_providers_shared.dll.br")]
	[InlineData("Talktastic.Native.sherpa-onnx-c-api.dll.br")]
	public void NativeDll_IsEmbedded(string resourceName)
	{
		AssertResourceExists(resourceName);
		AssertIsValidBrotli(resourceName);
	}

	[Fact]
	public void NativeManifest_IsEmbedded()
	{
		AssertResourceExists("Talktastic.Native.manifest.json");
	}

	[Fact]
	public void NativeManifest_IsValidJson()
	{
		using var stream = TargetAssembly.GetManifestResourceStream("Talktastic.Native.manifest.json")!;
		var entries = JsonSerializer.Deserialize
		(
			stream,
			NativeExtractorJsonContext.Default.NativePayloadManifestEntryArray
		);

		Assert.NotNull(entries);
		Assert.True(entries.Length >= 8, $"Manifest should list ≥8 DLLs, got {entries.Length}");

		foreach (var entry in entries)
		{
			Assert.False(string.IsNullOrWhiteSpace(entry.Name), "Manifest entry has empty Name");
			Assert.True(entry.Size > 0, $"Manifest entry '{entry.Name}' has zero Size");
			Assert.False(string.IsNullOrWhiteSpace(entry.Md5), $"Manifest entry '{entry.Name}' has empty Md5");
		}
	}

	[Fact]
	public void NativeManifest_AllDllsHaveMatchingResources()
	{
		using var stream = TargetAssembly.GetManifestResourceStream("Talktastic.Native.manifest.json")!;
		var entries = JsonSerializer.Deserialize
		(
			stream,
			NativeExtractorJsonContext.Default.NativePayloadManifestEntryArray
		)!;

		foreach (var entry in entries)
		{
			var brName = $"Talktastic.Native.{entry.Name}.br";
			AssertResourceExists(brName, $"Manifest references '{entry.Name}' but resource '{brName}' is missing");
		}
	}

	// ── espeak-ng-data (for sherpa-onnx phonemization) ──────────────

	[Fact]
	public void EspeakNgData_IsEmbedded()
	{
		AssertResourceExists("Talktastic.Piper.espeak-ng-data.zip");
	}

	[Fact]
	public void EspeakNgData_IsValidZip()
	{
		using var stream = TargetAssembly.GetManifestResourceStream("Talktastic.Piper.espeak-ng-data.zip")!;
		using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
		Assert.NotEmpty(zip.Entries);

		// Must contain key phonemization files
		var entryNames = zip.Entries.Select(static e => e.FullName).ToArray();
		Assert.Contains(entryNames, static n => n.Contains("phondata", StringComparison.Ordinal));
		Assert.Contains(entryNames, static n => n.Contains("phontab", StringComparison.Ordinal));
		Assert.Contains(entryNames, static n => n.Contains("phonindex", StringComparison.Ordinal));
		Assert.Contains(entryNames, static n => n.Contains("en_dict", StringComparison.Ordinal));
	}

	// ── RVC skeleton ONNX templates ─────────────────────────────────

	[Theory]
	[InlineData("32k")]
	[InlineData("40k")]
	[InlineData("48k")]
	public void RvcSkeleton_IsEmbedded(string srKey)
	{
		var resourceName = $"Talktastic.Rvc.skeleton_v2_{srKey}.onnx.gz";
		AssertResourceExists(resourceName);
		AssertIsValidGzip(resourceName);

		var bytes = Decompress(resourceName);
		Assert.True
		(
			bytes.Length > 1_000_000,
			$"Skeleton {srKey} should decompress to >1 MB, got {bytes.Length}"
		);
	}

	[Theory]
	[InlineData("32k")]
	[InlineData("40k")]
	[InlineData("48k")]
	public void RvcSkeletonManifest_IsEmbedded(string srKey)
	{
		var resourceName = $"Talktastic.Rvc.skeleton_v2_{srKey}_manifest.json.gz";
		AssertResourceExists(resourceName);
		AssertIsValidGzip(resourceName);

		var json = DecompressText(resourceName);
		var manifest = JsonSerializer.Deserialize
		(
			json,
			SkeletonManifestJsonContext.Default.SkeletonManifest
		);

		Assert.NotNull(manifest);
		Assert.NotEmpty(manifest.Initializers);
		Assert.NotEmpty(manifest.PthToOnnx);
	}

	// ── Completeness check ──────────────────────────────────────────

	[Fact]
	public void AllExpectedResources_ArePresent()
	{
		var allResources = TargetAssembly.GetManifestResourceNames();
		var nativeDlls = allResources.Where(static r => r.StartsWith("Talktastic.Native.", StringComparison.Ordinal) && r.EndsWith(".dll.br", StringComparison.Ordinal)).ToArray();
		var skeletons = allResources.Where(static r => r.StartsWith("Talktastic.Rvc.skeleton_v2_", StringComparison.Ordinal) && r.EndsWith(".onnx.gz", StringComparison.Ordinal)).ToArray();
		var manifests = allResources.Where(static r => r.StartsWith("Talktastic.Rvc.skeleton_v2_", StringComparison.Ordinal) && r.EndsWith("_manifest.json.gz", StringComparison.Ordinal)).ToArray();

		Assert.True(nativeDlls.Length >= 8, $"Expected ≥8 native DLL resources, found {nativeDlls.Length}: [{string.Join(", ", nativeDlls)}]");
		Assert.True(skeletons.Length >= 3, $"Expected ≥3 skeleton ONNX resources, found {skeletons.Length}: [{string.Join(", ", skeletons)}]");
		Assert.True(manifests.Length >= 3, $"Expected ≥3 skeleton manifest resources, found {manifests.Length}: [{string.Join(", ", manifests)}]");
		Assert.Contains(allResources, static r => r == "Talktastic.Native.manifest.json");
		Assert.Contains(allResources, static r => r == "Talktastic.Piper.espeak-ng-data.zip");
	}

	// ── Helpers ──────────────────────────────────────────────────────

	private static void AssertResourceExists(string resourceName, string? message = null)
	{
		using var stream = TargetAssembly.GetManifestResourceStream(resourceName);
		Assert.True
		(
			stream is not null,
			message ?? $"Required embedded resource '{resourceName}' is missing. Rebuild with all resources in native-resources/."
		);
	}

	private static void AssertIsValidBrotli(string resourceName)
	{
		var bytes = DecompressBrotli(resourceName);
		Assert.NotEmpty(bytes);
	}

	private static void AssertIsValidGzip(string resourceName)
	{
		using var stream = TargetAssembly.GetManifestResourceStream(resourceName)!;
		var header = new byte[2];
		var read = stream.Read(header, 0, 2);
		Assert.Equal(2, read);
		Assert.Equal(0x1F, header[0]);
		Assert.Equal(0x8B, header[1]);
	}

	private static byte[] Decompress(string resourceName)
	{
		using var stream = TargetAssembly.GetManifestResourceStream(resourceName)!;
		using var gzip = new GZipStream(stream, CompressionMode.Decompress);
		using var ms = new MemoryStream();
		gzip.CopyTo(ms);
		return ms.ToArray();
	}

	private static byte[] DecompressBrotli(string resourceName)
	{
		using var stream = TargetAssembly.GetManifestResourceStream(resourceName)!;
		using var brotli = new BrotliStream(stream, CompressionMode.Decompress);
		using var ms = new MemoryStream();
		brotli.CopyTo(ms);
		return ms.ToArray();
	}

	private static string DecompressText(string resourceName)
	{
		using var stream = TargetAssembly.GetManifestResourceStream(resourceName)!;
		using var gzip = new GZipStream(stream, CompressionMode.Decompress);
		using var reader = new StreamReader(gzip);
		return reader.ReadToEnd();
	}
}
