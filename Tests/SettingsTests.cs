namespace Talktastic.Tests;

public sealed class SettingsTests
{
	[Fact]
	public void RvcConstants_MatchExpectedValues()
	{
		Assert.Equal
		(
			16000,
			Settings.Rvc.InputSampleRate
		);
		Assert.Equal
		(
			160,
			Settings.Rvc.Window
		);
		Assert.Equal
		(
			0,
			Settings.Rvc.SpeakerId
		);
		Assert.Equal
		(
			0.25f,
			Settings.Rvc.RmsMixRate
		);
		Assert.Equal
		(
			3,
			Settings.Rvc.PadSeconds
		);
		Assert.Equal
		(
			10,
			Settings.Rvc.QuerySeconds
		);
		Assert.Equal
		(
			50,
			Settings.Rvc.CenterSeconds
		);
		Assert.Equal
		(
			50,
			Settings.Rvc.MaxSeconds
		);
		Assert.Equal
		(
			2.0,
			Settings.Rvc.StreamChunkSeconds
		);
		Assert.Equal
		(
			0.3,
			Settings.Rvc.StreamPadSeconds
		);
	}

	[Fact]
	public void StreamConstants_MatchExpectedValues()
	{
		Assert.Equal
		(
			2.0,
			Settings.Stream.ChunkSeconds
		);
		Assert.Equal
		(
			16,
			Settings.Stream.MaxInFlight
		);
		Assert.Equal
		(
			8,
			Settings.Stream.FifoCapacity
		);
	}

	[Fact]
	public void OutputValidationConstants_MatchExpectedValues()
	{
		Assert.Equal
		(
			900.0,
			Settings.OutputValidation.MaxZeroCrossingRate
		);
		Assert.Equal
		(
			0.02,
			Settings.OutputValidation.MinRms
		);
		Assert.Equal
		(
			0.5,
			Settings.OutputValidation.MinSeconds
		);
	}

	[Fact]
	public void EffectiveRvcChunkSeconds_NullOverride_ReturnsDefault()
	{
		var originalOverride = Settings.Cli.RvcChunkSecondsOverride;

		try
		{
			Settings.Cli.RvcChunkSecondsOverride = null;

			Assert.Equal
			(
				Settings.Rvc.StreamChunkSeconds,
				Settings.Cli.EffectiveRvcChunkSeconds
			);
		}
		finally
		{
			Settings.Cli.RvcChunkSecondsOverride = originalOverride;
		}
	}

	[Fact]
	public void EffectiveRvcChunkSeconds_SetOverride_ReturnsOverride()
	{
		var originalOverride = Settings.Cli.RvcChunkSecondsOverride;

		try
		{
			Settings.Cli.RvcChunkSecondsOverride = 1.25;

			Assert.Equal
			(
				1.25,
				Settings.Cli.EffectiveRvcChunkSeconds
			);
		}
		finally
		{
			Settings.Cli.RvcChunkSecondsOverride = originalOverride;
		}
	}

	[Fact]
	public void EffectiveRvcPadSeconds_NullOverride_ReturnsDefault()
	{
		var originalOverride = Settings.Cli.RvcPadSecondsOverride;

		try
		{
			Settings.Cli.RvcPadSecondsOverride = null;

			Assert.Equal
			(
				Settings.Rvc.StreamPadSeconds,
				Settings.Cli.EffectiveRvcPadSeconds
			);
		}
		finally
		{
			Settings.Cli.RvcPadSecondsOverride = originalOverride;
		}
	}

	[Fact]
	public void EffectiveRvcPadSeconds_SetOverride_ReturnsOverride()
	{
		var originalOverride = Settings.Cli.RvcPadSecondsOverride;

		try
		{
			Settings.Cli.RvcPadSecondsOverride = 0.75;

			Assert.Equal
			(
				0.75,
				Settings.Cli.EffectiveRvcPadSeconds
			);
		}
		finally
		{
			Settings.Cli.RvcPadSecondsOverride = originalOverride;
		}
	}

	[Fact]
	public void CliProperties_AreBooleanValues()
	{
		Assert.IsType<bool>
		(
			Settings.Cli.NoGpu
		);
		Assert.IsType<bool>
		(
			Settings.Cli.ShowPerf
		);
		Assert.IsType<bool>
		(
			Settings.Cli.Verbose
		);
	}

	[Fact]
	public void RvcChunkSecondsOverride_NegativeValue_IsAllowedBySettings()
	{
		var originalOverride = Settings.Cli.RvcChunkSecondsOverride;

		try
		{
			var exception = Record.Exception
			(
				() => Settings.Cli.RvcChunkSecondsOverride = -1.0
			);

			Assert.Null
			(
				exception
			);
			Assert.Equal
			(
				-1.0,
				Settings.Cli.EffectiveRvcChunkSeconds
			);
		}
		finally
		{
			Settings.Cli.RvcChunkSecondsOverride = originalOverride;
		}
	}
}
