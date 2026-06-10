using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Talktastic.Tests;

public sealed class ProgressTrackingReadStreamTests
{
	private static readonly Type ProgressTrackingReadStreamType = typeof(FileDownloader).GetNestedType
	(
		"ProgressTrackingReadStream",
		BindingFlags.NonPublic
	) ?? throw new InvalidOperationException("Could not find ProgressTrackingReadStream.");

	private static readonly Type DownloadPhaseType = typeof(FileDownloader).Assembly.GetType
	(
		"Talktastic.DownloadPhase",
		throwOnError: true
	) ?? throw new InvalidOperationException("Could not find DownloadPhase.");

	[Fact]
	public void BytesTransferred_IncrementedByRead()
	{
		using var inner = new MemoryStream([1, 2, 3, 4, 5]);
		using var stream = CreateProgressTrackingReadStream(inner);
		var buffer = new byte[3];

		var bytesRead = ReadViaArrayOverload(stream, buffer, 0, buffer.Length);

		Assert.Equal(3, bytesRead);
		Assert.Equal(3L, GetBytesTransferred(stream));
	}

	[Fact]
	public void BytesTransferred_IncrementedByReadSpan()
	{
		using var inner = new MemoryStream([1, 2, 3, 4, 5]);
		using var stream = CreateProgressTrackingReadStream(inner);
		Span<byte> buffer = stackalloc byte[4];

		var bytesRead = stream.Read(buffer);

		Assert.Equal(4, bytesRead);
		Assert.Equal(4L, GetBytesTransferred(stream));
	}

	[Fact]
	public async Task BytesTransferred_IncrementedByReadAsync()
	{
		using var inner = new MemoryStream([1, 2, 3, 4, 5]);
		using var stream = CreateProgressTrackingReadStream(inner);
		var buffer = new byte[2];

		var bytesRead = await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(true);

		Assert.Equal(2, bytesRead);
		Assert.Equal(2L, GetBytesTransferred(stream));
	}

	[Fact]
	public async Task BytesTransferred_IncrementedByReadAsyncOldPattern()
	{
		using var inner = new MemoryStream([1, 2, 3, 4, 5]);
		using var stream = CreateProgressTrackingReadStream(inner);
		var buffer = new byte[5];

		var bytesRead = await ReadAsyncViaArrayOverload(stream, buffer, 1, 3, CancellationToken.None).ConfigureAwait(true);

		Assert.Equal(3, bytesRead);
		Assert.Equal(3L, GetBytesTransferred(stream));
	}

	[Fact]
	public void CanRead_DelegatesInnerStream()
	{
		using var unreadable = new TrackingStream(canRead: false);
		using var readable = new TrackingStream(canRead: true);
		using var unreadableWrapper = CreateProgressTrackingReadStream(unreadable, leaveOpen: true);
		using var readableWrapper = CreateProgressTrackingReadStream(readable, leaveOpen: true);

		Assert.False(unreadableWrapper.CanRead);
		Assert.True(readableWrapper.CanRead);
	}

	[Fact]
	public void CanSeek_DelegatesInnerStream()
	{
		using var unseekable = new TrackingStream(canSeek: false);
		using var seekable = new TrackingStream(canSeek: true);
		using var unseekableWrapper = CreateProgressTrackingReadStream(unseekable, leaveOpen: true);
		using var seekableWrapper = CreateProgressTrackingReadStream(seekable, leaveOpen: true);

		Assert.False(unseekableWrapper.CanSeek);
		Assert.True(seekableWrapper.CanSeek);
	}

	[Fact]
	public void CanWrite_AlwaysFalse()
	{
		using var writable = new TrackingStream(canWrite: true);
		using var unwritable = new TrackingStream(canWrite: false);
		using var writableWrapper = CreateProgressTrackingReadStream(writable, leaveOpen: true);
		using var unwritableWrapper = CreateProgressTrackingReadStream(unwritable, leaveOpen: true);

		Assert.False(writableWrapper.CanWrite);
		Assert.False(unwritableWrapper.CanWrite);
	}

	[Fact]
	public void Length_DelegatesInnerStream()
	{
		using var inner = new TrackingStream(length: 42);
		using var stream = CreateProgressTrackingReadStream(inner, leaveOpen: true);

		Assert.Equal(42L, stream.Length);
	}

	[Fact]
	public void Position_GetSet_DelegatesInnerStream()
	{
		using var inner = new TrackingStream(length: 10) { Position = 2 };
		using var stream = CreateProgressTrackingReadStream(inner, leaveOpen: true);

		Assert.Equal(2L, stream.Position);

		stream.Position = 7;

		Assert.Equal(7L, inner.Position);
		Assert.Equal(7L, stream.Position);
	}

