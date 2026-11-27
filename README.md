# WalkTalk

Offline speech-to-text for Windows. Speak, and WalkTalk types what you said into the text fields of other apps: a chat box, a browser, Notepad, VRChat's chatbox or Twitch chat. Speech recognition runs on your own PC with [Whisper](https://github.com/openai/whisper), on the GPU when there is one, and no audio ever leaves the computer.

WalkTalk can also learn your voice: read a set of sentences aloud and it retrains its speech model on your accent and the way you pronounce words.

## Features

- **Live dictation.** Each sentence is written out as soon as you pause, into every field you switched on.
- **Any app.** Click "Add field", then click a text box in any app. WalkTalk remembers it and writes there, optionally pressing Enter after each sentence.
- **VRChat chatbox.** Sends each sentence to VRChat through OSC (127.0.0.1:9000), with the typing bubble while you speak.
- **Twitch chat.** Connect your Twitch account (OAuth, in your browser) and each sentence is posted in the channel of your choice. The sign-in is stored encrypted for your Windows account only.
- **Voice commands.** Say "Clean" to empty the fields, or "Send" / "Enter" to press Enter.
- **Word hints.** Names and terms Whisper should spell exactly (usernames, games, jargon).
- **Word replacements.** Search the words the model knows and write something else in their place: an abbreviation, slang, an emoji.
- **OBS overlay.** Shows each sentence on your stream through an OBS Browser Source, styled your way: canvas size, font (any font on your PC, or search and install Google Fonts), size, color, letter case, alignment, line height, text shadow and outline, with a live preview.
- **Read aloud (TTS).** Optionally reads each sentence back with a natural Kokoro voice, also offline.
- **Voice training.** Retrains the model on your recordings so it understands your accent better (see below).
- **Dark and light themes.**

## Requirements

- Windows 10 (1809) or later, 64-bit
- A microphone
- Internet the first time, to download the speech model (about 1.6 GB). Dictation then works offline.
- Optional: a GPU with Vulkan support (NVIDIA, AMD or Intel) for fast transcription. Without one, WalkTalk runs on the CPU.
- Voice training only: an NVIDIA graphics card with at least 3.5 GB of memory and an up-to-date driver.

## Install

