# JellySubtitles

A Jellyfin plugin to automatically generate subtitles for your media using local AI models (Whisper, Parakeet, etc.).

## Features
*   **Local Processing**: Runs entirely on your server. No data leaves your network.
*   **Pluggable Providers**: Support for Whisper (via whisper.cpp), Parakeet, and custom commands.
*   **Admin UI**: Manage subtitles directly from the Jellyfin dashboard.
*   **Scheduled Tasks**: Automatically scan and generate subtitles for new media.

## Installation

1.  **Build the Plugin**:
    ```bash
    dotnet build --configuration Release
    ```
2.  **Deploy**:
    Copy the `JellySubtitles.dll` (and dependencies) to your Jellyfin `plugins` directory (e.g., `/var/lib/jellyfin/plugins/JellySubtitles`).
3.  **Restart Jellyfin**.

## Configuration

1.  Go to **Dashboard** -> **Plugins** -> **JellySubtitles**.
2.  **Subtitle Provider**: Choose your preferred engine.
3.  **Whisper Model Path**: If using Whisper, provide the absolute path to the `.bin` model file.
4.  **Enable Auto-Generation**: Check this to let the plugin scan your libraries automatically.

## Requirements
*   **Jellyfin**: 10.8.0 or later.
*   **External Tools**:
    *   For Whisper: `whisper-cli` (whisper.cpp) must be in the system PATH or configured.
    *   For Parakeet: A running local instance or CLI tool.

## Development
This project targets `.NET 6.0` to be compatible with Jellyfin 10.8.x.

## License
MIT
