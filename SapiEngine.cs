using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Talktastic;

/// <summary>
/// Synthesizes classic SAPI5 voices via raw COM (ISpVoice), bypassing WinRT speech synthesis.
/// </summary>
/// <remarks>
/// The WinRT <c>Windows.Media.SpeechSynthesis.SpeechSynthesizer</c> depends on Media Foundation,
/// which is absent on Windows "N"/"KN" editions without the Media Feature Pack. Classic SAPI does
/// not. This engine drives <c>ISpVoice</c> directly through raw vtable pointers -- no
/// <c>[ComImport]</c>, no <c>System.Speech</c> -- so it remains NativeAOT-compatible.
/// </remarks>
[ExcludeFromCodeCoverage]
internal static partial class SapiEngine
{
	// COM CLSIDs / IIDs (Windows SDK sapi.h)
	static readonly Guid CLSID_SpVoice = new("96749377-3391-11D2-9EE3-00C04F797396");
	static readonly Guid CLSID_SpStream = new("715D9C59-4442-11D2-9605-00C04F8EE628");
	static readonly Guid CLSID_SpObjectToken = new("EF411752-3736-4CB4-9C8C-8EF4CCB58EFE");
	static readonly Guid IID_ISpVoice = new("6C44DF74-72B9-4992-A1EC-EF996E0422D4");
	static readonly Guid IID_ISpStream = new("12E3CCA9-7518-44C5-A5E7-BA5A79CB929E");
	static readonly Guid IID_ISpObjectToken = new("14056589-E16C-11D2-BB90-00C04F8EE6C0");

	// Stream data format identifier for a WAVEFORMATEX-described PCM stream.
	static readonly Guid SPDFID_WaveFormatEx = new("C31ADBAE-527F-4FF5-A230-F62BB61FF70C");

	const uint CLSCTX_INPROC_SERVER = 1;

	// SPEAKFLAGS
	const uint SPF_DEFAULT = 0;
	const uint SPF_IS_XML = 8;

	// STGC / STREAM_SEEK
	const uint STREAM_SEEK_END = 2;

	// ── Vtable slot indices (verified against Windows SDK 10.0.22621.0 sapi.h) ──

	// ISpVoice : ISpEventSource : ISpNotifySource : IUnknown
	const int Slot_Voice_SetOutput = 13;
	const int Slot_Voice_SetVoice = 18;
	const int Slot_Voice_Speak = 20;

	// ISpObjectToken : ISpDataKey : IUnknown
	const int Slot_Token_SetId = 15;

	// IStream : ISequentialStream : IUnknown (for the in-memory base stream)
	const int Slot_IStream_Seek = 5;

	// ISpStream : ISpStreamFormat : IStream
	const int Slot_Stream_SetBaseStream = 15;
	const int Slot_Stream_Close = 18;

	// ── P/Invoke ──

