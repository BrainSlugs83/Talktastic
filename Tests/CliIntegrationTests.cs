using System.Diagnostics;

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
		// homer should be cached from previous test / download
		Assert.Contains("homer", stdout, StringComparison.Ordinal);
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
}
