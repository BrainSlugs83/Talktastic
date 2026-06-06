namespace Talktastic.Tests;

public sealed class FuzzyMatcherTests
{
	[Fact]
	public void FindBestMatch_NullQuery_ReturnsDefault()
	{
		var candidates = new[] { "BartSimpson" };

		var result = FuzzyMatcher.FindBestMatch<string>(candidates, null!, static s => s);

		Assert.Null(result);
	}

	[Fact]
	public void FindBestMatch_EmptyQuery_ReturnsDefault()
	{
		var candidates = new[] { "BartSimpson" };

		var result = FuzzyMatcher.FindBestMatch<string>(candidates, string.Empty, static s => s);

		Assert.Null(result);
	}

	[Fact]
	public void FindBestMatch_WhitespaceQuery_ReturnsDefault()
	{
		var candidates = new[] { "BartSimpson" };

		var result = FuzzyMatcher.FindBestMatch<string>(candidates, " \t ", static s => s);

		Assert.Null(result);
	}

	[Fact]
	public void FindBestMatch_EmptyCandidates_ReturnsDefault()
	{
		var result = FuzzyMatcher.FindBestMatch<string>([], "bart", static s => s);

		Assert.Null(result);
	}

	[Fact]
	public void FindBestMatch_ExactMatch_ReturnsIt()
	{
		var candidates = new[] { "HomerSimpson", "BartSimpson", "MargeSimpson" };

		var result = FuzzyMatcher.FindBestMatch<string>(candidates, "BartSimpson", static s => s);

		Assert.Equal("BartSimpson", result);
	}

	[Fact]
	public void FindBestMatch_ContainsMatch_SingleHit_ReturnsIt()
	{
		var candidates = new[] { "HomerSimpson", "BartSimpson", "MargeSimpson" };

		var result = FuzzyMatcher.FindBestMatch<string>(candidates, "bart", static s => s);

		Assert.Equal("BartSimpson", result);
	}

	[Fact]
	public void FindBestMatch_ContainsMatch_MultipleHits_UsesLevenshtein()
	{
		var candidates = new[] { "BartSimpson", "MargeSimpson" };

		var result = FuzzyMatcher.FindBestMatch<string>(candidates, "simpson", static s => s);

		Assert.Equal("BartSimpson", result);
	}

	[Fact]
	public void FindBestMatch_NoContainsMatch_FallsBackToLevenshtein()
	{
		var candidates = new[] { "egirl", "goblin", "dragon" };

		var result = FuzzyMatcher.FindBestMatch<string>(candidates, "egurl", static s => s);

		Assert.Equal("egirl", result);
	}

	[Fact]
	public void FindBestMatch_PrefixTruncation_ShortQueryMatchesLongName()
	{
		var candidates = new[] { "KatyPerryTD", "MargeSimpson", "HomerSimpson" };

		var result = FuzzyMatcher.FindBestMatch<string>(candidates, "Katie", static s => s);

		Assert.Equal("KatyPerryTD", result);
	}

	[Fact]
	public void FindBestMatch_StringOverload_ReturnsEmptyOnNoMatch()
	{
		var result = FuzzyMatcher.FindBestMatch(["BartSimpson", "MargeSimpson"], "zzzzz");

		Assert.Equal(string.Empty, result);
	}

	[Fact]
	public void FindBestMatch_GenericOverload_WithConverter()
	{
		TestVoice[] candidates =
		[
			new TestVoice("Homer Simpson"),
			new TestVoice("Bart Simpson"),
			new TestVoice("Marge Simpson"),
		];

		var result = FuzzyMatcher.FindBestMatch(candidates, "bart", static candidate => candidate.DisplayName);

		Assert.NotNull(result);
		Assert.Equal("Bart Simpson", result.DisplayName);
	}

	[Fact]
	public void FindBestMatch_CaseInsensitive()
	{
		var candidates = new[] { "bartsimpson", "margesimpson" };

		var result = FuzzyMatcher.FindBestMatch<string>(candidates, "BART", static s => s);

		Assert.Equal("bartsimpson", result);
	}

	[Fact]
	public void FindBestMatch_VeryDifferentStrings_ReturnsDefault()
	{
		var candidates = new[] { "bart", "marge", "homer" };

		var result = FuzzyMatcher.FindBestMatch<string>(candidates, "zzzzz", static s => s);

		Assert.Null(result);
	}

	private sealed record TestVoice(string DisplayName);
}