	[LibraryImport("ole32")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial int CoCreateInstance(
		in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

	[LibraryImport("ole32")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial int CoInitializeEx(nint reserved, uint dwCoInit);

	[LibraryImport("ole32")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial int CreateStreamOnHGlobal(
		nint hGlobal, [MarshalAs(UnmanagedType.Bool)] bool fDeleteOnRelease, out nint ppstm);

	[LibraryImport("ole32")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial int GetHGlobalFromStream(nint pstm, out nint phglobal);

	[LibraryImport("kernel32")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial nint GlobalLock(nint hMem);

	[LibraryImport("kernel32")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GlobalUnlock(nint hMem);

	// ── WAVEFORMATEX ──

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	struct WaveFormatEx
	{
		public ushort wFormatTag;
		public ushort nChannels;
		public uint nSamplesPerSec;
		public uint nAvgBytesPerSec;
		public ushort nBlockAlign;
		public ushort wBitsPerSample;
		public ushort cbSize;

		public static WaveFormatEx Pcm(uint samplesPerSec, ushort bitsPerSample, ushort channels)
		{
			var blockAlign = (ushort)(channels * (bitsPerSample / 8));

			return new WaveFormatEx
			{
				wFormatTag = 1, // WAVE_FORMAT_PCM
				nChannels = channels,
				nSamplesPerSec = samplesPerSec,
				nAvgBytesPerSec = samplesPerSec * blockAlign,
				nBlockAlign = blockAlign,
				wBitsPerSample = bitsPerSample,
				cbSize = 0,
			};
		}
	}

	// ── Helpers ──

	static unsafe nint* Vtable(nint pObj) => *(nint**)pObj;

	static unsafe void Release(nint pObj)
	{
		if (pObj != 0)
			((delegate* unmanaged[Stdcall]<nint, uint>)Vtable(pObj)[2])(pObj);
	}

	// ── Public API ──

	/// <summary>
	/// Synthesizes text with the given SAPI voice token to 22.05 kHz, 16-bit, mono PCM WAV bytes,
	/// entirely in memory (no temporary file).
	/// </summary>
	/// <param name="voicePath">The full registry token path of the voice (ISpObjectToken id).</param>
	/// <param name="text">The text or SSML to speak.</param>
	/// <param name="isXml">Whether <paramref name="text"/> is XML/SSML.</param>
	/// <returns>The synthesized WAV bytes.</returns>
	public static byte[] Synthesize(string voicePath, string text, bool isXml)
	{
		_ = CoInitializeEx(0, 0); // COINIT_MULTITHREADED; ignore if already initialized
		return SynthesizeToMemory(voicePath, text, isXml);
	}

	/// <summary>
	/// Speaks text with the given SAPI voice token directly to the default audio device, streaming
	/// the audio as it is generated (no intermediate buffer, no temporary file). Blocks until
	/// playback completes.
	/// </summary>
	/// <param name="voicePath">The full registry token path of the voice (ISpObjectToken id).</param>
	/// <param name="text">The text or SSML to speak.</param>
	/// <param name="isXml">Whether <paramref name="text"/> is XML/SSML.</param>
	public static void Speak(string voicePath, string text, bool isXml)
	{
		_ = CoInitializeEx(0, 0); // COINIT_MULTITHREADED; ignore if already initialized
		SpeakToDefaultDevice(voicePath, text, isXml);
	}

#pragma warning disable CA1508 // Analyzer can't see through unsafe COM vtable writes

	private static unsafe byte[] SynthesizeToMemory(string voicePath, string text, bool isXml)
	{
		nint pVoice = 0, pStream = 0, pToken = 0, pBaseStream = 0;

		try
		{
			Marshal.ThrowExceptionForHR(CoCreateInstance(
				CLSID_SpVoice, 0, CLSCTX_INPROC_SERVER, IID_ISpVoice, out pVoice));
			Marshal.ThrowExceptionForHR(CoCreateInstance(
				CLSID_SpStream, 0, CLSCTX_INPROC_SERVER, IID_ISpStream, out pStream));
			Marshal.ThrowExceptionForHR(CoCreateInstance(
				CLSID_SpObjectToken, 0, CLSCTX_INPROC_SERVER, IID_ISpObjectToken, out pToken));

			SetVoiceToken(pVoice, pToken, voicePath);

			// Back the SAPI stream with an in-memory HGLOBAL IStream (fDeleteOnRelease = TRUE) so
			// the audio never touches the filesystem.
			Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(0, true, out pBaseStream));

			var wfx = WaveFormatEx.Pcm(22050, 16, 1);
			var formatId = SPDFID_WaveFormatEx;

			// Stream: SetBaseStream(pBaseStream, &SPDFID_WaveFormatEx, &wfx)
			Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, void*, int>)
				Vtable(pStream)[Slot_Stream_SetBaseStream])(pStream, pBaseStream, &formatId, &wfx));

			// Voice: SetOutput(stream, fAllowFormatChanges = FALSE)
			Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int, int>)
				Vtable(pVoice)[Slot_Voice_SetOutput])(pVoice, pStream, 0));

			Speak(pVoice, text, isXml);

			// Detach the stream from the voice, then close it to flush all bytes into the HGLOBAL.
			_ = ((delegate* unmanaged[Stdcall]<nint, nint, int, int>)
				Vtable(pVoice)[Slot_Voice_SetOutput])(pVoice, 0, 0);
			_ = ((delegate* unmanaged[Stdcall]<nint, int>)
				Vtable(pStream)[Slot_Stream_Close])(pStream);

			var pcm = ReadStreamBytes(pBaseStream);
			return EnsureRiffWav(pcm, wfx);
		}
		finally
		{
			Release(pToken);
			Release(pStream);
			Release(pBaseStream);
			Release(pVoice);
		}
	}

	private static unsafe void SpeakToDefaultDevice(string voicePath, string text, bool isXml)
	{
		nint pVoice = 0, pToken = 0;

		try
		{
			Marshal.ThrowExceptionForHR(CoCreateInstance(
				CLSID_SpVoice, 0, CLSCTX_INPROC_SERVER, IID_ISpVoice, out pVoice));
			Marshal.ThrowExceptionForHR(CoCreateInstance(
				CLSID_SpObjectToken, 0, CLSCTX_INPROC_SERVER, IID_ISpObjectToken, out pToken));

			SetVoiceToken(pVoice, pToken, voicePath);

			// No SetOutput: ISpVoice lazily binds the default audio output and renders to it as the
			// audio is generated, so speech starts playing before synthesis finishes.
			Speak(pVoice, text, isXml);
		}
		finally
		{
			Release(pToken);
			Release(pVoice);
		}
	}

