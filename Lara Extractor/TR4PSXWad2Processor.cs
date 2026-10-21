using System;
using System.IO;

namespace Lara_Extractor
{
    /// <summary>
    /// Wad2 export for PlayStation TR4/TR5 levels stored in GAMEWAD.OBJ.
    ///
    /// Done so far: container and level header parsing (PSXGamewad, PSXLevel) and texture decoding
    /// (VRAM, tpage, CLUT). Not done yet: meshes, bones, animations and statics, which in the level file are
    /// the sections between the room data and the texture tables (see LoadLevel() in TOMB5's GAME/SETUP.C).
    /// </summary>
    public sealed class TR4PSXWad2Processor
    {
        private readonly string _inputPath;
        private readonly string _outputWad2Path;
        private readonly Action<string> _logger;

        public TR4PSXWad2Processor(string inputPath, string outputWad2Path, Action<string> logger)
        {
            _inputPath = inputPath;
            _outputWad2Path = outputWad2Path;
            _logger = logger;
        }

        public void ExtractWad2()
        {
            _logger("[PSX Wad2] ========== STARTING PSX WAD2 PIPELINE ==========");

            var gamewad = PSXGamewad.TryOpen(_inputPath);
            if (gamewad == null)
            {
                _logger("[PSX Wad2 Error] The file is not a valid GAMEWAD.OBJ.");
                return;
            }

            int levels = 0;
            foreach (var entry in gamewad.Entries)
            {
                if (entry.Size < 0x10000) continue;
                var level = PSXLevel.TryParse(gamewad.Read(entry), out _);
                if (level == null) continue;

                levels++;
                _logger($"[PSX Wad2] Level {entry.Index:D2}: {level.NumRooms} rooms, {level.NumLevelItems} items, " +
                        $"{level.ObjectTextures.Count} object textures, {level.RoomTextureSets} room texture sets.");
            }

            _logger($"[PSX Wad2] {levels} level(s) recognized.");
            _logger("[PSX Wad2] Wad2 export is not implemented yet (meshes, bones and animations are still to be decoded); no file written.");
        }
    }
}