	[Fact]
	public void Flush_DelegatesInnerStream()
	{
		using var inner = new TrackingStream();
		using var stream = CreateProgressTrackingReadStream(inner, leaveOpen: true);

		stream.Flush();

		Assert.Equal(1, inner.FlushCallCount);
	}

	[Fact]
	public async Task FlushAsync_DelegatesInnerStream()
	{
		using var inner = new TrackingStream();
		using var stream = CreateProgressTrackingReadStream(inner, leaveOpen: true);

		await stream.FlushAsync(CancellationToken.None).ConfigureAwait(true);

		Assert.Equal(1, inner.FlushAsyncCallCount);
	}

	[Fact]
	public void Seek_DelegatesInnerStream()
	{
		using var inner = new TrackingStream(length: 20) { Position = 4 };
		using var stream = CreateProgressTrackingReadStream(inner, leaveOpen: true);

		var position = stream.Seek(3, SeekOrigin.Current);

		Assert.Equal(7L, position);
		Assert.Equal(1, inner.SeekCallCount);
		Assert.Equal(3L, inner.LastSeekOffset);
		Assert.Equal(SeekOrigin.Current, inner.LastSeekOrigin);
	}

	[Fact]
	public void SetLength_DelegatesInnerStream()
	{
		using var inner = new TrackingStream(length: 4);
		using var stream = CreateProgressTrackingReadStream(inner, leaveOpen: true);

		stream.SetLength(12);

		Assert.Equal(12L, inner.Length);
		Assert.Equal(1, inner.SetLengthCallCount);
		Assert.Equal(12L, inner.LastSetLengthValue);
	}

	[Fact]
	public void Write_ThrowsNotSupported()
	{
		using var inner = new TrackingStream();
		using var stream = CreateProgressTrackingReadStream(inner, leaveOpen: true);

		Assert.Throws<NotSupportedException>
		(
			() => stream.Write([1, 2, 3], 0, 3)
		);
	}

	[Fact]
	public void Write_Span_ThrowsNotSupported()
	{
		using var inner = new TrackingStream();
		using var stream = CreateProgressTrackingReadStream(inner, leaveOpen: true);

		Assert.Throws<NotSupportedException>
		(
			() => stream.Write(new byte[] { 1, 2, 3 }.AsSpan())
		);
	}

	[Fact]
	public async Task WriteAsync_Memory_ThrowsNotSupported()
	{
		using var inner = new TrackingStream();
		using var stream = CreateProgressTrackingReadStream(inner, leaveOpen: true);

		await Assert.ThrowsAsync<NotSupportedException>
		(
			async () => await stream.WriteAsync(new byte[] { 1, 2, 3 }.AsMemory(), CancellationToken.None).ConfigureAwait(true)
		).ConfigureAwait(true);
	}

	[Fact]
	public async Task WriteAsync_Array_ThrowsNotSupported()
	{
		using var inner = new TrackingStream();
		using var stream = CreateProgressTrackingReadStream(inner, leaveOpen: true);

		await Assert.ThrowsAsync<NotSupportedException>
		(
			async () => await WriteAsyncViaArrayOverload(stream, new byte[] { 1, 2, 3 }, 0, 3, CancellationToken.None).ConfigureAwait(true)
		).ConfigureAwait(true);
	}

	[Fact]
	public void Dispose_LeaveOpenTrue_InnerNotDisposed()
	{
		var inner = new TrackingStream();
		var stream = CreateProgressTrackingReadStream(inner, leaveOpen: true);

		stream.Dispose();

		Assert.False(inner.IsDisposed);
	}

	[Fact]
	public void Dispose_LeaveOpenFalse_InnerDisposed()
	{
		var inner = new TrackingStream();
		var stream = CreateProgressTrackingReadStream(inner, leaveOpen: false);

		stream.Dispose();

		Assert.True(inner.IsDisposed);
	}

	[Fact]
	public async Task DisposeAsync_LeaveOpenTrue_InnerNotDisposed()
	{
		var inner = new TrackingStream();
		var stream = CreateProgressTrackingReadStream(inner, leaveOpen: true);

		await stream.DisposeAsync().ConfigureAwait(true);

		Assert.False(inner.IsDisposed);
		Assert.False(inner.IsAsyncDisposed);
	}

	[Fact]
	public async Task DisposeAsync_LeaveOpenFalse_InnerDisposed()
	{
		var inner = new TrackingStream();
		var stream = CreateProgressTrackingReadStream(inner, leaveOpen: false);

		await stream.DisposeAsync().ConfigureAwait(true);

		Assert.True(inner.IsDisposed);
		Assert.True(inner.IsAsyncDisposed);
	}

