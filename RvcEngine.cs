using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Talktastic;

#pragma warning disable CA1814

static partial class RvcEngine
{
	private const string RvcDirName = ".rvc";
	private const string VoicesSubDir = "voices";
	private const string InfraSubDir = "infra";
	private const string RegistryFileName = "rvcs.json";

	private const string ContentVecUrl = "https://huggingface.co/NaruseMioShirakana/MoeSS-SUBModel/resolve/main/vec-768-layer-12.onnx";
	private const string RmvpeUrl = "https://huggingface.co/lj1995/VoiceConversionWebUI/resolve/main/rmvpe.onnx";

	private const int InputSampleRate = 16000;
	private const int Window = 160;
	private const int XPadSeconds = 3;
	private const int XQuerySeconds = 10;
	private const int XCenterSeconds = 50;
	private const int XMaxSeconds = 50;
	private const float RmsMixRate = 0.25f;
	private const float Protect = 0.33f;
	private const int SpeakerId = 0;
	private const int NoiseChannels = 192;
	private const float RmvpeThreshold = 0.03f;
	private const int DefaultTargetSampleRate = 40000;

	private static readonly HttpClient Http = CreateHttpClient();

	private static readonly string[] SearchBases =
	[
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Talktastic"),
		Path.GetTempPath(),
		Environment.CurrentDirectory,
	];

	private static readonly float[] CentsMapping = CreateCentsMapping();

	private static string? _resolvedRvcDir;

