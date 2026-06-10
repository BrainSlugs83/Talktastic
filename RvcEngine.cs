using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Talktastic;

#pragma warning disable CA1814

/// <summary>
/// Provides RVC model discovery, loading, and conversion helpers.
/// </summary>
static partial class RvcEngine
{
	private const string RvcDirName = ".rvc";
	private const string VoicesSubDir = "voices";
	private const string InfraSubDir = "infra";
	private const string UrlMapFileName = "rvcs.json";

	private const string ContentVecUrl = "https://huggingface.co/NaruseMioShirakana/MoeSS-SUBModel/resolve/main/vec-768-layer-12.onnx";
	private const string RmvpeUrl = "https://huggingface.co/lj1995/VoiceConversionWebUI/resolve/main/rmvpe.onnx";

	private const float Protect = 0.33f;
	private const float DefaultIndexRate = 0.75f;
	private const int NoiseChannels = 192;
	private const float RmvpeThreshold = 0.03f;
	private const int DefaultTargetSampleRate = 40000;

	private static readonly HttpClient Http = CreateHttpClient();

	private static readonly float[] CentsMapping = CreateCentsMapping();

	private static string? _resolvedRvcDir;

	/// <summary>
	/// Represents an audio segment and its pitch range.
	/// </summary>
	/// <param name="Audio">The segment audio samples.</param>
	/// <param name="PitchStart">The inclusive pitch start index.</param>
	/// <param name="PitchEnd">The exclusive pitch end index.</param>
	internal readonly record struct SegmentSlice
	(
		float[] Audio,
		int PitchStart,
		int PitchEnd
	);

	/// <summary>
	/// Returns the RVC voices directory path, if it exists.
	/// </summary>
	internal static string? FindVoicesDir()
	{
		return AppPaths.FindExistingDir(Path.Combine(RvcDirName, VoicesSubDir));
	}

	/// <summary>
	/// Returns a display name for a resolved RVC model path.
	/// Uses the parent folder name if the model lives in a subdirectory,
	/// otherwise falls back to the filename without extension.
	/// </summary>
	public static string GetDisplayName(string modelPath)
	{
		var parentDir = Path.GetDirectoryName(modelPath);
		var voicesDir = FindVoicesDir();

		// If the model is inside a subdirectory of the voices dir, use the folder name
		if (parentDir is not null && voicesDir is not null
			&& !string.Equals(Path.GetFullPath(parentDir), Path.GetFullPath(voicesDir), StringComparison.OrdinalIgnoreCase))
		{
			return Path.GetFileName(parentDir);
		}

		return Path.GetFileNameWithoutExtension(modelPath);
	}

	/// <summary>
	/// Enumerates all cached RVC models as (displayName, modelFilePath) pairs.
	/// Searches subdirectories first, then legacy flat files.
	/// </summary>
	internal static IEnumerable<(string Name, string Path)> EnumerateCachedModels(string voicesDir)
	{
		if (!Directory.Exists(voicesDir))
		{
			yield break;
		}

		// Subdirectories (current layout)
		foreach (var dir in Directory.GetDirectories(voicesDir))
		{
			var modelFile = FindModelFileInDir(dir);
			if (modelFile is not null)
			{
				yield return (System.IO.Path.GetFileName(dir), modelFile);
			}
		}

		// Legacy: flat files in voicesDir
		foreach (var file in Directory.GetFiles(voicesDir))
		{
			var ext = System.IO.Path.GetExtension(file);
			if
			(
				(
					string.Equals(ext, ".onnx", StringComparison.OrdinalIgnoreCase)
					&& !file.EndsWith(".cached.onnx", StringComparison.OrdinalIgnoreCase)
				)
				|| string.Equals(ext, ".pth", StringComparison.OrdinalIgnoreCase)
			)
			{
				yield return (System.IO.Path.GetFileNameWithoutExtension(file), file);
			}
		}
	}

	/// <summary>
	/// Describes a cached RVC model's properties.
	/// Single source of truth for model discovery -- used by listing, resolution, and loading.
	/// </summary>
	internal sealed record RvcModelInfo
	(
		string Name,
		string ModelPath,
		string Extension,
		int SizeMb,
		bool HasIndex,
		string? IndexPath
	);

	/// <summary>
	/// Lists downloaded RVC models with validated index detection.
	/// </summary>
	public static List<RvcModelInfo> GetCachedModels()
	{
		var results = new List<RvcModelInfo>();
		foreach (var basePath in AppPaths.SearchBases)
		{
			var voicesDir = Path.Combine(basePath, RvcDirName, VoicesSubDir);

			foreach (var (name, modelPath) in EnumerateCachedModels(voicesDir))
			{
				var ext = Path.GetExtension(modelPath);
				var sizeMb = (int)(new FileInfo(modelPath).Length / 1024 / 1024);
				var indexPath = FindCompanionIndex(modelPath);
				results.Add
				(
					new RvcModelInfo
					(
						Name: name,
						ModelPath: modelPath,
						Extension: ext[1..],
						SizeMb: sizeMb,
						HasIndex: indexPath is not null,
						IndexPath: indexPath
					)
				);
			}

			if (Directory.Exists(voicesDir))
			{
				break;
			}
		}

		return results;
	}

	[ExcludeFromCodeCoverage]
	/// <summary>
	/// Resolves an RVC model query to a local model path.
	/// </summary>
	/// <param name="rvcQuery">The model query.</param>
	/// <param name="ct">The cancellation token.</param>
	/// <returns>The resolved model path and display name.</returns>
	public static async Task<(string Path, string DisplayName)> ResolveRvcModelAsync
	(
		string rvcQuery,
		CancellationToken ct
	)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(rvcQuery);
		ct.ThrowIfCancellationRequested();

		if (File.Exists(rvcQuery))
		{
			var fullPath = Path.GetFullPath(rvcQuery);

			if (ArchiveExtractor.IsArchive(fullPath))
			{
				var extracted = await ExtractLocalArchiveAsync(fullPath, ct).ConfigureAwait(false);
				return (extracted, GetDisplayName(extracted));
			}

			return (fullPath, GetDisplayName(fullPath));
		}

		if (LooksLikeLocalPath(rvcQuery))
		{
			throw new FileNotFoundException($"RVC model not found: '{rvcQuery}'.", rvcQuery);
		}

		var rvcDir = EnsureRvcDirectory();
		var voicesDir = Path.Combine(rvcDir, VoicesSubDir);
		var urlMapPath = Path.Combine(rvcDir, UrlMapFileName);
		var registry = new DownloadRegistry(urlMapPath);
		Directory.CreateDirectory(voicesDir);

		if (ModelDownloader.IsUrl(rvcQuery))
		{
			var cachedEntry = registry.LookupByUrl(rvcQuery);
			var cachedPath = TryFindRegisteredRvcModelPath(voicesDir, cachedEntry);
			if (cachedPath is not null)
			{
				return
				(
					cachedPath,
					cachedEntry?.Names.FirstOrDefault() ?? GetDisplayName(cachedPath)
				);
			}

			var downloader = CreateRvcDownloader();
			var resolved = await ResolveRvcDownloadAsync(downloader, rvcQuery, ct).ConfigureAwait(false);

			foreach (var url in resolved.IntermediateUrls.Append(resolved.FinalUrl))
			{
				cachedEntry = registry.LookupByUrl(url);
				cachedPath = TryFindRegisteredRvcModelPath(voicesDir, cachedEntry);
				if (cachedPath is not null)
				{
					RegisterRvcResource(registry, cachedPath, rvcQuery, resolved);
					return
					(
						cachedPath,
						cachedEntry?.Names.FirstOrDefault() ?? GetDisplayName(cachedPath)
					);
				}
			}

			var existingPath = FindCachedModelExact(voicesDir, GetPreferredRvcModelName(resolved));
			if (existingPath is not null)
			{
				RegisterRvcResource(registry, existingPath, rvcQuery, resolved);
				return (existingPath, GetDisplayName(existingPath));
			}

			await Console.Error.WriteLineAsync
			(
				$"Downloading RVC model '{GetPreferredRvcModelName(resolved)}'..."
			).ConfigureAwait(false);

			var (downloadPath, modelName) = await DownloadRvcModelAsync
			(
				downloader,
				resolved,
				voicesDir,
				CreateRvcDownloadProgress(),
				ct
			).ConfigureAwait(false);

			var size = new FileInfo(downloadPath).Length.ToHumanReadableFileSize(false);
			await Console.Error.WriteLineAsync
			(
				$"Ready: {modelName} ({size})."
			).ConfigureAwait(false);

			RegisterRvcResource(registry, downloadPath, rvcQuery, resolved, modelName);
			return (downloadPath, modelName);
		}

		var namedModel = FindCachedModel(voicesDir, rvcQuery);
		if (namedModel is not null)
		{
				return (namedModel, GetDisplayName(namedModel));
		}

		var urlMapNames = ReadUrlMapModelNames(urlMapPath);

		var bestUrlMapMatch = FuzzyMatcher.FindBestMatch(urlMapNames!, rvcQuery);
		if (!string.IsNullOrEmpty(bestUrlMapMatch))
		{
			var resolvedPath = FindCachedModel(voicesDir, bestUrlMapMatch);
			if (resolvedPath is not null)
			{
				return (resolvedPath, bestUrlMapMatch);
			}
		}

