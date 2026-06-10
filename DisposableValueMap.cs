namespace Talktastic;

/// <summary>
/// A keyed collection of <see cref="IDisposable"/> values that disposes every value when the
/// collection itself is disposed. Used to build ONNX Runtime input sets so that no input tensor
/// can be leaked -- a single <c>using</c> on the collection releases them all, which matters on
/// memory-constrained GPUs where leaked native buffers accumulate and corrupt later inferences.
/// </summary>
/// <typeparam name="T">The disposable value type.</typeparam>
internal sealed class DisposableValueMap<T> : IDisposable
	where T : IDisposable
{
	private readonly Dictionary<string, T> _items = new(StringComparer.Ordinal);

	/// <summary>
	/// Gets the underlying key/value pairs (e.g. to pass as ONNX Runtime inputs).
	/// </summary>
	public IReadOnlyDictionary<string, T> Items => _items;

	/// <summary>
	/// Gets the number of values currently held.
	/// </summary>
	public int Count => _items.Count;

	/// <summary>
	/// Adds a value under the given key and returns it. The collection takes ownership and will
	/// dispose the value when the collection is disposed.
	/// </summary>
	/// <param name="key">The key.</param>
	/// <param name="value">The disposable value to take ownership of.</param>
	/// <returns>The added value.</returns>
	public T Add(string key, T value)
	{
		ArgumentNullException.ThrowIfNull(key);
		ArgumentNullException.ThrowIfNull(value);
		_items[key] = value;
		return value;
	}

	/// <summary>
	/// Disposes every contained value and clears the collection.
	/// </summary>
	public void Dispose()
	{
		foreach (var value in _items.Values)
		{
			value.Dispose();
		}

		_items.Clear();
	}
}
