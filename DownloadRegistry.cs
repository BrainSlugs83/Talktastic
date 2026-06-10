using System.Reflection;
using System.Text.Json;

namespace Talktastic;

/// <summary>
/// Maps downloaded resources to their known names and source URLs.
/// </summary>
sealed class DownloadRegistry
{
	private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
	private static readonly Version LegacyCutoffVersion = new(0, 8, 3);

	private readonly List<ResourceEntry> _resources = [];
	private readonly Dictionary<string, ResourceEntry> _urlIndex = new(StringComparer.OrdinalIgnoreCase);
	private readonly string _filePath;

	/// <summary>
	/// Initializes a registry backed by the specified file.
	/// </summary>
	/// <param name="filePath">The registry file path.</param>
	public DownloadRegistry(string filePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

		_filePath = filePath;
		Load();
	}

	/// <summary>
	/// Gets the registered resources.
	/// </summary>
	public IReadOnlyList<ResourceEntry> Resources => _resources;

	/// <summary>
	/// Looks up a resource by URL.
	/// </summary>
	/// <param name="url">The URL to match.</param>
	/// <returns>The matching resource, or <see langword="null"/>.</returns>
	public ResourceEntry? LookupByUrl(string url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return null;
		}

		try
		{
			var normalizedUrl = ModelDownloader.NormalizeUrl(url);
			return _urlIndex.TryGetValue(normalizedUrl, out var resource)
				? resource
				: null;
		}
		catch (UriFormatException)
		{
			return null;
		}
	}

	/// <summary>
	/// Looks up a resource by fuzzy name match.
	/// </summary>
	/// <param name="query">The name to search for.</param>
	/// <returns>The best matching resource, or <see langword="null"/>.</returns>
	public ResourceEntry? LookupByName(string query)
	{
		var candidates = _resources
			.SelectMany
			(
				static resource => resource.Names.Select(name => new NameCandidate(resource, name))
			)
			.ToArray();

		return FuzzyMatcher.FindBestMatch(candidates, query, static candidate => candidate.Name)?.Entry;
	}

	/// <summary>
	/// Adds or merges a resource entry into the registry.
	/// </summary>
	/// <param name="entry">The entry to register.</param>
	public void Register(ResourceEntry entry)
	{
		ArgumentNullException.ThrowIfNull(entry);

		var normalizedEntry = NormalizeEntry(entry);
		normalizedEntry.Version = GetCurrentAppVersion();

		var overlaps = normalizedEntry.Urls
			.Select
			(
				url => _urlIndex.TryGetValue(url, out var existing)
					? existing
					: null
			)
			.Where(static resource => resource is not null)
			.Distinct()
			.Cast<ResourceEntry>()
			.ToArray();

		if (overlaps.Length == 0)
		{
			_resources.Add(normalizedEntry);
			RebuildUrlIndex();
			return;
		}

		var primary = overlaps[0];
		MergeInto(primary, normalizedEntry);

		foreach (var overlap in overlaps.Skip(1))
		{
			MergeInto(primary, overlap);
			_resources.Remove(overlap);
		}

		primary.Version = GetCurrentAppVersion();
		RebuildUrlIndex();
	}

	/// <summary>
	/// Removes a resource entry by key.
	/// </summary>
	/// <param name="key">The resource key to remove.</param>
	public void Unregister(string key)
	{
		if (string.IsNullOrWhiteSpace(key))
		{
			return;
		}

		var removed = _resources.RemoveAll
		(
			resource => string.Equals(resource.Key, key, StringComparison.OrdinalIgnoreCase)
		);

		if (removed > 0)
		{
			RebuildUrlIndex();
		}
	}

	/// <summary>
	/// Saves the registry to disk.
	/// </summary>
	public void Save()
	{
		var directory = Path.GetDirectoryName(_filePath);
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}

		using var stream = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.None);
		using var writer = new Utf8JsonWriter
		(
			stream,
			new JsonWriterOptions
			{
				Indented = true,
			}
		);

		writer.WriteStartObject();

		foreach (var resource in _resources.OrderBy(GetSerializedKey, StringComparer.OrdinalIgnoreCase))
		{
			if (ShouldWriteLegacy(resource))
			{
				writer.WriteString(resource.Urls[0], resource.Key);
				continue;
			}

			writer.WritePropertyName(resource.Key);
			writer.WriteStartObject();
			WriteStringOrArray(writer, "names", resource.Names);
			WriteStringOrArray(writer, "urls", resource.Urls);

			if (!string.IsNullOrWhiteSpace(resource.Version))
			{
				writer.WriteString("version", resource.Version);
			}

			writer.WriteEndObject();
		}

		writer.WriteEndObject();
		writer.Flush();
	}

	private void Load()
	{
		if (!File.Exists(_filePath))
		{
			return;
		}

		using var stream = File.OpenRead(_filePath);
		if (stream.Length == 0)
		{
			return;
		}

		using var document = JsonDocument.Parse(stream);
		if (document.RootElement.ValueKind != JsonValueKind.Object)
		{
			throw new JsonException("Download registry root must be a JSON object.");
		}

		foreach (var property in document.RootElement.EnumerateObject())
		{
			_resources.Add(ParseEntry(property));
		}

		RebuildUrlIndex();
	}

	private static ResourceEntry ParseEntry(JsonProperty property)
	{
		return property.Value.ValueKind switch
		{
			JsonValueKind.String => ParseLegacyEntry(property.Name, property.Value),
			JsonValueKind.Object => ParseObjectEntry(property.Name, property.Value),
			_ => throw new JsonException($"Unsupported download registry value kind '{property.Value.ValueKind}'."),
		};
	}

	private static ResourceEntry ParseLegacyEntry(string url, JsonElement value)
	{
		var key = value.GetString() ?? string.Empty;
		return CreateEntry
		(
			key,
			[key],
			[url],
			version: null
		);
	}

	private static ResourceEntry ParseObjectEntry(string key, JsonElement value)
	{
		return CreateEntry
		(
			key,
			ReadStringOrArray(value, "names"),
			ReadStringOrArray(value, "urls"),
			ReadOptionalString(value, "version")
		);
	}

	private static ResourceEntry CreateEntry
	(
		string key,
		IEnumerable<string> names,
		IEnumerable<string> urls,
		string? version
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(key);

		var entry = new ResourceEntry
		{
			Key = key.Trim(),
			Version = NormalizeVersion(version),
		};

		SetNames(entry, names);
		SetUrls(entry, urls);
		return entry;
	}

	private static ResourceEntry NormalizeEntry(ResourceEntry entry)
	{
		return CreateEntry(entry.Key, entry.Names, entry.Urls, entry.Version);
	}

	private static void MergeInto(ResourceEntry target, ResourceEntry source)
	{
		SetNames(target, target.Names.Concat(source.Names));
		SetUrls(target, target.Urls.Concat(source.Urls));
	}

	private void RebuildUrlIndex()
	{
		_urlIndex.Clear();

		foreach (var resource in _resources)
		{
			foreach (var url in resource.Urls)
			{
				_urlIndex[url] = resource;
			}
		}
	}

	private static void SetNames(ResourceEntry entry, IEnumerable<string> names)
	{
		var candidates = names
			.Where(static name => !string.IsNullOrWhiteSpace(name))
			.Select(static name => name.Trim())
			.Distinct(NameComparer)
			.ToArray();

		var usable = candidates
			.Where(ModelDownloader.IsUsableName)
			.ToArray();

		var chosen = usable.Length > 0
			? usable
			: candidates;

		var ordered = chosen
			.OrderBy(static name => name.Length)
			.ThenBy(static name => name, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		entry.Names.Clear();
		entry.Names.AddRange(ordered);
	}

	private static void SetUrls(ResourceEntry entry, IEnumerable<string> urls)
	{
		var normalizedUrls = urls
			.Where(static url => !string.IsNullOrWhiteSpace(url))
			.Select(static url => ModelDownloader.NormalizeUrl(url.Trim()))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();

		entry.Urls.Clear();
		entry.Urls.AddRange(normalizedUrls);
	}

	private static string[] ReadStringOrArray(JsonElement parent, string propertyName)
	{
		if (!parent.TryGetProperty(propertyName, out var property))
		{
			return [];
		}

		return property.ValueKind switch
		{
			JsonValueKind.String => WrapString(property.GetString()),
			JsonValueKind.Array => property
				.EnumerateArray()
				.Select
				(
					static item => item.ValueKind == JsonValueKind.String
						? item.GetString()
						: throw new JsonException("Download registry arrays must contain only strings.")
				)
				.Where(static value => !string.IsNullOrWhiteSpace(value))
				.Cast<string>()
				.ToArray(),
			JsonValueKind.Null => [],
			_ => throw new JsonException($"Download registry property '{propertyName}' must be a string or array."),
		};
	}

	private static string? ReadOptionalString(JsonElement parent, string propertyName)
	{
		if (!parent.TryGetProperty(propertyName, out var property))
		{
			return null;
		}

		return property.ValueKind switch
		{
			JsonValueKind.Null => null,
			JsonValueKind.String => NormalizeVersion(property.GetString()),
			_ => throw new JsonException($"Download registry property '{propertyName}' must be a string or null."),
		};
	}

	private static string[] WrapString(string? value)
	{
		return string.IsNullOrWhiteSpace(value)
			? []
			: [value.Trim()];
	}

	private static void WriteStringOrArray(Utf8JsonWriter writer, string propertyName, List<string> values)
	{
		if (values.Count == 1)
		{
			writer.WriteString(propertyName, values[0]);
			return;
		}

		writer.WritePropertyName(propertyName);
		writer.WriteStartArray();

		foreach (var value in values)
		{
			writer.WriteStringValue(value);
		}

		writer.WriteEndArray();
	}

	private static string? NormalizeVersion(string? version)
	{
		return string.IsNullOrWhiteSpace(version)
			? null
			: version.Trim();
	}

	private static bool ShouldWriteLegacy(ResourceEntry resource)
	{
		return resource.Names.Count == 1
			&& resource.Urls.Count == 1
			&& string.Equals(resource.Names[0], resource.Key, StringComparison.Ordinal)
			&& IsLegacyVersion(resource.Version);
	}

	private static bool IsLegacyVersion(string? version)
	{
		if (string.IsNullOrWhiteSpace(version))
		{
			return true;
		}

		return Version.TryParse(version, out var parsedVersion)
			&& parsedVersion.CompareTo(LegacyCutoffVersion) <= 0;
	}

	private static string GetSerializedKey(ResourceEntry resource)
	{
		return ShouldWriteLegacy(resource)
			? resource.Urls[0]
			: resource.Key;
	}

	private static string GetCurrentAppVersion()
	{
		var informationalVersion = typeof(DownloadRegistry).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
			?.InformationalVersion;

		return string.IsNullOrWhiteSpace(informationalVersion)
			? "0.0.0"
			: informationalVersion.Split('+')[0];
	}

	private sealed record NameCandidate(ResourceEntry Entry, string Name);
}

/// <summary>
/// Describes a downloaded resource and the URLs that identify it.
/// </summary>
sealed class ResourceEntry
{
	/// <summary>
	/// Gets or sets the stable resource key.
	/// </summary>
	public string Key { get; set; } = string.Empty;

	/// <summary>
	/// Gets the known display names for the resource.
	/// </summary>
	public List<string> Names { get; } = [];

	/// <summary>
	/// Gets the known URLs for the resource.
	/// </summary>
	public List<string> Urls { get; } = [];

	/// <summary>
	/// Gets or sets the app version that last wrote the entry.
	/// </summary>
	public string? Version { get; set; }
}