		throw new InvalidOperationException
		(
			$"Unknown RVC model '{rvcQuery}'. Use a friendly name already cached, a local .onnx/.pth path, or a URL."
		);
	}

	[ExcludeFromCodeCoverage]
	/// <summary>
	/// Extracts a local RVC model archive into the cache.
	/// </summary>
	/// <param name="archivePath">The archive file path.</param>
	/// <param name="ct">The cancellation token.</param>
	/// <returns>The extracted model path.</returns>
	private static async Task<string> ExtractLocalArchiveAsync(string archivePath, CancellationToken ct)
	{
		var rvcDir = EnsureRvcDirectory();
		var voicesDir = Path.Combine(rvcDir, VoicesSubDir);
		var urlMapPath = Path.Combine(rvcDir, UrlMapFileName);
		var registry = new DownloadRegistry(urlMapPath);
		Directory.CreateDirectory(voicesDir);

		var cachedPath = TryFindRegisteredRvcModelPath(voicesDir, registry.LookupByUrl(archivePath));
		if (cachedPath is not null)
		{
			return cachedPath;
		}

		await Console.Error.WriteLineAsync
		(
			$"Extracting RVC model from '{Path.GetFileName(archivePath)}'..."
		).ConfigureAwait(false);

		var (modelPath, extractedName) = await ModelDownloader.ExtractArchiveAsync
		(
				archivePath, voicesDir, cancellationToken: ct
		).ConfigureAwait(false);

		RegisterRvcResource
		(
			registry,
			modelPath,
			archivePath,
			resolved: null,
			modelNameOverride: extractedName
		);
		return modelPath;
	}

	/// <summary>
	/// Exact-match lookup for URL map-resolved names. No fuzzy matching.
	/// </summary>
	internal static string? FindCachedModelExact(string voicesDir, string modelName)
	{
		return EnumerateCachedModels(voicesDir)
			.Where(m => m.Name.EqualsIgnoreCase(modelName))
			.Select(m => m.Path)
			.FirstOrDefault();
	}

	/// <summary>
	/// Finds the best cached model path for a model name.
	/// </summary>
	/// <param name="voicesDir">The voices directory.</param>
	/// <param name="modelName">The model name.</param>
	/// <returns>The matching model path, or <c>null</c>.</returns>
	internal static string? FindCachedModel(string voicesDir, string modelName)
	{
		var all = EnumerateCachedModels(voicesDir).ToArray();

		// Exact match first
		var exact = all.FirstOrDefault(m => m.Name.EqualsIgnoreCase(modelName));
		if (exact.Path is not null)
		{
			return exact.Path;
		}

		// Fuzzy match
		var names = all.Select(m => m.Name).ToArray();
		var best = FuzzyMatcher.FindBestMatch(names, modelName);
		if (!string.IsNullOrEmpty(best))
		{
			return all.First(m => m.Name.EqualsIgnoreCase(best)).Path;
		}

		return null;
	}

	/// <summary>
	/// Finds the best model file in a directory.
	/// Priority: native .onnx > .pth > validated .cached.onnx (with .cached.meta).
	/// </summary>
	internal static string? FindModelFileInDir(string dir)
	{
		// 1. Prefer native .onnx (not auto-generated cache)
		var nativeOnnx = Directory.GetFiles(dir, "*.onnx")
			.Where
			(
				f => !f.EndsWith(".cached.onnx", StringComparison.OrdinalIgnoreCase)
			)
			.FirstOrDefault();
		if (nativeOnnx is not null)
		{
			return nativeOnnx;
		}

		// 2. Then .pth (CreatePthSession handles its own caching)
		var pth = Directory.GetFiles(dir, "*.pth").FirstOrDefault();
		if (pth is not null)
		{
			return pth;
		}

		// 3. Last resort: .cached.onnx — only if .cached.meta validates
		var cachedOnnx = Directory.GetFiles(dir, "*.cached.onnx").FirstOrDefault();
		if (cachedOnnx is not null && HasValidCachedMeta(cachedOnnx))
		{
			return cachedOnnx;
		}

		return null;
	}

	/// <summary>
	/// Gets the .cached.meta path for a .cached.onnx file.
	/// </summary>
	internal static string GetCachedMetaPath(string cachedOnnxPath)
	{
		// Can't use Path.ChangeExtension -- it only changes after the last dot
		// "foo.cached.onnx" → need "foo.cached.meta", not "foo.cached.cached.meta"
		const string onnxSuffix = ".cached.onnx";
		const string metaSuffix = ".cached.meta";

		if (cachedOnnxPath.EndsWith(onnxSuffix, StringComparison.OrdinalIgnoreCase))
		{
			return string.Concat
			(
				cachedOnnxPath.AsSpan(0, cachedOnnxPath.Length - onnxSuffix.Length),
				metaSuffix
			);
		}

		return Path.ChangeExtension(cachedOnnxPath, ".meta");
	}

	/// <summary>
	/// Checks whether a .cached.onnx file has a companion .cached.meta
	/// with a valid integer sample rate.
	/// </summary>
	internal static bool HasValidCachedMeta(string cachedOnnxPath)
	{
		var metaPath = GetCachedMetaPath(cachedOnnxPath);
		if (!File.Exists(metaPath))
		{
			return false;
		}

		var text = File.ReadAllText(metaPath).Trim();
		return int.TryParse(text, out var sr) && sr > 0;
	}

	/// <summary>
	/// Reads the target sample rate from a .cached.meta file.
	/// Returns DefaultTargetSampleRate if the meta file is missing or invalid.
	/// </summary>
	internal static int ReadCachedMetaSampleRate(string cachedOnnxPath)
	{
		var metaPath = GetCachedMetaPath(cachedOnnxPath);
		if (File.Exists(metaPath))
		{
			var text = File.ReadAllText(metaPath).Trim();
			if (int.TryParse(text, out var sr) && sr > 0)
			{
				return sr;
			}
		}

		return DefaultTargetSampleRate;
	}

	/// <summary>
	/// Determines whether a path targets a cached ONNX model.
	/// </summary>
	/// <param name="path">The path to inspect.</param>
	/// <returns><c>true</c> if the path targets a cached ONNX model; otherwise, <c>false</c>.</returns>
	internal static bool IsCachedOnnxFile(string path)
	{
		return path.EndsWith(".cached.onnx", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Finds a companion .index file next to the model file.
	/// Only returns the path if the index is a recognized FAISS format.
	/// </summary>
	internal static string? FindCompanionIndex(string modelPath)
	{
		var dir = Path.GetDirectoryName(modelPath);
		if (dir is null || !Directory.Exists(dir))
		{
			return null;
		}

		var indexFile = Directory.GetFiles(dir, "*.index").FirstOrDefault();
		if (indexFile is null || !FaissIndex.IsSupportedFormat(indexFile))
		{
			return null;
		}

		return indexFile;
	}

	[ExcludeFromCodeCoverage]
	/// <summary>
	/// Ensures the required infrastructure models are cached locally.
	/// </summary>
	/// <param name="ct">The cancellation token.</param>
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

	[ExcludeFromCodeCoverage]
	/// <summary>
	/// Converts WAV audio with the specified RVC model.
	/// </summary>
	/// <param name="wavBytes">The source WAV bytes.</param>
	/// <param name="rvcModelPath">The RVC model path.</param>
	/// <param name="pitchShiftSemitones">The pitch shift in semitones.</param>
	/// <param name="onStart">
	/// Optional callback invoked once with the converted output sample rate before the first
	/// segment is produced. Used to open a streaming sink for incremental playback.
	/// </param>
	/// <param name="onSegment">
	/// Optional callback invoked with each converted segment (in order) as it is produced,
	/// enabling playback to begin before the whole clip finishes converting.
	/// </param>
	/// <param name="ct">The cancellation token.</param>
	/// <returns>The converted WAV bytes.</returns>
	public static async Task<byte[]> ConvertAsync
	(
		byte[] wavBytes,
		string rvcModelPath,
		float pitchShiftSemitones,
		Action<int>? onStart,
		Action<float[]>? onSegment,
		CancellationToken ct
	)
	{
		ArgumentNullException.ThrowIfNull(wavBytes);
		ArgumentException.ThrowIfNullOrWhiteSpace(rvcModelPath);

		ct.ThrowIfCancellationRequested();
		var sw = System.Diagnostics.Stopwatch.StartNew();
		await EnsureInfraModelsAsync(ct).ConfigureAwait(false);

		var (samples, sampleRate, channels) = AudioDsp.ParseWavToFloat(wavBytes);
		var mono16k = AudioDsp.ResampleToMono16k(samples, sampleRate, channels);
		var normalized = NormalizePeak(mono16k, 0.95f);
		var filtered = AudioDsp.ButterworthHighPass(normalized);
		var analysisPad = AudioDsp.ReflectPad(filtered, Settings.Rvc.Window / 2);
		var (optTs, inferenceAudio) = FindOptimalTimestamps(filtered, analysisPad);
		var tPrep = sw.ElapsedMilliseconds;

		var infraDir = Path.Combine(EnsureRvcDirectory(), InfraSubDir);
		var vecPath = Path.Combine(infraDir, "vec-768-layer-12.onnx");
		var rmvpePath = Path.Combine(infraDir, "rmvpe.onnx");

		NativeExtractor.EnsureAvailable(DllGroup.OnnxRuntime);

		// Load all 3 ORT sessions in parallel -- they're independent. Sessions come from
		// the process-wide prewarm cache (populated by `RvcEngine.Prewarm`), so when
		// SpeechEngine has called it ahead of TTS, these awaits are essentially free.
		InferenceSession? vecSession = null;
		InferenceSession? rmvpeSession = null;
		InferenceSession? rvcSession = null;
		InferenceSession? cpuRvcSession = null;
		int targetSampleRate = 0;

		// Per-task stopwatches measure how long the inference path WAITED for each session
		// (which is roughly zero when the prewarm fired early enough). Cache-hit logs are
		// emitted by CreatePthSession etc. inside the task body on the first miss.
		long tVecLoad = 0, tRmvpeLoad = 0, tRvcLoad = 0, tCpuRvcLoad = 0, tFaissLoad = 0;
		var vecTask = Task.Run
		(
			async () =>
			{
				var s = System.Diagnostics.Stopwatch.StartNew();
				vecSession = await GetOrLoadInfraSessionTask(vecPath, useGpu: true).ConfigureAwait(false);
				tVecLoad = s.ElapsedMilliseconds;
			},
			ct
		);
		var rmvpeTask = Task.Run
		(
			async () =>
			{
				var s = System.Diagnostics.Stopwatch.StartNew();
				rmvpeSession = await GetOrLoadInfraSessionTask(rmvpePath, useGpu: false).ConfigureAwait(false);
				tRmvpeLoad = s.ElapsedMilliseconds;
			},
			ct
		);
		var rvcTask = Task.Run
		(
			async () =>
			{
				var s = System.Diagnostics.Stopwatch.StartNew();
				(rvcSession, targetSampleRate) = await GetOrLoadRvcSessionTask(rvcModelPath, useGpu: true).ConfigureAwait(false);
				tRvcLoad = s.ElapsedMilliseconds;
			},
			ct
		);

		// Belt-and-suspenders: when running on GPU, ALSO preload a CPU copy of the RVC generator
		// in parallel so we can re-run any single chunk that comes out as corrupt on a
		// memory-constrained GPU. The CPU session is unused on healthy GPU runs; cost is one model
		// load alongside everything else (no end-to-end latency) plus modest extra RAM.
		var cpuRvcTask = !Settings.Cli.NoGpu
			? Task.Run
			(
				async () =>
				{
					var s = System.Diagnostics.Stopwatch.StartNew();
					(cpuRvcSession, _) = await GetOrLoadRvcSessionTask(rvcModelPath, useGpu: false).ConfigureAwait(false);
					tCpuRvcLoad = s.ElapsedMilliseconds;
				},
				ct
			)
			: Task.CompletedTask;

		// Also start FAISS loading in parallel
		FaissIndex.Index? faissIndex = null;
		var indexPath = FindCompanionIndex(rvcModelPath);
		var faissTask = indexPath is not null
			? Task.Run
			(
				async () =>
				{
					var s = System.Diagnostics.Stopwatch.StartNew();
					faissIndex = await GetOrLoadFaissIndexTask(indexPath).ConfigureAwait(false);
					tFaissLoad = s.ElapsedMilliseconds;
				},
				ct
			)
			: Task.CompletedTask;

		await Task.WhenAll(vecTask, rmvpeTask, rvcTask, cpuRvcTask, faissTask).ConfigureAwait(false);
		var tLoad = sw.ElapsedMilliseconds;

		Diagnostics.LogPerf
		(
			string.Create
			(
				CultureInfo.InvariantCulture,
				$"[rvc-load] total={tLoad - tPrep}ms rvc={tRvcLoad}ms vec={tVecLoad}ms "
				+ $"rmvpe={tRmvpeLoad}ms cpu-rvc={tCpuRvcLoad}ms faiss={tFaissLoad}ms"
			)
		);

		try
		{
			if (faissIndex is not null)
			{
				await Console.Error.WriteLineAsync
				(
					$"Using index: {Path.GetFileName(indexPath)} "
					+ $"({faissIndex.Count} vectors, dim={faissIndex.Dimension})"
				).ConfigureAwait(false);
			}
			var segmentSlices = ComputeSegmentSlices(inferenceAudio, optTs, ct);
			var f0Sw = System.Diagnostics.Stopwatch.StartNew();
			var (pitchf, pitch) = await Task.Run
			(
				() =>
				{
					var result = ExtractF0(rmvpeSession!, inferenceAudio, pitchShiftSemitones, ct);
					Diagnostics.LogPerf
					(
						string.Create
						(
							CultureInfo.InvariantCulture,
							$"[vec/f0] f0 done in {f0Sw.ElapsedMilliseconds}ms"
						)
					);
					return result;
				},
				ct
			).ConfigureAwait(false);

			var tFeatures = sw.ElapsedMilliseconds;

			var convertedSegments = new List<float[]>(segmentSlices.Length * 4);
			onStart?.Invoke(targetSampleRate);
			long tFirstSegment = -1;

			var chunkSec = Settings.Cli.EffectiveRvcChunkSeconds;
			var padSec = Settings.Cli.EffectiveRvcPadSeconds;
			var inputSr = Settings.Rvc.InputSampleRate;
			var window = Settings.Rvc.Window;
			var segReflectPadSamples = Settings.Rvc.PadSeconds * inputSr;
			var subPadSamples = (int)Math.Round(padSec * inputSr);
			var subPadTrimSamples = (int)Math.Round(padSec * targetSampleRate);

			var totalSubChunkCount = 0;
			var validationRetryCount = 0;
			var gpuRvcFailed = false;
			for (var li = 0; li < segmentSlices.Length; li++)
			{
				ct.ThrowIfCancellationRequested();
				var lSlice = segmentSlices[li];
				var (pStart, pEnd) = ClampPitchRange(lSlice, pitch, pitchf);

				// Compute the "content" region within the segment's audio, excluding the segment-level
				// reflect-pad (Settings.Rvc.PadSeconds at each side). The very first / last logical
				// segment is bounded by the original input's reflect-pad in inferenceAudio; mid-input
				// segments have neighbor audio on the other side. We always model the content region as
				// the inner [segReflectPadSamples, length - segReflectPadSamples] so sub-chunk slicing
				// is uniform.
				var contentStart = Math.Min(segReflectPadSamples, lSlice.Audio.Length);
				var contentEnd = Math.Max(contentStart, lSlice.Audio.Length - segReflectPadSamples);
				var contentLengthSeconds = (contentEnd - contentStart) / (double)inputSr;

				var streamChunks = PlanStreamChunks(contentLengthSeconds, chunkSec, padSec);
				if (streamChunks.Count == 0)
				{
					continue;
				}

				foreach (var chunk in streamChunks)
				{
					ct.ThrowIfCancellationRequested();
					totalSubChunkCount++;

					// Convert chunk (start, end) seconds into sample offsets within lSlice.Audio
					// (relative to the segment, so we account for contentStart).
					var chunkStartSamples = contentStart + (int)Math.Round(chunk.StartSeconds * inputSr);
					var chunkEndSamples = contentStart + (int)Math.Round(chunk.EndSeconds * inputSr);

					// Frame-align so pitch / feature frames line up cleanly with audio.
					chunkStartSamples = (chunkStartSamples / window) * window;
					chunkEndSamples = (chunkEndSamples / window) * window;

					// Expand by padSec context on each side, clamped to segment bounds. Inside the
					// segment this draws from neighboring chunks; at outer segment edges it draws
					// from the segment-level reflect-pad. Either way the input is real audio.
					var audioStart = Math.Max(0, chunkStartSamples - subPadSamples);
					var audioEnd = Math.Min(lSlice.Audio.Length, chunkEndSamples + subPadSamples);
					if (audioEnd - audioStart < window)
					{
						continue;
					}

					var chunkAudio = lSlice.Audio[audioStart..audioEnd];

					var frameStart = Math.Min(pitchf.Length, (pStart * window + audioStart) / window);
					var frameEnd = Math.Min(pitchf.Length, frameStart + ((audioEnd - audioStart) / window));
					if (frameEnd <= frameStart)
					{
						continue;
					}

					var chunkPitch = pitch[frameStart..frameEnd];
					var chunkPitchf = pitchf[frameStart..frameEnd];

					var (converted, newGpuFailed) = RunChunkValidated
					(
						vecSession!,
						rvcSession!,
						cpuRvcSession,
						faissIndex,
						gpuRvcFailed,
						chunkAudio,
						chunkPitch,
						chunkPitchf,
						targetSampleRate,
						subPadTrimSamples,
						totalSubChunkCount,
						ct
					);
					if (newGpuFailed && !gpuRvcFailed)
					{
						validationRetryCount++;
					}
					gpuRvcFailed = newGpuFailed;

					convertedSegments.Add(converted);
					if (tFirstSegment < 0)
					{
						tFirstSegment = sw.ElapsedMilliseconds;
					}

					onSegment?.Invoke(converted);
				}
			}
			var tInfer = sw.ElapsedMilliseconds;

			var finalSamples = Concatenate(convertedSegments);
			var result = AudioDsp.EncodeWav(finalSamples, targetSampleRate);

			Diagnostics.Log
			(
				string.Create
				(
					CultureInfo.InvariantCulture,
					$"[rvc] output: samples={finalSamples.Length} rms={AudioDsp.Rms(finalSamples):F4} "
					+ $"zcr={AudioDsp.ZeroCrossingRate(finalSamples, targetSampleRate):F0}/s gpu={!Settings.Cli.NoGpu} "
					+ $"retries={validationRetryCount}/{totalSubChunkCount}"
				)
			);

			if (!Settings.Cli.NoGpu && AudioDsp.IsLikelyCorruptOutput(finalSamples, targetSampleRate))
			{
				await Console.Error.WriteLineAsync
				(
					"warning: RVC output looks corrupt even after CPU retry; "
					+ "consider re-running with --no-gpu for a fully reliable CPU conversion."
				).ConfigureAwait(false);
			}

			Diagnostics.LogPerf
			(
				string.Create
				(
					CultureInfo.InvariantCulture,
					$"[perf] prep={tPrep}ms load={tLoad - tPrep}ms "
					+ $"f0={tFeatures - tLoad}ms infer={tInfer - tFeatures}ms "
					+ $"segments={segmentSlices.Length} subChunks={totalSubChunkCount} "
					+ $"retries={validationRetryCount} "
					+ $"firstAudio={tFirstSegment}ms total={sw.ElapsedMilliseconds}ms"
				)
			);

			return result;
		}
		finally
		{
			// Sessions are owned by the process-wide caches (`_rvcSessionCache` /
			// `_infraSessionCache` / `_faissIndexCache`) so subsequent ConvertAsync calls
			// can reuse them. They live for the rest of the process and are reclaimed when
			// the CLI exits. Disposing here would invalidate the cache.
		}
	}

	[ExcludeFromCodeCoverage]
	/// <summary>
	/// Runs ContentVec + RVC for a single streaming sub-chunk and validates the result.
	/// When the GPU RVC output fails the corrupt-output detector (low zero-crossing rate
	/// indicative of a continuous drone instead of speech), the chunk is re-run on the
	/// warm CPU RVC session and the GPU is considered "failed" for the remainder of the
	/// utterance -- matching the one-shot trip behavior of <see cref="_dmlAvailable"/>.
	/// </summary>
	/// <param name="vecSession">The ContentVec session.</param>
	/// <param name="rvcSession">The (typically GPU) RVC session.</param>
	/// <param name="cpuRvcSession">Optional warm CPU RVC session for fallback; null when CPU-only.</param>
	/// <param name="faissIndex">The optional FAISS index.</param>
	/// <param name="alreadyGpuFailed">True when a previous chunk in the same utterance already failed on the GPU.</param>
	/// <param name="chunkAudio">The padded source audio for this chunk.</param>
	/// <param name="chunkPitch">The quantized pitch slice for this chunk.</param>
	/// <param name="chunkPitchf">The continuous pitch slice for this chunk.</param>
	/// <param name="targetSampleRate">The RVC output sample rate.</param>
	/// <param name="trimPadSamples">Padding samples to trim from each end of the output.</param>
	/// <param name="chunkOrdinal">The chunk index (1-based) for diagnostic logging.</param>
	/// <param name="ct">The cancellation token.</param>
	/// <returns>The chunk output samples and the updated GPU-failed flag.</returns>
	private static (float[] Output, bool GpuFailed) RunChunkValidated
	(
		InferenceSession vecSession,
		InferenceSession rvcSession,
		InferenceSession? cpuRvcSession,
		FaissIndex.Index? faissIndex,
		bool alreadyGpuFailed,
		float[] chunkAudio,
		long[] chunkPitch,
		float[] chunkPitchf,
		int targetSampleRate,
		int trimPadSamples,
		int chunkOrdinal,
		CancellationToken ct
	)
	{
		var chunkFeatures = ExtractSegmentFeatures(vecSession, chunkAudio, faissIndex);

		var pinnedToCpu = alreadyGpuFailed && cpuRvcSession is not null;
		var activeSession = pinnedToCpu ? cpuRvcSession! : rvcSession;

		var output = RunRvcInference
		(
			activeSession,
			chunkFeatures,
			chunkPitch,
			chunkPitchf,
			chunkAudio,
			targetSampleRate,
			trimPadSamples,
			ct
		);

		if (pinnedToCpu || cpuRvcSession is null || !AudioDsp.IsLikelyCorruptOutput(output, targetSampleRate))
		{
			return (output, alreadyGpuFailed);
		}

		Diagnostics.Log
		(
			string.Create
			(
				CultureInfo.InvariantCulture,
				$"[rvc] chunk {chunkOrdinal} GPU output looks corrupt "
				+ $"(zcr={AudioDsp.ZeroCrossingRate(output, targetSampleRate):F0}/s); "
				+ $"retrying on CPU and pinning remaining chunks to CPU"
			)
		);

		output = RunRvcInference
		(
			cpuRvcSession,
			chunkFeatures,
			chunkPitch,
			chunkPitchf,
			chunkAudio,
			targetSampleRate,
			trimPadSamples,
			ct
		);

		return (output, true);
	}

	/// <summary>
	/// Determines whether a path targets a PyTorch checkpoint.
	/// </summary>
	/// <param name="path">The path to inspect.</param>
	/// <returns><c>true</c> if the path targets a PyTorch checkpoint; otherwise, <c>false</c>.</returns>
	internal static bool IsPthFile(string path)
	{
		return path.EndsWith(".pth", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Creates an inference session from a PyTorch checkpoint.
	/// </summary>
	/// <param name="pthPath">The checkpoint path.</param>
	/// <returns>The created session and target sample rate.</returns>
	internal static (InferenceSession Session, int TargetSampleRate) CreatePthSession
	(
		string pthPath,
		bool useGpu = true
	)
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();
		var ep = useGpu ? "gpu" : "cpu";

		// Check for cached patched ONNX alongside the .pth file
		var cachedOnnxPath = Path.ChangeExtension(pthPath, ".cached.onnx");
		var cachedMetaPath = Path.ChangeExtension(pthPath, ".cached.meta");

		if (File.Exists(cachedOnnxPath) && File.Exists(cachedMetaPath))
		{
			var pthLastWrite = File.GetLastWriteTimeUtc(pthPath);
			var cacheLastWrite = File.GetLastWriteTimeUtc(cachedOnnxPath);

			if (cacheLastWrite >= pthLastWrite)
			{
				var metaText = File.ReadAllText(cachedMetaPath).Trim();
				if (int.TryParse(metaText, out var cachedSr))
				{
					using var opts = CreateSessionOptions(useGpu);
					var tBeforeSession = sw.ElapsedMilliseconds;
					var cachedSession = new InferenceSession(cachedOnnxPath, opts);
					Diagnostics.LogPerf
					(
						string.Create
						(
							CultureInfo.InvariantCulture,
							$"[pth/{ep}] cache-hit: ort-init={sw.ElapsedMilliseconds - tBeforeSession}ms "
							+ $"(disk+ort, no patch)"
						)
					);
					return (cachedSession, cachedSr);
				}
			}
		}

		var pthModel = PthLoader.Load(pthPath);
		var tPthLoad = sw.ElapsedMilliseconds;

		var srKey = pthModel.SampleRateLabel.TrimEnd('k', 'K') + "k";
		var skeletonBytes = LoadEmbeddedSkeleton(srKey);
		var manifest = LoadEmbeddedManifest(srKey);
		var tSkeleton = sw.ElapsedMilliseconds;

		var nameMap = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var (pthName, _) in pthModel.Weights)
		{
			if (manifest.Initializers.ContainsKey(pthName))
			{
				nameMap[pthName] = pthName;
			}
			else if (manifest.PthToOnnx.TryGetValue(pthName, out var onnxName))
			{
				nameMap[pthName] = onnxName;
			}
		}

		var offsets = OnnxPatcher.FindInitializerOffsets(skeletonBytes);
		OnnxPatcher.PatchWeights(skeletonBytes, offsets, pthModel.Weights, nameMap);
		var tPatch = sw.ElapsedMilliseconds;

		// Cache the patched ONNX to disk for next time
		try
		{
			File.WriteAllBytes(cachedOnnxPath, skeletonBytes);
			File.WriteAllText(cachedMetaPath, pthModel.TargetSampleRate.ToString(CultureInfo.InvariantCulture));
		}
		catch (IOException)
		{
			// Best-effort caching -- don't fail if we can't write
		}
		var tCache = sw.ElapsedMilliseconds;

		using var options = CreateSessionOptions(useGpu);
		var session = new InferenceSession(skeletonBytes, options);
		var tSession = sw.ElapsedMilliseconds;

		Diagnostics.LogPerf
		(
			string.Create
			(
				CultureInfo.InvariantCulture,
				$"[pth/{ep}] cache-miss: pickle={tPthLoad}ms skeleton={tSkeleton - tPthLoad}ms "
				+ $"patch={tPatch - tSkeleton}ms cache-write={tCache - tPatch}ms "
				+ $"ort-init={tSession - tCache}ms total={tSession}ms"
			)
		);

		return (session, pthModel.TargetSampleRate);
	}

	/// <summary>
	/// Loads an embedded ONNX skeleton for a sample rate key.
	/// </summary>
	/// <param name="srKey">The sample rate key.</param>
	/// <returns>The decompressed skeleton bytes.</returns>
	internal static byte[] LoadEmbeddedSkeleton(string srKey)
	{
		var resourceName = $"Talktastic.Rvc.skeleton_v2_{srKey}.onnx.br";
		using var stream = typeof(RvcEngine).Assembly.GetManifestResourceStream(resourceName)
			?? throw new InvalidOperationException
			(
				$"Unsupported RVC architecture: {srKey}. "
				+ $"No embedded skeleton template found."
			);

		using var brotli = new System.IO.Compression.BrotliStream
		(
			stream, System.IO.Compression.CompressionMode.Decompress
		);
		using var ms = new MemoryStream();
		brotli.CopyTo(ms);
		return ms.ToArray();
	}

	/// <summary>
	/// Loads an embedded manifest for a sample rate key.
	/// </summary>
	/// <param name="srKey">The sample rate key.</param>
	/// <returns>The embedded manifest.</returns>
	internal static SkeletonManifest LoadEmbeddedManifest(string srKey)
	{
		var resourceName = $"Talktastic.Rvc.skeleton_v2_{srKey}_manifest.json.br";
		using var stream = typeof(RvcEngine).Assembly.GetManifestResourceStream(resourceName)
			?? throw new InvalidOperationException
			(
				$"Unsupported RVC architecture: {srKey}. "
				+ $"No embedded manifest found."
			);

		using var brotli = new System.IO.Compression.BrotliStream
		(
			stream, System.IO.Compression.CompressionMode.Decompress
		);
		using var reader = new StreamReader(brotli);
		var json = reader.ReadToEnd();

		return JsonSerializer.Deserialize
		(
			json,
			SkeletonManifestJsonContext.Default.SkeletonManifest
		) ?? throw new InvalidDataException($"Failed to parse embedded manifest for {srKey}");
	}

	[ExcludeFromCodeCoverage]
	/// <summary>
	/// Resolves or creates the RVC cache directory.
	/// </summary>
	/// <returns>The RVC cache directory path.</returns>
	private static string EnsureRvcDirectory()
	{
		if (_resolvedRvcDir is not null)
		{
			return _resolvedRvcDir;
		}

		foreach (var basePath in AppPaths.SearchBases)
		{
			var candidate = Path.Combine(basePath, RvcDirName);
			if (Directory.Exists(candidate))
			{
				_resolvedRvcDir = candidate;
				return candidate;
			}
		}

		foreach (var basePath in AppPaths.SearchBases)
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

	[ExcludeFromCodeCoverage]
	/// <summary>
	/// Ensures an infrastructure model exists on disk.
	/// </summary>
	/// <param name="url">The model download URL.</param>
	/// <param name="destinationPath">The destination file path.</param>
	/// <param name="displayName">The display name.</param>
	/// <param name="ct">The cancellation token.</param>
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

	[ExcludeFromCodeCoverage]
	/// <summary>
	/// Creates the shared HTTP client.
	/// </summary>
	/// <returns>The configured HTTP client.</returns>
	private static HttpClient CreateHttpClient()
	{
		var http = new HttpClient();
		http.DefaultRequestHeaders.UserAgent.ParseAdd("Talktastic");
		return http;
	}

	/// <summary>
	/// Determines whether a model query looks like a local path.
	/// </summary>
	/// <param name="query">The model query.</param>
	/// <returns><c>true</c> if the query looks like a local path; otherwise, <c>false</c>.</returns>
	internal static bool LooksLikeLocalPath(string query)
	{
		if (ModelDownloader.IsUrl(query))
		{
			return false;
		}

		return query.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
			|| query.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
			|| Path.IsPathRooted(query)
			|| query.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
			|| query.EndsWith(".pth", StringComparison.OrdinalIgnoreCase);
	}

	[ExcludeFromCodeCoverage]
	/// <summary>
	/// Reads the model names from the URL map file.
	/// </summary>
	/// <param name="urlMapPath">The URL map file path.</param>
	/// <returns>The URL map model names.</returns>
	private static string[] ReadUrlMapModelNames(string urlMapPath)
	{
		return
		[
			.. new DownloadRegistry(urlMapPath).Resources
				.SelectMany(static resource => resource.Names.Prepend(resource.Key))
				.Distinct(StringComparer.OrdinalIgnoreCase)
		];
	}

	private static FileDownloader CreateRvcDownloader()
	{
		return new FileDownloader(Http)
		{
			Resolvers =
			{
				new VoiceModelsComResolver(),
				new GoogleDriveResolver(),
				new HuggingFaceResolver(),
				new GitHubReleaseResolver(),
			},
		};
	}

	[ExcludeFromCodeCoverage]
	private static async Task<ResolvedUrl> ResolveRvcDownloadAsync
	(
		FileDownloader downloader,
		string rvcQuery,
		CancellationToken ct
	)
	{
		try
		{
			if (rvcQuery.EndsWith(".onnx.json", StringComparison.OrdinalIgnoreCase))
			{
				var modelUrl = rvcQuery[..^".json".Length];
				return CreateDirectResolvedUrl(modelUrl, [rvcQuery], [GetModelNameFromDirectUrl(modelUrl)], [rvcQuery]);
			}

			if
			(
				rvcQuery.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
				|| rvcQuery.EndsWith(".pth", StringComparison.OrdinalIgnoreCase)
			)
			{
				IReadOnlyList<string>? companionUrls = rvcQuery.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
					? [rvcQuery + ".json"]
					: null;
				return CreateDirectResolvedUrl
				(
					rvcQuery,
					[],
					[GetModelNameFromDirectUrl(rvcQuery)],
					companionUrls
				);
			}

			if (ArchiveExtractor.IsArchive(rvcQuery))
			{
				return CreateDirectResolvedUrl
				(
					rvcQuery,
					[],
					[GetModelNameFromDirectUrl(rvcQuery)],
					null,
					ResolvedUrlSourceType.Archive
				);
			}

			return await downloader.ResolveAsync(rvcQuery, ct).ConfigureAwait(false);
		}
		catch (HttpRequestException ex)
		{
			throw new InvalidOperationException
			(
				$"Failed to download {rvcQuery}: {ModelDownloader.DescribeNetworkFailure(rvcQuery, ex)}",
				ex
			);
		}
	}

	private static ResolvedUrl CreateDirectResolvedUrl
	(
		string finalUrl,
		IReadOnlyList<string> intermediateUrls,
		IReadOnlyList<string> names,
		IReadOnlyList<string>? companionUrls = null,
		ResolvedUrlSourceType sourceType = ResolvedUrlSourceType.SingleFile
	)
	{
		return new ResolvedUrl
		{
			FinalUrl = finalUrl,
			FileName = Path.GetFileName(new Uri(finalUrl).LocalPath),
			SourceType = sourceType,
			IntermediateUrls = intermediateUrls,
			CompanionUrls = companionUrls,
			Names = names,
		};
	}

	[ExcludeFromCodeCoverage]
	private static async Task<(string ModelPath, string ModelName)> DownloadRvcModelAsync
	(
		FileDownloader downloader,
		ResolvedUrl resolved,
		string voicesDir,
		IProgress<FileDownloadProgress>? progress,
		CancellationToken ct
	)
	{
		var tempDir = Path.Combine(voicesDir, $".download-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);

		try
		{
			await downloader.DownloadAsync(resolved, tempDir, progress, ct).ConfigureAwait(false);
			return CommitDownloadedRvcModel(tempDir, voicesDir, GetPreferredRvcModelName(resolved));
		}
		finally
		{
			try
			{
				if (Directory.Exists(tempDir))
				{
					Directory.Delete(tempDir, recursive: true);
				}
			}
			catch (IOException)
			{
			}
		}
	}

	private static (string ModelPath, string ModelName) CommitDownloadedRvcModel
	(
		string tempDir,
		string voicesDir,
		string preferredName
	)
	{
		var modelFile = Directory.GetFiles(tempDir, "*.onnx", SearchOption.AllDirectories)
			.Where
			(
				static path => !path.EndsWith(".cached.onnx", StringComparison.OrdinalIgnoreCase)
			)
			.FirstOrDefault()
			?? Directory.GetFiles(tempDir, "*.pth", SearchOption.AllDirectories).FirstOrDefault()
			?? throw new InvalidOperationException("No .onnx or .pth model file was downloaded.");
		var fallbackName = Path.GetFileNameWithoutExtension(modelFile);
		var modelName = ModelDownloader.ResolveModelName(modelFile, fallbackName, preferredName);
		var modelDir = Path.Combine(voicesDir, modelName);
		if (Directory.Exists(modelDir))
		{
			Directory.Delete(modelDir, recursive: true);
		}

		var sourceDir = Path.GetDirectoryName(modelFile)
			?? throw new InvalidOperationException("Downloaded model file did not have a parent directory.");
		Directory.Move(sourceDir, modelDir);

		if (Directory.Exists(tempDir))
		{
			foreach (var pattern in new[] { "*.pth", "*.onnx", "*.index", "*.json" })
			{
				foreach (var file in Directory.GetFiles(tempDir, pattern, SearchOption.AllDirectories))
				{
					var destFile = Path.Combine(modelDir, Path.GetFileName(file));
					if (!File.Exists(destFile))
					{
						File.Copy(file, destFile);
					}
				}
			}
		}

		var finalPath = FindModelFileInDir(modelDir)
			?? throw new InvalidOperationException($"No RVC model file could be finalized in '{modelDir}'.");
		return (finalPath, modelName);
	}

	private static string? TryFindRegisteredRvcModelPath(string voicesDir, ResourceEntry? entry)
	{
		if (entry is null)
		{
			return null;
		}

		foreach (var name in entry.Names.Prepend(entry.Key).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			var path = FindCachedModelExact(voicesDir, name);
			if (path is not null)
			{
				return path;
			}
		}

		return null;
	}

	private static string GetPreferredRvcModelName(ResolvedUrl resolved)
	{
		return (resolved.Names.Count > 0 ? resolved.Names[0] : null)
			?? GetModelNameFromDirectUrl(resolved.FinalUrl);
	}

	private static string GetModelNameFromDirectUrl(string url)
	{
		return ModelDownloader.DeriveNameFromDirectUrl(new Uri(url));
	}

	private static void RegisterRvcResource
	(
		DownloadRegistry registry,
		string modelPath,
		string inputUrl,
		ResolvedUrl? resolved,
		string? modelNameOverride = null
	)
	{
		var modelName = modelNameOverride ?? GetDisplayName(modelPath);
		var entry = new ResourceEntry
		{
			Key = modelName,
		};
		entry.Names.Add(modelName);
		entry.Urls.Add(inputUrl);

		if (resolved is not null)
		{
			entry.Names.AddRange(resolved.Names);
			entry.Urls.AddRange(resolved.IntermediateUrls);
			entry.Urls.Add(resolved.FinalUrl);
		}

		registry.Register(entry);
		registry.Save();
	}

	private static Progress<FileDownloadProgress> CreateRvcDownloadProgress()
	{
		return new Progress<FileDownloadProgress>
		(
			p =>
			{
				if (p.Phase == DownloadPhase.Downloading || p.Phase == DownloadPhase.Streaming)
				{
					var pct = p.OverallPercent?.ToString("F0", CultureInfo.InvariantCulture) ?? "?";
					var rate = ((long)p.TransferRate).ToHumanReadableFileSize(false);
					Console.Error.Write($"\rDownloading... {pct}% at {rate}/s");
				}
				else if (p.Phase == DownloadPhase.Complete)
				{
					Console.Error.WriteLine();
				}
			}
		);
	}

	private static bool _dmlAvailable = true;
	private static readonly object _dmlLock = new();

	// Process-wide session caches. When SpeechEngine knows RVC is about to be used, it
	// kicks off Prewarm() which populates these caches in the background while the source
	// TTS (SAPI / Neural / Piper) is still running. ConvertAsync then awaits the cached
	// tasks instead of starting fresh loads -- shaving the load wall time off the critical
	// path. Cached sessions live for the rest of the process; this is fine for a CLI tool
	// (process exits, OS reclaims) and a free ~1.8s win for typical use.
	private static readonly ConcurrentDictionary<(string Path, bool UseGpu), Task<(InferenceSession Session, int SampleRate)>> _rvcSessionCache
		= new();
	private static readonly ConcurrentDictionary<(string Path, bool UseGpu), Task<InferenceSession>> _infraSessionCache
		= new();
	private static readonly ConcurrentDictionary<string, Task<FaissIndex.Index>> _faissIndexCache
		= new(StringComparer.Ordinal);

	/// <summary>
	/// Starts loading all RVC-pipeline sessions (vec / rmvpe / rvc / cpu-rvc backup) and the
	/// FAISS index for <paramref name="rvcModelPath"/> in the background, so they are ready
	/// (or close to it) by the time <see cref="ConvertAsync"/> needs them. Idempotent --
	/// repeated calls with the same path return immediately and reuse the in-flight task.
	/// Call this as soon as the RVC model path is known and BEFORE the source TTS work
	/// starts, so the ~1.8s load happens in parallel with synthesis instead of after it.
	/// </summary>
	/// <param name="rvcModelPath">The local RVC model path.</param>
	[ExcludeFromCodeCoverage]
	public static void Prewarm(string rvcModelPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(rvcModelPath);

		// Extract bundled ONNX Runtime DLLs synchronously so background session loads
		// resolve against OUR onnxruntime.dll (1.24.x) instead of racing with whatever
		// sherpa-onnx (used by Piper) loads later from its own bundle (1.17.x). This
		// matches the order ConvertAsync used to do inline.
		NativeExtractor.EnsureAvailable(DllGroup.OnnxRuntime);

		// Kick off the RVC generator session(s). Always preload a CPU backup alongside
		// the GPU session for the corrupt-output retry path (matches what ConvertAsync would do).
		_ = GetOrLoadRvcSessionTask(rvcModelPath, useGpu: true);
		if (!Settings.Cli.NoGpu)
		{
			_ = GetOrLoadRvcSessionTask(rvcModelPath, useGpu: false);
		}

		// FAISS companion (if any) loads independently.
		var indexPath = FindCompanionIndex(rvcModelPath);
		if (indexPath is not null)
		{
			_ = _faissIndexCache.GetOrAdd
			(
				indexPath,
				p => Task.Run(() => FaissIndex.Load(p))
			);
		}

		// Infra (ContentVec + RMVPE) needs EnsureInfraModelsAsync to have downloaded them
		// first. Fire-and-forget; if the files aren't present yet, this just no-ops and
		// ConvertAsync will await EnsureInfraModelsAsync + load itself.
		_ = Task.Run(async () =>
		{
			try
			{
				await EnsureInfraModelsAsync(CancellationToken.None).ConfigureAwait(false);
				PrewarmInfraSessions();
			}
			catch (Exception ex) when
			(
				ex is HttpRequestException
				or IOException
				or OnnxRuntimeException
			)
			{
				// Best-effort prewarm; ConvertAsync will re-attempt and surface any real error.
			}
		});
	}

	[ExcludeFromCodeCoverage]
	private static void PrewarmInfraSessions()
	{
		var infraDir = Path.Combine(EnsureRvcDirectory(), InfraSubDir);
		var vecPath = Path.Combine(infraDir, "vec-768-layer-12.onnx");
		var rmvpePath = Path.Combine(infraDir, "rmvpe.onnx");
		_ = GetOrLoadInfraSessionTask(vecPath, useGpu: true);
		_ = GetOrLoadInfraSessionTask(rmvpePath, useGpu: false);
	}

	[ExcludeFromCodeCoverage]
	private static Task<(InferenceSession Session, int SampleRate)> GetOrLoadRvcSessionTask
	(
		string rvcModelPath,
		bool useGpu
	)
	{
		return _rvcSessionCache.GetOrAdd
		(
			(rvcModelPath, useGpu),
			key => Task.Run(() => LoadRvcSession(key.Path, key.UseGpu))
		);
	}

	[ExcludeFromCodeCoverage]
	private static (InferenceSession Session, int SampleRate) LoadRvcSession(string rvcModelPath, bool useGpu)
	{
		if (IsPthFile(rvcModelPath))
		{
			return CreatePthSession(rvcModelPath, useGpu);
		}

		if (IsCachedOnnxFile(rvcModelPath))
		{
			return (CreateSession(rvcModelPath, useGpu), ReadCachedMetaSampleRate(rvcModelPath));
		}

		var session = CreateSession(rvcModelPath, useGpu);
		return (session, GetTargetSampleRate(session));
	}

	[ExcludeFromCodeCoverage]
	private static Task<InferenceSession> GetOrLoadInfraSessionTask(string modelPath, bool useGpu)
	{
		return _infraSessionCache.GetOrAdd
		(
			(modelPath, useGpu),
			key => Task.Run(() => CreateSession(key.Path, key.UseGpu))
		);
	}

	[ExcludeFromCodeCoverage]
	private static Task<FaissIndex.Index> GetOrLoadFaissIndexTask(string indexPath)
	{
		return _faissIndexCache.GetOrAdd
		(
			indexPath,
			p => Task.Run(() => FaissIndex.Load(p))
		);
	}


	[ExcludeFromCodeCoverage]
	/// <summary>
	/// Creates ONNX Runtime session options.
	/// </summary>
	/// <param name="useGpu">A value indicating whether to prefer GPU execution.</param>
	/// <returns>The session options.</returns>
	private static SessionOptions CreateSessionOptions(bool useGpu = true)
	{
		var options = new SessionOptions();
		options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
		options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;

		if (useGpu && _dmlAvailable && !Settings.Cli.NoGpu)
		{
			try
			{
				options.AppendExecutionProvider_DML(0);
			}
			catch (Exception ex) when
			(
				ex is OnnxRuntimeException
				or EntryPointNotFoundException
				or DllNotFoundException
				or BadImageFormatException
				or TypeLoadException
				or PlatformNotSupportedException
			)
			{
				lock (_dmlLock)
				{
					if (_dmlAvailable)
					{
						_dmlAvailable = false;
						Console.Error.WriteLine
						(
							string.Create
							(
								CultureInfo.InvariantCulture,
								$"DirectML failed ({ex.GetType().Name}: {ex.Message}); falling back to CPU."
							)
						);
					}
				}
			}
		}

		return options;
	}

	[ExcludeFromCodeCoverage]
	/// <summary>
	/// Creates an inference session for a model path.
	/// </summary>
	/// <param name="modelPath">The model file path.</param>
	/// <param name="useGpu">A value indicating whether to prefer GPU execution.</param>
	/// <returns>The created inference session.</returns>
	private static InferenceSession CreateSession(string modelPath, bool useGpu = true)
	{
		using var options = CreateSessionOptions(useGpu);
		return new InferenceSession(modelPath, options);
	}

	[ExcludeFromCodeCoverage]
	/// <summary>
	/// Gets the target sample rate from model metadata.
	/// </summary>
	/// <param name="session">The model session.</param>
	/// <returns>The target sample rate.</returns>
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
	/// <summary>
	/// Gets the regex used to parse numeric config values.
	/// </summary>
	/// <returns>The numeric config regex.</returns>
	private static partial Regex ConfigNumberPattern();

	/// <summary>
	/// Finds optimal segment split points for inference.
	/// </summary>
	/// <param name="filteredAudio">The filtered source audio.</param>
	/// <param name="analysisPad">The padded analysis audio.</param>
	/// <returns>The optimal split timestamps and padded inference audio.</returns>
	internal static (List<int> OptimalTimestamps, float[] InferenceAudio) FindOptimalTimestamps
	(
		float[] filteredAudio,
		float[] analysisPad
	)
	{
		var tPad = Settings.Rvc.InputSampleRate * Settings.Rvc.PadSeconds;
		var tQuery = Settings.Rvc.InputSampleRate * Settings.Rvc.QuerySeconds;
		var tCenter = Settings.Rvc.InputSampleRate * Settings.Rvc.CenterSeconds;
		var tMax = Settings.Rvc.InputSampleRate * Settings.Rvc.MaxSeconds;

		var optimalTimestamps = new List<int>();

		if (analysisPad.Length > tMax)
		{
			var energy = new float[filteredAudio.Length];
			for (var i = 0; i < Settings.Rvc.Window; i++)
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

	[ExcludeFromCodeCoverage]
	/// <summary>
	/// Extracts continuous and quantized pitch tracks.
	/// </summary>
	/// <param name="rmvpeSession">The RMVPE session.</param>
	/// <param name="audioPad">The padded input audio.</param>
	/// <param name="pitchShiftSemitones">The pitch shift in semitones.</param>
	/// <param name="ct">The cancellation token.</param>
	/// <returns>The decoded continuous and quantized pitch tracks.</returns>
	private static (float[] Pitchf, long[] Pitch) ExtractF0
	(
		InferenceSession rmvpeSession,
		float[] audioPad,
		float pitchShiftSemitones,
		CancellationToken ct
	)
	{
		ct.ThrowIfCancellationRequested();

		var sw = System.Diagnostics.Stopwatch.StartNew();
		var mel = AudioDsp.ComputeMelSpectrogram(audioPad, center: true);
		var tMel = sw.ElapsedMilliseconds;

		var hidden = RunRmvpeHidden(rmvpeSession, mel);
		var tRmvpe = sw.ElapsedMilliseconds;

		var cents = DecodeLocalAverageCents(hidden, RmvpeThreshold);
		var pitchf = DecodeF0(cents);
		var tDecode = sw.ElapsedMilliseconds;

		Diagnostics.LogPerf
		(
			string.Create
			(
				CultureInfo.InvariantCulture,
				$"[f0] mel={tMel}ms rmvpe={tRmvpe - tMel}ms decode={tDecode - tRmvpe}ms"
			)
		);

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

		var pLen = audioPad.Length / Settings.Rvc.Window;
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

	[ExcludeFromCodeCoverage]
	/// <summary>
	/// Runs RMVPE and returns the hidden salience matrix.
	/// </summary>
	/// <param name="session">The RMVPE session.</param>
	/// <param name="mel">The mel spectrogram.</param>
	/// <returns>The hidden salience matrix.</returns>
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

	/// <summary>
	/// Decodes local-average cents values from salience bins.
	/// </summary>
	/// <param name="salience">The salience matrix.</param>
	/// <param name="threshold">The voiced threshold.</param>
	/// <returns>The decoded cents values.</returns>
	internal static float[] DecodeLocalAverageCents(float[,] salience, float threshold)
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

	/// <summary>
	/// Decodes F0 values from cents values.
	/// </summary>
	/// <param name="cents">The cents values.</param>
	/// <returns>The decoded F0 values.</returns>
	internal static float[] DecodeF0(float[] cents)
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

	/// <summary>
	/// Quantizes continuous pitch values to model bins.
	/// </summary>
	/// <param name="pitchf">The continuous pitch values.</param>
	/// <returns>The quantized pitch values.</returns>
	internal static long[] QuantizePitch(float[] pitchf)
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

	/// <summary>
	/// Computes audio segment slices for inference.
	/// </summary>
	/// <param name="audioPad">The padded audio.</param>
	/// <param name="optTs">The optimal split timestamps.</param>
	/// <param name="ct">The cancellation token.</param>
	/// <returns>The computed segment slices.</returns>
	internal static SegmentSlice[] ComputeSegmentSlices
	(
		float[] audioPad,
		List<int> optTs,
		CancellationToken ct
	)
	{
		var tPad = Settings.Rvc.InputSampleRate * Settings.Rvc.PadSeconds;
		var tPad2 = tPad * 2;
		var totalFrames = audioPad.Length / Settings.Rvc.Window;
		var results = new List<SegmentSlice>(optTs.Count + 1);
		var segmentStart = 0;

		foreach (var timestamp in optTs)
		{
			ct.ThrowIfCancellationRequested();

			var alignedTimestamp = (timestamp / Settings.Rvc.Window) * Settings.Rvc.Window;
			var pitchStart = segmentStart / Settings.Rvc.Window;
			var pitchEnd = Math.Min(totalFrames, (alignedTimestamp + tPad2) / Settings.Rvc.Window);
			var audioEnd = Math.Min(audioPad.Length, alignedTimestamp + tPad2 + Settings.Rvc.Window);

			results.Add(new SegmentSlice(audioPad[segmentStart..audioEnd], pitchStart, pitchEnd));
			segmentStart = alignedTimestamp;
		}

		if (segmentStart < audioPad.Length)
		{
			ct.ThrowIfCancellationRequested();

			var pitchStart = Math.Min(totalFrames, segmentStart / Settings.Rvc.Window);
			results.Add(new SegmentSlice(audioPad[segmentStart..], pitchStart, totalFrames));
		}

		return [.. results];
	}

	/// <summary>
	/// Represents a streaming sub-chunk within a single logical RVC segment, in seconds
	/// relative to the start of the segment.
	/// </summary>
	/// <param name="StartSeconds">The inclusive start time of the chunk's CONTENT region.</param>
	/// <param name="EndSeconds">The exclusive end time of the chunk's CONTENT region.</param>
	internal readonly record struct StreamChunk(double StartSeconds, double EndSeconds)
	{
		/// <summary>Gets the length of the content region in seconds.</summary>
		internal double LengthSeconds => EndSeconds - StartSeconds;
	}

	/// <summary>
	/// Plans how to subdivide an input of <paramref name="totalSeconds"/> seconds into
	/// streaming RVC chunks of approximately <paramref name="chunkSeconds"/> seconds each,
	/// with an absorb-tail rule that prevents tiny trailing chunks: if the remaining
	/// audio is less than two minimum-size inferences worth (i.e.
	/// <c>2 * (chunkSeconds + padSeconds)</c>), the remainder is folded into the previous
	/// chunk instead of being emitted as a separate short chunk. The padding is reported
	/// for documentation; this function does not slice the audio itself.
	/// </summary>
	/// <param name="totalSeconds">The total input duration in seconds.</param>
	/// <param name="chunkSeconds">The target content length per chunk; must be positive.</param>
	/// <param name="padSeconds">The per-side context padding; must be non-negative.</param>
	/// <returns>The ordered list of (start, end) chunks covering <c>[0, totalSeconds)</c>.</returns>
	/// <exception cref="ArgumentOutOfRangeException">
	/// Thrown when <paramref name="chunkSeconds"/> is not positive or
	/// <paramref name="padSeconds"/> is negative.
	/// </exception>
	internal static List<StreamChunk> PlanStreamChunks
	(
		double totalSeconds,
		double chunkSeconds,
		double padSeconds
	)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSeconds);
		ArgumentOutOfRangeException.ThrowIfNegative(padSeconds);

		var chunks = new List<StreamChunk>();
		if (totalSeconds <= 0.0)
		{
			return chunks;
		}

		var absorbThreshold = (2.0 * chunkSeconds) + (2.0 * padSeconds);
		var pos = 0.0;

		while (pos < totalSeconds)
		{
			var remaining = totalSeconds - pos;
			if (remaining < absorbThreshold)
			{
				chunks.Add(new StreamChunk(pos, totalSeconds));
				break;
			}

			chunks.Add(new StreamChunk(pos, pos + chunkSeconds));
			pos += chunkSeconds;
		}

		return chunks;
	}

	/// <summary>
	/// Clamps a segment pitch range to available pitch data.
	/// </summary>
	/// <param name="segmentSlice">The segment slice.</param>
	/// <param name="pitch">The quantized pitch values.</param>
	/// <param name="pitchf">The continuous pitch values.</param>
	/// <returns>The clamped pitch range.</returns>
	internal static (int PitchStart, int PitchEnd) ClampPitchRange
	(
		SegmentSlice segmentSlice,
		long[] pitch,
		float[] pitchf
	)
	{
		var pitchEnd = Math.Min(segmentSlice.PitchEnd, Math.Min(pitch.Length, pitchf.Length));
		var pitchStart = Math.Min(segmentSlice.PitchStart, pitchEnd);
		return (pitchStart, pitchEnd);
	}

	[ExcludeFromCodeCoverage]
	/// <summary>
	/// Extracts features for an audio segment.
	/// </summary>
	/// <param name="vecSession">The ContentVec session.</param>
	/// <param name="audioSegment">The audio segment.</param>
	/// <param name="faissIndex">The optional FAISS index.</param>
	/// <returns>The extracted feature matrix.</returns>
	private static float[,] ExtractSegmentFeatures
	(
		InferenceSession vecSession,
		float[] audioSegment,
		FaissIndex.Index? faissIndex
	)
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();
		var features = RunContentVec(vecSession, audioSegment);
		var tVec = sw.ElapsedMilliseconds;

		if (faissIndex is not null && faissIndex.Dimension == features.GetLength(1))
		{
			features = ApplyFaissRetrieval(features, faissIndex);
		}
		var tFaiss = sw.ElapsedMilliseconds;

		Diagnostics.LogPerf
		(
			string.Create
			(
				CultureInfo.InvariantCulture,
				$"[vec] contentvec={tVec}ms faiss={tFaiss - tVec}ms "
				+ $"audio={audioSegment.Length} samples"
			)
		);

		return features;
	}

	/// <summary>
	/// Runs RVC inference for a prepared segment.
	/// </summary>
	/// <param name="rvcSession">The RVC session.</param>
	/// <param name="features">The segment features.</param>
	/// <param name="pitchSegment">The quantized pitch segment.</param>
	/// <param name="pitchfSegment">The continuous pitch segment.</param>
	/// <param name="audioSegment">The source audio segment (including any context padding).</param>
	/// <param name="targetSampleRate">The target sample rate.</param>
	/// <param name="trimPadSamples">
	/// Number of samples (at <paramref name="targetSampleRate"/>) to discard from each end of
	/// the produced audio, to remove the contribution of the input context padding. Caller
	/// must pass <c>Settings.Rvc.PadSeconds * targetSampleRate</c> for full logical segments,
	/// or <c>Settings.Cli.EffectiveRvcPadSeconds * targetSampleRate</c> for streaming sub-chunks.
	/// </param>
	/// <param name="ct">The cancellation token.</param>
	/// <returns>The converted audio samples with the configured padding trimmed.</returns>
	private static float[] RunRvcInference
	(
		InferenceSession rvcSession,
		float[,] features,
		long[] pitchSegment,
		float[] pitchfSegment,
		float[] audioSegment,
		int targetSampleRate,
		int trimPadSamples,
		CancellationToken ct
	)
	{
		ct.ThrowIfCancellationRequested();

		// ContentVec runs at half the RVC frame rate — double the frames first
		var doubledFrames = features.GetLength(0) * 2;
		var audioFrames = audioSegment.Length / Settings.Rvc.Window;
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

		var speakerData = new long[] { Settings.Rvc.SpeakerId };
		var (noiseData, noiseDims) = CreateNoiseArray(pLen);

		{
			using var inputs = new DisposableValueMap<OrtValue>();
			inputs.Add(rvcSession.InputNames[0], OrtValue.CreateTensorValueFromMemory(phoneData, phoneDims));
			inputs.Add(rvcSession.InputNames[1], OrtValue.CreateTensorValueFromMemory(lengthData, new long[] { 1 }));
			inputs.Add(rvcSession.InputNames[2], OrtValue.CreateTensorValueFromMemory(pitchData, new long[] { 1, pLen }));
			inputs.Add(rvcSession.InputNames[3], OrtValue.CreateTensorValueFromMemory(pitchfData, new long[] { 1, pLen }));
			inputs.Add(rvcSession.InputNames[4], OrtValue.CreateTensorValueFromMemory(speakerData, new long[] { 1 }));
			inputs.Add(rvcSession.InputNames[5], OrtValue.CreateTensorValueFromMemory(noiseData, noiseDims));

			using var runOptions = new RunOptions();
			using var results = rvcSession.Run
			(
				runOptions,
				inputs.Items,
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

			var rmsMatched = MatchRms(audioSegment, Settings.Rvc.InputSampleRate, rawAudio, targetSampleRate);
			var normalized = NormalizePeak(rmsMatched, 0.99f);

			if (trimPadSamples <= 0 || normalized.Length <= trimPadSamples * 2)
			{
				return normalized;
			}

			return normalized[trimPadSamples..^trimPadSamples];
		}
	}

	[ExcludeFromCodeCoverage]
	/// <summary>
	/// Runs ContentVec feature extraction for audio.
	/// </summary>
	/// <param name="session">The ContentVec session.</param>
	/// <param name="audio">The input audio.</param>
	/// <returns>The extracted ContentVec feature matrix.</returns>
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

	/// <summary>
	/// Applies FAISS index-based feature retrieval: for each frame's
	/// ContentVec features, finds k=8 nearest training vectors and
	/// blends them with the original using inverse-squared-distance
	/// weighting and the configured index rate.
	/// </summary>
	private static float[,] ApplyFaissRetrieval
	(
		float[,] features,
		FaissIndex.Index faissIndex
	)
	{
		var frameCount = features.GetLength(0);
		var d = features.GetLength(1);

		// Flatten features to 1D for FaissIndex.SearchAndBlend
		var flat = new float[frameCount * d];
		Buffer.BlockCopy(features, 0, flat, 0, flat.Length * sizeof(float));

		var blended = FaissIndex.SearchAndBlend
		(
			faissIndex,
			flat,
			frameCount,
			k: 8,
			indexRate: DefaultIndexRate
		);

		// Reshape back to [frames, channels]
		var result = new float[frameCount, d];
		Buffer.BlockCopy(blended, 0, result, 0, blended.Length * sizeof(float));
		return result;
	}

	/// <summary>
	/// Slices a feature matrix to the requested frame count.
	/// </summary>
	/// <param name="features">The feature matrix.</param>
	/// <param name="frameCount">The frame count.</param>
	/// <returns>The sliced feature matrix.</returns>
	internal static float[,] SliceFeatures(float[,] features, int frameCount)
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

	/// <summary>
	/// Applies unvoiced protection scaling to features.
	/// </summary>
	/// <param name="features">The feature matrix.</param>
	/// <param name="pitchf">The continuous pitch values.</param>
	internal static void ApplyProtect(float[,] features, float[] pitchf)
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
	internal static (Float16[] Data, long[] Dimensions) CreatePhoneArray
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
	/// <summary>
	/// Creates Gaussian noise input data for the RVC model.
	/// </summary>
	/// <param name="frameCount">The frame count.</param>
	/// <returns>The noise tensor data and dimensions.</returns>
	internal static (Float16[] Data, long[] Dimensions) CreateNoiseArray(int frameCount)
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

	/// <summary>
	/// Matches output RMS to the source audio envelope.
	/// </summary>
	/// <param name="sourceAudio">The source audio.</param>
	/// <param name="sourceSampleRate">The source sample rate.</param>
	/// <param name="outputAudio">The output audio.</param>
	/// <param name="outputSampleRate">The output sample rate.</param>
	/// <returns>The RMS-matched audio.</returns>
	internal static float[] MatchRms
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
			var gain = MathF.Pow(sourceCurve[i], 1.0f - Settings.Rvc.RmsMixRate) * MathF.Pow(safeOutput, Settings.Rvc.RmsMixRate - 1.0f);
			mixed[i] = outputAudio[i] * gain;
		}

		return mixed;
	}

	/// <summary>
	/// Concatenates converted audio segments.
	/// </summary>
	/// <param name="segments">The audio segments.</param>
	/// <returns>The concatenated audio.</returns>
	internal static float[] Concatenate(List<float[]> segments)
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
	internal static float[] NormalizePeak(float[] samples, float targetPeak)
	{
		var max = 0.0f;
		var i = 0;

		// SIMD peak find (max absolute value).
		if (Vector.IsHardwareAccelerated && samples.Length >= Vector<float>.Count)
		{
			var vMax = Vector<float>.Zero;
			var width = Vector<float>.Count;
			for (; i <= samples.Length - width; i += width)
			{
				vMax = Vector.Max(vMax, Vector.Abs(new Vector<float>(samples, i)));
			}

			for (var lane = 0; lane < Vector<float>.Count; lane++)
			{
				if (vMax[lane] > max)
				{
					max = vMax[lane];
				}
			}
		}

		for (; i < samples.Length; i++)
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
		i = 0;

		// SIMD scalar-broadcast multiply.
		if (Vector.IsHardwareAccelerated && samples.Length >= Vector<float>.Count)
		{
			var vScale = new Vector<float>(scale);
			var width = Vector<float>.Count;
			for (; i <= samples.Length - width; i += width)
			{
				(new Vector<float>(samples, i) * vScale).CopyTo(normalized, i);
			}
		}

		for (; i < samples.Length; i++)
		{
			normalized[i] = samples[i] * scale;
		}

		return normalized;
	}

	/// <summary>
	/// Creates the cents lookup table.
	/// </summary>
	/// <returns>The cents mapping table.</returns>
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

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812")]
/// <summary>
/// Represents the embedded ONNX skeleton manifest.
/// </summary>
/// <param name="Initializers">The initializer offsets.</param>
/// <param name="PthToOnnx">The PTH-to-ONNX name map.</param>
internal sealed record SkeletonManifest
(
	Dictionary<string, int[]> Initializers,
	Dictionary<string, string> PthToOnnx
);

[JsonSerializable(typeof(SkeletonManifest))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
/// <summary>
/// Provides JSON serialization metadata for <see cref="SkeletonManifest"/>.
/// </summary>
internal sealed partial class SkeletonManifestJsonContext : JsonSerializerContext
{
}
