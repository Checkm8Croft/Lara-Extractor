using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Windows.Media.Imaging;
using TombLib;
using TombLib.LevelData;
using TombLib.Utils;
using TombLib.Wad;

namespace Lara_Extractor
{
    /// <summary>
    /// Converts a compiled TombEngine level (.ten, compiler version 2.x) back to a Wad2.
    ///
    /// File layout (see TombLib/LevelData/Compilers/TombEngine/TombEngine.cs and TombEngine/Specific/level.cpp):
    ///   header (16 bytes) | media block | geometry block | dynamic block   (blocks are LZ4 chunked)
    ///
    /// Media block    : texture atlases (rooms, moveables, statics, animated), sprite atlases, sky, sounds.
    /// Geometry block : rooms, floordata, meshes, bones, moveables, statics, sprites, boxes, mirrors, anim. textures.
    ///
    /// Only what a Wad2 needs is read: meshes, bones, moveables (with baked animations), statics, sprites.
    /// Rooms are skipped by size, so the room layout below must match the engine loader exactly.
    /// </summary>
    public sealed class TENWad2Processor
    {
        private readonly string _inputLevelPath;
        private readonly string _outputWad2Path;
        private readonly Action<string> _logger;

        // Atlas groups, in the order they are stored in the media block.
        private const int GroupRoom = 0;
        private const int GroupMoveable = 1;
        private const int GroupStatic = 2;
        private const int GroupAnimated = 3;

        // Sizes of the fixed-size records skipped while walking over the room data (bytes).
        private const int PortalSize = 2 + 12 + 4 * 12;   // short room, normal(3 int), 4 vertices (3 int each)
        private const int SectorSize = 100;               // see SkipRooms
        private const int RoomLightSize = 58;             // 3 int + 11 float + byte + bool

        // ---------------------------------------------------------------- data model

        private sealed class Atlas
        {
            public int Width, Height;
            public byte[] ColorBlob = Array.Empty<byte>();
            public ImageC? Image;
            public bool DecodeFailed;
        }

        private sealed class TenPoly
        {
            public int Shape;                  // 0 = quad, otherwise triangle
            public int AnimatedSequence;
            public int AnimatedFrame;
            public float Shine;
            public Vector3 Normal;
            public int[] Indices = Array.Empty<int>();
            public Vector2[] UV = Array.Empty<Vector2>();
            public Vector3[] Normals = Array.Empty<Vector3>();
        }

        private sealed class TenBucket
        {
            public int Texture;
            public byte BlendMode;
            public int Material;
            public bool Animated;
            public List<TenPoly> Polys = new();
        }

        private sealed class TenMesh
        {
            public bool Hidden;
            public byte Lighting;
            public Vector3 SphereCenter;
            public float SphereRadius;
            public Vector3[] Positions = Array.Empty<Vector3>();
            public Vector3[] Colors = Array.Empty<Vector3>();
            public Vector3[] Effects = Array.Empty<Vector3>();   // glow, move, (locked ? 0 : 1)
            public byte[] BoneIndices = Array.Empty<byte>();     // 4 per vertex
            public byte[] BoneWeights = Array.Empty<byte>();     // 4 per vertex
            public List<TenBucket> Buckets = new();
        }

        private struct TenFrame
        {
            public Vector3 Center, Extents, Root;
            public Quaternion[] Bones;
        }

        private struct TenDispatch
        {
            public int State, FrameLow, FrameHigh, NextAnim, NextLow, NextHigh, Blend;
            public Vector2[] Curve;
        }

        private struct TenCommand
        {
            public int Type;
            public Vector3 Vector;
            public int A, B, C;
        }

        private sealed class TenAnim
        {
            public int State, FrameEnd, NextAnim, NextFrame, BlendFrames;
            public Vector2[] BlendCurve = Array.Empty<Vector2>();
            public Vector2[] CurveX = Array.Empty<Vector2>();
            public Vector2[] CurveY = Array.Empty<Vector2>();
            public Vector2[] CurveZ = Array.Empty<Vector2>();
            public List<TenFrame> Frames = new();
            public List<TenDispatch> Dispatches = new();
            public List<TenCommand> Commands = new();
            public int Flags;
        }

        private sealed class TenModel
        {
            public int Id, Skin, NumMeshes, StartMesh, BoneIndex;
            public List<TenAnim> Anims = new();
        }

        private struct TenStatic
        {
            public int Id, Mesh;
            public short[] Vis, Col;       // X1 X2 Y1 Y2 Z1 Z2
            public short Flags, ShatterType, ShatterSound;
        }

        private struct TenSpriteTexture
        {
            public int Tile;
            public float X1, Y1, X2, Y2, X3, Y3, X4, Y4;
        }

        private struct TenSpriteSequence
        {
            public int Id;
            public short NegLength, Offset;
        }

        private readonly Atlas[][] _atlases = new Atlas[4][];
        private Atlas[] _spriteAtlases = Array.Empty<Atlas>();

        private TenMesh[] _meshes = Array.Empty<TenMesh>();
        private int[] _bones = Array.Empty<int>();
        private TenModel[] _models = Array.Empty<TenModel>();
        private TenStatic[] _statics = Array.Empty<TenStatic>();
        private TenSpriteTexture[] _spriteTextures = Array.Empty<TenSpriteTexture>();
        private TenSpriteSequence[] _spriteSequences = Array.Empty<TenSpriteSequence>();

        // (group, atlas, x, y, w, h) -> cropped texture
        private readonly Dictionary<(int, int, int, int, int, int), WadTexture> _cropCache = new();
        private WadTexture? _fallbackTexture;

        private int _statDoubleSidedMerged;
        private int _statMissingTexture;
        private int _statAnimatedPolys;

        public TENWad2Processor(string inputLevelPath, string outputWad2Path, Action<string> logger)
        {
            _inputLevelPath = inputLevelPath;
            _outputWad2Path = outputWad2Path;
            _logger = logger;
        }

        // ---------------------------------------------------------------- entry point

