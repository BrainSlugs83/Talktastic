# Talktastic

Standalone Windows TTS command-line tool. Speaks text using Windows' built-in
neural voices (Ava, Aria, Sonia, etc.) and legacy SAPI voices (David, Mark,
Zira) -- no cloud, no API keys, no installation required.

Ships as a single NativeAOT-compiled `say.exe` (~20 MB) with all native
dependencies embedded and extracted on first run.

## Usage

```
say "Hello, world!"                          # speak with default voice
say "Hello" -v Ava                           # pick a voice (substring match)
say "Hello" -v Sonia -o greeting.wav         # save to WAV
say "Hello" -v Ava -o greeting.mp3           # save to MP3
say "Hello" -d "Realtek"                     # target a specific audio device
echo "piped text" | say -                    # read from stdin
say --list                                   # list voices and devices
say --list-voices                            # list voices only
say --list-devices                           # list devices only
say --help-ssml                              # SSML quick reference
say --ssml "<speak>...</speak>"              # speak SSML directly
```

## Default Voice

When no `--voice` is specified, Talktastic uses your current **Narrator voice**
(Settings → Accessibility → Narrator → Voice). If that can't be read, it falls
back to the system default speech voice.

## Voice Matching

Voice and device names support **case-insensitive substring matching**. If
multiple voices match, neural voices are preferred over legacy ones.

```
say -v ava "Hi"      # matches "Microsoft Ava (Natural HD) - English (US)"
say -v david "Hi"    # matches "Microsoft David" (legacy)
say -d realtek "Hi"  # matches "Speakers (Realtek(R) Audio)"
```

## Building

Requires .NET 10 SDK.

```powershell
# Debug build (uses NuGet native DLLs directly)
dotnet build

# AOT publish -- produces single say.exe with embedded native DLLs
dotnet publish -c Release -r win-x64 /p:PublishAot=true

# Copy to dist/
copy bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\say.exe dist\say.exe
```

The build automatically:
1. Locates Speech SDK native DLLs from NuGet cache
2. Gzip-compresses them via `build-resources.ps1`
3. Embeds them as assembly resources
4. Excludes the loose DLLs from publish output

At runtime, `say.exe` extracts the compressed DLLs to
`%LOCALAPPDATA%\Talktastic\native\` (validated by MD5 + file size on each run).

## How It Works

- **Neural voices**: Uses the Azure Embedded Speech SDK to synthesize via the
  same voice models that ship with Windows (MSIX packages under
  `MicrosoftWindows.Voice.*`). The license (EULA text) is extracted at runtime
  from `SpeechSynthesizerExtension.dll` -- nothing is hardcoded.
- **Legacy voices**: Uses the WinRT `SpeechSynthesizer` API for classic SAPI
  voices (David, Mark, Zira).
- **MP3 encoding**: Direct P/Invoke to `libmp3lame.dll` (embedded alongside
  the Speech SDK DLLs). No managed wrappers or reflection.
- **Device routing**: WinRT `MediaPlayer` with `AudioDevice` property for
  targeted playback. Default speaker uses the Speech SDK's native output path
  (zero-copy, no buffering).

## Prior Art

Inspired by [NaturalVoiceSAPIAdapter](https://github.com/gexgd0419/NaturalVoiceSAPIAdapter),
which exposes Windows neural voices as SAPI5 COM objects. Talktastic takes a
different approach -- standalone CLI, no COM registration, no admin required.

## License

For educational purposes.
