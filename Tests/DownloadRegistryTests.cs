using System.Reflection;
using System.Text.Json;

using Talktastic;

namespace Talktastic.Tests;

public sealed class DownloadRegistryTests : IDisposable
{
	private readonly List<string> _tempFiles = [];

	public void Dispose()
	{
		foreach (var tempFile in _tempFiles)
		{
			try
			{
				if (File.Exists(tempFile))
				{
					File.Delete(tempFile);
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void Load_MissingFile_ReturnsEmptyRegistry()
	{
		var path = CreateMissingTempFilePath();

		var registry = new DownloadRegistry(path);

		Assert.Empty(registry.Resources);
	}

	[Fact]
	public void Load_EmptyFile_ReturnsEmptyRegistry()
	{
		var path = CreateTempFilePath();

		var registry = new DownloadRegistry(path);

		Assert.Empty(registry.Resources);
	}

	[Fact]
	public void Load_OldFormat_ParsesLegacyEntry()
	{
		var path = CreateTempFilePath();
		File.WriteAllText(path, """{ "https://example.com/model.onnx": "egirl" }""");

		var registry = new DownloadRegistry(path);
		var entry = Assert.Single(registry.Resources);

		Assert.Equal("egirl", entry.Key);
		Assert.Equal(["egirl"], entry.Names);
		Assert.Equal(["https://example.com/model.onnx"], entry.Urls);
		Assert.Null(entry.Version);
	}

	[Fact]
	public void Load_NewFormat_ParsesStringsAndArrays()
	{
		var path = CreateTempFilePath();
		File.WriteAllText
		(
			path,
			"""
			{
				"BartSimpson_e230_s7360": {
					"names": "Bart Simpson",
					"urls": ["https://voice-models.com/xyz", "https://drive.google.com/abc"],
					"version": "0.8.4"
				},
				"ryan": {
					"names": ["British Ryan", "Ryan"],
					"urls": "https://example.com/voices/ryan.onnx"
				}
			}
			"""
		);

		var registry = new DownloadRegistry(path);
		var bart = Assert.Single(registry.Resources, static resource => resource.Key == "BartSimpson_e230_s7360");
		var ryan = Assert.Single(registry.Resources, static resource => resource.Key == "ryan");

		Assert.Equal(["Bart Simpson"], bart.Names);
		Assert.Equal
		(
			["https://voice-models.com/xyz", "https://drive.google.com/abc"],
			bart.Urls
		);
		Assert.Equal("0.8.4", bart.Version);

		Assert.Equal(["Ryan", "British Ryan"], ryan.Names);
		Assert.Equal(["https://example.com/voices/ryan.onnx"], ryan.Urls);
		Assert.Null(ryan.Version);
	}

	[Fact]
	public void Load_NewFormat_NullProperties_ReturnEmptyCollectionsAndNullVersion()
	{
		var path = CreateTempFilePath();
		File.WriteAllText
		(
			path,
			"""
			{
				"empty": {
					"names": null,
					"urls": null,
					"version": null
				}
			}
			"""
		);

		var registry = new DownloadRegistry(path);
		var entry = Assert.Single(registry.Resources);

		Assert.Equal("empty", entry.Key);
		Assert.Empty(entry.Names);
		Assert.Empty(entry.Urls);
		Assert.Null(entry.Version);
	}

	[Fact]
	public void Load_NewFormat_WhitespaceStringProperties_ReturnEmptyCollections()
	{
		var path = CreateTempFilePath();
		File.WriteAllText
		(
			path,
			"""
			{
				"empty": {
					"names": "   ",
					"urls": "   "
				}
			}
			"""
		);

		var registry = new DownloadRegistry(path);
		var entry = Assert.Single(registry.Resources);

		Assert.Empty(entry.Names);
		Assert.Empty(entry.Urls);
	}

	[Fact]
	public void Load_MixedFormats_ParsesAllEntries()
	{
		var path = CreateTempFilePath();
		File.WriteAllText
		(
			path,
			"""
			{
				"https://example.com/model.onnx": "egirl",
				"bart": {
					"names": ["Bart Simpson", "Bart"],
					"urls": ["https://example.com/bart.zip", "https://drive.google.com/bart"],
					"version": "0.8.4"
				}
			}
			"""
		);

		var registry = new DownloadRegistry(path);

		Assert.Equal(2, registry.Resources.Count);
		Assert.NotNull(registry.LookupByUrl("https://example.com/model.onnx"));
		Assert.NotNull(registry.LookupByUrl("https://drive.google.com/bart"));
	}

	[Fact]
	public void LookupByUrl_FindsAnyRegisteredUrl()
	{
		var path = CreateTempFilePath();
		File.WriteAllText
		(
			path,
			"""
			{
				"bart": {
					"names": ["Bart Simpson", "Bart"],
					"urls": ["https://example.com/bart.zip", "https://drive.google.com/bart"],
					"version": "0.8.4"
				}
			}
			"""
		);

		var registry = new DownloadRegistry(path);
		var byPrimaryUrl = registry.LookupByUrl("https://example.com/bart.zip");
		var bySecondaryUrl = registry.LookupByUrl("https://drive.google.com/bart");

		Assert.NotNull(byPrimaryUrl);
		Assert.Same(byPrimaryUrl, bySecondaryUrl);
	}

	[Fact]
	public void LookupByUrl_NormalizesHostAndTrailingSlash()
	{
		var path = CreateTempFilePath();
		File.WriteAllText(path, """{ "https://example.com/models": "egirl" }""");

		var registry = new DownloadRegistry(path);

		Assert.NotNull(registry.LookupByUrl("HTTPS://EXAMPLE.COM/models/"));
	}

	[Fact]
	public void Register_NewResource_AddsItToRegistryAndUrlIndex()
	{
		var registry = new DownloadRegistry(CreateMissingTempFilePath());
		var entry = CreateEntry
		(
			"egirl",
			["egirl"],
			["https://example.com/model.onnx"]
		);

		registry.Register(entry);

		var stored = Assert.Single(registry.Resources);
		Assert.Equal("egirl", stored.Key);
		Assert.Equal(CurrentAppVersion, stored.Version);
		Assert.Same(stored, registry.LookupByUrl("https://example.com/model.onnx/"));
	}

	[Fact]
	public void Register_NullEntry_ThrowsArgumentNullException()
	{
		var registry = new DownloadRegistry(CreateMissingTempFilePath());

		Assert.Throws<ArgumentNullException>
		(
			() => registry.Register(null!)
		);
	}

	[Fact]
	public void Register_OverlappingUrl_MergesIntoExistingResource()
	{
		var registry = new DownloadRegistry(CreateMissingTempFilePath());

		registry.Register
		(
			CreateEntry
			(
				"egirl",
				["Electronic Girl", "egirl"],
				["https://example.com/model.onnx"]
			)
		);

		registry.Register
		(
			CreateEntry
			(
				"alternate",
				["E-Girl", "Electronic Girl"],
				["https://example.com/model.onnx", "https://drive.google.com/egirl"]
			)
		);

		var stored = Assert.Single(registry.Resources);

		Assert.Equal("egirl", stored.Key);
		Assert.Equal(["egirl", "E-Girl", "Electronic Girl"], stored.Names);
		Assert.Equal
		(
			["https://example.com/model.onnx", "https://drive.google.com/egirl"],
			stored.Urls
		);
	}

	[Fact]
	public void Register_CrossResourceOverlap_MergesAllMatchingResources()
	{
		var registry = new DownloadRegistry(CreateMissingTempFilePath());

		registry.Register
		(
			CreateEntry
			(
				"alpha",
				["Alpha Voice"],
				["https://example.com/a"]
			)
		);

		registry.Register
		(
			CreateEntry
			(
				"beta",
				["Beta Voice"],
				["https://example.com/b"]
			)
		);

		registry.Register
		(
			CreateEntry
			(
				"gamma",
				["Gamma Voice"],
				["https://example.com/a", "https://example.com/b", "https://example.com/c"]
			)
		);

		var stored = Assert.Single(registry.Resources);

		Assert.Equal("alpha", stored.Key);
		Assert.Equal
		(
			["https://example.com/a", "https://example.com/b", "https://example.com/c"],
			stored.Urls
		);
		Assert.Equal(["Beta Voice", "Alpha Voice", "Gamma Voice"], stored.Names);
	}

	[Fact]
	public void LookupByName_UsesFuzzyMatchingAcrossAllNames()
	{
		var registry = new DownloadRegistry(CreateMissingTempFilePath());

		registry.Register
		(
			CreateEntry
			(
				"bart",
				["Bart Simpson", "BartSimpson_e230_s7360"],
				["https://example.com/bart"]
			)
		);

		registry.Register
		(
			CreateEntry
			(
				"marge",
				["Marge Simpson"],
				["https://example.com/marge"]
			)
		);

		var match = registry.LookupByName("bartsimpsn");

		Assert.NotNull(match);
		Assert.Equal("bart", match.Key);
	}

	[Fact]
	public void Unregister_RemovesResourceByKey()
	{
		var registry = new DownloadRegistry(CreateMissingTempFilePath());

		registry.Register(CreateEntry("egirl", ["egirl"], ["https://example.com/e"]));
		registry.Register(CreateEntry("bart", ["Bart Simpson"], ["https://example.com/b"]));

		registry.Unregister("EGIRL");

		var remaining = Assert.Single(registry.Resources);
		Assert.Equal("bart", remaining.Key);
		Assert.Null(registry.LookupByUrl("https://example.com/e"));
	}

	[Fact]
	public void Save_RoundTripsMixedData()
	{
		var path = CreateTempFilePath();
		File.WriteAllText
		(
			path,
			"""
			{
				"https://example.com/model.onnx": "egirl",
				"bart": {
					"names": ["Bart Simpson", "Bart"],
					"urls": ["https://example.com/bart.zip"],
					"version": "0.8.4"
				}
			}
			"""
		);

		var registry = new DownloadRegistry(path);
		registry.Register
		(
			CreateEntry
			(
				"ignored",
				["Electronic Girl Deluxe"],
				["https://example.com/model.onnx", "https://drive.google.com/egirl"]
			)
		);
		registry.Save();

		var reloaded = new DownloadRegistry(path);
		var egirl = Assert.Single(reloaded.Resources, static resource => resource.Key == "egirl");
		var bart = Assert.Single(reloaded.Resources, static resource => resource.Key == "bart");

		Assert.Equal(["egirl", "Electronic Girl Deluxe"], egirl.Names);
		Assert.Equal
		(
			["https://example.com/model.onnx", "https://drive.google.com/egirl"],
			egirl.Urls
		);
		Assert.Equal(CurrentAppVersion, egirl.Version);
		Assert.Equal(["Bart", "Bart Simpson"], bart.Names);
	}

	[Fact]
	public void Register_AllUnusableNames_FallsBackToCandidates()
	{
		var registry = new DownloadRegistry(CreateMissingTempFilePath());
		var guid = Guid.NewGuid().ToString("D");
		var tempName = $"download-{Guid.NewGuid():D}";

		registry.Register
		(
			CreateEntry
			(
				"placeholder",
				["drive_abc123", guid, tempName],
				["https://example.com/model.onnx"]
			)
		);

		var entry = Assert.Single(registry.Resources);

		Assert.Equal(["drive_abc123", guid, tempName], entry.Names);
	}

	[Fact]
	public void Register_EmptyNames_HandlesGracefully()
	{
		var registry = new DownloadRegistry(CreateMissingTempFilePath());

		registry.Register
		(
			CreateEntry
			(
				"nameless",
				[],
				["https://example.com/model.onnx"]
			)
		);

		var entry = Assert.Single(registry.Resources);

		Assert.Empty(entry.Names);
		Assert.Equal(["https://example.com/model.onnx"], entry.Urls);
	}

	[Fact]
	public void Save_SingleItemNamesAndUrls_AreWrittenAsStrings()
	{
		var path = CreateTempFilePath();
		var registry = new DownloadRegistry(path);

		registry.Register
		(
			CreateEntry
			(
				"ryan",
				["Ryan"],
				["https://example.com/voices/ryan.onnx"]
			)
		);

		registry.Save();

		using var document = JsonDocument.Parse(File.ReadAllText(path));
		var entry = document.RootElement.GetProperty("ryan");

		Assert.Equal(JsonValueKind.String, entry.GetProperty("names").ValueKind);
		Assert.Equal(JsonValueKind.String, entry.GetProperty("urls").ValueKind);
	}

	[Fact]
	public void Save_EmptyLists_AreWrittenAsEmptyArrays()
	{
		var path = CreateTempFilePath();
		var registry = new DownloadRegistry(path);

		registry.Register(CreateEntry("empty", [], []));
		registry.Save();

		using var document = JsonDocument.Parse(File.ReadAllText(path));
		var entry = document.RootElement.GetProperty("empty");

		Assert.Equal(JsonValueKind.Array, entry.GetProperty("names").ValueKind);
		Assert.Equal(0, entry.GetProperty("names").GetArrayLength());
		Assert.Equal(JsonValueKind.Array, entry.GetProperty("urls").ValueKind);
		Assert.Equal(0, entry.GetProperty("urls").GetArrayLength());
	}

	[Theory]
	[InlineData("""{ "egirl": { "names": "egirl", "urls": "https://example.com/model.onnx" } }""")]
	[InlineData("""{ "egirl": { "names": "egirl", "urls": "https://example.com/model.onnx", "version": null } }""")]
	[InlineData("""{ "egirl": { "names": "egirl", "urls": "https://example.com/model.onnx", "version": "0.8.3" } }""")]
	[InlineData("""{ "egirl": { "names": "egirl", "urls": "https://example.com/model.onnx", "version": "0.8.2" } }""")]
	public void Save_LegacyVersions_WriteOldFormat(string json)
	{
		var path = CreateTempFilePath();
		File.WriteAllText(path, json);

		var registry = new DownloadRegistry(path);
		registry.Save();

		using var document = JsonDocument.Parse(File.ReadAllText(path));
		var property = Assert.Single(document.RootElement.EnumerateObject());

		Assert.Equal("https://example.com/model.onnx", property.Name);
		Assert.Equal(JsonValueKind.String, property.Value.ValueKind);
		Assert.Equal("egirl", property.Value.GetString());
	}

	[Fact]
	public void Register_NewEntries_StampCurrentVersion()
	{
		var path = CreateTempFilePath();
		var registry = new DownloadRegistry(path);

		registry.Register
		(
			CreateEntry
			(
				"egirl",
				["egirl"],
				["https://example.com/model.onnx"]
			)
		);
		registry.Save();

		var stored = Assert.Single(registry.Resources);
		using var document = JsonDocument.Parse(File.ReadAllText(path));
		var jsonEntry = document.RootElement.GetProperty("egirl");

		Assert.Equal(CurrentAppVersion, stored.Version);
		Assert.Equal(CurrentAppVersion, jsonEntry.GetProperty("version").GetString());
	}

	[Fact]
	public void LookupByUrl_NullOrWhitespace_ReturnsNull()
	{
		var registry = new DownloadRegistry(CreateMissingTempFilePath());

		Assert.Null(registry.LookupByUrl(null!));
		Assert.Null(registry.LookupByUrl(""));
		Assert.Null(registry.LookupByUrl("   "));
	}

	[Fact]
	public void LookupByUrl_MalformedUrl_ReturnsNull()
	{
		var registry = new DownloadRegistry(CreateMissingTempFilePath());

		Assert.Null(registry.LookupByUrl("://not-a-valid-uri"));
	}

	[Fact]
	public void LookupByUrl_NonExistentUrl_ReturnsNull()
	{
		var registry = new DownloadRegistry(CreateMissingTempFilePath());
		registry.Register
		(
			CreateEntry
			(
				"bart",
				["Bart Simpson"],
				["https://example.com/bart"]
			)
		);

		Assert.Null(registry.LookupByUrl("https://example.com/nonexistent"));
	}

	[Fact]
	public void LookupByName_NoMatch_ReturnsNull()
	{
		var registry = new DownloadRegistry(CreateMissingTempFilePath());
		registry.Register
		(
			CreateEntry
			(
				"bart",
				["Bart Simpson"],
				["https://example.com/bart"]
			)
		);

		Assert.Null(registry.LookupByName("zzzzcompletegibberishzzz"));
	}

	[Fact]
	public void Unregister_WhitespaceKey_DoesNothing()
	{
		var registry = new DownloadRegistry(CreateMissingTempFilePath());
		registry.Register(CreateEntry("bart", ["Bart"], ["https://example.com/bart"]));

		registry.Unregister("");
		registry.Unregister("   ");

		Assert.Single(registry.Resources);
	}

	[Fact]
	public void Unregister_NonExistentKey_DoesNothing()
	{
		var registry = new DownloadRegistry(CreateMissingTempFilePath());
		registry.Register(CreateEntry("bart", ["Bart"], ["https://example.com/bart"]));

		registry.Unregister("marge");

		Assert.Single(registry.Resources);
	}

	[Fact]
	public void Constructor_ThrowsOnNullOrWhitespace()
	{
		Assert.Throws<ArgumentException>(() => new DownloadRegistry(""));
		Assert.Throws<ArgumentException>(() => new DownloadRegistry("   "));
	}

	[Fact]
	public void Load_UnsupportedValueKind_ThrowsJsonException()
	{
		var path = CreateTempFilePath();
		File.WriteAllText(path, """{ "bad-key": 42 }""");

		Assert.Throws<JsonException>(() => new DownloadRegistry(path));
	}

	[Fact]
	public void Load_StringArrayWithNonStringItem_ThrowsJsonException()
	{
		var path = CreateTempFilePath();
		File.WriteAllText
		(
			path,
			"""
			{
				"bad": {
					"names": ["valid", 42],
					"urls": "https://example.com/model.onnx"
				}
			}
			"""
		);

		Assert.Throws<JsonException>(() => new DownloadRegistry(path));
	}

	[Fact]
	public void Load_UnsupportedNamesKind_ThrowsJsonException()
	{
		var path = CreateTempFilePath();
		File.WriteAllText
		(
			path,
			"""
			{
				"bad": {
					"names": 42,
					"urls": "https://example.com/model.onnx"
				}
			}
			"""
		);

		Assert.Throws<JsonException>(() => new DownloadRegistry(path));
	}

	[Fact]
	public void Load_UnsupportedVersionKind_ThrowsJsonException()
	{
		var path = CreateTempFilePath();
		File.WriteAllText
		(
			path,
			"""
			{
				"bad": {
					"names": "Model",
					"urls": "https://example.com/model.onnx",
					"version": 42
				}
			}
			"""
		);

		Assert.Throws<JsonException>(() => new DownloadRegistry(path));
	}

	[Fact]
	public void Load_NonObjectRoot_ThrowsJsonException()
	{
		var path = CreateTempFilePath();
		File.WriteAllText(path, """[ "not", "an", "object" ]""");

		Assert.Throws<JsonException>(() => new DownloadRegistry(path));
	}

	[Fact]
	public void Save_LegacyEntryWithMultipleUrls_WritesNewFormat()
	{
		var path = CreateTempFilePath();
		File.WriteAllText(path, """{ "https://example.com/model.onnx": "egirl" }""");

		var registry = new DownloadRegistry(path);
		registry.Register
		(
			CreateEntry
			(
				"ignored",
				["egirl"],
				["https://example.com/model.onnx", "https://mirror.example.com/model.onnx"]
			)
		);
		registry.Save();

		using var document = JsonDocument.Parse(File.ReadAllText(path));
		var property = Assert.Single(document.RootElement.EnumerateObject());
		Assert.Equal(JsonValueKind.Object, property.Value.ValueKind);
	}

	[Fact]
	public void Save_UnparseableVersion_WritesNewFormat()
	{
		var path = CreateTempFilePath();
		File.WriteAllText
		(
			path,
			"""{ "model": { "names": "Model", "urls": "https://example.com/m", "version": "definitely-not-a-version" } }"""
		);

		var registry = new DownloadRegistry(path);
		registry.Save();

		using var document = JsonDocument.Parse(File.ReadAllText(path));
		var property = Assert.Single(document.RootElement.EnumerateObject());
		Assert.Equal("model", property.Name);
		Assert.Equal(JsonValueKind.Object, property.Value.ValueKind);
	}

	[Fact]
	public void Save_MissingDirectory_CreatesDirectory()
	{
		var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		var path = Path.Combine(directory, "registry.json");

		try
		{
			var registry = new DownloadRegistry(path);
			registry.Register(CreateEntry("egirl", ["egirl"], ["https://example.com/model.onnx"]));

			registry.Save();

			Assert.True(Directory.Exists(directory));
			Assert.True(File.Exists(path));
		}
		finally
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}

			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}
		}
	}

	[Fact]
	public void Save_NewVersionEntry_WritesNewFormat()
	{
		var path = CreateTempFilePath();
		File.WriteAllText
		(
			path,
			"""{ "model": { "names": "Model", "urls": "https://example.com/m", "version": "0.8.4" } }"""
		);

		var registry = new DownloadRegistry(path);
		registry.Save();

		using var document = JsonDocument.Parse(File.ReadAllText(path));
		var property = Assert.Single(document.RootElement.EnumerateObject());
		Assert.Equal("model", property.Name);
		Assert.Equal(JsonValueKind.Object, property.Value.ValueKind);
	}

	private string CreateTempFilePath()
	{
		var path = Path.GetTempFileName();
		_tempFiles.Add(path);
		return path;
	}

	private string CreateMissingTempFilePath()
	{
		var path = CreateTempFilePath();
		File.Delete(path);
		return path;
	}

	private static ResourceEntry CreateEntry
	(
		string key,
		IEnumerable<string> names,
		IEnumerable<string> urls,
		string? version = null
	)
	{
		var entry = new ResourceEntry
		{
			Key = key,
			Version = version,
		};

		entry.Names.AddRange(names);
		entry.Urls.AddRange(urls);
		return entry;
	}

	private static string CurrentAppVersion =>
		typeof(DownloadRegistry).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
			?.InformationalVersion
			?.Split('+')[0]
		?? "0.0.0";
}