	/// <summary>
	/// Points the voice at the given SAPI voice token: token.SetId(path), voice.SetVoice(token).
	/// </summary>
	private static unsafe void SetVoiceToken(nint pVoice, nint pToken, string voicePath)
	{
		// Token: SetId(NULL category, full registry token path, fCreateIfNotExist = FALSE)
		fixed (char* pPath = voicePath)
		{
			Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, char*, int, int>)
				Vtable(pToken)[Slot_Token_SetId])(pToken, null, pPath, 0));
		}

		// Voice: SetVoice(token)
		Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)
			Vtable(pVoice)[Slot_Voice_SetVoice])(pVoice, pToken));
	}

	/// <summary>
	/// Voice: Speak(text, flags, NULL). Synchronous (no SPF_ASYNC) -- returns when complete.
	/// </summary>
	private static unsafe void Speak(nint pVoice, string text, bool isXml)
	{
		var flags = isXml ? SPF_IS_XML : SPF_DEFAULT;
		fixed (char* pText = text)
		{
			Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, uint, nint, int>)
				Vtable(pVoice)[Slot_Voice_Speak])(pVoice, pText, flags, 0));
		}
	}

	/// <summary>
	/// Reads the full logical contents of an in-memory IStream into a managed byte array.
	/// </summary>
	private static unsafe byte[] ReadStreamBytes(nint pStream)
	{
		// Seek to the end to obtain the true logical length (the HGLOBAL allocation may be larger).
		ulong length;
		Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)
			Vtable(pStream)[Slot_IStream_Seek])(pStream, 0, STREAM_SEEK_END, &length));

		var size = checked((int)length);
		var buffer = new byte[size];
		if (size == 0)
			return buffer;

		Marshal.ThrowExceptionForHR(GetHGlobalFromStream(pStream, out var hGlobal));
		var ptr = GlobalLock(hGlobal);
		try
		{
			Marshal.Copy(ptr, buffer, 0, size);
		}
		finally
		{
			_ = GlobalUnlock(hGlobal);
		}

		return buffer;
	}

#pragma warning restore CA1508

	/// <summary>
	/// Returns the buffer unchanged when it already carries a RIFF/WAVE header; otherwise wraps the
	/// raw PCM payload in a canonical 44-byte WAV header for the given format. This makes the result
	/// correct whether <c>ISpStream</c> emitted a full RIFF stream or bare PCM samples.
	/// </summary>
	private static byte[] EnsureRiffWav(byte[] data, WaveFormatEx wfx)
	{
		if (data.Length >= 12
			&& data.AsSpan(0, 4).SequenceEqual("RIFF"u8)
			&& data.AsSpan(8, 4).SequenceEqual("WAVE"u8))
		{
			return data;
		}

		return WrapPcmAsWav(data, wfx);
	}

	/// <summary>
	/// Wraps raw PCM bytes in a canonical 44-byte RIFF/WAVE header.
	/// </summary>
	private static byte[] WrapPcmAsWav(byte[] pcm, WaveFormatEx wfx)
	{
		var wav = new byte[44 + pcm.Length];
		var span = wav.AsSpan();

		"RIFF"u8.CopyTo(span);
		BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(36 + pcm.Length));
		"WAVE"u8.CopyTo(span[8..]);
		"fmt "u8.CopyTo(span[12..]);
		BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
		BinaryPrimitives.WriteUInt16LittleEndian(span[20..], wfx.wFormatTag);
		BinaryPrimitives.WriteUInt16LittleEndian(span[22..], wfx.nChannels);
		BinaryPrimitives.WriteUInt32LittleEndian(span[24..], wfx.nSamplesPerSec);
		BinaryPrimitives.WriteUInt32LittleEndian(span[28..], wfx.nAvgBytesPerSec);
		BinaryPrimitives.WriteUInt16LittleEndian(span[32..], wfx.nBlockAlign);
		BinaryPrimitives.WriteUInt16LittleEndian(span[34..], wfx.wBitsPerSample);
		"data"u8.CopyTo(span[36..]);
		BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)pcm.Length);
		pcm.CopyTo(span[44..]);

		return wav;
	}
}