        public void ExtractWad2()
        {
            _logger("[TEN Wad2] ========== STARTING TEN WAD2 PIPELINE ==========");

            if (!File.Exists(_inputLevelPath))
            {
                _logger("[TEN Wad2 Error] Input level file not found.");
                return;
            }

            Wad2? wad = Build();
            if (wad == null)
            {
                _logger("[TEN Wad2 Error] Failed to generate Wad2 from TEN level.");
                return;
            }

            _logger("[TEN Wad2] --- Saving ---");
            _logger($"[TEN Wad2]   Moveables : {wad.Moveables.Count}");
            _logger($"[TEN Wad2]   Statics   : {wad.Statics.Count}");
            _logger($"[TEN Wad2]   Sprites   : {wad.SpriteSequences.Count}");
            _logger($"[TEN Wad2]   Version   : {wad.GameVersion}");

            string? dir = Path.GetDirectoryName(_outputWad2Path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            Wad2Writer.SaveToFile(wad, _outputWad2Path);
            _logger($"[TEN Wad2] ========== WAD2 SAVED TO: {_outputWad2Path} ==========");
        }

        public Wad2? Build()
        {
            try
            {
                using var file = File.OpenRead(_inputLevelPath);
                using var reader = new BinaryReader(file);

                if (reader.ReadUInt32() != 0x004E4554)
                    throw new InvalidDataException("Invalid TEN header.");

                byte[] version = reader.ReadBytes(4);
                reader.ReadInt32(); // system hash
                reader.ReadInt32(); // level hash
                _logger($"[TEN Wad2] Level compiler version: {version[0]}.{version[1]}.{version[2]}");

                if (version[0] < 2)
                    throw new NotSupportedException(
                        $"TEN level compiler version {version[0]}.{version[1]}.{version[2]} is not supported (animations are stored differently before 2.0).");

                byte[] media = TENAssetProcessor.ReadCompressedBlock(reader, "media");
                byte[] geometry = TENAssetProcessor.ReadCompressedBlock(reader, "geometry");
                _logger($"[TEN Wad2] Media block: {media.Length:N0} bytes, geometry block: {geometry.Length:N0} bytes.");

                ReadMedia(media);
                ReadGeometry(geometry);

                var wad = BuildWad2();
                ReleaseImages();
                return wad;
            }
            catch (Exception ex)
            {
                _logger($"[TEN Wad2 Error] {ex.GetType().Name}: {ex.Message}");
                var st = ex.StackTrace;
                if (st != null)
                    foreach (var line in st.Split('\n'))
                        if (line.Contains("TENWad2Processor"))
                            _logger($"[TEN Wad2]   {line.Trim()}");
                return null;
            }
        }

        // ---------------------------------------------------------------- media block (textures only)

        private void ReadMedia(byte[] media)
        {
            using var ms = new MemoryStream(media, writable: false);
            using var r = new BinaryReader(ms);

            string[] names = { "Room", "Moveable", "Static", "Animated" };
            for (int g = 0; g < 4; g++)
            {
                int count = ReadCount(r, 4096);
                _atlases[g] = new Atlas[count];
                for (int i = 0; i < count; i++)
                {
                    var atlas = new Atlas { Width = r.ReadInt32(), Height = r.ReadInt32() };
                    atlas.ColorBlob = ReadBlob(r);

                    // Normal, ORSH and emissive maps are not needed for a Wad2.
                    for (int extra = 0; extra < 3; extra++)
                        if (r.ReadBoolean())
                            SkipBlob(r);

                    _atlases[g][i] = atlas;
                }
                _logger($"[TEN Wad2] {names[g]} atlases: {count}" +
                        (count > 0 ? $" ({_atlases[g][0].Width}x{_atlases[g][0].Height})" : ""));
            }

            int spriteCount = ReadCount(r, 4096);
            _spriteAtlases = new Atlas[spriteCount];
            for (int i = 0; i < spriteCount; i++)
            {
                var atlas = new Atlas { Width = r.ReadInt32(), Height = r.ReadInt32() };
                atlas.ColorBlob = ReadBlob(r);
                _spriteAtlases[i] = atlas;
            }
            _logger($"[TEN Wad2] Sprite atlases: {spriteCount}");
        }

        private static int ReadCount(BinaryReader r, int max)
        {
            int count = r.ReadInt32();
            if (count < 0 || count > max)
                throw new InvalidDataException($"Invalid TEN count {count} at offset {r.BaseStream.Position - 4}.");
            return count;
        }

        private static byte[] ReadBlob(BinaryReader r)
        {
            int length = r.ReadInt32();
            if (length < 0 || length > r.BaseStream.Length - r.BaseStream.Position)
                throw new InvalidDataException("Invalid TEN texture blob length.");
            return r.ReadBytes(length);
        }

        private static void SkipBlob(BinaryReader r)
        {
            int length = r.ReadInt32();
            if (length < 0 || length > r.BaseStream.Length - r.BaseStream.Position)
                throw new InvalidDataException("Invalid TEN texture blob length.");
            r.BaseStream.Seek(length, SeekOrigin.Current);
        }

        // ---------------------------------------------------------------- geometry block

        private void ReadGeometry(byte[] geometry)
        {
            using var ms = new MemoryStream(geometry, writable: false);
            using var r = new BinaryReader(ms);

            SkipRooms(r);

            int floorDataCount = r.ReadInt32();
            if (floorDataCount < 0 || floorDataCount * 2L > ms.Length - ms.Position)
                throw new InvalidDataException($"Invalid floordata count {floorDataCount}; room layout is out of sync.");
            ms.Seek(floorDataCount * 2L, SeekOrigin.Current);

            // ---- meshes
            int meshCount = ReadCount(r, 1024 * 1024);
            _meshes = new TenMesh[meshCount];
            for (int i = 0; i < meshCount; i++)
                _meshes[i] = ReadMesh(r);
            _logger($"[TEN Wad2] Meshes: {meshCount}");

            // ---- bones: flat int array, 4 ints (opcode, x, y, z) per bone
            int boneInts = ReadCount(r, 1024 * 1024);
            _bones = new int[boneInts];
            for (int i = 0; i < boneInts; i++)
                _bones[i] = r.ReadInt32();

            // ---- moveables
            int modelCount = ReadCount(r, 1024 * 1024);
            _models = new TenModel[modelCount];
            for (int i = 0; i < modelCount; i++)
                _models[i] = ReadModel(r);
            _logger($"[TEN Wad2] Moveables: {modelCount}");

            // ---- statics
            int staticCount = ReadCount(r, 1024 * 1024);
            _statics = new TenStatic[staticCount];
            for (int i = 0; i < staticCount; i++)
            {
                var s = new TenStatic { Id = r.ReadInt32(), Mesh = r.ReadInt32(), Vis = new short[6], Col = new short[6] };
                for (int k = 0; k < 6; k++) s.Vis[k] = r.ReadInt16();
                for (int k = 0; k < 6; k++) s.Col[k] = r.ReadInt16();
                s.Flags = r.ReadInt16();
                s.ShatterType = r.ReadInt16();
                s.ShatterSound = r.ReadInt16();
                _statics[i] = s;
            }
            _logger($"[TEN Wad2] Statics: {staticCount}");

            // ---- sprites
            int spriteTexCount = ReadCount(r, 1024 * 1024);
            _spriteTextures = new TenSpriteTexture[spriteTexCount];
            for (int i = 0; i < spriteTexCount; i++)
                _spriteTextures[i] = new TenSpriteTexture
                {
                    Tile = r.ReadInt32(),
                    X1 = r.ReadSingle(), Y1 = r.ReadSingle(),
                    X2 = r.ReadSingle(), Y2 = r.ReadSingle(),
                    X3 = r.ReadSingle(), Y3 = r.ReadSingle(),
                    X4 = r.ReadSingle(), Y4 = r.ReadSingle()
                };

            int spriteSeqCount = ReadCount(r, 1024 * 1024);
            _spriteSequences = new TenSpriteSequence[spriteSeqCount];
            for (int i = 0; i < spriteSeqCount; i++)
                _spriteSequences[i] = new TenSpriteSequence { Id = r.ReadInt32(), NegLength = r.ReadInt16(), Offset = r.ReadInt16() };
            _logger($"[TEN Wad2] Sprite textures: {spriteTexCount}, sequences: {spriteSeqCount}");

            // ---- sanity checks (a wrong room skip would almost certainly break at least one of them)
            foreach (var m in _models)
            {
                if (m.Id < 0 || m.StartMesh < 0 || m.StartMesh + m.NumMeshes > _meshes.Length)
                    throw new InvalidDataException($"Moveable {m.Id} references meshes outside the mesh table; data is out of sync.");
                if (m.NumMeshes > 0 && m.BoneIndex + (m.NumMeshes - 1) * 4 > _bones.Length)
                    throw new InvalidDataException($"Moveable {m.Id} references bones outside the bone table; data is out of sync.");
            }
            foreach (var s in _statics)
                if (s.Mesh < 0 || s.Mesh >= _meshes.Length)
                    throw new InvalidDataException($"Static {s.Id} references mesh {s.Mesh} outside the mesh table; data is out of sync.");
        }

        /// <summary>
        /// Walks over the room data without keeping it. Layout taken from LoadStaticRoomData() in level.cpp.
        /// Note: room polygons have no shine strength field, mesh polygons do.
        /// </summary>
        private void SkipRooms(BinaryReader r)
        {
            var s = r.BaseStream;
            int roomCount = ReadCount(r, 1024 * 1024);
            _logger($"[TEN Wad2] Rooms: {roomCount} (skipping)");

            for (int room = 0; room < roomCount; room++)
            {
                s.Seek(4 * 4, SeekOrigin.Current);                        // x, z, bottom, top

                int vertexCount = ReadCount(r, 1024 * 1024 * 1024);
                s.Seek(3L * 12 * vertexCount, SeekOrigin.Current);        // positions, colors, effects

                int bucketCount = ReadCount(r, 1024 * 1024);
                for (int b = 0; b < bucketCount; b++)
                {
                    s.Seek(4 + 1 + 4 + 1, SeekOrigin.Current);            // texture, blend, material, animated
                    int polyCount = ReadCount(r, 1024 * 1024 * 1024);
                    for (int p = 0; p < polyCount; p++)
                    {
                        int shape = r.ReadInt32();
                        int count = shape == 0 ? 4 : 3;
                        // animSeq, animFrame, normal | indices, uv, normals, tangents, binormals
                        s.Seek(4 + 4 + 12 + count * (4 + 8 + 12 + 12 + 12), SeekOrigin.Current);
                    }
                }

                int portalCount = ReadCount(r, 1024 * 1024);
                s.Seek((long)portalCount * PortalSize, SeekOrigin.Current);

                int zSize = r.ReadInt32();
                int xSize = r.ReadInt32();
                if (zSize < 0 || xSize < 0 || (long)zSize * xSize > 1024 * 1024)
                    throw new InvalidDataException($"Invalid room size {xSize}x{zSize} in room {room}; room layout is out of sync.");
                s.Seek((long)zSize * xSize * SectorSize, SeekOrigin.Current);

                int lightCount = ReadCount(r, 1024 * 1024);
                s.Seek((long)lightCount * RoomLightSize, SeekOrigin.Current);

                if (s.Position > s.Length)
                    throw new EndOfStreamException($"Room {room} runs past the end of the geometry block.");
            }
        }

        private static Vector3[] ReadVector3Array(BinaryReader r, int count)
        {
            var result = new Vector3[count];
            byte[] raw = r.ReadBytes(12 * count);
            if (raw.Length != 12 * count) throw new EndOfStreamException("Truncated vector array.");
            for (int i = 0; i < count; i++)
                result[i] = new Vector3(BitConverter.ToSingle(raw, i * 12), BitConverter.ToSingle(raw, i * 12 + 4), BitConverter.ToSingle(raw, i * 12 + 8));
            return result;
        }

        private static Vector2 ReadVector2(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle());
        private static Vector3 ReadVector3(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        private static TenMesh ReadMesh(BinaryReader r)
        {
            var mesh = new TenMesh
            {
                Hidden = r.ReadBoolean(),
                Lighting = r.ReadByte(),
                SphereCenter = ReadVector3(r),
                SphereRadius = r.ReadSingle()
            };

            int vertexCount = ReadCount(r, 1024 * 1024 * 1024);
            mesh.Positions = ReadVector3Array(r, vertexCount);
            mesh.Colors = ReadVector3Array(r, vertexCount);
            mesh.Effects = ReadVector3Array(r, vertexCount);
            mesh.BoneIndices = r.ReadBytes(4 * vertexCount);
            mesh.BoneWeights = r.ReadBytes(4 * vertexCount);

            int bucketCount = ReadCount(r, 1024 * 1024);
            for (int b = 0; b < bucketCount; b++)
            {
                var bucket = new TenBucket
                {
                    Texture = r.ReadInt32(),
                    BlendMode = r.ReadByte(),
                    Material = r.ReadInt32(),
                    Animated = r.ReadBoolean()
                };

                int polyCount = ReadCount(r, 1024 * 1024 * 1024);
                for (int p = 0; p < polyCount; p++)
                {
                    var poly = new TenPoly
                    {
                        Shape = r.ReadInt32(),
                        AnimatedSequence = r.ReadInt32(),
                        AnimatedFrame = r.ReadInt32(),
                        Shine = r.ReadSingle(),
                        Normal = ReadVector3(r)
                    };

                    int count = poly.Shape == 0 ? 4 : 3;
                    poly.Indices = new int[count];
                    poly.UV = new Vector2[count];
                    poly.Normals = new Vector3[count];

                    for (int n = 0; n < count; n++) poly.Indices[n] = r.ReadInt32();
                    for (int n = 0; n < count; n++) poly.UV[n] = ReadVector2(r);
                    for (int n = 0; n < count; n++) poly.Normals[n] = ReadVector3(r);
                    r.BaseStream.Seek(count * 24L, SeekOrigin.Current);   // tangents + binormals

                    bucket.Polys.Add(poly);
                }
                mesh.Buckets.Add(bucket);
            }

            return mesh;
        }

        private static Vector2[] ReadCurve(BinaryReader r)
            => new[] { ReadVector2(r), ReadVector2(r), ReadVector2(r), ReadVector2(r) }; // start, end, startHandle, endHandle

        private static TenModel ReadModel(BinaryReader r)
        {
            var model = new TenModel
            {
                Id = r.ReadInt32(),
                Skin = r.ReadInt32(),
                NumMeshes = r.ReadInt32(),
                StartMesh = r.ReadInt32(),
                BoneIndex = r.ReadInt32()
            };

            int animCount = ReadCount(r, 1024 * 1024);
            for (int a = 0; a < animCount; a++)
            {
                var anim = new TenAnim
                {
                    State = r.ReadInt32(),
                    FrameEnd = r.ReadInt32(),
                    NextAnim = r.ReadInt32(),
                    NextFrame = r.ReadInt32(),
                    BlendFrames = ReadCount(r, 1024 * 1024)
                };
                anim.BlendCurve = ReadCurve(r);
                anim.CurveX = ReadCurve(r);
                anim.CurveY = ReadCurve(r);
                anim.CurveZ = ReadCurve(r);

                int frameCount = ReadCount(r, 1024 * 1024);
                for (int f = 0; f < frameCount; f++)
                {
                    var frame = new TenFrame
                    {
                        Center = ReadVector3(r),
                        Extents = ReadVector3(r),
                        Root = ReadVector3(r)
                    };
                    int boneCount = ReadCount(r, 1024 * 1024);
                    frame.Bones = new Quaternion[boneCount];
                    for (int b = 0; b < boneCount; b++)
                        frame.Bones[b] = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    anim.Frames.Add(frame);
                }

                int dispatchCount = ReadCount(r, 1024 * 1024);
                for (int d = 0; d < dispatchCount; d++)
                    anim.Dispatches.Add(new TenDispatch
                    {
                        State = r.ReadInt32(),
                        FrameLow = r.ReadInt32(),
                        FrameHigh = r.ReadInt32(),
                        NextAnim = r.ReadInt32(),
                        NextLow = r.ReadInt32(),
                        NextHigh = r.ReadInt32(),
                        Blend = r.ReadInt32(),
                        Curve = ReadCurve(r)
                    });

                int commandCount = ReadCount(r, 1024 * 1024);
                for (int c = 0; c < commandCount; c++)
                {
                    var cmd = new TenCommand { Type = r.ReadInt32() };
                    switch (cmd.Type)
                    {
                        case 1: // set position
                        case 2: // set jump distance
                            cmd.Vector = ReadVector3(r);
                            break;
                        case 5: // play sound: id, frame, environment
                            cmd.A = r.ReadInt32(); cmd.B = r.ReadInt32(); cmd.C = r.ReadInt32();
                            break;
                        case 6: // flip effect: id, frame
                            cmd.A = r.ReadInt32(); cmd.B = r.ReadInt32();
                            break;
                        case 7: // disable interpolation: frame
                            cmd.A = r.ReadInt32();
                            break;
                        // 0, 3, 4: no data (the engine skips unknown types the same way)
                    }
                    anim.Commands.Add(cmd);
                }

                anim.Flags = r.ReadInt32();
                model.Anims.Add(anim);
            }

            return model;
        }

        // ---------------------------------------------------------------- Wad2 construction

        private Wad2 BuildWad2()
        {
            var wad = new Wad2 { GameVersion = TRVersion.Game.TombEngine };

            int ok = 0, fail = 0;
            foreach (var model in _models)
            {
                try
                {
                    wad.Moveables[new WadMoveableId((uint)model.Id)] = BuildMoveable(model);
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    _logger($"[TEN Wad2] Moveable {model.Id}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            _logger($"[TEN Wad2] Moveables: {ok} OK, {fail} fail");

            ok = 0; fail = 0;
            foreach (var st in _statics)
            {
                try
                {
                    wad.Statics[new WadStaticId((uint)st.Id)] = BuildStatic(st);
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    _logger($"[TEN Wad2] Static {st.Id}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            _logger($"[TEN Wad2] Statics: {ok} OK, {fail} fail");

            ok = 0; fail = 0;
            foreach (var seq in _spriteSequences)
            {
                try
                {
                    wad.SpriteSequences[new WadSpriteSequenceId((uint)seq.Id)] = BuildSpriteSequence(seq);
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    _logger($"[TEN Wad2] Sprite sequence {seq.Id}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            _logger($"[TEN Wad2] Sprite sequences: {ok} OK, {fail} fail");

            if (_statDoubleSidedMerged > 0)
                _logger($"[TEN Wad2] {_statDoubleSidedMerged} reversed polygon pair(s) merged back into double-sided faces.");
            if (_statAnimatedPolys > 0)
                _logger($"[TEN Wad2] {_statAnimatedPolys} animated-texture polygon(s) exported with their first frame only (animated texture sets are not rebuilt).");
            if (_statMissingTexture > 0)
                _logger($"[TEN Wad2 Warning] {_statMissingTexture} polygon(s) point to an atlas that could not be read; a placeholder was used.");

            return wad;
        }

        private WadMoveable BuildMoveable(TenModel model)
        {
            var mov = new WadMoveable(new WadMoveableId((uint)model.Id));
            string name = $"moveable_{model.Id}";

            for (int i = 0; i < model.NumMeshes; i++)
            {
                var bone = new WadBone
                {
                    Name = $"bone_{i}",
                    Mesh = BuildMesh(_meshes[model.StartMesh + i], i, false, $"{name}_mesh_{i}")
                };

                if (i == 0)
                {
                    bone.Translation = Vector3.Zero;
                    bone.OpCode = WadLinkOpcode.NotUseStack;
                }
                else
                {
                    int o = model.BoneIndex + (i - 1) * 4;
                    int opcode = _bones[o];
                    bone.OpCode = opcode >= 0 && opcode <= 3 ? (WadLinkOpcode)opcode : WadLinkOpcode.NotUseStack;
                    bone.Translation = new Vector3(_bones[o + 1], -_bones[o + 2], _bones[o + 3]);
                }

                mov.Bones.Add(bone);
            }

            if (model.Skin >= 0 && model.Skin < _meshes.Length)
                mov.Skin = BuildMesh(_meshes[model.Skin], 0, false, $"{name}_skin");

            for (int a = 0; a < model.Anims.Count; a++)
                mov.Animations.Add(BuildAnimation(model.Anims[a], a));

            return mov;
        }

        private WadStatic BuildStatic(TenStatic s)
        {
            var stat = new WadStatic(new WadStaticId((uint)s.Id))
            {
                Mesh = BuildMesh(_meshes[s.Mesh], 0, true, $"static_{s.Id}"),
                Flags = s.Flags,
                Shatter = s.ShatterType != 0,
                ShatterSoundID = s.ShatterSound
            };

            // Compiler writes X1/X2 = min/max X, Y1/Y2 = -min/-max Y, Z1/Z2 = min/max Z.
            stat.VisibilityBox = new BoundingBox(
                new Vector3(s.Vis[0], -s.Vis[2], s.Vis[4]),
                new Vector3(s.Vis[1], -s.Vis[3], s.Vis[5]));
            stat.CollisionBox = new BoundingBox(
                new Vector3(s.Col[0], -s.Col[2], s.Col[4]),
                new Vector3(s.Col[1], -s.Col[3], s.Col[5]));
            return stat;
        }

        // ---------------------------------------------------------------- meshes

        private WadMesh BuildMesh(TenMesh src, int defaultBone, bool isStatic, string name)
        {
            var mesh = new WadMesh
            {
                Name = name,
                Hidden = src.Hidden,
                LightingType = src.Lighting == 1 ? WadMeshLightingType.VertexColors : WadMeshLightingType.Normals
            };

            int vertexCount = src.Positions.Length;
            var normals = new Vector3[vertexCount];

            for (int i = 0; i < vertexCount; i++)
            {
                var p = src.Positions[i];
                mesh.VertexPositions.Add(new Vector3(p.X, -p.Y, p.Z));
                mesh.VertexColors.Add(src.Colors[i]);
                mesh.VertexAttributes.Add(new VertexAttributes
                {
                    Glow = (int)MathF.Round(src.Effects[i].X * 64.0f),
                    Move = (int)MathF.Round(src.Effects[i].Y * 64.0f)
                });
            }

            // Vertex weights: only store them if some vertex differs from the compiler's default (100% on its own bone).
            bool hasCustomWeights = false;
            for (int i = 0; i < vertexCount && !hasCustomWeights; i++)
            {
                if (src.BoneIndices[i * 4] != defaultBone || src.BoneWeights[i * 4] != 255 ||
                    src.BoneWeights[i * 4 + 1] != 0 || src.BoneWeights[i * 4 + 2] != 0 || src.BoneWeights[i * 4 + 3] != 0)
                    hasCustomWeights = true;
            }
            if (hasCustomWeights)
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    var w = new VertexWeight { Index = new int[4], Weight = new float[4] };
                    for (int k = 0; k < 4; k++)
                    {
                        w.Index[k] = src.BoneIndices[i * 4 + k];
                        w.Weight[k] = src.BoneWeights[i * 4 + k] / 255.0f;
                    }
                    mesh.VertexWeights.Add(w);
                }
            }

            // Polygons. Double-sided faces are compiled as two polygons (the second one reversed), merge them back.
            var seen = new Dictionary<string, int>();
            foreach (var bucket in src.Buckets)
            {
                int group = bucket.Animated ? GroupAnimated : (isStatic ? GroupStatic : GroupMoveable);
                var blend = Enum.IsDefined(typeof(BlendMode), (ushort)bucket.BlendMode) ? (BlendMode)bucket.BlendMode : BlendMode.Normal;

                foreach (var tp in bucket.Polys)
                {
                    bool isTriangle = tp.Shape != 0;
                    int count = tp.Indices.Length;

                    if (bucket.Animated || tp.AnimatedSequence >= 0)
                        _statAnimatedPolys++;

                    // Vertex normals are stored per polygon corner (already averaged by the compiler).
                    for (int k = 0; k < count; k++)
                    {
                        int idx = tp.Indices[k];
                        if (idx < 0 || idx >= vertexCount) continue;
                        var n = tp.Normals[k];
                        var nn = new Vector3(n.X, -n.Y, n.Z);
                        if (nn.LengthSquared() > 1e-6f) normals[idx] = Vector3.Normalize(nn);
                    }

                    var area = BuildTextureArea(group, bucket.Texture, tp.UV, isTriangle, blend);

                    var poly = new WadPolygon
                    {
                        Shape = isTriangle ? WadPolygonShape.Triangle : WadPolygonShape.Quad,
                        Index0 = tp.Indices[0],
                        Index1 = tp.Indices[1],
                        Index2 = tp.Indices[2],
                        Index3 = isTriangle ? 0 : tp.Indices[3],
                        ShineStrength = (byte)Math.Clamp((int)MathF.Round(tp.Shine * 63.0f), 0, 63),
                        Texture = area
                    };

                    string key = group + ":" + bucket.Texture + ":" + (int)blend + ":" + isTriangle + ":" + SortedKey(tp.Indices);
                    if (seen.TryGetValue(key, out int existing))
                    {
                        var other = mesh.Polys[existing];
                        if (!other.Texture.DoubleSided && IsOppositeWinding(mesh, other, poly))
                        {
                            var merged = other;
                            merged.Texture.DoubleSided = true;
                            mesh.Polys[existing] = merged;
                            _statDoubleSidedMerged++;
                            continue;
                        }
                    }

                    seen[key] = mesh.Polys.Count;
                    mesh.Polys.Add(poly);
                }
            }

            for (int i = 0; i < vertexCount; i++)
                mesh.VertexNormals.Add(normals[i].LengthSquared() > 1e-6f ? normals[i] : Vector3.UnitY);

            mesh.BoundingSphere = new BoundingSphere(new Vector3(src.SphereCenter.X, -src.SphereCenter.Y, src.SphereCenter.Z), src.SphereRadius);
            mesh.BoundingBox = vertexCount > 0
                ? mesh.CalculateBoundingBox()
                : new BoundingBox(mesh.BoundingSphere.Center - new Vector3(src.SphereRadius), mesh.BoundingSphere.Center + new Vector3(src.SphereRadius));

            return mesh;
        }

        private static string SortedKey(int[] indices)
        {
            var copy = (int[])indices.Clone();
            Array.Sort(copy);
            return string.Join(",", copy);
        }

        private static bool IsOppositeWinding(WadMesh mesh, WadPolygon a, WadPolygon b)
        {
            Vector3 NormalOf(WadPolygon p)
            {
                var v0 = mesh.VertexPositions[p.Index0];
                var v1 = mesh.VertexPositions[p.Index1];
                var v2 = mesh.VertexPositions[p.Index2];
                return Vector3.Cross(v1 - v0, v2 - v0);
            }

            var na = NormalOf(a);
            var nb = NormalOf(b);
            if (na.LengthSquared() < 1e-6f || nb.LengthSquared() < 1e-6f) return false;
            return Vector3.Dot(Vector3.Normalize(na), Vector3.Normalize(nb)) < -0.9f;
        }

        // ---------------------------------------------------------------- textures

        private TextureArea BuildTextureArea(int group, int atlasIndex, Vector2[] uv, bool isTriangle, BlendMode blend)
        {
            var area = new TextureArea { BlendMode = blend, DoubleSided = false };

            Atlas? atlas = (atlasIndex >= 0 && atlasIndex < _atlases[group].Length) ? _atlases[group][atlasIndex] : null;
            if (atlas == null || !EnsureDecoded(atlas))
            {
                _statMissingTexture++;
                area.Texture = GetFallbackTexture();
                area.TexCoord0 = new Vector2(0, 0);
                area.TexCoord1 = new Vector2(1, 0);
                area.TexCoord2 = new Vector2(1, 1);
                area.TexCoord3 = isTriangle ? new Vector2(1, 1) : new Vector2(0, 1);
                return area;
            }

            // UVs are normalized to the atlas; convert to pixels.
            var px = new Vector2[uv.Length];
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < uv.Length; i++)
            {
                px[i] = new Vector2(uv[i].X * atlas.Width, uv[i].Y * atlas.Height);
                minX = MathF.Min(minX, px[i].X); maxX = MathF.Max(maxX, px[i].X);
                minY = MathF.Min(minY, px[i].Y); maxY = MathF.Max(maxY, px[i].Y);
            }

            // Crop the used rectangle instead of keeping the whole atlas (atlases can be 4096x4096, and Wad2 is saved uncompressed).
            int x0 = Math.Clamp((int)MathF.Floor(minX), 0, atlas.Width - 1);
            int y0 = Math.Clamp((int)MathF.Floor(minY), 0, atlas.Height - 1);
            int x1 = Math.Clamp((int)MathF.Ceiling(maxX), x0 + 1, atlas.Width);
            int y1 = Math.Clamp((int)MathF.Ceiling(maxY), y0 + 1, atlas.Height);
            int w = x1 - x0, h = y1 - y0;

            var cacheKey = (group, atlasIndex, x0, y0, w, h);
            if (!_cropCache.TryGetValue(cacheKey, out var tex))
            {
                var crop = ImageC.CreateNew(w, h);
                crop.CopyFrom(0, 0, atlas.Image!.Value, x0, y0, w, h);
                tex = new WadTexture(crop);
                _cropCache[cacheKey] = tex;
            }

            area.Texture = tex;
            area.TexCoord0 = px[0] - new Vector2(x0, y0);
            area.TexCoord1 = px[1] - new Vector2(x0, y0);
            area.TexCoord2 = px[2] - new Vector2(x0, y0);
            area.TexCoord3 = isTriangle ? area.TexCoord2 : px[3] - new Vector2(x0, y0);
            return area;
        }

        private WadTexture GetFallbackTexture()
        {
            if (_fallbackTexture == null)
            {
                var img = ImageC.CreateNew(2, 2);
                img.Fill(new ColorC(255, 0, 255, 255));
                _fallbackTexture = new WadTexture(img);
            }
            return _fallbackTexture;
        }

        private bool EnsureDecoded(Atlas atlas)
        {
            if (atlas.Image.HasValue) return true;
            if (atlas.DecodeFailed) return false;

            byte[]? bgra = DecodeToBgra(atlas.ColorBlob, atlas.Width, atlas.Height, out int w, out int h);
            if (bgra == null || w <= 0 || h <= 0)
            {
                atlas.DecodeFailed = true;
                _logger($"[TEN Wad2 Warning] Could not decode a {atlas.Width}x{atlas.Height} atlas (unsupported format).");
                return false;
            }

            atlas.Width = w;
            atlas.Height = h;
            atlas.Image = ImageC.FromByteArray(bgra, w, h);
            return true;
        }

        private void ReleaseImages()
        {
            foreach (var group in _atlases)
                foreach (var a in group)
                    if (a != null) { a.Image = null; a.ColorBlob = Array.Empty<byte>(); }
            foreach (var a in _spriteAtlases)
                if (a != null) { a.Image = null; a.ColorBlob = Array.Empty<byte>(); }
        }

        /// <summary>Decodes a PNG or BC1/BC3 DDS blob (as written by the TEN compiler) to BGRA32.</summary>
        internal static byte[]? DecodeToBgra(byte[] blob, int expectedW, int expectedH, out int width, out int height)
        {
            width = expectedW;
            height = expectedH;
            if (blob.Length < 8) return null;

            // PNG
            if (blob[0] == 0x89 && blob[1] == 0x50 && blob[2] == 0x4E && blob[3] == 0x47)
            {
                using var ms = new MemoryStream(blob, writable: false);
                var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapSource source = decoder.Frames[0];
                if (source.Format != System.Windows.Media.PixelFormats.Bgra32)
                    source = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                width = source.PixelWidth;
                height = source.PixelHeight;
                var pixels = new byte[width * height * 4];
                source.CopyPixels(pixels, width * 4, 0);
                return pixels;
            }

            // DDS
            if (blob.Length >= 128 && blob[0] == 'D' && blob[1] == 'D' && blob[2] == 'S' && blob[3] == ' ')
            {
                int ddsHeight = BitConverter.ToInt32(blob, 12);
                int ddsWidth = BitConverter.ToInt32(blob, 16);
                int fourCc = BitConverter.ToInt32(blob, 84);
                if (ddsWidth > 0) width = ddsWidth;
                if (ddsHeight > 0) height = ddsHeight;

                byte[] dds = blob;
                if (fourCc == 0x30315844 && blob.Length >= 148) // "DX10": 20 extra header bytes, DXGI format at 128
                {
                    int dxgi = BitConverter.ToInt32(blob, 128);
                    fourCc = dxgi switch
                    {
                        71 or 72 => 0x31545844,   // BC1  -> DXT1
                        77 or 78 => 0x35545844,   // BC3  -> DXT5
                        _ => 0
                    };
                    dds = new byte[blob.Length - 20];
                    Buffer.BlockCopy(blob, 0, dds, 0, 128);
                    Buffer.BlockCopy(blob, 148, dds, 128, blob.Length - 148);
                }

                return fourCc switch
                {
                    0x35545844 => TENAssetProcessor.DecodeDxt5(dds, width, height),
                    0x31545844 => TENAssetProcessor.DecodeDxt1(dds, width, height),
                    _ => null
                };
            }

            return null;
        }

        // ---------------------------------------------------------------- animations

        private WadAnimation BuildAnimation(TenAnim src, int index)
        {
            var anim = new WadAnimation
            {
                Name = $"Animation {index}",
                StateId = (ushort)src.State,
                // Level frames are already baked (one frame per game tick), so every frame is a keyframe at rate 1.
                FrameRate = 1,
                EndFrame = (ushort)Math.Max(0, src.FrameEnd),
                NextAnimation = (ushort)Math.Max(0, src.NextAnim),
                NextFrame = (ushort)Math.Max(0, src.NextFrame),
                BlendFrameCount = (ushort)Math.Max(0, src.BlendFrames),
                BlendCurve = ToCurve(src.BlendCurve),
                // Fixed motion curves are written as (0, startVelocity) -> (1, endVelocity).
                StartLateralVelocity = src.CurveX[0].Y,
                EndLateralVelocity = src.CurveX[1].Y,
                StartVelocity = src.CurveZ[0].Y,
                EndVelocity = src.CurveZ[1].Y,
                RootMotion = new WadAnimRootMotionSettings { Flags = (WadAnimRootMotionFlags)src.Flags }
            };

            foreach (var f in src.Frames)
            {
                // Compiler stores center/extents of (X1,X2,Y1,Y2,Z1,Z2) with Y1/Y2 = -min/-max.
                float x1 = f.Center.X - f.Extents.X, x2 = f.Center.X + f.Extents.X;
                float y1 = f.Center.Y - f.Extents.Y, y2 = f.Center.Y + f.Extents.Y;
                float z1 = f.Center.Z - f.Extents.Z, z2 = f.Center.Z + f.Extents.Z;

                var kf = new WadKeyFrame
                {
                    BoundingBox = new BoundingBox(new Vector3(x1, -y1, z1), new Vector3(x2, -y2, z2)),
                    Offset = new Vector3(f.Root.X, -f.Root.Y, f.Root.Z)
                };
                foreach (var q in f.Bones)
                    kf.Angles.Add(FromQuaternion(q));
                anim.KeyFrames.Add(kf);
            }

            var changes = new Dictionary<int, WadStateChange>();
            foreach (var d in src.Dispatches)
            {
                if (!changes.TryGetValue(d.State, out var change))
                {
                    change = new WadStateChange { StateId = (ushort)d.State };
                    changes[d.State] = change;
                    anim.StateChanges.Add(change);
                }

                change.Dispatches.Add(new WadAnimDispatch
                {
                    InFrame = (ushort)Math.Max(0, d.FrameLow),
                    OutFrame = (ushort)Math.Max(0, d.FrameHigh),
                    NextAnimation = (ushort)Math.Max(0, d.NextAnim),
                    NextLowFrame = (ushort)Math.Max(0, d.NextLow),
                    NextHighFrame = (ushort)Math.Max(0, d.NextHigh),
                    BlendFrames = (ushort)Math.Max(0, d.Blend),
                    BlendCurve = ToCurve(d.Curve)
                });
            }

            foreach (var c in src.Commands)
            {
                switch (c.Type)
                {
                    case 1:
                        anim.AnimCommands.Add(new WadAnimCommand
                        {
                            Type = WadAnimCommandType.SetPosition,
                            Parameter1 = (short)MathF.Round(c.Vector.X),
                            Parameter2 = (short)MathF.Round(c.Vector.Y),
                            Parameter3 = (short)MathF.Round(c.Vector.Z)
                        });
                        break;
                    case 2:
                        anim.AnimCommands.Add(new WadAnimCommand
                        {
                            Type = WadAnimCommandType.SetJumpDistance,
                            Parameter1 = (short)MathF.Round(c.Vector.Y),
                            Parameter2 = (short)MathF.Round(c.Vector.Z)
                        });
                        break;
                    case 3:
                        anim.AnimCommands.Add(new WadAnimCommand { Type = WadAnimCommandType.EmptyHands });
                        break;
                    case 4:
                        anim.AnimCommands.Add(new WadAnimCommand { Type = WadAnimCommandType.KillEntity });
                        break;
                    case 5: // compiler: id = Parameter2, frame = Parameter1, environment = Parameter3
                        anim.AnimCommands.Add(new WadAnimCommand
                        {
                            Type = WadAnimCommandType.PlaySound,
                            Parameter1 = (short)c.B,
                            Parameter2 = (short)c.A,
                            Parameter3 = (short)c.C
                        });
                        break;
                    case 6: // compiler: id = Parameter2, frame = Parameter1
                        anim.AnimCommands.Add(new WadAnimCommand
                        {
                            Type = WadAnimCommandType.FlipEffect,
                            Parameter1 = (short)c.B,
                            Parameter2 = (short)c.A
                        });
                        break;
                    case 7:
                        anim.AnimCommands.Add(new WadAnimCommand
                        {
                            Type = WadAnimCommandType.DisableInterpolation,
                            Parameter1 = (short)c.A
                        });
                        break;
                }
            }

            return anim;
        }

        private static TombLib.Types.BezierCurve2 ToCurve(Vector2[] c)
            => new(c[0], c[1], c[2], c[3]);

        /// <summary>
        /// Inverse of WadKeyFrameRotation.Quaternion, which is
        /// Quaternion.CreateFromYawPitchRoll(yaw = Rotations.Y, pitch = -Rotations.X, roll = -Rotations.Z) (radians).
        /// </summary>
        internal static WadKeyFrameRotation FromQuaternion(Quaternion q)
        {
            if (q.LengthSquared() < 1e-12f) q = Quaternion.Identity;
            q = Quaternion.Normalize(q);

            // Extract yaw/pitch/roll from the rotation matrix (System.Numerics convention: M = Rz(roll) * Rx(pitch) * Ry(yaw)).
            var m = Matrix4x4.CreateFromQuaternion(q);
            float sinPitch = Math.Clamp(-m.M32, -1.0f, 1.0f);
            float pitch = MathF.Asin(sinPitch);
            float yaw, roll;
            if (MathF.Abs(sinPitch) < 0.99999f)
            {
                yaw = MathF.Atan2(m.M31, m.M33);
                roll = MathF.Atan2(m.M12, m.M22);
            }
            else // gimbal lock: fold roll into yaw
            {
                roll = 0;
                yaw = MathF.Atan2(-m.M13, m.M11);
            }
            const float rad2deg = 180.0f / MathF.PI;
            return new WadKeyFrameRotation { Rotations = new Vector3(-pitch * rad2deg, yaw * rad2deg, -roll * rad2deg) };
        }

        // ---------------------------------------------------------------- sprites

        private WadSpriteSequence BuildSpriteSequence(TenSpriteSequence seq)
        {
            var result = new WadSpriteSequence(new WadSpriteSequenceId((uint)seq.Id));
            int count = Math.Abs((int)seq.NegLength);

            for (int i = 0; i < count; i++)
            {
                int idx = seq.Offset + i;
                if (idx < 0 || idx >= _spriteTextures.Length) continue;
                var st = _spriteTextures[idx];

                if (st.Tile < 0 || st.Tile >= _spriteAtlases.Length || !EnsureDecoded(_spriteAtlases[st.Tile]))
                {
                    _statMissingTexture++;
                    continue;
                }

                var atlas = _spriteAtlases[st.Tile];

                // Compiler: x = (pos + padding) / W, w = (width - 1) / W, so the real size is w * W + 1.
                int x = (int)MathF.Round(st.X1 * atlas.Width);
                int y = (int)MathF.Round(st.Y1 * atlas.Height);
                int w = (int)MathF.Round((st.X2 - st.X1) * atlas.Width) + 1;
                int h = (int)MathF.Round((st.Y3 - st.Y2) * atlas.Height) + 1;

                x = Math.Clamp(x, 0, atlas.Width - 1);
                y = Math.Clamp(y, 0, atlas.Height - 1);
                w = Math.Clamp(w, 1, atlas.Width - x);
                h = Math.Clamp(h, 1, atlas.Height - y);

                var crop = ImageC.CreateNew(w, h);
                crop.CopyFrom(0, 0, atlas.Image!.Value, x, y, w, h);

                result.Sprites.Add(new WadSprite { Texture = new WadTexture(crop) });
            }

            return result;
        }
    }
}
