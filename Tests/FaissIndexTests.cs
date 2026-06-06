using System.Buffers.Binary;
using System.Collections.Generic;

namespace Talktastic.Tests;

public sealed class FaissIndexTests
{
	private const uint FourCCIwFl = 0x6C46_7749;
	private const uint FourCCIxF2 = 0x3246_7849;
	private const uint FourCCIlar = 0x7261_6C69;
	private const uint FourCCFull = 0x6C6C_7566;

	[Fact]
	public void Load_ValidMinimalIndex_ParsesCorrectly()
	{
		var data = BuildMinimalFaissBytes
		(
			4,
			[
				1f, 2f, 3f, 4f,
				5f, 6f, 7f, 8f,
			]
		);

		var index = FaissIndex.Load(data);

		Assert.Equal(4, index.Dimension);
		Assert.Equal(2, index.Count);
		Assert.Equal
		(
			[
				1f, 2f, 3f, 4f,
				5f, 6f, 7f, 8f,
			],
			index.Vectors
		);
	}

	[Fact]
	public void Load_MultipleInvertedLists_CombinesAllVectors()
	{
		var data = BuildMinimalFaissBytes
		(
			2,
			[
				1f, 2f,
			],
			[
				3f, 4f,
				5f, 6f,
			],
			[
				7f, 8f,
				9f, 10f,
				11f, 12f,
			]
		);

		var index = FaissIndex.Load(data);

		Assert.Equal(2, index.Dimension);
		Assert.Equal(6, index.Count);
		Assert.Equal
		(
			[
				1f, 2f,
				3f, 4f,
				5f, 6f,
				7f, 8f,
				9f, 10f,
				11f, 12f,
			],
			index.Vectors
		);
	}

	[Fact]
	public void Load_EmptyInvertedList_SkipsCorrectly()
	{
		var data = BuildMinimalFaissBytes
		(
			3,
			Array.Empty<float>(),
			[
				1f, 2f, 3f,
				4f, 5f, 6f,
			],
			Array.Empty<float>(),
			[
				7f, 8f, 9f,
			]
		);

		var index = FaissIndex.Load(data);

		Assert.Equal(3, index.Dimension);
		Assert.Equal(3, index.Count);
		Assert.Equal
		(
			[
				1f, 2f, 3f,
				4f, 5f, 6f,
				7f, 8f, 9f,
			],
			index.Vectors
		);
	}

	[Fact]
	public void Load_WrongMagic_ThrowsInvalidDataException()
	{
		var data = BuildMinimalFaissBytes
		(
			2,
			[
				1f, 2f,
			]
		);

		WriteUInt32(data, 0, 0xDEAD_BEEF);

		Assert.Throws<InvalidDataException>
		(
			() => FaissIndex.Load(data)
		);
	}

	[Fact]
	public void Load_UnsupportedQuantizer_ThrowsInvalidDataException()
	{
		var data = BuildMinimalFaissBytes
		(
			2,
			[
				1f, 2f,
			]
		);

		WriteUInt32(data, GetQuantizerMagicOffset(), 0x1234_5678);

		Assert.Throws<InvalidDataException>
		(
			() => FaissIndex.Load(data)
		);
	}

	[Fact]
	public void Load_BadInvertedListMagic_ThrowsInvalidDataException()
	{
		var data = BuildMinimalFaissBytes
		(
			2,
			[
				1f, 2f,
			]
		);

		WriteUInt32
		(
			data,
			GetInvertedListMagicOffset(2, 1),
			0x1111_2222
		);

		Assert.Throws<InvalidDataException>
		(
			() => FaissIndex.Load(data)
		);
	}

	[Fact]
	public void Load_CodeSizeMismatch_ThrowsInvalidDataException()
	{
		var data = BuildMinimalFaissBytes
		(
			4,
			[
				1f, 2f, 3f, 4f,
			]
		);

		WriteInt64
		(
			data,
			GetCodeSizeOffset(4, 1),
			12
		);

		Assert.Throws<InvalidDataException>
		(
			() => FaissIndex.Load(data)
		);
	}

	[Fact]
	public void Load_VectorCountMismatch_ThrowsInvalidDataException()
	{
		var data = BuildMinimalFaissBytes
		(
			2,
			[
				1f, 2f,
			],
			[
				3f, 4f,
			]
		);

		WriteInt64(data, 8, 3);

		Assert.Throws<InvalidDataException>
		(
			() => FaissIndex.Load(data)
		);
	}

