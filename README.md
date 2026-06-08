# Talktastic

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![NativeAOT](https://img.shields.io/badge/NativeAOT-Windows%20CLI-5C2D91)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)

Standalone Windows TTS CLI that speaks with neural, SAPI, and Piper voices -- with optional RVC voice conversion -- compiled to a single NativeAOT executable. No runtime, no installers, just `say.exe`.

## Usage

```powershell
# Speak with the default voice
say "Hello from Talktastic"

# Use a Windows neural voice
say "Good evening." -v "Microsoft Ryan"

# Save to a file -- extension picks the encoder
say "Hello" -v "Microsoft Aria" -o hello.mp3

# Download a Piper voice by URL -- cached automatically for next time
say "Hello" -v "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/amy/medium/en_US-amy-medium.onnx"

# Or use the shorthand
say "Hello" -v "piper:en_US-ryan-high"

# Apply RVC voice conversion on top of TTS
say "Hello" -v "Microsoft Aria" --rvc "https://huggingface.co/Sunwest/Homer_Simpson_300"

# Convert an existing audio file through RVC (no TTS)
say --in dry.wav --rvc Homer --rvc-pitch 12 -o wet.ogg

# Read from stdin
echo "Hello from a pipe" | say -

# List everything -- voices, RVC models, audio devices
say --list
```

## Features

- **Neural voices** -- Windows Embedded Speech SDK neural voices with full SSML support
- **SAPI / legacy voices** -- classic Windows voices via WinRT
- **Piper voices** -- download and run open-source ONNX TTS models in-process (via sherpa-onnx)
- **RVC voice conversion** -- `.onnx` and `.pth` models with FAISS index support, DirectML GPU acceleration
- **Multiple output formats** -- play to any audio device, or write `.wav`, `.mp3`, `.ogg` (Opus)
- **Zero dependencies** -- NativeAOT single-file binary with embedded native DLLs, extracted and cached at runtime
- **Fuzzy matching** -- voice names are case-insensitive partial matches; type prefixes like `neural:`, `sapi:`, `piper:` narrow the search
- **Model management** -- `--list`, `--rename-voice`, `--rename-rvc`, `--remove-voice`, `--remove-rvc`
- **SSML** -- `--ssml` flag, `--help-ssml` for examples, `--rate`/`--pitch` shortcuts

## Architecture

### High-level pipeline

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

### Voice resolution

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

### RVC pipeline

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

### Native DLL extraction

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

### Module dependency graph

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

## Voice Types

| Voice type | Backing technology | Discovery source | Notes |
|---|---|---|---|
| Neural | Microsoft Embedded Speech SDK | Installed `MicrosoftWindows.Voice.*` packages | Best SSML support; output format comes from `--format`. |
| SAPI / Legacy | WinRT `SpeechSynthesizer` voice inventory | `SpeechSynthesizer.AllVoices` | Covers classic Windows voices such as David/Mark/Zira-style voices. |
| Piper | Local ONNX model files | `.piper-tts\voices` cache | User-facing local models; downloaded on demand and synthesized in-process. |
| Sherpa runtime | `org.k2fsa.sherpa.onnx` | Not a separate voice catalog | Execution backend for Piper models, plus token/espeak/metadata compatibility work. |

> Note: `VoiceEnumerator` exposes user-selectable voice families as Neural, Legacy, and Piper. sherpa-onnx is the Piper execution backend, not a separate enumerated voice inventory.

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
dotnet run -- "Hello from source"

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

## License

MIT
