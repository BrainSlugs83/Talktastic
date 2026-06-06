namespace Talktastic;

/// <summary>
/// Reads a FAISS IVF Flat index file and performs nearest-neighbor
/// retrieval. When IVF structure is available (centroids + posting lists),
/// uses cluster-based pruning for O(nprobe × cluster_size) search instead
/// of O(ntotal) brute-force. Falls back to brute-force for indices
/// constructed without IVF structure (e.g. in tests).
/// Only supports the "IwFl" (IndexIVFFlat) format with "ilar"
/// (ArrayInvertedLists) — the standard format produced by RVC training.
/// </summary>
static partial class FaissIndex
{
	/// <summary>
	/// The result of loading a FAISS index: a dense matrix of stored
	/// feature vectors ready for nearest-neighbor search.
	/// When IVF structure is present (loaded from file), search uses
	/// cluster-based pruning; otherwise falls back to brute-force.
	/// </summary>
	internal sealed class Index
	{
		/// <summary>All stored vectors, row-major [ntotal × d].</summary>
		public required float[] Vectors { get; init; }

		/// <summary>Vector dimensionality.</summary>
		public required int Dimension { get; init; }

		/// <summary>Total number of stored vectors.</summary>
		public int Count => Vectors.Length / Dimension;

		/// <summary>
		/// IVF centroids, row-major [nlist × d]. Null when the index
		/// was constructed without IVF structure (e.g. in tests).
		/// </summary>
		public float[]? Centroids { get; init; }

		/// <summary>
		/// Cumulative vector offsets per inverted list, length nlist+1.
		/// <c>ListOffsets[i]</c> is the first vector index for list i,
		/// <c>ListOffsets[i+1] - ListOffsets[i]</c> is the list size.
		/// </summary>
		public int[]? ListOffsets { get; init; }

		/// <summary>
		/// Number of centroids to probe during search (default 1).
		/// </summary>
		public int Nprobe { get; init; } = 1;

		/// <summary>Whether this index has IVF structure for fast search.</summary>
		public bool HasIvf => Centroids is not null && ListOffsets is not null;
	}

	// ── FAISS fourcc constants ──

	private const uint FourCC_IwFl = 0x6C46_7749; // "IwFl"
	private const uint FourCC_IxF2 = 0x3246_7849; // "IxF2"
	private const uint FourCC_IxFI = 0x4946_7849; // "IxFI"
	private const uint FourCC_IxFl = 0x6C46_7849; // "IxFl"
	private const uint FourCC_ilar = 0x7261_6C69; // "ilar"

	/// <summary>
	/// Loads a FAISS IVF Flat index from a file path.
	/// Returns all stored vectors as a dense float array for brute-force search.
	/// </summary>
	public static Index Load(string path)
	{
		using var fs = File.OpenRead(path);
		using var br = new BinaryReader(fs);
		return Read(br);
	}

	/// <summary>
	/// Loads a FAISS IVF Flat index from a byte array.
	/// </summary>
	public static Index Load(byte[] data)
	{
		using var ms = new MemoryStream(data, writable: false);
		using var br = new BinaryReader(ms);
		return Read(br);
	}

