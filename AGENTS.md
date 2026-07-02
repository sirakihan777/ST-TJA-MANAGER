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

## Runtime Dependency

- The app calls `ffmpeg` via `PATH` to convert `.wav` files to `.ogg` on folder load.
- On startup it checks `ffmpeg -version` and shows a warning dialog if missing; non-audio features still work.

## Architecture

- Entry UI: `App.xaml` → `MainWindow.xaml` (two tabs: TJA editor / STFDB editor).
- Dialog: `StfdbAddTjaDialog.xaml`.
- Models: `Models/CourseItem.cs`, `Models/StfdbEntryItem.cs`.
- Services: `Services/TjaParser.cs`, `Services/TjaWriter.cs`, `Services/StfdbService.cs`, `Services/AudioConverter.cs`, `Services/ConfigService.cs`.

## Key Behavioral Quirks

- `AudioConverter.DeleteOriginalWav` is hardcoded to `true`: successful WAV→OGG conversion deletes the source `.wav`.
- On folder load, `AudioConverter` also rewrites `WAVE:` lines inside TJA files from `.wav`/`.wav.ogg` to `.ogg` and removes redundant blank lines.
- `StfdbService.SaveStfdbFile` always creates a `.bak` copy of the original STFDB before writing.
- `StfdbService` preserves the detected encoding (BOM-UTF8, UTF8, or Shift_JIS) and newline style of the original STFDB.
- `ConfigService` writes `last_path.txt` in the app base directory to remember the last selected folder.
- A sanity check (`StfdbService.RunSanityCheck`) runs in the `MainWindow` constructor and is swallowed if it throws; failures appear in trace output only.

## Text Encoding

- TJA/STFDB default encoding is `shift_jis` (CP932), with UTF-8 detection/fallback for files that contain non-Shift_JIS characters or a BOM.
- Use `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` (done in `App.xaml.cs`) before relying on `shift_jis`.
