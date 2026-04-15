<p align="center">
  <img src="docs/images/banner.svg" alt="WhisperSubs Banner" width="900"/>
</p>

<p align="center">
  <a href="https://github.com/GeiserX/whisper-subs/releases"><img src="https://img.shields.io/github/v/release/GeiserX/whisper-subs?style=flat-square&logo=github&color=6B4C9A" alt="Release"></a>
  <a href="https://github.com/GeiserX/whisper-subs/actions/workflows/build-release.yml"><img src="https://img.shields.io/github/actions/workflow/status/GeiserX/whisper-subs/build-release.yml?branch=main&style=flat-square&label=tests" alt="Tests"></a>
  <a href="https://github.com/GeiserX/whisper-subs/blob/main/LICENSE"><img src="https://img.shields.io/badge/license-GPL--3.0-blue?style=flat-square" alt="License"></a>
  <img src="https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet&logoColor=white" alt=".NET 9.0">
  <img src="https://img.shields.io/badge/Jellyfin-10.11%2B-6B4C9A?style=flat-square" alt="Jellyfin 10.11+">
  <a href="https://github.com/awesome-jellyfin/awesome-jellyfin#readme"><img src="https://img.shields.io/badge/listed%20on-awesome--jellyfin-00a4dc?style=flat-square&logo=jellyfin&logoColor=white" alt="listed on awesome-jellyfin"></a>
</p>

---

**WhisperSubs** is a Jellyfin plugin that automatically generates subtitles for your media library using local AI models. All transcription runs entirely on your server -- no audio data ever leaves your network. Your media stays private.

## Features

