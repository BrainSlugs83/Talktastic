using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Talktastic;

/// <summary>
/// Provides embedded speech license operations.
/// </summary>
internal static class LicenseProvider
{
	private const string EmbeddedSpeechExtensionPath =
		@"C:\Windows\SystemApps\MicrosoftWindows.Client.Core_cw5n1h2txyewy\SpeechSynthesizerExtension.dll";

	private const string EulaMarker =
		"This model and the software may not be used or distributed";

	/// <summary>
	/// Extracts the embedded speech license text.
	/// </summary>
	/// <returns>The resulting string.</returns>
	[ExcludeFromCodeCoverage]
	public static string GetLicenseText()
	{
		if (!File.Exists(EmbeddedSpeechExtensionPath))
		{
			throw new InvalidOperationException
			(
				$"Cannot find '{EmbeddedSpeechExtensionPath}'. Neural voices require Windows with built-in speech components."
			);
		}

		var bytes = File.ReadAllBytes(EmbeddedSpeechExtensionPath);
		return ExtractLicenseText(bytes);
	}

	/// <summary>
	/// Extracts the embedded speech license text from raw extension bytes.
	/// </summary>
	/// <param name="fileBytes">The extension file bytes.</param>
	/// <returns>The resulting string.</returns>
	internal static string ExtractLicenseText(byte[] fileBytes)
	{
		var fileText = Encoding.UTF8.GetString(fileBytes);
		var markerIndex = fileText.IndexOf(EulaMarker, StringComparison.Ordinal);
		if (markerIndex < 0)
		{
			throw new InvalidOperationException
			(
				"Could not extract license text from SpeechSynthesizerExtension.dll. The file format may have changed."
			);
		}

		var terminatorIndex = fileText.IndexOf('\0', markerIndex);
		if (terminatorIndex < 0)
		{
			throw new InvalidOperationException
			(
				"Could not extract license text from SpeechSynthesizerExtension.dll. Unexpected format."
			);
		}

		var licenseBodyStart = markerIndex + EulaMarker.Length;
		var licenseBody = fileText[licenseBodyStart..terminatorIndex];
		if (string.IsNullOrWhiteSpace(licenseBody))
		{
			throw new InvalidOperationException
			(
				"Extracted empty license text from SpeechSynthesizerExtension.dll."
			);
		}

		return fileText[markerIndex..terminatorIndex].Trim();
	}
}