	[Fact]
	public void Progress_NullProgress_DoesNotThrow()
	{
		using var inner = new MemoryStream([1, 2, 3]);
		using var stream = CreateProgressTrackingReadStream(inner, progress: null);
		var buffer = new byte[2];

		var bytesRead = ReadViaArrayOverload(stream, buffer, 0, buffer.Length);

		Assert.Equal(2, bytesRead);
		Assert.Equal(2L, GetBytesTransferred(stream));
	}

	[Fact]
	public void Progress_ReportedOnForce()
	{
		using var inner = new MemoryStream([1, 2, 3, 4]);
		var progress = new ProgressSink();
		var stopwatch = Stopwatch.StartNew();
		using var stream = CreateProgressTrackingReadStream
		(
			inner,
			totalBytes: 4,
			progress: progress,
			stopwatch: stopwatch
		);
		var buffer = new byte[2];

		ReadViaArrayOverload(stream, buffer, 0, buffer.Length);

		Assert.Empty(progress.Reports);

		ReportFinal(stream);

		var report = Assert.Single(progress.Reports);
		Assert.Equal(2L, GetInt64PropertyValue(report, "BytesTransferred"));
	}

	[Fact]
	public void Progress_ReportIncludesPhaseAndCurrentFile()
	{
		using var inner = new MemoryStream(new byte[10]);
		var progress = new ProgressSink();
		var stopwatch = Stopwatch.StartNew();
		using var stream = CreateProgressTrackingReadStream
		(
			inner,
			totalBytes: 10,
			progress: progress,
			stopwatch: stopwatch,
			phaseName: "Extracting",
			currentFile: "voices\\sample.onnx"
		);
		var buffer = new byte[4];

		Thread.Sleep(150);
		ReadViaArrayOverload(stream, buffer, 0, buffer.Length);

		var report = Assert.Single(progress.Reports);
		Assert.Equal("Extracting", GetPropertyValue(report, "Phase")?.ToString());
		Assert.Equal("voices\\sample.onnx", Assert.IsType<string>(GetPropertyValue(report, "CurrentFile")));
	}

	[Fact]
	public void Progress_WithTotalBytes_ReportsPercentAndEta()
	{
		using var inner = new MemoryStream(new byte[20]);
		var progress = new ProgressSink();
		var stopwatch = Stopwatch.StartNew();
		using var stream = CreateProgressTrackingReadStream
		(
			inner,
			totalBytes: 20,
			progress: progress,
			stopwatch: stopwatch
		);
		var buffer = new byte[10];

		ReadViaArrayOverload(stream, buffer, 0, buffer.Length);
		Thread.Sleep(150);
		ReportFinal(stream);

		var report = Assert.Single(progress.Reports);
		Assert.Equal(20L, GetInt64PropertyValue(report, "TotalBytes"));
		Assert.Equal(50d, GetDoublePropertyValue(report, "OverallPercent"), 3);
		Assert.NotNull(GetPropertyValue(report, "Eta"));
		Assert.True(GetDoublePropertyValue(report, "TransferRate") > 0);
	}

	[Fact]
	public void Progress_WithoutTotalBytes_NoPercentOrEta()
	{
		using var inner = new MemoryStream(new byte[20]);
		var progress = new ProgressSink();
		var stopwatch = Stopwatch.StartNew();
		using var stream = CreateProgressTrackingReadStream
		(
			inner,
			totalBytes: null,
			progress: progress,
			stopwatch: stopwatch
		);
		var buffer = new byte[10];

		ReadViaArrayOverload(stream, buffer, 0, buffer.Length);
		Thread.Sleep(150);
		ReportFinal(stream);

		var report = Assert.Single(progress.Reports);
		Assert.Null(GetPropertyValue(report, "OverallPercent"));
		Assert.Null(GetPropertyValue(report, "Eta"));
	}

	[Fact]
	public void Progress_ThrottlesReports()
	{
		using var inner = new MemoryStream(new byte[30]);
		var progress = new ProgressSink();
		var stopwatch = Stopwatch.StartNew();
		using var stream = CreateProgressTrackingReadStream
		(
			inner,
			totalBytes: 30,
			progress: progress,
			stopwatch: stopwatch
		);
		var buffer = new byte[10];

		Thread.Sleep(150);
		ReadViaArrayOverload(stream, buffer, 0, buffer.Length);

		Assert.Single(progress.Reports);

		ReadViaArrayOverload(stream, buffer, 0, buffer.Length);

		Assert.Single(progress.Reports);

		Thread.Sleep(150);
		ReadViaArrayOverload(stream, buffer, 0, buffer.Length);

		Assert.Equal(2, progress.Reports.Count);
	}