- **Fully Local Processing** -- Audio is transcribed on your hardware using [whisper.cpp](https://github.com/ggerganov/whisper.cpp). No cloud APIs, no external services, no data exfiltration.
- **Automatic Language Detection** -- Reads audio stream metadata to detect the spoken language and generate matching subtitles. Falls back to whisper's built-in language detection when tags are absent.
- **GPU Acceleration** -- Supports Vulkan (Intel / AMD) and CUDA (NVIDIA) for significantly faster transcription.
- **Admin Dashboard UI** -- Browse libraries, view items, and trigger subtitle generation directly from the Jellyfin admin panel.
- **Scheduled Tasks** -- Enable automatic scanning so new media gets subtitles without manual intervention.
- **Pluggable Provider Architecture** -- Built around an `ISubtitleProvider` interface. Whisper is the default; additional providers can be added.
- **Per-Library Control** -- Choose which libraries are monitored for automatic subtitle generation.
- **SRT Output** -- Generates standard `.srt` subtitle files placed alongside your media, automatically picked up by Jellyfin.

## Prerequisites

| Dependency | Details |
|---|---|
| **Jellyfin** | 10.11.0 or later |
| **FFmpeg** | Bundled with Jellyfin (`/usr/lib/jellyfin-ffmpeg/ffmpeg`) or available in `PATH`. Used to extract audio from media files. |
| **whisper.cpp** | The `whisper-cli` binary. Either in `PATH` or configured via the plugin's **Whisper Binary Path** setting. See [Installing whisper.cpp](#installing-whispercpp) below. |
| **Whisper Model** | A GGML model file (e.g., `ggml-base.bin`, `ggml-large-v3-turbo.bin`). Download from [Hugging Face](https://huggingface.co/ggerganov/whisper.cpp). |

## Installation

### From the Jellyfin Plugin Repository (Recommended)

1. In Jellyfin, go to **Dashboard** > **Plugins** > **Repositories**.
2. Add a new repository with this URL:
   ```
   https://geiserx.github.io/whisper-subs/manifest.json
   ```
3. Go to **Catalog**, find **WhisperSubs**, and click **Install**.
4. Restart Jellyfin.

### Manual Installation

1. Build from source:
   ```bash
   dotnet build --configuration Release
   ```
2. Copy `WhisperSubs.dll` to your Jellyfin plugins directory:
   ```
   /var/lib/jellyfin/plugins/WhisperSubs/
   ```
3. Restart Jellyfin.

## Installing whisper.cpp

The plugin requires whisper.cpp for transcription. Choose the method that matches your setup.

### Option A: Pre-built Binary (Recommended for most users)

1. Download the latest release for your platform from [whisper.cpp releases](https://github.com/ggerganov/whisper.cpp/releases).
2. Extract and place the `whisper-cli` binary somewhere persistent (e.g., `/opt/whisper/`).
3. Download a model:
   ```bash
   mkdir -p /opt/whisper/models

   # Base model (~148 MB) -- fast, good for quick transcription
   wget -O /opt/whisper/models/ggml-base.bin \
     https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin

   # Large V3 Turbo (~1.6 GB) -- best accuracy with reasonable speed (recommended)
   wget -O /opt/whisper/models/ggml-large-v3-turbo.bin \
     https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin
   ```
4. In the plugin settings, set **Whisper Binary Path** to `/opt/whisper/whisper-cli` and **Whisper Model Path** to the model file.

### Option B: Build from Source (CPU only)

```bash
git clone https://github.com/ggerganov/whisper.cpp.git
cd whisper.cpp
cmake -B build -DBUILD_SHARED_LIBS=OFF
cmake --build build --config Release -j$(nproc)
# Binary will be at build/bin/whisper-cli
```

### Option C: Build from Source with GPU Acceleration

See [GPU Acceleration](#gpu-acceleration) below for detailed instructions.

### Docker / Container Setups

If Jellyfin runs in a Docker container, whisper.cpp must be accessible **inside** the container. The recommended approach is to bind-mount a host directory containing the binary and model:

```yaml
# docker-compose.yml
services:
  jellyfin:
    image: jellyfin/jellyfin
    volumes:
      - /opt/whisper:/opt/whisper:ro   # whisper-cli binary + models
      # ... your other volumes
```

Then configure the plugin with:
- **Whisper Binary Path**: `/opt/whisper/whisper-cli`
- **Whisper Model Path**: `/opt/whisper/models/ggml-large-v3-turbo.bin`

> **Note:** The binary must be compiled for the same architecture as the container (typically x86_64 Linux). Download the `linux-x64` release asset or build inside a matching environment.

#### <a id="container-setup"></a>Container Library Requirements

The plugin's built-in binary downloader fetches pre-built whisper-cli binaries. These require runtime libraries that are **not included** in the default Jellyfin Docker image:

| Variant | Required packages | Install command |
|---------|-------------------|-----------------|
| **CPU** | `libgomp1` | `apt install libgomp1` |
| **Vulkan** | `libgomp1`, `libvulkan1`, `mesa-vulkan-drivers` | `apt install libgomp1 libvulkan1 mesa-vulkan-drivers` |
| **CUDA 12** | `libgomp1` + NVIDIA Container Toolkit on host | See [CUDA section](#cuda-nvidia) |
| **ROCm** | `libgomp1` + ROCm runtime | See [ROCm docs](https://rocm.docs.amd.com/) |

> `libgomp1` (OpenMP threading) is required by **all** variants, including CPU.

To install persistently, add to your container's entrypoint or Dockerfile:

```bash
apt-get update -qq && apt-get install -y -qq --no-install-recommends libgomp1 && rm -rf /var/lib/apt/lists/*
```

The plugin's setup page will detect missing libraries and warn you before downloading.

### Verifying the Installation

```bash
# If in PATH:
whisper-cli --help

# If using an absolute path:
/opt/whisper/whisper-cli --help

# Inside a Docker container:
docker exec jellyfin /opt/whisper/whisper-cli --help
```

## GPU Acceleration

whisper.cpp supports GPU offloading via **Vulkan** (Intel, AMD, and some NVIDIA GPUs), **CUDA** (NVIDIA), and **ROCm** (AMD). GPU acceleration dramatically reduces transcription time, especially with larger models.

> **Docker users:** Passing the GPU device (e.g., `/dev/dri`) to a container is **not enough** -- the container also needs the matching userspace libraries installed. The auto-setup wizard detects both the device and the library and will fall back to CPU if the library is missing.
>
> | Backend | Device | Required library | Install command (Debian/Ubuntu) |
> |---------|--------|------------------|---------------------------------|
> | **CUDA** | `/dev/nvidia0` | `libcuda.so.1` | `nvidia-container-toolkit` (host) |
> | **Vulkan** | `/dev/dri` | `libvulkan.so.1` + ICD JSON | `apt install libvulkan1 mesa-vulkan-drivers` (also needs `/usr/share/vulkan/icd.d/*.json`) |
> | **ROCm** | `/dev/kfd` | `libamdhip64.so` | `apt install rocm-hip-runtime` |

### Vulkan (Intel / AMD)

Vulkan is the best option for Intel iGPUs (e.g., UHD 770) and AMD GPUs. It works through the Mesa Vulkan drivers.

#### Building whisper.cpp with Vulkan

```bash
git clone https://github.com/ggerganov/whisper.cpp.git
cd whisper.cpp
cmake -B build \
  -DGGML_VULKAN=ON \
  -DBUILD_SHARED_LIBS=OFF
cmake --build build --config Release -j$(nproc)
# Binary: build/bin/whisper-cli
```

> **Important:** The CMake flag is `-DGGML_VULKAN=ON` (not `-DWHISPER_VULKAN`). This is a common source of confusion.

#### Runtime Dependencies

The Vulkan binary requires these libraries at runtime:

| Package (Debian/Ubuntu) | Purpose |
|---|---|
| `libvulkan1` | Vulkan loader |
| `mesa-vulkan-drivers` | Intel (ANV) and AMD (RADV) Vulkan ICDs |
| `libgomp1` | OpenMP threading |

```bash
apt-get install -y libvulkan1 mesa-vulkan-drivers libgomp1
```

#### Docker: GPU Passthrough for Vulkan

To use an Intel or AMD GPU inside a Docker container:

```yaml
services:
  jellyfin:
    image: jellyfin/jellyfin
    devices:
      - /dev/dri:/dev/dri    # GPU render nodes
    volumes:
      - /opt/whisper:/opt/whisper:ro
```

The container also needs the Vulkan runtime libraries. If using the official Jellyfin image (Debian-based), install them on startup:

```yaml
    entrypoint:
      - /bin/bash
      - -c
      - |
        dpkg -s libvulkan1 > /dev/null 2>&1 || \
          (apt-get update -qq && \
           apt-get install -y -qq --no-install-recommends \
             libvulkan1 mesa-vulkan-drivers libgomp1 > /dev/null 2>&1 && \
           rm -rf /var/lib/apt/lists/*)
        exec /jellyfin/jellyfin
```

Verify GPU detection inside the container:

```bash
# Should show your GPU (e.g., "Intel(R) UHD Graphics 770")
docker exec jellyfin apt-get update -qq && \
  docker exec jellyfin apt-get install -y -qq vulkan-tools && \
  docker exec jellyfin vulkaninfo --summary
```

#### Building Inside Docker (ABI Compatibility)

When Jellyfin runs in a container, the whisper binary must be compiled against matching system libraries. Build inside a container with the same base image:

```bash
# On the Docker host:
docker run --rm -v /opt/whisper:/output debian:trixie bash -c '
  apt-get update && apt-get install -y git cmake g++ libvulkan-dev &&
  git clone https://github.com/ggerganov/whisper.cpp.git /tmp/whisper &&
  cd /tmp/whisper &&
  cmake -B build -DGGML_VULKAN=ON -DBUILD_SHARED_LIBS=OFF &&
  cmake --build build --config Release -j$(nproc) &&
  cp build/bin/whisper-cli /output/whisper-cli
'
```

### CUDA (NVIDIA)

For NVIDIA GPUs with CUDA support:

#### Building whisper.cpp with CUDA

```bash
git clone https://github.com/ggerganov/whisper.cpp.git
cd whisper.cpp
cmake -B build \
  -DGGML_CUDA=ON \
  -DBUILD_SHARED_LIBS=OFF
cmake --build build --config Release -j$(nproc)
```

#### Docker: NVIDIA GPU Passthrough

```yaml
services:
  jellyfin:
    image: jellyfin/jellyfin
    runtime: nvidia
    environment:
      - NVIDIA_VISIBLE_DEVICES=all
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: all
              capabilities: [gpu]
    volumes:
      - /opt/whisper:/opt/whisper:ro
```

> Requires the [NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/install-guide.html).

### Verifying GPU Acceleration

After configuring GPU support, trigger a transcription and check the Jellyfin logs. You should see:

```
# Vulkan
whisper_backend_init_gpu: using Vulkan0 backend

# CUDA
whisper_backend_init_gpu: using CUDA0 backend
```

If you see `no GPU found` or `using CPU backend`, the binary was not built with GPU support or the runtime drivers are missing.

### Model Recommendations

| Model | Size | Speed (CPU) | Speed (GPU) | Quality | Use Case |
|---|---|---|---|---|---|
| `ggml-base.bin` | 148 MB | Fast | Very fast | Good | Quick transcription, testing |
| `ggml-medium.bin` | 1.5 GB | Moderate | Fast | Very good | Balanced quality/speed |
| `ggml-large-v3-turbo.bin` | 1.6 GB | Slow | Fast | Excellent | Best accuracy, recommended with GPU |
| `ggml-large-v3.bin` | 3.1 GB | Very slow | Moderate | Excellent | Maximum accuracy |

With GPU acceleration, `ggml-large-v3-turbo` offers the best quality-to-speed ratio.

## Configuration

After installation, navigate to **Dashboard** > **Plugins** > **WhisperSubs** to configure:

| Setting | Description |
|---|---|
| **Subtitle Provider** | The transcription engine to use. Currently `Whisper` is available. |
| **Whisper Binary Path** | Absolute path to the `whisper-cli` binary (e.g., `/opt/whisper/whisper-cli`). Leave empty to search `PATH`. |
| **Whisper Model Path** | Absolute path to the GGML model file (e.g., `/opt/whisper/models/ggml-large-v3-turbo.bin`). |
| **Default Language** | `Auto-detect` reads the language from each file's audio stream metadata and generates matching subtitles. Choose a specific language to force it for all transcriptions. |
| **Enable Auto-Generation** | When enabled, the scheduled task will scan selected libraries and generate subtitles for items that lack them. |
| **Enabled Libraries** | Select which libraries should be monitored for automatic subtitle generation. |

### Language Handling

The plugin supports three language modes:

1. **Auto-detect (recommended)** -- The plugin uses FFprobe to read the audio stream's language tag (e.g., `spa` → `es`, `eng` → `en`). Subtitles are generated in the language that matches the audio. If a file has multiple audio tracks in different languages, subtitles are generated for each one.

2. **Whisper auto-detection** -- When no language metadata is available, the request falls through to whisper's built-in language detection (`-l auto`), which analyzes the first 30 seconds of audio.

3. **Forced language** -- Set a specific language code (e.g., `es`) in the configuration or per-request via the API. This overrides detection and tells whisper to transcribe using that language model.

## Usage

### Admin Dashboard

The plugin adds a dedicated page to the Jellyfin admin dashboard (accessible from **Dashboard** > **Plugins** > **WhisperSubs**, or from the main sidebar menu). From there you can:

- **Configure** the plugin settings (provider, model, binary path, default language).
- **Browse** all libraries and their items.
- **See** which items already have subtitles (green check / orange cross).
- **Select a language** for subtitle generation (auto-detect or any specific language).
- **Generate** subtitles for individual items with a single click.

### REST API

All endpoints require Jellyfin admin authentication.

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/Plugins/WhisperSubs/Libraries` | List all media libraries |
| `GET` | `/Plugins/WhisperSubs/Libraries/{libraryId}/Items` | List items in a library |
| `POST` | `/Plugins/WhisperSubs/Items/{itemId}/Generate?language=auto` | Generate subtitles for a specific item |
| `GET` | `/Plugins/WhisperSubs/Items/{itemId}/AudioLanguages` | Detect audio languages in a media file |
| `GET` | `/Plugins/WhisperSubs/Items/{itemId}/Status` | Check subtitle generation status |

The `language` parameter accepts `auto` (default), or any ISO 639-1 code (`en`, `es`, `fr`, etc.).

### Scheduled Task

A scheduled task named **Generate Subtitles** is registered under the **WhisperSubs** category. It can be configured in **Dashboard** > **Scheduled Tasks** with your preferred schedule or triggered manually. The task:

1. Scans all enabled libraries (or all libraries if none are explicitly selected).
2. Finds video items that lack subtitles.
3. Generates subtitles using the configured default language (auto-detect by default).
4. Reports progress in the Jellyfin task UI.

## How It Works

1. **Language Detection** -- FFprobe reads the audio stream metadata to determine the spoken language(s).
2. **Audio Extraction** -- FFmpeg extracts a 16 kHz mono WAV track from the media file.
3. **Transcription** -- The extracted audio is passed to whisper.cpp, which produces an SRT subtitle file.
4. **Output** -- The `.srt` file is saved alongside the original media (e.g., `Movie.es.generated.srt`).
5. **Metadata Refresh** -- The item's metadata is refreshed so Jellyfin picks up the new subtitle immediately.

Temporary audio files are cleaned up automatically after processing.

## Roadmap

See [ROADMAP.md](ROADMAP.md) for planned features and design details.

## Other Jellyfin Projects by GeiserX

- [smart-covers](https://github.com/GeiserX/smart-covers) — Cover extraction for books, audiobooks, comics, magazines, and music libraries with online fallback
- [quality-gate](https://github.com/GeiserX/quality-gate) — Restrict users to specific media versions based on configurable path-based policies
- [jellyfin-encoder](https://github.com/GeiserX/jellyfin-encoder) — Automatic 720p HEVC/AV1 transcoding service with hardware acceleration
- [jellyfin-telegram-channel-sync](https://github.com/GeiserX/jellyfin-telegram-channel-sync) — Sync Jellyfin access with Telegram channel membership


## License

This project is licensed under the **GNU General Public License v3.0**. See the [LICENSE](LICENSE) file for the full text.
