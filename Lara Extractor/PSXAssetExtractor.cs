using System;
using System.IO;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Lara_Extractor
{
    /// <summary>
    /// Asset extraction for PlayStation TR4/TR5 GAMEWAD.OBJ containers: one folder per level
    /// with the textures used by the level rooms (not the ones of moveables and statics).
    /// </summary>
    internal static class PSXAssetExtractor
    {
        public static bool IsPsxContainerName(string path)
        {
            string name = Path.GetFileName(path);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return name.StartsWith("GAMEWAD", StringComparison.OrdinalIgnoreCase) || ext == ".obj";
        }

        public static void Extract(string path, string outputDir, bool extractTextures, Action<string> log, Action<double> progress)
        {
            var gamewad = PSXGamewad.TryOpen(path);
            if (gamewad == null)
            {
                log("[PSX Error] The file is not a valid GAMEWAD.OBJ (invalid entry table).");
                return;
            }

            log($"[PSX] GAMEWAD.OBJ: {gamewad.Entries.Count} entries.");

            var levels = new List<(PSXGamewad.Entry Entry, PSXLevel Level)>();
            foreach (var entry in gamewad.Entries)
            {
                if (entry.Size < 0x10000) continue;
                var level = PSXLevel.TryParse(gamewad.Read(entry), out _);
                if (level != null)
                    levels.Add((entry, level));
            }

            log($"[PSX] {levels.Count} level(s) found.");
            if (!extractTextures)
            {
                log("[PSX] Texture extraction disabled; nothing to do.");
                return;
            }

            int done = 0;
            int totalFiles = 0;
            foreach (var (entry, level) in levels)
            {
                string levelDir = Path.Combine(outputDir, $"Level_{entry.Index:D2}", "Textures");
                Directory.CreateDirectory(levelDir);

                var seen = new HashSet<string>();
                int saved = 0;

                for (int i = 0; i < level.RoomTextures.Count; i++)
                {
                    if (!level.TryRender(level.RoomTextures[i], out int w, out int h, out byte[] bgra))
                        continue;

                    if (!seen.Add(Hash(w, h, bgra)))
                        continue;

                    // Each room texture set holds three versions (full size and smaller ones).
                    string name = $"Room_{i / 3:D4}_{i % 3}.tga";
                    WriteTga(Path.Combine(levelDir, name), bgra, w, h);
                    saved++;
                }

                totalFiles += saved;
                done++;
                progress(done * 100.0 / levels.Count);
                log($"[PSX Tex] Level {entry.Index:D2}: {saved} unique room texture(s) from {level.RoomTextures.Count} entries, {level.NumRooms} rooms.");
            }

            log($"[PSX Tex] {totalFiles} texture(s) saved in {levels.Count} level folder(s).");
        }

        private static string Hash(int w, int h, byte[] bgra)
        {
            using var sha = SHA1.Create();
            sha.TransformBlock(BitConverter.GetBytes(w), 0, 4, null, 0);
            sha.TransformBlock(BitConverter.GetBytes(h), 0, 4, null, 0);
            sha.TransformFinalBlock(bgra, 0, bgra.Length);
            return Convert.ToHexString(sha.Hash!);
        }

        internal static void WriteTga(string path, byte[] bgra, int width, int height)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            var header = new byte[18];
            header[2] = 2;                       // uncompressed true-color
            header[12] = (byte)(width & 255); header[13] = (byte)(width >> 8);
            header[14] = (byte)(height & 255); header[15] = (byte)(height >> 8);
            header[16] = 32;                     // bits per pixel
            header[17] = 0x28;                   // 8 alpha bits, top-left origin
            fs.Write(header, 0, header.Length);
            fs.Write(bgra, 0, width * height * 4);
        }
    }
}
