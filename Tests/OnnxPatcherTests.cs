using System.Text;

namespace Talktastic.Tests;

public sealed class OnnxPatcherTests
{
	[Fact]
	public void FindInitializerOffsets_EmptyModel_ReturnsEmpty()
	{
		var result = OnnxPatcher.FindInitializerOffsets([]);

		Assert.Empty(result);
	}

	[Fact]
	public void FindInitializerOffsets_SingleInitializer_ReturnsCorrectOffset()
	{
		var model = CreateModel
		(
			new InitializerSpec("encoder.weight", [0x10, 0x20, 0x30])
		);

		var result = OnnxPatcher.FindInitializerOffsets(model.Bytes);

		var region = Assert.Single(result);
		Assert.Equal("encoder.weight", region.Key);
		Assert.Equal(model.ExpectedOffsets["encoder.weight"], region.Value);
		Assert.Equal([0x10, 0x20, 0x30], model.Bytes.AsSpan(region.Value.Offset, region.Value.Length).ToArray());
	}

	[Fact]
	public void FindInitializerOffsets_MultipleInitializers_ReturnsAll()
	{
		var model = CreateModel
		(
			new InitializerSpec("alpha", [0x01, 0x02]),
			new InitializerSpec("beta", [0x03, 0x04, 0x05]),
			new InitializerSpec("gamma", [0x06])
		);

		var result = OnnxPatcher.FindInitializerOffsets(model.Bytes);

		Assert.Equal(3, result.Count);
		Assert.Equal(model.ExpectedOffsets["alpha"], result["alpha"]);
		Assert.Equal(model.ExpectedOffsets["beta"], result["beta"]);
		Assert.Equal(model.ExpectedOffsets["gamma"], result["gamma"]);
	}

	[Fact]
	public void FindInitializerOffsets_NoRawData_SkipsInitializer()
	{
		var model = CreateModel
		(
			new InitializerSpec("alpha", [0x01, 0x02]),
			new InitializerSpec("name-only", null, IncludeRawData: false)
		);

		var result = OnnxPatcher.FindInitializerOffsets(model.Bytes);

		Assert.Single(result);
		Assert.DoesNotContain("name-only", result.Keys);
	}

	[Fact]
	public void FindInitializerOffsets_NameBeforeAndAfterRawData_BothWork()
	{
		var model = CreateModel
		(
			new InitializerSpec("name-first", [0x11, 0x12], NameFirst: true),
			new InitializerSpec("raw-first", [0x21, 0x22, 0x23], NameFirst: false)
		);

		var result = OnnxPatcher.FindInitializerOffsets(model.Bytes);

		Assert.Equal(model.ExpectedOffsets["name-first"], result["name-first"]);
		Assert.Equal(model.ExpectedOffsets["raw-first"], result["raw-first"]);
	}

	[Fact]
	public void PatchWeights_CorrectSizeData_PatchesInPlace()
	{
		var model = CreateModel(new InitializerSpec("decoder.weight", [0x00, 0x00, 0x00, 0x00]));
		var offsets = OnnxPatcher.FindInitializerOffsets(model.Bytes);
		var tensor = new PthTensor("pth.decoder", [0xAA, 0xBB, 0xCC, 0xDD], [2, 2], "float16");
		var weights = new Dictionary<string, PthTensor>(StringComparer.Ordinal)
		{
			["pth.decoder"] = tensor,
		};
		var nameMap = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["pth.decoder"] = "decoder.weight",
		};

		OnnxPatcher.PatchWeights(model.Bytes, offsets, weights, nameMap);