1. Download `WalkTalk-Setup-<version>.exe` from the [latest release](https://github.com/AnthonyArade/WalkTalk/releases/latest).
2. Run it. It installs for your Windows account only and needs no administrator rights.

The installer is not code-signed yet ([code signing policy](#code-signing-policy)), so Windows SmartScreen may warn "Windows protected your PC". Choose **More info**, then **Run anyway**.

Everything the app needs is in the installer. These larger parts download the first time they are needed:

| What | When | Size |
|---|---|---|
| Speech model (turbo) | first Start listening | about 1.6 GB |
| Text-to-speech voices | first time TTS is turned on | 334 MB |
| Training tools (a private Python with PyTorch) | first voice training | about 5 GB |

Models, settings and recordings are kept in `%LOCALAPPDATA%\WhisperStt`. Uninstalling asks whether to delete them.

## Getting started

1. Pick your microphone, a model and your language in the toolbar.
2. Click **Add field**, then click the text box you want to write into, in any app.
3. Click **Start listening** and speak. Pause briefly at the end of each sentence.

Click **Stop** when you are done: the model is unloaded to free the graphics card for games and other apps.

### OBS overlay

1. Click **OBS** next to the theme button and turn the canvas on.
2. In OBS: **Sources**, **+**, **Browser**. Paste the address shown in WalkTalk (`http://127.0.0.1:7480/`) and set Width and Height to the canvas size.
3. Style the text in WalkTalk; the preview and OBS update as you change it.

The overlay is served from your own PC only (127.0.0.1). A Google font must be installed in WalkTalk to appear in OBS; installed fonts are kept, so the overlay also works offline.

### Models

| Model | Size | Notes |
|---|---|---|
| tiny, base | 75 to 150 MB | fastest, least accurate |
| small | 490 MB | a good choice on the CPU |
| medium | 1.5 GB | |
| turbo | 1.6 GB | the default: fast on a GPU and accurate |
| large | 3.1 GB | most accurate, slowest |
| my voice | | appears once you have trained a voice model |

## Voice training

Voice training adapts the speech model to how *you* speak, which helps most with accents and words you pronounce your own way.

1. Open **Voice training** (stop listening first).
2. Choose your language and how many sentences to use (20% to 100%, with the estimated training time for each).
3. Read the sentences aloud. You can stop and continue later. You can also add sentences of your own, with names and words you really say.
4. Click **Train my voice model**. A full training takes about 10 minutes on an 8 GB RTX 3050.
5. WalkTalk shows the standard model and your trained model side by side on sentences kept apart for the test. Use the new model only if you like the result.

**How it works:** WalkTalk trains small LoRA adapters on Whisper's attention layers with your recordings, then merges them into a normal Whisper model file. One sentence in five is held back first to find how long to train before the model starts memorising; then it trains again on every recording.

**Fixing mistakes as you go:** while you dictate with your voice model, the ten latest sentences show a **Review** button. If one was written wrong, press it, type what you actually said and save. Then press **Teach my voice model**: a short training of a minute or two adds the correction to your model. For a word that keeps coming out wrong, correct it a few times; each recording is a little different, and that is what teaches the model the word.

Everything stays on your PC: recordings, training and the resulting model.

## Building from source

Requirements:

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php), for the installer only (`winget install JRSoftware.InnoSetup`)

```powershell
# Build everything
dotnet build WhisperStt.slnx -c Release

# Run the app
dotnet run --project src\WhisperStt.Desktop

# Build the installer: publishes into publish\app, then creates publish\WalkTalk-Setup-<version>.exe
.\build-installer.ps1
```

### Project layout

| Folder | What it is |
|---|---|
| `src/WhisperStt.Core` | Speech recognition, microphone, voice detection, models, voice training, VRChat and Twitch |
| `src/WhisperStt.Desktop` | The WPF app (WalkTalk) |
| `src/WhisperStt.Cli` | A small command-line tool: transcribe a file or record from the microphone |
| `tests/UiChecks` | Drives the real window off-screen on a sandbox copy of the data, and saves screenshots |
| `tests/ModelChecks` | Compares how models write one recording and altered copies of it |
| `installer` | The Inno Setup script |

The test tools never touch your real settings or recordings: they work on folders you give them. See the top of each `Program.cs` for usage.

### Built with

- [Whisper.net](https://github.com/sandrohanea/whisper.net) (whisper.cpp) with the Vulkan runtime, and the Silero voice detector
- [NAudio](https://github.com/naudio/NAudio) for the microphone
- [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) with [Kokoro](https://huggingface.co/hexgrad/Kokoro-82M) for text-to-speech
- [PyTorch](https://pytorch.org) and [openai-whisper](https://github.com/openai/whisper) for voice training

## Privacy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

- Speech recognition, text-to-speech and voice training run entirely on your PC. No audio ever leaves it.
- The text of a sentence is sent only to the destinations you switch on: Twitch chat (to Twitch, once you connect your account), VRChat (to the game on your own PC) and the OBS overlay (served on your own PC only).
- The other connections are downloads you start: the speech model (from Hugging Face, through Whisper.net), the text-to-speech voices (from GitHub), the voice-training tools (from nuget.org and pytorch.org), and Google Fonts when you search for or install one (from fonts.google.com).

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- Committers and reviewers: [AnthonyArade](https://github.com/AnthonyArade)
- Approvers: [AnthonyArade](https://github.com/AnthonyArade)

Releases are built from this repository by [GitHub Actions](.github/workflows/build.yml), and every signing request is approved by hand.

## License

[MIT](LICENSE)

## Known limitations

- Windows only.
- Voice training needs an NVIDIA graphics card.
- Releases are not code-signed yet (see [Code signing policy](#code-signing-policy)), so SmartScreen or Smart App Control may warn about or block them.
- A voice command only works when said on its own: pause briefly before and after it.
