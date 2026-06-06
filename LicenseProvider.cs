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
		var fileText = Encoding.UTF8.GetString(bytes);
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

		var extracted = fileText[markerIndex..terminatorIndex].Trim();
		if (string.IsNullOrWhiteSpace(extracted))
		{
			throw new InvalidOperationException
			(
				"Extracted empty license text from SpeechSynthesizerExtension.dll."
			);
		}

		return extracted;
	}
}