	private static Index Read(BinaryReader br)
	{
		// ── IVF header ──
		var magic = br.ReadUInt32();
		if (magic != FourCC_IwFl)
		{
			throw new InvalidDataException
			(
				$"Not an IVF Flat index. Expected fourcc 'IwFl' (0x{FourCC_IwFl:X8}), "
				+ $"got 0x{magic:X8}."
			);
		}

		var d = br.ReadInt32();
		var ntotal = br.ReadInt64();
		br.ReadInt64(); // dummy
		br.ReadInt64(); // dummy
		br.ReadByte();  // is_trained
		var metric = br.ReadInt32();

		var nlist = br.ReadInt64();
		var nprobe = br.ReadInt64();

		// ── Quantizer (IndexFlat) — read centroids ──
		var qMagic = br.ReadUInt32();
		if (qMagic != FourCC_IxF2 && qMagic != FourCC_IxFI && qMagic != FourCC_IxFl)
		{
			throw new InvalidDataException
			(
				$"Unsupported quantizer type 0x{qMagic:X8}. "
				+ "Expected IxF2, IxFI, or IxFl."
			);
		}

		br.ReadInt32();  // quantizer d
		br.ReadInt64();  // quantizer ntotal
		br.ReadInt64();  // dummy
		br.ReadInt64();  // dummy
		br.ReadByte();   // is_trained
		br.ReadInt32();  // metric_type

		// READXBVECTOR: count stored as total floats
		var xbCount = br.ReadInt64();
		var centroidFloats = (int)xbCount;
		var centroids = new float[centroidFloats];
		var centroidBytes = centroidFloats * 4;
		var centroidRaw = br.ReadBytes(centroidBytes);
		Buffer.BlockCopy(centroidRaw, 0, centroids, 0, centroidBytes);

		// ── Direct map ──
		var dmType = br.ReadByte();
		var dmSize = br.ReadInt64();
		if (dmType == 1)
		{
			SkipBytes(br, dmSize * 8);
		}
		else if (dmType == 2)
		{
			// Hashtable: pairs of (idx_t, idx_t)
			SkipBytes(br, dmSize * 16);
		}

		// ── Inverted Lists ──
		var ilMagic = br.ReadUInt32();
		if (ilMagic != FourCC_ilar)
		{
			throw new InvalidDataException
			(
				$"Unsupported inverted list format 0x{ilMagic:X8}. "
				+ "Expected 'ilar' (ArrayInvertedLists)."
			);
		}

		var ilNlist = br.ReadInt64();
		var codeSize = br.ReadInt64();

		if (codeSize != d * 4)
		{
			throw new InvalidDataException
			(
				$"Unexpected code_size {codeSize}, expected {d * 4} "
				+ $"(d={d} × sizeof(float))."
			);
		}

		// List sizes
		var listTypeBytes = br.ReadUInt32();
		var szCount = br.ReadInt64();
		var sizes = new long[szCount];
		long totalVectors = 0;
		for (var i = 0; i < szCount; i++)
		{
			sizes[i] = br.ReadInt64();
			totalVectors += sizes[i];
		}

		if (totalVectors != ntotal)
		{
			throw new InvalidDataException
			(
				$"Vector count mismatch: header says {ntotal}, "
				+ $"inverted lists contain {totalVectors}."
			);
		}

		// ── Read all vectors from posting lists, tracking boundaries ──
		var vectors = new float[totalVectors * d];
		var listOffsets = new int[(int)szCount + 1];
		var writePos = 0;

		for (var i = 0; i < szCount; i++)
		{
			listOffsets[i] = writePos / d;
			var count = (int)sizes[i];
			if (count == 0)
			{
				continue;
			}

			// Read codes (float32 vectors)
			var floatCount = count * d;
			var byteCount = floatCount * 4;
			var raw = br.ReadBytes(byteCount);
			Buffer.BlockCopy(raw, 0, vectors, writePos * 4, byteCount);
			writePos += floatCount;

			// Skip IDs (int64 per vector) — we don't need them
			SkipBytes(br, count * 8L);
		}

		listOffsets[(int)szCount] = writePos / d;

		return new Index
		{
			Vectors = vectors,
			Dimension = d,
			Centroids = centroids,
			ListOffsets = listOffsets,
			Nprobe = Math.Max(1, (int)nprobe),
		};
	}

	/// <summary>
	/// Searches for the k nearest neighbors of each query vector using
	/// brute-force L2 distance. Returns blended feature vectors using
	/// inverse-squared-distance weighting.
	/// </summary>
	/// <param name="index">The loaded FAISS index.</param>
	/// <param name="queries">
	/// Query vectors, flat row-major array [frameCount * dimension].
	/// </param>
	/// <param name="frameCount">Number of query frames.</param>
	/// <param name="k">Number of nearest neighbors (default 8).</param>
	/// <param name="indexRate">
	/// Blend ratio: 0.0 = original features only, 1.0 = index features only.
	/// </param>
	/// <returns>
	/// Blended features, flat row-major [frameCount * dimension].
	/// </returns>
	public static float[] SearchAndBlend
	(
		Index index,
		float[] queries,
		int frameCount,
		int k = 8,
		float indexRate = 0.75f
	)
	{
		var d = index.Dimension;

		if (queries.Length != frameCount * d)
		{
			throw new ArgumentException
			(
				$"Query array length {queries.Length} does not match "
				+ $"frameCount({frameCount}) × dimension({d})."
			);
		}

		if (indexRate <= 0f)
		{
			return (float[])queries.Clone();
		}

		var result = new float[frameCount * d];
		var actualK = Math.Min(k, index.Count);

		if (index.HasIvf)
		{
			SearchAndBlendIvf
			(
				index, queries, frameCount, actualK, indexRate, result
			);
		}
		else
		{
			SearchAndBlendBruteForce
			(
				index, queries, frameCount, actualK, indexRate, result
			);
		}

		return result;
	}

