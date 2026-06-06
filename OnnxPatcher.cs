namespace Talktastic;

/// <summary>
/// Patches ONNX model file bytes in-place by replacing initializer raw_data.
/// Relies on replacement data being exactly the same size as the original
/// (same shape × dtype = same byte count), so no protobuf length fields change.
/// </summary>
internal static class OnnxPatcher
{
	// Protobuf field numbers from onnx.proto3
	private const int ModelProto_Graph = 7;
	private const int GraphProto_Initializer = 5;
	private const int TensorProto_Name = 8;
	private const int TensorProto_RawData = 9;

	// Protobuf wire types
	private const int WireType_Varint = 0;
	private const int WireType_Fixed64 = 1;
	private const int WireType_LengthDelimited = 2;
	private const int WireType_Fixed32 = 5;

	/// <summary>
	/// Finds all initializer raw_data regions in the ONNX model bytes.
	/// Returns a dictionary mapping initializer name to (offset, length) in the byte array.
	/// </summary>
	public static Dictionary<string, (int Offset, int Length)> FindInitializerOffsets(byte[] modelBytes)
	{
		var result = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
		var pos = 0;

		// Walk ModelProto fields
		while (pos < modelBytes.Length)
		{
			var tag = ReadTag(modelBytes, ref pos);
			var fieldNumber = tag >> 3;
			var wireType = tag & 7;

			if (fieldNumber == ModelProto_Graph && wireType == WireType_LengthDelimited)
			{
				var graphLen = ReadVarint(modelBytes, ref pos);
				var graphEnd = pos + (int)graphLen;
				ParseGraph(modelBytes, pos, graphEnd, result);
				pos = graphEnd;
			}
			else
			{
				SkipField(modelBytes, ref pos, wireType);
			}
		}

		return result;
	}

	/// <summary>
	/// Patches the model bytes in-place: for each weight in the map,
	/// copies the new data to the raw_data region at the given offset.
	/// </summary>
	public static void PatchWeights
	(
		byte[] modelBytes,
		Dictionary<string, (int Offset, int Length)> offsets,
		IReadOnlyDictionary<string, PthTensor> pthWeights,
		Dictionary<string, string> nameMap
	)
	{
		var injected = 0;
		foreach (var (pthName, tensor) in pthWeights)
		{
			if (!nameMap.TryGetValue(pthName, out var onnxNameRaw))
			{
				continue;
			}

			// `:T` suffix indicates the weight needs transposing
			var needsTranspose = onnxNameRaw.EndsWith(":T", StringComparison.Ordinal);
			var onnxName = needsTranspose
				? onnxNameRaw[..^2]
				: onnxNameRaw;

			if (!offsets.TryGetValue(onnxName, out var region))
			{
				continue;
			}

			if (tensor.Data.Length != region.Length)
			{
				throw new InvalidOperationException
				(
					$"Size mismatch for '{onnxName}': .pth has {tensor.Data.Length} bytes, "
					+ $"skeleton has {region.Length} bytes."
				);
			}

			if (needsTranspose && tensor.Shape.Length == 2)
			{
				TransposeAndCopy(tensor, modelBytes, region.Offset);
			}
			else
			{
				tensor.Data.CopyTo(modelBytes.AsSpan(region.Offset, region.Length));
			}

			injected++;
		}

		Console.Error.WriteLine
		(
			$"Patched {injected}/{pthWeights.Count} weights into skeleton ONNX."
		);
	}

	private static void TransposeAndCopy(PthTensor tensor, byte[] dest, int destOffset)
	{
		// Transpose a 2D float16 tensor in-place during copy
		var rows = tensor.Shape[0];
		var cols = tensor.Shape[1];
		var src = tensor.Data;

		for (var r = 0; r < rows; r++)
		{
			for (var c = 0; c < cols; c++)
			{
				var srcIdx = (r * cols + c) * 2;
				var dstIdx = destOffset + (c * rows + r) * 2;
				dest[dstIdx] = src[srcIdx];
				dest[dstIdx + 1] = src[srcIdx + 1];
			}
		}
	}

	private static void ParseGraph
	(
		byte[] data,
		int start,
		int end,
		Dictionary<string, (int, int)> result
	)
	{
		var pos = start;
		while (pos < end)
		{
			var tag = ReadTag(data, ref pos);
			var fieldNumber = tag >> 3;
			var wireType = tag & 7;

			if (fieldNumber == GraphProto_Initializer && wireType == WireType_LengthDelimited)
			{
				var tensorLen = ReadVarint(data, ref pos);
				var tensorEnd = pos + (int)tensorLen;
				ParseTensorProto(data, pos, tensorEnd, result);
				pos = tensorEnd;
			}
			else
			{
				SkipField(data, ref pos, wireType);
			}
		}
	}

	private static void ParseTensorProto
	(
		byte[] data,
		int start,
		int end,
		Dictionary<string, (int, int)> result
	)
	{
		string? name = null;
		int rawDataOffset = -1;
		int rawDataLength = 0;

		var pos = start;
		while (pos < end)
		{
			var tag = ReadTag(data, ref pos);
			var fieldNumber = tag >> 3;
			var wireType = tag & 7;

			if (fieldNumber == TensorProto_Name && wireType == WireType_LengthDelimited)
			{
				var len = (int)ReadVarint(data, ref pos);
				name = System.Text.Encoding.UTF8.GetString(data, pos, len);
				pos += len;
			}
			else if (fieldNumber == TensorProto_RawData && wireType == WireType_LengthDelimited)
			{
				var len = (int)ReadVarint(data, ref pos);
				rawDataOffset = pos;
				rawDataLength = len;
				pos += len;
			}
			else
			{
				SkipField(data, ref pos, wireType);
			}
		}

		if (name is not null && rawDataOffset >= 0)
		{
			result[name] = (rawDataOffset, rawDataLength);
		}
	}

	private static int ReadTag(byte[] data, ref int pos)
	{
		return (int)ReadVarint(data, ref pos);
	}

	private static long ReadVarint(byte[] data, ref int pos)
	{
		long result = 0;
		var shift = 0;
		while (pos < data.Length)
		{
			var b = data[pos++];
			result |= (long)(b & 0x7F) << shift;
			if ((b & 0x80) == 0)
			{
				return result;
			}
			shift += 7;
		}

		throw new InvalidDataException("Unexpected end of varint");
	}

	private static void SkipField(byte[] data, ref int pos, int wireType)
	{
		switch (wireType)
		{
			case WireType_Varint:
				ReadVarint(data, ref pos);
				break;
			case WireType_Fixed64:
				pos += 8;
				break;
			case WireType_LengthDelimited:
				var len = (int)ReadVarint(data, ref pos);
				pos += len;
				break;
			case WireType_Fixed32:
				pos += 4;
				break;
			default:
				throw new InvalidDataException($"Unknown wire type: {wireType}");
		}
	}
}
