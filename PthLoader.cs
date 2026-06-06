using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace Talktastic;

[System.Diagnostics.CodeAnalysis.SuppressMessage
(
	"Performance",
	"CA1812:Avoid uninstantiated internal classes",
	Justification = "Requested API surface is an internal sealed utility type with static entry points."
)]
/// <summary>
/// Loads RVC checkpoint archives into <see cref="PthModel"/> instances.
/// </summary>
internal sealed class PthLoader
{
	private const string ConfigKey = "config";
	private const string WeightKey = "weight";
	private const string VersionKey = "version";
	private const string SampleRateKey = "sr";
	private const string F0Key = "f0";
	private const string InfoKey = "info";
	private const string EncQPrefix = "enc_q.";
	private const string WeightGSuffix = "_g";
	private const string WeightVSuffix = "_v";

	/// <summary>
	/// Loads a checkpoint model from a file path.
	/// </summary>
	/// <param name="pthFilePath">The checkpoint file path.</param>
	/// <returns>The loaded checkpoint model.</returns>
	public static PthModel Load(string pthFilePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pthFilePath);

		using var stream = File.OpenRead(pthFilePath);
		return Load(stream);
	}

	/// <summary>
	/// Loads a checkpoint model from a stream.
	/// </summary>
	/// <param name="pthStream">The checkpoint stream.</param>
	/// <returns>The loaded checkpoint model.</returns>
	public static PthModel Load(Stream pthStream)
	{
		ArgumentNullException.ThrowIfNull(pthStream);

		if (!pthStream.CanSeek)
		{
			using var copy = new MemoryStream();
			pthStream.CopyTo(copy);
			copy.Position = 0;
			return LoadArchive(copy, leaveOpen: false);
		}

		return LoadArchive(pthStream, leaveOpen: true);
	}

	/// <summary>
	/// Loads a checkpoint model from an archive stream.
	/// </summary>
	/// <param name="stream">The archive stream.</param>
	/// <param name="leaveOpen">A value indicating whether to leave <paramref name="stream"/> open.</param>
	/// <returns>The loaded checkpoint model.</returns>
	private static PthModel LoadArchive(Stream stream, bool leaveOpen)
	{
		using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen);
		var prefix = ResolvePrefix(archive);
		var pickleEntry = GetRequiredEntry(archive, prefix + "data.pkl");

		object root;
		using (var pickleStream = pickleEntry.Open())
		{
			root = new PickleParser().Parse(pickleStream);
		}

		var manifest = ParseModel(root);
		var weights = LoadWeights(archive, prefix, manifest.WeightManifests);
		FuseWeightNorm(weights);

		return new PthModel
		(
			manifest.Config,
			manifest.TargetSampleRate,
			manifest.Version,
			manifest.SampleRateLabel,
			manifest.F0,
			manifest.Info,
			new ReadOnlyDictionary<string, PthTensor>(weights)
		);
	}

	/// <summary>
	/// Resolves the archive entry prefix for the checkpoint payload.
	/// </summary>
	/// <param name="archive">The checkpoint archive.</param>
	/// <returns>The archive entry prefix.</returns>
	private static string ResolvePrefix(ZipArchive archive)
	{
		foreach (var entry in archive.Entries)
		{
			if (!entry.FullName.EndsWith("data.pkl", StringComparison.Ordinal))
			{
				continue;
			}

			return entry.FullName[..^"data.pkl".Length];
		}

		throw new InvalidDataException("Checkpoint archive does not contain a data.pkl entry.");
	}

	/// <summary>
	/// Gets a required archive entry.
	/// </summary>
	/// <param name="archive">The checkpoint archive.</param>
	/// <param name="entryName">The archive entry name.</param>
	/// <returns>The required archive entry.</returns>
	private static ZipArchiveEntry GetRequiredEntry(ZipArchive archive, string entryName)
	{
		var entry = archive.GetEntry(entryName);
		if (entry is null)
		{
			throw new InvalidDataException($"Checkpoint archive is missing '{entryName}'.");
		}

		return entry;
	}

	/// <summary>
	/// Parses checkpoint metadata and tensor manifests.
	/// </summary>
	/// <param name="root">The parsed checkpoint root.</param>
	/// <returns>The parsed checkpoint model.</returns>
	private static ParsedModel ParseModel(object root)
	{
		var rootMap = ExpectDictionary(root, "checkpoint root");
		var config = ConvertPublicList(GetRequiredValue(rootMap, ConfigKey));
		if (config.Count == 0)
		{
			throw new InvalidDataException("Checkpoint config is empty.");
		}

		var targetSampleRate = ConvertToInt32(config[^1], "config[-1]");
		var version = GetOptionalString(rootMap, VersionKey, "v2");
		var sampleRateLabel = GetOptionalString(rootMap, SampleRateKey, "unknown");
		var f0 = GetOptionalInt32(rootMap, F0Key, 1);
		var info = GetOptionalString(rootMap, InfoKey, string.Empty);

		var weightMap = ExpectDictionary(GetRequiredValue(rootMap, WeightKey), "weight");
		var manifests = new Dictionary<string, TensorManifest>(StringComparer.Ordinal);

		foreach (var pair in weightMap)
		{
			if (pair.Key.StartsWith(EncQPrefix, StringComparison.Ordinal))
			{
				continue;
			}

			if (pair.Value is not TensorManifest manifest)
			{
				throw new InvalidDataException($"Weight '{pair.Key}' is not a tensor.");
			}

			manifests.Add(pair.Key, manifest.WithName(pair.Key));
		}

		return new ParsedModel
		(
			config,
			targetSampleRate,
			version,
			sampleRateLabel,
			f0,
			info,
			manifests
		);
	}

	/// <summary>
	/// Loads tensor data for the specified manifests.
	/// </summary>
	/// <param name="archive">The checkpoint archive.</param>
	/// <param name="prefix">The archive entry prefix.</param>
	/// <param name="manifests">The tensor manifests.</param>
	/// <returns>The loaded tensors keyed by checkpoint name.</returns>
	private static Dictionary<string, PthTensor> LoadWeights
	(
		ZipArchive archive,
		string prefix,
		IReadOnlyDictionary<string, TensorManifest> manifests
	)
	{
		var storageCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		var weights = new Dictionary<string, PthTensor>(manifests.Count, StringComparer.Ordinal);

		foreach (var pair in manifests)
		{
			var manifest = pair.Value;
			if (!storageCache.TryGetValue(manifest.StorageKey, out var storageBytes))
			{
				var entry = GetRequiredEntry(archive, $"{prefix}data/{manifest.StorageKey}");
				using var entryStream = entry.Open();
				using var memory = new MemoryStream();
				entryStream.CopyTo(memory);
				storageBytes = memory.ToArray();
				storageCache.Add(manifest.StorageKey, storageBytes);
			}

			var tensorBytes = ExtractTensorBytes(storageBytes, manifest);
			weights.Add
			(
				pair.Key,
				new PthTensor(pair.Key, tensorBytes, manifest.Shape, manifest.DType)
			);
		}

		return weights;
	}

	/// <summary>
	/// Extracts contiguous tensor bytes from backing storage.
	/// </summary>
	/// <param name="storageBytes">The backing storage bytes.</param>
	/// <param name="manifest">The tensor manifest.</param>
	/// <returns>The extracted tensor bytes.</returns>
	private static byte[] ExtractTensorBytes(byte[] storageBytes, TensorManifest manifest)
	{
		var byteCount = checked(manifest.ElementCount * manifest.ElementSize);
		if (byteCount == 0)
		{
			return [];
		}

		var contiguousStride = ComputeContiguousStride(manifest.Shape);
		if (manifest.StorageOffset >= 0 && StrideEquals(manifest.Stride, contiguousStride))
		{
			var start = checked(manifest.StorageOffset * manifest.ElementSize);
			var end = checked(start + byteCount);
			if (end > storageBytes.Length)
			{
				throw new InvalidDataException($"Tensor '{manifest.Name}' extends past the backing storage.");
			}

			var result = new byte[byteCount];
			Buffer.BlockCopy(storageBytes, start, result, 0, byteCount);
			return result;
		}

		var output = new byte[byteCount];
		if (manifest.Shape.Length == 0)
		{
			var sourceIndex = checked(manifest.StorageOffset * manifest.ElementSize);
			Buffer.BlockCopy(storageBytes, sourceIndex, output, 0, manifest.ElementSize);
			return output;
		}

		var indices = new int[manifest.Shape.Length];
		for (var destinationElement = 0; destinationElement < manifest.ElementCount; destinationElement++)
		{
			var sourceElement = manifest.StorageOffset;
			for (var dimension = 0; dimension < indices.Length; dimension++)
			{
				sourceElement = checked(sourceElement + (indices[dimension] * manifest.Stride[dimension]));
			}

			var sourceByteIndex = checked(sourceElement * manifest.ElementSize);
			var destinationByteIndex = checked(destinationElement * manifest.ElementSize);
			var sourceByteEnd = checked(sourceByteIndex + manifest.ElementSize);
			if (sourceByteEnd > storageBytes.Length)
			{
				throw new InvalidDataException($"Tensor '{manifest.Name}' references data past the end of storage '{manifest.StorageKey}'.");
			}

			Buffer.BlockCopy
			(
				storageBytes,
				sourceByteIndex,
				output,
				destinationByteIndex,
				manifest.ElementSize
			);

			IncrementIndices(indices, manifest.Shape);
		}

		return output;
	}

	/// <summary>
	/// Advances a multidimensional index vector.
	/// </summary>
	/// <param name="indices">The indices to advance.</param>
	/// <param name="shape">The tensor shape.</param>
	private static void IncrementIndices(int[] indices, int[] shape)
	{
		for (var dimension = indices.Length - 1; dimension >= 0; dimension--)
		{
			indices[dimension]++;
			if (indices[dimension] < shape[dimension])
			{
				return;
			}

			indices[dimension] = 0;
		}
	}

	/// <summary>
	/// Computes contiguous strides for a tensor shape.
	/// </summary>
	/// <param name="shape">The tensor shape.</param>
	/// <returns>The contiguous stride values.</returns>
	internal static int[] ComputeContiguousStride(int[] shape)
	{
		if (shape.Length == 0)
		{
			return [];
		}

		var stride = new int[shape.Length];
		var running = 1;
		for (var dimension = shape.Length - 1; dimension >= 0; dimension--)
		{
			stride[dimension] = running;
			running = checked(running * shape[dimension]);
		}

		return stride;
	}

	/// <summary>
	/// Determines whether two stride vectors are equal.
	/// </summary>
	/// <param name="left">The left stride vector.</param>
	/// <param name="right">The right stride vector.</param>
	/// <returns><c>true</c> if the stride vectors are equal; otherwise, <c>false</c>.</returns>
	internal static bool StrideEquals(int[] left, int[] right)
	{
		if (left.Length != right.Length)
		{
			return false;
		}

		for (var index = 0; index < left.Length; index++)
		{
			if (left[index] != right[index])
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Fuses weight-normalized tensor pairs in place.
	/// </summary>
	/// <param name="weights">The tensors to update.</param>
	internal static void FuseWeightNorm(Dictionary<string, PthTensor> weights)
	{
		var gKeys = weights.Keys
			.Where(static key => key.EndsWith(WeightGSuffix, StringComparison.Ordinal))
			.ToArray();

		foreach (var gKey in gKeys)
		{
			if (!weights.TryGetValue(gKey, out var gTensor))
			{
				continue;
			}

			var baseName = gKey[..^WeightGSuffix.Length];
			var vKey = baseName + WeightVSuffix;
			if (!weights.TryGetValue(vKey, out var vTensor))
			{
				continue;
			}

			var fused = FuseWeightNormPair(baseName, gTensor, vTensor);
			weights.Remove(gKey);
			weights.Remove(vKey);
			weights[baseName] = fused;
		}
	}

	/// <summary>
	/// Fuses a weight-normalized tensor pair.
	/// </summary>
	/// <param name="outputName">The output tensor name.</param>
	/// <param name="gTensor">The gain tensor.</param>
	/// <param name="vTensor">The value tensor.</param>
	/// <returns>The fused tensor.</returns>
	internal static PthTensor FuseWeightNormPair(string outputName, PthTensor gTensor, PthTensor vTensor)
	{
		if (!IsSupportedWeightNormDType(gTensor.DType) || !IsSupportedWeightNormDType(vTensor.DType))
		{
			throw new NotSupportedException
			(
				$"Weight norm fusion for '{outputName}' requires float16 or float32 tensors; got '{gTensor.DType}' and '{vTensor.DType}'."
			);
		}

		if (gTensor.Shape.Length == 0 || vTensor.Shape.Length == 0)
		{
			throw new InvalidDataException($"Weight norm tensor '{outputName}' must be at least 1-D.");
		}

		if (gTensor.Shape[0] != vTensor.Shape[0])
		{
			throw new InvalidDataException($"Weight norm tensors '{outputName}_g' and '{outputName}_v' have incompatible leading dimensions.");
		}

		var channels = vTensor.Shape[0];
		var channelSize = checked(vTensor.ElementCount / channels);
		var gChannelStride = checked(gTensor.ElementCount / channels);
		if (channelSize <= 0 || gChannelStride <= 0)
		{
			throw new InvalidDataException($"Weight norm tensor '{outputName}' has an invalid shape.");
		}

		var output = new byte[vTensor.Data.Length];
		for (var channel = 0; channel < channels; channel++)
		{
			var gValue = ReadTensorScalar(gTensor, checked(channel * gChannelStride));
			var baseIndex = checked(channel * channelSize);

			var sumSquares = 0f;
			for (var offset = 0; offset < channelSize; offset++)
			{
				var value = ReadTensorScalar(vTensor, checked(baseIndex + offset));
				sumSquares += value * value;
			}

			var norm = MathF.Sqrt(sumSquares);
			var scale = norm > 0f ? (gValue / norm) : 0f;

			for (var offset = 0; offset < channelSize; offset++)
			{
				var value = ReadTensorScalar(vTensor, checked(baseIndex + offset));
				WriteTensorScalar(vTensor.DType, output, checked(baseIndex + offset), value * scale);
			}
		}

		return new PthTensor(outputName, output, vTensor.Shape, vTensor.DType);
	}

	/// <summary>
	/// Determines whether a tensor dtype supports weight normalization fusion.
	/// </summary>
	/// <param name="dtype">The tensor dtype.</param>
	/// <returns><c>true</c> if the dtype is supported; otherwise, <c>false</c>.</returns>
	internal static bool IsSupportedWeightNormDType(string dtype)
	{
		return string.Equals(dtype, "float16", StringComparison.Ordinal)
			|| string.Equals(dtype, "float32", StringComparison.Ordinal);
	}

	/// <summary>
	/// Reads a scalar value from a tensor element.
	/// </summary>
	/// <param name="tensor">The tensor to read.</param>
	/// <param name="elementIndex">The element index.</param>
	/// <returns>The scalar value.</returns>
	private static float ReadTensorScalar(PthTensor tensor, int elementIndex)
	{
		return ReadScalar(tensor.DType, tensor.Data, elementIndex);
	}

	/// <summary>
	/// Reads a scalar value from tensor bytes.
	/// </summary>
	/// <param name="dtype">The tensor dtype.</param>
	/// <param name="data">The tensor bytes.</param>
	/// <param name="elementIndex">The element index.</param>
	/// <returns>The scalar value.</returns>
	internal static float ReadScalar(string dtype, byte[] data, int elementIndex)
	{
		return dtype switch
		{
			"float16" => Float16BitsToSingle(ReadUInt16LittleEndian(data, checked(elementIndex * 2))),
			"float32" => BitConverter.Int32BitsToSingle(ReadInt32LittleEndian(data, checked(elementIndex * 4))),
			_ => throw new NotSupportedException($"Unsupported tensor dtype '{dtype}'."),
		};
	}

	/// <summary>
	/// Writes a scalar value to tensor bytes.
	/// </summary>
	/// <param name="dtype">The tensor dtype.</param>
	/// <param name="data">The destination bytes.</param>
	/// <param name="elementIndex">The element index.</param>
	/// <param name="value">The scalar value.</param>
	internal static void WriteTensorScalar(string dtype, byte[] data, int elementIndex, float value)
	{
		switch (dtype)
		{
			case "float16":
				WriteUInt16LittleEndian(data, checked(elementIndex * 2), SingleToFloat16Bits(value));
				break;

			case "float32":
				WriteInt32LittleEndian(data, checked(elementIndex * 4), BitConverter.SingleToInt32Bits(value));
				break;

			default:
				throw new NotSupportedException($"Unsupported tensor dtype '{dtype}'.");
		}
	}

	/// <summary>
	/// Reads a little-endian 16-bit unsigned integer from a byte array.
	/// </summary>
	/// <param name="data">The source bytes.</param>
	/// <param name="offset">The byte offset.</param>
	/// <returns>The 16-bit value.</returns>
	private static ushort ReadUInt16LittleEndian(byte[] data, int offset)
	{
		return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, sizeof(ushort)));
	}

	/// <summary>
	/// Reads a little-endian 32-bit integer from a byte array.
	/// </summary>
	/// <param name="data">The source bytes.</param>
	/// <param name="offset">The byte offset.</param>
	/// <returns>The 32-bit value.</returns>
	private static int ReadInt32LittleEndian(byte[] data, int offset)
	{
		return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, sizeof(int)));
	}

	/// <summary>
	/// Writes a little-endian 16-bit unsigned integer to a byte array.
	/// </summary>
	/// <param name="data">The destination bytes.</param>
	/// <param name="offset">The byte offset.</param>
	/// <param name="value">The value to write.</param>
	private static void WriteUInt16LittleEndian(byte[] data, int offset, ushort value)
	{
		BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, sizeof(ushort)), value);
	}

	/// <summary>
	/// Writes a little-endian 32-bit integer to a byte array.
	/// </summary>
	/// <param name="data">The destination bytes.</param>
	/// <param name="offset">The byte offset.</param>
	/// <param name="value">The value to write.</param>
	private static void WriteInt32LittleEndian(byte[] data, int offset, int value)
	{
		BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, sizeof(int)), value);
	}

	/// <summary>
	/// Converts Float16 bits to a single-precision value.
	/// </summary>
	/// <param name="bits">The Float16 bit pattern.</param>
	/// <returns>The converted single-precision value.</returns>
	internal static float Float16BitsToSingle(ushort bits)
	{
		return (float)BitConverter.UInt16BitsToHalf(bits);
	}

	/// <summary>
	/// Converts a single-precision value to Float16 bits.
	/// </summary>
	/// <param name="value">The value to convert.</param>
	/// <returns>The Float16 bit pattern.</returns>
	internal static ushort SingleToFloat16Bits(float value)
	{
		return BitConverter.HalfToUInt16Bits((Half)value);
	}

	/// <summary>
	/// Validates that a value is a string-keyed dictionary.
	/// </summary>
	/// <param name="value">The value to validate.</param>
	/// <param name="context">The validation context.</param>
	/// <returns>The validated dictionary.</returns>
	private static Dictionary<string, object?> ExpectDictionary(object? value, string context)
	{
		if (value is Dictionary<string, object?> dictionary)
		{
			return dictionary;
		}

		throw new InvalidDataException($"Expected {context} to be a dictionary.");
	}

	/// <summary>
	/// Gets a required dictionary value.
	/// </summary>
	/// <param name="dictionary">The source dictionary.</param>
	/// <param name="key">The dictionary key.</param>
	/// <returns>The dictionary value.</returns>
	private static object GetRequiredValue(Dictionary<string, object?> dictionary, string key)
	{
		if (!dictionary.TryGetValue(key, out var value) || value is null)
		{
			throw new InvalidDataException($"Checkpoint is missing required key '{key}'.");
		}

		return value;
	}

	/// <summary>
	/// Gets an optional string value.
	/// </summary>
	/// <param name="dictionary">The source dictionary.</param>
	/// <param name="key">The dictionary key.</param>
	/// <param name="fallback">The fallback value.</param>
	/// <returns>The string value.</returns>
	private static string GetOptionalString(Dictionary<string, object?> dictionary, string key, string fallback)
	{
		if (!dictionary.TryGetValue(key, out var value) || value is null)
		{
			return fallback;
		}

		return ConvertToString(value, key);
	}

	/// <summary>
	/// Gets an optional 32-bit integer value.
	/// </summary>
	/// <param name="dictionary">The source dictionary.</param>
	/// <param name="key">The dictionary key.</param>
	/// <param name="fallback">The fallback value.</param>
	/// <returns>The integer value.</returns>
	private static int GetOptionalInt32(Dictionary<string, object?> dictionary, string key, int fallback)
	{
		if (!dictionary.TryGetValue(key, out var value) || value is null)
		{
			return fallback;
		}

		return ConvertToInt32(value, key);
	}

	/// <summary>
	/// Converts a public config sequence to a normalized list.
	/// </summary>
	/// <param name="value">The value to convert.</param>
	/// <returns>The normalized list.</returns>
	private static List<object> ConvertPublicList(object value)
	{
		var sequence = value switch
		{
			List<object?> list => list,
			object?[] tuple => tuple.ToList(),
			_ => throw new InvalidDataException("Expected a list or tuple."),
		};

		var result = new List<object>(sequence.Count);
		foreach (var item in sequence)
		{
			result.Add(ConvertPublicValue(item));
		}

		return result;
	}

	/// <summary>
	/// Converts a public config value to a supported CLR value.
	/// </summary>
	/// <param name="value">The value to convert.</param>
	/// <returns>The converted value.</returns>
	private static object ConvertPublicValue(object? value)
	{
		return value switch
		{
			null => throw new InvalidDataException("Config values cannot be null."),
			byte byteValue => byteValue,
			ushort ushortValue => ushortValue,
			List<object?> list => ConvertPublicList(list),
			object?[] tuple => ConvertPublicList(tuple),
			string stringValue => stringValue,
			byte[] bytes => bytes,
			bool boolValue => boolValue,
			int intValue => intValue,
			long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
			long longValue => longValue,
			BigInteger bigInt when bigInt >= int.MinValue && bigInt <= int.MaxValue => (int)bigInt,
			BigInteger bigInt when bigInt >= long.MinValue && bigInt <= long.MaxValue => (long)bigInt,
			BigInteger bigInt => bigInt,
			double doubleValue => doubleValue,
			float floatValue => floatValue,
			_ => throw new InvalidDataException($"Unsupported config value type '{value.GetType().FullName}'."),
		};
	}

	/// <summary>
	/// Converts a checkpoint value to a string.
	/// </summary>
	/// <param name="value">The value to convert.</param>
	/// <param name="context">The conversion context.</param>
	/// <returns>The string value.</returns>
	private static string ConvertToString(object value, string context)
	{
		return value switch
		{
			string stringValue => stringValue,
			byte byteValue => byteValue.ToString(CultureInfo.InvariantCulture),
			ushort ushortValue => ushortValue.ToString(CultureInfo.InvariantCulture),
			int intValue => intValue.ToString(CultureInfo.InvariantCulture),
			long longValue => longValue.ToString(CultureInfo.InvariantCulture),
			_ => throw new InvalidDataException($"Expected '{context}' to be a string."),
		};
	}

	/// <summary>
	/// Converts a checkpoint value to a 32-bit integer.
	/// </summary>
	/// <param name="value">The value to convert.</param>
	/// <param name="context">The conversion context.</param>
	/// <returns>The integer value.</returns>
	private static int ConvertToInt32(object value, string context)
	{
		return value switch
		{
			byte byteValue => byteValue,
			ushort ushortValue => ushortValue,
			int intValue => intValue,
			long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
			BigInteger bigIntegerValue when bigIntegerValue >= int.MinValue && bigIntegerValue <= int.MaxValue => (int)bigIntegerValue,
			_ => throw new InvalidDataException($"Expected '{context}' to be a 32-bit integer."),
		};
	}

	/// <summary>
	/// Stores parsed checkpoint metadata before tensor loading.
	/// </summary>
	private sealed class ParsedModel
	{
		/// <summary>
		/// Initializes a new instance of the <see cref="ParsedModel"/> class.
		/// </summary>
		/// <param name="config">The parsed config values.</param>
		/// <param name="targetSampleRate">The target sample rate.</param>
		/// <param name="version">The model version.</param>
		/// <param name="sampleRateLabel">The sample rate label.</param>
		/// <param name="f0">The F0 flag.</param>
		/// <param name="info">The model info text.</param>
		/// <param name="weightManifests">The tensor manifests.</param>
		public ParsedModel
		(
			List<object> config,
			int targetSampleRate,
			string version,
			string sampleRateLabel,
			int f0,
			string info,
			Dictionary<string, TensorManifest> weightManifests
		)
		{
			Config = config;
			TargetSampleRate = targetSampleRate;
			Version = version;
			SampleRateLabel = sampleRateLabel;
			F0 = f0;
			Info = info;
			WeightManifests = weightManifests;
		}

		/// <summary>
		/// Gets the parsed config values.
		/// </summary>
		public List<object> Config { get; }

		/// <summary>
		/// Gets the target sample rate.
		/// </summary>
		public int TargetSampleRate { get; }

		/// <summary>
		/// Gets the model version.
		/// </summary>
		public string Version { get; }

		/// <summary>
		/// Gets the sample rate label.
		/// </summary>
		public string SampleRateLabel { get; }

		/// <summary>
		/// Gets the F0 flag.
		/// </summary>
		public int F0 { get; }

		/// <summary>
		/// Gets the model info text.
		/// </summary>
		public string Info { get; }

		/// <summary>
		/// Gets the tensor manifests keyed by weight name.
		/// </summary>
		public Dictionary<string, TensorManifest> WeightManifests { get; }
	}

	/// <summary>
	/// Parses the pickle payload stored in checkpoint archives.
	/// </summary>
	private sealed class PickleParser
	{
		private static readonly object Mark = new();

		private readonly List<object?> _stack = [];
		private readonly Dictionary<int, object?> _memo = [];
		private byte[] _buffer = [];
		private int _index;

		#pragma warning disable CA1502 // TODO: Refactor binary parser to reduce complexity
		/// <summary>
		/// Parses a pickle stream.
		/// </summary>
		/// <param name="stream">The pickle stream.</param>
		/// <returns>The parsed pickle root value.</returns>
		public object Parse(Stream stream)
		{
			ArgumentNullException.ThrowIfNull(stream);

			using var memory = new MemoryStream();
			stream.CopyTo(memory);
			_buffer = memory.ToArray();
			_index = 0;
			_stack.Clear();
			_memo.Clear();

			while (_index < _buffer.Length)
			{
				switch (ReadByte())
				{
					case 0x2E:
						return _stack.Count != 0
							? _stack[^1] ?? throw new InvalidDataException("Pickle STOP encountered with null on stack.")
							: throw new InvalidDataException("Pickle STOP encountered with an empty stack.");

					case 0x80:
						_ = ReadByte();
						break;

					case 0x95:
						_ = ReadUInt64LittleEndian();
						break;

					case 0x94:
						_memo.Add(_memo.Count, Peek());
						break;

					case 0x28:
						_stack.Add(Mark);
						break;

					case 0x29:
						_stack.Add(Array.Empty<object?>());
						break;

					case 0x5D:
						_stack.Add(new List<object?>());
						break;

					case 0x7D:
						_stack.Add(new Dictionary<string, object?>(StringComparer.Ordinal));
						break;

					case 0x4E:
						_stack.Add(null);
						break;

					case 0x88:
						_stack.Add(true);
						break;

					case 0x89:
						_stack.Add(false);
						break;

					case 0x4A:
						_stack.Add(ReadInt32LittleEndian());
						break;

					case 0x4B:
						_stack.Add(ReadByte());
						break;

					case 0x4D:
						_stack.Add((int)ReadUInt16LittleEndian());
						break;

					case 0x8A:
						_stack.Add(ReadLong1());
						break;

					case 0x47:
						_stack.Add(ReadBinaryFloat());
						break;

					case 0x55:
						_stack.Add(ReadEncodedString(ReadByte(), Encoding.UTF8));
						break;

					case 0x54:
						_stack.Add(ReadEncodedString(ReadInt32LittleEndian(), Encoding.UTF8));
						break;

					case 0x58:
						_stack.Add(ReadEncodedString(ReadInt32LittleEndian(), Encoding.UTF8));
						break;

					case 0x8C:
						_stack.Add(ReadEncodedString(ReadByte(), Encoding.UTF8));
						break;

					case 0x8D:
						_stack.Add(ReadEncodedString(ReadLength64(), Encoding.UTF8));
						break;

					case 0x43:
						_stack.Add(ReadBytes(ReadByte()));
						break;

					case 0x42:
						_stack.Add(ReadBytes(ReadInt32LittleEndian()));
						break;

					case 0x8E:
						_stack.Add(ReadBytes(ReadLength64()));
						break;

					case 0x61:
						AppendToList(Pop());
						break;

					case 0x65:
						AppendMarkedItemsToList();
						break;

					case 0x73:
						SetSingleItem();
						break;

					case 0x75:
						SetMarkedItems();
						break;

					case 0x74:
						_stack.Add(PopMarkedItems().ToArray());
						break;

					case 0x85:
						_stack.Add(new object?[] { Pop() });
						break;

					case 0x86:
					{
						var item2 = Pop();
						var item1 = Pop();
						_stack.Add(new object?[] { item1, item2 });
						break;
					}

					case 0x87:
					{
						var item3 = Pop();
						var item2 = Pop();
						var item1 = Pop();
						_stack.Add(new object?[] { item1, item2, item3 });
						break;
					}

					case 0x63:
						_stack.Add(new GlobalReference(ReadLine(), ReadLine()));
						break;

					case 0x93:
					{
						var name = PopString();
						var module = PopString();
						_stack.Add(new GlobalReference(module, name));
						break;
					}

					case 0x51:
						_stack.Add(ResolvePersistentId(Pop()));
						break;

					case 0x52:
					{
						var args = Pop();
						var callable = Pop();
						_stack.Add(ApplyReduce(callable, args));
						break;
					}

					case 0x62:
					{
						var state = Pop();
						var instance = Pop();
						_stack.Add(ApplyBuild(instance, state));
						break;
					}

					case 0x71:
						_memo[ReadByte()] = Peek();
						break;

					case 0x72:
						_memo[ReadInt32LittleEndian()] = Peek();
						break;

					case 0x68:
						_stack.Add(GetMemoValue(ReadByte()));
						break;

					case 0x6A:
						_stack.Add(GetMemoValue(ReadInt32LittleEndian()));
						break;

					default:
						throw new NotSupportedException($"Unsupported pickle opcode 0x{_buffer[_index - 1]:X2}.");
				}
			}

			throw new InvalidDataException("Pickle stream ended without STOP.");
		}
		#pragma warning restore CA1502

		/// <summary>
		/// Returns the value at the top of the pickle stack.
		/// </summary>
		/// <returns>The value at the top of the stack.</returns>
		private object? Peek()
		{
			if (_stack.Count == 0)
			{
				throw new InvalidDataException("Pickle stack is empty.");
			}

			return _stack[^1];
		}

		/// <summary>
		/// Removes and returns the value at the top of the pickle stack.
		/// </summary>
		/// <returns>The removed stack value.</returns>
		private object? Pop()
		{
			if (_stack.Count == 0)
			{
				throw new InvalidDataException("Pickle stack is empty.");
			}

			var value = _stack[^1];
			_stack.RemoveAt(_stack.Count - 1);
			return value;
		}

		/// <summary>
		/// Removes and returns the items above the last pickle mark.
		/// </summary>
		/// <returns>The removed stack items.</returns>
		private List<object?> PopMarkedItems()
		{
			var markIndex = _stack.LastIndexOf(Mark);
			if (markIndex < 0)
			{
				throw new InvalidDataException("Pickle MARK not found.");
			}

			var count = _stack.Count - markIndex - 1;
			var values = _stack.GetRange(markIndex + 1, count);
			_stack.RemoveRange(markIndex, count + 1);
			return values;
		}

		/// <summary>
		/// Appends an item to the list at the top of the stack.
		/// </summary>
		/// <param name="item">The item to append.</param>
		private void AppendToList(object? item)
		{
			var target = Peek();
			if (target is not List<object?> list)
			{
				throw new InvalidDataException("APPEND target is not a list.");
			}

			list.Add(item);
		}

		/// <summary>
		/// Appends the items above the last mark to the target list.
		/// </summary>
		private void AppendMarkedItemsToList()
		{
			var markIndex = _stack.LastIndexOf(Mark);
			if (markIndex < 1 || _stack[markIndex - 1] is not List<object?> list)
			{
				throw new InvalidDataException("APPENDS target is not a list.");
			}

			var items = _stack.GetRange(markIndex + 1, _stack.Count - markIndex - 1);
			list.AddRange(items);
			_stack.RemoveRange(markIndex, _stack.Count - markIndex);
		}

		/// <summary>
		/// Assigns a single key-value pair to the target dictionary.
		/// </summary>
		private void SetSingleItem()
		{
			var value = Pop();
			var key = PopString();
			var target = Peek();
			if (target is not Dictionary<string, object?> dictionary)
			{
				throw new InvalidDataException("SETITEM target is not a dictionary.");
			}

			dictionary[key] = value;
		}

		/// <summary>
		/// Assigns the marked key-value pairs to the target dictionary.
		/// </summary>
		private void SetMarkedItems()
		{
			var markIndex = _stack.LastIndexOf(Mark);
			if (markIndex < 1 || _stack[markIndex - 1] is not Dictionary<string, object?> dictionary)
			{
				throw new InvalidDataException("SETITEMS target is not a dictionary.");
			}

			var items = _stack.GetRange(markIndex + 1, _stack.Count - markIndex - 1);
			if ((items.Count & 1) != 0)
			{
				throw new InvalidDataException("SETITEMS received an odd number of stack items.");
			}

			for (var index = 0; index < items.Count; index += 2)
			{
				if (items[index] is not string key)
				{
					throw new InvalidDataException("SETITEMS key is not a string.");
				}

				dictionary[key] = items[index + 1];
			}

			_stack.RemoveRange(markIndex, _stack.Count - markIndex);
		}

		/// <summary>
		/// Gets a memoized pickle value.
		/// </summary>
		/// <param name="index">The memo slot index.</param>
		/// <returns>The memoized value.</returns>
		private object? GetMemoValue(int index)
		{
			if (!_memo.TryGetValue(index, out var value))
			{
				throw new InvalidDataException($"Pickle memo slot {index} was not initialized.");
			}

			return value;
		}

		/// <summary>
		/// Applies a supported pickle REDUCE operation.
		/// </summary>
		/// <param name="callable">The callable reference.</param>
		/// <param name="args">The callable arguments.</param>
		/// <returns>The reduced value.</returns>
		private static object ApplyReduce(object? callable, object? args)
		{
			var global = callable as GlobalReference
				?? throw new InvalidDataException("REDUCE callable is not a global reference.");
			var tuple = AsTuple(args);

			return global.Module switch
			{
				"collections" when global.Name == "OrderedDict" => new Dictionary<string, object?>(StringComparer.Ordinal),
				"torch._utils" when global.Name == "_rebuild_tensor_v2" => BuildTensor(tuple),
				"torch._utils" when global.Name == "_rebuild_parameter" => tuple[0] ?? throw new InvalidDataException("Parameter tensor is null."),
				"torch._utils" when global.Name == "_rebuild_parameter_with_state" => tuple[0] ?? throw new InvalidDataException("Parameter tensor is null."),
				"torch" when global.Name == "device" => tuple.Length > 0
					? ConvertToString(tuple[0] ?? throw new InvalidDataException("torch.device argument is null."), "torch.device")
					: "cpu",
				_ => throw new NotSupportedException($"Unsupported REDUCE target '{global.Module}.{global.Name}'."),
			};
		}

		/// <summary>
		/// Applies a supported pickle BUILD operation.
		/// </summary>
		/// <param name="instance">The instance to update.</param>
		/// <param name="state">The serialized state.</param>
		/// <returns>The built value.</returns>
		private static object ApplyBuild(object? instance, object? state)
		{
			if (state is null)
			{
				return instance ?? throw new InvalidDataException("BUILD instance is null.");
			}

			if (state is Dictionary<string, object?> dictionary && dictionary.Count == 0)
			{
				return instance ?? throw new InvalidDataException("BUILD instance is null.");
			}

			if (state is object?[] tuple && tuple.Length == 0)
			{
				return instance ?? throw new InvalidDataException("BUILD instance is null.");
			}

			if (state is byte[] { Length: 0 })
			{
				return instance ?? throw new InvalidDataException("BUILD instance is null.");
			}

			throw new NotSupportedException($"Unsupported BUILD state type '{state.GetType().FullName}'.");
		}

		/// <summary>
		/// Builds a tensor manifest from a rebuild tuple.
		/// </summary>
		/// <param name="tuple">The rebuild tuple.</param>
		/// <returns>The tensor manifest.</returns>
		private static TensorManifest BuildTensor(object?[] tuple)
		{
			if (tuple.Length < 4 || tuple[0] is not StorageReference storage)
			{
				throw new InvalidDataException("Tensor rebuild tuple is malformed.");
			}

			var storageOffset = ConvertToInt32(tuple[1] ?? throw new InvalidDataException("Tensor storage offset is null."), "storage offset");
			var shape = ToInt32Array(tuple[2], "shape");
			var stride = ToInt32Array(tuple[3], "stride");

			if (stride.Length != shape.Length)
			{
				throw new InvalidDataException("Tensor shape and stride rank do not match.");
			}

			return new TensorManifest
			(
				string.Empty,
				storage.Key,
				storage.DType,
				storage.ElementSize,
				shape,
				stride,
				storageOffset
			);
		}

		/// <summary>
		/// Resolves a persistent storage identifier.
		/// </summary>
		/// <param name="persistentId">The persistent identifier.</param>
		/// <returns>The resolved storage reference.</returns>
		private static StorageReference ResolvePersistentId(object? persistentId)
		{
			var tuple = AsTuple(persistentId);
			if (tuple.Length < 5)
			{
				throw new InvalidDataException("Persistent storage ID is malformed.");
			}

			if (tuple[0] is not string kind || !string.Equals(kind, "storage", StringComparison.Ordinal))
			{
				throw new NotSupportedException("Only storage persistent IDs are supported.");
			}

			if (tuple[1] is not GlobalReference storageType)
			{
				throw new InvalidDataException("Persistent storage ID is missing the storage type.");
			}

			var key = ConvertToString(tuple[2] ?? throw new InvalidDataException("Persistent storage key is null."), "storage key");
			var (dtype, elementSize) = MapStorageType(storageType);
			return new StorageReference(key, dtype, elementSize);
		}

		/// <summary>
		/// Maps a pickle storage type to tensor metadata.
		/// </summary>
		/// <param name="storageType">The storage type reference.</param>
		/// <returns>The tensor dtype and element size.</returns>
		private static (string DType, int ElementSize) MapStorageType(GlobalReference storageType)
		{
			return storageType.Name switch
			{
				"HalfStorage" => ("float16", 2),
				"FloatStorage" => ("float32", 4),
				"DoubleStorage" => ("float64", 8),
				"BFloat16Storage" => ("bfloat16", 2),
				"LongStorage" => ("int64", 8),
				"IntStorage" => ("int32", 4),
				"ShortStorage" => ("int16", 2),
				"ByteStorage" => ("uint8", 1),
				"CharStorage" => ("int8", 1),
				"BoolStorage" => ("bool", 1),
				_ => throw new NotSupportedException($"Unsupported storage type '{storageType.Module}.{storageType.Name}'."),
			};
		}

		/// <summary>
		/// Converts a pickle sequence value to a tuple array.
		/// </summary>
		/// <param name="value">The value to convert.</param>
		/// <returns>The tuple array.</returns>
		private static object?[] AsTuple(object? value)
		{
			return value switch
			{
				object?[] tuple => tuple,
				List<object?> list => [.. list],
				_ => throw new InvalidDataException("Expected a tuple."),
			};
		}

		/// <summary>
		/// Converts a pickle sequence to a 32-bit integer array.
		/// </summary>
		/// <param name="value">The value to convert.</param>
		/// <param name="context">The conversion context.</param>
		/// <returns>The integer array.</returns>
		private static int[] ToInt32Array(object? value, string context)
		{
			var tuple = AsTuple(value);
			var result = new int[tuple.Length];
			for (var index = 0; index < tuple.Length; index++)
			{
				result[index] = ConvertToInt32
				(
					tuple[index] ?? throw new InvalidDataException($"Tensor {context} contains null."),
					context
				);
			}

			return result;
		}

		/// <summary>
		/// Removes and returns a string from the pickle stack.
		/// </summary>
		/// <returns>The string value.</returns>
		private string PopString()
		{
			var value = Pop();
			if (value is not string stringValue)
			{
				throw new InvalidDataException("Pickle value is not a string.");
			}

			return stringValue;
		}

		/// <summary>
		/// Reads the next byte from the pickle buffer.
		/// </summary>
		/// <returns>The next byte.</returns>
		private byte ReadByte()
		{
			if (_index >= _buffer.Length)
			{
				throw new EndOfStreamException();
			}

			return _buffer[_index++];
		}

		/// <summary>
		/// Reads the next little-endian 16-bit unsigned integer from the pickle buffer.
		/// </summary>
		/// <returns>The next 16-bit value.</returns>
		private ushort ReadUInt16LittleEndian()
		{
			var value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(_index, sizeof(ushort)));
			_index += sizeof(ushort);
			return value;
		}

		/// <summary>
		/// Reads the next little-endian 32-bit integer from the pickle buffer.
		/// </summary>
		/// <returns>The next 32-bit value.</returns>
		private int ReadInt32LittleEndian()
		{
			var value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_index, sizeof(int)));
			_index += sizeof(int);
			return value;
		}

		/// <summary>
		/// Reads the next little-endian 64-bit unsigned integer from the pickle buffer.
		/// </summary>
		/// <returns>The next 64-bit value.</returns>
		private ulong ReadUInt64LittleEndian()
		{
			var value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.AsSpan(_index, sizeof(ulong)));
			_index += sizeof(ulong);
			return value;
		}

		/// <summary>
		/// Reads a 64-bit pickle length as a 32-bit integer.
		/// </summary>
		/// <returns>The length value.</returns>
		private int ReadLength64()
		{
			var length = ReadUInt64LittleEndian();
			if (length > int.MaxValue)
			{
				throw new InvalidDataException("Pickle object is too large.");
			}

			return (int)length;
		}

		/// <summary>
		/// Reads an encoded string from the pickle buffer.
		/// </summary>
		/// <param name="length">The encoded byte length.</param>
		/// <param name="encoding">The string encoding.</param>
		/// <returns>The decoded string.</returns>
		private string ReadEncodedString(int length, Encoding encoding)
		{
			return encoding.GetString(ReadBytes(length));
		}

		/// <summary>
		/// Reads a byte range from the pickle buffer.
		/// </summary>
		/// <param name="length">The byte count.</param>
		/// <returns>The requested bytes.</returns>
		private byte[] ReadBytes(int length)
		{
			if (length < 0)
			{
				throw new InvalidDataException("Pickle length cannot be negative.");
			}

			var bytes = _buffer.AsSpan(_index, length).ToArray();
			_index += length;
			return bytes;
		}

		/// <summary>
		/// Reads a UTF-8 line from the pickle buffer.
		/// </summary>
		/// <returns>The line text.</returns>
		private string ReadLine()
		{
			var start = _index;
			while (_index < _buffer.Length && _buffer[_index] != (byte)'\n')
			{
				_index++;
			}

			if (_index >= _buffer.Length)
			{
				throw new InvalidDataException("Pickle line was not terminated.");
			}

			var line = Encoding.UTF8.GetString(_buffer, start, _index - start);
			_index++;
			return line;
		}

		/// <summary>
		/// Reads a LONG1 integer from the pickle buffer.
		/// </summary>
		/// <returns>The decoded integer.</returns>
		private BigInteger ReadLong1()
		{
			var length = ReadByte();
			var bytes = ReadBytes(length);
			return new BigInteger(bytes);
		}

		/// <summary>
		/// Reads a big-endian binary floating-point value from the pickle buffer.
		/// </summary>
		/// <returns>The decoded floating-point value.</returns>
		private double ReadBinaryFloat()
		{
			var bits = BinaryPrimitives.ReadInt64BigEndian(_buffer.AsSpan(_index, sizeof(long)));
			_index += sizeof(long);
			return BitConverter.Int64BitsToDouble(bits);
		}
	}

	/// <summary>
	/// Represents a referenced pickle global.
	/// </summary>
	private sealed class GlobalReference
	{
		/// <summary>
		/// Initializes a new instance of the <see cref="GlobalReference"/> class.
		/// </summary>
		/// <param name="module">The module name.</param>
		/// <param name="name">The global name.</param>
		public GlobalReference(string module, string name)
		{
			Module = module;
			Name = name;
		}

		/// <summary>
		/// Gets the module name.
		/// </summary>
		public string Module { get; }

		/// <summary>
		/// Gets the global name.
		/// </summary>
		public string Name { get; }
	}

	/// <summary>
	/// Represents a referenced tensor storage.
	/// </summary>
	private sealed class StorageReference
	{
		/// <summary>
		/// Initializes a new instance of the <see cref="StorageReference"/> class.
		/// </summary>
		/// <param name="key">The storage key.</param>
		/// <param name="dType">The tensor dtype.</param>
		/// <param name="elementSize">The element size in bytes.</param>
		public StorageReference(string key, string dType, int elementSize)
		{
			Key = key;
			DType = dType;
			ElementSize = elementSize;
		}

		/// <summary>
		/// Gets the storage key.
		/// </summary>
		public string Key { get; }

		/// <summary>
		/// Gets the tensor dtype.
		/// </summary>
		public string DType { get; }

		/// <summary>
		/// Gets the element size in bytes.
		/// </summary>
		public int ElementSize { get; }
	}

	/// <summary>
	/// Describes tensor storage and shape metadata.
	/// </summary>
	private sealed class TensorManifest
	{
		/// <summary>
		/// Initializes a new instance of the <see cref="TensorManifest"/> class.
		/// </summary>
		/// <param name="name">The tensor name.</param>
		/// <param name="storageKey">The storage key.</param>
		/// <param name="dType">The tensor dtype.</param>
		/// <param name="elementSize">The element size in bytes.</param>
		/// <param name="shape">The tensor shape.</param>
		/// <param name="stride">The tensor stride.</param>
		/// <param name="storageOffset">The storage offset.</param>
		public TensorManifest
		(
			string name,
			string storageKey,
			string dType,
			int elementSize,
			int[] shape,
			int[] stride,
			int storageOffset
		)
		{
			Name = name;
			StorageKey = storageKey;
			DType = dType;
			ElementSize = elementSize;
			Shape = [.. shape];
			Stride = [.. stride];
			StorageOffset = storageOffset;
			ElementCount = ComputeElementCount(shape);
		}

		/// <summary>
		/// Gets the tensor name.
		/// </summary>
		public string Name { get; }

		/// <summary>
		/// Gets the backing storage key.
		/// </summary>
		public string StorageKey { get; }

		/// <summary>
		/// Gets the tensor dtype.
		/// </summary>
		public string DType { get; }

		/// <summary>
		/// Gets the element size in bytes.
		/// </summary>
		public int ElementSize { get; }

		/// <summary>
		/// Gets the tensor shape.
		/// </summary>
		public int[] Shape { get; }

		/// <summary>
		/// Gets the tensor stride.
		/// </summary>
		public int[] Stride { get; }

		/// <summary>
		/// Gets the storage offset.
		/// </summary>
		public int StorageOffset { get; }

		/// <summary>
		/// Gets the number of tensor elements.
		/// </summary>
		public int ElementCount { get; }

		/// <summary>
		/// Creates a copy with a different tensor name.
		/// </summary>
		/// <param name="name">The tensor name.</param>
		/// <returns>The renamed manifest.</returns>
		public TensorManifest WithName(string name)
		{
			return new TensorManifest
			(
				name,
				StorageKey,
				DType,
				ElementSize,
				Shape,
				Stride,
				StorageOffset
			);
		}

		/// <summary>
		/// Computes the number of elements in a tensor shape.
		/// </summary>
		/// <param name="shape">The tensor shape.</param>
		/// <returns>The element count.</returns>
		private static int ComputeElementCount(int[] shape)
		{
			if (shape.Length == 0)
			{
				return 1;
			}

			var count = 1;
			foreach (var dimension in shape)
			{
				count = checked(count * dimension);
			}

			return count;
		}
	}
}

