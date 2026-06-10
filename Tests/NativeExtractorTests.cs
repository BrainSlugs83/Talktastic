using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;

namespace Talktastic.Tests;

// NativeExtractorTests mutates process-global state (current directory, PATH, and
// static NativeExtractor fields). xUnit runs collections in parallel within a single
// process, so that global state would leak into other test classes -- e.g. a parallel
// test's file I/O resolving against this class's hijacked current directory, which then
// blocks the recursive artifact cleanup in Dispose with "being used by another process".
// Disabling parallelization keeps this class from running alongside any other collection.
[CollectionDefinition("ProcessGlobalState", DisableParallelization = true)]
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit collection definition markers must be public to be discovered.")]
public sealed class ProcessGlobalStateDefinition
{
}

[Collection("ProcessGlobalState")]
public sealed class NativeExtractorTests : IDisposable
{
	private const string HelloMd5 = "5D41402ABC4B2A76B9719D911017C592";

	private readonly string _artifactRoot;
	private readonly List<string> _filesToDelete = [];
	private readonly string _originalCurrentDirectory;
	private readonly string _originalPath;

	public NativeExtractorTests()
	{
		_artifactRoot = Path.Combine(AppContext.BaseDirectory, nameof(NativeExtractorTests), Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_artifactRoot);
		_originalCurrentDirectory = Directory.GetCurrentDirectory();
		_originalPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
	}