	private static void SearchAndBlendIvf
	(
		Index index,
		float[] queries,
		int frameCount,
		int actualK,
		float indexRate,
		float[] result
	)
	{
		var d = index.Dimension;
		var centroids = index.Centroids!;
		var listOffsets = index.ListOffsets!;
		var nlist = listOffsets.Length - 1;
		var nprobe = Math.Min(index.Nprobe, nlist);
		var vectors = index.Vectors;

		Enumerable.Range(0, frameCount)
			.AsParallel()
			.ForAll
			(
				frame =>
				{
					var qOffset = frame * d;
					var rOffset = frame * d;

					// Step 1: find nprobe closest centroids
					var centDist = new float[nlist];
					ComputeL2Distances
					(
						queries, qOffset, centroids, d, nlist, centDist
					);

					var probeIdx = new int[nprobe];
					var probeDist = new float[nprobe];
					FindTopK(centDist, nprobe, probeIdx, probeDist);

					// Step 2: search only vectors in the probed clusters
					var candidateCount = 0;
					for (var p = 0; p < nprobe; p++)
					{
						var listIdx = probeIdx[p];
						candidateCount += listOffsets[listIdx + 1]
							- listOffsets[listIdx];
					}

					var topKIdx = new int[actualK];
					var topKDist = new float[actualK];
					for (var i = 0; i < actualK; i++)
					{
						topKDist[i] = float.MaxValue;
						topKIdx[i] = -1;
					}

					var heapSize = 0;

					for (var p = 0; p < nprobe; p++)
					{
						var listIdx = probeIdx[p];
						var start = listOffsets[listIdx];
						var end = listOffsets[listIdx + 1];

						for (var vi = start; vi < end; vi++)
						{
							var vecOffset = vi * d;
							var dist = ComputeL2Distance
							(
								queries, qOffset, vectors, vecOffset, d
							);

							if (heapSize < actualK)
							{
								topKIdx[heapSize] = vi;
								topKDist[heapSize] = dist;
								heapSize++;
								if (heapSize == actualK)
								{
									// Build max-heap
									for
									(
										var i = actualK / 2 - 1;
										i >= 0;
										i--
									)
									{
										SiftDown
										(
											topKDist, topKIdx, actualK, i
										);
									}
								}
							}
							else if (dist < topKDist[0])
							{
								topKDist[0] = dist;
								topKIdx[0] = vi;
								SiftDown(topKDist, topKIdx, actualK, 0);
							}
						}
					}

					var usedK = Math.Min(actualK, heapSize);
					BlendResults
					(
						queries, qOffset, vectors, d, topKIdx,
						topKDist, usedK, indexRate, result, rOffset
					);
				}
			);
	}

	private static void SearchAndBlendBruteForce
	(
		Index index,
		float[] queries,
		int frameCount,
		int actualK,
		float indexRate,
		float[] result
	)
	{
		var d = index.Dimension;
		var n = index.Count;
		var vectors = index.Vectors;

		// Each frame's search is independent — parallelize with PLINQ
		Enumerable.Range(0, frameCount)
			.AsParallel()
			.ForAll
			(
				frame =>
				{
					var qOffset = frame * d;
					var rOffset = frame * d;

					// Thread-local scratch buffers
					var distances = new float[n];
					var topKIdx = new int[actualK];
					var topKDist = new float[actualK];

					ComputeL2Distances
					(
						queries, qOffset, vectors, d, n, distances
					);
					FindTopK(distances, actualK, topKIdx, topKDist);

					BlendResults
					(
						queries, qOffset, vectors, d, topKIdx,
						topKDist, actualK, indexRate, result, rOffset
					);
				}
			);
	}

	private static void BlendResults
	(
		float[] queries,
		int qOffset,
		float[] vectors,
		int d,
		int[] topKIdx,
		float[] topKDist,
		int usedK,
		float indexRate,
		float[] result,
		int rOffset
	)
	{
		// Inverse-squared-distance weighting
		Span<float> weights = stackalloc float[usedK];
		var weightSum = 0f;
		for (var ki = 0; ki < usedK; ki++)
		{
			var dist = topKDist[ki];
			var w = dist > 1e-10f ? 1f / (dist * dist) : 1e10f;
			weights[ki] = w;
			weightSum += w;
		}

		if (weightSum > 0f)
		{
			for (var ki = 0; ki < usedK; ki++)
			{
				weights[ki] /= weightSum;
			}
		}

		// Weighted blend of retrieved vectors
		for (var ki = 0; ki < usedK; ki++)
		{
			var vecOffset = topKIdx[ki] * d;
			var w = weights[ki];
			for (var j = 0; j < d; j++)
			{
				result[rOffset + j] += w * vectors[vecOffset + j];
			}
		}

		// Blend with original features
		var oneMinusRate = 1f - indexRate;
		for (var j = 0; j < d; j++)
		{
			result[rOffset + j] = indexRate * result[rOffset + j]
				+ oneMinusRate * queries[qOffset + j];
		}
	}

