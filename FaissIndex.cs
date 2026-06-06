namespace Talktastic;

/// <summary>
/// Reads a FAISS IVF Flat index file and performs brute-force
/// nearest-neighbor retrieval. Only supports the "IwFl" (IndexIVFFlat)
/// format with "ilar" (ArrayInvertedLists) — the standard format
/// produced by RVC model training.
/// </summary>
static partial class FaissIndex
{
	/// <summary>
	/// The result of loading a FAISS index: a dense matrix of stored
	/// feature vectors ready for nearest-neighbor search.
	/// </summary>
	internal sealed class Index
	{
		/// <summary>All stored vectors, row-major [ntotal × d].</summary>
		public required float[] Vectors { get; init; }

		/// <summary>Vector dimensionality.</summary>
		public required int Dimension { get; init; }

		/// <summary>Total number of stored vectors.</summary>
		public int Count => Vectors.Length / Dimension;
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
		br.ReadInt64(); // nprobe (we do brute-force, don't need this)

		// ── Quantizer (IndexFlat) — skip centroids, we don't need them ──
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

		// READXBVECTOR: size stored as count/4, actual bytes = count * 4
		var xbCount = br.ReadInt64();
		var centroidBytes = xbCount * 4;
		SkipBytes(br, centroidBytes);

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

		// ── Read all vectors from posting lists ──
		var vectors = new float[totalVectors * d];
		var writePos = 0;

		for (var i = 0; i < szCount; i++)
		{
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

		return new Index
		{
			Vectors = vectors,
			Dimension = d,
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

		var n = index.Count;
		var vectors = index.Vectors;
		var result = new float[frameCount * d];
		var actualK = Math.Min(k, n);

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

					ComputeL2Distances(queries, qOffset, vectors, d, n, distances);
					FindTopK(distances, actualK, topKIdx, topKDist);

					// Inverse-squared-distance weighting
					Span<float> weights = stackalloc float[actualK];
					var weightSum = 0f;
					for (var ki = 0; ki < actualK; ki++)
					{
						var dist = topKDist[ki];
						var w = dist > 1e-10f ? 1f / (dist * dist) : 1e10f;
						weights[ki] = w;
						weightSum += w;
					}

					if (weightSum > 0f)
					{
						for (var ki = 0; ki < actualK; ki++)
						{
							weights[ki] /= weightSum;
						}
					}

					// Weighted blend of retrieved vectors
					for (var ki = 0; ki < actualK; ki++)
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
			);

		return result;
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
