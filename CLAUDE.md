# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in the Lara Extractor repository.

## Project Overview

Lara Extractor is a WPF application for extracting resources from Tomb Raider 1-5 game files (.phd, .tr2, .tr4, .trc). It extracts textures (.tga), sounds (.wav), and 3D assets (.wad2 files containing level objects, movables, and statics).

## Project Structure

- **Lara Extractor/** - Main WPF application
  - `MainWindow.xaml.cs` - Main UI with file browsing and extraction controls
  - `AssetProcessor.cs` - Extracts textures and audio from game files
  - `Wad2Processor.cs` - Processes WAD2 files for 3D assets using TombLib
  - `TR1Wad2Builder.cs` - Special handler for TR1 files (bypasses TombLib issues)
  - `SilentDialogHandler.cs` - Handles dialogs silently for TombLib integration
  - `App.xaml.cs` - Standard WPF application entry point

- **Diagnostics/** - Diagnostic tool for analyzing TR1 file structures
- **Wad2Worker/** - Appears to be a worker process for WAD2 processing
- **TombLib.dll** and **WadTool.dll** - Referenced libraries for Tomb Raider file handling

## Development Setup

### Prerequisites
- .NET 10.0 SDK or later
- Visual Studio 2022 or later with WPF workload
- Tomb Editor installation (for TombLib.dll reference)

### Building the Project
```bash
# From repository root
dotnet build "Lara Extractor/Lara Extractor.csproj"
```

### Running the Application
```bash
dotnet run --project "Lara Extractor/Lara Extractor.csproj"
```

### Running Diagnostics Tool
```bash
dotnet run --project Diagnostics/Diagnostics.csproj
```

## Key Architectural Patterns

### Two-Phase Extraction Process
1. **Asset Processing** (`AssetProcessor.cs`):
   - Reads raw game files (.phd, .tr2, .tr4, .trc)
   - Extracts textures (converts to .tga with proper palettes)
   - Extracts audio samples (converts to .wav)
   - Handles different TR versions (TR1-TR5) with version-specific parsing

2. **WAD2 Processing** (`Wad2Processor.cs`):
   - Creates WAD2 files containing 3D assets (objects, meshes, textures)
   - Uses TombLib for TR2-TR5 (with STA thread requirements)
   - Uses custom TR1 parser (`TR1Wad2Builder`) for TR1 files (avoids TombLib issues)

### Key Dependencies
- **TombLib.dll**: Core library for Tomb Raider file format handling
  - Referenced from Tomb Editor installation
  - Used for TR2-TR5 level loading and WAD2 generation
  - Requires STA thread for certain operations (handled via `SilentDialogHandler`)

- **WadTool.dll**: Local utility for WAD2 file manipulation

### Threading Model
- Main UI runs on standard MTA thread
- TombLib operations require STA thread (handled in `Wad2Processor.RunViaTombLib()`)
- Extraction operations run on background threads via `Task.Run()` to keep UI responsive

## Common Development Tasks

### Adding Support for New File Formats
1. Modify `AssetProcessor.cs` to detect new file signatures/magic numbers
2. Add parsing logic in appropriate `ParseTR*` methods
3. Update file filter in `MainWindow.xaml.cs` BtnBrowse_Click handler
4. Ensure proper texture/audio extraction for the new format

### Modifying Extraction Pipeline
1. **AssetProcessor**: Controls texture/audio extraction
   - Texture extraction: Converts indexed/paletted images to truecolor .tga
   - Audio extraction: Converts raw samples to .wav format
2. **Wad2Processor**: Controls 3D asset extraction
   - Uses TombLib for TR2-TR5 (LoadLevel + ConvertTrLevel)
   - Uses custom parser for TR1 (TR1Wad2Builder)

### Debugging TombLib Issues
TombLib can exhibit threading issues or infinite loops:
1. Check `Wad2Processor.cs` for STA thread handling patterns
2. Use `SilentDialogHandler` to prevent UI dialogs from blocking
3. Monitor progress logging for timeout detection (120s default)
4. Consider increasing timeout values in `Wad2Processor` constants

## Important Files to Review

### Entry Point
- `MainWindow.xaml.cs` - Main application flow
  - `BtnBrowse_Click`: File selection handler
  - `BtnExtract_Click`: Main extraction pipeline orchestrator

### Core Extraction Logic
- `AssetProcessor.cs`: Raw asset extraction (textures/audio)
  - Version detection via file magic numbers
  - Version-specific parsing methods (ParseTR1, ParseTR2TR3, ParseTR4TR5)
  - Palette handling for texture conversion
  - Audio sample extraction and WAV conversion

- `Wad2Processor.cs`: 3D asset/WAD2 generation
  - Format detection and routing
  - TR1 special handling via TR1Wad2Builder
  - TombLib integration for TR2-TR5 with STA threading
  - Progress reporting and timeout handling

### Specialized Parsers
- `TR1Wad2Builder.cs`: Custom TR1 parser that bypasses TombLib limitations
  - Direct binary parsing of TR1 structures
  - Manual WAD2 construction with proper UV mapping
  - Handles TR1-specific quirks (different data layout)

## Coding Conventions

### Naming
- PascalCase for methods, properties, classes
- `_camelCase` for private fields
- XML documentation comments for public members
- Region comments for logical code sections (using // â€œâ€œâ€œ pattern)

### Error Handling
- Try/catch blocks with detailed logging
- Exception logging includes type, message, and stack trace
- Inner exception logging when present
- User-friendly error messages via MessageBox for critical UI errors

### Threading
- UI updates use `Dispatcher.BeginInvoke` or `Dispatcher.Invoke`
- Long-running operations use `Task.Run`
- TombLib operations require STA threads with explicit Dispatcher setup
- Progress reporting via callback actions (`Action<string> logger`, `Action<double> progressReporter`)

### Resource Management
- Proper disposal of FileStream, BinaryReader via `using` statements
- Explicit GC.Collect() and GC.WaitForPendingFinalizers() after large operations
- Manual resource cleanup where necessary (setting objects to null)

## Troubleshooting

### Common Issues
1. **TombLib hangs/freezes**: Ensure STA thread is properly configured in Wad2Processor
2. **Missing textures/audio**: Verify file format detection in AssetProcessor
3. **WAD2 generation failures**: Check TombLib version compatibility
4. **UI freezing during extraction**: Verify background task usage

### Debugging Tips
1. Enable detailed logging in extraction methods
202` TODO: Add more detailed logging if needed
2. Check file format detection logic in AssetProcessor.ParseTR* methods
3. Verify TombLib STA thread setup in Wad2Processor.RunViaTombLib
4. Monitor memory usage during large file processing

## Dependencies

### NuGet Packages
- Blake3 (0.6.0) - Cryptographic hashing for file verification

### References
- TombLib.dll - From Tomb Editor installation
- WadTool.dll - Local reference in project directory

### File Format References
- TRosettaStone 3: https://opentomb.github.io/TRosettaStone3/
- Tomb Engine team: https://github.com/TombEngine
- Tomb Raider Chronicles: Texture and audio format references

## Notes for Future Development

### Potential Improvements
1. Add PRJ2 (level geometry) support as mentioned in README
2. Improve error handling and user feedback during extraction
3. Add support for additional texture/audio formats
4. Implement progress cancellation for long-running operations
5. Add batch processing capabilities
6. Improve UI feedback during extraction process

### Maintenance Considerations
- TombLib version compatibility with Tomb Editor updates
- .NET version updates and WPF compatibility
- File format variations across different Tomb Raider versions/platforms