	private static float ComputeL2Distance
	(
		float[] a,
		int aOffset,
		float[] b,
		int bOffset,
		int d
	)
	{
		var sum = 0f;
		var j = 0;

		var simdWidth = System.Numerics.Vector<float>.Count;
		if (d >= simdWidth)
		{
			var vSum = System.Numerics.Vector<float>.Zero;
			for (; j <= d - simdWidth; j += simdWidth)
			{
				var va = new System.Numerics.Vector<float>(a, aOffset + j);
				var vb = new System.Numerics.Vector<float>(b, bOffset + j);
				var diff = va - vb;
				vSum += diff * diff;
			}

			sum = System.Numerics.Vector.Dot
			(
				vSum,
				System.Numerics.Vector<float>.One
			);
		}

		for (; j < d; j++)
		{
			var diff = a[aOffset + j] - b[bOffset + j];
			sum += diff * diff;
		}

		return sum;
	}

	private static void ComputeL2Distances
	(
		float[] queries,
		int queryOffset,
		float[] vectors,
		int d,
		int n,
		float[] distances
	)
	{
		for (var i = 0; i < n; i++)
		{
			var sum = 0f;
			var offset = i * d;
			var j = 0;

			// SIMD path using System.Numerics.Vector<float>
			var simdWidth = System.Numerics.Vector<float>.Count;
			if (d >= simdWidth)
			{
				var vSum = System.Numerics.Vector<float>.Zero;
				for (; j <= d - simdWidth; j += simdWidth)
				{
					var q = new System.Numerics.Vector<float>(
						queries, queryOffset + j);
					var v = new System.Numerics.Vector<float>(
						vectors, offset + j);
					var diff = q - v;
					vSum += diff * diff;
				}

				sum = System.Numerics.Vector.Dot
				(
					vSum,
					System.Numerics.Vector<float>.One
				);
			}

			// Scalar tail
			for (; j < d; j++)
			{
				var diff = queries[queryOffset + j] - vectors[offset + j];
				sum += diff * diff;
			}

			distances[i] = sum;
		}
	}

	/// <summary>
	/// Finds the indices and distances of the k smallest values
	/// using a partial selection (max-heap of size k).
	/// </summary>
	private static void FindTopK
	(
		float[] distances,
		int k,
		int[] topKIdx,
		float[] topKDist
	)
	{
		// Initialize with first k elements
		for (var i = 0; i < k; i++)
		{
			topKIdx[i] = i;
			topKDist[i] = distances[i];
		}

		// Build max-heap
		for (var i = k / 2 - 1; i >= 0; i--)
		{
			SiftDown(topKDist, topKIdx, k, i);
		}

		// Scan remaining elements
		for (var i = k; i < distances.Length; i++)
		{
			if (distances[i] < topKDist[0])
			{
				topKDist[0] = distances[i];
				topKIdx[0] = i;
				SiftDown(topKDist, topKIdx, k, 0);
			}
		}
	}

	private static void SiftDown(float[] dist, int[] idx, int n, int i)
	{
		while (true)
		{
			var largest = i;
			var left = 2 * i + 1;
			var right = 2 * i + 2;

			if (left < n && dist[left] > dist[largest])
			{
				largest = left;
			}

			if (right < n && dist[right] > dist[largest])
			{
				largest = right;
			}

			if (largest == i)
			{
				break;
			}

			(dist[i], dist[largest]) = (dist[largest], dist[i]);
			(idx[i], idx[largest]) = (idx[largest], idx[i]);
			i = largest;
		}
	}

	private static void SkipBytes(BinaryReader br, long count)
	{
		if (br.BaseStream.CanSeek)
		{
			br.BaseStream.Seek(count, SeekOrigin.Current);
		}
		else
		{
			// Fallback for non-seekable streams
			var buf = new byte[Math.Min(count, 8192)];
			var remaining = count;
			while (remaining > 0)
			{
				var toRead = (int)Math.Min(remaining, buf.Length);
				var read = br.Read(buf, 0, toRead);
				if (read == 0)
				{
					break;
				}
				remaining -= read;
			}
		}
	}
}
