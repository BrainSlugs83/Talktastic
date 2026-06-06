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

var renameVoiceOption = new Option<string>("--rename-voice")
{
	Description = "Rename a downloaded Piper voice (old=new)",
};

var renameRvcOption = new Option<string>("--rename-rvc")
{
	Description = "Rename a downloaded RVC model (old=new)",
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
	renameVoiceOption,
	renameRvcOption,
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
				var renameVoice = parseResult.GetValue(renameVoiceOption);
				var renameRvc = parseResult.GetValue(renameRvcOption);
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

			// Management operations are mutually exclusive
			var managementOps = new[]
			{
				("--remove-voice", removeVoice),
				("--remove-rvc", removeRvc),
				("--rename-voice", renameVoice),
				("--rename-rvc", renameRvc),
			};
			var activeOps = managementOps.Where(op => !string.IsNullOrWhiteSpace(op.Item2)).ToArray();
			if (activeOps.Length > 1)
			{
				var names = string.Join(", ", activeOps.Select(op => op.Item1));
				await Console.Error.WriteLineAsync
				(
					$"{names} cannot be combined."
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

			if (!string.IsNullOrWhiteSpace(renameVoice))
			{
				return RenamePiperVoice(renameVoice);
			}

			if (!string.IsNullOrWhiteSpace(renameRvc))
			{
				return RenameRvcModel(renameRvc);
			}

			// --rvc with a URL but no text: just download/cache the model and exit
			if
			(
				!string.IsNullOrWhiteSpace(rvc)
				&& ModelDownloader.IsUrl(rvc)
				&& string.IsNullOrWhiteSpace(text)
			)
			{
				var (modelPath, _) = await RvcEngine.ResolveRvcModelAsync(rvc, cancellationToken).ConfigureAwait(false);
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

			// Resolve voice and RVC model once, up front
			var resolvedVoice = await VoiceEnumerator.ResolveVoiceAsync(voice, cancellationToken).ConfigureAwait(false);
			var resolvedRvc = !string.IsNullOrWhiteSpace(rvc)
				? await RvcEngine.ResolveRvcModelAsync(rvc, cancellationToken).ConfigureAwait(false)
				: ((string Path, string DisplayName)?)null;

			if (!quiet && !superQuiet)
			{
				var voiceLabel = resolvedVoice.VoiceType == VoiceType.Piper
					? resolvedVoice.LocalName
					: resolvedVoice.Name;

				var label = resolvedRvc is not null
					? $"Voice: {voiceLabel} → {resolvedRvc.Value.DisplayName}"
					: $"Voice: {voiceLabel}";

				await Console.Out.WriteLineAsync(label).ConfigureAwait(false);
			}

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

			var summary = await SpeechEngine.SynthesizeAsync(request, resolvedVoice, resolvedRvc, cancellationToken).ConfigureAwait(false);
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
	var match = ResolvePiperVoice(query);

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

	var voicesDir = Path.GetDirectoryName(match.PrimaryPath)!;
	var piperRoot = Path.GetDirectoryName(voicesDir)!;
	RemoveRegistryEntries(Path.Combine(piperRoot, "voices.json"), match.Name);
	return 0;
}

static int RemoveRvcModel(string query)
{
	var match = ResolveRvcModel(query);

	// Delete the entire model directory (subdirectory layout)
	var modelDir = Path.GetDirectoryName(match.PrimaryPath)!;
	var voicesDir = RvcEngine.FindVoicesDir()!;
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

static (string OldName, string NewName) ParseRenameArg(string arg)
{
	var eqIndex = arg.IndexOf('=', StringComparison.Ordinal);
	if (eqIndex < 0)
	{
		throw new InvalidOperationException
		(
			$"Rename argument must be in the form 'old=new', got '{arg}'."
		);
	}

	var oldName = arg[..eqIndex].Trim();
	var newName = arg[(eqIndex + 1)..].Trim();

	if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
	{
		throw new InvalidOperationException
		(
			$"Rename argument must be in the form 'old=new', got '{arg}'."
		);
	}

	return (oldName, newName);
}

static int RenamePiperVoice(string arg)
{
	var (oldQuery, newName) = ParseRenameArg(arg);
	var match = ResolvePiperVoice(oldQuery);

	var voicesDir = Path.GetDirectoryName(match.PrimaryPath)!;
	var newOnnxPath = Path.Combine(voicesDir, newName + ".onnx");

	if (File.Exists(newOnnxPath))
	{
		throw new InvalidOperationException($"A Piper voice named '{newName}' already exists.");
	}

	File.Move(match.PrimaryPath, newOnnxPath);

	var oldConfigPath = match.PrimaryPath + ".json";
	var newConfigPath = newOnnxPath + ".json";
	if (File.Exists(oldConfigPath))
	{
		File.Move(oldConfigPath, newConfigPath);
	}

	var piperRoot = Path.GetDirectoryName(voicesDir)!;
	RenameRegistryEntries(Path.Combine(piperRoot, "voices.json"), match.Name, newName);

	Console.Error.WriteLine($"Renamed Piper voice '{match.Name}' → '{newName}'.");
	return 0;
}

static int RenameRvcModel(string arg)
{
	var (oldQuery, newName) = ParseRenameArg(arg);
	var match = ResolveRvcModel(oldQuery);

	var modelDir = Path.GetDirectoryName(match.PrimaryPath)!;
	var voicesDir = RvcEngine.FindVoicesDir()!;
	var isSubDir = !string.Equals
	(
		Path.GetFullPath(modelDir),
		Path.GetFullPath(voicesDir),
		StringComparison.OrdinalIgnoreCase
	);

	if (isSubDir)
	{
		var newDir = Path.Combine(voicesDir, newName);
		if (Directory.Exists(newDir))
		{
			throw new InvalidOperationException($"An RVC model named '{newName}' already exists.");
		}

		Directory.Move(modelDir, newDir);
	}
	else
	{
		// Legacy flat file
		var ext = Path.GetExtension(match.PrimaryPath);
		var newPath = Path.Combine(voicesDir, newName + ext);
		if (File.Exists(newPath))
		{
			throw new InvalidOperationException($"An RVC model named '{newName}' already exists.");
		}

		File.Move(match.PrimaryPath, newPath);
	}

	var rvcRoot = Path.GetDirectoryName(voicesDir)!;
	RenameRegistryEntries(Path.Combine(rvcRoot, "rvcs.json"), match.Name, newName);

	Console.Error.WriteLine($"Renamed RVC model '{match.Name}' → '{newName}'.");
	return 0;
}

static void RenameRegistryEntries(string registryPath, string oldName, string newName)
{
	if (!File.Exists(registryPath))
	{
		return;
	}

	var lines = File.ReadAllLines(registryPath);
	var modified = false;

	for (var i = 0; i < lines.Length; i++)
	{
		var tab = lines[i].IndexOf('\t', StringComparison.Ordinal);
		if (tab < 0)
		{
			continue;
		}

		var entryModelName = lines[i][(tab + 1)..];
		if (string.Equals(entryModelName, oldName, StringComparison.OrdinalIgnoreCase))
		{
			lines[i] = lines[i][..(tab + 1)] + newName;
			modified = true;
		}
	}

	if (modified)
	{
		File.WriteAllLines(registryPath, lines);
	}
}

static CachedItem ResolvePiperVoice(string query)
{
	var voicesDir = PiperEngine.FindVoicesDir()
		?? throw new InvalidOperationException($"Unknown Piper voice '{query}'.");

	var candidates = PiperEngine.EnumerateCachedVoices(voicesDir)
		.Select(v => new CachedItem(v.OnnxPath))
		.ToArray();

	return FuzzyMatcher.FindBestMatch(candidates, query, static c => c.Name)
		?? throw new InvalidOperationException($"Unknown Piper voice '{query}'.");
}

static CachedItem ResolveRvcModel(string query)
{
	var voicesDir = RvcEngine.FindVoicesDir()
		?? throw new InvalidOperationException($"Unknown RVC model '{query}'.");

	var candidates = RvcEngine.EnumerateCachedModels(voicesDir)
		.Select(m => new CachedItem(m.Path, m.Name))
		.ToArray();

	return FuzzyMatcher.FindBestMatch(candidates, query, static c => c.Name)
		?? throw new InvalidOperationException($"Unknown RVC model '{query}'.");
}

file sealed record CachedItem(string PrimaryPath, string? DisplayName = null)
{
	public string Name { get; } = DisplayName ?? Path.GetFileNameWithoutExtension(PrimaryPath);

	public string FileName { get; } = Path.GetFileName(PrimaryPath);

	public long SizeBytes { get; } = new FileInfo(PrimaryPath).Length;
}
