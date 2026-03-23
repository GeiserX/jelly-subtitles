<p align="center">
  <img src="docs/images/banner.svg" alt="JellySubtitles Banner" width="900"/>
</p>

<p align="center">
  <a href="https://github.com/GeiserX/jelly-subtitles/releases"><img src="https://img.shields.io/github/v/release/GeiserX/jelly-subtitles?style=flat-square&logo=github&color=6B4C9A" alt="Release"></a>
  <a href="https://github.com/GeiserX/jelly-subtitles/blob/main/LICENSE"><img src="https://img.shields.io/badge/license-GPL--3.0%20with%20Commons%20Clause-blue?style=flat-square" alt="License"></a>
  <img src="https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet&logoColor=white" alt=".NET 9.0">
  <img src="https://img.shields.io/badge/Jellyfin-10.11%2B-6B4C9A?style=flat-square" alt="Jellyfin 10.11+">
</p>

---

**JellySubtitles** is a Jellyfin plugin that automatically generates subtitles for your media library using local AI models. All transcription runs entirely on your server -- no audio data ever leaves your network. Your media stays private.

## Features

- **Fully Local Processing** -- Audio is transcribed on your hardware using [whisper.cpp](https://github.com/ggerganov/whisper.cpp). No cloud APIs, no external services, no data exfiltration.
- **Admin Dashboard UI** -- Browse libraries, view items, and trigger subtitle generation directly from the Jellyfin admin panel.
- **Scheduled Tasks** -- Enable automatic scanning so new media gets subtitles without manual intervention.
- **Pluggable Provider Architecture** -- Built around an `ISubtitleProvider` interface. Whisper is the default; additional providers (Parakeet, custom commands) can be added.
- **Per-Library Control** -- Choose which libraries are monitored for automatic subtitle generation.
- **SRT Output** -- Generates standard `.srt` subtitle files placed alongside your media, automatically picked up by Jellyfin.

## Prerequisites

| Dependency | Details |
|---|---|
| **Jellyfin** | 10.11.0 or later |
| **FFmpeg** | Bundled with Jellyfin (`/usr/lib/jellyfin-ffmpeg/ffmpeg`) or available in `PATH`. Used to extract audio from media files. |
| **whisper.cpp** | The `whisper-cli` (or `main`) binary must be installed and available in `PATH`. See [whisper.cpp releases](https://github.com/ggerganov/whisper.cpp/releases). |
| **Whisper Model** | A `.bin` model file (e.g., `ggml-base.bin`, `ggml-medium.bin`). Download from [Hugging Face](https://huggingface.co/ggerganov/whisper.cpp). |

## Installation

### From the Jellyfin Plugin Repository (Recommended)

1. In Jellyfin, go to **Dashboard** > **Plugins** > **Repositories**.
2. Add a new repository with this URL:
   ```
   https://geiserx.github.io/jelly-subtitles/manifest.json
   ```
3. Go to **Catalog**, find **JellySubtitles**, and click **Install**.
4. Restart Jellyfin.

### Manual Installation

1. Build from source:
   ```bash
   dotnet build --configuration Release
   ```
2. Copy `JellySubtitles.dll` to your Jellyfin plugins directory:
   ```
   /var/lib/jellyfin/plugins/JellySubtitles/
   ```
3. Restart Jellyfin.

## Configuration

After installation, navigate to **Dashboard** > **Plugins** > **JellySubtitles** to configure:

| Setting | Description |
|---|---|
| **Subtitle Provider** | The transcription engine to use. Currently `Whisper` is available. |
| **Whisper Model Path** | Absolute path to the `.bin` model file on the server (e.g., `/opt/whisper/models/ggml-medium.bin`). |
| **Enable Auto-Generation** | When enabled, the scheduled task will scan selected libraries and generate subtitles for items that lack them. |
| **Enabled Libraries** | Select which libraries should be monitored for automatic subtitle generation. |

## Usage

### Admin Dashboard

The plugin adds a dedicated page to the Jellyfin admin dashboard (accessible from the main menu). From there you can:

- Browse all libraries and their items.
- See which items already have subtitles.
- Trigger subtitle generation for individual items with a single click.

### REST API

All endpoints require Jellyfin admin authentication.

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/Plugins/JellySubtitles/Libraries` | List all media libraries |
| `GET` | `/Plugins/JellySubtitles/Libraries/{libraryId}/Items` | List items in a library (Movies, Episodes, Series) |
| `POST` | `/Plugins/JellySubtitles/Items/{itemId}/Generate?language=en` | Generate subtitles for a specific item |
| `GET` | `/Plugins/JellySubtitles/Items/{itemId}/Status` | Check subtitle generation status for an item |

### Scheduled Task

A scheduled task named **Generate Subtitles** is registered under the **JellySubtitles** category. It can be configured in **Dashboard** > **Scheduled Tasks** with your preferred schedule or triggered manually.

## How It Works

1. **Audio Extraction** -- FFmpeg extracts a 16 kHz mono WAV track from the media file.
2. **Transcription** -- The extracted audio is passed to whisper.cpp, which produces an SRT subtitle file.
3. **Output** -- The `.srt` file is saved alongside the original media (e.g., `Movie.en.generated.srt`).
4. **Metadata Refresh** -- The item's metadata is refreshed so Jellyfin picks up the new subtitle immediately.

Temporary audio files are cleaned up automatically after processing.

## Demos

- [Subtitle generation demo (live stream)](https://youtube.com/live/7pPMtL0e8eE)
- [Extended walkthrough (live stream)](https://youtube.com/live/9ns0jZ_VBC4)

## Roadmap

- [ ] **Parakeet provider** -- NVIDIA Parakeet integration for GPU-accelerated transcription.
- [ ] **Custom command provider** -- Define arbitrary CLI commands as transcription backends.
- [ ] **Language detection** -- Automatic source language detection before transcription.
- [ ] **Multi-language support** -- Generate subtitles in multiple languages per item.
- [ ] **Progress tracking** -- Real-time progress reporting in the admin UI during transcription.
- [ ] **Batch operations** -- Generate subtitles for entire libraries or filtered sets from the dashboard.

## License

This project is licensed under the **GNU General Public License v3.0** with the **Commons Clause** restriction. In short: you may use, modify, and redistribute this software freely, but you may not sell it or offer it as a paid service. See the [LICENSE](LICENSE) file for the full text.
