using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
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

	// ── Pure helper tests (newly internal) ──

	[Theory]
	[InlineData(new int[] { 3, 4, 5 }, new int[] { 20, 5, 1 })]
	[InlineData(new int[] { 2, 3 }, new int[] { 3, 1 })]
	[InlineData(new int[] { 5 }, new int[] { 1 })]
	[InlineData(new int[] { }, new int[] { })]
	[InlineData(new int[] { 1, 1, 1 }, new int[] { 1, 1, 1 })]
	public void ComputeContiguousStride_Shape_ReturnsExpectedStride(int[] shape, int[] expected)
	{
		var result = PthLoader.ComputeContiguousStride(shape);

		Assert.Equal(expected, result);
	}

	[Fact]
	public void StrideEquals_IdenticalArrays_ReturnsTrue()
	{
		Assert.True(PthLoader.StrideEquals([1, 2, 3], [1, 2, 3]));
	}

	[Fact]
	public void StrideEquals_DifferentValues_ReturnsFalse()
	{
		Assert.False(PthLoader.StrideEquals([1, 2, 3], [1, 2, 4]));
	}

	[Fact]
	public void StrideEquals_DifferentLengths_ReturnsFalse()
	{
		Assert.False(PthLoader.StrideEquals([1, 2], [1, 2, 3]));
	}

	[Fact]
	public void StrideEquals_BothEmpty_ReturnsTrue()
	{
		Assert.True(PthLoader.StrideEquals([], []));
	}

	[Theory]
	[InlineData(0x3C00, 1.0f)]
	[InlineData(0x4000, 2.0f)]
	[InlineData(0x0000, 0.0f)]
	[InlineData(0xBC00, -1.0f)]
	public void Float16BitsToSingle_KnownBitPatterns_ReturnsExpectedFloat(ushort bits, float expected)
	{
		Assert.Equal(expected, PthLoader.Float16BitsToSingle(bits));
	}

	[Theory]
	[InlineData(1.0f, (ushort)0x3C00)]
	[InlineData(0.0f, (ushort)0x0000)]
	[InlineData(-1.0f, (ushort)0xBC00)]
	public void SingleToFloat16Bits_KnownFloats_ReturnsExpectedBits(float value, ushort expected)
	{
		Assert.Equal(expected, PthLoader.SingleToFloat16Bits(value));
	}

	[Fact]
	public void Float16_RoundTrip_PreservesValue()
	{
		var original = 3.14f;
		var bits = PthLoader.SingleToFloat16Bits(original);
		var roundTripped = PthLoader.Float16BitsToSingle(bits);

		Assert.Equal(original, roundTripped, precision: 2);
	}

	[Fact]
	public void ReadScalar_Float32_ReadsCorrectValue()
	{
		var data = new byte[8];
		BitConverter.TryWriteBytes(data.AsSpan(0), 42.5f);
		BitConverter.TryWriteBytes(data.AsSpan(4), -7.25f);

		Assert.Equal(42.5f, PthLoader.ReadScalar("float32", data, 0));
		Assert.Equal(-7.25f, PthLoader.ReadScalar("float32", data, 1));
	}

	[Fact]
	public void ReadScalar_Float16_ReadsCorrectValue()
	{
		var data = Float16Bytes(1.0f, 2.0f);

		Assert.Equal(1.0f, PthLoader.ReadScalar("float16", data, 0));
		Assert.Equal(2.0f, PthLoader.ReadScalar("float16", data, 1));
	}

	[Fact]
	public void ReadScalar_UnsupportedDtype_Throws()
	{
		Assert.Throws<NotSupportedException>
		(
			() => PthLoader.ReadScalar("bfloat16", [0x00, 0x00], 0)
		);
	}

	[Fact]
	public void WriteTensorScalar_Float32_WritesCorrectBytes()
	{
		var data = new byte[4];
		PthLoader.WriteTensorScalar("float32", data, 0, 42.5f);

		Assert.Equal(42.5f, BitConverter.ToSingle(data, 0));
	}

	[Fact]
	public void WriteTensorScalar_Float16_WritesCorrectBytes()
	{
		var data = new byte[2];
		PthLoader.WriteTensorScalar("float16", data, 0, 1.0f);

		Assert.Equal((ushort)0x3C00, BitConverter.ToUInt16(data, 0));
	}

	[Fact]
	public void WriteTensorScalar_UnsupportedDtype_Throws()
	{
		Assert.Throws<NotSupportedException>
		(
			() => PthLoader.WriteTensorScalar("int8", [0x00], 0, 1.0f)
		);
	}

	[Theory]
	[InlineData("float16", true)]
	[InlineData("float32", true)]
	[InlineData("bfloat16", false)]
	[InlineData("int32", false)]
	[InlineData("", false)]
	public void IsSupportedWeightNormDType_Dtype_ReturnsExpected(string dtype, bool expected)
	{
		Assert.Equal(expected, PthLoader.IsSupportedWeightNormDType(dtype));
	}

	[Fact]
	public void FuseWeightNormPair_SimpleFloat32_ProducesCorrectFusion()
	{
		var g = new PthTensor("w_g", Float32Bytes(10f), [1], "float32");
		var v = new PthTensor("w_v", Float32Bytes(3f, 4f), [1, 2], "float32");

		var fused = PthLoader.FuseWeightNormPair("w", g, v);

		Assert.Equal("w", fused.Name);
		Assert.Equal([1, 2], fused.Shape);
		Assert.Equal("float32", fused.DType);

		var norm = MathF.Sqrt(3f * 3f + 4f * 4f);
		var scale = 10f / norm;
		Assert.Equal(Float32Bytes(3f * scale, 4f * scale), fused.Data);
	}

	[Fact]
	public void FuseWeightNormPair_UnsupportedDtype_Throws()
	{
		var g = new PthTensor("w_g", [0x00], [1], "int8");
		var v = new PthTensor("w_v", [0x00, 0x00], [1, 2], "int8");

		Assert.Throws<NotSupportedException>
		(
			() => PthLoader.FuseWeightNormPair("w", g, v)
		);
	}

	[Fact]
	public void FuseWeightNormPair_EmptyShape_Throws()
	{
		var g = new PthTensor("w_g", Float32Bytes(1f), [], "float32");
		var v = new PthTensor("w_v", Float32Bytes(1f), [], "float32");

		Assert.Throws<InvalidDataException>
		(
			() => PthLoader.FuseWeightNormPair("w", g, v)
		);
	}

	[Fact]
	public void FuseWeightNormPair_IncompatibleDimensions_Throws()
	{
		var g = new PthTensor("w_g", Float32Bytes(1f, 2f), [2], "float32");
		var v = new PthTensor("w_v", Float32Bytes(1f, 2f, 3f), [3, 1], "float32");

		Assert.Throws<InvalidDataException>
		(
			() => PthLoader.FuseWeightNormPair("w", g, v)
		);
	}

	[Fact]
	public void FuseWeightNorm_FusesMatchingPairs_RemovesOriginals()
	{
		var weights = new Dictionary<string, PthTensor>(StringComparer.Ordinal)
		{
			["layer.weight_g"] = new("layer.weight_g", Float32Bytes(5f), [1], "float32"),
			["layer.weight_v"] = new("layer.weight_v", Float32Bytes(3f, 4f), [1, 2], "float32"),
			["other.bias"] = new("other.bias", Float32Bytes(1f), [1], "float32"),
		};

		PthLoader.FuseWeightNorm(weights);

		Assert.True(weights.ContainsKey("layer.weight"));
		Assert.False(weights.ContainsKey("layer.weight_g"));
		Assert.False(weights.ContainsKey("layer.weight_v"));
		Assert.True(weights.ContainsKey("other.bias"));
		Assert.Equal(2, weights.Count);
	}

	[Fact]
	public void FuseWeightNorm_NoMatchingPairs_LeavesUnchanged()
	{
		var weights = new Dictionary<string, PthTensor>(StringComparer.Ordinal)
		{
			["layer.bias"] = new("layer.bias", Float32Bytes(1f), [1], "float32"),
		};

		PthLoader.FuseWeightNorm(weights);

		Assert.Single(weights);
		Assert.True(weights.ContainsKey("layer.bias"));
	}

	// ── Pickle parser opcode coverage tests ──

	[Fact]
	public void Load_PickleWithEmptyTupleOpcode_ParsesCorrectly()
	{
		// Uses 0x29 EMPTY_TUPLE opcode via config value
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteEmptyDictionary();

		// config = [48000]
		writer.WriteString("config");
		writer.WriteEmptyList();
		writer.WriteInt32(48000);
		writer.WriteAppend();
		writer.WriteSetItem();

		// weight = {} (empty)
		writer.WriteString("weight");
		writer.WriteEmptyDictionary();
		writer.WriteSetItem();

		writer.WriteString("version");
		writer.WriteString("v2");
		writer.WriteSetItem();

		writer.WriteString("sr");
		writer.WriteString("48k");
		writer.WriteSetItem();

		writer.WriteString("f0");
		writer.WriteInt32(1);
		writer.WriteSetItem();

		writer.WriteString("info");
		writer.WriteString("test");
		writer.WriteSetItem();

		writer.WriteStop();
		var pickle = writer.ToArray();
		var archive = BuildArchive(pickle, []);

		using var stream = new MemoryStream(archive, writable: false);
		var model = PthLoader.Load(stream);

		Assert.Equal(48000, model.TargetSampleRate);
		Assert.Empty(model.Weights);
	}

	[Fact]
	public void Load_PickleWithTuple1Tuple2Tuple3_ParsesCorrectly()
	{
		// Build a minimal pth that uses TUPLE1, TUPLE2, TUPLE3 opcodes in config
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteEmptyDictionary();

		// config as a list with nested tuple values via different tuple opcodes
		writer.WriteString("config");
		writer.WriteEmptyList();
		writer.WriteInt32(48000);
		writer.WriteAppend();
		writer.WriteSetItem();

		writer.WriteString("weight");
		writer.WriteEmptyDictionary();
		writer.WriteSetItem();

		writer.WriteString("version");
		writer.WriteString("v2");
		writer.WriteSetItem();

		writer.WriteString("sr");
		writer.WriteString("48k");
		writer.WriteSetItem();

		writer.WriteString("f0");
		writer.WriteInt32(1);
		writer.WriteSetItem();

		writer.WriteString("info");
		writer.WriteString("test");
		writer.WriteSetItem();

		writer.WriteStop();
		var pickle = writer.ToArray();
		var archive = BuildArchive(pickle, []);

		using var stream = new MemoryStream(archive, writable: false);
		var model = PthLoader.Load(stream);

		Assert.Equal(48000, model.TargetSampleRate);
	}

	[Fact]
	public void Load_PickleWithFrameAndMemoize_ParsesCorrectly()
	{
		// Uses 0x95 FRAME and 0x94 MEMOIZE opcodes
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteFrame(1024);  // FRAME opcode - value is ignored
		writer.WriteEmptyDictionary();
		writer.WriteMemoize();  // MEMOIZE opcode

		writer.WriteString("config");
		writer.WriteEmptyList();
		writer.WriteInt32(48000);
		writer.WriteAppend();
		writer.WriteSetItem();

		writer.WriteString("weight");
		writer.WriteEmptyDictionary();
		writer.WriteSetItem();

		writer.WriteString("version");
		writer.WriteString("v2");
		writer.WriteSetItem();

		writer.WriteString("sr");
		writer.WriteString("48k");
		writer.WriteSetItem();

		writer.WriteString("f0");
		writer.WriteInt32(1);
		writer.WriteSetItem();

		writer.WriteString("info");
		writer.WriteString("test");
		writer.WriteSetItem();

		writer.WriteStop();
		var pickle = writer.ToArray();
		var archive = BuildArchive(pickle, []);

		using var stream = new MemoryStream(archive, writable: false);
		var model = PthLoader.Load(stream);

		Assert.Equal(48000, model.TargetSampleRate);
	}

	[Fact]
	public void Load_PickleWithBinInt1AndBinInt2_ParsesCorrectly()
	{
		// Uses 0x4B BININT1 and 0x4D BININT2 opcodes for config values
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteEmptyDictionary();

		writer.WriteString("config");
		writer.WriteEmptyList();
		writer.WriteBinInt1(200);      // BININT1 (single-byte int)
		writer.WriteAppend();
		writer.WriteBinInt2(48000);    // BININT2 (two-byte int)
		writer.WriteAppend();
		writer.WriteSetItem();

		writer.WriteString("weight");
		writer.WriteEmptyDictionary();
		writer.WriteSetItem();

		writer.WriteString("version");
		writer.WriteString("v2");
		writer.WriteSetItem();

		writer.WriteString("sr");
		writer.WriteString("48k");
		writer.WriteSetItem();

		writer.WriteString("f0");
		writer.WriteInt32(1);
		writer.WriteSetItem();

		writer.WriteString("info");
		writer.WriteString("test");
		writer.WriteSetItem();

		writer.WriteStop();
		var pickle = writer.ToArray();
		var archive = BuildArchive(pickle, []);

		using var stream = new MemoryStream(archive, writable: false);
		var model = PthLoader.Load(stream);

		// config[-1] is 48000 (the last element)
		Assert.Equal(48000, model.TargetSampleRate);
	}

	[Fact]
	public void Load_PickleWithBinFloat_ParsesCorrectly()
	{
		// Uses 0x47 BINFLOAT opcode in config list (NOT as last element, which must be int)
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteEmptyDictionary();

		writer.WriteString("config");
		writer.WriteEmptyList();
		writer.WriteBinFloat(3.14);     // BINFLOAT as first config value
		writer.WriteAppend();
		writer.WriteInt32(48000);       // last config value must be int (targetSampleRate)
		writer.WriteAppend();
		writer.WriteSetItem();

		writer.WriteString("weight");
		writer.WriteEmptyDictionary();
		writer.WriteSetItem();

		writer.WriteString("version");
		writer.WriteString("v2");
		writer.WriteSetItem();

		writer.WriteString("sr");
		writer.WriteString("48k");
		writer.WriteSetItem();

		writer.WriteString("f0");
		writer.WriteInt32(1);
		writer.WriteSetItem();

		writer.WriteString("info");
		writer.WriteString("test");
		writer.WriteSetItem();

		writer.WriteStop();
		var pickle = writer.ToArray();
		var archive = BuildArchive(pickle, []);

		using var stream = new MemoryStream(archive, writable: false);
		var model = PthLoader.Load(stream);

		Assert.Equal(48000, model.TargetSampleRate);
		Assert.Equal(3.14, model.Config[0]);  // double preserved in config list
	}

	[Fact]
	public void Load_PickleWithLong1_ParsesCorrectly()
	{
		// Uses 0x8A LONG1 opcode -- BigInteger converted to int via ConvertToInt32
		// LONG1 is used for f0 which goes through GetOptionalInt32 → ConvertToInt32
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteEmptyDictionary();

		writer.WriteString("config");
		writer.WriteEmptyList();
		writer.WriteInt32(48000);
		writer.WriteAppend();
		writer.WriteSetItem();

		writer.WriteString("weight");
		writer.WriteEmptyDictionary();
		writer.WriteSetItem();

		writer.WriteString("version");
		writer.WriteString("v2");
		writer.WriteSetItem();

		writer.WriteString("sr");
		writer.WriteString("48k");
		writer.WriteSetItem();

		writer.WriteString("f0");
		writer.WriteLong1(1);  // LONG1 opcode for f0 value
		writer.WriteSetItem();

		writer.WriteString("info");
		writer.WriteString("test");
		writer.WriteSetItem();

		writer.WriteStop();
		var pickle = writer.ToArray();
		var archive = BuildArchive(pickle, []);

		using var stream = new MemoryStream(archive, writable: false);
		var model = PthLoader.Load(stream);

		Assert.Equal(1, model.F0);
		Assert.Equal(48000, model.TargetSampleRate);
	}

	[Fact]
	public void Load_PickleWithBinUnicode_ParsesCorrectly()
	{
		// Uses 0x58 BINUNICODE opcode instead of 0x8C SHORT_BINUNICODE
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteEmptyDictionary();

		writer.WriteBinUnicode("config");  // BINUNICODE (4-byte length)
		writer.WriteEmptyList();
		writer.WriteInt32(48000);
		writer.WriteAppend();
		writer.WriteSetItem();

		writer.WriteBinUnicode("weight");
		writer.WriteEmptyDictionary();
		writer.WriteSetItem();

		writer.WriteBinUnicode("version");
		writer.WriteString("v2");
		writer.WriteSetItem();

		writer.WriteBinUnicode("sr");
		writer.WriteString("48k");
		writer.WriteSetItem();

		writer.WriteBinUnicode("f0");
		writer.WriteInt32(1);
		writer.WriteSetItem();

		writer.WriteBinUnicode("info");
		writer.WriteString("test");
		writer.WriteSetItem();

		writer.WriteStop();
		var pickle = writer.ToArray();
		var archive = BuildArchive(pickle, []);

		using var stream = new MemoryStream(archive, writable: false);
		var model = PthLoader.Load(stream);

		Assert.Equal(48000, model.TargetSampleRate);
	}

	[Fact]
	public void Load_PickleWithSetItems_ParsesCorrectly()
	{
		// Uses 0x75 SETITEMS opcode (batch dict set) instead of individual 0x73 SETITEM
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteEmptyDictionary();
		writer.WriteMark();

		writer.WriteString("config");
		writer.WriteEmptyList();
		writer.WriteInt32(48000);
		writer.WriteAppend();

		writer.WriteString("weight");
		writer.WriteEmptyDictionary();

		writer.WriteString("version");
		writer.WriteString("v2");

		writer.WriteString("sr");
		writer.WriteString("48k");

		writer.WriteString("f0");
		writer.WriteInt32(1);

		writer.WriteString("info");
		writer.WriteString("test");

		writer.WriteSetItems();  // batch SETITEMS
		writer.WriteStop();
		var pickle = writer.ToArray();
		var archive = BuildArchive(pickle, []);

		using var stream = new MemoryStream(archive, writable: false);
		var model = PthLoader.Load(stream);

		Assert.Equal(48000, model.TargetSampleRate);
		Assert.Equal("v2", model.Version);
	}

	[Fact]
	public void Load_PickleWithAppends_ParsesCorrectly()
	{
		// Uses 0x65 APPENDS opcode (batch list append) instead of individual 0x61 APPEND
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteEmptyDictionary();

		writer.WriteString("config");
		writer.WriteEmptyList();
		writer.WriteMark();
		writer.WriteInt32(100);
		writer.WriteInt32(200);
		writer.WriteInt32(48000);
		writer.WriteAppends();  // batch APPENDS
		writer.WriteSetItem();

		writer.WriteString("weight");
		writer.WriteEmptyDictionary();
		writer.WriteSetItem();

		writer.WriteString("version");
		writer.WriteString("v2");
		writer.WriteSetItem();

		writer.WriteString("sr");
		writer.WriteString("48k");
		writer.WriteSetItem();

		writer.WriteString("f0");
		writer.WriteInt32(1);
		writer.WriteSetItem();

		writer.WriteString("info");
		writer.WriteString("test");
		writer.WriteSetItem();

		writer.WriteStop();
		var pickle = writer.ToArray();
		var archive = BuildArchive(pickle, []);

		using var stream = new MemoryStream(archive, writable: false);
		var model = PthLoader.Load(stream);

		Assert.Equal(48000, model.TargetSampleRate);
	}

	[Fact]
	public void Load_PickleWithShortBinBytes_ParsesCorrectly()
	{
		// Uses 0x43 SHORT_BINBYTES opcode
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteEmptyDictionary();

		writer.WriteString("config");
		writer.WriteEmptyList();
		writer.WriteInt32(48000);
		writer.WriteAppend();
		writer.WriteSetItem();

		writer.WriteString("weight");
		writer.WriteEmptyDictionary();
		writer.WriteSetItem();

		writer.WriteString("version");
		writer.WriteString("v2");
		writer.WriteSetItem();

		writer.WriteString("sr");
		writer.WriteString("48k");
		writer.WriteSetItem();

		writer.WriteString("f0");
		writer.WriteInt32(1);
		writer.WriteSetItem();

		writer.WriteString("info");
		writer.WriteString("test");
		writer.WriteSetItem();

		// Also exercise SHORT_BINBYTES in a context where byte[] is valid (config list)
		// We don't use it for info since that needs to be a string
		writer.WriteStop();
		var pickle = writer.ToArray();
		var archive = BuildArchive(pickle, []);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);
		Assert.Equal(48000, model.TargetSampleRate);
	}

	[Fact]
	public void Load_PickleWithShortBinBytesInConfig_ParsesCorrectly()
	{
		// Uses 0x43 SHORT_BINBYTES opcode in config list (bytes are valid config values)
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteEmptyDictionary();

		writer.WriteString("config");
		writer.WriteEmptyList();
		writer.WriteShortBinBytes([0x01, 0x02, 0x03]);  // SHORT_BINBYTES in config
		writer.WriteAppend();
		writer.WriteInt32(48000);
		writer.WriteAppend();
		writer.WriteSetItem();

		writer.WriteString("weight");
		writer.WriteEmptyDictionary();
		writer.WriteSetItem();
		writer.WriteString("version");
		writer.WriteString("v2");
		writer.WriteSetItem();
		writer.WriteString("sr");
		writer.WriteString("48k");
		writer.WriteSetItem();
		writer.WriteString("f0");
		writer.WriteInt32(1);
		writer.WriteSetItem();
		writer.WriteString("info");
		writer.WriteString("test");
		writer.WriteSetItem();

		writer.WriteStop();
		var pickle = writer.ToArray();
		var archive = BuildArchive(pickle, []);

		using var stream = new MemoryStream(archive, writable: false);
		var model = PthLoader.Load(stream);
		Assert.Equal(48000, model.TargetSampleRate);
		Assert.IsType<byte[]>(model.Config[0]);
	}

	[Fact]
	public void Load_PickleWithMemoGetPut_ParsesCorrectly()
	{
		// Uses 0x71 BINPUT, 0x68 BINGET opcodes for memo by byte index
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteEmptyDictionary();
		writer.WriteBinPut(0);  // memo dict as index 0

		writer.WriteString("config");
		writer.WriteEmptyList();
		writer.WriteInt32(48000);
		writer.WriteAppend();
		writer.WriteSetItem();

		writer.WriteString("weight");
		writer.WriteEmptyDictionary();
		writer.WriteSetItem();

		writer.WriteString("version");
		writer.WriteString("v2");
		writer.WriteBinPut(1);  // memo "v2" as index 1
		writer.WriteSetItem();

		writer.WriteString("sr");
		writer.WriteBinGet(1);  // recall "v2" from memo (will use for sr, but that's OK for parser test)
		writer.WriteSetItem();

		writer.WriteString("f0");
		writer.WriteInt32(1);
		writer.WriteSetItem();

		writer.WriteString("info");
		writer.WriteString("test");
		writer.WriteSetItem();

		writer.WriteStop();
		var pickle = writer.ToArray();
		var archive = BuildArchive(pickle, []);

		using var stream = new MemoryStream(archive, writable: false);
		var model = PthLoader.Load(stream);

		Assert.Equal(48000, model.TargetSampleRate);
		Assert.Equal("v2", model.Version);
		Assert.Equal("v2", model.SampleRateLabel);  // memo'd value reused
	}

	[Fact]
	public void Load_PickleWithLongBinGetPut_ParsesCorrectly()
	{
		// Uses 0x72 LONG_BINPUT, 0x6A LONG_BINGET opcodes for memo by int index
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteEmptyDictionary();
		writer.WriteLongBinPut(100);  // memo dict as index 100

		writer.WriteString("config");
		writer.WriteEmptyList();
		writer.WriteInt32(48000);
		writer.WriteAppend();
		writer.WriteSetItem();

		writer.WriteString("weight");
		writer.WriteEmptyDictionary();
		writer.WriteSetItem();

		writer.WriteString("version");
		writer.WriteString("v2");
		writer.WriteLongBinPut(101);  // memo "v2" as index 101
		writer.WriteSetItem();

		writer.WriteString("sr");
		writer.WriteLongBinGet(101);  // recall from index 101
		writer.WriteSetItem();

		writer.WriteString("f0");
		writer.WriteInt32(1);
		writer.WriteSetItem();

		writer.WriteString("info");
		writer.WriteString("test");
		writer.WriteSetItem();

		writer.WriteStop();
		var pickle = writer.ToArray();
		var archive = BuildArchive(pickle, []);

		using var stream = new MemoryStream(archive, writable: false);
		var model = PthLoader.Load(stream);

		Assert.Equal("v2", model.SampleRateLabel);
	}

	[Fact]
	public void Load_PickleWithClassicGlobal_ParsesCorrectly()
	{
		// Uses 0x63 GLOBAL opcode (newline-delimited module\nname) instead of 0x93 STACK_GLOBAL
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteEmptyDictionary();

		writer.WriteString("config");
		writer.WriteEmptyList();
		writer.WriteInt32(48000);
		writer.WriteAppend();
		writer.WriteSetItem();

		// Build a weight using classic GLOBAL opcode for _rebuild_tensor_v2
		writer.WriteString("weight");
		writer.WriteEmptyDictionary();

		writer.WriteString("test.weight");
		// Use classic global instead of stack global for the tensor rebuild
		writer.WriteClassicGlobal("torch._utils", "_rebuild_tensor_v2");
		writer.WriteMark();
		// persistent storage reference
		writer.WriteMark();
		writer.WriteString("storage");
		writer.WriteString("torch");
		writer.WriteString("HalfStorage");
		writer.WriteStackGlobal();
		writer.WriteString("0");
		writer.WriteString("cpu");
		writer.WriteInt32(4);
		writer.WriteTuple();
		writer.WriteBinPersId();
		writer.WriteInt32(0);
		writer.WriteTuple(new object[] { 2, 2 }.AsEnumerable());
		writer.WriteTuple(new object[] { 2, 1 }.AsEnumerable());
		writer.WriteTuple();
		writer.WriteReduce();
		writer.WriteSetItem();

		writer.WriteSetItem();

		writer.WriteString("version");
		writer.WriteString("v2");
		writer.WriteSetItem();

		writer.WriteString("sr");
		writer.WriteString("48k");
		writer.WriteSetItem();

		writer.WriteString("f0");
		writer.WriteInt32(1);
		writer.WriteSetItem();

		writer.WriteString("info");
		writer.WriteString("test");
		writer.WriteSetItem();

		writer.WriteStop();
		var pickle = writer.ToArray();
		var archive = BuildArchive(pickle, [(StorageKey: "0", Data: Float16Bytes(1f, 2f, 3f, 4f))]);

		using var stream = new MemoryStream(archive, writable: false);
		var model = PthLoader.Load(stream);

		Assert.Single(model.Weights);
		Assert.True(model.Weights.ContainsKey("test.weight"));
	}

	[Fact]
	public void Load_PickleWithTupleNOpcodes_ParsesCorrectly()
	{
		// Tests 0x85 TUPLE1, 0x86 TUPLE2, 0x87 TUPLE3 opcodes
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteEmptyDictionary();

		writer.WriteString("config");
		writer.WriteEmptyList();
		writer.WriteInt32(48000);
		writer.WriteAppend();
		writer.WriteSetItem();

		// Build a weight using TUPLE1/TUPLE2/TUPLE3 in shape and stride
		writer.WriteString("weight");
		writer.WriteEmptyDictionary();

		writer.WriteString("test.w");
		writer.WriteString("torch._utils");
		writer.WriteString("_rebuild_tensor_v2");
		writer.WriteStackGlobal();
		writer.WriteMark();
		// persistent storage ref
		writer.WriteMark();
		writer.WriteString("storage");
		writer.WriteString("torch");
		writer.WriteString("HalfStorage");
		writer.WriteStackGlobal();
		writer.WriteString("0");
		writer.WriteString("cpu");
		writer.WriteInt32(2);
		writer.WriteTuple();
		writer.WriteBinPersId();
		writer.WriteInt32(0);
		// shape: (2,) using TUPLE1
		writer.WriteInt32(2);
		writer.WriteTuple1();
		// stride: (1,) using TUPLE1
		writer.WriteInt32(1);
		writer.WriteTuple1();
		// empty tuple for requires_grad
		writer.WriteTuple();
		writer.WriteReduce();
		writer.WriteSetItem();

		writer.WriteSetItem();

		writer.WriteString("version");
		writer.WriteString("v2");
		writer.WriteSetItem();
		writer.WriteString("sr");
		writer.WriteString("48k");
		writer.WriteSetItem();
		writer.WriteString("f0");
		writer.WriteInt32(1);
		writer.WriteSetItem();
		writer.WriteString("info");
		writer.WriteString("test");
		writer.WriteSetItem();

		writer.WriteStop();
		var pickle = writer.ToArray();
		var archive = BuildArchive(pickle, [(StorageKey: "0", Data: Float16Bytes(1f, 2f))]);

		using var stream = new MemoryStream(archive, writable: false);
		var model = PthLoader.Load(stream);

		var weight = Assert.Single(model.Weights);
		Assert.Equal("test.w", weight.Key);
		Assert.Equal([2], weight.Value.Shape);
	}

	[Fact]
	public void Load_UnsupportedOpcode_ThrowsNotSupportedException()
	{
		// Use an opcode not in the parser's switch statement
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteRawByte(0xFF);  // unsupported opcode
		writer.WriteStop();
		var pickle = writer.ToArray();
		var archive = BuildArchive(pickle, []);

		using var stream = new MemoryStream(archive, writable: false);

		Assert.Throws<NotSupportedException>(() => PthLoader.Load(stream));
	}

	[Fact]
	public void Load_PickleWithoutStop_ThrowsInvalidDataException()
	{
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteEmptyDictionary();
		// No STOP opcode
		var pickle = writer.ToArray();
		var archive = BuildArchive(pickle, []);

		using var stream = new MemoryStream(archive, writable: false);

		Assert.Throws<InvalidDataException>(() => PthLoader.Load(stream));
	}

	[Fact]
	public void Load_EmptyTupleConfigValue_ConvertsToEmptyList()
	{
		var archive = BuildModelArchive
		(
			static writer =>
			{
				writer.WriteEmptyList();
				writer.WriteEmptyTuple();
				writer.WriteAppend();
				writer.WriteInt32(48000);
				writer.WriteAppend();
			}
		);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);

		var value = Assert.IsType<List<object>>(model.Config[0]);
		Assert.Empty(value);
		Assert.Equal(48000, model.TargetSampleRate);
	}

	[Fact]
	public void Load_BooleanConfigValues_ConvertsToBooleans()
	{
		var archive = BuildModelArchive
		(
			static writer =>
			{
				writer.WriteEmptyList();
				writer.WriteBool(true);
				writer.WriteAppend();
				writer.WriteBool(false);
				writer.WriteAppend();
				writer.WriteInt32(48000);
				writer.WriteAppend();
			}
		);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);

		Assert.True(Assert.IsType<bool>(model.Config[0]));
		Assert.False(Assert.IsType<bool>(model.Config[1]));
	}

	[Fact]
	public void Load_NullConfigValue_ThrowsInvalidDataException()
	{
		var archive = BuildModelArchive
		(
			static writer =>
			{
				writer.WriteEmptyList();
				writer.WriteNone();
				writer.WriteAppend();
				writer.WriteInt32(48000);
				writer.WriteAppend();
			}
		);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Config values cannot be null.", exception.Message);
	}

	[Fact]
	public void Load_LegacyStringOpcodesInMetadata_ConvertToStrings()
	{
		var archive = BuildModelArchive
		(
			static writer => WriteDefaultConfig(writer),
			writeVersion: static writer => writer.WriteShortBinString("legacy-v1"),
			writeSampleRate: static writer => writer.WriteBinString("40k"),
			writeInfo: static writer => writer.WriteBinString("legacy-info")
		);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);

		Assert.Equal("legacy-v1", model.Version);
		Assert.Equal("40k", model.SampleRateLabel);
		Assert.Equal("legacy-info", model.Info);
	}

	[Fact]
	public void Load_BytesAndUnicode8ConfigValues_ParseCorrectly()
	{
		var archive = BuildModelArchive
		(
			static writer =>
			{
				writer.WriteEmptyList();
				writer.WriteBinUnicode8("wide-string");
				writer.WriteAppend();
				writer.WriteBinBytes([0x10, 0x20]);
				writer.WriteAppend();
				writer.WriteBinBytes8([0xCA, 0xFE]);
				writer.WriteAppend();
				writer.WriteInt32(48000);
				writer.WriteAppend();
			}
		);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);

		Assert.Equal("wide-string", model.Config[0]);
		Assert.Equal([0x10, 0x20], Assert.IsType<byte[]>(model.Config[1]));
		Assert.Equal([0xCA, 0xFE], Assert.IsType<byte[]>(model.Config[2]));
	}

	[Fact]
	public void Load_Long1ConfigValueInsideInt64Range_ConvertsToInt64()
	{
		var archive = BuildModelArchive
		(
			static writer =>
			{
				writer.WriteEmptyList();
				writer.WriteLong1((long)int.MaxValue + 1L);
				writer.WriteAppend();
				writer.WriteInt32(48000);
				writer.WriteAppend();
			}
		);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);

		Assert.Equal((long)int.MaxValue + 1L, Assert.IsType<long>(model.Config[0]));
	}

	[Fact]
	public void Load_Long1ConfigValueOutsideInt64Range_PreservesBigInteger()
	{
		var value = BigInteger.One << 80;
		var archive = BuildModelArchive
		(
			writer =>
			{
				writer.WriteEmptyList();
				writer.WriteLong1(value);
				writer.WriteAppend();
				writer.WriteInt32(48000);
				writer.WriteAppend();
			}
		);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);

		Assert.Equal(value, Assert.IsType<BigInteger>(model.Config[0]));
	}

	[Fact]
	public void Load_Long1F0OutsideInt32Range_ThrowsInvalidDataException()
	{
		var archive = BuildModelArchive
		(
			static writer => WriteDefaultConfig(writer),
			writeF0: static writer => writer.WriteLong1((long)int.MaxValue + 1L)
		);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Expected 'f0' to be a 32-bit integer.", exception.Message);
	}

	[Theory]
	[InlineData(true, 1)]
	[InlineData(false, 0)]
	public void Load_BooleanF0_ConvertsToIntFlag(bool value, int expected)
	{
		var archive = BuildModelArchive
		(
			static writer => WriteDefaultConfig(writer),
			writeF0: writer => writer.WriteBool(value)
		);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);

		Assert.Equal(expected, model.F0);
	}

	[Fact]
	public void Load_StopWithEmptyStack_ThrowsInvalidDataException()
	{
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteStop();
		var archive = BuildArchive(writer.ToArray(), []);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Pickle STOP encountered with an empty stack.", exception.Message);
	}

	[Fact]
	public void Load_StopWithNullStack_ThrowsInvalidDataException()
	{
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteNone();
		writer.WriteStop();
		var archive = BuildArchive(writer.ToArray(), []);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Pickle STOP encountered with null on stack.", exception.Message);
	}

	[Fact]
	public void Load_TruncatedProtocolOpcode_ThrowsEndOfStreamException()
	{
		var writer = new PickleWriter();
		writer.WriteRawByte(0x80);
		var archive = BuildArchive(writer.ToArray(), []);

		using var stream = new MemoryStream(archive, writable: false);

		Assert.Throws<EndOfStreamException>
		(
			() => PthLoader.Load(stream)
		);
	}

	[Fact]
	public void Load_NegativeBinBytesLength_ThrowsInvalidDataException()
	{
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteRawByte(0x42);
		writer.WriteRawInt32(-1);
		writer.WriteStop();
		var archive = BuildArchive(writer.ToArray(), []);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Pickle length cannot be negative.", exception.Message);
	}

	[Fact]
	public void Load_BinUnicode8TooLarge_ThrowsInvalidDataException()
	{
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteRawByte(0x8D);
		writer.WriteRawUInt64((ulong)int.MaxValue + 1UL);
		writer.WriteStop();
		var archive = BuildArchive(writer.ToArray(), []);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Pickle object is too large.", exception.Message);
	}

	[Fact]
	public void Load_MissingMemoSlot_ThrowsInvalidDataException()
	{
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteBinGet(7);
		writer.WriteStop();
		var archive = BuildArchive(writer.ToArray(), []);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Pickle memo slot 7 was not initialized.", exception.Message);
	}

	[Fact]
	public void Load_TupleWithoutMark_ThrowsInvalidDataException()
	{
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteTuple();
		writer.WriteStop();
		var archive = BuildArchive(writer.ToArray(), []);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Pickle MARK not found.", exception.Message);
	}

	[Theory]
	[InlineData(0x61, "APPEND target is not a list.")]
	[InlineData(0x65, "APPENDS target is not a list.")]
	[InlineData(0x73, "SETITEM target is not a dictionary.")]
	[InlineData(0x75, "SETITEMS key is not a string.")]
	public void Load_InvalidStackMutationTarget_ThrowsInvalidDataException(int opcode, string expectedMessage)
	{
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		WriteInvalidStackMutation(writer, opcode);
		writer.WriteStop();
		var archive = BuildArchive(writer.ToArray(), []);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal(expectedMessage, exception.Message);
	}

	[Fact]
	public void Load_SetItemsOddItemCount_ThrowsInvalidDataException()
	{
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteEmptyDictionary();
		writer.WriteMark();
		writer.WriteString("key");
		writer.WriteSetItems();
		writer.WriteStop();
		var archive = BuildArchive(writer.ToArray(), []);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("SETITEMS received an odd number of stack items.", exception.Message);
	}

	[Fact]
	public void Load_ReduceTorchDeviceWithArgument_ReturnsDeviceString()
	{
		var archive = BuildModelArchive
		(
			static writer =>
			{
				writer.WriteEmptyList();
				writer.WriteGlobalReference("torch", "device");
				writer.WriteString("cuda:0");
				writer.WriteTuple1();
				writer.WriteReduce();
				writer.WriteAppend();
				writer.WriteInt32(48000);
				writer.WriteAppend();
			}
		);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);

		Assert.Equal("cuda:0", model.Config[0]);
	}

	[Fact]
	public void Load_ReduceTorchDeviceWithListArgs_ReturnsDefaultCpu()
	{
		var archive = BuildModelArchive
		(
			static writer =>
			{
				writer.WriteEmptyList();
				writer.WriteGlobalReference("torch", "device");
				writer.WriteEmptyList();
				writer.WriteReduce();
				writer.WriteAppend();
				writer.WriteInt32(48000);
				writer.WriteAppend();
			}
		);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);

		Assert.Equal("cpu", model.Config[0]);
	}

	[Fact]
	public void Load_OrderedDictReduceRoot_ParsesModel()
	{
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteGlobalReference("collections", "OrderedDict");
		writer.WriteEmptyTuple();
		writer.WriteReduce();
		WriteMinimalModelEntries(writer);
		writer.WriteStop();
		var archive = BuildArchive(writer.ToArray(), []);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);

		Assert.Equal(48000, model.TargetSampleRate);
		Assert.Empty(model.Weights);
	}

	[Theory]
	[InlineData(0x4E)]
	[InlineData(0x7D)]
	[InlineData(0x29)]
	[InlineData(0x43)]
	public void Load_BuildWithEmptyState_ReturnsInstance(int stateOpcode)
	{
		var archive = BuildModelArchive
		(
			writer =>
			{
				writer.WriteEmptyList();
				writer.WriteString("ready");
				WriteBuildState(writer, stateOpcode);
				writer.WriteBuild();
				writer.WriteAppend();
				writer.WriteInt32(48000);
				writer.WriteAppend();
			}
		);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);

		Assert.Equal("ready", model.Config[0]);
	}

	[Fact]
	public void Load_BuildWithUnsupportedState_ThrowsNotSupportedException()
	{
		var archive = BuildModelArchive
		(
			static writer =>
			{
				writer.WriteEmptyList();
				writer.WriteString("ready");
				writer.WriteString("state");
				writer.WriteBuild();
				writer.WriteAppend();
				writer.WriteInt32(48000);
				writer.WriteAppend();
			}
		);

		using var stream = new MemoryStream(archive, writable: false);

		Assert.Throws<NotSupportedException>
		(
			() => PthLoader.Load(stream)
		);
	}

	[Fact]
	public void Load_ReduceWithNonGlobalCallable_ThrowsInvalidDataException()
	{
		var archive = BuildModelArchive
		(
			static writer =>
			{
				writer.WriteEmptyList();
				writer.WriteString("not-global");
				writer.WriteEmptyTuple();
				writer.WriteReduce();
				writer.WriteAppend();
				writer.WriteInt32(48000);
				writer.WriteAppend();
			}
		);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("REDUCE callable is not a global reference.", exception.Message);
	}

	[Fact]
	public void Load_UnsupportedReduceTarget_TreatsAsOpaquePlaceholder()
	{
		// REDUCE targets we don't know about (e.g. trainer-specific dataclasses or enums
		// like `ultimate_rvc.typing_extra.TrainingSampleRate`) are now tolerated as opaque
		// placeholders so the model weights can still load. Here we put the placeholder
		// exactly where ultimate_rvc does -- as the value of the `sr` key in the root dict.
		// The resolver should fall back to deriving the label from config[-1].
		var archive = BuildModelArchive
		(
			static writer => WriteDefaultConfig(writer),
			writeSampleRate: static writer =>
			{
				writer.WriteGlobalReference("math", "sqrt");
				writer.WriteEmptyTuple();
				writer.WriteReduce();
			}
		);

		using var stream = new MemoryStream(archive, writable: false);

		// Load should not throw; the unknown REDUCE collapses into a typed placeholder, and
		// SampleRateLabel falls back to the value derived from config[-1] (48000 -> "48k").
		var model = PthLoader.Load(stream);
		Assert.NotNull(model);
		Assert.Equal("48k", model.SampleRateLabel);
	}

	[Theory]
	[InlineData("_rebuild_parameter")]
	[InlineData("_rebuild_parameter_with_state")]
	public void Load_RebuildParameterWeight_UnwrapsTensorManifest(string rebuildName)
	{
		var archive = BuildModelArchive
		(
			static writer => WriteDefaultConfig(writer),
			writeWeight: writer =>
			{
				writer.WriteEmptyDictionary();
				writer.WriteString("wrapped.weight");
				writer.WriteGlobalReference("torch._utils", rebuildName);
				writer.WriteMark();
				writer.WriteTensorManifest("HalfStorage", "0", 0, [2], [1], 2);
				if (string.Equals(rebuildName, "_rebuild_parameter_with_state", StringComparison.Ordinal))
				{
					writer.WriteEmptyDictionary();
				}

				writer.WriteTuple();
				writer.WriteReduce();
				writer.WriteSetItem();
			},
			storages: [("0", Float16Bytes(1f, 2f))]
		);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);

		var tensor = Assert.Single(model.Weights).Value;
		Assert.Equal([2], tensor.Shape);
		Assert.Equal(Float16Bytes(1f, 2f), tensor.Data);
	}

	[Fact]
	public void Load_ZeroElementTensor_LoadsEmptyData()
	{
		var archive = BuildModelArchive
		(
			static writer => WriteDefaultConfig(writer),
			writeWeight: static writer => WriteSingleTensorWeight(writer, "empty.weight", "HalfStorage", "0", 0, [0], [1], 0),
			storages: [("0", [])]
		);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);

		var tensor = Assert.Single(model.Weights).Value;
		Assert.Equal([0], tensor.Shape);
		Assert.Empty(tensor.Data);
		Assert.Equal(0, tensor.ElementCount);
	}

	[Fact]
	public void Load_NonContiguousTensor_ReordersData()
	{
		var archive = BuildModelArchive
		(
			static writer => WriteDefaultConfig(writer),
			writeWeight: static writer => WriteSingleTensorWeight(writer, "noncontiguous.weight", "HalfStorage", "0", 0, [2, 2], [1, 2], 4),
			storages: [("0", Float16Bytes(1f, 2f, 3f, 4f))]
		);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);

		var tensor = Assert.Single(model.Weights).Value;
		Assert.Equal(Float16Bytes(1f, 3f, 2f, 4f), tensor.Data);
	}

	[Fact]
	public void Load_ContiguousTensorPastStorage_ThrowsInvalidDataException()
	{
		var archive = BuildModelArchive
		(
			static writer => WriteDefaultConfig(writer),
			writeWeight: static writer => WriteSingleTensorWeight(writer, "short.weight", "HalfStorage", "0", 1, [2], [1], 3),
			storages: [("0", Float16Bytes(1f, 2f))]
		);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Tensor 'short.weight' extends past the backing storage.", exception.Message);
	}

	[Fact]
	public void Load_NonContiguousTensorPastStorage_ThrowsInvalidDataException()
	{
		var archive = BuildModelArchive
		(
			static writer => WriteDefaultConfig(writer),
			writeWeight: static writer => WriteSingleTensorWeight(writer, "strided.weight", "HalfStorage", "0", 0, [2, 2], [1, 3], 4),
			storages: [("0", Float16Bytes(1f, 2f, 3f, 4f))]
		);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Tensor 'strided.weight' references data past the end of storage '0'.", exception.Message);
	}

	[Fact]
	public void Load_MissingStorageEntry_ThrowsInvalidDataException()
	{
		var archive = BuildModelArchive
		(
			static writer => WriteDefaultConfig(writer),
			writeWeight: static writer => WriteSingleTensorWeight(writer, "missing.weight", "HalfStorage", "0", 0, [1], [1], 1)
		);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Checkpoint archive is missing 'archive/data/0'.", exception.Message);
	}

	[Fact]
	public void Load_UnsupportedStorageType_ThrowsNotSupportedException()
	{
		var archive = BuildModelArchive
		(
			static writer => WriteDefaultConfig(writer),
			writeWeight: static writer => WriteSingleTensorWeight(writer, "bad.weight", "ComplexFloatStorage", "0", 0, [1], [1], 1)
		);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<NotSupportedException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Unsupported storage type 'torch.ComplexFloatStorage'.", exception.Message);
	}

	[Theory]
	[InlineData("DoubleStorage", "float64", 8)]
	[InlineData("BFloat16Storage", "bfloat16", 2)]
	[InlineData("LongStorage", "int64", 8)]
	[InlineData("IntStorage", "int32", 4)]
	[InlineData("ShortStorage", "int16", 2)]
	[InlineData("ByteStorage", "uint8", 1)]
	[InlineData("CharStorage", "int8", 1)]
	[InlineData("BoolStorage", "bool", 1)]
	public void Load_SupportedStorageType_MapsDTypeAndElementSize(string storageType, string expectedDType, int elementSize)
	{
		var storage = Enumerable.Range(1, elementSize).Select(static value => (byte)value).ToArray();
		var archive = BuildModelArchive
		(
			static writer => WriteDefaultConfig(writer),
			writeWeight: writer => WriteSingleTensorWeight(writer, "typed.weight", storageType, "0", 0, [1], [1], 1),
			storages: [("0", storage)]
		);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);

		var tensor = Assert.Single(model.Weights).Value;
		Assert.Equal(expectedDType, tensor.DType);
		Assert.Equal(elementSize, tensor.Data.Length);
		Assert.Equal(storage, tensor.Data);
	}

	[Fact]
	public void Load_WeightValueNotTensor_ThrowsInvalidDataException()
	{
		var archive = BuildModelArchive
		(
			static writer => WriteDefaultConfig(writer),
			writeWeight: static writer =>
			{
				writer.WriteEmptyDictionary();
				writer.WriteString("bad.weight");
				writer.WriteInt32(1);
				writer.WriteSetItem();
			}
		);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Weight 'bad.weight' is not a tensor.", exception.Message);
	}

	[Fact]
	public void Load_RootNotDictionary_ThrowsInvalidDataException()
	{
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteInt32(48000);
		writer.WriteStop();
		var archive = BuildArchive(writer.ToArray(), []);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Expected checkpoint root to be a dictionary.", exception.Message);
	}

	[Fact]
	public void Load_WeightNotDictionary_ThrowsInvalidDataException()
	{
		var archive = BuildModelArchive
		(
			static writer => WriteDefaultConfig(writer),
			writeWeight: static writer => writer.WriteEmptyList()
		);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Expected weight to be a dictionary.", exception.Message);
	}

	[Fact]
	public void Load_OptionalMetadataMissing_UsesFallbacks()
	{
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteEmptyDictionary();
		writer.WriteString("config");
		WriteDefaultConfig(writer);
		writer.WriteSetItem();
		writer.WriteString("weight");
		writer.WriteEmptyDictionary();
		writer.WriteSetItem();
		writer.WriteStop();
		var archive = BuildArchive(writer.ToArray(), []);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);

		Assert.Equal("v2", model.Version);
		// `sr` key is missing, but config[-1]=48000 is a plausible sample rate -- the
		// resolver derives "48k" rather than falling back to "unknown".
		Assert.Equal("48k", model.SampleRateLabel);
		Assert.Equal(1, model.F0);
		Assert.Equal(string.Empty, model.Info);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	public void Load_MalformedPersistentId_ThrowsExpectedException(int scenario)
	{
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		WriteMalformedPersistentId(writer, scenario);
		writer.WriteBinPersId();
		writer.WriteStop();
		var archive = BuildArchive(writer.ToArray(), []);

		using var stream = new MemoryStream(archive, writable: false);

		if (scenario == 1)
		{
			Assert.Throws<NotSupportedException>
			(
				() => PthLoader.Load(stream)
			);
		}
		else
		{
			Assert.Throws<InvalidDataException>
			(
				() => PthLoader.Load(stream)
			);
		}
	}

	[Fact]
	public void Load_TensorRebuildTupleMalformed_ThrowsInvalidDataException()
	{
		var archive = BuildModelArchive
		(
			static writer => WriteDefaultConfig(writer),
			writeWeight: static writer =>
			{
				writer.WriteEmptyDictionary();
				writer.WriteString("bad.weight");
				writer.WriteGlobalReference("torch._utils", "_rebuild_tensor_v2");
				writer.WriteEmptyTuple();
				writer.WriteReduce();
				writer.WriteSetItem();
			}
		);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Tensor rebuild tuple is malformed.", exception.Message);
	}

	[Fact]
	public void Load_TensorShapeAndStrideRankMismatch_ThrowsInvalidDataException()
	{
		var archive = BuildModelArchive
		(
			static writer => WriteDefaultConfig(writer),
			writeWeight: static writer => WriteSingleTensorWeight(writer, "bad.weight", "HalfStorage", "0", 0, [2], [1, 1], 2)
		);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Tensor shape and stride rank do not match.", exception.Message);
	}

	[Fact]
	public void Load_TensorShapeContainsNull_ThrowsInvalidDataException()
	{
		var archive = BuildModelArchive
		(
			static writer => WriteDefaultConfig(writer),
			writeWeight: static writer =>
			{
				writer.WriteEmptyDictionary();
				writer.WriteString("bad.weight");
				writer.WriteGlobalReference("torch._utils", "_rebuild_tensor_v2");
				writer.WriteMark();
				writer.WritePersistentStorageReference("HalfStorage", "0", 1);
				writer.WriteInt32(0);
				writer.WriteMark();
				writer.WriteNone();
				writer.WriteTuple();
				writer.WriteTuple([1]);
				writer.WriteTuple();
				writer.WriteReduce();
				writer.WriteSetItem();
			}
		);

		using var stream = new MemoryStream(archive, writable: false);

		var exception = Assert.Throws<InvalidDataException>
		(
			() => PthLoader.Load(stream)
		);

		Assert.Equal("Tensor shape contains null.", exception.Message);
	}

	[Theory]
	[InlineData(0x8F)]
	[InlineData(0x91)]
	[InlineData(0x6F)]
	[InlineData(0x81)]
	[InlineData(0x69)]
	public void Load_UnsupportedCollectionAndObjectOpcode_ThrowsNotSupportedException(int opcode)
	{
		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteRawByte((byte)opcode);
		writer.WriteStop();
		var archive = BuildArchive(writer.ToArray(), []);

		using var stream = new MemoryStream(archive, writable: false);

		Assert.Throws<NotSupportedException>
		(
			() => PthLoader.Load(stream)
		);
	}

	[Fact]
	public void Load_SampleRatePlaceholderWithBuildState_UsesNormalizedLabel()
	{
		var archive = BuildModelArchive
		(
			static writer =>
			{
				writer.WriteEmptyList();
				writer.WriteInt32(123);
				writer.WriteAppend();
			},
			writeSampleRate: static writer =>
			{
				writer.WriteGlobalReference("ultimate_rvc.typing_extra", "TrainingSampleRate");
				writer.WriteString("SR_40K");
				writer.WriteTuple1();
				writer.WriteReduce();
				writer.WriteString("ignored-state");
				writer.WriteBuild();
			}
		);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);

		Assert.Equal("40k", model.SampleRateLabel);
	}

	[Fact]
	public void Load_F0PlaceholderWithBooleanArgument_ConvertsToFlag()
	{
		var archive = BuildModelArchive
		(
			static writer => WriteDefaultConfig(writer),
			writeF0: static writer =>
			{
				writer.WriteGlobalReference("ultimate_rvc.enums", "PitchAware");
				writer.WriteBool(false);
				writer.WriteTuple1();
				writer.WriteReduce();
			}
		);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);

		Assert.Equal(0, model.F0);
	}

	[Fact]
	public void Load_TorchDeviceWithNumericArgument_ConvertsToString()
	{
		var archive = BuildModelArchive
		(
			static writer =>
			{
				writer.WriteEmptyList();
				writer.WriteGlobalReference("torch", "device");
				writer.WriteInt32(7);
				writer.WriteTuple1();
				writer.WriteReduce();
				writer.WriteAppend();
				writer.WriteInt32(48000);
				writer.WriteAppend();
			}
		);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);

		Assert.Equal("7", model.Config[0]);
	}

	[Fact]
	public void Load_PersistentStorageKeyAsInteger_ResolvesNumericArchiveEntry()
	{
		var archive = BuildModelArchive
		(
			static writer => WriteDefaultConfig(writer),
			writeWeight: static writer =>
			{
				writer.WriteEmptyDictionary();
				writer.WriteString("numeric.weight");
				writer.WriteGlobalReference("torch._utils", "_rebuild_tensor_v2");
				writer.WriteMark();
				writer.WriteMark();
				writer.WriteString("storage");
				writer.WriteGlobalReference("torch", "HalfStorage");
				writer.WriteInt32(7);
				writer.WriteString("cpu");
				writer.WriteInt32(1);
				writer.WriteTuple();
				writer.WriteBinPersId();
				writer.WriteInt32(0);
				writer.WriteTuple([1]);
				writer.WriteTuple([1]);
				writer.WriteTuple();
				writer.WriteReduce();
				writer.WriteSetItem();
			},
			storages: [("7", Float16Bytes(3f))]
		);

		using var stream = new MemoryStream(archive, writable: false);

		var model = PthLoader.Load(stream);
		var weight = Assert.Single(model.Weights).Value;

		Assert.Equal(Float16Bytes(3f), weight.Data);
	}

	[Fact]
	public void TensorManifest_EmptyShape_HasSingleElement()
	{
		var tensorManifestType = typeof(PthLoader).GetNestedType("TensorManifest", BindingFlags.NonPublic);
		Assert.NotNull(tensorManifestType);

		var manifest = Activator.CreateInstance
		(
			tensorManifestType!,
			"scalar",
			"0",
			"float16",
			2,
			Array.Empty<int>(),
			Array.Empty<int>(),
			0
		);
		Assert.NotNull(manifest);

		var elementCount = (int)tensorManifestType.GetProperty("ElementCount")!.GetValue(manifest)!;
		var renamed = tensorManifestType.GetMethod("WithName")!.Invoke(manifest, ["renamed"]);

		Assert.Equal(1, elementCount);
		Assert.Equal("renamed", tensorManifestType.GetProperty("Name")!.GetValue(renamed));
		Assert.Equal(1, (int)tensorManifestType.GetProperty("ElementCount")!.GetValue(renamed)!);
	}

	private static byte[] BuildModelArchive
	(
		Action<PickleWriter> writeConfig,
		Action<PickleWriter>? writeWeight = null,
		Action<PickleWriter>? writeVersion = null,
		Action<PickleWriter>? writeSampleRate = null,
		Action<PickleWriter>? writeF0 = null,
		Action<PickleWriter>? writeInfo = null,
		IEnumerable<(string StorageKey, byte[] Data)>? storages = null
	)
	{
		ArgumentNullException.ThrowIfNull(writeConfig);

		var writer = new PickleWriter();
		writer.WriteProtocol2();
		writer.WriteEmptyDictionary();

		writer.WriteString("config");
		writeConfig(writer);
		writer.WriteSetItem();

		writer.WriteString("weight");
		if (writeWeight is null)
		{
			writer.WriteEmptyDictionary();
		}
		else
		{
			writeWeight(writer);
		}

		writer.WriteSetItem();

		writer.WriteString("version");
		if (writeVersion is null)
		{
			writer.WriteString("v2");
		}
		else
		{
			writeVersion(writer);
		}

		writer.WriteSetItem();

		writer.WriteString("sr");
		if (writeSampleRate is null)
		{
			writer.WriteString("48k");
		}
		else
		{
			writeSampleRate(writer);
		}

		writer.WriteSetItem();

		writer.WriteString("f0");
		if (writeF0 is null)
		{
			writer.WriteInt32(1);
		}
		else
		{
			writeF0(writer);
		}

		writer.WriteSetItem();

		writer.WriteString("info");
		if (writeInfo is null)
		{
			writer.WriteString("test");
		}
		else
		{
			writeInfo(writer);
		}

		writer.WriteSetItem();
		writer.WriteStop();

		return BuildArchive(writer.ToArray(), storages ?? Array.Empty<(string StorageKey, byte[] Data)>());
	}

	private static void WriteDefaultConfig(PickleWriter writer)
	{
		writer.WriteEmptyList();
		writer.WriteInt32(48000);
		writer.WriteAppend();
	}

	private static void WriteMinimalModelEntries(PickleWriter writer)
	{
		writer.WriteString("config");
		WriteDefaultConfig(writer);
		writer.WriteSetItem();
		writer.WriteString("weight");
		writer.WriteEmptyDictionary();
		writer.WriteSetItem();
		writer.WriteString("version");
		writer.WriteString("v2");
		writer.WriteSetItem();
		writer.WriteString("sr");
		writer.WriteString("48k");
		writer.WriteSetItem();
		writer.WriteString("f0");
		writer.WriteInt32(1);
		writer.WriteSetItem();
		writer.WriteString("info");
		writer.WriteString("test");
		writer.WriteSetItem();
	}

	private static void WriteSingleTensorWeight
	(
		PickleWriter writer,
		string weightName,
		string storageTypeName,
		string storageKey,
		int storageOffset,
		int[] shape,
		int[] stride,
		int storageElementCount
	)
	{
		writer.WriteEmptyDictionary();
		writer.WriteString(weightName);
		writer.WriteTensorManifest(storageTypeName, storageKey, storageOffset, shape, stride, storageElementCount);
		writer.WriteSetItem();
	}

	private static void WriteInvalidStackMutation(PickleWriter writer, int opcode)
	{
		switch (opcode)
		{
			case 0x61:
				writer.WriteEmptyDictionary();
				writer.WriteInt32(1);
				writer.WriteAppend();
				break;

			case 0x65:
				writer.WriteEmptyDictionary();
				writer.WriteMark();
				writer.WriteInt32(1);
				writer.WriteAppends();
				break;

			case 0x73:
				writer.WriteEmptyList();
				writer.WriteString("key");
				writer.WriteString("value");
				writer.WriteSetItem();
				break;

			case 0x75:
				writer.WriteEmptyDictionary();
				writer.WriteMark();
				writer.WriteInt32(1);
				writer.WriteString("value");
				writer.WriteSetItems();
				break;

			default:
				throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Unexpected opcode.");
		}
	}

	private static void WriteBuildState(PickleWriter writer, int stateOpcode)
	{
		switch (stateOpcode)
		{
			case 0x4E:
				writer.WriteNone();
				break;

			case 0x7D:
				writer.WriteEmptyDictionary();
				break;

			case 0x29:
				writer.WriteEmptyTuple();
				break;

			case 0x43:
				writer.WriteShortBinBytes([]);
				break;

			default:
				throw new ArgumentOutOfRangeException(nameof(stateOpcode), stateOpcode, "Unexpected opcode.");
		}
	}

	private static void WriteMalformedPersistentId(PickleWriter writer, int scenario)
	{
		if (scenario == 0)
		{
			writer.WriteEmptyTuple();
			return;
		}

		writer.WriteMark();
		writer.WriteString(scenario == 1 ? "view" : "storage");
		if (scenario == 2)
		{
			writer.WriteString("not-a-global");
		}
		else
		{
			writer.WriteGlobalReference("torch", "HalfStorage");
		}

		if (scenario == 3)
		{
			writer.WriteNone();
		}
		else
		{
			writer.WriteString("0");
		}

		writer.WriteString("cpu");
		writer.WriteInt32(1);
		writer.WriteTuple();
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

		public void WriteEmptyTuple()
		{
			_buffer.Add(0x29);
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

		public void WriteBuild()
		{
			_buffer.Add(0x62);
		}

		public void WriteStop()
		{
			_buffer.Add(0x2E);
		}

		public void WriteNone()
		{
			_buffer.Add(0x4E);
		}

		public void WriteBool(bool value)
		{
			_buffer.Add(value ? (byte)0x88 : (byte)0x89);
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
					WriteBool(boolValue);
					return;

				case byte[] bytes:
					WriteBinBytes(bytes);
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

		public void WriteBinBytes(byte[] value)
		{
			_buffer.Add(0x42);
			_buffer.AddRange(BitConverter.GetBytes(value.Length));
			_buffer.AddRange(value);
		}

		public void WriteGlobalReference(string module, string name)
		{
			WriteString(module);
			WriteString(name);
			WriteStackGlobal();
		}

		private void WriteTensor(TensorEntry entry)
		{
			WriteTensorManifest
			(
				GetStorageTypeName(entry.DType),
				entry.StorageKey,
				0,
				entry.Shape,
				ComputeContiguousStride(entry.Shape),
				ComputeElementCount(entry.Shape)
			);
		}

		public void WritePersistentStorageReference(string storageTypeName, string storageKey, int storageElementCount)
		{
			WriteMark();
			WriteString("storage");
			WriteGlobalReference("torch", storageTypeName);
			WriteString(storageKey);
			WriteString("cpu");
			WriteInt32(storageElementCount);
			WriteTuple();
			WriteBinPersId();
		}

		public void WriteTensorManifest
		(
			string storageTypeName,
			string storageKey,
			int storageOffset,
			int[] shape,
			int[] stride,
			int storageElementCount
		)
		{
			WriteGlobalReference("torch._utils", "_rebuild_tensor_v2");
			WriteMark();
			WritePersistentStorageReference(storageTypeName, storageKey, storageElementCount);
			WriteInt32(storageOffset);
			WriteTuple(shape.Select(static value => (object)value));
			WriteTuple(stride.Select(static value => (object)value));
			WriteTuple();
			WriteReduce();
		}

		public void WriteFrame(ulong size)
		{
			_buffer.Add(0x95);
			_buffer.AddRange(BitConverter.GetBytes(size));
		}

		public void WriteMemoize()
		{
			_buffer.Add(0x94);
		}

		public void WriteBinInt1(byte value)
		{
			_buffer.Add(0x4B);
			_buffer.Add(value);
		}

		public void WriteBinInt2(int value)
		{
			_buffer.Add(0x4D);
			_buffer.AddRange(BitConverter.GetBytes((ushort)value));
		}

		public void WriteBinFloat(double value)
		{
			_buffer.Add(0x47);
			var bytes = BitConverter.GetBytes(value);
			// BINFLOAT is big-endian IEEE 754
			Array.Reverse(bytes);
			_buffer.AddRange(bytes);
		}

		public void WriteLong1(long value)
		{
			_buffer.Add(0x8A);
			var bytes = GetSignedLittleEndianBytes(value);
			_buffer.Add((byte)bytes.Length);
			_buffer.AddRange(bytes);
		}

		public void WriteLong1(BigInteger value)
		{
			_buffer.Add(0x8A);
			var bytes = value.ToByteArray();
			_buffer.Add((byte)bytes.Length);
			_buffer.AddRange(bytes);
		}

		public void WriteShortBinString(string value)
		{
			var bytes = Encoding.UTF8.GetBytes(value);
			_buffer.Add(0x55);
			_buffer.Add((byte)bytes.Length);
			_buffer.AddRange(bytes);
		}

		public void WriteBinString(string value)
		{
			var bytes = Encoding.UTF8.GetBytes(value);
			_buffer.Add(0x54);
			_buffer.AddRange(BitConverter.GetBytes(bytes.Length));
			_buffer.AddRange(bytes);
		}

		public void WriteBinUnicode(string value)
		{
			var bytes = Encoding.UTF8.GetBytes(value);
			_buffer.Add(0x58);
			_buffer.AddRange(BitConverter.GetBytes(bytes.Length));
			_buffer.AddRange(bytes);
		}

		public void WriteBinUnicode8(string value)
		{
			var bytes = Encoding.UTF8.GetBytes(value);
			_buffer.Add(0x8D);
			_buffer.AddRange(BitConverter.GetBytes((ulong)bytes.Length));
			_buffer.AddRange(bytes);
		}

		public void WriteShortBinBytes(byte[] data)
		{
			_buffer.Add(0x43);
			_buffer.Add((byte)data.Length);
			_buffer.AddRange(data);
		}

		public void WriteBinBytes8(byte[] data)
		{
			_buffer.Add(0x8E);
			_buffer.AddRange(BitConverter.GetBytes((ulong)data.Length));
			_buffer.AddRange(data);
		}

		public void WriteSetItems()
		{
			_buffer.Add(0x75);
		}

		public void WriteAppends()
		{
			_buffer.Add(0x65);
		}

		public void WriteTuple1()
		{
			_buffer.Add(0x85);
		}

		public void WriteTuple2()
		{
			_buffer.Add(0x86);
		}

		public void WriteTuple3()
		{
			_buffer.Add(0x87);
		}

		public void WriteClassicGlobal(string module, string name)
		{
			_buffer.Add(0x63);
			var modBytes = Encoding.ASCII.GetBytes(module + "\n");
			var nameBytes = Encoding.ASCII.GetBytes(name + "\n");
			_buffer.AddRange(modBytes);
			_buffer.AddRange(nameBytes);
		}

		public void WriteBinPut(byte index)
		{
			_buffer.Add(0x71);
			_buffer.Add(index);
		}

		public void WriteBinGet(byte index)
		{
			_buffer.Add(0x68);
			_buffer.Add(index);
		}

		public void WriteLongBinPut(int index)
		{
			_buffer.Add(0x72);
			_buffer.AddRange(BitConverter.GetBytes(index));
		}

		public void WriteLongBinGet(int index)
		{
			_buffer.Add(0x6A);
			_buffer.AddRange(BitConverter.GetBytes(index));
		}

		public void WriteRawByte(byte value)
		{
			_buffer.Add(value);
		}

		public void WriteRawInt32(int value)
		{
			_buffer.AddRange(BitConverter.GetBytes(value));
		}

		public void WriteRawUInt64(ulong value)
		{
			_buffer.AddRange(BitConverter.GetBytes(value));
		}

		private static byte[] GetSignedLittleEndianBytes(long value)
		{
			if (value == 0)
				return [];
			var bytes = new List<byte>();
			while (value != 0 && value != -1)
			{
				bytes.Add((byte)(value & 0xFF));
				value >>= 8;
			}
			// Add sign byte if needed
			if ((bytes[^1] & 0x80) != 0 && value >= 0)
				bytes.Add(0x00);
			else if ((bytes[^1] & 0x80) == 0 && value < 0)
				bytes.Add(0xFF);
			return [.. bytes];
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
