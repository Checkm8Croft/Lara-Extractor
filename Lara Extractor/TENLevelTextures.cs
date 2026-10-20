using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lara_Extractor
{
    /// <summary>
    /// Extracts the individual textures used by the level rooms (not the game assets: moveables, statics, sprites).
    /// Room polygons store normalized UVs into the room (or animated) atlas, so the textures are recovered by cropping
    /// the atlas to the rectangles those UVs cover, then dropping rectangles contained in a bigger one.
    /// </summary>
    public sealed partial class TENAssetProcessor
    {
        private const int LevelGroupRoom = 0;
        private const int LevelGroupAnimated = 3;

        // (atlas group, atlas index) -> atlas as stored in the media block
        private readonly Dictionary<(int Group, int Index), (int W, int H, byte[] Blob)> _levelAtlases = new();

        private readonly record struct PixelRect(int X0, int Y0, int X1, int Y1)
        {
            public int Width => X1 - X0;
            public int Height => Y1 - Y0;
            public long Area => (long)Width * Height;
            public bool Contains(PixelRect o) => o.X0 >= X0 && o.Y0 >= Y0 && o.X1 <= X1 && o.Y1 <= Y1;
        }

        private void ExtractLevelTextures(byte[] geometry)
        {
            var rects = CollectRoomTextureRects(geometry);

            string dir = Path.Combine(_outputDir, "Textures", "Level");
            Directory.CreateDirectory(dir);

            int saved = 0, rawRects = 0;
            foreach (var ((group, atlasIndex), set) in rects.OrderBy(k => k.Key.Item1).ThenBy(k => k.Key.Item2))
            {
                rawRects += set.Count;

                if (!_levelAtlases.TryGetValue((group, atlasIndex), out var atlas))
                {
                    _logger($"[TEN Level Tex Warning] Room polygons reference missing atlas {group}/{atlasIndex}.");
                    continue;
                }

                byte[]? bgra = TENWad2Processor.DecodeToBgra(atlas.Blob, atlas.W, atlas.H, out int width, out int height);
                if (bgra == null)
                {
                    _logger($"[TEN Level Tex Warning] Atlas {group}/{atlasIndex} could not be decoded.");
                    continue;
                }

                // Biggest first; keep a rectangle only if no already kept rectangle contains it.
                var kept = new List<PixelRect>();
                foreach (var rect in set.OrderByDescending(r => r.Area).ThenBy(r => r.Y0).ThenBy(r => r.X0))
                {
                    if (rect.X0 < 0 || rect.Y0 < 0 || rect.X1 > width || rect.Y1 > height || rect.Width < 1 || rect.Height < 1)
                        continue;
                    if (!kept.Any(k => k.Contains(rect)))
                        kept.Add(rect);
                }

                string prefix = group == LevelGroupAnimated ? "Animated" : "Room";
                int n = 0;
                foreach (var rect in kept.OrderBy(r => r.Y0).ThenBy(r => r.X0))
                {
                    var crop = new byte[rect.Width * rect.Height * 4];
                    for (int y = 0; y < rect.Height; y++)
                        Buffer.BlockCopy(bgra, ((rect.Y0 + y) * width + rect.X0) * 4, crop, y * rect.Width * 4, rect.Width * 4);

                    string name = $"{prefix}_{atlasIndex:D2}_{n:D4}_x{rect.X0}_y{rect.Y0}_{rect.Width}x{rect.Height}.tga";
                    WriteBgraAsTga(Path.Combine(dir, name), crop, rect.Width, rect.Height);
                    n++;
                    saved++;
                }
            }

            _logger($"[TEN Level Tex] {saved} level texture(s) saved to Textures\\Level (from {rawRects} distinct UV rectangles).");
        }

        /// <summary>
        /// Walks every room polygon and records the pixel rectangle covered by its UVs, per atlas.
        /// Room layout comes from LoadStaticRoomData() in TombEngine's level.cpp (room polygons have no shine field).
        /// </summary>
        private Dictionary<(int, int), HashSet<PixelRect>> CollectRoomTextureRects(byte[] geometry)
        {
            var result = new Dictionary<(int, int), HashSet<PixelRect>>();

            using var ms = new MemoryStream(geometry, writable: false);
            using var r = new BinaryReader(ms);
            var s = r.BaseStream;

            int roomCount = ReadCount(r, 1024 * 1024);
            for (int room = 0; room < roomCount; room++)
            {
                s.Seek(4 * 4, SeekOrigin.Current);                          // x, z, bottom, top

                int vertexCount = ReadCount(r, 1024 * 1024 * 1024);
                s.Seek(3L * 12 * vertexCount, SeekOrigin.Current);          // positions, colors, effects

                int bucketCount = ReadCount(r, 1024 * 1024);
                for (int b = 0; b < bucketCount; b++)
                {
                    int texture = r.ReadInt32();
                    r.ReadByte();                                           // blend mode
                    r.ReadInt32();                                          // material
                    bool animated = r.ReadBoolean();
                    int polyCount = ReadCount(r, 1024 * 1024 * 1024);

                    int group = animated ? LevelGroupAnimated : LevelGroupRoom;
                    _levelAtlases.TryGetValue((group, texture), out var atlas);

                    for (int p = 0; p < polyCount; p++)
                    {
                        int shape = r.ReadInt32();
                        int count = shape == 0 ? 4 : 3;
                        s.Seek(4 + 4 + 12 + count * 4, SeekOrigin.Current); // animSeq, animFrame, normal, indices

                        float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
                        for (int k = 0; k < count; k++)
                        {
                            float u = r.ReadSingle(), v = r.ReadSingle();
                            minU = MathF.Min(minU, u); maxU = MathF.Max(maxU, u);
                            minV = MathF.Min(minV, v); maxV = MathF.Max(maxV, v);
                        }
                        s.Seek(count * (12 + 12 + 12), SeekOrigin.Current); // normals, tangents, binormals

                        if (atlas.W <= 0 || atlas.H <= 0) continue;

                        int x0 = Math.Clamp((int)MathF.Floor(minU * atlas.W), 0, atlas.W - 1);
                        int y0 = Math.Clamp((int)MathF.Floor(minV * atlas.H), 0, atlas.H - 1);
                        int x1 = Math.Clamp((int)MathF.Ceiling(maxU * atlas.W), x0 + 1, atlas.W);
                        int y1 = Math.Clamp((int)MathF.Ceiling(maxV * atlas.H), y0 + 1, atlas.H);

                        if (!result.TryGetValue((group, texture), out var set))
                            result[(group, texture)] = set = new HashSet<PixelRect>();
                        set.Add(new PixelRect(x0, y0, x1, y1));
                    }
                }

                int portalCount = ReadCount(r, 1024 * 1024);
                s.Seek((long)portalCount * (2 + 12 + 4 * 12), SeekOrigin.Current);

                int zSize = r.ReadInt32();
                int xSize = r.ReadInt32();
                if (zSize < 0 || xSize < 0 || (long)zSize * xSize > 1024 * 1024)
                    throw new InvalidDataException($"Invalid room size {xSize}x{zSize} in room {room}; room layout is out of sync.");
                s.Seek((long)zSize * xSize * 100, SeekOrigin.Current);      // sectors

                int lightCount = ReadCount(r, 1024 * 1024);
                s.Seek((long)lightCount * 58, SeekOrigin.Current);
            }

            return result;
        }
    }
}
