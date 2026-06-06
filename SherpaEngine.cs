using System.IO.Compression;
using System.Text;
using System.Text.Json;

using SherpaOnnx;

namespace Talktastic;

/// <summary>
/// Synthesizes speech using sherpa-onnx in-process TTS with Piper ONNX models.
/// Replaces the piper.exe process invocation approach -- no external process needed.
/// </summary>
internal static class SherpaEngine
{
	private const int ModelProto_MetadataProps = 14;
	private const int WireType_LengthDelimited = 2;
	private const int StringEntry_Key = 1;
	private const int StringEntry_Value = 2;

	/// <summary>
	/// Synthesizes text to WAV bytes using a Piper ONNX model via sherpa-onnx.
	/// </summary>
	public static byte[] SynthesizeToWav
	(
		string text,
		string modelPath,
		double? lengthScale
	)
	{
		NativeExtractor.EnsureAvailable(DllGroup.OnnxRuntime);

		var tokensPath = EnsureTokensFile(modelPath);
		var espeakDataDir = EnsureEspeakData(modelPath);

		EnsureOnnxMetadata(modelPath);

		var config = new OfflineTtsConfig();
		config.Model.Vits.Model = modelPath;
		config.Model.Vits.Tokens = tokensPath;
		config.Model.Vits.DataDir = espeakDataDir;
		config.Model.Vits.NoiseScale = 0.667f;
		config.Model.Vits.NoiseScaleW = 0.8f;
		config.Model.Vits.LengthScale = lengthScale is not null ? (float)lengthScale.Value : 1.0f;
		config.Model.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
		config.Model.Debug = 0;
		config.Model.Provider = "cpu";

		using var tts = new OfflineTts(config);

		var audio = tts.Generate(text, speed: 1.0f, speakerId: 0);

		return BuildWav(audio.Samples, tts.SampleRate);
	}