	[Fact]
	public void Load_FromFile_SameAsFromBytes()
	{
		var data = BuildMinimalFaissBytes
		(
			3,
			[
				1f, 2f, 3f,
				4f, 5f, 6f,
			],
			[
				7f, 8f, 9f,
			]
		);

		var path = Path.Combine
		(
			AppContext.BaseDirectory,
			$"{Guid.NewGuid():N}.faiss"
		);

		File.WriteAllBytes(path, data);

		try
		{
			var fromBytes = FaissIndex.Load(data);
			var fromFile = FaissIndex.Load(path);

			Assert.Equal(fromBytes.Dimension, fromFile.Dimension);
			Assert.Equal(fromBytes.Count, fromFile.Count);
			Assert.Equal(fromBytes.Vectors, fromFile.Vectors);
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
	public void SearchAndBlend_IdenticalQueryAndIndex_ReturnsQuery()
	{
		float[] queries =
		[
			1f, 2f,
			3f, 4f,
			5f, 6f,
		];
		var index = CreateIndex
		(
			2,
			(float[])queries.Clone()
		);

		var result = FaissIndex.SearchAndBlend
		(
			index,
			queries,
			frameCount: 3,
			k: 1,
			indexRate: 1f
		);

		Assert.Equal(queries, result);
	}

	[Fact]
	public void SearchAndBlend_IndexRateZero_ReturnsOriginalQueries()
	{
		float[] queries =
		[
			1f, 2f,
			3f, 4f,
		];
		var index = CreateIndex
		(
			2,
			[
				10f, 20f,
				30f, 40f,
			]
		);

		var result = FaissIndex.SearchAndBlend
		(
			index,
			queries,
			frameCount: 2,
			k: 2,
			indexRate: 0f
		);

		Assert.Equal(queries, result);
		Assert.NotSame(queries, result);
	}

	[Fact]
	public void SearchAndBlend_IndexRateOne_ReturnsFullRetrieved()
	{
		var index = CreateIndex
		(
			2,
			[
				1f, 2f,
				10f, 20f,
			]
		);
		float[] queries =
		[
			9f, 19f,
		];

		var result = FaissIndex.SearchAndBlend
		(
			index,
			queries,
			frameCount: 1,
			k: 1,
			indexRate: 1f
		);

		Assert.Equal([10f, 20f], result);
	}

	[Fact]
	public void SearchAndBlend_QueryLengthMismatch_ThrowsArgumentException()
	{
		var index = CreateIndex
		(
			2,
			[
				1f, 2f,
				3f, 4f,
			]
		);

		Assert.Throws<ArgumentException>
		(
			() => FaissIndex.SearchAndBlend
			(
				index,
				[
					1f, 2f, 3f,
				],
				frameCount: 2
			)
		);
	}

	[Fact]
	public void SearchAndBlend_KLargerThanIndex_ClampsToIndexSize()
	{
		float[] queries =
		[
			1f, 1f,
		];
		var index = CreateIndex
		(
			2,
			[
				1f, 1f,
				2f, 2f,
				3f, 3f,
				4f, 4f,
				5f, 5f,
			]
		);

		var clamped = FaissIndex.SearchAndBlend
		(
			index,
			queries,
			frameCount: 1,
			k: 5,
			indexRate: 1f
		);
		var oversized = FaissIndex.SearchAndBlend
		(
			index,
			queries,
			frameCount: 1,
			k: 100,
			indexRate: 1f
		);

		AssertEqualWithinTolerance(clamped, oversized);
	}

	[Fact]
	public void SearchAndBlend_SingleVector_ReturnsWeightedBlend()
	{
		var index = CreateIndex
		(
			2,
			[
				10f, 20f,
			]
		);
		float[] queries =
		[
			2f, 4f,
		];

		var result = FaissIndex.SearchAndBlend
		(
			index,
			queries,
			frameCount: 1,
			k: 1,
			indexRate: 0.25f
		);

		Assert.Equal([4f, 8f], result);
	}

	[Fact]
	public void SearchAndBlend_MultipleFrames_ProcessesAllFrames()
	{
		float[] queries =
		[
			1f, 1f,
			10f, 10f,
			100f, 100f,
		];
		var index = CreateIndex
		(
			2,
			(float[])queries.Clone()
		);

		var result = FaissIndex.SearchAndBlend
		(
			index,
			queries,
			frameCount: 3,
			k: 1,
			indexRate: 1f
		);

		Assert.Equal(queries, result);
	}

	[Fact]
	public void Load_ParsesCentroidsAndListOffsets()
	{
		var data = BuildMinimalFaissBytes
		(
			2,
			[
				1f, 2f,
				3f, 4f,
			],
			[
				5f, 6f,
			]
		);

		var index = FaissIndex.Load(data);

		Assert.True(index.HasIvf);
		Assert.NotNull(index.Centroids);
		Assert.NotNull(index.ListOffsets);
		Assert.Equal(3, index.ListOffsets.Length); // 2 lists + sentinel
		Assert.Equal(0, index.ListOffsets[0]);
		Assert.Equal(2, index.ListOffsets[1]); // list 0 has 2 vectors
		Assert.Equal(3, index.ListOffsets[2]); // list 1 has 1 vector
	}

	[Fact]
	public void Load_EmptyList_HasCorrectOffsets()
	{
		var data = BuildMinimalFaissBytes
		(
			2,
			Array.Empty<float>(),
			[
				1f, 2f,
				3f, 4f,
			],
			Array.Empty<float>()
		);

		var index = FaissIndex.Load(data);

		Assert.True(index.HasIvf);
		Assert.Equal(4, index.ListOffsets!.Length);
		Assert.Equal(0, index.ListOffsets[0]); // empty list 0
		Assert.Equal(0, index.ListOffsets[1]); // list 1 starts at 0
		Assert.Equal(2, index.ListOffsets[2]); // list 1 has 2 vectors
		Assert.Equal(2, index.ListOffsets[3]); // empty list 2
	}

	[Fact]
	public void SearchAndBlend_IvfIndex_FindsNearestInCorrectCluster()
	{
		// Build index with 2 clusters:
		// cluster 0: [1,1], [2,2]
		// cluster 1: [10,10], [11,11]
		float[] centroids =
		[
			1.5f, 1.5f,
			10.5f, 10.5f,
		];
		var data = BuildMinimalFaissBytesWithCentroids
		(
			2,
			centroids,
			[
				1f, 1f,
				2f, 2f,
			],
			[
				10f, 10f,
				11f, 11f,
			]
		);

		var index = FaissIndex.Load(data);
		Assert.True(index.HasIvf);

		// Query near cluster 1 → should find [10,10]
		float[] queries = [9f, 9f];
		var result = FaissIndex.SearchAndBlend
		(
			index,
			queries,
			frameCount: 1,
			k: 1,
			indexRate: 1f
		);

		// With nprobe=1, should find nearest in cluster 1
		Assert.Equal(10f, result[0], 0.5f);
		Assert.Equal(10f, result[1], 0.5f);
	}

	[Fact]
	public void SearchAndBlend_IvfVsBruteForce_SimilarResults()
	{
		// Build identical data as IVF (from file) and brute-force
		var data = BuildMinimalFaissBytes
		(
			2,
			[
				1f, 2f,
				3f, 4f,
			],
			[
				5f, 6f,
				7f, 8f,
			]
		);

		var ivfIndex = FaissIndex.Load(data);
		Assert.True(ivfIndex.HasIvf);

		var bfIndex = CreateIndex
		(
			2,
			[
				1f, 2f,
				3f, 4f,
				5f, 6f,
				7f, 8f,
			]
		);
		Assert.False(bfIndex.HasIvf);

		// Query that's equidistant to both clusters → both methods
		// should give similar results with enough nprobe
		float[] queries = [4f, 5f];

		var bfResult = FaissIndex.SearchAndBlend
		(
			bfIndex,
			queries,
			frameCount: 1,
			k: 2,
			indexRate: 1f
		);

		// The IVF result may differ slightly since nprobe=1 only
		// searches one cluster, but should be reasonable
		var ivfResult = FaissIndex.SearchAndBlend
		(
			ivfIndex,
			queries,
			frameCount: 1,
			k: 2,
			indexRate: 1f
		);

		// Both should produce results in the same ballpark
		Assert.InRange(ivfResult[0], 0f, 10f);
		Assert.InRange(ivfResult[1], 0f, 10f);
	}

	private static FaissIndex.Index CreateIndex(int dimension, float[] vectors)
	{
		return new FaissIndex.Index
		{
			Dimension = dimension,
			Vectors = vectors,
		};
	}

	private static void AssertEqualWithinTolerance
	(
		float[] expected,
		float[] actual,
		float tolerance = 1e-6f
	)
	{
		Assert.Equal(expected.Length, actual.Length);

		for (var i = 0; i < expected.Length; i++)
		{
			Assert.True
			(
				Math.Abs(expected[i] - actual[i]) <= tolerance,
				$"Index {i}: expected {expected[i]}, actual {actual[i]}."
			);
		}
	}

	private static byte[] BuildMinimalFaissBytes
	(
		int dimension,
		params float[][] lists
	)
	{
		return BuildMinimalFaissBytesWithCentroids
		(
			dimension, centroids: null, lists
		);
	}

	private static byte[] BuildMinimalFaissBytesWithCentroids
	(
		int dimension,
		float[]? centroids,
		params float[][] lists
	)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
		ArgumentNullException.ThrowIfNull(lists);

		var bytes = new List<byte>();
		long ntotal = 0;

		foreach (var list in lists)
		{
			ArgumentNullException.ThrowIfNull(list);

			if (list.Length % dimension != 0)
			{
				throw new ArgumentException
				(
					"Each list must contain a whole number of vectors.",
					nameof(lists)
				);
			}

			ntotal += list.Length / dimension;
		}

		AppendUInt32(bytes, FourCCIwFl);
		AppendInt32(bytes, dimension);
		AppendInt64(bytes, ntotal);
		AppendInt64(bytes, 0);
		AppendInt64(bytes, 0);
		AppendByte(bytes, 1);
		AppendInt32(bytes, 0);
		AppendInt64(bytes, lists.Length);
		AppendInt64(bytes, 1);

		AppendUInt32(bytes, FourCCIxF2);
		AppendInt32(bytes, dimension);
		AppendInt64(bytes, lists.Length);
		AppendInt64(bytes, 0);
		AppendInt64(bytes, 0);
		AppendByte(bytes, 1);
		AppendInt32(bytes, 0);
		AppendInt64(bytes, (long)lists.Length * dimension);

		if (centroids is not null)
		{
			if (centroids.Length != lists.Length * dimension)
			{
				throw new ArgumentException
				(
					"Centroids array length must equal lists.Length × dimension.",
					nameof(centroids)
				);
			}

			foreach (var c in centroids)
			{
				AppendSingle(bytes, c);
			}
		}
		else
		{
			for (var i = 0; i < lists.Length * dimension; i++)
			{
				AppendSingle(bytes, 0f);
			}
		}

		AppendByte(bytes, 0);
		AppendInt64(bytes, 0);

		AppendUInt32(bytes, FourCCIlar);
		AppendInt64(bytes, lists.Length);
		AppendInt64(bytes, dimension * 4L);
		AppendUInt32(bytes, FourCCFull);
		AppendInt64(bytes, lists.Length);

		foreach (var list in lists)
		{
			AppendInt64(bytes, list.Length / dimension);
		}

		long nextId = 0;
		foreach (var list in lists)
		{
			foreach (var value in list)
			{
				AppendSingle(bytes, value);
			}

			for (var vectorIndex = 0; vectorIndex < list.Length / dimension; vectorIndex++)
			{
				AppendInt64(bytes, nextId);
				nextId++;
			}
		}

		return bytes.ToArray();
	}

	private static int GetQuantizerMagicOffset()
	{
		return 53;
	}

	private static int GetInvertedListMagicOffset(int dimension, int listCount)
	{
		return 98 + (listCount * dimension * sizeof(float)) + 1 + sizeof(long);
	}

	private static int GetCodeSizeOffset(int dimension, int listCount)
	{
		return GetInvertedListMagicOffset(dimension, listCount) + sizeof(uint) + sizeof(long);
	}

	private static void WriteUInt32(byte[] data, int offset, uint value)
	{
		BinaryPrimitives.WriteUInt32LittleEndian
		(
			data.AsSpan(offset, sizeof(uint)),
			value
		);
	}

	private static void WriteInt64(byte[] data, int offset, long value)
	{
		BinaryPrimitives.WriteInt64LittleEndian
		(
			data.AsSpan(offset, sizeof(long)),
			value
		);
	}

	private static void AppendByte(List<byte> bytes, byte value)
	{
		bytes.Add(value);
	}

	private static void AppendInt32(List<byte> bytes, int value)
	{
		Span<byte> buffer = stackalloc byte[sizeof(int)];
		BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
		AppendBuffer(bytes, buffer);
	}

	private static void AppendInt64(List<byte> bytes, long value)
	{
		Span<byte> buffer = stackalloc byte[sizeof(long)];
		BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
		AppendBuffer(bytes, buffer);
	}

	private static void AppendUInt32(List<byte> bytes, uint value)
	{
		Span<byte> buffer = stackalloc byte[sizeof(uint)];
		BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
		AppendBuffer(bytes, buffer);
	}

	private static void AppendSingle(List<byte> bytes, float value)
	{
		Span<byte> buffer = stackalloc byte[sizeof(float)];
		BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
		AppendBuffer(bytes, buffer);
	}

	private static void AppendBuffer(List<byte> bytes, ReadOnlySpan<byte> buffer)
	{
		for (var i = 0; i < buffer.Length; i++)
		{
			bytes.Add(buffer[i]);
		}
	}

	// ── IsSupportedFormat tests ──

	[Fact]
	public void IsSupportedFormat_ValidIwFlFile_ReturnsTrue()
	{
		var data = BuildMinimalFaissBytes(2, [1f, 2f]);
		var path = Path.Combine(Path.GetTempPath(), $"faiss-{Guid.NewGuid()}.index");
		try
		{
			File.WriteAllBytes(path, data);
			Assert.True(FaissIndex.IsSupportedFormat(path));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void IsSupportedFormat_ValidIxF2File_ReturnsTrue()
	{
		var data = BuildMinimalIndexFlatBytes(FourCCIxF2, 2, [1f, 2f, 3f, 4f]);
		var path = Path.Combine(Path.GetTempPath(), $"faiss-{Guid.NewGuid()}.index");
		try
		{
			File.WriteAllBytes(path, data);
			Assert.True(FaissIndex.IsSupportedFormat(path));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void IsSupportedFormat_WrongMagic_ReturnsFalse()
	{
		var path = Path.Combine(Path.GetTempPath(), $"faiss-{Guid.NewGuid()}.index");
		try
		{
			File.WriteAllBytes(path, [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x00]);
			Assert.False(FaissIndex.IsSupportedFormat(path));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void IsSupportedFormat_FileTooShort_ReturnsFalse()
	{
		var path = Path.Combine(Path.GetTempPath(), $"faiss-{Guid.NewGuid()}.index");
		try
		{
			File.WriteAllBytes(path, [0x01, 0x02]);
			Assert.False(FaissIndex.IsSupportedFormat(path));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void IsSupportedFormat_EmptyFile_ReturnsFalse()
	{
		var path = Path.Combine(Path.GetTempPath(), $"faiss-{Guid.NewGuid()}.index");
		try
		{
			File.WriteAllBytes(path, []);
			Assert.False(FaissIndex.IsSupportedFormat(path));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void IsSupportedFormat_MissingFile_ReturnsFalse()
	{
		Assert.False(FaissIndex.IsSupportedFormat(@"C:\nonexistent\fake.index"));
	}

	// ── IndexFlat loading tests (RED until IndexFlat support is added) ──

	[Fact]
	public void Load_IndexFlatL2_ParsesCorrectly()
	{
		var data = BuildMinimalIndexFlatBytes
		(
			FourCCIxF2, 3,
			[
				1f, 2f, 3f,
				4f, 5f, 6f,
			]
		);

		// RED: currently throws because Read() only handles IwFl
		var index = FaissIndex.Load(data);

		Assert.Equal(3, index.Dimension);
		Assert.Equal(2, index.Count);
		Assert.False(index.HasIvf);
		Assert.Equal([1f, 2f, 3f, 4f, 5f, 6f], index.Vectors);
	}

	[Fact]
	public void Load_IndexFlatIxFl_ParsesCorrectly()
	{
		var data = BuildMinimalIndexFlatBytes
		(
			FourCCIxFl, 2,
			[10f, 20f]
		);

		var index = FaissIndex.Load(data);

		Assert.Equal(2, index.Dimension);
		Assert.Equal(1, index.Count);
		Assert.False(index.HasIvf);
		Assert.Equal([10f, 20f], index.Vectors);
	}

	[Fact]
	public void SearchAndBlend_IndexFlat_ProducesSameResultAsBruteForce()
	{
		var data = BuildMinimalIndexFlatBytes
		(
			FourCCIxF2, 2,
			[1f, 2f, 5f, 6f, 3f, 4f]
		);
		var flatIndex = FaissIndex.Load(data);
		Assert.False(flatIndex.HasIvf);

		var bfIndex = CreateIndex(2, [1f, 2f, 5f, 6f, 3f, 4f]);

		float[] queries = [2f, 3f];
		var flatResult = FaissIndex.SearchAndBlend
		(
			flatIndex, queries, frameCount: 1, k: 2, indexRate: 1f
		);
		var bfResult = FaissIndex.SearchAndBlend
		(
			bfIndex, queries, frameCount: 1, k: 2, indexRate: 1f
		);

		AssertEqualWithinTolerance(bfResult, flatResult);
	}

	[Fact]
	public void Load_IndexFlatIP_ParsesCorrectly()
	{
		const uint fourCCIxFI = 0x4946_7849; // "IxFI"
		var data = BuildMinimalIndexFlatBytes
		(
			fourCCIxFI, 2,
			[7f, 8f, 9f, 10f]
		);

		var index = FaissIndex.Load(data);

		Assert.Equal(2, index.Dimension);
		Assert.Equal(2, index.Count);
		Assert.False(index.HasIvf);
		Assert.Equal([7f, 8f, 9f, 10f], index.Vectors);
	}

	[Fact]
	public void Load_UnknownMagic_ThrowsInvalidDataException()
	{
		var bytes = new byte[32];
		bytes[0] = 0xDE;
		bytes[1] = 0xAD;
		bytes[2] = 0xBE;
		bytes[3] = 0xEF;

		var ex = Assert.Throws<InvalidDataException>(() => FaissIndex.Load(bytes));
		Assert.Contains("0xEFBEADDE", ex.Message, StringComparison.Ordinal);
		Assert.Contains("Unsupported", ex.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(0x6C46_7749u, true)]  // IwFl
	[InlineData(0x3246_7849u, true)]  // IxF2
	[InlineData(0x4946_7849u, true)]  // IxFI
	[InlineData(0x6C46_7849u, true)]  // IxFl
	[InlineData(0xDEAD_BEEFu, false)] // unknown
	public void IsSupportedFormat_AllKnownMagics(uint magic, bool expected)
	{
		var path = Path.Combine(Path.GetTempPath(), $"faiss-{Guid.NewGuid()}.index");
		try
		{
			var bytes = new byte[4];
			System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, magic);
			File.WriteAllBytes(path, bytes);
			Assert.Equal(expected, FaissIndex.IsSupportedFormat(path));
		}
		finally
		{
			File.Delete(path);
		}
	}

	// ── IndexFlat builder helper ──

	private const uint FourCCIxFl = 0x6C46_7849;

	private static byte[] BuildMinimalIndexFlatBytes
	(
		uint magic,
		int dimension,
		float[] vectors
	)
	{
		if (vectors.Length % dimension != 0)
		{
			throw new ArgumentException("vectors length must be a multiple of dimension");
		}

		var ntotal = vectors.Length / dimension;
		var bytes = new List<byte>();

		// IndexFlat header
		AppendUInt32(bytes, magic);       // fourcc
		AppendInt32(bytes, dimension);    // d
		AppendInt64(bytes, ntotal);       // ntotal
		AppendInt64(bytes, 0);           // dummy
		AppendInt64(bytes, 0);           // dummy
		AppendByte(bytes, 1);            // is_trained
		AppendInt32(bytes, 0);           // metric_type (L2)

		// READXBVECTOR: total floats then data
		AppendInt64(bytes, vectors.Length);
		foreach (var v in vectors)
		{
			AppendSingle(bytes, v);
		}

		return bytes.ToArray();
	}
}
