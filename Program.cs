using Microsoft.CognitiveServices.Speech;
using System.CommandLine;
using System.CommandLine.Help;
using System.Reflection;
using System.Xml;

using Talktastic;

NativeExtractor.EnsureExtracted();

var version = Assembly.GetEntryAssembly()
	?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
	?.InformationalVersion ?? "0.0.0";

var textArgument = new Argument<string?>("text")
{
	Arity = ArgumentArity.ZeroOrOne,
	Description = "Text to speak (or - for stdin)",
};

var voiceOption = new Option<string>("--voice", "-v")
{
	Description = "Voice name (partial match, case-insensitive)",
};

var outputOption = new Option<string>("--output", "-o")
{
	Description = "Output to WAV or MP3 file",
};

var deviceOption = new Option<string>("--device", "-d")
{
	Description = "Audio output device name",
};

var listVoicesOption = new Option<bool>("--list-voices")
{
	Description = "List available voices",
};

var listDevicesOption = new Option<bool>("--list-devices")
{
	Description = "List audio output devices",
};

var listAllOption = new Option<bool>("--list", "-l")
{
	Description = "List voices and devices",
};

var rateOption = new Option<string>("--rate", "-r")
{
	Description = "Speaking rate adjustment",
};

var formatOption = new Option<string>("--format", "-f")
{
	DefaultValueFactory = static _ => SpeechSynthesisOutputFormat.Riff24Khz16BitMonoPcm.ToString(),
	Description = "Audio format",
};

var ssmlOption = new Option<bool>("--ssml")
{
	Description = "Treat input as SSML",
};

var helpSsmlOption = new Option<bool>("--help-ssml")
{
	Description = "Show SSML usage examples",
};

var quietOption = new Option<bool>("--quiet", "-q")
{
	Description = "Suppress stdout output (errors still go to stderr)",
};

var superQuietOption = new Option<bool>("--super-quiet", "-Q")
{
	Description = "Suppress all output (stdout and stderr)",
};

var rootCommand = new RootCommand($"Talktastic v{version} - standalone Windows TTS CLI")
{
	textArgument,
	voiceOption,
	outputOption,
	deviceOption,
	listAllOption,
	listVoicesOption,
	listDevicesOption,
	rateOption,
	formatOption,
	ssmlOption,
	helpSsmlOption,
	quietOption,
	superQuietOption,
};

rootCommand.SetAction
(
	async
	(
		ParseResult parseResult,
		CancellationToken cancellationToken
	) =>
	{
		try
		{
			var text = parseResult.GetValue(textArgument);
			var voice = parseResult.GetValue(voiceOption);
			var output = parseResult.GetValue(outputOption);
			var device = parseResult.GetValue(deviceOption);
				var listAll = parseResult.GetValue(listAllOption);
				var listVoices = parseResult.GetValue(listVoicesOption);
				var listDevices = parseResult.GetValue(listDevicesOption);
				var rate = parseResult.GetValue(rateOption);
				var format = parseResult.GetRequiredValue(formatOption);
				var ssml = parseResult.GetValue(ssmlOption);
				var quiet = parseResult.GetValue(quietOption);
				var superQuiet = parseResult.GetValue(superQuietOption);

				if (superQuiet)
				{
					Console.SetOut(TextWriter.Null);
					Console.SetError(TextWriter.Null);
				}
				else if (quiet)
				{
					Console.SetOut(TextWriter.Null);
				}

				if (!Enum.TryParse(format, ignoreCase: true, out SpeechSynthesisOutputFormat outputFormat))
				{
					await Console.Error.WriteLineAsync($"Unknown audio format '{format}'.").ConfigureAwait(false);
					return 1;
				}

				if (listAll || listVoices || listDevices)
				{
					if (listAll || listVoices)
					{
						var voices = await VoiceEnumerator.GetVoicesAsync(cancellationToken).ConfigureAwait(false);
						await Console.Out.WriteLineAsync("Voices:").ConfigureAwait(false);
						foreach (var v in voices)
						{
							var tag = v.VoiceType == VoiceType.Neural ? "neural" : "legacy";
							await Console.Out.WriteLineAsync($"  {v.Name} [{tag}] ({v.Locale}, {v.Gender})").ConfigureAwait(false);
						}
					}

					if (listAll || listDevices)
					{
						if (listAll || listVoices)
						{
							await Console.Out.WriteLineAsync().ConfigureAwait(false);
						}

						await Console.Out.WriteLineAsync("Devices:").ConfigureAwait(false);
						var devices = AudioOutput.GetSpeakers();
						foreach (var d in devices)
						{
							await Console.Out.WriteLineAsync($"  {d.FriendlyName}").ConfigureAwait(false);
						}

						if (devices.Count == 0)
						{
							await Console.Out.WriteLineAsync("  (none found)").ConfigureAwait(false);
						}
					}

					return 0;
				}

			var inputText = await ReadInputTextAsync(text, cancellationToken).ConfigureAwait(false);
			if (string.IsNullOrWhiteSpace(inputText))
			{
				new HelpAction().Invoke(parseResult);
				return 0;
			}

			var request = new SynthesisRequest
			(
				Text: inputText,
				VoiceQuery: voice,
				OutputPath: output,
				DeviceQuery: device,
				Rate: rate,
				OutputFormat: outputFormat,
				TreatInputAsSsml: ssml
			);

			var summary = await SpeechEngine.SynthesizeAsync(request, cancellationToken).ConfigureAwait(false);
			await Console.Out.WriteLineAsync(summary).ConfigureAwait(false);
			return 0;
		}
		catch (OperationCanceledException)
		{
			await Console.Error.WriteLineAsync("Operation canceled.").ConfigureAwait(false);
			return 1;
		}
		catch (InvalidOperationException ex)
		{
			await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
			return 1;
		}
		catch (ArgumentException ex)
		{
			await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
			return 1;
		}
		catch (IOException ex)
		{
			await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
			return 1;
		}
		catch (XmlException ex)
		{
			await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
			return 1;
		}
	}
);

var parserConfiguration = new ParserConfiguration();
var invocationConfiguration = new InvocationConfiguration();
var rootParseResult = rootCommand.Parse(args, parserConfiguration);
return await rootParseResult.InvokeAsync(invocationConfiguration, CancellationToken.None).ConfigureAwait(false);

static async Task<string?> ReadInputTextAsync(string? text, CancellationToken cancellationToken)
{
	cancellationToken.ThrowIfCancellationRequested();

	if (string.IsNullOrWhiteSpace(text))
	{
		return null;
	}

	if (!string.Equals(text, "-", StringComparison.Ordinal))
	{
		return text;
	}

	return await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
}
