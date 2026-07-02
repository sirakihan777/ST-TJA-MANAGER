# Agent Notes

## Project Basics

- WPF desktop app targeting `net10.0-windows`, using the modern `.slnx` solution format.
- Single project: `ST-TJA-MANAGER.csproj`. Root namespace is `ST_Fumen_Manager_WPF`, not the project filename.
- No unit-test project, no `README`, no CI workflows, no `.editorconfig`, no `global.json`.

## Build & Run

```powershell
# Build (use the .slnx file, not a nonexistent .sln)
dotnet build ST-TJA-MANAGER.slnx

# Run the WPF app
dotnet run --project ST-TJA-MANAGER.csproj
```

- Windows-only. Running the GUI from a headless environment is not useful, but `dotnet build` works cross-platform for analysis.

## Runtime Dependencies

- `ffmpeg` must be on `PATH`. On startup the app checks `ffmpeg -version` and shows a warning dialog if missing; non-audio features still work.
- `git` must be on `PATH` for the **公式順STFDB生成** tab, which clones/updates `https://ese.tjadataba.se/ESE/ESE.git`.
- The same tab fetches the official Namco songlist from `https://taiko.namco-ch.net/taiko/songlist/`.
- The same tab fetches CS-version metadata and goodbye-song lists from `https://wikiwiki.jp/taiko-fumen/`.

## Architecture

- Entry UI: `App.xaml` → `MainWindow.xaml` (three tabs: TJA編集 / STFDB編集 / 公式順STFDB生成).
- Dialog: `StfdbAddTjaDialog.xaml`.
- Models: `Models/CourseItem.cs`, `Models/StfdbEntryItem.cs`, `Models/OfficialSongRecord.cs`, `Models/ConsumerSongRecord.cs`, `Models/GoodbyeSongRecord.cs`.
- Core services: `Services/TjaParser.cs`, `Services/TjaWriter.cs`, `Services/StfdbService.cs`, `Services/AudioConverter.cs`, `Services/ConfigService.cs`.
- Official-order generation services: `Services/GitSongSourceService.cs`, `Services/OfficialSongListService.cs`, `Services/OfficialSongMatcher.cs`, `Services/OfficialStfdbGenerator.cs`, `Services/TitleNormalizer.cs`.
- Classification services: `Services/ConsumerSongListService.cs`, `Services/GoodbyeSongListService.cs`, `Services/SongClassificationService.cs`, `Services/ClassifiedStfdbGenerator.cs`.

## Key Behavioral Quirks

- On folder load, the app:
  1. Converts `.wav` → `.ogg` via `ffmpeg` and deletes the source `.wav` (`AudioConverter.DeleteOriginalWav` is hardcoded to `true`).
  2. Normalizes legacy `.wav.ogg` files to `.ogg`.
  3. Rewrites `WAVE:` lines in every `.tja` from `.wav`/`.wav.ogg` to `.ogg` and removes redundant blank lines (`TjaWriter.UpdateTjaWaveLines` / `CleanRedundantNewlines`).
- `TjaWriter.SaveWithEncodingFallback` writes TJA files in Shift_JIS when possible; falls back to UTF-8 BOM if Shift_JIS cannot encode the content.
- `StfdbService.SaveStfdbFile` always creates a `.bak` copy of the original STFDB before writing.
- `StfdbService` preserves the detected encoding (BOM-UTF8, UTF8, or Shift_JIS) and newline style of the original STFDB.
- `OfficialStfdbGenerator` backs up existing `official.stfdb` files and creates missing `box.def` files, but never overwrites an existing `box.def`.
- `ConsumerSongListService` scrapes CS-version song lists from `https://wikiwiki.jp/taiko-fumen/作品/{work}` and caches them in `%APPDATA%\ST-TJA-MANAGER\Database\consumer_songs.json`.
- `GoodbyeSongListService` scrapes the goodbye-song list from `https://wikiwiki.jp/taiko-fumen/作品/新AC/サヨナラ曲` and caches it in `%APPDATA%\ST-TJA-MANAGER\Database\goodbye_songs.json`.
- `ClassifiedStfdbGenerator` extends `OfficialStfdbGenerator` by optionally creating `08 サヨナラ曲` and `09 CS版限定候補` folders for unmatched TJAs; missing caches are fetched automatically when those options are enabled.
- CS-version-only candidates are defined as: present in a CS work page **and** absent from the official Namco AC songlist. Songs that appear in both are treated as regular official songs, not CS-only candidates.
- `ConfigService` writes `last_path.txt` in the app base directory to remember the last selected folder.
- `GitSongSourceService` only operates inside `%APPDATA%\ST-TJA-MANAGER\Cache\ESE`; it refuses to delete or modify anything outside that cache folder.
- A sanity check (`StfdbService.RunSanityCheck`) runs in the `MainWindow` constructor; exceptions are swallowed and failures are written to trace output only.

## Text Encoding

- TJA/STFDB default encoding is `shift_jis` (CP932), with UTF-8 detection/fallback for files that contain non-Shift_JIS characters or a BOM.
- Use `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` (done in `App.xaml.cs`) before relying on `shift_jis`.