	public void Dispose()
	{
		Directory.SetCurrentDirectory(_originalCurrentDirectory);
		Environment.SetEnvironmentVariable("PATH", _originalPath);
		ResetNativeExtractorState();

		foreach (var file in _filesToDelete)
		{
			try
			{
				if (File.Exists(file))
				{
					File.Delete(file);
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}

		if (Directory.Exists(_artifactRoot))
		{
			TryDeleteDirectory(_artifactRoot);
		}
	}

	private static void TryDeleteDirectory(string path)
	{
		// A transient handle (e.g. from a thread-pool worker still unwinding) can briefly
		// keep the directory in use; retry a few times before giving up. Cleanup failures
		// must never fail the test.
		for (var attempt = 0; attempt < 5; attempt++)
		{
			try
			{
				if (Directory.Exists(path))
				{
					Directory.Delete(path, recursive: true);
				}

				return;
			}
			catch (IOException)
			{
				Thread.Sleep(50);
			}
			catch (UnauthorizedAccessException)
			{
				Thread.Sleep(50);
			}
		}
	}

	[Fact]
	public void ManifestJson_EmptyArray_ReturnsEmptyManifest()
	{
			var manifest = DeserializeManifest("[]");

			Assert.NotNull(manifest);
			Assert.Empty(manifest);
	}

	[Fact]
	public void ManifestJson_MissingFields_UsesDefaultValues()
	{
			var manifest = DeserializeManifest("""[{ "Name": "alpha.dll" }]""");

			Assert.NotNull(manifest);
			Assert.Single(manifest!);
			Assert.Equal("alpha.dll", manifest[0].Name);
			Assert.Equal(0, manifest[0].Size);
			Assert.Null(manifest[0].Md5);
	}

	[Fact]
	public void ManifestJson_MalformedJson_ThrowsJsonException()
	{
			Assert.Throws<JsonException>(() => DeserializeManifest("""[{"""));
	}

	[Fact]
	public void ManifestJson_ValidJson_DeserializesEntries()
	{
			const string json = """
			[
				{
					"Name": "alpha.dll",
					"Size": 5,
					"Md5": "5D41402ABC4B2A76B9719D911017C592"
				},
				{
					"Name": "beta.dll",
					"Size": 7,
					"Md5": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
				}
			]
			""";

		var manifest = JsonSerializer.Deserialize
		(
			json,
			NativeExtractorJsonContext.Default.NativePayloadManifestEntryArray
		);

		Assert.NotNull(manifest);
		Assert.Collection
		(
			manifest!,
			entry =>
			{
				Assert.Equal("alpha.dll", entry.Name);
				Assert.Equal(5, entry.Size);
				Assert.Equal(HelloMd5, entry.Md5);
			},
			entry =>
			{
				Assert.Equal("beta.dll", entry.Name);
				Assert.Equal(7, entry.Size);
				Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", entry.Md5);
			}
		);
	}

	[Fact]
	public void ManifestJson_InvalidJson_ThrowsJsonException()
	{
		const string json = """[{ "Name": "alpha.dll", "Size": "oops", "Md5": "x" }]""";

		Assert.Throws<JsonException>
		(
			() => JsonSerializer.Deserialize
			(
				json,
				NativeExtractorJsonContext.Default.NativePayloadManifestEntryArray
			)
		);
	}

	[Fact]
	public void GetManifest_ReturnsCachedManifestContainingAllConfiguredDlls()
	{
		var first = InvokePrivateStatic<NativePayloadManifestEntry[]>
		(
			typeof(NativeExtractor),
			"GetManifest"
		);
		var second = InvokePrivateStatic<NativePayloadManifestEntry[]>
		(
			typeof(NativeExtractor),
			"GetManifest"
		);
		var allNames = InvokePrivateStatic<HashSet<string>>
		(
			typeof(NativeExtractor),
			"GetDllNames",
			DllGroup.SpeechSdk | DllGroup.Lame | DllGroup.OnnxRuntime
		);

		Assert.Same(first, second);
		Assert.NotEmpty(first);

		var manifestNames = first.Select(static entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		Assert.Subset(allNames, manifestNames);
	}

	[Fact]
	public void GetDllNames_CombinedGroups_ReturnDistinctUnion()
	{
		var names = InvokePrivateStatic<HashSet<string>>
		(
			typeof(NativeExtractor),
			"GetDllNames",
			DllGroup.SpeechSdk | DllGroup.Lame | DllGroup.OnnxRuntime
		);

		Assert.Equal(9, names.Count);
		Assert.Contains("Microsoft.CognitiveServices.Speech.core.dll", names);
		Assert.Contains("Microsoft.CognitiveServices.Speech.extension.audio.sys.dll", names);
		Assert.Contains("Microsoft.CognitiveServices.Speech.extension.embedded.tts.dll", names);
		Assert.Contains("Microsoft.CognitiveServices.Speech.extension.onnxruntime.dll", names);
		Assert.Contains("libmp3lame.dll", names);
		Assert.Contains("onnxruntime.dll", names);
		Assert.Contains("onnxruntime_providers_shared.dll", names);
		Assert.Contains("DirectML.dll", names);
		Assert.Contains("sherpa-onnx-c-api.dll", names);
	}

	[Fact]
	public void GetDllNames_UnknownFlag_ReturnsEmptySet()
	{
		var names = InvokePrivateStatic<HashSet<string>>
		(
			typeof(NativeExtractor),
			"GetDllNames",
			(DllGroup)8
		);

		Assert.Empty(names);
	}

	[Theory]
	[InlineData((int)DllGroup.None, 0, null)]
	[InlineData((int)DllGroup.SpeechSdk, 4, "Microsoft.CognitiveServices.Speech.core.dll")]
	[InlineData((int)DllGroup.Lame, 1, "libmp3lame.dll")]
	[InlineData((int)DllGroup.OnnxRuntime, 4, "onnxruntime.dll")]
	[InlineData((int)(DllGroup.SpeechSdk | DllGroup.Lame | DllGroup.OnnxRuntime), 9, "sherpa-onnx-c-api.dll")]
	public void GetDllNames_GroupSelection_ReturnsExpectedNames(int groupsValue, int expectedCount, string? expectedName)
	{
		var groups = (DllGroup)groupsValue;

		var names = InvokePrivateStatic<HashSet<string>>
		(
			typeof(NativeExtractor),
			"GetDllNames",
			groups
		);

		Assert.Equal(expectedCount, names.Count);

		if (expectedName is not null)
		{
			Assert.Contains(expectedName, names);
		}

		if (groups == DllGroup.SpeechSdk)
		{
			Assert.DoesNotContain("libmp3lame.dll", names);
			Assert.DoesNotContain("onnxruntime.dll", names);
		}

		if (groups == DllGroup.Lame)
		{
			Assert.DoesNotContain("Microsoft.CognitiveServices.Speech.core.dll", names);
			Assert.DoesNotContain("onnxruntime.dll", names);
		}

		if (groups == DllGroup.OnnxRuntime)
		{
			Assert.DoesNotContain("libmp3lame.dll", names);
			Assert.DoesNotContain("Microsoft.CognitiveServices.Speech.core.dll", names);
		}
	}

	[Fact]
	public void IsCwd_NormalizesAndIgnoresCase()
	{
		var cwd = Path.Combine(_artifactRoot, "Folder");
		var equivalent = Path.Combine(_artifactRoot, ".", "folder");

		var isCwd = InvokePrivateStatic<bool>
		(
			typeof(NativeExtractor),
			"IsCwd",
			equivalent.ToUpperInvariant(),
			Path.GetFullPath(cwd)
		);

		Assert.True(isCwd);
	}

	[Fact]
	public void IsValid_MissingFile_ReturnsFalse()
	{
		var entry = new NativePayloadManifestEntry("missing.dll", 5, HelloMd5);
		var path = Path.Combine(_artifactRoot, entry.Name);

		var isValid = InvokePrivateStatic<bool>
		(
			typeof(NativeExtractor),
			"IsValid",
			path,
			entry
		);

		Assert.False(isValid);
	}

	[Fact]
	public void IsValid_SizeMismatch_ReturnsFalse()
	{
		var path = WriteArtifact("size-mismatch.dll", "hello"u8.ToArray());
		var entry = new NativePayloadManifestEntry("size-mismatch.dll", 4, HelloMd5);

		var isValid = InvokePrivateStatic<bool>
		(
			typeof(NativeExtractor),
			"IsValid",
			path,
			entry
		);

		Assert.False(isValid);
	}

	[Fact]
	public void IsValid_HashMismatch_ReturnsFalse()
	{
		var path = WriteArtifact("hash-mismatch.dll", "hello"u8.ToArray());
		var entry = new NativePayloadManifestEntry("hash-mismatch.dll", 5, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

		var isValid = InvokePrivateStatic<bool>
		(
			typeof(NativeExtractor),
			"IsValid",
			path,
			entry
		);

		Assert.False(isValid);
	}

	[Fact]
	public void IsValid_MatchingSizeAndHash_ReturnsTrue()
	{
		var path = WriteArtifact("valid.dll", "hello"u8.ToArray());
		var entry = new NativePayloadManifestEntry("valid.dll", 5, HelloMd5);

		var isValid = InvokePrivateStatic<bool>
		(
			typeof(NativeExtractor),
			"IsValid",
			path,
			entry
		);

		Assert.True(isValid);
	}

	[Fact]
	public void ComputeMd5_ExistingFile_ReturnsExpectedHash()
	{
		var path = WriteArtifact("hash.dll", "hello"u8.ToArray());

		var hash = InvokePrivateStatic<string>
		(
			typeof(NativeExtractor),
			"ComputeMd5",
			path
		);

		Assert.Equal(HelloMd5, hash);
	}

	[Fact]
	public void ComputeMd5_MissingFile_ThrowsFileNotFoundException()
	{
		var path = Path.Combine(_artifactRoot, "missing-hash.dll");

		var exception = Assert.Throws<TargetInvocationException>
		(
			() => InvokePrivateStatic<string>
			(
				typeof(NativeExtractor),
				"ComputeMd5",
				path
			)
		);

		Assert.IsType<FileNotFoundException>(exception.InnerException);
	}

	[Fact]
	public void FindOrExtract_CurrentWorkingDirectoryContainsValidCopy_ReturnsThatPath()
	{
		var entry = new NativePayloadManifestEntry($"cwd-{Guid.NewGuid():N}.dll", 5, HelloMd5);
		var cwd = Path.Combine(_artifactRoot, "cwd");
		Directory.CreateDirectory(cwd);
		var cwdPath = WriteArtifact(entry.Name, "hello"u8.ToArray(), cwd);

		var resolved = InvokePrivateStatic<ResolvedDll>
		(
			typeof(NativeExtractor),
			"FindOrExtract",
			typeof(NativeExtractor).Assembly,
			entry,
			Path.GetFullPath(cwd)
		);

		Assert.Equal(entry.Name, resolved.Name);
		Assert.Equal(Path.GetFullPath(cwdPath), Path.GetFullPath(resolved.Path));
	}

	[Fact]
	public void FindOrExtract_AppDataAndCwdCopiesExist_PrefersAppData()
	{
		var entry = new NativePayloadManifestEntry($"appdata-{Guid.NewGuid():N}.dll", 5, HelloMd5);
		var cwd = Path.Combine(_artifactRoot, "cwd");
		var appDataDir = GetPrivateField<string>(typeof(NativeExtractor), "AppDataDir");

		Directory.CreateDirectory(cwd);
		Directory.CreateDirectory(appDataDir);

		WriteArtifact(entry.Name, "hello"u8.ToArray(), cwd);
		var appDataPath = WriteArtifact(entry.Name, "hello"u8.ToArray(), appDataDir);

		var resolved = InvokePrivateStatic<ResolvedDll>
		(
			typeof(NativeExtractor),
			"FindOrExtract",
			typeof(NativeExtractor).Assembly,
			entry,
			Path.GetFullPath(cwd)
		);

		Assert.Equal(Path.GetFullPath(appDataPath), Path.GetFullPath(resolved.Path));
	}

	[Fact]
	public void FindOrExtract_TempContainsValidCopy_ReturnsTempPath()
	{
		var entry = new NativePayloadManifestEntry($"temp-{Guid.NewGuid():N}.dll", 5, HelloMd5);
		var cwd = Path.Combine(_artifactRoot, "cwd");
		var tempDir = GetPrivateField<string>(typeof(NativeExtractor), "TempDir");
		var tempPath = WriteArtifact(entry.Name, "hello"u8.ToArray(), tempDir);

		Directory.CreateDirectory(cwd);

		var resolved = InvokePrivateStatic<ResolvedDll>
		(
			typeof(NativeExtractor),
			"FindOrExtract",
			typeof(NativeExtractor).Assembly,
			entry,
			Path.GetFullPath(cwd)
		);

		Assert.Equal(entry.Name, resolved.Name);
		Assert.Equal(Path.GetFullPath(tempPath), Path.GetFullPath(resolved.Path));
	}

	[Fact]
	public void EnsureAvailable_ValidCurrentDirectoryDllExists_SkipsExtraction()
	{
		ResetNativeExtractorState();

		var cwd = Path.Combine(_artifactRoot, "ensure-available");
		var fileName = $"ensure-{Guid.NewGuid():N}.dll";
		var path = WriteArtifact(fileName, "hello"u8.ToArray(), cwd);
		var beforeWriteTime = File.GetLastWriteTimeUtc(path);
		var originalManifest = GetPrivateFieldOrNull<NativePayloadManifestEntry[]>(typeof(NativeExtractor), "_cachedManifest");
		var originalLameNames = GetGroupDllNames()[DllGroup.Lame].ToArray();

		try
		{
			Directory.CreateDirectory(cwd);
			Directory.SetCurrentDirectory(cwd);
			GetGroupDllNames()[DllGroup.Lame] = new[] { fileName };
			SetPrivateField(typeof(NativeExtractor), "_cachedManifest", new[] { new NativePayloadManifestEntry(fileName, 5, HelloMd5) });
			SetPrivateField(typeof(NativeExtractor), "_staleCleaned", true);

			NativeExtractor.EnsureAvailable(DllGroup.Lame);

			Assert.True(File.Exists(path));
			Assert.Equal(beforeWriteTime, File.GetLastWriteTimeUtc(path));
			Assert.Empty(GetCwdExtractions());
			Assert.Equal(DllGroup.Lame, GetPrivateField<DllGroup>(typeof(NativeExtractor), "_resolvedGroups"));
		}
		finally
		{
			GetGroupDllNames()[DllGroup.Lame] = originalLameNames;
			SetPrivateField(typeof(NativeExtractor), "_cachedManifest", originalManifest);
		}
	}

	[Fact]
	public async Task EnsureAvailable_ConcurrentCallsForSameGroup_CompletesWithoutErrors()
	{
		ResetNativeExtractorState();

		var cwd = Path.Combine(_artifactRoot, "ensure-concurrent");
		var fileName = $"concurrent-{Guid.NewGuid():N}.dll";
		WriteArtifact(fileName, "hello"u8.ToArray(), cwd);
		var originalManifest = GetPrivateFieldOrNull<NativePayloadManifestEntry[]>(typeof(NativeExtractor), "_cachedManifest");
		var originalLameNames = GetGroupDllNames()[DllGroup.Lame].ToArray();

		try
		{
			Directory.CreateDirectory(cwd);
			Directory.SetCurrentDirectory(cwd);
			GetGroupDllNames()[DllGroup.Lame] = new[] { fileName };
			SetPrivateField(typeof(NativeExtractor), "_cachedManifest", new[] { new NativePayloadManifestEntry(fileName, 5, HelloMd5) });
			SetPrivateField(typeof(NativeExtractor), "_staleCleaned", true);

			var tasks = Enumerable.Range(0, 8)
				.Select(_ => Task.Run(() => NativeExtractor.EnsureAvailable(DllGroup.Lame)))
				.ToArray();

			await Task.WhenAll(tasks);

			Assert.Equal(DllGroup.Lame, GetPrivateField<DllGroup>(typeof(NativeExtractor), "_resolvedGroups"));
			Assert.Empty(GetCwdExtractions());
		}
		finally
		{
			GetGroupDllNames()[DllGroup.Lame] = originalLameNames;
			SetPrivateField(typeof(NativeExtractor), "_cachedManifest", originalManifest);
		}
	}

	[Fact]
	public void CleanupCwdExtractions_LockedFile_DoesNotThrow()
	{
		var path = WriteArtifact("locked.dll", "hello"u8.ToArray());
		GetCwdExtractions().Add(path);

		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

		NativeExtractor.CleanupCwdExtractions();

		Assert.True(File.Exists(path));
	}

	[Fact]
	public void CleanupCwdExtractions_UnlockedFile_DeletesFileAndDrainsBag()
	{
		var path = WriteArtifact("unlocked.dll", "hello"u8.ToArray());
		GetCwdExtractions().Add(path);

		NativeExtractor.CleanupCwdExtractions();

		Assert.False(File.Exists(path));
		Assert.Empty(GetCwdExtractions());
	}

	[Fact]
	public void CleanStaleFiles_DeletesUnknownDllsButKeepsManifestEntries()
	{
		var appDataDir = GetPrivateField<string>(typeof(NativeExtractor), "AppDataDir");
		var keepPath = WriteArtifact($"keep-{Guid.NewGuid():N}.dll", "hello"u8.ToArray(), appDataDir);
		var stalePath = WriteArtifact($"stale-{Guid.NewGuid():N}.dll", "hello"u8.ToArray(), appDataDir);
		var nonDllPath = WriteArtifact($"notes-{Guid.NewGuid():N}.txt", "hello"u8.ToArray(), appDataDir);
		var manifest = Directory.Exists(appDataDir)
			? Directory.EnumerateFiles(appDataDir, "*.dll")
				.Select(path => new NativePayloadManifestEntry(Path.GetFileName(path), 0, string.Empty))
				.ToList()
			: [];
		manifest.RemoveAll(entry => string.Equals(entry.Name, Path.GetFileName(stalePath), StringComparison.OrdinalIgnoreCase));
		if (!manifest.Any(entry => string.Equals(entry.Name, Path.GetFileName(keepPath), StringComparison.OrdinalIgnoreCase)))
		{
			manifest.Add(new NativePayloadManifestEntry(Path.GetFileName(keepPath), 5, HelloMd5));
		}

		InvokePrivateStaticVoid
		(
			typeof(NativeExtractor),
			"CleanStaleFiles",
			(object?)manifest.ToArray()
		);

		Assert.True(File.Exists(keepPath));
		Assert.False(File.Exists(stalePath));
		Assert.True(File.Exists(nonDllPath));
	}

	[Fact]
	public void EnsureAvailable_NoneGroup_ReturnsWithoutChangingState()
	{
		ResetNativeExtractorState();

		NativeExtractor.EnsureAvailable(DllGroup.None);

		Assert.Equal(DllGroup.None, GetPrivateField<DllGroup>(typeof(NativeExtractor), "_resolvedGroups"));
	}

	[Fact]
	public void EnsureAvailable_AlreadyResolvedGroup_ReturnsWithoutReadingManifest()
	{
		ResetNativeExtractorState();
		SetPrivateField(typeof(NativeExtractor), "_resolvedGroups", DllGroup.Lame);
		SetPrivateField<NativePayloadManifestEntry[]?>(typeof(NativeExtractor), "_cachedManifest", null);

		NativeExtractor.EnsureAvailable(DllGroup.Lame);

		Assert.Equal(DllGroup.Lame, GetPrivateField<DllGroup>(typeof(NativeExtractor), "_resolvedGroups"));
		Assert.Null(GetPrivateFieldOrNull<NativePayloadManifestEntry[]>(typeof(NativeExtractor), "_cachedManifest"));
	}

	[Fact]
	public void ConfigureSearchPaths_NewDirectories_PrependsPathAndAvoidsDuplicates()
	{
		ResetNativeExtractorState();

		var configuredDirs = GetPrivateField<HashSet<string>>(typeof(NativeExtractor), "ConfiguredDirs");
		var originalConfiguredDirs = configuredDirs.ToArray();
		var originalPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		var dir1 = Path.Combine(_artifactRoot, "dll-dir-1");
		var dir2 = Path.Combine(_artifactRoot, "dll-dir-2");
		Directory.CreateDirectory(dir1);
		Directory.CreateDirectory(dir2);

		try
		{
			InvokePrivateStaticVoid
			(
				typeof(NativeExtractor),
				"ConfigureSearchPaths",
				(object?)new[]
				{
					new ResolvedDll("a.dll", Path.Combine(dir1, "a.dll")),
					new ResolvedDll("b.dll", Path.Combine(dir2, "b.dll")),
					new ResolvedDll("c.dll", Path.Combine(dir1, "c.dll")),
				}
			);

			var firstPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
			Assert.StartsWith($"{dir1};{dir2};", firstPath, StringComparison.OrdinalIgnoreCase);
			Assert.Contains(dir1, configuredDirs);
			Assert.Contains(dir2, configuredDirs);

			InvokePrivateStaticVoid
			(
				typeof(NativeExtractor),
				"ConfigureSearchPaths",
				(object?)new[]
				{
					new ResolvedDll("again-a.dll", Path.Combine(dir1, "again-a.dll")),
					new ResolvedDll("again-b.dll", Path.Combine(dir2, "again-b.dll")),
				}
			);

			Assert.Equal(firstPath, Environment.GetEnvironmentVariable("PATH"));
			Assert.Equal(2, configuredDirs.Count);
		}
		finally
		{
			Environment.SetEnvironmentVariable("PATH", originalPath);
			configuredDirs.Clear();
			foreach (var dir in originalConfiguredDirs)
			{
				configuredDirs.Add(dir);
			}
		}
	}

	private string WriteArtifact(string fileName, byte[] bytes, string? directory = null)
	{
		var path = Path.Combine(directory ?? _artifactRoot, fileName);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, bytes);
		_filesToDelete.Add(path);
		return path;
	}

	private static NativePayloadManifestEntry[]? DeserializeManifest(string json)
	{
		return JsonSerializer.Deserialize
		(
			json,
			NativeExtractorJsonContext.Default.NativePayloadManifestEntryArray
		);
	}

	private static ConcurrentBag<string> GetCwdExtractions()
	{
		return GetPrivateField<ConcurrentBag<string>>(typeof(NativeExtractor), "CwdExtractions");
	}

	private static Dictionary<DllGroup, string[]> GetGroupDllNames()
	{
		return GetPrivateField<Dictionary<DllGroup, string[]>>(typeof(NativeExtractor), "GroupDllNames");
	}

	private static void ResetNativeExtractorState()
	{
		SetPrivateField(typeof(NativeExtractor), "_resolvedGroups", DllGroup.None);
		SetPrivateField(typeof(NativeExtractor), "_staleCleaned", false);
		GetPrivateField<HashSet<string>>(typeof(NativeExtractor), "ConfiguredDirs").Clear();

		var extractions = GetCwdExtractions();

		while (extractions.TryTake(out _))
		{
		}
	}

	private static T InvokePrivateStatic<T>(Type type, string methodName, params object?[] args)
	{
		var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(method);

		var result = method!.Invoke(null, args);
		Assert.NotNull(result);
		return Assert.IsType<T>(result);
	}

	private static void InvokePrivateStaticVoid(Type type, string methodName, params object?[] args)
	{
		var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(method);
		method!.Invoke(null, args);
	}

	private static T GetPrivateField<T>(Type type, string fieldName)
	{
		var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(field);

		var value = field!.GetValue(null);
		Assert.NotNull(value);
		return Assert.IsType<T>(value);
	}

	private static T? GetPrivateFieldOrNull<T>(Type type, string fieldName)
	{
		var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(field);
		return (T?)field!.GetValue(null);
	}

	private static void SetPrivateField<T>(Type type, string fieldName, T value)
	{
		var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(field);
		field!.SetValue(null, value);
	}
}
