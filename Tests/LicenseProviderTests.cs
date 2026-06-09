using System.Text;

namespace Talktastic.Tests;

public sealed class LicenseProviderTests
{
	private const string EulaMarker =
		"This model and the software may not be used or distributed";

	[Fact]
	public void ExtractLicenseText_MarkerFound_ReturnsTextThroughNullTerminator()
	{
		var expected = EulaMarker + " except under the applicable license terms.";
		var fileBytes = Encoding.UTF8.GetBytes("prefix bytes " + expected + "\0ignored trailing bytes");

		var result = LicenseProvider.ExtractLicenseText(fileBytes);

		Assert.Equal(expected, result);
	}

	[Fact]
	public void ExtractLicenseText_MarkerNotFound_ThrowsInvalidOperationException()
	{
		var fileBytes = Encoding.UTF8.GetBytes("No license marker here.\0");

		var exception = Assert.Throws<InvalidOperationException>
		(
			() => LicenseProvider.ExtractLicenseText(fileBytes)
		);

		Assert.Contains("Could not extract license text", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ExtractLicenseText_NulTerminatorMissing_ThrowsInvalidOperationException()
	{
		var fileBytes = Encoding.UTF8.GetBytes(EulaMarker + " except under the applicable license terms.");

		var exception = Assert.Throws<InvalidOperationException>
		(
			() => LicenseProvider.ExtractLicenseText(fileBytes)
		);

		Assert.Contains("Unexpected format", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ExtractLicenseText_ExtractedTextIsWhitespace_ThrowsInvalidOperationException()
	{
		var fileBytes = Encoding.UTF8.GetBytes(EulaMarker + " \t \r\n\0");

		var exception = Assert.Throws<InvalidOperationException>
		(
			() => LicenseProvider.ExtractLicenseText(fileBytes)
		);

		Assert.Contains("Extracted empty license text", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ExtractLicenseText_HappyPath_TrimsExtractedText()
	{
		var expected = EulaMarker + " with surrounding whitespace trimmed.";
		var fileBytes = Encoding.UTF8.GetBytes("\r\n\t" + expected + " \t\0more bytes");

		var result = LicenseProvider.ExtractLicenseText(fileBytes);

		Assert.Equal(expected, result);
	}
}
