# Roadmap

## Forced Subtitles

Generate foreign-parts-only subtitle tracks. When a movie's English audio contains scenes in Spanish, French, etc., only those non-English segments get subtitled. No existing tool does this today.

- **Settings**: New `Subtitle Mode` dropdown in the config page:
  - `Full` (default) -- current behavior, complete transcription of all speech
  - `Forced Only` -- only subtitle segments where detected language differs from the audio track's primary language
  - `Full + Forced` -- generate both complete and forced subtitle files per track
- **Output**: `Movie.en.forced.generated.srt` alongside `Movie.en.generated.srt`. Jellyfin auto-recognizes `.forced.` in filenames.
- **Approach**: VAD-based chunking of the audio, per-chunk language detection via `whisper-cli --detect-language`, selective transcription of foreign segments only. Primary language is inferred from audio stream metadata (already implemented in v2.5.0.0).
- See [#2](https://github.com/GeiserX/whisper-subs/issues/2) for discussion.

## Parakeet Provider

NVIDIA Parakeet integration for GPU-accelerated transcription.

## Custom Command Provider

Define arbitrary CLI commands as transcription backends.

## Translation

Generate subtitles in a different language than the audio (e.g., English subs for Spanish audio).

## Progress Tracking

Real-time progress reporting in the admin UI during transcription.

## Batch Operations

Generate subtitles for entire libraries or filtered sets from the dashboard.
