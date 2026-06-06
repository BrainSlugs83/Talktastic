using Microsoft.CognitiveServices.Speech;
using System.CommandLine;
using System.CommandLine.Help;
using System.Reflection;
using System.Text;
using System.Xml;

using Talktastic;

Console.OutputEncoding = Encoding.UTF8;

var fullVersion = Assembly.GetEntryAssembly()
	?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
	?.InformationalVersion ?? "0.0.0";
var version = fullVersion.Split('+')[0];

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
	Description = "List voices, devices, Piper voices, and RVC models",
};

var listRvcsOption = new Option<bool>("--list-rvcs")
{
	Description = "List downloaded RVC voice conversion models",
};

var removeVoiceOption = new Option<string>("--remove-voice")
{
	Description = "Remove a downloaded Piper voice",
};

var removeRvcOption = new Option<string>("--remove-rvc")
{
	Description = "Remove a downloaded RVC model",
};

var rateOption = new Option<string>("--rate", "-r")
{
	Description = "Speaking rate adjustment",
};

var pitchOption = new Option<string>("--pitch", "-p")
{
	Description = "Pitch adjustment (e.g. high, low, +10%, -5st)",
};

var rvcOption = new Option<string>("--rvc")
{
	Description = "Apply RVC voice conversion (URL or local .onnx path)",
};