/// <summary>
/// Represents a loaded checkpoint model.
/// </summary>
internal sealed class PthModel
{
	/// <summary>
	/// Initializes a new instance of the <see cref="PthModel"/> class.
	/// </summary>
	/// <param name="config">The raw config values.</param>
	/// <param name="targetSampleRate">The target sample rate.</param>
	/// <param name="version">The model version.</param>
	/// <param name="sampleRateLabel">The sample rate label.</param>
	/// <param name="f0">The F0 flag.</param>
	/// <param name="info">The model info text.</param>
	/// <param name="weights">The loaded tensors.</param>
	public PthModel
	(
		IReadOnlyList<object> config,
		int targetSampleRate,
		string version,
		string sampleRateLabel,
		int f0,
		string info,
		IReadOnlyDictionary<string, PthTensor> weights
	)
	{
		Config = config;
		TargetSampleRate = targetSampleRate;
		Version = version;
		SampleRateLabel = sampleRateLabel;
		F0 = f0;
		Info = info;
		Weights = weights;
	}

	/// <summary>
	/// Gets the raw config values.
	/// </summary>
	public IReadOnlyList<object> Config { get; }

	/// <summary>
	/// Gets the target sample rate.
	/// </summary>
	public int TargetSampleRate { get; }

	/// <summary>
	/// Gets the model version.
	/// </summary>
	public string Version { get; }