	[Fact]
	public void Progress_ReportFinal_IncludesFilesCompletedAndEntryCount()
	{
		using var inner = new MemoryStream(new byte[10]);
		var progress = new ProgressSink();
		var stopwatch = Stopwatch.StartNew();
		using var stream = CreateProgressTrackingReadStream
		(
			inner,
			totalBytes: 10,
			progress: progress,
			stopwatch: stopwatch
		);

		ReportFinal(stream, filesCompleted: 3, entryCount: 8);

		var report = Assert.Single(progress.Reports);
		Assert.Equal(3, GetInt32PropertyValue(report, "FilesCompleted"));
		Assert.Equal(8, GetInt32PropertyValue(report, "EntryCount"));
	}

	[Fact]
	public void ZeroBytesRead_DoesNotIncrementBytesTransferred()
	{
		using var inner = new MemoryStream([1, 2, 3, 4, 5]);
		using var stream = CreateProgressTrackingReadStream(inner);
		var buffer = new byte[5];

		Assert.Equal(5, ReadViaArrayOverload(stream, buffer, 0, buffer.Length));
		Assert.Equal(5L, GetBytesTransferred(stream));

		Assert.Equal(0, ReadViaArrayOverload(stream, buffer, 0, 1));
		Assert.Equal(5L, GetBytesTransferred(stream));
	}

	private static Stream CreateProgressTrackingReadStream
	(
		Stream inner,
		long? totalBytes = null,
		IProgress<object>? progress = null,
		Stopwatch? stopwatch = null,
		string phaseName = "Downloading",
		string? currentFile = "voice.onnx",
		bool leaveOpen = false
	)
	{
		stopwatch ??= Stopwatch.StartNew();
		var instance = Activator.CreateInstance
		(
			ProgressTrackingReadStreamType,
			inner,
			totalBytes,
			progress,
			stopwatch,
			Enum.Parse(DownloadPhaseType, phaseName),
			currentFile,
			leaveOpen
		);

		return Assert.IsAssignableFrom<Stream>(instance);
	}

	private static long GetBytesTransferred(Stream stream)
	{
		return GetInt64PropertyValue(stream, "BytesTransferred");
	}

	private static int ReadViaArrayOverload(Stream stream, byte[] buffer, int offset, int count)
	{
#pragma warning disable CA2022
		return stream.Read(buffer, offset, count);
#pragma warning restore CA2022
	}

	private static async Task<int> ReadAsyncViaArrayOverload
	(
		Stream stream,
		byte[] buffer,
		int offset,
		int count,
		CancellationToken cancellationToken
	)
	{
		var method = stream.GetType().GetMethod
		(
			"ReadAsync",
			BindingFlags.Instance | BindingFlags.Public,
			binder: null,
			[
				typeof(byte[]),
				typeof(int),
				typeof(int),
				typeof(CancellationToken),
			],
			modifiers: null
		) ?? throw new InvalidOperationException("Could not find array ReadAsync overload.");

		var task = Assert.IsType<Task<int>>
		(
			method.Invoke(stream, [buffer, offset, count, cancellationToken])
		);

		return await task.ConfigureAwait(false);
	}

	private static async Task WriteAsyncViaArrayOverload
	(
		Stream stream,
		byte[] buffer,
		int offset,
		int count,
		CancellationToken cancellationToken
	)
	{
		var method = stream.GetType().GetMethod
		(
			"WriteAsync",
			BindingFlags.Instance | BindingFlags.Public,
			binder: null,
			[
				typeof(byte[]),
				typeof(int),
				typeof(int),
				typeof(CancellationToken),
			],
			modifiers: null
		) ?? throw new InvalidOperationException("Could not find array WriteAsync overload.");

		Task task;
		try
		{
			task = Assert.IsType<Task>
			(
				method.Invoke(stream, [buffer, offset, count, cancellationToken])
			);
		}
		catch (TargetInvocationException ex) when (ex.InnerException is not null)
		{
			ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
			throw;
		}

		await task.ConfigureAwait(false);
	}

	private static int GetInt32PropertyValue(object instance, string propertyName)
	{
		return Assert.IsType<int>(GetPropertyValue(instance, propertyName));
	}

	private static long GetInt64PropertyValue(object instance, string propertyName)
	{
		return Assert.IsType<long>(GetPropertyValue(instance, propertyName));
	}

	private static double GetDoublePropertyValue(object instance, string propertyName)
	{
		return Assert.IsType<double>(GetPropertyValue(instance, propertyName));
	}

