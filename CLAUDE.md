# CLAUDE.md — WhisperSubs

## Project Overview

WhisperSubs is a Jellyfin plugin that generates subtitles for media libraries using local AI speech-to-text models. All processing happens on the server — no cloud APIs. The primary backend is [whisper.cpp](https://github.com/ggml-org/whisper.cpp) with Vulkan/CUDA GPU acceleration support.

- **Repo:** [GeiserX/whisper-subs](https://github.com/GeiserX/whisper-subs)
- **Plugin GUID:** `97124bd9-c8cd-4a53-a213-e593aa3fef52`
- **Target:** Jellyfin 10.11+ / .NET 9.0
- **License:** GPL-3.0

## Architecture

```
Plugin.cs                          Entry point, IHasWebPages (embeds config UI)
├── Configuration/
│   └── PluginConfiguration.cs     User-editable settings (model path, binary path, language, etc.)
├── Api/
│   └── SubtitleController.cs      REST API endpoints under /Plugins/WhisperSubs/*
├── Controller/
│   ├── SubtitleManager.cs         Orchestrator: language detection → audio extraction → transcription → save
│   └── SubtitleQueueService.cs    Thread-safe in-memory queue with single-worker drain loop
├── Providers/
│   ├── ISubtitleProvider.cs       Provider interface (TranscribeAsync)
│   └── WhisperProvider.cs         whisper.cpp integration (finds binary, runs process, reads SRT output)
├── ScheduledTasks/
│   └── SubtitleGenerationTask.cs  Jellyfin scheduled task for auto-generation
└── Web/
    └── configPage.html            Admin UI (embedded resource) — vanilla JS, Jellyfin emby-* components
```

### Data Flow — Full Subtitles

1. **Language detection** — `SubtitleManager.DetectAudioLanguagesAsync` calls FFprobe to read audio stream language tags. ISO 639-2/B codes are normalized to 639-1 (e.g., `spa` → `es`).
2. **Audio extraction** — FFmpeg extracts 16kHz mono PCM WAV from the media file to a temp path.
3. **Transcription** — `WhisperProvider.TranscribeAsync` invokes `whisper-cli` as a child process with the model and audio file. Output is an SRT file.
4. **Save** — The SRT content is written alongside the media as `<filename>.<lang>.generated.srt`.
5. **Metadata refresh** — `item.RefreshMetadata()` tells Jellyfin to pick up the new subtitle file.

### Data Flow — English Translation (v3.11.0.0+)

When `EnableTranslation` is enabled in config (and SubtitleMode is Full or FullAndForced):

1. **English audio check** — FFprobe checks if any audio stream is already English. If so, translation is skipped entirely. If FFprobe cannot resolve a language (returns `"auto"`), whisper detects the language from a 30-second audio sample; if English is detected with sufficient confidence (p>=0.3), translation is skipped.
2. **Existing file check** — If `<filename>.en.translated.srt` already exists, skip. In `"auto"` mode, also checks for existing English subtitle files (`.en.srt`, `.eng.srt`, etc.).
3. **Source language selection** — Uses the first non-English language from resolved languages. In `"auto"` mode, uses the whisper-detected language from step 1 if available, otherwise falls back to `"auto"`.
4. **Transcription with translation** — Calls `WhisperProvider.TranscribeAsync(audioPath, sourceLanguage, ct, translate: true)`, which adds `--translate` to the whisper-cli invocation. Whisper's `--translate` flag always translates TO English.
5. **Save** — Written as `<filename>.en.translated.srt`.

**Important:** The `language` argument to `TranscribeAsync` must be the SOURCE language (e.g., `es`), not `en`. Whisper uses `-l` to set the source language and `--translate` to indicate output should be English.

### Data Flow — Forced Subtitles (v3.0.0+)

Forced subtitles capture only foreign-language dialogue segments (e.g., Russian dialogue in an English film). The pipeline:

1. **VAD (Voice Activity Detection)** — FFmpeg `silencedetect` splits the full audio into speech chunks using `-30dB:d=0.5` thresholds.
2. **Per-chunk language detection** — Each chunk is fed to `WhisperProvider.DetectLanguageAsync` (whisper `--detect-language` mode). Returns a language code + probability.
3. **Foreign segment identification** — Chunks where `detectedLang != primaryLang && probability >= 0.3` are marked as foreign. Adjacent foreign chunks are merged.
4. **Selective transcription** — Only the foreign segments are extracted and transcribed individually, with timestamps offset to match the original media timeline.
5. **Save** — Written as `<filename>.<lang>.forced.generated.srt`.
6. **No-foreign marker** — If zero foreign chunks are detected (and at least one detection succeeded), a `<filename>.<lang>.forced.noforeignlang` empty marker file is written to skip the item on future runs.

**SubtitleMode** enum controls behavior:
- `Full` (0, default) — Only full transcription
- `ForcedOnly` (1) — Only forced subtitle detection
- `FullAndForced` (2) — Both

### Configuration — Thread Count (v3.1.0+)

`WhisperThreadCount` controls the `-t N` flag passed to whisper-cli. Default `0` = whisper's internal default (4 threads). Set to your CPU core count for faster transcription. On a 20-thread CPU, this can yield ~12-13x parallelism.

WhisperProvider instances are constructed fresh for each work item (both in `SubtitleController` and `SubtitleGenerationTask`), so config changes via the plugin settings page take effect on the next work item without a Jellyfin restart.

### Queue System

Manual subtitle requests go through `SubtitleQueueService`:

- **`Enqueue()`** — Fire-and-forget. The `POST /Items/{id}/Generate` endpoint returns HTTP 202 immediately.
- **`EnsureDraining()`** — Starts a single background worker if one isn't already running. Uses `Interlocked.CompareExchange` for thread safety.
- **Race condition protection** — After the drain loop exits, it re-checks the queue and restarts if new items arrived during the `finally` block.
- **Skip existing** — The drain loop checks for `.generated.srt` files before processing, so re-queuing after a restart is safe (already-done items are skipped instantly).
- **Persisted to disk** — The queue is saved to `queue.json` in the plugin data folder (`/config/data/WhisperSubs/queue.json`) on every enqueue/dequeue. On startup, `RestoreQueue()` reloads pending items before the library scan begins.

### Scheduled Task

`SubtitleGenerationTask` runs on startup and daily at 2 AM (configurable in Jellyfin UI). It:

1. Queries all enabled libraries for `Movie` and `Episode` items without subtitles.
2. Checks for existing `.generated.srt` files (restart resilience).
3. Between each auto-generation item, drains any priority queue items (manual requests take precedence).

## API Endpoints

All require Jellyfin admin auth (`Authorization: MediaBrowser Token="<token>"`).

| Method | Path | Returns | Notes |
|--------|------|---------|-------|
| `GET` | `/Plugins/WhisperSubs/Libraries` | `LibraryInfo[]` | All virtual folders |
| `GET` | `/Plugins/WhisperSubs/Libraries/{id}/Items?startIndex=0&limit=50` | `PagedItemResult` | Movies/Episodes with subtitle status |
| `POST` | `/Plugins/WhisperSubs/Items/{id}/Generate?language=auto` | 202 Accepted | Enqueues, returns immediately |
| `GET` | `/Plugins/WhisperSubs/Items/{id}/Status?language=auto` | `SubtitleStatus` | Checks for `.generated.srt` on disk |
| `GET` | `/Plugins/WhisperSubs/Items/{id}/AudioLanguages` | `string[]` | FFprobe-detected languages |
| `GET` | `/Plugins/WhisperSubs/Queue` | `{isProcessing, currentItem, remaining, processed}` | Live queue status |
| `GET` | `/Plugins/WhisperSubs/Models` | `ModelInfo[]` | `.bin` files in the model directory |
| `POST` | `/Plugins/WhisperSubs/RunTask` | 200 | Triggers the scheduled task |

## Build & Deploy

### Build

```bash
dotnet build --configuration Release
# Output: bin/Release/net9.0/WhisperSubs.dll
```

### Deploy (manual)

Copy the DLL to the Jellyfin plugin directory and restart:

```bash
cp bin/Release/net9.0/WhisperSubs.dll \
  /path/to/jellyfin/config/plugins/WhisperSubs_<version>/WhisperSubs.dll
# Restart Jellyfin
```

### CI/CD

The GitHub Actions workflow (`.github/workflows/build-release.yml`) triggers on push to `main`:

1. Builds the DLL
2. Packages it into a versioned ZIP
3. Creates a GitHub Release
4. Updates `manifest.json` with the checksum
5. Deploys to GitHub Pages (serves the plugin repository manifest)

Version is read from `<Version>` in `WhisperSubs.csproj`. Bump it there before pushing.

**Note:** The `manifest.json` in the source tree is NOT authoritative — CI generates a fresh one with the correct version, checksum, and `sourceUrl` and deploys it to GitHub Pages. The checked-in copy is stale and only exists for reference.

## Config Page (Web UI)

`Web/configPage.html` is embedded as a resource (`EmbeddedResourcePath` in `Plugin.cs`).

### Key constraints

- **Jellyfin custom elements** — Dropdowns with static options (Subtitle Provider, Default Language) use `is="emby-select"` for native Jellyfin styling. Dropdowns populated dynamically via JS (Detected Models, Library selector) also use `is="emby-select"` — the options are added after the `pageshow` event fires via API calls.
- **`data-require`** — The page declares `data-require="emby-input,emby-button,emby-select,emby-checkbox"` to ensure Jellyfin loads these components before rendering.
- **No framework** — Pure vanilla JS. The `WhisperSubsConfig` object namespace holds all logic.
- **Auth** — API calls use `ApiClient.accessToken()` via the `getAuthHeader()` helper.
- **Config load/save** — Uses `ApiClient.getPluginConfiguration()` / `ApiClient.updatePluginConfiguration()` with the plugin GUID.

### Debugging the UI

Open the browser console and look for lines prefixed with `WhisperSubs:`. All `ajaxGet` calls log the URL, response status, and parsed data.

## whisper.cpp Integration

### Binary discovery

`WhisperProvider.FindWhisperExecutable()` tries candidates in order:

1. The configured `WhisperBinaryPath` (if set)
2. `whisper-cli` (PATH)
3. `main` (PATH)
4. `whisper` (PATH)

Each candidate is tested with `--help`. The first one that exits with code 0 or 1 is used.

### Build requirements for Docker

The whisper-cli binary must be built for the same environment as the Jellyfin container. Jellyfin 10.11.x uses Debian Trixie/Sid. Building on the host and mounting won't work if glibc versions differ.

**Build inside the running container or a matching Docker image.**

```bash
# CPU-only build (any Debian):
apt-get install -y git cmake g++ make
git clone --depth 1 --branch v1.8.4 https://github.com/ggml-org/whisper.cpp.git /tmp/whisper
cd /tmp/whisper
cmake -B build -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF
cmake --build build --config Release -j$(nproc)
# Binary: build/bin/whisper-cli
```

```bash
# Vulkan (GPU) build — requires glslc SPIR-V compiler:
apt-get install -y git cmake g++ make pkg-config libvulkan-dev glslc
# On Debian Bookworm: `glslc` is in the `shaderc` package — install `shaderc` if `glslc` is not found
# On Debian Trixie: `glslc` package exists directly
git clone --depth 1 --branch v1.8.4 https://github.com/ggml-org/whisper.cpp.git /tmp/whisper
cd /tmp/whisper
cmake -B build -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DGGML_VULKAN=ON
cmake --build build --config Release -j$(nproc)
# Binary: build/bin/whisper-cli
# Verify: ldd build/bin/whisper-cli | grep vulkan
```

Key flags:
- **`-DBUILD_SHARED_LIBS=OFF`** — Static link whisper/ggml libraries into the binary. Without this, you get `libwhisper.so.1: cannot open shared object file` at runtime.
- **`-DGGML_VULKAN=ON`** — Intel/AMD GPU acceleration via Vulkan. Requires `libvulkan-dev` and `glslc` (SPIR-V compiler) at build time, `libvulkan1` and `mesa-vulkan-drivers` (or `intel-media-va-driver`) at runtime.
- **`-DGGML_CUDA=ON`** — NVIDIA GPU acceleration. Requires CUDA toolkit.
- **Common build failure**: `Could NOT find Vulkan (missing: glslc)` — the `glslang-tools` / `glslang-dev` packages do NOT provide `glslc`. You need the `glslc` or `shaderc` package specifically.

### Persistent storage

The whisper binary and models MUST be on persistent storage that survives reboots. Do NOT use tmpfs paths like `/opt` on diskless systems (e.g., Unraid where `/opt` is on the root RAM disk).

Store in an appdata directory and bind-mount into the container:

```yaml
volumes:
  - /path/to/persistent/whisper:/opt/whisper:ro
```

### GPU passthrough (Docker)

```yaml
devices:
  - /dev/dri   # Intel/AMD GPU render nodes
environment:
  - VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/intel_icd.json  # Required for Vulkan in containers
```

The `VK_ICD_FILENAMES` env var is critical — without it, the Vulkan loader may fail to find the Intel ICD inside the container even with `mesa-vulkan-drivers` installed. Set it to:
- **Intel:** `/usr/share/vulkan/icd.d/intel_icd.json`
- **AMD:** `/usr/share/vulkan/icd.d/radeon_icd.json`

The GPU wrapper script (`whisper-cli-gpu`) is self-healing: it checks for the Vulkan ICD file on each invocation and runs `apt-get install` if missing. This survives container recreates without requiring entrypoint modifications. The one-time install adds ~10s to the first transcription after a fresh container.

Verify GPU detection:

```bash
docker exec jellyfin /opt/whisper/whisper-cli \
  -m /opt/whisper/models/ggml-base.bin -f /dev/null 2>&1 | grep -i vulkan
# Should show: "ggml_vulkan: Found N Vulkan devices"
# And: "whisper_backend_init_gpu: using Vulkan0 backend"
# If it says "no GPU found", check VK_ICD_FILENAMES
```

## Performance Benchmarks

Tested with a 2h15m film (8107s audio), large-v3 model, 5-beam search.

| Config | Wall time | Real-time factor | CPU usage |
|--------|-----------|------------------|-----------|
| CPU, 4 threads (default) | ~7h+ (est.) | ~3.2x | ~400% |
| CPU, 16 threads (i5-14500) | 1h48m | 0.80x | ~1270% |

Per-segment breakdown (16 threads):
- Encode: 13,010ms per 30s segment (278 segments) — 56% of total time
- Batch decode: 23ms per run — fast
- Total: 6,477,485ms

**GPU offloading is critical** — the encode step dominates and is highly parallelizable on GPU. With Vulkan on Intel UHD 770, expect 2-4x overall speedup for full transcription.

**GPU disabled for language detection (by design)**: `DetectLanguageAsync` passes `--no-gpu` because each call spawns a fresh whisper process per chunk, and GPU init overhead (model load + shader compilation) exceeds the detection work itself (~21s/chunk with GPU vs ~15s/chunk CPU-only). Transcription still uses GPU where available. The deeper issue — per-chunk process spawning — remains; long-term fix is a persistent whisper-server process that stays loaded (see GitHub issue).

### Known quality issues

- **Hallucination on non-speech audio**: During music, credits, or silence, large-v3 generates nonsense (e.g., "Suscríbete al canal!"). The `--suppress-non-speech` (`-sns`) flag helps but doesn't eliminate it.
- **Language detection false positives**: At `probability >= 0.3`, concert/music audio can be misidentified as foreign language (e.g., Aerosmith concert detected as Japanese with p=0.316). Consider raising the threshold for non-dialogue content.
- **Hallucination signatures**: Common in Spanish: "La Iglesia de Jesucristo de los Santos de los Últimos Días", "Suscríbete al canal", "Subtítulos por". These appear in credits and silent segments.

## Subtitle File Naming

Output files follow the pattern:

```
<media_filename>.<lang>.generated.srt
```

Examples:
- `Movie.es.generated.srt`
- `Show S01E01.en.generated.srt`

The `.generated.srt` suffix distinguishes AI-generated subtitles from manually added ones. Jellyfin auto-discovers these files when placed alongside the media.

## Common Issues

### "Whisper model not found"
The model path in the plugin config doesn't match the actual file location inside the container. Check the bind-mount and verify the path exists inside the container:
```bash
docker exec jellyfin ls -lh /opt/whisper/models/
```

### "Whisper executable not found"
The binary isn't in PATH and the configured path is wrong or the binary crashes on `--help`. Test it manually:
```bash
docker exec jellyfin /opt/whisper/whisper-cli --help
```

### "libwhisper.so.1: cannot open shared object file"
The binary was built with shared libraries. Rebuild with `-DBUILD_SHARED_LIBS=OFF`.

### "no GPU found" despite Vulkan binary
Set `VK_ICD_FILENAMES` environment variable in the container. See [GPU passthrough](#gpu-passthrough-docker) above.

### Queue stops processing after restart
The queue persists to disk, so pending items are restored on Jellyfin restart. If items still appear missing, the scheduled task will re-scan and pick them up automatically.

### High CPU during transcription
If not using GPU acceleration, whisper.cpp uses all available CPU cores. Consider:
- Building with Vulkan/CUDA support to offload to GPU
- Using a smaller model (`ggml-base.bin` or `ggml-large-v3-turbo.bin`)
- Scheduling transcription during off-peak hours via the scheduled task settings

### emby-select dropdowns empty
If dynamically populated dropdowns appear empty, check the browser console for `WhisperSubs:` log lines. The API calls may be failing due to auth issues. Hard-refresh the page (Ctrl+Shift+R).

## Partial SRT & Resume on Restart

If transcription is cancelled or Jellyfin restarts mid-processing:

1. **WhisperProvider** kills the whisper process and returns whatever partial SRT content was written to disk.
2. **SubtitleManager** saves the partial SRT as `<filename>.<lang>.generated.srt`.
3. On the next run, `SubtitleManager.GenerateSubtitleAsync()` detects the existing file, parses the last timestamp via `WhisperProvider.ParseLastSrtTimestamp()`, and compares it against the media duration (via FFprobe).
4. If the SRT is within 30 seconds of the media end, it's considered **complete** and skipped.
5. If partial, FFmpeg extracts audio starting from the resume offset (`-ss`), whisper transcribes the remainder, and the new SRT entries are offset-adjusted and appended to the existing file.

Key helpers in `WhisperProvider`:
- `ParseLastSrtTimestamp(srtContent)` — returns last end timestamp in seconds
- `OffsetSrt(srtContent, offsetSeconds, startIndex)` — shifts all timestamps and renumbers entries
- `CountSrtEntries(srtContent)` — counts `-->` lines

## Deployment

### Manual deployment to a running Jellyfin container

```bash
dotnet build --configuration Release
scp bin/Release/net9.0/WhisperSubs.dll \
  <host>:/path/to/jellyfin/config/plugins/WhisperSubs_<version>/WhisperSubs.dll
# Restart Jellyfin to load the new DLL
```

The host path for `/config` depends on the Docker volume mapping. Find it with:
```bash
docker inspect jellyfin --format '{{range .Mounts}}{{.Source}} -> {{.Destination}}{{println}}{{end}}'
```

### Gotchas

- **NEVER restart Jellyfin without asking the user first.** Jellyfin restarts interrupt active playback and kill the in-memory transcription queue. Always confirm before running `docker restart jellyfin`.
- **Unraid tmpfs**: Do NOT store whisper binaries or models in `/opt` on Unraid — it's a RAM disk that wipes on reboot. Use `/mnt/user/appdata/whisper` and bind-mount into the container.
- **Static linking is mandatory**: Always build whisper.cpp with `-DBUILD_SHARED_LIBS=OFF`. Dynamic builds fail with `libwhisper.so.1: cannot open shared object file` inside the Jellyfin container.
- **Orphaned docker-proxy**: If Jellyfin crashes, the docker-proxy process may hold port 8096. On Unraid, run `rc.docker restart` to clean up. On other systems, restart the Docker daemon.
- **Memory limits**: Transcription (especially with large models) can consume 5-10 GB RAM. Set `mem_limit` in docker-compose to prevent OOM kills affecting other services.
- **Plugin directory moves on version change**: Jellyfin may rename the plugin folder (e.g. `WhisperSubs_1.0.4.2` → `WhisperSubs`). Always check the actual path with `docker exec jellyfin find /config/plugins -name "WhisperSubs*" -type d` before deploying.

## Queue Persistence & Concurrency

- **Queue persists to disk** as `queue.json` in the plugin data folder (`/config/data/WhisperSubs/queue.json`). Updated on every enqueue/dequeue. On startup, `RestoreQueue()` reloads all entries and drains them before the library scan begins.
- **Global `TranscriptionLock`** (`SemaphoreSlim(1,1)`) prevents concurrent whisper processes. Both the drain loop and the scheduled task must acquire it. Without this, two whisper processes run simultaneously and can OOM the container (11.4 GB / 12 GB observed).
- **Per-language error isolation**: If whisper fails on one language (e.g. `en`), the error is caught and logged but does not abort remaining languages (e.g. `es` SRT is still saved). Only `OperationCanceledException` propagates up.
- **whisper.cpp writes SRT only at completion** — not incrementally. Mid-process kills produce no partial file. The resume feature only helps when whisper finishes writing a file that covers part of the media (rare edge case).
- **Killed items are not auto-retried** — they fall out of the queue. The scheduled task's library scan will eventually re-process them. Manually re-queue if urgent.

## Language Detection

- FFprobe extracts `language` tags from audio streams. Most HDO/WEB-DL files have proper `spa`/`eng` tags.
- Normalization: 30+ ISO 639-2 → 639-1 mappings in `SubtitleManager.NormalizeLanguageCode()`.
- Dedup: if a file has 4 audio streams (`spa, spa, eng, eng` — e.g. DD+ and DD variants), only `es` and `en` are generated.
- Fallback: files with no language tags (older rips, some PlutoTV content) get whisper auto-detection — one SRT with language `auto`.

## Development Notes

- The `.csproj` targets `net9.0` and references `Jellyfin.Model` and `Jellyfin.Controller` 10.11.0.
- **Jellyfin SDK pinning**: ALWAYS pin `Jellyfin.Controller` and `Jellyfin.Model` to the MINIMUM supported minor version (e.g., `10.11.0`), NEVER use wildcards like `10.11.*` or exact higher patches like `10.11.8`. Wildcards resolve to the latest patch at build time, which breaks users on older patch versions with `ReflectionTypeLoadException`. All plugin APIs used are stable across patch versions.
- The config page HTML is an embedded resource — changes require rebuilding the DLL.
- `Plugin.Instance` is a static singleton set in the constructor. All components access config via `Plugin.Instance.Configuration`.
- The `ISubtitleProvider` interface is designed for extensibility (Parakeet, custom commands), but only `WhisperProvider` is currently implemented.
- Language normalization covers 30 ISO 639-2 → 639-1 mappings. Add new ones to `SubtitleManager.NormalizeLanguageCode()`.
- The Generate endpoint returns HTTP 202 immediately — transcription runs in a background queue. Manual requests get priority over scheduled-task items.
- The config page UI uses Jellyfin's `emby-*` custom elements. Dynamic dropdowns (models, libraries) must use `is="emby-select"` **and** populate options only after the `pageshow` event fires. Do not call `loadLibraries()` twice — it causes a race condition that wipes the dropdown.

---

*Generated by [LynxPrompt](https://lynxprompt.com) CLI*
