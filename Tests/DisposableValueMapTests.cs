namespace Talktastic.Tests;

#pragma warning disable CA2000 // DisposableValueMap takes ownership; these tests assert that disposal.

public sealed class DisposableValueMapTests
{
	private sealed class Tracked : IDisposable
	{
		public int DisposeCount { get; private set; }

		public void Dispose()
		{
			DisposeCount++;
		}
	}

	[Fact]
	public void Add_StoresValueAndExposesItThroughItems()
	{
		using var map = new DisposableValueMap<Tracked>();
		var value = new Tracked();

		var returned = map.Add("a", value);

		Assert.Same(value, returned);
		Assert.Equal(1, map.Count);
		Assert.Same(value, map.Items["a"]);
	}

	[Fact]
	public void Add_NullKey_Throws()
	{
		using var map = new DisposableValueMap<Tracked>();

		Assert.Throws<ArgumentNullException>(() => map.Add(null!, new Tracked()));
	}

	[Fact]
	public void Add_NullValue_Throws()
	{
		using var map = new DisposableValueMap<Tracked>();

		Assert.Throws<ArgumentNullException>(() => map.Add("a", null!));
	}

	[Fact]
	public void Dispose_DisposesEveryContainedValue()
	{
		var first = new Tracked();
		var second = new Tracked();
		var third = new Tracked();

		var map = new DisposableValueMap<Tracked>();
		map.Add("first", first);
		map.Add("second", second);
		map.Add("third", third);

		map.Dispose();

		Assert.Equal(1, first.DisposeCount);
		Assert.Equal(1, second.DisposeCount);
		Assert.Equal(1, third.DisposeCount);
		Assert.Equal(0, map.Count);
	}

	[Fact]
	public void Add_DuplicateKey_ReplacesValueWithoutGrowingCount()
	{
		using var map = new DisposableValueMap<Tracked>();
		var original = new Tracked();
		var replacement = new Tracked();

		map.Add("key", original);
		map.Add("key", replacement);

		Assert.Equal(1, map.Count);
		Assert.Same(replacement, map.Items["key"]);
	}
}