	public static async Task<string> ResolveRvcModelAsync
	(
		string rvcQuery,
		CancellationToken ct
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(rvcQuery);
		ct.ThrowIfCancellationRequested();

		if (File.Exists(rvcQuery))
		{
			return Path.GetFullPath(rvcQuery);
		}

		if (LooksLikeLocalPath(rvcQuery))
		{
			throw new FileNotFoundException($"RVC model not found: '{rvcQuery}'.", rvcQuery);
		}

		var rvcDir = EnsureRvcDirectory();
		var voicesDir = Path.Combine(rvcDir, VoicesSubDir);
		var registryPath = Path.Combine(rvcDir, RegistryFileName);
		Directory.CreateDirectory(voicesDir);

		if (ModelDownloader.IsUrl(rvcQuery))
		{
			var cachedModelName = ModelDownloader.LookupRegistry(registryPath, rvcQuery);
			if (cachedModelName is not null)
			{
				var cachedPath = Path.Combine(voicesDir, $"{cachedModelName}.onnx");
				if (File.Exists(cachedPath))
				{
					return cachedPath;
				}
			}

			var resolved = await ModelDownloader.ResolveModelUrlAsync(Http, rvcQuery, ct).ConfigureAwait(false);

			string downloadPath;
			string modelName;

			if (resolved.IsZip)
			{
				await Console.Error.WriteLineAsync
				(
					$"Downloading and extracting RVC model '{resolved.ModelName}'..."
				).ConfigureAwait(false);

				var (onnxPath, extractedName) = await ModelDownloader.DownloadAndExtractZipAsync
				(
					Http, resolved.FileUrl, voicesDir, ct
				).ConfigureAwait(false);

				downloadPath = onnxPath;
				modelName = extractedName;
			}
			else
			{
				modelName = resolved.ModelName;
				downloadPath = Path.Combine(voicesDir, $"{modelName}.onnx");

				if (!File.Exists(downloadPath))
				{
					await Console.Error.WriteLineAsync
					(
						$"Downloading RVC model '{modelName}'..."
					).ConfigureAwait(false);

					await ModelDownloader.DownloadFileAsync(Http, resolved.FileUrl, downloadPath, ct).ConfigureAwait(false);
				}
			}

			var sizeMb = new FileInfo(downloadPath).Length / 1024 / 1024;
			await Console.Error.WriteLineAsync
			(
				$"Ready: {modelName} ({sizeMb} MB)."
			).ConfigureAwait(false);

			ModelDownloader.WriteRegistry(registryPath, rvcQuery, modelName);
			return downloadPath;
		}

		var namedPath = Path.Combine(voicesDir, $"{rvcQuery}.onnx");
		if (File.Exists(namedPath))
		{
			return namedPath;
		}

		foreach (var line in ReadRegistryLines(registryPath))
		{
			var tab = line.IndexOf('\t', StringComparison.Ordinal);
			if (tab < 0)
			{
				continue;
			}

			var modelName = line[(tab + 1)..];
			if (!string.Equals(modelName, rvcQuery, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			var resolvedPath = Path.Combine(voicesDir, $"{modelName}.onnx");
			if (File.Exists(resolvedPath))
			{
				return resolvedPath;
			}
		}

		throw new InvalidOperationException
		(
			$"Unknown RVC model '{rvcQuery}'. Use a friendly name already cached, a local .onnx path, or a URL."
		);
	}

	public static async Task EnsureInfraModelsAsync(CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();

		var infraDir = Path.Combine(EnsureRvcDirectory(), InfraSubDir);
		Directory.CreateDirectory(infraDir);

		await EnsureInfraModelAsync
		(
			ContentVecUrl,
			Path.Combine(infraDir, "vec-768-layer-12.onnx"),
			"ContentVec",
			ct
		).ConfigureAwait(false);

		await EnsureInfraModelAsync
		(
			RmvpeUrl,
			Path.Combine(infraDir, "rmvpe.onnx"),
			"RMVPE",
			ct
		).ConfigureAwait(false);
	}

	public static async Task<byte[]> ConvertAsync
	(
		byte[] wavBytes,
		string rvcModelPath,
		float pitchShiftSemitones,
		CancellationToken ct
	)
	{
		ArgumentNullException.ThrowIfNull(wavBytes);
		ArgumentException.ThrowIfNullOrWhiteSpace(rvcModelPath);

		ct.ThrowIfCancellationRequested();
		await EnsureInfraModelsAsync(ct).ConfigureAwait(false);

		var (samples, sampleRate, channels) = AudioDsp.ParseWavToFloat(wavBytes);
		var mono16k = AudioDsp.ResampleToMono16k(samples, sampleRate, channels);
		var normalized = NormalizePeak(mono16k, 0.95f);
		var filtered = AudioDsp.ButterworthHighPass(normalized);
		var analysisPad = AudioDsp.ReflectPad(filtered, Window / 2);
		var (optTs, inferenceAudio) = FindOptimalTimestamps(filtered, analysisPad);

		var infraDir = Path.Combine(EnsureRvcDirectory(), InfraSubDir);
		var vecPath = Path.Combine(infraDir, "vec-768-layer-12.onnx");
		var rmvpePath = Path.Combine(infraDir, "rmvpe.onnx");

		using var vecSession = CreateSession(vecPath);
		using var rmvpeSession = CreateSession(rmvpePath);
		using var rvcSession = CreateSession(rvcModelPath);

		var targetSampleRate = GetTargetSampleRate(rvcSession);
		var (pitchf, pitch) = ExtractF0(rmvpeSession, inferenceAudio, pitchShiftSemitones, ct);
		var convertedSegments = InferSegments
		(
			rvcSession,
			vecSession,
			inferenceAudio,
			pitch,
			pitchf,
			optTs,
			targetSampleRate,
			ct
		);

		var finalSamples = Concatenate(convertedSegments);

		return AudioDsp.EncodeWav(finalSamples, targetSampleRate);
	}

	private static string EnsureRvcDirectory()
	{
		if (_resolvedRvcDir is not null)
		{
			return _resolvedRvcDir;
		}

		foreach (var basePath in SearchBases)
		{
			var candidate = Path.Combine(basePath, RvcDirName);
			if (Directory.Exists(candidate))
			{
				_resolvedRvcDir = candidate;
				return candidate;
			}
		}

		foreach (var basePath in SearchBases)
		{
			var candidate = Path.Combine(basePath, RvcDirName);
			try
			{
				Directory.CreateDirectory(candidate);
				_resolvedRvcDir = candidate;
				return candidate;
			}
			catch (UnauthorizedAccessException)
			{
			}
			catch (IOException)
			{
			}
		}

		throw new InvalidOperationException("Failed to create the .rvc cache directory in LOCALAPPDATA, TEMP, or CWD.");
	}

	private static async Task EnsureInfraModelAsync
	(
		string url,
		string destinationPath,
		string displayName,
		CancellationToken ct
	)
	{
		if (File.Exists(destinationPath))
		{
			return;
		}

		await Console.Error.WriteLineAsync($"Downloading {displayName} infrastructure model...").ConfigureAwait(false);
		await ModelDownloader.DownloadFileAsync(Http, url, destinationPath, ct).ConfigureAwait(false);

		var sizeMb = new FileInfo(destinationPath).Length / 1024 / 1024;
		await Console.Error.WriteLineAsync
		(
			$"Downloaded {displayName} ({sizeMb} MB)."
		).ConfigureAwait(false);
	}

	private static HttpClient CreateHttpClient()
	{
		var http = new HttpClient();
		http.DefaultRequestHeaders.UserAgent.ParseAdd("Talktastic");
		return http;
	}

	private static bool LooksLikeLocalPath(string query)
	{
		return query.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
			|| query.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
			|| Path.IsPathRooted(query)
			|| query.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase);
	}

	private static string[] ReadRegistryLines(string registryPath)
	{
		return File.Exists(registryPath)
			? File.ReadAllLines(registryPath)
			: [];
	}

	private static InferenceSession CreateSession(string modelPath)
	{
		using var options = new SessionOptions();
		options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
		return new InferenceSession(modelPath, options);
	}

	private static int GetTargetSampleRate(InferenceSession session)
	{
		if
		(
			session.ModelMetadata.CustomMetadataMap is { } metadata
			&& metadata.TryGetValue("config", out var configJson)
		)
		{
			try
			{
				using var document = JsonDocument.Parse(configJson);
				if (document.RootElement.ValueKind == JsonValueKind.Array)
				{
					var last = document.RootElement.EnumerateArray().LastOrDefault();
					if (last.ValueKind == JsonValueKind.Number && last.TryGetInt32(out var sampleRate))
					{
						return sampleRate;
					}
				}
			}
			catch (JsonException)
			{
			}

			var matches = ConfigNumberPattern().Matches(configJson);
			if (matches.Count > 0 && int.TryParse(matches[^1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
			{
				return parsed;
			}
		}

		return DefaultTargetSampleRate;
	}

	[GeneratedRegex(@"-?\d+")]
	private static partial Regex ConfigNumberPattern();

	private static (List<int> OptimalTimestamps, float[] InferenceAudio) FindOptimalTimestamps
	(
		float[] filteredAudio,
		float[] analysisPad
	)
	{
		var tPad = InputSampleRate * XPadSeconds;
		var tQuery = InputSampleRate * XQuerySeconds;
		var tCenter = InputSampleRate * XCenterSeconds;
		var tMax = InputSampleRate * XMaxSeconds;

		var optimalTimestamps = new List<int>();

		if (analysisPad.Length > tMax)
		{
			var energy = new float[filteredAudio.Length];
			for (var i = 0; i < Window; i++)
			{
				for (var sampleIndex = 0; sampleIndex < filteredAudio.Length; sampleIndex++)
				{
					energy[sampleIndex] += MathF.Abs(analysisPad[i + sampleIndex]);
				}
			}

			for (var center = tCenter; center < filteredAudio.Length; center += tCenter)
			{
				var start = Math.Max(0, center - tQuery);
				var end = Math.Min(energy.Length, center + tQuery);
				if (end <= start)
				{
					continue;
				}

				var minIndex = start;
				var minValue = energy[start];
				for (var i = start + 1; i < end; i++)
				{
					if (energy[i] < minValue)
					{
						minValue = energy[i];
						minIndex = i;
					}
				}

				optimalTimestamps.Add(minIndex);
			}
		}

		return (optimalTimestamps, AudioDsp.ReflectPad(filteredAudio, tPad));
	}

	private static (float[] Pitchf, long[] Pitch) ExtractF0
	(
		InferenceSession rmvpeSession,
		float[] audioPad,
		float pitchShiftSemitones,
		CancellationToken ct
	)
	{
		ct.ThrowIfCancellationRequested();

		var mel = AudioDsp.ComputeMelSpectrogram(audioPad, center: true);

		var hidden = RunRmvpeHidden(rmvpeSession, mel);
		var cents = DecodeLocalAverageCents(hidden, RmvpeThreshold);
		var pitchf = DecodeF0(cents);

		// Apply pitch shift (in semitones) before quantization
		if (pitchShiftSemitones != 0.0f)
		{
			var scale = MathF.Pow(2.0f, pitchShiftSemitones / 12.0f);
			for (var i = 0; i < pitchf.Length; i++)
			{
				pitchf[i] *= scale;
			}
		}

		var pitch = QuantizePitch(pitchf);

		var pLen = audioPad.Length / Window;
		if (pitchf.Length > pLen)
		{
			Array.Resize(ref pitchf, pLen);
		}

		if (pitch.Length > pLen)
		{
			Array.Resize(ref pitch, pLen);
		}

		return (pitchf, pitch);
	}

	private static float[,] RunRmvpeHidden(InferenceSession session, float[,] mel)
	{
		var melBins = mel.GetLength(0);
		var frameCount = mel.GetLength(1);
		var paddedFrameCount = 32 * (((frameCount - 1) / 32) + 1);

		var input = new DenseTensor<float>([1, melBins, paddedFrameCount]);
		for (var melIndex = 0; melIndex < melBins; melIndex++)
		{
			for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
			{
				input[0, melIndex, frameIndex] = mel[melIndex, frameIndex];
			}
		}

		using var results = session.Run
		(
			[
				NamedOnnxValue.CreateFromTensor(session.InputNames[0], input),
			]
		);

		var outputTensor = results[0].AsTensor<float>();
		var outputFrames = Math.Min(frameCount, outputTensor.Dimensions[1]);
		var outputBins = outputTensor.Dimensions[2];
		var hidden = new float[outputFrames, outputBins];

		for (var frameIndex = 0; frameIndex < outputFrames; frameIndex++)
		{
			for (var binIndex = 0; binIndex < outputBins; binIndex++)
			{
				hidden[frameIndex, binIndex] = outputTensor[0, frameIndex, binIndex];
			}
		}

		return hidden;
	}

	private static float[] DecodeLocalAverageCents(float[,] salience, float threshold)
	{
		var frames = salience.GetLength(0);
		var bins = salience.GetLength(1);
		var cents = new float[frames];

		for (var frameIndex = 0; frameIndex < frames; frameIndex++)
		{
			var center = 0;
			var maxValue = salience[frameIndex, 0];
			for (var binIndex = 1; binIndex < bins; binIndex++)
			{
				var value = salience[frameIndex, binIndex];
				if (value > maxValue)
				{
					maxValue = value;
					center = binIndex;
				}
			}

			if (maxValue <= threshold)
			{
				continue;
			}

			center += 4;
			float productSum = 0.0f;
			float weightSum = 0.0f;

			for (var offset = -4; offset <= 4; offset++)
			{
				var sourceIndex = center + offset - 4;
				if ((uint)sourceIndex >= (uint)bins)
				{
					continue;
				}

				var weight = salience[frameIndex, sourceIndex];
				var mapping = CentsMapping[center + offset];
				productSum += weight * mapping;
				weightSum += weight;
			}

			if (weightSum > 0.0f)
			{
				cents[frameIndex] = productSum / weightSum;
			}
		}

		return cents;
	}

	private static float[] DecodeF0(float[] cents)
	{
		var f0 = new float[cents.Length];

		for (var i = 0; i < cents.Length; i++)
		{
			if (cents[i] == 0.0f)
			{
				f0[i] = 0.0f;
				continue;
			}

			var hz = 10.0f * MathF.Pow(2.0f, cents[i] / 1200.0f);
			f0[i] = hz == 10.0f ? 0.0f : hz;
		}

		return f0;
	}

	private static long[] QuantizePitch(float[] pitchf)
	{
		const float f0Min = 50.0f;
		const float f0Max = 1100.0f;

		var f0MelMin = 1127.0f * MathF.Log(1.0f + (f0Min / 700.0f));
		var f0MelMax = 1127.0f * MathF.Log(1.0f + (f0Max / 700.0f));
		var pitch = new long[pitchf.Length];

		for (var i = 0; i < pitchf.Length; i++)
		{
			var f0Mel = 1127.0f * MathF.Log(1.0f + (pitchf[i] / 700.0f));
			if (f0Mel > 0.0f)
			{
				f0Mel = ((f0Mel - f0MelMin) * 254.0f / (f0MelMax - f0MelMin)) + 1.0f;
			}

			f0Mel = Math.Clamp(f0Mel, 1.0f, 255.0f);
			pitch[i] = (long)MathF.Round(f0Mel);
		}

		return pitch;
	}

	private static List<float[]> InferSegments
	(
		InferenceSession rvcSession,
		InferenceSession vecSession,
		float[] audioPad,
		long[] pitch,
		float[] pitchf,
		List<int> optTs,
		int targetSampleRate,
		CancellationToken ct
	)
	{
		var tPad = InputSampleRate * XPadSeconds;
		var tPad2 = tPad * 2;
		var results = new List<float[]>();
		var segmentStart = 0;

		foreach (var timestamp in optTs)
		{
			ct.ThrowIfCancellationRequested();

			var alignedTimestamp = (timestamp / Window) * Window;
			var startWin = segmentStart / Window;
			var endWin = Math.Min(pitch.Length, (alignedTimestamp + tPad2) / Window);
			var audioEnd = Math.Min(audioPad.Length, alignedTimestamp + tPad2 + Window);

			var audioSlice = audioPad[segmentStart..audioEnd];
			var pitchSlice = pitch[startWin..endWin];
			var pitchfSlice = pitchf[startWin..endWin];

			results.Add
			(
				RunSegment(rvcSession, vecSession, audioSlice, pitchSlice, pitchfSlice, targetSampleRate, ct)
			);

			segmentStart = alignedTimestamp;
		}

		if (segmentStart < audioPad.Length)
		{
			ct.ThrowIfCancellationRequested();

			var startWin = Math.Min(pitch.Length, segmentStart / Window);
			results.Add
			(
				RunSegment
				(
					rvcSession,
					vecSession,
					audioPad[segmentStart..],
					pitch[startWin..],
					pitchf[startWin..],
					targetSampleRate,
					ct
				)
			);
		}

		return results;
	}

	private static float[] RunSegment
	(
		InferenceSession rvcSession,
		InferenceSession vecSession,
		float[] audioSegment,
		long[] pitchSegment,
		float[] pitchfSegment,
		int targetSampleRate,
		CancellationToken ct
	)
	{
		ct.ThrowIfCancellationRequested();

		var features = RunContentVec(vecSession, audioSegment);

		// ContentVec runs at half the RVC frame rate — double the frames first
		var doubledFrames = features.GetLength(0) * 2;
		var audioFrames = audioSegment.Length / Window;
		var pLen = Math.Min(doubledFrames, audioFrames);

		// Also clip to available pitch data
		pLen = Math.Min(pLen, Math.Min(pitchSegment.Length, pitchfSegment.Length));

		if (pLen <= 0)
		{
			return [];
		}

		var pitchSlice = pitchSegment[..pLen];
		var pitchfSlice = pitchfSegment[..pLen];

		// Slice features to pLen/2 (source frames before doubling)
		var srcFrames = (pLen + 1) / 2;
		if (srcFrames > features.GetLength(0))
		{
			srcFrames = features.GetLength(0);
		}
		var featureSlice = SliceFeatures(features, srcFrames);

		ApplyProtect(featureSlice, pitchfSlice);

		var (phoneData, phoneDims) = CreatePhoneArray(featureSlice, pLen);
		var lengthData = new long[] { pLen };
		var pitchData = new long[pLen];
		var pitchfData = new Float16[pLen];
		for (var i = 0; i < pLen; i++)
		{
			pitchData[i] = pitchSlice[i];
			pitchfData[i] = (Float16)pitchfSlice[i];
		}

		var speakerData = new long[] { SpeakerId };
		var (noiseData, noiseDims) = CreateNoiseArray(pLen);

		{
			using var phoneOrt = OrtValue.CreateTensorValueFromMemory(phoneData, phoneDims);
			using var pitchfOrt = OrtValue.CreateTensorValueFromMemory(pitchfData, new long[] { 1, pLen });
			using var noiseOrt = OrtValue.CreateTensorValueFromMemory(noiseData, noiseDims);

			var inputs = new Dictionary<string, OrtValue>
			{
				[rvcSession.InputNames[0]] = phoneOrt,
				[rvcSession.InputNames[1]] = OrtValue.CreateTensorValueFromMemory(lengthData, new long[] { 1 }),
				[rvcSession.InputNames[2]] = OrtValue.CreateTensorValueFromMemory(pitchData, new long[] { 1, pLen }),
				[rvcSession.InputNames[3]] = pitchfOrt,
				[rvcSession.InputNames[4]] = OrtValue.CreateTensorValueFromMemory(speakerData, new long[] { 1 }),
				[rvcSession.InputNames[5]] = noiseOrt,
			};

			using var runOptions = new RunOptions();
			using var results = rvcSession.Run
			(
				runOptions,
				inputs,
				rvcSession.OutputNames
			);

			// Read output -- OrtValue API returns raw data
			var outputValue = results[0];
			var outputSpan = outputValue.GetTensorDataAsSpan<Float16>();
			var rawAudio = new float[outputSpan.Length];
			for (var i = 0; i < outputSpan.Length; i++)
			{
				rawAudio[i] = outputSpan[i].ToFloat();
			}

			var rmsMatched = MatchRms(audioSegment, InputSampleRate, rawAudio, targetSampleRate);
			var normalized = NormalizePeak(rmsMatched, 0.99f);

			var tPadTgt = targetSampleRate * XPadSeconds;
			if (normalized.Length <= tPadTgt * 2)
			{
				return normalized;
			}

			return normalized[tPadTgt..^tPadTgt];
		}
	}

	private static float[,] RunContentVec(InferenceSession session, float[] audio)
	{
		var input = new DenseTensor<float>([1, 1, audio.Length]);
		for (var i = 0; i < audio.Length; i++)
		{
			input[0, 0, i] = audio[i];
		}

		using var results = session.Run
		(
			[
				NamedOnnxValue.CreateFromTensor(session.InputNames[0], input),
			]
		);

		// ContentVec output from ORT C# is [batch, time, channels=768]
		var outputTensor = results[0].AsTensor<float>();
		var frames = outputTensor.Dimensions[1];
		var channels = outputTensor.Dimensions[2];
		var features = new float[frames, channels];

		for (var frameIndex = 0; frameIndex < frames; frameIndex++)
		{
			for (var channelIndex = 0; channelIndex < channels; channelIndex++)
			{
				features[frameIndex, channelIndex] = outputTensor[0, frameIndex, channelIndex];
			}
		}

		return features;
	}

	private static float[,] SliceFeatures(float[,] features, int frameCount)
	{
		var channels = features.GetLength(1);
		var sliced = new float[frameCount, channels];

		for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
		{
			for (var channelIndex = 0; channelIndex < channels; channelIndex++)
			{
				sliced[frameIndex, channelIndex] = features[frameIndex, channelIndex];
			}
		}

		return sliced;
	}

	private static void ApplyProtect(float[,] features, float[] pitchf)
	{
		var frames = Math.Min(features.GetLength(0), pitchf.Length);
		var channels = features.GetLength(1);
		for (var frameIndex = 0; frameIndex < frames; frameIndex++)
		{
			var blend = pitchf[frameIndex] > 0.0f ? 1.0f : Protect;
			if (blend >= 1.0f)
			{
				continue;
			}

			for (var channelIndex = 0; channelIndex < channels; channelIndex++)
			{
				features[frameIndex, channelIndex] *= blend;
			}
		}
	}

	/// <summary>
	/// Creates a flat Float16 array + dimensions for the phone (features) input tensor.
	/// ContentVec operates at half the RVC frame rate, so each feature frame is repeated twice.
	/// Output layout: [1, targetFrames, channels] matching the model's [1, seq_len, 768] input.
	/// </summary>
	private static (Float16[] Data, long[] Dimensions) CreatePhoneArray
	(
		float[,] features,
		int targetFrames
	)
	{
		var srcFrames = features.GetLength(0);
		var channels = features.GetLength(1);
		var outputFrames = Math.Min(srcFrames * 2, targetFrames);
		var data = new Float16[outputFrames * channels];

		for (var f = 0; f < outputFrames; f++)
		{
			var srcF = f / 2;
			for (var c = 0; c < channels; c++)
			{
				data[(f * channels) + c] = (Float16)features[srcF, c];
			}
		}
		return (data, [1, outputFrames, channels]);
	}

#pragma warning disable CA5394
	private static (Float16[] Data, long[] Dimensions) CreateNoiseArray(int frameCount)
	{
		var random = new Random();
		var data = new Float16[1 * NoiseChannels * frameCount];
		for (var channel = 0; channel < NoiseChannels; channel++)
		{
			for (var frame = 0; frame < frameCount; frame += 2)
			{
				var u1 = Math.Max(random.NextDouble(), double.Epsilon);
				var u2 = random.NextDouble();
				var radius = Math.Sqrt(-2.0 * Math.Log(u1));
				var theta = 2.0 * Math.PI * u2;
				var z0 = radius * Math.Cos(theta);
				var z1 = radius * Math.Sin(theta);

				data[(channel * frameCount) + frame] = (Float16)(float)z0;
				if (frame + 1 < frameCount)
				{
					data[(channel * frameCount) + frame + 1] = (Float16)(float)z1;
				}
			}
		}

		return (data, [1, NoiseChannels, frameCount]);
	}
#pragma warning restore CA5394

	private static float[] MatchRms
	(
		float[] sourceAudio,
		int sourceSampleRate,
		float[] outputAudio,
		int outputSampleRate
	)
	{
		var sourceHop = sourceSampleRate / 2;
		var outputHop = outputSampleRate / 2;

		var sourceRms = AudioDsp.ComputeRms(sourceAudio, sourceHop * 2, sourceHop);
		var outputRms = AudioDsp.ComputeRms(outputAudio, outputHop * 2, outputHop);
		var sourceCurve = AudioDsp.InterpolateLinear(sourceRms, outputAudio.Length);
		var outputCurve = AudioDsp.InterpolateLinear(outputRms, outputAudio.Length);
		var mixed = new float[outputAudio.Length];

		for (var i = 0; i < outputAudio.Length; i++)
		{
			var safeOutput = MathF.Max(outputCurve[i], 1.0e-6f);
			var gain = MathF.Pow(sourceCurve[i], 1.0f - RmsMixRate) * MathF.Pow(safeOutput, RmsMixRate - 1.0f);
			mixed[i] = outputAudio[i] * gain;
		}

		return mixed;
	}

	private static float[] Concatenate(List<float[]> segments)
	{
		var totalLength = 0;
		foreach (var segment in segments)
		{
			totalLength += segment.Length;
		}

		var combined = new float[totalLength];
		var offset = 0;
		foreach (var segment in segments)
		{
			segment.CopyTo(combined, offset);
			offset += segment.Length;
		}

		return combined;
	}

	/// <summary>
	/// Prevents clipping by scaling down if peak exceeds targetPeak.
	/// Does NOT scale up — matches Python RVC behavior where quiet signals are left as-is.
	/// </summary>
	private static float[] NormalizePeak(float[] samples, float targetPeak)
	{
		var max = 0.0f;
		for (var i = 0; i < samples.Length; i++)
		{
			var sample = MathF.Abs(samples[i]);
			if (sample > max)
			{
				max = sample;
			}
		}

		if (max <= targetPeak)
		{
			return samples;
		}

		var scale = targetPeak / max;
		var normalized = new float[samples.Length];
		for (var i = 0; i < samples.Length; i++)
		{
			normalized[i] = samples[i] * scale;
		}

		return normalized;
	}

	private static float[] CreateCentsMapping()
	{
		var mapping = new float[368];
		for (var i = 0; i < 360; i++)
		{
			mapping[i + 4] = (20.0f * i) + 1997.3794f;
		}

		return mapping;
	}
}
