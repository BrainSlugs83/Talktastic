using System.Diagnostics;
using System.Text;

namespace Talktastic.Tests;

/// <summary>
/// Integration tests that invoke say.exe as a subprocess to verify CLI behavior.
/// </summary>
public class CliIntegrationTests
{
	private static readonly string SayExe = Path.GetFullPath
	(
		Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "dist", "say.exe")
	);

	private static async Task<(int ExitCode, string StdOut, string StdErr)> RunSayAsync
	(
		params string[] args
	)
	{
		var psi = new ProcessStartInfo
		{
			FileName = SayExe,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};

		foreach (var arg in args)
		{
			psi.ArgumentList.Add(arg);
		}

		using var proc = Process.Start(psi)!;
		var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
		var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
		await proc.WaitForExitAsync().ConfigureAwait(false);
		return (proc.ExitCode, stdout, stderr);
	}

	[Fact]
	public void SayExeExists()
	{
		Assert.True(File.Exists(SayExe), $"say.exe not found at {SayExe}");
	}

	[Fact]
	public async Task RvcUrlOnly_DownloadsWithoutText()
	{
		// --rvc with a URL and NO text should download/cache and exit 0
		var (exitCode, stdout, stderr) = await RunSayAsync
		(
			"--rvc", "https://huggingface.co/Sunwest/Homer_Simpson_300"
		);

		Assert.Equal(0, exitCode);
		Assert.Contains("Cached:", stderr, StringComparison.Ordinal);
	}

	[Fact]
	public async Task VoiceUrlOnly_DownloadsWithoutText()
	{
		// -v with a Piper URL and NO text should download/cache and exit 0
		var (exitCode, stdout, stderr) = await RunSayAsync
		(
			"-v",
			"https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/amy/medium/en_US-amy-medium.onnx"
		);

		Assert.Equal(0, exitCode);
		Assert.Contains("Cached:", stderr, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ListAll_ReturnsVoicesAndDevices()
	{
		var (exitCode, stdout, stderr) = await RunSayAsync("-l");

		Assert.Equal(0, exitCode);
		Assert.Contains("Voices:", stdout, StringComparison.Ordinal);
		Assert.Contains("Devices:", stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ListAll_ShowsRvcModels()
	{
		var (exitCode, stdout, stderr) = await RunSayAsync("-l");

		Assert.Equal(0, exitCode);
		Assert.Contains("RVC models:", stdout, StringComparison.Ordinal);
		// homer should be cached from previous test / download (case-insensitive
		// since the user may have renamed it to "Homer" or similar)
		Assert.Contains("homer", stdout, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task NoArgs_ShowsHelp()
	{
		// No args and no text should show help or exit cleanly
		var (exitCode, stdout, stderr) = await RunSayAsync();

		// Should not crash -- exit 0 or show help
		Assert.True(exitCode == 0 || stdout.Contains("Talktastic", StringComparison.Ordinal) || stderr.Contains("Talktastic", StringComparison.Ordinal),
			$"Expected help or clean exit, got exit code {exitCode}");
	}

	private static async Task<string?> GetFirstSapiVoiceAsync()
	{
		var (exitCode, stdout, _) = await RunSayAsync("--list-voices").ConfigureAwait(false);
		if (exitCode != 0)
		{
			return null;
		}

		foreach (var line in stdout.Split('\n'))
		{
			var marker = line.IndexOf("[sapi]", StringComparison.Ordinal);
			if (marker > 0)
			{
				return line[..marker].Trim();
			}
		}

		return null;
	}

	[Fact]
	public async Task LegacyVoice_SynthesizesValidWavFile()
	{
		// Positive: validates the Windows N fix -- legacy SAPI voices must
		// synthesize via direct COM (no Media Foundation) into a valid RIFF/WAVE file.
		var voice = await GetFirstSapiVoiceAsync();
		if (voice is null)
		{
			// No SAPI voices installed on this machine -- nothing to validate.
			return;
		}

		var wavPath = Path.Combine(Path.GetTempPath(), $"talktastic_sapi_{Guid.NewGuid():N}.wav");
		try
		{
			var (exitCode, stdout, stderr) = await RunSayAsync
			(
				"Integration test.", "-v", voice, "-o", wavPath
			);

			Assert.Equal(0, exitCode);
			Assert.DoesNotContain("Unhandled exception", stdout + stderr, StringComparison.Ordinal);
			Assert.True(File.Exists(wavPath), $"WAV not written to {wavPath}");

			var bytes = await File.ReadAllBytesAsync(wavPath);
			Assert.True(bytes.Length > 44, $"WAV too small ({bytes.Length} bytes)");
			Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
			Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
		}
		finally
		{
			if (File.Exists(wavPath))
			{
				File.Delete(wavPath);
			}
		}
	}

	[Fact]
	public async Task UnknownVoice_ReportsErrorWithoutCrashing()
	{
		// Negative: an unknown voice must produce a clean error, not an unhandled exception.
		var wavPath = Path.Combine(Path.GetTempPath(), $"talktastic_novoice_{Guid.NewGuid():N}.wav");
		try
		{
			var (exitCode, stdout, stderr) = await RunSayAsync
			(
				"hello", "-v", "ZZZNoSuchVoiceXYZ", "-o", wavPath
			);

			Assert.NotEqual(0, exitCode);
			Assert.DoesNotContain("Unhandled exception", stdout + stderr, StringComparison.Ordinal);
			Assert.Contains("No voice matched", stdout + stderr, StringComparison.Ordinal);
		}
		finally
		{
			if (File.Exists(wavPath))
			{
				File.Delete(wavPath);
			}
		}
	}

	[Fact]
	public async Task UnknownDevice_ReportsErrorWithoutCrashing()
	{
		// Negative: an unknown output device must produce a clean error, not an unhandled exception.
		var voice = await GetFirstSapiVoiceAsync();
		if (voice is null)
		{
			return;
		}

		var (exitCode, stdout, stderr) = await RunSayAsync
		(
			"hello", "-v", voice, "-d", "ZZZNoSuchDeviceXYZ"
		);

		Assert.NotEqual(0, exitCode);
		Assert.DoesNotContain("Unhandled exception", stdout + stderr, StringComparison.Ordinal);
		Assert.Contains("No speaker matched", stdout + stderr, StringComparison.Ordinal);
	}

	// ── FileDownloader / download error integration tests ────────────

	[Fact]
	public async Task InvalidPiperUrl_ReportsErrorWithoutCrashing()
	{
		// Negative: a Piper voice URL that 404s must produce a clean error message.
		var (exitCode, _, stderr) = await RunSayAsync
		(
			"-v", "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/NONEXISTENT/medium/en_US-NONEXISTENT-medium.onnx"
		);

		var combined = stderr;
		Assert.NotEqual(0, exitCode);
		Assert.DoesNotContain("Unhandled exception", combined, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnreachableHost_ReportsNetworkErrorWithoutCrashing()
	{
		// Negative: a completely unreachable host must surface a network error, not crash.
		var (exitCode, stdout, stderr) = await RunSayAsync
		(
			"-v", "https://this-host-does-not-exist-zzz.example.invalid/voice.onnx"
		);

		var combined = stdout + stderr;
		Assert.NotEqual(0, exitCode);
		Assert.DoesNotContain("Unhandled exception", combined, StringComparison.Ordinal);
	}

	[Fact]
	public async Task InvalidRvcUrl_ReportsErrorWithoutCrashing()
	{
		// Negative: an invalid RVC URL must produce a clean error, not crash.
		var (exitCode, stdout, stderr) = await RunSayAsync
		(
			"--rvc", "https://this-host-does-not-exist-zzz.example.invalid/model.zip"
		);

		var combined = stdout + stderr;
		Assert.NotEqual(0, exitCode);
		Assert.DoesNotContain("Unhandled exception", combined, StringComparison.Ordinal);
	}

	[Fact]
	public async Task VersionFlag_PrintsVersionString()
	{
		// Positive: --version should print a semver-like string and exit 0.
		var (exitCode, stdout, _) = await RunSayAsync("--version");

		Assert.Equal(0, exitCode);
		Assert.Matches(@"^\d+\.\d+\.\d+", stdout.Trim());
	}

	[Fact]
	public async Task ListVoices_IncludesPiperVoices()
	{
		// Positive: --list-voices should include at least one [piper] voice
		// (Amy was downloaded by VoiceUrlOnly_DownloadsWithoutText).
		var (exitCode, stdout, _) = await RunSayAsync("--list-voices");

		Assert.Equal(0, exitCode);
		Assert.Contains("[piper]", stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ListVoices_IncludesNeuralVoices()
	{
		// Positive: --list-voices should include at least one [neural] voice.
		var (exitCode, stdout, _) = await RunSayAsync("--list-voices");

		Assert.Equal(0, exitCode);
		Assert.Contains("[neural]", stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PiperVoice_SynthesizesValidWavFile()
	{
		// Positive: cached Piper voice must synthesize valid WAV output.
		// Amy was downloaded by VoiceUrlOnly_DownloadsWithoutText.
		var wavPath = Path.Combine(Path.GetTempPath(), $"talktastic_piper_{Guid.NewGuid():N}.wav");
		try
		{
			var (exitCode, stdout, stderr) = await RunSayAsync
			(
				"Integration test for Piper.", "-v", "amy", "-o", wavPath
			);

			Assert.Equal(0, exitCode);
			Assert.DoesNotContain("Unhandled exception", stdout + stderr, StringComparison.Ordinal);
			Assert.True(File.Exists(wavPath), $"WAV not written to {wavPath}");

			var bytes = await File.ReadAllBytesAsync(wavPath);
			Assert.True(bytes.Length > 44, $"WAV too small ({bytes.Length} bytes)");
			Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
			Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
		}
		finally
		{
			if (File.Exists(wavPath))
			{
				File.Delete(wavPath);
			}
		}
	}
}