	private static object? GetPropertyValue(object instance, string propertyName)
	{
		var property = instance.GetType().GetProperty
		(
			propertyName,
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
		) ?? throw new InvalidOperationException($"Could not find property '{propertyName}'.");

		return property.GetValue(instance);
	}

	private static void ReportFinal
	(
		Stream stream,
		int filesCompleted = 0,
		int? entryCount = null
	)
	{
		var method = ProgressTrackingReadStreamType.GetMethod
		(
			"ReportFinal",
			BindingFlags.Instance | BindingFlags.Public
		) ?? throw new InvalidOperationException("Could not find ReportFinal.");

		method.Invoke(stream, [filesCompleted, entryCount]);
	}

	private sealed class ProgressSink : IProgress<object>
	{
		public List<object> Reports { get; } = [];

		public void Report(object value)
		{
			Reports.Add(value);
		}
	}

	private sealed class TrackingStream : Stream
	{
		private readonly bool _canRead;
		private readonly bool _canSeek;
		private readonly bool _canWrite;

		public TrackingStream
		(
			long length = 0,
			bool canRead = true,
			bool canSeek = true,
			bool canWrite = true
		)
		{
			LengthValue = length;
			_canRead = canRead;
			_canSeek = canSeek;
			_canWrite = canWrite;
		}

		public int FlushCallCount { get; private set; }

		public int FlushAsyncCallCount { get; private set; }

		public bool IsDisposed { get; private set; }

		public bool IsAsyncDisposed { get; private set; }

		public long? LastSeekOffset { get; private set; }

		public SeekOrigin? LastSeekOrigin { get; private set; }

		public long? LastSetLengthValue { get; private set; }

		public long LengthValue { get; private set; }

		public int SeekCallCount { get; private set; }

		public int SetLengthCallCount { get; private set; }

		public override bool CanRead => !IsDisposed && _canRead;

		public override bool CanSeek => !IsDisposed && _canSeek;

		public override bool CanWrite => !IsDisposed && _canWrite;

		public override long Length => LengthValue;

		public override long Position { get; set; }

		public override void Flush()
		{
			ThrowIfDisposed();
			FlushCallCount++;
		}

		public override Task FlushAsync(CancellationToken cancellationToken)
		{
			ThrowIfDisposed();
			FlushAsyncCallCount++;
			return Task.CompletedTask;
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			ThrowIfDisposed();
			return 0;
		}

		public override int Read(Span<byte> buffer)
		{
			ThrowIfDisposed();
			return 0;
		}

		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			ThrowIfDisposed();
			return ValueTask.FromResult(0);
		}

		public override Task<int> ReadAsync
		(
			byte[] buffer,
			int offset,
			int count,
			CancellationToken cancellationToken
		)
		{
			ThrowIfDisposed();
			return Task.FromResult(0);
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			ThrowIfDisposed();
			SeekCallCount++;
			LastSeekOffset = offset;
			LastSeekOrigin = origin;
			Position = origin switch
			{
				SeekOrigin.Begin => offset,
				SeekOrigin.Current => Position + offset,
				SeekOrigin.End => LengthValue + offset,
				_ => throw new ArgumentOutOfRangeException(nameof(origin)),
			};

			return Position;
		}

		public override void SetLength(long value)
		{
			ThrowIfDisposed();
			SetLengthCallCount++;
			LastSetLengthValue = value;
			LengthValue = value;
			if (Position > value)
			{
				Position = value;
			}
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			ThrowIfDisposed();
			Position += count;
			LengthValue = Math.Max(LengthValue, Position);
		}

		public override void Write(ReadOnlySpan<byte> buffer)
		{
			ThrowIfDisposed();
			Position += buffer.Length;
			LengthValue = Math.Max(LengthValue, Position);
		}

		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
		{
			ThrowIfDisposed();
			Write(buffer.Span);
			return ValueTask.CompletedTask;
		}

		public override Task WriteAsync
		(
			byte[] buffer,
			int offset,
			int count,
			CancellationToken cancellationToken
		)
		{
			ThrowIfDisposed();
			Write(buffer, offset, count);
			return Task.CompletedTask;
		}

		protected override void Dispose(bool disposing)
		{
			IsDisposed = true;
			base.Dispose(disposing);
		}

		public override async ValueTask DisposeAsync()
		{
			IsDisposed = true;
			IsAsyncDisposed = true;
			await base.DisposeAsync().ConfigureAwait(false);
		}

		private void ThrowIfDisposed()
		{
			ObjectDisposedException.ThrowIf(IsDisposed, this);
		}
	}
}