		var region = offsets["decoder.weight"];
		Assert.Equal(tensor.Data, model.Bytes.AsSpan(region.Offset, region.Length).ToArray());
	}

	[Fact]
	public void PatchWeights_SizeMismatch_ThrowsInvalidOperationException()
	{
		var model = CreateModel(new InitializerSpec("decoder.weight", [0x00, 0x00, 0x00, 0x00]));
		var offsets = OnnxPatcher.FindInitializerOffsets(model.Bytes);
		var weights = new Dictionary<string, PthTensor>(StringComparer.Ordinal)
		{
			["pth.decoder"] = new PthTensor("pth.decoder", [0xAA, 0xBB], [1], "float16"),
		};
		var nameMap = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["pth.decoder"] = "decoder.weight",
		};

		var exception = Assert.Throws<InvalidOperationException>
		(
			() => OnnxPatcher.PatchWeights(model.Bytes, offsets, weights, nameMap)
		);

		Assert.Contains("decoder.weight", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void PatchWeights_UnmappedWeight_SkipsIt()
	{
		var model = CreateModel(new InitializerSpec("decoder.weight", [0x01, 0x02, 0x03, 0x04]));
		var original = model.Bytes.ToArray();
		var offsets = OnnxPatcher.FindInitializerOffsets(model.Bytes);
		var weights = new Dictionary<string, PthTensor>(StringComparer.Ordinal)
		{
			["pth.unmapped"] = new PthTensor("pth.unmapped", [0xAA, 0xBB, 0xCC, 0xDD], [2, 2], "float16"),
		};
		var nameMap = new Dictionary<string, string>(StringComparer.Ordinal);

		OnnxPatcher.PatchWeights(model.Bytes, offsets, weights, nameMap);

		Assert.Equal(original, model.Bytes);
	}

	[Fact]
	public void PatchWeights_TransposeFlag_TransposesCopy()
	{
		var model = CreateModel(new InitializerSpec("decoder.weight", new byte[12]));
		var offsets = OnnxPatcher.FindInitializerOffsets(model.Bytes);
		var source = new byte[]
		{
			0x01, 0x11,
			0x02, 0x12,
			0x03, 0x13,
			0x04, 0x14,
			0x05, 0x15,
			0x06, 0x16,
		};
		var weights = new Dictionary<string, PthTensor>(StringComparer.Ordinal)
		{
			["pth.decoder"] = new PthTensor("pth.decoder", source, [2, 3], "float16"),
		};
		var nameMap = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["pth.decoder"] = "decoder.weight:T",
		};

		OnnxPatcher.PatchWeights(model.Bytes, offsets, weights, nameMap);

		var region = offsets["decoder.weight"];
		var patched = model.Bytes.AsSpan(region.Offset, region.Length).ToArray();
		Assert.Equal
		(
			new byte[]
			{
				0x01, 0x11,
				0x04, 0x14,
				0x02, 0x12,
				0x05, 0x15,
				0x03, 0x13,
				0x06, 0x16,
			},
			patched
		);
	}

	private static ModelBytes CreateModel(params InitializerSpec[] initializers)
	{
		var graphPayload = new List<byte>();
		var expected = new Dictionary<string, (int Offset, int Length)>(StringComparer.Ordinal);

		foreach (var initializer in initializers)
		{
			var tensor = BuildTensorPayload(initializer);
			var tensorField = EncodeLengthDelimitedField(5, tensor.Bytes);

			if (tensor.RawDataOffset is int rawDataOffset && initializer.RawData is not null)
			{
				expected.Add
				(
					initializer.Name,
					(
						graphPayload.Count + tensorField.PayloadOffset + rawDataOffset,
						initializer.RawData.Length
					)
				);
			}

			graphPayload.AddRange(tensorField.Bytes);
		}

		var graphField = EncodeLengthDelimitedField(7, [.. graphPayload]);
		var adjusted = expected.ToDictionary
		(
			static pair => pair.Key,
			pair => (pair.Value.Offset + graphField.PayloadOffset, pair.Value.Length),
			StringComparer.Ordinal
		);

		return new ModelBytes(graphField.Bytes, adjusted);
	}

	private static TensorPayload BuildTensorPayload(InitializerSpec initializer)
	{
		var payload = new List<byte>();
		int? rawDataOffset = null;

		if (initializer.NameFirst)
		{
			payload.AddRange(EncodeLengthDelimitedField(8, Encoding.UTF8.GetBytes(initializer.Name)).Bytes);
		}

		if (initializer.IncludeRawData && initializer.RawData is not null)
		{
			var rawField = EncodeLengthDelimitedField(9, initializer.RawData);
			rawDataOffset = payload.Count + rawField.PayloadOffset;
			payload.AddRange(rawField.Bytes);
		}

		if (!initializer.NameFirst)
		{
			payload.AddRange(EncodeLengthDelimitedField(8, Encoding.UTF8.GetBytes(initializer.Name)).Bytes);
		}

		return new TensorPayload([.. payload], rawDataOffset);
	}

	private static EncodedField EncodeLengthDelimitedField(int fieldNumber, byte[] payload)
	{
		var tag = EncodeVarint((fieldNumber << 3) | 2);
		var length = EncodeVarint(payload.Length);
		var bytes = new byte[tag.Length + length.Length + payload.Length];

		tag.CopyTo(bytes, 0);
		length.CopyTo(bytes, tag.Length);
		payload.CopyTo(bytes, tag.Length + length.Length);

		return new EncodedField(bytes, tag.Length + length.Length);
	}

	private static byte[] EncodeVarint(int value)
	{
		var bytes = new List<byte>();
		uint remaining = (uint)value;

		do
		{
			var current = (byte)(remaining & 0x7Fu);
			remaining >>= 7;

			if (remaining != 0)
			{
				current |= 0x80;
			}

			bytes.Add(current);
		}
		while (remaining != 0);

		return [.. bytes];
	}

	private sealed record InitializerSpec
	(
		string Name,
		byte[]? RawData,
		bool NameFirst = true,
		bool IncludeRawData = true
	);

	private sealed record ModelBytes
	(
		byte[] Bytes,
		IReadOnlyDictionary<string, (int Offset, int Length)> ExpectedOffsets
	);

	private sealed record TensorPayload(byte[] Bytes, int? RawDataOffset);

	private sealed record EncodedField(byte[] Bytes, int PayloadOffset);
}
