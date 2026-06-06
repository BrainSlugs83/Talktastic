namespace Talktastic.Tests;

public sealed class AppPathsTests : IDisposable
{
	private readonly string _artifactRoot;
	private readonly string[] _originalSearchBases;

	public AppPathsTests()
	{
		_artifactRoot = Path.Combine(AppContext.BaseDirectory, nameof(AppPathsTests), Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_artifactRoot);
		_originalSearchBases = [.. AppPaths.SearchBases];
	}

	public void Dispose()
	{
		Array.Copy(_originalSearchBases, AppPaths.SearchBases, _originalSearchBases.Length);

		try
		{
			if (Directory.Exists(_artifactRoot))
			{
				Directory.Delete(_artifactRoot, recursive: true);
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	[Fact]
	public void SearchBases_DefaultValues_ContainsLocalAppDataTempAndStartupCurrentDirectory()
	{
		Assert.Equal(3, AppPaths.SearchBases.Length);
		Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Talktastic"), AppPaths.SearchBases[0]);
		Assert.Equal(Path.GetTempPath(), AppPaths.SearchBases[1]);
		Assert.Equal(_originalSearchBases[2], AppPaths.SearchBases[2]);
	}

	[Fact]
	public void FindExistingDir_ExistingSubdirectory_ReturnsFirstMatchingPath()
	{
		var searchBases = ConfigureSearchBases();
		var expected = Path.Combine(searchBases[1], "voices", "existing");
		Directory.CreateDirectory(expected);

		var found = AppPaths.FindExistingDir(Path.Combine("voices", "existing"));

		Assert.Equal(expected, found);
	}

	[Fact]
	public void FindExistingDir_MissingSubdirectory_ReturnsNull()
	{
		ConfigureSearchBases();

		var found = AppPaths.FindExistingDir(Path.Combine("models", "missing"));

		Assert.Null(found);
	}

	[Fact]
	public void EnsureDir_MissingDirectory_CreatesDirectoryUnderFirstSearchBase()
	{
		var searchBases = ConfigureSearchBases();

		var created = AppPaths.EnsureDir(Path.Combine("native", "cache"));

		Assert.Equal(Path.Combine(searchBases[0], "native", "cache"), created);
		Assert.True(Directory.Exists(created));
	}

	private string[] ConfigureSearchBases()
	{
		var bases = new[]
		{
			Path.Combine(_artifactRoot, "base-0"),
			Path.Combine(_artifactRoot, "base-1"),
			Path.Combine(_artifactRoot, "base-2"),
		};

		Array.Copy(bases, AppPaths.SearchBases, bases.Length);
		return bases;
	}
}
