# Workspace Rules (`JellyfinTrailerSuite`)

## Project Overview
This workspace contains two `.NET 9.0` projects targeting **Jellyfin 10.11.3**:
1. **`src/TrailerGenerator.WinForms`** (`net9.0-windows`): A standalone Windows Forms application that scans media folders and generates 30-second local trailer preview clips (`{VideoFileNameWithoutExtension}-trailer.mp4`) in the exact same directory as the source video using `ffmpeg`/`ffprobe`.
2. **`src/Jellyfin.Plugin.CustomHoverTrailer`** (`net9.0`): A Jellyfin 10.11.3 plugin that provides YouTube-style in-card hover video previews without opening pop-up modals or YouTube `<iframe>` embeds.

## Core Conventions & Requirements
- **Local Trailer Naming & Location:**
  - Local trailers MUST reside in the **same folder** as the movie/video file and match `{VideoFileNameWithoutExtension}-trailer.mp4` (with fallback support for `.mkv`, `.webm`, or `trailer.mp4`).
  - The Jellyfin plugin resolves local trailers directly from disk via `ILibraryManager` item paths (`System.IO.File.Exists`) rather than relying on Jellyfin's internal local trailer scanner.
- **Trailer Source Priority in Plugin:**
  1. Direct disk file match (`{VideoFileNameWithoutExtension}-trailer.mp4` in the same folder).
  2. Direct `.mp4` / `.webm` URLs found in `item.RemoteTrailers` or custom metadata.
  3. YouTube URLs found in `item.RemoteTrailers` resolved on the backend to a raw media stream (never an `<iframe>` embed).
- **Frontend Hover UI (Option B):**
  - Plays directly inside the hovered `.card` (`cardScalable` / `cardImageContainer`) using a native HTML5 `<video>` element.
  - Uses `object-fit: contain` (letterboxed 16:9 inside the poster card with dark backdrop, plus an optional setting for smooth in-place 16:9 card expansion).
  - Never opens a floating center-screen modal or YouTube iframe window.
## Build & Target Version
- Target Jellyfin `10.11.3` (`Jellyfin.Controller` and `Jellyfin.Model` version `10.11.*` on `net9.0`).
- Local Jellyfin plugins path on this PC: `C:\ProgramData\Jellyfin\ServerData\plugins`.
- Local .NET 9.0 SDK binary: `C:\Users\Ionut\AppData\Local\Microsoft\dotnet\dotnet.exe`.