	/// <summary>
	/// Gets the sample rate label.
	/// </summary>
	public string SampleRateLabel { get; }

	/// <summary>
	/// Gets the F0 flag.
	/// </summary>
	public int F0 { get; }

	/// <summary>
	/// Gets the model info text.
	/// </summary>
	public string Info { get; }

	/// <summary>
	/// Gets the loaded tensors keyed by name.
	/// </summary>
	public IReadOnlyDictionary<string, PthTensor> Weights { get; }
}

/// <summary>
/// Represents a tensor loaded from a checkpoint.
/// </summary>
internal sealed class PthTensor
{
	/// <summary>
	/// Initializes a new instance of the <see cref="PthTensor"/> class.
	/// </summary>
	/// <param name="name">The tensor name.</param>
	/// <param name="data">The tensor bytes.</param>
	/// <param name="shape">The tensor shape.</param>
	/// <param name="dType">The tensor dtype.</param>
	public PthTensor(string name, byte[] data, int[] shape, string dType)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentNullException.ThrowIfNull(data);
		ArgumentNullException.ThrowIfNull(shape);
		ArgumentException.ThrowIfNullOrWhiteSpace(dType);

		Name = name;
		Data = data;
		Shape = [.. shape];
		DType = dType;
		ElementCount = ComputeElementCount(shape);
	}

	/// <summary>
	/// Gets the tensor name.
	/// </summary>
	public string Name { get; }

	/// <summary>
	/// Gets the tensor bytes.
	/// </summary>
	public byte[] Data { get; }

	/// <summary>
	/// Gets the tensor shape.
	/// </summary>
	public int[] Shape { get; }

	/// <summary>
	/// Gets the tensor dtype.
	/// </summary>
	public string DType { get; }

	/// <summary>
	/// Gets the number of tensor elements.
	/// </summary>
	public int ElementCount { get; }

	/// <summary>
	/// Computes the number of elements in a tensor shape.
	/// </summary>
	/// <param name="shape">The tensor shape.</param>
	/// <returns>The element count.</returns>
	private static int ComputeElementCount(int[] shape)
	{
		if (shape.Length == 0)
		{
			return 1;
		}

		var count = 1;
		foreach (var dimension in shape)
		{
			count = checked(count * dimension);
		}

		return count;
	}
}