	/// <summary>
	/// Ensures a tokens.txt file exists alongside the ONNX model.
	/// Generated from the .onnx.json config's phoneme_id_map if missing.
	/// </summary>
	internal static string EnsureTokensFile(string modelPath)
	{
		var tokensPath = Path.ChangeExtension(modelPath, ".tokens.txt");
		if (File.Exists(tokensPath))
		{
			return tokensPath;
		}

		var configPath = modelPath + ".json";
		if (!File.Exists(configPath))
		{
			throw new FileNotFoundException
			(
				$"Piper config not found: {configPath}"
			);
		}

		var json = File.ReadAllText(configPath, Encoding.UTF8);
		using var doc = JsonDocument.Parse(json);

		if (!doc.RootElement.TryGetProperty("phoneme_id_map", out var phonemeMap))
		{
			throw new InvalidDataException
			(
				$"Piper config missing 'phoneme_id_map': {configPath}"
			);
		}

		var entries = new List<(int Id, string Symbol)>();
		foreach (var prop in phonemeMap.EnumerateObject())
		{
			foreach (var id in prop.Value.EnumerateArray())
			{
				entries.Add((id.GetInt32(), prop.Name));
			}
		}

		entries.Sort((a, b) => a.Id.CompareTo(b.Id));

		var sb = new StringBuilder();
		foreach (var (id, symbol) in entries)
		{
			sb.Append(symbol).Append(' ').Append(id).Append('\n');
		}

		File.WriteAllText(tokensPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		return tokensPath;
	}

	/// <summary>
	/// Locates or extracts the espeak-ng-data directory needed for phonemization.
	/// Searches alongside the model, then in Piper's dir, then extracts from embedded resource.
	/// </summary>
	internal static string EnsureEspeakData(string modelPath)
	{
		// Check alongside the voices dir (typical layout: .piper-tts/espeak-ng-data/)
		var voicesDir = Path.GetDirectoryName(modelPath);
		if (voicesDir is not null)
		{
			var parentDir = Path.GetDirectoryName(voicesDir);
			if (parentDir is not null)
			{
				var espeakDir = Path.Combine(parentDir, "espeak-ng-data");
				if (Directory.Exists(espeakDir))
				{
					return espeakDir;
				}
			}
		}

		// Check all search bases
		foreach (var basePath in AppPaths.SearchBases)
		{
			var candidate = Path.Combine(basePath, ".piper-tts", "espeak-ng-data");
			if (Directory.Exists(candidate))
			{
				return candidate;
			}
		}

		// Not found -- extract from embedded resource
		return ExtractEspeakData();
	}

	private static string ExtractEspeakData()
	{
		var assembly = typeof(SherpaEngine).Assembly;
		const string resourceName = "Talktastic.Piper.espeak-ng-data.zip";

		using var stream = assembly.GetManifestResourceStream(resourceName)
			?? throw new InvalidOperationException
			(
				"espeak-ng-data not embedded. Piper voice synthesis is not available in this build."
			);

		var targetDir = Path.Combine(AppPaths.SearchBases[0], ".piper-tts");
		Directory.CreateDirectory(targetDir);

		using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
		var espeakDir = Path.Combine(targetDir, "espeak-ng-data");
		Directory.CreateDirectory(espeakDir);

		foreach (var entry in zip.Entries)
		{
			if (string.IsNullOrEmpty(entry.Name))
			{
				continue;
			}

			var destPath = Path.GetFullPath(Path.Combine(espeakDir, entry.FullName));
			if (!destPath.StartsWith(espeakDir, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			var destDir = Path.GetDirectoryName(destPath)!;
			Directory.CreateDirectory(destDir);

			entry.ExtractToFile(destPath, overwrite: true);
		}

		return espeakDir;
	}

	/// <summary>
	/// Checks if the ONNX model has the required sherpa-onnx metadata
	/// (model_type, sample_rate, etc.) and patches it in-place if missing.
	/// </summary>
	internal static void EnsureOnnxMetadata(string modelPath)
	{
		var modelBytes = File.ReadAllBytes(modelPath);

		if (HasSherpaMetadata(modelBytes))
		{
			return;
		}

		// Read config to get actual sample rate and language
		var configPath = modelPath + ".json";
		if (!File.Exists(configPath))
		{
			return;
		}

		var json = File.ReadAllText(configPath, Encoding.UTF8);
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;

		var sampleRate = "22050";
		if (root.TryGetProperty("audio", out var audio)
			&& audio.TryGetProperty("sample_rate", out var sr))
		{
			sampleRate = sr.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
		}

		var voice = "en-us";
		if (root.TryGetProperty("espeak", out var espeak)
			&& espeak.TryGetProperty("voice", out var v))
		{
			voice = v.GetString() ?? "en-us";
		}

		var numSpeakers = "1";
		if (root.TryGetProperty("num_speakers", out var ns))
		{
			numSpeakers = ns.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
		}

		var metadata = new Dictionary<string, string>
		{
			["model_type"] = "vits",
			["comment"] = "piper",
			["sample_rate"] = sampleRate,
			["has_espeak"] = "1",
			["language"] = "English",
			["voice"] = voice,
			["n_speakers"] = numSpeakers,
		};

		// Append protobuf metadata_props to the model file
		var metadataBytes = EncodeMetadataProps(metadata);
		using var fs = new FileStream(modelPath, FileMode.Append, FileAccess.Write);
		fs.Write(metadataBytes);

		Console.Error.WriteLine($"Patched sherpa-onnx metadata into {Path.GetFileName(modelPath)}");
	}

	/// <summary>
	/// Checks if the ONNX model bytes contain a 'model_type' metadata property,
	/// indicating sherpa-onnx compatibility.
	/// </summary>
	private static bool HasSherpaMetadata(byte[] data)
	{
		// Quick scan for the string "model_type" in the file.
		// This is a heuristic -- protobuf metadata_props contain this as a key.
		var needle = Encoding.UTF8.GetBytes("model_type");
		return ContainsSequence(data, needle);
	}

	private static bool ContainsSequence(byte[] haystack, byte[] needle)
	{
		for (var i = 0; i <= haystack.Length - needle.Length; i++)
		{
			var found = true;
			for (var j = 0; j < needle.Length; j++)
			{
				if (haystack[i + j] != needle[j])
				{
					found = false;
					break;
				}
			}

			if (found)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Encodes metadata key-value pairs as protobuf StringStringEntryProto
	/// fields for ModelProto.metadata_props (field 14).
	/// </summary>
	private static byte[] EncodeMetadataProps(Dictionary<string, string> metadata)
	{
		using var ms = new MemoryStream();

		foreach (var (key, value) in metadata)
		{
			// Build the inner StringStringEntryProto message
			var keyBytes = Encoding.UTF8.GetBytes(key);
			var valueBytes = Encoding.UTF8.GetBytes(value);

			using var inner = new MemoryStream();

			// field 1 (key): string
			WriteTag(inner, StringEntry_Key, WireType_LengthDelimited);
			WriteVarint(inner, keyBytes.Length);
			inner.Write(keyBytes);

			// field 2 (value): string
			WriteTag(inner, StringEntry_Value, WireType_LengthDelimited);
			WriteVarint(inner, valueBytes.Length);
			inner.Write(valueBytes);

			var innerBytes = inner.ToArray();

			// Outer: field 14 (metadata_props): length-delimited
			WriteTag(ms, ModelProto_MetadataProps, WireType_LengthDelimited);
			WriteVarint(ms, innerBytes.Length);
			ms.Write(innerBytes);
		}

		return ms.ToArray();
	}

	private static void WriteTag(Stream stream, int fieldNumber, int wireType)
	{
		WriteVarint(stream, (fieldNumber << 3) | wireType);
	}

	private static void WriteVarint(Stream stream, int value)
	{
		var v = (uint)value;
		while (v >= 0x80)
		{
			stream.WriteByte((byte)(v | 0x80));
			v >>= 7;
		}

		stream.WriteByte((byte)v);
	}

	/// <summary>
	/// Builds a WAV file from raw PCM float samples.
	/// </summary>
	private static byte[] BuildWav(float[] samples, int sampleRate)
	{
		var bitsPerSample = 16;
		var channels = 1;
		var byteRate = sampleRate * channels * bitsPerSample / 8;
		var blockAlign = channels * bitsPerSample / 8;
		var dataSize = samples.Length * blockAlign;

		using var ms = new MemoryStream(44 + dataSize);
		using var writer = new BinaryWriter(ms);

		// RIFF header
		writer.Write("RIFF"u8);
		writer.Write(36 + dataSize);
		writer.Write("WAVE"u8);

		// fmt chunk
		writer.Write("fmt "u8);
		writer.Write(16); // chunk size
		writer.Write((short)1); // PCM format
		writer.Write((short)channels);
		writer.Write(sampleRate);
		writer.Write(byteRate);
		writer.Write((short)blockAlign);
		writer.Write((short)bitsPerSample);

		// data chunk
		writer.Write("data"u8);
		writer.Write(dataSize);

		// Convert float samples to 16-bit PCM
		foreach (var sample in samples)
		{
			var clamped = Math.Clamp(sample, -1.0f, 1.0f);
			var pcm = (short)(clamped * 32767);
			writer.Write(pcm);
		}

		return ms.ToArray();
	}
}
