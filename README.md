# Talktastic

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![NativeAOT](https://img.shields.io/badge/NativeAOT-Windows%20CLI-5C2D91)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)

Standalone Windows TTS CLI that speaks with neural, SAPI, and Piper voices -- with optional RVC voice conversion -- compiled to a single NativeAOT executable. No runtime, no installers, just `say.exe`.

## Features

- **Neural voices** -- Windows Embedded Speech SDK neural voices with full SSML support
- **SAPI / legacy voices** -- classic Windows voices via WinRT
- **Piper voices** -- download and run open-source ONNX TTS models in-process (via sherpa-onnx)
- **RVC voice conversion** -- `.onnx` and `.pth` models with FAISS index support, DirectML GPU acceleration
- **Multiple output formats** -- play to any audio device, or write `.wav`, `.mp3`, `.ogg` (Opus)
- **Multiple input formats** -- import `.wav`, `.mp3`, or `.ogg` for RVC-only processing
- **Zero dependencies** -- NativeAOT single-file binary with embedded native DLLs, extracted and cached at runtime
- **Fuzzy matching** -- voice names are case-insensitive partial matches; type prefixes like `neural:`, `sapi:`, `piper:` narrow the search
- **Model management** -- `--list`, `--rename-voice`, `--rename-rvc`, `--remove-voice`, `--remove-rvc`
- **SSML** -- `--ssml` flag, `--help-ssml` for examples, `--rate`/`--pitch` shortcuts

## Usage

```powershell
# Speak with the voice, currently configured as the Default, in Windows.
say "Hello, from Talktastic!"

# Use a Windows 11 neural voice (make sure to install it first; e.g via --add-voices)
say "This is Microsoft Ryan, a Windows 11 neural voice." -v "Microsoft Ryan"

# Download and use an Open Source Piper voice by URL (cached automatically).
say "Piper voices can be downloaded by URL!" -v "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/amy/medium/en_US-amy-medium.onnx"

# Silently download and install a Piper voice (no text = no speech).
say -v "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/ryan/high/en_US-ryan-high.onnx"

# Once a Piper voice is installed, you can use it by name.
say "Once a Piper voice is installed, you can use it by name!" -v "Amy"

# Use type prefixes to disambiguate voices with the same name.
say "This is how you disambiguate between two voices with the same name." -v "piper:Ryan"

# Apply RVC voice conversion on top of any TTS voice.
say "D'oh! This is Piper Ryan's voice, converted to sound like Homer Simpson." -v "piper:Ryan" --rvc "Homer"

# RVC models can be downloaded by URL too.
say "RVC models can also be downloaded by URL." --rvc "https://huggingface.co/Sunwest/Homer_Simpson_300"

# Read from stdin.
echo "You can also pipe text in from stdin!" | say "-"

# List everything -- voices, RVC models, audio devices.
say --list
```

## File I/O

Export to `.wav`, `.mp3`, or `.ogg` -- the extension picks the encoder:

```powershell
say "This will be saved as a WAV file." -o hello.wav
say "You can export to MP3 as well!" -v "Microsoft Aria" -o hello.mp3
say "And OGG Opus, if you fancy." -v "Alan" -o hello.ogg
```

Import `.wav`, `.mp3`, or `.ogg` files to run through RVC without doing TTS:

```powershell
say --in recording.wav --rvc "Homer" -o converted.wav
say --in podcast.mp3 --rvc "Homer" -o converted.mp3
say --in clip.ogg --rvc "Homer" -o converted.ogg
```

Mix and match any input format with any output format:

```powershell
say --in recording.wav --rvc "Homer" -o converted.ogg
say --in podcast.mp3 --rvc "Homer" -o converted.wav
```

## Advanced Usage

```powershell
# SSML for fine-grained speech control
say --ssml "<prosody rate='slow' pitch='-10%'>SSML lets you control rate, pitch, and emphasis.</prosody>"

# Rate and pitch shortcuts (neural and SAPI voices)
say "Rate and pitch can also be set with shorthand flags." -v "Microsoft Aria" --rate fast --pitch high

# RVC pitch shifting (semitones: +12 = octave up, -12 = octave down)
say --in vocals.wav --rvc "Homer" --rvc-pitch 12 -o octave-up.wav

# TTS + RVC + file output -- the full pipeline
say "This runs the full pipeline: TTS, then RVC, then file export." -v "Microsoft Ryan" --rvc "Homer" -o result.mp3

# Play to a specific audio device
say "You can route audio to a specific output device." -v "Microsoft Ryan" -d "Speakers (Realtek)"

# Disable GPU acceleration (CPU-only RVC)
say --in input.wav --rvc "Homer" --no-gpu -o output.wav

# Show RVC pipeline timing
say "The perf flag shows RVC pipeline timing." --rvc "Homer" --perf

# Voice type prefixes narrow the search
say "Type prefixes let you pick exactly which engine to use." -v "neural:Aria"
say "This forces the SAPI engine." -v "sapi:David"

# Manage cached models
say --list-voices
say --list-rvcs
say --rename-voice "en_US-amy-medium=Amy"
say --rename-rvc "Homer=Homer Simpson"
say --remove-voice "Amy"
say --remove-rvc "Homer Simpson"
```

## Voice Types

| Voice type | Backing technology | Discovery source | Notes |
|---|---|---|---|
| Neural | Microsoft Embedded Speech SDK (Windows 11) | Installed `MicrosoftWindows.Voice.*` packages | Full SSML support; output format comes from `--format`. Requires Windows 11 neural voice packages. |
| SAPI / Legacy | WinRT `SpeechSynthesizer` (Windows 10+) | `SpeechSynthesizer.AllVoices` | Classic Windows voices (David, Mark, Zira, etc.). Limited SSML support -- most prosody tags are ignored. |
| Piper | Open-source neural TTS via local ONNX models | `.piper-tts\voices` cache | Downloaded on demand from any URL and synthesized in-process via sherpa-onnx. Supports global rate and pitch controls. |

## CLI Reference

```text
say [<text>] [options]
```

`<text>` is optional. Use `-` to read from stdin.

### Options

| Option | Meaning |
|---|---|
| `-v, --voice <voice>` | Voice name, partial match, case-insensitive. Supports prefixes like `piper:` and URL downloads. |
| `-o, --output <path>` | Output file path. Encoder chosen by extension: `.wav`, `.mp3`, `.ogg`. |
| `-d, --device <device>` | Output device name for playback. |
| `-i, --in <file>` | Process an existing `.wav`, `.mp3`, or `.ogg` through RVC (no TTS). |
| `--rvc <model>` | Apply RVC conversion using a local path, cached name, or URL. |
| `--rvc-pitch <semitones>` | Shift the RVC input pitch before conversion. |
| `-r, --rate <rate>` | Speech rate adjustment. |
| `-p, --pitch <pitch>` | Pitch adjustment (`high`, `low`, `+10%`, `-5st`, etc.). |
| `-f, --format <format>` | Speech SDK output format (neural voices). |
| `--ssml` | Treat input as SSML. |
| `--help-ssml` | Print SSML usage examples. |
| `-l, --list` | List voices, RVC models, and audio devices. |
| `--list-voices` | List voices only. |
| `--list-devices` | List output devices only. |
| `--list-rvcs` | List cached RVC models only. |
| `--remove-voice <name>` | Remove a cached Piper voice. |
| `--remove-rvc <name>` | Remove a cached RVC model. |
| `--rename-voice old=new` | Rename a cached Piper voice. |
| `--rename-rvc old=new` | Rename a cached RVC model. |
| `--add-voices` | Open the Windows "Add a voice" dialog. |
| `-q, --quiet` | Suppress stdout. |
| `-Q, --super-quiet` | Suppress stdout and stderr. |
| `--no-gpu` | Disable DirectML, fall back to CPU. |
| `--perf` | Emit timing data. |
| `-h, --help` | Show help. |
| `--version` | Show version. |

## Building

### Prerequisites

- Windows
- .NET 10 SDK
- NativeAOT publishing prerequisites for `win-x64` if you want the standalone single-file executable

### Build commands

```powershell
# Regular build
dotnet build

# Run directly from the build output
dotnet run -- "Hello, from source!"

# NativeAOT publish
dotnet publish -c Release -r win-x64 /p:PublishAot=true
```

The project file is configured to:

- build the executable as `say`
- gather native speech / ONNX / LAME assets via `build-resources.ps1`
- embed compressed native payloads and RVC skeleton models as resources
- exclude loose native DLLs from publish output when publishing AOT

At runtime, `NativeExtractor` validates or extracts the embedded native DLLs and configures the process to load them from the local cache.

## Testing

Run the standard test suite:

```powershell
dotnet test --nologo
```

Or use the repository test script for coverage reporting:

```powershell
pwsh .\test.ps1
pwsh .\test.ps1 -Html
```

The test suite covers:

- CLI behavior
- voice enumeration and fuzzy matching
- Piper and sherpa compatibility helpers
- RVC model loading, FAISS parsing, and `.pth` patching
- audio DSP and output encoding
- native resource extraction

## Architecture

<details>
<summary>High-level pipeline</summary>

```mermaid
graph TD
    CLI["CLI / Program.cs"] --> VR["Voice resolution"]
    VR --> TSEL["TTS engine selector"]

    subgraph TTS["TTS engines"]
        N["Neural<br/>Embedded Speech SDK"]
        S["SAPI / WinRT legacy"]
        P["Piper ONNX"]
        SH["sherpa-onnx runtime"]
        P --> SH
    end

    TSEL --> N
    TSEL --> S
    TSEL --> P

    N --> MIX["Synthesized WAV/audio bytes"]
    S --> MIX
    SH --> MIX

    MIX --> RVCQ{"RVC enabled?"}
    RVCQ -- No --> ROUTE["Output routing"]
    RVCQ -- Yes --> RVC["RVC conversion pipeline"]
    RVC --> ROUTE

    subgraph OUT["Audio output"]
        DEV["Playback device"]
        WAV["WAV file"]
        MP3["MP3 file"]
        OGG["OGG Opus file"]
    end

    ROUTE --> DEV
    ROUTE --> WAV
    ROUTE --> MP3
    ROUTE --> OGG
```

</details>

<details>
<summary>Voice resolution</summary>

```mermaid
graph TD
    IN["Input voice query"] --> EMPTY{"Query provided?"}
    EMPTY -- No --> DEFAULT["Resolve default voice"]
    EMPTY -- Yes --> PREFIX{"Type prefix?"}
    PREFIX -- "neural:/sapi:/legacy:/piper:" --> FILTER["Apply type filter"]
    PREFIX -- "No prefix" --> CLEAN["Use unified voice pool"]
    FILTER --> EXACT{"Exact match?"}
    CLEAN --> EXACT
    EXACT -- Yes --> MATCH["Resolved voice"]
    EXACT -- No --> FUZZY{"Fuzzy match?"}
    FUZZY -- Yes --> MATCH
    FUZZY -- No --> DL{"Piper download candidate?"}
    DL -- Yes --> CACHE["Download/cache Piper model"]
    CACHE --> MATCH
    DL -- No --> FAIL["No match"]
    DEFAULT --> MATCH
```

</details>

<details>
<summary>RVC pipeline</summary>

```mermaid
graph TD
    IN["Input WAV/audio"] --> PRE["Decode + mono + 16 kHz resample"]
    PRE --> HP["High-pass + padding + segmentation"]
    HP --> F0["F0 extraction (RMVPE)"]
    HP --> CV["ContentVec feature extraction"]
    CV --> FAISS{"FAISS index present?"}
    FAISS -- Yes --> BLEND["Blend retrieved speaker features"]
    FAISS -- No --> FEAT["Use raw ContentVec features"]
    BLEND --> INF["RVC ONNX inference"]
    FEAT --> INF
    F0 --> INF
    INF --> POST["RMS match + normalization + trim"]
    POST --> OUT["Converted WAV output"]
```

</details>

<details>
<summary>Native DLL extraction</summary>

```mermaid
sequenceDiagram
    participant App as say.exe
    participant NE as NativeExtractor
    participant Cache as Local cache
    participant Res as Embedded resources

    App->>NE: EnsureAvailable(group)
    NE->>Cache: Check validated DLL copy
    alt Cache hit
        Cache-->>NE: Existing DLL path
    else Cache miss or stale
        NE->>Res: Read compressed embedded DLL
        Res-->>NE: GZip payload + manifest metadata
        NE->>Cache: Extract and validate
    end
    NE-->>App: Configure load/search path
```

</details>

<details>
<summary>Module dependency graph</summary>

```mermaid
graph TD
    Program["Program.cs"] --> VoiceEnumerator["VoiceEnumerator.cs"]
    Program --> SpeechEngine["SpeechEngine.cs"]
    Program --> AudioOutput["AudioOutput.cs"]
    Program --> RvcEngine["RvcEngine.cs"]

    VoiceEnumerator --> PiperEngine["PiperEngine.cs"]
    VoiceEnumerator --> NativeExtractor["NativeExtractor.cs"]
    VoiceEnumerator --> FuzzyMatcher["FuzzyMatcher.cs"]
    VoiceEnumerator --> AppPaths["AppPaths.cs"]

    SpeechEngine --> AudioOutput
    SpeechEngine --> PiperEngine
    SpeechEngine --> RvcEngine
    SpeechEngine --> NativeExtractor

    PiperEngine --> SherpaEngine["SherpaEngine.cs"]
    PiperEngine --> ModelDownloader["ModelDownloader.cs"]
    PiperEngine --> AppPaths

    SherpaEngine --> NativeExtractor
    SherpaEngine --> AppPaths
    SherpaEngine --> OnnxPatcher["OnnxPatcher.cs"]

    RvcEngine --> AudioDsp["AudioDsp.cs"]
    RvcEngine --> ModelDownloader
    RvcEngine --> NativeExtractor
    RvcEngine --> PthLoader["PthLoader.cs"]
    RvcEngine --> OnnxPatcher
    RvcEngine --> FaissIndex["FaissIndex.cs"]
    RvcEngine --> AppPaths
```

</details>

## License

MIT
