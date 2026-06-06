using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Talktastic.Tests;

public sealed class PthLoaderTests
{
	[Fact]
	public void Load_MinimalValidPth_ReturnsModel()
	{
		var config = new List<object> { 48000 };
		var bytes = BuildPthZip
		(
			config,
			new Dictionary<string, (int[] shape, string dtype, byte[] data)>(StringComparer.Ordinal)
			{
				["layer.weight"] = ([2, 2], "float16", Float16Bytes(1f, 2f, 3f, 4f)),
			}
		);
		var path = Path.Combine(AppContext.BaseDirectory, $"{nameof(PthLoaderTests)}-{Guid.NewGuid():N}.pth");

		try
		{
			File.WriteAllBytes(path, bytes);

			var model = PthLoader.Load(path);

			Assert.Equal(config, model.Config);
			Assert.Equal(48000, model.TargetSampleRate);
			Assert.Equal("v2", model.Version);
			Assert.Equal("48k", model.SampleRateLabel);
			Assert.Equal(1, model.F0);
			Assert.Equal("test", model.Info);
			var weight = Assert.Single(model.Weights);
			Assert.Equal("layer.weight", weight.Key);
			Assert.Equal([2, 2], weight.Value.Shape);
		}
		finally
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
	}

	[Fact]
	public void Load_MultipleWeights_AllLoaded()
	{
		var bytes = BuildPthZip
		(
			[48000],
			new Dictionary<string, (int[] shape, string dtype, byte[] data)>(StringComparer.Ordinal)
			{
				["encoder.weight"] = ([2, 3], "float16", Float16Bytes(1f, 2f, 3f, 4f, 5f, 6f)),
				["decoder.bias"] = ([4], "float16", Float16Bytes(7f, 8f, 9f, 10f)),
				["proj.weight"] = ([1, 1, 2], "float16", Float16Bytes(11f, 12f)),
			}
		);

		using var stream = new MemoryStream(bytes, writable: false);

		var model = PthLoader.Load(stream);

		Assert.Equal(3, model.Weights.Count);
		Assert.Equal([2, 3], model.Weights["encoder.weight"].Shape);
		Assert.Equal([4], model.Weights["decoder.bias"].Shape);
		Assert.Equal([1, 1, 2], model.Weights["proj.weight"].Shape);
	}

	[Fact]
	public void Load_Float32Storage_CorrectElementSize()
	{
		var expected = Float32Bytes(1f, 2f, 3f, 4f);
		var bytes = BuildPthZip
		(
			[32000],
			new Dictionary<string, (int[] shape, string dtype, byte[] data)>(StringComparer.Ordinal)
			{
				["layer.weight"] = ([2, 2], "float32", expected),
			}
		);

		using var stream = new MemoryStream(bytes, writable: false);

		var model = PthLoader.Load(stream);
		var tensor = model.Weights["layer.weight"];

		Assert.Equal("float32", tensor.DType);
		Assert.Equal(4, tensor.ElementCount);
		Assert.Equal(16, tensor.Data.Length);
		Assert.Equal(expected, tensor.Data);
	}

