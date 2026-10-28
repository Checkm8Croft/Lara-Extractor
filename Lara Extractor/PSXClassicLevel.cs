using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lara_Extractor
{
    /// <summary>
    /// A single PlayStation level file (.PSX) of Tomb Raider 1-3.
    ///
    /// Layout of TR3 PSX (from the trview project, Level_tr3_psx.ixx, checked on a TR3 .PSX file whose tables end 10 bytes
    /// before the end of the file):
    ///   int32 version (0xFFFFFFC8) | sound offsets + VAG sound data | 13 skipped blocks | rooms | floor data | outside map |
    ///   mesh data + pointers | animations, state changes, dispatches, commands | mesh tree | frames | models | statics |
    ///   4-bit textiles (256x256) + CLUTs | object textures | sprite textures + sequences | cameras, sound sources, boxes,
    ///   overlaps, zones, animated textures, entities | room textures | sound map and details.
    /// Rooms are skipped by size; only what is needed for assets and Wad2 is kept.
    /// </summary>
    internal sealed class PSXClassicLevel
    {
        public enum GameKind { TR1, TR2, TR3 }

        public const int TextileSize = 256 * 256 / 2;   // 4-bit, 256x256
        public const int ClutSize = 32;                 // 16 colors of 16 bits

        /// <summary>Texture record (16 bytes): u0 v0 clut | u1 v1 tile | u2 v2 triDraw quadDraw | u3 v3 attribute.</summary>
        public readonly record struct ObjectTexture(byte[] U, byte[] V, ushort Clut, ushort Tile, byte TriDraw, byte QuadDraw, ushort Attribute);

        public readonly record struct Model(int Id, int NumMeshes, int StartingMesh, int MeshTree, int FrameByteOffset, int Animation);

        public readonly record struct StaticMesh(int Id, int Mesh, short[] Visibility, short[] Collision, int Flags);

        public readonly record struct SpriteTexture(ushort Clut, ushort Tile, int U0, int V0, int U1, int V1);

        public readonly record struct SpriteSequence(int Id, int Count, int Offset);

        public GameKind Game { get; private set; }

        /// <summary>Sample rate of the sound data: 11025 Hz in TR3, 8000 Hz in TR2 (values used by trview).</summary>
        public int SoundSampleRate => Game == GameKind.TR2 ? 8000 : 11025;
        public byte[] Data { get; }

        public List<(int Start, int Length)> Sounds { get; } = new();

        public int MeshDataOffset { get; private set; }
        public uint[] MeshPointers { get; private set; } = Array.Empty<uint>();
        public int AnimOffset { get; private set; }
        public int AnimCount { get; private set; }
        public int ChangeOffset { get; private set; }
        public int ChangeCount { get; private set; }
        public int DispatchOffset { get; private set; }
        public int DispatchCount { get; private set; }
        public int CommandOffset { get; private set; }
        public int CommandCount { get; private set; }
        public int MeshTreeOffset { get; private set; }
        public int MeshTreeCount { get; private set; }
        public int FrameOffset { get; private set; }
        public int FrameCount { get; private set; }     // in 16-bit words

        public List<Model> Models { get; } = new();
        public List<StaticMesh> Statics { get; } = new();

        public int TextileOffset { get; private set; }
        public int TextileCount { get; private set; }
        public int ClutOffset { get; private set; }
        public int ClutCount { get; private set; }

        public List<ObjectTexture> ObjectTextures { get; } = new();
        public List<ObjectTexture> RoomTextures { get; } = new();
        public List<SpriteTexture> SpriteTextures { get; } = new();
        public List<SpriteSequence> SpriteSequences { get; } = new();

        private int _p;

        private PSXClassicLevel(byte[] data) => Data = data;

        // ------------------------------------------------------------ reading helpers

        private int I32() { Check(4); int v = BitConverter.ToInt32(Data, _p); _p += 4; return v; }
        private uint U32() { Check(4); uint v = BitConverter.ToUInt32(Data, _p); _p += 4; return v; }
        private int U16() { Check(2); int v = BitConverter.ToUInt16(Data, _p); _p += 2; return v; }
        private void Skip(long n)
        {
            if (n < 0 || _p + n > Data.Length) throw new InvalidDataException($"skip of {n} bytes at {_p} runs past the end of the file");
            _p += (int)n;
        }
        private void Check(int n)
        {
            if (_p + n > Data.Length) throw new InvalidDataException($"read at {_p} runs past the end of the file");
        }

        /// <summary>Reads a uint32 count and returns the position of the elements, advancing past them.</summary>
        private (int Count, int Offset) Vector(int elementSize)
        {
            uint count = U32();
            if ((long)count * elementSize > Data.Length - _p) throw new InvalidDataException($"vector of {count} x {elementSize} bytes at {_p} does not fit in the file");
            int offset = _p;
            _p += (int)count * elementSize;
            return ((int)count, offset);
        }

        // ------------------------------------------------------------ parsing

        public static bool IsTr3(byte[] data) => data.Length > 16 && BitConverter.ToInt32(data, 0) == unchecked((int)0xFFFFFFC8);

        /// <summary>TR2 PSX files start with the sound data and have the version (45) right after it.</summary>
        private bool IsTr2()
        {
            try
            {
                _p = 0;
                uint count = U32();
                if (count > 10000) return false;
                Skip(count * 4L);
                uint size = U32();
                Skip(size);
                return I32() == 45;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        public static PSXClassicLevel? TryParse(byte[] data, out string error)
        {
            error = "";
            var level = new PSXClassicLevel(data);
            try
            {
                if (IsTr3(data)) level.ParseTr3();
                else if (level.IsTr2()) level.ParseTr2();
                else
                {
                    error = "unsupported or unrecognized PSX level version (supported: TR2 version 45 and TR3)";
                    return null;
                }
                return level;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is ArgumentException || ex is IndexOutOfRangeException)
            {
                error = ex.Message;
                return null;
            }
        }

        private void ParseTr3()
        {
            Game = GameKind.TR3;
            _p = 4;                                                   // version

            ReadSounds();

            // 13 blocks of unknown data, each possibly made of two parts.
            for (int i = 0; i < 13; i++)
            {
                int size = I32();
                if (size != 0)
                {
                    Skip(size);
                    int size2 = I32();
                    Skip(size2);
                }
            }

            int rooms = U16();
            for (int r = 0; r < rooms; r++)
            {
                Skip(16);                                             // room info
                uint words = U32();
                Skip(words * 2L);                                     // vertices/faces in "roomlets"
                Skip(U16() * 32L);                                    // portals
                int z = U16(), x = U16();
                Skip(z * x * 8L);                                     // sectors
                Skip(4);                                              // ambient intensity, light mode
                Skip(U16() * 24L);                                    // lights
                Skip(U16() * 20L);                                    // static meshes
                Skip(2 + 2 + 1 + 1 + 1);                              // alternate room, flags, water scheme, reverb, filler
            }

            ReadCommonTables(tr3: true);
        }

        /// <summary>
        /// TR2 PSX, version 45 (layout from trview, Level_tr2_psx.ixx, checked on a TR2 .PSX file): the sound data comes
        /// first, then the version, rooms, and tables like TR3's but without the outside map and the room textures.
        /// Textures used by the rooms are taken from the room faces.
        /// </summary>
        private void ParseTr2()
        {
            Game = GameKind.TR2;
            _p = 0;

            ReadSounds();
            I32();                                                    // version (45)

            int rooms = U16();
            for (int r = 0; r < rooms; r++)
            {
                Skip(16);                                             // room info
                uint words = U32();
                if (words > 0) CollectRoomTextures(_p, (int)words);
                Skip(words * 2L);                                     // vertices/faces
                Skip(U16() * 32L);                                    // portals
                int z = U16(), x = U16();
                Skip(z * x * 8L);                                     // sectors
                Skip(6);                                              // ambient intensity 1 and 2, light mode
                Skip(U16() * 24L);                                    // lights
                Skip(U16() * 20L);                                    // static meshes
                Skip(4);                                              // alternate room, flags
            }

            ReadCommonTables(tr3: false);

            // Room textures of TR2: the object textures that room faces point to.
            foreach (int index in _roomTextureIndices.OrderBy(i => i))
                if (index < ObjectTextures.Count)
                    RoomTextures.Add(ObjectTextures[index]);
        }

        private readonly HashSet<int> _roomTextureIndices = new();

        /// <summary>Room data of TR2 PSX: vertices (uint32), rectangles (textures, then vertices), triangles (texture, vertices).</summary>
        private void CollectRoomTextures(int start, int words)
        {
            try
            {
                int end = start + words * 2;
                if (end > Data.Length) return;

                int q = start;
                int vertices = BitConverter.ToUInt16(Data, q);
                q += 4 + vertices * 4;

                int rectStart = q;
                int rects = BitConverter.ToUInt16(Data, q);
                q += 2;
                for (int k = 0; k < rects && q + k * 2 + 2 <= end; k++)
                    _roomTextureIndices.Add(BitConverter.ToUInt16(Data, q + k * 2) & 0x7FFF);
                q += rects * 2;
                if ((q - rectStart) % 4 != 0) q += 2;
                q += rects * 8;

                int triangles = BitConverter.ToInt16(Data, q);
                q += 2;
                if ((q - rectStart) % 4 != 0) q += 2;
                for (int k = 0; k < triangles && q + k * 8 + 2 <= end; k++)
                    _roomTextureIndices.Add(BitConverter.ToUInt16(Data, q + k * 8) & 0x7FFF);
            }
            catch (ArgumentException)
            {
            }
        }

        /// <summary>Sounds: offsets into the sound data, then the sound data itself (PlayStation ADPCM).</summary>
        private void ReadSounds()
        {
            var (soundCount, soundOffsetsPos) = Vector(4);
            if (soundCount > 0)
            {
                uint size = U32();
                if (size > Data.Length - _p) throw new InvalidDataException("sound data does not fit in the file");
                int soundDataPos = _p;
                for (int i = 0; i < soundCount; i++)
                {
                    int start = (int)BitConverter.ToUInt32(Data, soundOffsetsPos + i * 4);
                    int limit = i + 1 < soundCount ? (int)BitConverter.ToUInt32(Data, soundOffsetsPos + (i + 1) * 4) : (int)size;
                    if (start < 0 || start >= size || limit > size || limit <= start) continue;
                    int length = PSXAdpcm.FindSampleLength(Data, soundDataPos + start, soundDataPos + limit);
                    if (length >= PSXAdpcm.BlockSize) Sounds.Add((soundDataPos + start, length));
                }
                Skip(size);
            }
        }

        /// <summary>Tables that follow the rooms, shared by TR2 and TR3 PSX (TR3 has a few extra ones).</summary>
        private void ReadCommonTables(bool tr3)
        {
            Vector(2);                                                // floor data
            if (tr3)
            {
                Skip(729 * 2);                                        // room outside map
                Vector(1);                                            // outside room table
                Skip(I32() * 8L);                                     // bounding boxes
            }

            var meshData = Vector(2);
            MeshDataOffset = meshData.Offset;
            var meshPointers = Vector(4);
            MeshPointers = new uint[meshPointers.Count];
            for (int i = 0; i < MeshPointers.Length; i++) MeshPointers[i] = BitConverter.ToUInt32(Data, meshPointers.Offset + i * 4);

            (AnimCount, AnimOffset) = Vector(32);
            (ChangeCount, ChangeOffset) = Vector(6);
            (DispatchCount, DispatchOffset) = Vector(8);
            (CommandCount, CommandOffset) = Vector(2);
            (MeshTreeCount, MeshTreeOffset) = Vector(4);
            (FrameCount, FrameOffset) = Vector(2);

            var models = Vector(20);
            for (int i = 0; i < models.Count; i++)
            {
                int p = models.Offset + i * 20;
                Models.Add(new Model((int)BitConverter.ToUInt32(Data, p), BitConverter.ToUInt16(Data, p + 4), BitConverter.ToUInt16(Data, p + 6),
                    (int)BitConverter.ToUInt32(Data, p + 8), (int)BitConverter.ToUInt32(Data, p + 12), BitConverter.ToUInt16(Data, p + 16)));
            }

            var statics = Vector(32);
            for (int i = 0; i < statics.Count; i++)
            {
                int p = statics.Offset + i * 32;
                var vis = new short[6];
                var col = new short[6];
                for (int k = 0; k < 6; k++)
                {
                    vis[k] = BitConverter.ToInt16(Data, p + 6 + k * 2);
                    col[k] = BitConverter.ToInt16(Data, p + 18 + k * 2);
                }
                Statics.Add(new StaticMesh((int)BitConverter.ToUInt32(Data, p), BitConverter.ToUInt16(Data, p + 4), vis, col, BitConverter.ToUInt16(Data, p + 30)));
            }

            // 4-bit textiles and CLUTs.
            TextileCount = (int)U32();
            TextileOffset = _p;
            Skip((long)TextileCount * TextileSize);

            uint clutWords = U32();
            if ((clutWords & 0xFFFF0000) != 0)
            {
                _p -= 2;                                              // the count was misaligned by 2 bytes
                clutWords = U32();
            }
            ClutCount = tr3 ? (int)clutWords * 2 : (int)clutWords;   // TR3 counts pairs of CLUTs
            ClutOffset = _p;
            Skip((long)ClutCount * ClutSize);
            if (!tr3) Skip(4);

            var objectTextures = Vector(16);
            for (int i = 0; i < objectTextures.Count; i++)
                ObjectTextures.Add(ReadTexture(objectTextures.Offset + i * 16));

            var spriteTextures = Vector(16);
            for (int i = 0; i < spriteTextures.Count; i++)
            {
                int p = spriteTextures.Offset + i * 16;
                SpriteTextures.Add(new SpriteTexture(BitConverter.ToUInt16(Data, p + 8), BitConverter.ToUInt16(Data, p + 10), Data[p + 12], Data[p + 13], Data[p + 14], Data[p + 15]));
            }

            var spriteSequences = Vector(8);
            for (int i = 0; i < spriteSequences.Count; i++)
            {
                int p = spriteSequences.Offset + i * 8;
                SpriteSequences.Add(new SpriteSequence(BitConverter.ToInt32(Data, p), -BitConverter.ToInt16(Data, p + 4), BitConverter.ToInt16(Data, p + 6)));
            }

            if (!tr3) return;

            Vector(16);                                               // cameras
            Vector(16);                                               // sound sources
            var boxes = Vector(8);
            Vector(2);                                                // overlaps
            Skip(boxes.Count * 10L * 2);                              // zones
            Skip(U32() * 2L);                                         // animated textures
            Vector(24);                                               // entities
            Skip(4);                                                  // horizon colour

            // Room textures: each record is followed by two more records that are not needed.
            uint roomTextures = U32();
            for (uint i = 0; i < roomTextures; i++)
            {
                Check(48);
                RoomTextures.Add(ReadTexture(_p));
                _p += 16 + 32;
            }
        }

        private ObjectTexture ReadTexture(int p)
        {
            var u = new[] { Data[p], Data[p + 4], Data[p + 8], Data[p + 12] };
            var v = new[] { Data[p + 1], Data[p + 5], Data[p + 9], Data[p + 13] };
            return new ObjectTexture(u, v, BitConverter.ToUInt16(Data, p + 2), BitConverter.ToUInt16(Data, p + 6), Data[p + 10], Data[p + 11], BitConverter.ToUInt16(Data, p + 14));
        }

        // ------------------------------------------------------------ meshes

        /// <summary>Reads the mesh at a mesh-pointer index (the pointer is a byte offset into the mesh data).</summary>
        public PsxMesh? ReadMesh(int pointerIndex)
        {
            if (pointerIndex < 0 || pointerIndex >= MeshPointers.Length) return null;
            if (Game == GameKind.TR2) return ReadMeshTr2(pointerIndex);

            try
            {
                int m = MeshDataOffset + (int)MeshPointers[pointerIndex];
                if (m < 0 || m + 12 > Data.Length) return null;

                var mesh = new PsxMesh
                {
                    CenterX = BitConverter.ToInt16(Data, m),
                    CenterY = BitConverter.ToInt16(Data, m + 2),
                    CenterZ = BitConverter.ToInt16(Data, m + 4),
                    Radius = BitConverter.ToInt16(Data, m + 6)
                };

                int vertexCount = Data[m + 8];
                mesh.Flags = Data[m + 9];
                mesh.PreLit = (mesh.Flags & 0x80) != 0;
                int faceOffset = BitConverter.ToUInt16(Data, m + 10);
                if (vertexCount == 0) return null;

                int vs = m + 12;
                if (vs + vertexCount * 8 > Data.Length) return null;
                mesh.Vertices = new (short, short, short, short)[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    int p = vs + i * 8;
                    mesh.Vertices[i] = (BitConverter.ToInt16(Data, p), BitConverter.ToInt16(Data, p + 2), BitConverter.ToInt16(Data, p + 4), BitConverter.ToInt16(Data, p + 6));
                }

                if (mesh.PreLit)
                {
                    // One 16-bit value per vertex follows; stored in W (read as 5-5-5 color, see PSXAssetNotes).
                    for (int i = 0; i < vertexCount; i++)
                    {
                        var v = mesh.Vertices[i];
                        mesh.Vertices[i] = (v.X, v.Y, v.Z, BitConverter.ToInt16(Data, vs + vertexCount * 8 + i * 2));
                    }
                }
                else
                {
                    mesh.Normals = new (short, short, short)[vertexCount];
                    for (int i = 0; i < vertexCount; i++)
                    {
                        int p = vs + vertexCount * 8 + i * 8;
                        mesh.Normals[i] = (BitConverter.ToInt16(Data, p), BitConverter.ToInt16(Data, p + 2), BitConverter.ToInt16(Data, p + 4));
                    }
                }

                int ps = vs + faceOffset;
                if (ps + 4 > Data.Length) return null;
                int triCount = BitConverter.ToUInt16(Data, ps);
                int quadCount = BitConverter.ToUInt16(Data, ps + 2);
                int q = ps + 4;
                int textures = ObjectTextures.Count;   // room faces use the room textures, meshes only the object textures

                uint faceA = 0;
                for (int t = 0; t < triCount; t++)
                {
                    if (t % 4 == 0) { if (q + 4 > Data.Length) return null; faceA = BitConverter.ToUInt32(Data, q); q += 4; }
                    if (q + 4 > Data.Length) return null;
                    uint faceB = BitConverter.ToUInt32(Data, q); q += 4;
                    int a = (int)(faceB & 0xFF), b = (int)((faceB >> 8) & 0xFF), c = (int)((faceB >> 16) & 0xFF);
                    int tex = (int)((faceA & 0xFF) | (((faceB >> 24) & 0xFF) << 8));
                    if (a >= vertexCount || b >= vertexCount || c >= vertexCount || tex >= textures) return null;
                    mesh.Triangles.Add((a, b, c, tex));
                    faceA >>= 8;
                }

                for (int r = 0; r < quadCount; r++)
                {
                    if (r % 2 == 0) { if (q + 4 > Data.Length) return null; faceA = BitConverter.ToUInt32(Data, q); q += 4; }
                    if (q + 4 > Data.Length) return null;
                    uint faceB = BitConverter.ToUInt32(Data, q); q += 4;
                    int a = (int)(faceB & 0xFF), b = (int)((faceB >> 8) & 0xFF), c = (int)((faceB >> 16) & 0xFF), d = (int)((faceB >> 24) & 0xFF);
                    int tex = (int)(faceA & 0xFFFF);
                    if (a >= vertexCount || b >= vertexCount || c >= vertexCount || d >= vertexCount || tex >= textures) return null;
                    mesh.Quads.Add((a, b, c, d, tex));
                    faceA >>= 16;
                }

                return mesh;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// TR2 PSX mesh: centre (3 x int16), radius (int32), signed vertex count (positive = normals + reflective faces,
        /// negative = pre-computed lights), vertices (8 bytes), then textured rectangles and triangles.
        /// Quad vertices are stored as an outline; they are converted to the Z-order PsxMesh convention.
        /// </summary>
        private PsxMesh? ReadMeshTr2(int pointerIndex)
        {
            try
            {
                int m = MeshDataOffset + (int)MeshPointers[pointerIndex];
                if (m < 0 || m + 12 > Data.Length) return null;

                int signedCount = BitConverter.ToInt16(Data, m + 10);
                int n = Math.Abs(signedCount);
                if (n == 0) return null;

                var mesh = new PsxMesh
                {
                    CenterX = BitConverter.ToInt16(Data, m),
                    CenterY = BitConverter.ToInt16(Data, m + 2),
                    CenterZ = BitConverter.ToInt16(Data, m + 4),
                    Radius = (short)Math.Clamp(BitConverter.ToInt32(Data, m + 6), short.MinValue, short.MaxValue),
                    PreLit = signedCount < 0
                };

                int q = m + 12;
                if (q + n * 8 > Data.Length) return null;
                mesh.Vertices = new (short, short, short, short)[n];
                for (int i = 0; i < n; i++)
                    mesh.Vertices[i] = (BitConverter.ToInt16(Data, q + i * 8), BitConverter.ToInt16(Data, q + i * 8 + 2), BitConverter.ToInt16(Data, q + i * 8 + 4), BitConverter.ToInt16(Data, q + i * 8 + 6));
                q += n * 8;

                bool AddQuad(int pos)
                {
                    int p0 = BitConverter.ToUInt16(Data, pos) >> 3, p1 = BitConverter.ToUInt16(Data, pos + 2) >> 3;
                    int p2 = BitConverter.ToUInt16(Data, pos + 4) >> 3, p3 = BitConverter.ToUInt16(Data, pos + 6) >> 3;
                    int tex = BitConverter.ToUInt16(Data, pos + 8) & 0x7FFF;
                    if (p0 >= n || p1 >= n || p2 >= n || p3 >= n || tex >= ObjectTextures.Count) return false;
                    mesh.Quads.Add((p0, p1, p3, p2, tex));            // outline p0 p1 p2 p3 = Z-order A B D C
                    return true;
                }

                bool AddTriangle(int pos)
                {
                    int a = BitConverter.ToUInt16(Data, pos) >> 3, b = BitConverter.ToUInt16(Data, pos + 2) >> 3, c = BitConverter.ToUInt16(Data, pos + 4) >> 3;
                    int tex = BitConverter.ToUInt16(Data, pos + 6) & 0x7FFF;
                    if (a >= n || b >= n || c >= n || tex >= ObjectTextures.Count) return false;
                    mesh.Triangles.Add((a, b, c, tex));
                    return true;
                }

                if (signedCount > 0)
                {
                    mesh.Normals = new (short, short, short)[n];
                    for (int i = 0; i < n; i++)
                        mesh.Normals[i] = (BitConverter.ToInt16(Data, q + i * 8), BitConverter.ToInt16(Data, q + i * 8 + 2), BitConverter.ToInt16(Data, q + i * 8 + 4));
                    q += n * 8;

                    int reflectiveRects = BitConverter.ToInt16(Data, q); q += 2;
                    for (int i = 0; i < reflectiveRects; i++, q += 12) if (!AddQuad(q)) return null;
                    int reflectiveTris = BitConverter.ToInt16(Data, q); q += 2;
                    for (int i = 0; i < reflectiveTris; i++, q += 10) if (!AddTriangle(q)) return null;
                }
                else
                {
                    // Pre-computed lights, one int16 per vertex (stored in W, read as 5-5-5 color).
                    for (int i = 0; i < n; i++)
                    {
                        var v = mesh.Vertices[i];
                        mesh.Vertices[i] = (v.X, v.Y, v.Z, BitConverter.ToInt16(Data, q + i * 2));
                    }
                    q += n * 2;
                }

                int rects = BitConverter.ToInt16(Data, q); q += 2;
                for (int i = 0; i < rects; i++, q += 10) if (!AddQuad(q)) return null;
                int tris = BitConverter.ToInt16(Data, q); q += 2;
                for (int i = 0; i < tris; i++, q += 8) if (!AddTriangle(q)) return null;

                return mesh;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        // ------------------------------------------------------------ animations

        public int ReadMeshTreeInt(int index)
            => (index < 0 || index >= MeshTreeCount) ? 0 : BitConverter.ToInt32(Data, MeshTreeOffset + index * 4);

        public short ReadCommand(int index)
            => (index < 0 || index >= CommandCount) ? (short)0 : BitConverter.ToInt16(Data, CommandOffset + index * 2);

        public short[]? ReadFrameWords(long byteOffset, int words)
        {
            if (byteOffset < 0 || words <= 0 || byteOffset + words * 2L > FrameCount * 2L) return null;
            var result = new short[words];
            Buffer.BlockCopy(Data, FrameOffset + (int)byteOffset, result, 0, words * 2);
            return result;
        }

        public (int State, int Count, int First) ReadChange(int index)
        {
            if (index < 0 || index >= ChangeCount) return (0, 0, 0);
            int p = ChangeOffset + index * 6;
            return (BitConverter.ToUInt16(Data, p), BitConverter.ToUInt16(Data, p + 2), BitConverter.ToUInt16(Data, p + 4));
        }

        public (int Low, int High, int NextAnim, int NextFrame) ReadDispatch(int index)
        {
            if (index < 0 || index >= DispatchCount) return (0, 0, 0, 0);
            int p = DispatchOffset + index * 8;
            return (BitConverter.ToInt16(Data, p), BitConverter.ToInt16(Data, p + 2), BitConverter.ToInt16(Data, p + 4), BitConverter.ToInt16(Data, p + 6));
        }

        /// <summary>Animation record (32 bytes). Speed and acceleration are 16.16 fixed point.</summary>
        public PsxClassicAnim? ReadAnim(int index)
        {
            if (index < 0 || index >= AnimCount) return null;
            int p = AnimOffset + index * 32;
            return new PsxClassicAnim
            {
                FrameByteOffset = BitConverter.ToUInt32(Data, p),
                Rate = Data[p + 4],
                FrameSizeWords = Data[p + 5],
                State = BitConverter.ToUInt16(Data, p + 6),
                Speed = BitConverter.ToInt32(Data, p + 8),
                Accel = BitConverter.ToInt32(Data, p + 12),
                FrameStart = BitConverter.ToUInt16(Data, p + 16),
                FrameEnd = BitConverter.ToUInt16(Data, p + 18),
                NextAnim = BitConverter.ToUInt16(Data, p + 20),
                NextFrame = BitConverter.ToUInt16(Data, p + 22),
                NumChanges = BitConverter.ToUInt16(Data, p + 24),
                ChangeIndex = BitConverter.ToUInt16(Data, p + 26),
                NumCommands = BitConverter.ToUInt16(Data, p + 28),
                CommandIndex = BitConverter.ToUInt16(Data, p + 30)
            };
        }

        // ------------------------------------------------------------ textures

        /// <summary>A triangle leaves its fourth corner at (0,0); treat it as a triangle unless that is a real corner.</summary>
        public static bool LooksLikeTriangle(ObjectTexture t)
            => t.U[3] == 0 && t.V[3] == 0 && Math.Min(Math.Min(t.U[0], t.U[1]), t.U[2]) > 0;

        /// <summary>Bounding rectangle (inclusive, inside the 256x256 tile) covered by the texture corners.</summary>
        public static (int U0, int V0, int U1, int V1) GetRect(ObjectTexture t, bool triangle)
        {
            int count = triangle ? 3 : 4;
            int u0 = 255, u1 = 0, v0 = 255, v1 = 0;
            for (int i = 0; i < count; i++)
            {
                u0 = Math.Min(u0, t.U[i]); u1 = Math.Max(u1, t.U[i]);
                v0 = Math.Min(v0, t.V[i]); v1 = Math.Max(v1, t.V[i]);
            }
            return (u0, v0, u1, v1);
        }

        public bool TryRender(ObjectTexture t, bool triangle, out int width, out int height, out byte[] bgra)
        {
            var (u0, v0, u1, v1) = GetRect(t, triangle);
            return TryRenderRect(t.Tile, t.Clut, u0, v0, u1, v1, out width, out height, out bgra);
        }

        public bool TryRenderRect(int tile, int clut, int u0, int v0, int u1, int v1, out int width, out int height, out byte[] bgra)
        {
            width = u1 - u0 + 1;
            height = v1 - v0 + 1;
            bgra = Array.Empty<byte>();
            if (width < 1 || height < 1 || tile < 0 || tile >= TextileCount || clut < 0 || clut >= ClutCount) return false;

            int tileBase = TextileOffset + tile * TextileSize;
            int clutBase = ClutOffset + clut * ClutSize;
            bgra = new byte[width * height * 4];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int u = u0 + x, v = v0 + y;
                    int packed = Data[tileBase + v * 128 + u / 2];
                    int index = (u & 1) == 0 ? packed & 15 : packed >> 4;
                    ushort color = BitConverter.ToUInt16(Data, clutBase + index * 2);
                    int o = (y * width + x) * 4;
                    if (color == 0)                                    // 0x0000 is transparent on PlayStation
                    {
                        bgra[o] = bgra[o + 1] = bgra[o + 2] = bgra[o + 3] = 0;
                        continue;
                    }
                    bgra[o] = (byte)(((color >> 10) & 31) * 255 / 31);
                    bgra[o + 1] = (byte)(((color >> 5) & 31) * 255 / 31);
                    bgra[o + 2] = (byte)((color & 31) * 255 / 31);
                    bgra[o + 3] = 255;
                }
            }
            return true;
        }
    }

    /// <summary>Animation record of the classic (TR1-3) level format.</summary>
    internal sealed class PsxClassicAnim
    {
        public uint FrameByteOffset;
        public int Rate, FrameSizeWords, State, Speed, Accel;
        public int FrameStart, FrameEnd, NextAnim, NextFrame;
        public int NumChanges, ChangeIndex, NumCommands, CommandIndex;
    }
}