var rvcPitchOption = new Option<float>("--rvc-pitch")
{
	DefaultValueFactory = static _ => 0f,
	Description = "RVC pitch shift in semitones (e.g. +12 = octave up, -12 = octave down)",
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

var installVoicesOption = new Option<bool>("--add-voices")
{
	Description = "Open the Windows 'Add a voice' dialog",
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
	listRvcsOption,
	removeVoiceOption,
	removeRvcOption,
	rateOption,
	pitchOption,
	rvcOption,
	rvcPitchOption,
	formatOption,
	ssmlOption,
	helpSsmlOption,
	installVoicesOption,
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
			var listRvcs = parseResult.GetValue(listRvcsOption);
			var removeVoice = parseResult.GetValue(removeVoiceOption);
			var removeRvc = parseResult.GetValue(removeRvcOption);
			var rate = parseResult.GetValue(rateOption);
			var pitch = parseResult.GetValue(pitchOption);
			var rvc = parseResult.GetValue(rvcOption);
			var rvcPitch = parseResult.GetRequiredValue(rvcPitchOption);
			var format = parseResult.GetRequiredValue(formatOption);
			var ssml = parseResult.GetValue(ssmlOption);
			var helpSsml = parseResult.GetValue(helpSsmlOption);
			var installVoices = parseResult.GetValue(installVoicesOption);
			var quiet = parseResult.GetValue(quietOption);
			var superQuiet = parseResult.GetValue(superQuietOption);

			if (superQuiet)
			{
				Console.SetError(TextWriter.Null);
			}

			if (!string.IsNullOrWhiteSpace(removeVoice) && !string.IsNullOrWhiteSpace(removeRvc))
			{
				await Console.Error.WriteLineAsync
				(
					"--remove-voice and --remove-rvc cannot be combined."
				).ConfigureAwait(false);
				return 1;
			}

			if (!string.IsNullOrWhiteSpace(removeVoice))
			{
				return RemovePiperVoice(removeVoice);
			}

			if (!string.IsNullOrWhiteSpace(removeRvc))
			{
				return RemoveRvcModel(removeRvc);
			}

			// --rvc with a URL but no text: just download/cache the model and exit
			if
			(
				!string.IsNullOrWhiteSpace(rvc)
				&& ModelDownloader.IsUrl(rvc)
				&& string.IsNullOrWhiteSpace(text)
			)
			{
				var modelPath = await RvcEngine.ResolveRvcModelAsync(rvc, cancellationToken).ConfigureAwait(false);
				await Console.Error.WriteLineAsync
				(
					$"Cached: {modelPath}"
				).ConfigureAwait(false);
				return 0;
			}

			// -v with a URL but no text: just download/cache the Piper voice and exit
			if
			(
				!string.IsNullOrWhiteSpace(voice)
				&& ModelDownloader.IsUrl(voice)
				&& string.IsNullOrWhiteSpace(text)
			)
			{
				var modelPath = await PiperEngine.EnsureVoiceModelAsync(voice, cancellationToken).ConfigureAwait(false);
				await Console.Error.WriteLineAsync
				(
					$"Cached: {modelPath}"
				).ConfigureAwait(false);
				return 0;
			}

			if (!Enum.TryParse(format, ignoreCase: true, out SpeechSynthesisOutputFormat outputFormat))
			{
				await Console.Error.WriteLineAsync($"Unknown audio format '{format}'.").ConfigureAwait(false);
				return 1;
			}

			if (ssml && (!string.IsNullOrWhiteSpace(rate) || !string.IsNullOrWhiteSpace(pitch)))
			{
				await Console.Error.WriteLineAsync
				(
					"--ssml cannot be combined with --rate or --pitch. Use <prosody> in your SSML instead."
				).ConfigureAwait(false);
				return 1;
			}

			if (installVoices)
			{
				await Console.Out.WriteLineAsync
				(
					"Opening voice installer..."
				).ConfigureAwait(false);
				VoiceInstaller.OpenAddVoiceDialog();
				return 0;
			}

			if (helpSsml)
			{
				await Console.Out.WriteLineAsync
				(
"""
SSML (Speech Synthesis Markup Language) lets you control how text is spoken.
Pass --ssml to treat the input as SSML instead of plain text.

The <speak> wrapper is added automatically if missing, so you can use
bare SSML fragments directly.

Pauses:
  say --ssml "Taking a break <break time='500ms'/> and continuing."
  say --ssml "A longer pause <break time='2s'/> between sentences."

Speed, pitch, and volume (prosody):
  say --ssml "<prosody rate='slow'>This is spoken slowly.</prosody>"
  say --ssml "<prosody rate='fast' pitch='high'>Fast and high-pitched.</prosody>"
  say --ssml "<prosody volume='soft'>This is quieter.</prosody>"
  say --ssml "<prosody rate='+20%'>Twenty percent faster than normal.</prosody>"

Emphasis:
  say --ssml "This is <emphasis level='strong'>very important</emphasis>."

Phonemes (pronunciation override):
  say --ssml "<phoneme alphabet='ipa' ph='tɒmɑːtoʊ'>tomato</phoneme>"

Say-as (interpretation hints):
  say --ssml "<say-as interpret-as='characters'>SSML</say-as>"
  say --ssml "<say-as interpret-as='date' format='mdy'>3/14/2026</say-as>"
  say --ssml "<say-as interpret-as='telephone'>+1-555-0199</say-as>"

Substitution:
  say --ssml "<sub alias='World Wide Web Consortium'>W3C</sub>"

Full SSML documents with <speak> are also accepted:
  say --ssml "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis'
    xml:lang='en-US'><prosody rate='slow'>Hello.</prosody></speak>"

Notes:
  - Neural voices support all tags above; legacy voices have limited support.
  - The --rate option can be combined with --ssml to wrap everything in <prosody>.
"""
				).ConfigureAwait(false);
				return 0;
			}

			if (listAll || listVoices || listDevices || listRvcs)
			{
				var needSeparator = false;

				if (listAll || listVoices)
				{
					var voices = await VoiceEnumerator.GetVoicesAsync(cancellationToken).ConfigureAwait(false);
					await Console.Out.WriteLineAsync("Voices:").ConfigureAwait(false);
					foreach (var v in voices)
					{
							var tag = v.VoiceType switch
							{
								VoiceType.Neural => "neural",
								VoiceType.Legacy => "sapi",
								VoiceType.Piper => "piper",
								_ => "unknown",
							};

							var details = v.VoiceType == VoiceType.Piper
								? v.Locale
								: $"{v.Locale}, {v.Gender}";

							await Console.Out.WriteLineAsync($"  {v.Name} [{tag}] ({details})").ConfigureAwait(false);
						}

						needSeparator = true;
					}

				if (listAll || listRvcs)
				{
					if (needSeparator)
					{
						await Console.Out.WriteLineAsync().ConfigureAwait(false);
					}

					var rvcModels = RvcEngine.GetCachedModels();
					await Console.Out.WriteLineAsync("RVC models:").ConfigureAwait(false);
					if (rvcModels.Count > 0)
					{
						foreach (var (name, ext, sizeMb, hasIndex) in rvcModels)
						{
								var tag = hasIndex ? $"{ext}+idx" : ext;
								await Console.Out.WriteLineAsync($"  {name} [{tag}] ({sizeMb} MB)").ConfigureAwait(false);
							}
					}
					else
					{
						await Console.Out.WriteLineAsync("  (none downloaded)").ConfigureAwait(false);
					}

					needSeparator = true;
				}

				if (listAll || listDevices)
				{
					if (needSeparator)
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

			// Suppress stdout after help/list checks so -q doesn't hide help text
			if (quiet || superQuiet)
			{
				Console.SetOut(TextWriter.Null);
			}

			var request = new SynthesisRequest
			(
				Text: inputText,
				VoiceQuery: voice,
				OutputPath: output,
				DeviceQuery: device,
				Rate: rate,
					Pitch: pitch,
					RvcModel: rvc,
					RvcPitchShift: rvcPitch,
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

try
{
	return await rootParseResult.InvokeAsync(invocationConfiguration, CancellationToken.None).ConfigureAwait(false);
}
finally
{
	NativeExtractor.CleanupCwdExtractions();
}

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

static int RemovePiperVoice(string query)
{
	var voicesDir = PiperEngine.FindVoicesDir();
	if (voicesDir is null)
	{
		throw new InvalidOperationException($"Unknown Piper voice '{query}'.");
	}

	var candidates = PiperEngine.EnumerateCachedVoices(voicesDir)
		.Select(v => new CachedItem(v.OnnxPath))
		.ToArray();

	var match = FuzzyMatcher.FindBestMatch
	(
		candidates,
		query,
		static candidate => candidate.Name
	);

	if (match is null)
	{
		throw new InvalidOperationException($"Unknown Piper voice '{query}'.");
	}

	var configPath = match.PrimaryPath + ".json";
	var deletePaths = new List<string> { match.PrimaryPath };

	if (File.Exists(configPath))
	{
		deletePaths.Add(configPath);
	}

	var sizeMb = (int)(match.SizeBytes / 1024 / 1024);
	Console.Error.Write($"Remove Piper voice '{match.Name}' ({match.FileName}, {sizeMb} MB)? [y/N] ");
	var response = Console.In.ReadLine();
	if (!string.Equals(response, "y", StringComparison.OrdinalIgnoreCase))
	{
		return 0;
	}

	foreach (var path in deletePaths)
	{
		File.Delete(path);
	}

	var piperRoot = Path.GetDirectoryName(voicesDir)!;
	RemoveRegistryEntries(Path.Combine(piperRoot, "voices.json"), match.Name);
	return 0;
}

static int RemoveRvcModel(string query)
{
	var voicesDir = RvcEngine.FindVoicesDir();
	if (voicesDir is null)
	{
		throw new InvalidOperationException($"Unknown RVC model '{query}'.");
	}

	var candidates = RvcEngine.EnumerateCachedModels(voicesDir)
		.Select(m => new CachedItem(m.Path))
		.ToArray();

	var match = FuzzyMatcher.FindBestMatch
	(
		candidates,
		query,
		static candidate => candidate.Name
	);

	if (match is null)
	{
		throw new InvalidOperationException($"Unknown RVC model '{query}'.");
	}

	// Delete the entire model directory (subdirectory layout)
	var modelDir = Path.GetDirectoryName(match.PrimaryPath)!;
	var isSubDir = !string.Equals
	(
		Path.GetFullPath(modelDir),
		Path.GetFullPath(voicesDir),
		StringComparison.OrdinalIgnoreCase
	);

	var sizeMb = (int)(match.SizeBytes / 1024 / 1024);
	Console.Error.Write($"Remove RVC model '{match.Name}' ({match.FileName}, {sizeMb} MB)? [y/N] ");
	var response = Console.In.ReadLine();
	if (!string.Equals(response, "y", StringComparison.OrdinalIgnoreCase))
	{
		return 0;
	}

	if (isSubDir)
	{
		Directory.Delete(modelDir, recursive: true);
	}
	else
	{
		// Legacy flat file
		File.Delete(match.PrimaryPath);
	}

	var rvcRoot = Path.GetDirectoryName(voicesDir)!;
	RemoveRegistryEntries(Path.Combine(rvcRoot, "rvcs.json"), match.Name);
	return 0;
}

static void RemoveRegistryEntries(string registryPath, string modelName)
{
	if (!File.Exists(registryPath))
	{
		return;
	}

	var remainingLines = File.ReadAllLines(registryPath)
		.Where
		(
			line =>
			{
				var tab = line.IndexOf('\t', StringComparison.Ordinal);
				if (tab < 0)
				{
					return true;
				}

				var entryModelName = line[(tab + 1)..];
				return !string.Equals(entryModelName, modelName, StringComparison.OrdinalIgnoreCase);
			}
		)
		.ToArray();

	File.WriteAllLines(registryPath, remainingLines);
}

file sealed record CachedItem(string PrimaryPath)
{
	public string Name { get; } = Path.GetFileNameWithoutExtension(PrimaryPath);

	public string FileName { get; } = Path.GetFileName(PrimaryPath);

	public long SizeBytes { get; } = new FileInfo(PrimaryPath).Length;
}