	[Fact]
	public void Load_MissingDataPkl_ThrowsInvalidDataException()
	{
		var bytes = BuildArchive(null, []);

		using var stream = new MemoryStream(bytes, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Contains("data.pkl", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Load_EmptyConfig_ThrowsInvalidDataException()
	{
		var bytes = BuildCustomPthZip
		(
			config: [],
			weights: new Dictionary<string, (int[] shape, string dtype, byte[] data)>(StringComparer.Ordinal)
		);

		using var stream = new MemoryStream(bytes, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Checkpoint config is empty.", exception.Message);
	}

	[Fact]
	public void Load_MissingWeightKey_ThrowsInvalidDataException()
	{
		var bytes = BuildCustomPthZip(config: [48000], includeWeight: false);

		using var stream = new MemoryStream(bytes, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Checkpoint is missing required key 'weight'.", exception.Message);
	}

	[Fact]
	public void Load_MissingConfigKey_ThrowsInvalidDataException()
	{
		var bytes = BuildCustomPthZip
		(
			weights: new Dictionary<string, (int[] shape, string dtype, byte[] data)>(StringComparer.Ordinal),
			includeConfig: false
		);

		using var stream = new MemoryStream(bytes, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Checkpoint is missing required key 'config'.", exception.Message);
	}

	[Fact]
	public void Load_EncQWeights_AreSkipped()
	{
		var bytes = BuildPthZip
		(
			[48000],
			new Dictionary<string, (int[] shape, string dtype, byte[] data)>(StringComparer.Ordinal)
			{
				["enc_q.skip"] = ([2], "float16", Float16Bytes(99f, 100f)),
				["decoder.weight"] = ([2], "float16", Float16Bytes(1f, 2f)),
			}
		);

		using var stream = new MemoryStream(bytes, writable: false);

		var model = PthLoader.Load(stream);

		var weight = Assert.Single(model.Weights);
		Assert.Equal("decoder.weight", weight.Key);
	}

	[Fact]
	public void Load_NonSeekableStream_StillWorks()
	{
		var bytes = BuildPthZip
		(
			[44100],
			new Dictionary<string, (int[] shape, string dtype, byte[] data)>(StringComparer.Ordinal)
			{
				["speaker.emb"] = ([3], "float16", Float16Bytes(1f, 2f, 3f)),
			}
		);

		using var stream = new NonSeekableReadStream(bytes);

		var model = PthLoader.Load(stream);

		Assert.Equal(44100, model.TargetSampleRate);
		var tensor = Assert.Single(model.Weights).Value;
		Assert.Equal([3], tensor.Shape);
		Assert.Equal(Float16Bytes(1f, 2f, 3f), tensor.Data);
	}

	[Fact]
	public void PthTensor_ElementCount_ComputedCorrectly()
	{
		var tensor = new PthTensor("tensor", new byte[120], [3, 4, 5], "float16");

		Assert.Equal(60, tensor.ElementCount);
	}

	[Fact]
	public void PthTensor_EmptyShape_ElementCountIsOne()
	{
		var tensor = new PthTensor("scalar", [0x00, 0x00], [], "float16");

		Assert.Equal(1, tensor.ElementCount);
	}

	[Fact]
	public void PthTensor_NullName_ThrowsArgumentException()
	{
		string? name = null;

		Assert.ThrowsAny<ArgumentException>
		(
			() => new PthTensor(name!, [], [], "float16")
		);
	}

	[Fact]
	public void Load_WeightNormFusion_FusesGAndV()
	{
		var bytes = BuildPthZip
		(
			[48000],
			new Dictionary<string, (int[] shape, string dtype, byte[] data)>(StringComparer.Ordinal)
			{
				["layer.weight_g"] = ([2], "float32", Float32Bytes(10f, 26f)),
				["layer.weight_v"] = ([2, 2], "float32", Float32Bytes(3f, 4f, 5f, 12f)),
			}
		);

		using var stream = new MemoryStream(bytes, writable: false);

		var model = PthLoader.Load(stream);

		var weight = Assert.Single(model.Weights);
		Assert.Equal("layer.weight", weight.Key);
		Assert.Equal([2, 2], weight.Value.Shape);
		Assert.Equal("float32", weight.Value.DType);
		Assert.Equal(Float32Bytes(6f, 8f, 10f, 24f), weight.Value.Data);
	}

	private static byte[] BuildPthZip
	(
		List<object> config,
		Dictionary<string, (int[] shape, string dtype, byte[] data)> weights,
		string version = "v2",
		string sr = "48k",
		int f0 = 1,
		string info = "test"
	)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(weights);

		return BuildCustomPthZip(config, weights, version, sr, f0, info);
	}

	private static byte[] BuildCustomPthZip
	(
		List<object>? config = null,
		Dictionary<string, (int[] shape, string dtype, byte[] data)>? weights = null,
		string version = "v2",
		string sr = "48k",
		int f0 = 1,
		string info = "test",
		bool includeConfig = true,
		bool includeWeight = true
	)
	{
		var tensorEntries = BuildTensorEntries(weights ?? new Dictionary<string, (int[] shape, string dtype, byte[] data)>(StringComparer.Ordinal));
		var pickle = BuildRootPickle(config, tensorEntries, version, sr, f0, info, includeConfig, includeWeight);
		var storages = includeWeight
			? tensorEntries.Select(static entry => (entry.StorageKey, entry.Data))
			: [];

		return BuildArchive(pickle, storages);
	}

	private static List<TensorEntry> BuildTensorEntries
	(
		Dictionary<string, (int[] shape, string dtype, byte[] data)> weights
	)
	{
		var index = 0;
		var entries = new List<TensorEntry>(weights.Count);

		foreach (var pair in weights)
		{
			entries.Add
			(
				new TensorEntry
				(
					pair.Key,
					index.ToString(CultureInfo.InvariantCulture),
					[.. pair.Value.shape],
					pair.Value.dtype,
					[.. pair.Value.data]
				)
			);
			index++;
		}

		return entries;
	}

	private static byte[] BuildRootPickle
	(
		List<object>? config,
		List<TensorEntry> weights,
		string version,
		string sr,
		int f0,
		string info,
		bool includeConfig,
		bool includeWeight
	)
	{
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteEmptyDictionary();

		if (includeConfig)
		{
			writer.WriteString("config");
			writer.WriteValue(config ?? []);
			writer.WriteSetItem();
		}

		if (includeWeight)
		{
			writer.WriteString("weight");
			writer.WriteWeightDictionary(weights);
			writer.WriteSetItem();
		}

		writer.WriteString("version");
		writer.WriteString(version);
		writer.WriteSetItem();

		writer.WriteString("sr");
		writer.WriteString(sr);
		writer.WriteSetItem();

		writer.WriteString("f0");
		writer.WriteInt32(f0);
		writer.WriteSetItem();

		writer.WriteString("info");
		writer.WriteString(info);
		writer.WriteSetItem();

		writer.WriteStop();
		return writer.ToArray();
	}

	private static byte[] BuildArchive
	(
		byte[]? pickle,
		IEnumerable<(string StorageKey, byte[] Data)> storages
	)
	{
		using var stream = new MemoryStream();
		using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
		{
			if (pickle is not null)
			{
				var pickleEntry = archive.CreateEntry("archive/data.pkl");
				using var pickleStream = pickleEntry.Open();
				pickleStream.Write(pickle);
			}

			foreach (var storage in storages)
			{
				var entry = archive.CreateEntry($"archive/data/{storage.StorageKey}");
				using var entryStream = entry.Open();
				entryStream.Write(storage.Data);
			}
		}

		return stream.ToArray();
	}

	private static int[] ComputeContiguousStride(int[] shape)
	{
		if (shape.Length == 0)
		{
			return [];
		}

		var stride = new int[shape.Length];
		var running = 1;
		for (var index = shape.Length - 1; index >= 0; index--)
		{
			stride[index] = running;
			running *= shape[index];
		}

		return stride;
	}

	private static int ComputeElementCount(int[] shape)
	{
		if (shape.Length == 0)
		{
			return 1;
		}

		var count = 1;
		foreach (var dimension in shape)
		{
			count *= dimension;
		}

		return count;
	}

	private static byte[] Float16Bytes(params float[] values)
	{
		var bytes = new byte[values.Length * sizeof(ushort)];
		for (var index = 0; index < values.Length; index++)
		{
			BinaryPrimitives.WriteUInt16LittleEndian
			(
				bytes.AsSpan(index * sizeof(ushort), sizeof(ushort)),
				BitConverter.HalfToUInt16Bits((Half)values[index])
			);
		}

		return bytes;
	}

	private static byte[] Float32Bytes(params float[] values)
	{
		var bytes = new byte[values.Length * sizeof(float)];
		for (var index = 0; index < values.Length; index++)
		{
			BinaryPrimitives.WriteInt32LittleEndian
			(
				bytes.AsSpan(index * sizeof(float), sizeof(float)),
				BitConverter.SingleToInt32Bits(values[index])
			);
		}

		return bytes;
	}

	private sealed record TensorEntry
	(
		string Name,
		string StorageKey,
		int[] Shape,
		string DType,
		byte[] Data
	);

	private sealed class PickleWriter
	{
		private readonly List<byte> _buffer = [];

		public byte[] ToArray()
		{
			return [.. _buffer];
		}

		public void WriteProtocol2()
		{
			_buffer.Add(0x80);
			_buffer.Add(0x02);
		}

		public void WriteEmptyDictionary()
		{
			_buffer.Add(0x7D);
		}

		public void WriteEmptyList()
		{
			_buffer.Add(0x5D);
		}

		public void WriteMark()
		{
			_buffer.Add(0x28);
		}

		public void WriteTuple()
		{
			_buffer.Add(0x74);
		}

		public void WriteSetItem()
		{
			_buffer.Add(0x73);
		}

		public void WriteAppend()
		{
			_buffer.Add(0x61);
		}

		public void WriteStackGlobal()
		{
			_buffer.Add(0x93);
		}

		public void WriteBinPersId()
		{
			_buffer.Add(0x51);
		}

		public void WriteReduce()
		{
			_buffer.Add(0x52);
		}

		public void WriteStop()
		{
			_buffer.Add(0x2E);
		}

		public void WriteString(string value)
		{
			var bytes = Encoding.UTF8.GetBytes(value);
			_buffer.Add(0x8C);
			_buffer.Add((byte)bytes.Length);
			_buffer.AddRange(bytes);
		}

		public void WriteInt32(int value)
		{
			_buffer.Add(0x4A);
			var bytes = BitConverter.GetBytes(value);
			_buffer.AddRange(bytes);
		}

		public void WriteValue(object? value)
		{
			switch (value)
			{
				case null:
					_buffer.Add(0x4E);
					return;

				case int intValue:
					WriteInt32(intValue);
					return;

				case long longValue when longValue is >= int.MinValue and <= int.MaxValue:
					WriteInt32((int)longValue);
					return;

				case string stringValue:
					WriteString(stringValue);
					return;

				case bool boolValue:
					_buffer.Add(boolValue ? (byte)0x88 : (byte)0x89);
					return;

				case byte[] bytes:
					WriteBytes(bytes);
					return;

				case List<object> list:
					WriteList(list);
					return;

				case object[] tuple:
					WriteTuple(tuple);
					return;

				default:
					throw new NotSupportedException($"Unsupported test pickle value type '{value.GetType().FullName}'.");
			}
		}

		public void WriteList(IEnumerable<object> values)
		{
			WriteEmptyList();
			foreach (var value in values)
			{
				WriteValue(value);
				WriteAppend();
			}
		}

		public void WriteTuple(IEnumerable<object?> values)
		{
			WriteMark();
			foreach (var value in values)
			{
				WriteValue(value);
			}

			WriteTuple();
		}

		public void WriteWeightDictionary(IEnumerable<TensorEntry> entries)
		{
			WriteEmptyDictionary();
			foreach (var entry in entries)
			{
				WriteString(entry.Name);
				WriteTensor(entry);
				WriteSetItem();
			}
		}

		private void WriteBytes(byte[] value)
		{
			_buffer.Add(0x42);
			_buffer.AddRange(BitConverter.GetBytes(value.Length));
			_buffer.AddRange(value);
		}

		private void WriteGlobalReference(string module, string name)
		{
			WriteString(module);
			WriteString(name);
			WriteStackGlobal();
		}

		private void WriteTensor(TensorEntry entry)
		{
			WriteGlobalReference("torch._utils", "_rebuild_tensor_v2");
			WriteMark();
			WritePersistentStorageReference(entry);
			WriteInt32(0);
			WriteTuple(entry.Shape.Select(static value => (object)value));
			WriteTuple(ComputeContiguousStride(entry.Shape).Select(static value => (object)value));
			WriteTuple();
			WriteReduce();
		}

		private void WritePersistentStorageReference(TensorEntry entry)
		{
			WriteMark();
			WriteString("storage");
			WriteGlobalReference("torch", GetStorageTypeName(entry.DType));
			WriteString(entry.StorageKey);
			WriteString("cpu");
			WriteInt32(ComputeElementCount(entry.Shape));
			WriteTuple();
			WriteBinPersId();
		}
	}

	private sealed class NonSeekableReadStream : Stream
	{
		private readonly MemoryStream _inner;

		public NonSeekableReadStream(byte[] data)
		{
			_inner = new MemoryStream(data, writable: false);
		}

		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => false;

		public override long Length => _inner.Length;

		public override long Position
		{
			get => _inner.Position;
			set => throw new NotSupportedException();
		}

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			return _inner.Read(buffer, offset, count);
		}

		public override int Read(Span<byte> buffer)
		{
			return _inner.Read(buffer);
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				_inner.Dispose();
			}

			base.Dispose(disposing);
		}
	}

	private static string GetStorageTypeName(string dtype)
	{
		return dtype switch
		{
			"float16" => "HalfStorage",
			"float32" => "FloatStorage",
			_ => throw new NotSupportedException($"Unsupported test dtype '{dtype}'."),
		};
	}
}
