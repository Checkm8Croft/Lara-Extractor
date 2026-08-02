using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using TombLib;
using TombLib.LevelData;
using TombLib.Utils;
using TombLib.Wad;

namespace Lara_Extractor
{
    
    public class TR3Wad2Builder
    {
        private readonly string _filePath;
        private readonly Action<string> _logger;

        private byte[]   _meshData        = Array.Empty<byte>();
        private uint[]   _meshPtrs        = Array.Empty<uint>();
        private byte[]   _animData        = Array.Empty<byte>();
        private uint[]   _animFrameOffsets = Array.Empty<uint>(); 
        private int[]    _animNumFrames    = Array.Empty<int>();  
        private int[]    _animFrameSizes   = Array.Empty<int>();  
        private int[]    _meshTrees       = Array.Empty<int>();
        private ushort[] _frames          = Array.Empty<ushort>();
        private TR3Model[]          _models          = Array.Empty<TR3Model>();
        private TR3StaticMesh[]     _statics         = Array.Empty<TR3StaticMesh>();
        private TR3ObjectTexture[]  _objectTextures  = Array.Empty<TR3ObjectTexture>();
        private TR3SpriteTexture[]  _spriteTextures  = Array.Empty<TR3SpriteTexture>();
        private TR3SpriteSequence[] _spriteSequences = Array.Empty<TR3SpriteSequence>();
        private byte[]? _palette8 = null;

        private bool _isTr2 = false;

        private WadTexture[] _tileTextures = Array.Empty<WadTexture>();
        private readonly Dictionary<int, WadTexture> _objTexCache = new();

        
        private struct TR3Model
        {
            public uint   ID;
            public ushort NumMeshes;
            public ushort StartingMesh;
            public uint   MeshTree;
            public uint   FrameOffset;
            public ushort Animation;
        }

        private struct TR3StaticMesh
        {
            public uint   ID;
            public ushort Mesh;
            public short  VisMinX, VisMaxX, VisMinY, VisMaxY, VisMinZ, VisMaxZ;
            public short  ColMinX, ColMaxX, ColMinY, ColMaxY, ColMinZ, ColMaxZ;
            public ushort Flags;
        }

        private struct TR3ObjectTexture
        {
            public ushort Attribute; // 0=opaque, 1=alpha, 2=additive
            public ushort Tile;
            public Vector2[] UV; // [4] in pixel coordinates (0..255)
        }

        private struct TR3SpriteTexture
        {
            public ushort Tile;
            public byte   X, Y;
            public ushort Width, Height; // (actual_width-1)*256, (actual_height-1)*256
            public short  LeftSide, TopSide, RightSide, BottomSide;
        }

        private struct TR3SpriteSequence
        {
            public int   SpriteID;
            public short NegativeLength;
            public short Offset;
        }

        public TR3Wad2Builder(string filePath, Action<string> logger)
        {
            _filePath = filePath;
            _logger   = logger;
        }

        // ── Entry point ──────────────────────────────────────────────────

        public Wad2? Build()
        {
            _logger("[TR3Parser] Starting parsing of native TR3...");
            try
            {
                using var fs = File.OpenRead(_filePath);
                using var br = new BinaryReader(fs);

                uint version = br.ReadUInt32();
                _logger($"[TR3Parser] Version: 0x{version:X8}");

                _isTr2 = (version & 0xFFFF0000) != 0xFF180000 && (version & 0xFFFF0000) != 0xFF080000;
                _logger(_isTr2 ? "[TR3Parser] Detected layout TR2" : "[TR3Parser] Detected layout TR3");
                byte[] rawPal = br.ReadBytes(768);
                var palette8 = new byte[768];
                for (int i = 0; i < 768; i++) palette8[i] = (byte)(rawPal[i] * 4); // VGA 6-bit -> 8-bit
                _palette8 = palette8;
                _logger($"[TR3Parser] Palette8 read (max val: {MaxVal(palette8)})");

                br.BaseStream.Seek(1024, SeekOrigin.Current); // Palette16 (not used)

                uint numTiles = br.ReadUInt32();
                long tilesStart = br.BaseStream.Position;
                _logger($"[TR3Parser] Textile8: {numTiles} tiles");

                _tileTextures = BuildTileTextures(br, tilesStart, (int)numTiles, palette8);
                _logger($"[TR3Parser] {_tileTextures.Length} WadTexture tile built");
                br.BaseStream.Position = tilesStart + numTiles * 65536L + numTiles * 131072L;
                br.ReadUInt32(); // Unused

                SkipRooms(br);

                uint nFD = br.ReadUInt32();
                br.BaseStream.Seek(nFD * 2, SeekOrigin.Current);

                uint nMeshWords = br.ReadUInt32();
                _meshData = br.ReadBytes((int)nMeshWords * 2);
                _logger($"[TR3Parser] MeshData: {nMeshWords} words");

                uint nPtrs = br.ReadUInt32();
                _meshPtrs = new uint[nPtrs];
                for (int i = 0; i < nPtrs; i++) _meshPtrs[i] = br.ReadUInt32();
                _logger($"[TR3Parser] MeshPointers: {nPtrs}");

                uint nAnims = br.ReadUInt32();
                _animData = br.ReadBytes((int)nAnims * 32);
                _animFrameOffsets = new uint[nAnims];
                _animNumFrames    = new int[nAnims];
                for (int i = 0; i < nAnims; i++)
                {
                    _animFrameOffsets[i] = BitConverter.ToUInt32(_animData, i * 32);
                    int fStart = BitConverter.ToUInt16(_animData, i * 32 + 16);
                    int fEnd   = BitConverter.ToUInt16(_animData, i * 32 + 18);
                    _animNumFrames[i] = Math.Max(1, fEnd - fStart + 1);
                }
                _animFrameSizes = new int[nAnims];
                var sortedFO = new SortedSet<uint>(_animFrameOffsets);
                for (int i = 0; i < nAnims; i++)
                {
                    uint fo = _animFrameOffsets[i];
                    int  nf = _animNumFrames[i];
                    uint nextFO = 0;
                    foreach (var x in sortedFO) { if (x > fo) { nextFO = x; break; } }
                    if (nextFO > 0)
                    {
                        uint delta = nextFO - fo;
                        if (nf > 0 && delta % (uint)(nf * 2) == 0)
                            _animFrameSizes[i] = (int)(delta / (uint)(nf * 2));
                    }
                }
                _logger($"[TR3Parser] Animations: {nAnims}");

                uint nSC = br.ReadUInt32(); br.BaseStream.Seek(nSC * 6, SeekOrigin.Current);
                uint nAD = br.ReadUInt32(); br.BaseStream.Seek(nAD * 8, SeekOrigin.Current);
                uint nAC = br.ReadUInt32(); br.BaseStream.Seek(nAC * 2, SeekOrigin.Current);

                uint nMT = br.ReadUInt32();
                _meshTrees = new int[nMT];
                for (int i = 0; i < nMT; i++) _meshTrees[i] = br.ReadInt32();
                _logger($"[TR3Parser] MeshTrees: {nMT}");

                uint nFrames = br.ReadUInt32();
                _frames = new ushort[nFrames];
                for (int i = 0; i < nFrames; i++) _frames[i] = br.ReadUInt16();
                _logger($"[TR3Parser] Frames: {nFrames} words");

                uint nModels = br.ReadUInt32();
                _models = new TR3Model[nModels];
                for (int i = 0; i < nModels; i++)
                    _models[i] = new TR3Model
                    {
                        ID           = br.ReadUInt32(),
                        NumMeshes    = br.ReadUInt16(),
                        StartingMesh = br.ReadUInt16(),
                        MeshTree     = br.ReadUInt32(),
                        FrameOffset  = br.ReadUInt32(),
                        Animation    = br.ReadUInt16()
                    };
                _logger($"[TR3Parser] Models: {nModels}");

                uint nStatics = br.ReadUInt32();
                _statics = new TR3StaticMesh[nStatics];
                for (int i = 0; i < nStatics; i++)
                    _statics[i] = new TR3StaticMesh
                    {
                        ID      = br.ReadUInt32(), Mesh = br.ReadUInt16(),
                        VisMinX = br.ReadInt16(), VisMaxX = br.ReadInt16(),
                        VisMinY = br.ReadInt16(), VisMaxY = br.ReadInt16(),
                        VisMinZ = br.ReadInt16(), VisMaxZ = br.ReadInt16(),
                        ColMinX = br.ReadInt16(), ColMaxX = br.ReadInt16(),
                        ColMinY = br.ReadInt16(), ColMaxY = br.ReadInt16(),
                        ColMinZ = br.ReadInt16(), ColMaxZ = br.ReadInt16(),
                        Flags   = br.ReadUInt16()
                    };
                _logger($"[TR3Parser] StaticMeshes: {nStatics}");

                
                if (_isTr2)
                {
                    uint nOTt2 = br.ReadUInt32();
                    _objectTextures = new TR3ObjectTexture[nOTt2];
                    for (int i = 0; i < nOTt2; i++)
                    {
                        ushort attr = br.ReadUInt16();
                        ushort tile = br.ReadUInt16();
                        var uv = new Vector2[4];
                        for (int v = 0; v < 4; v++)
                        {
                            ushort xc = br.ReadUInt16();
                            ushort yc = br.ReadUInt16();
                            float u = (xc >> 8) + (xc & 0xFF) / 256.0f;
                            float v2 = (yc >> 8) + (yc & 0xFF) / 256.0f;
                            uv[v] = new Vector2(u, v2);
                        }
                        _objectTextures[i] = new TR3ObjectTexture { Attribute = attr, Tile = tile, UV = uv };
                    }
                    _logger($"[TR3Parser] ObjectTextures: {nOTt2}");
                }

                uint nST = br.ReadUInt32();
                _spriteTextures = new TR3SpriteTexture[nST];
                for (int i = 0; i < nST; i++)
                    _spriteTextures[i] = new TR3SpriteTexture
                    {
                        Tile      = br.ReadUInt16(),
                        X         = br.ReadByte(), Y = br.ReadByte(),
                        Width     = br.ReadUInt16(), Height = br.ReadUInt16(),
                        LeftSide  = br.ReadInt16(), TopSide    = br.ReadInt16(),
                        RightSide = br.ReadInt16(), BottomSide = br.ReadInt16()
                    };
                _logger($"[TR3Parser] SpriteTextures: {nST}");

                uint nSS = br.ReadUInt32();
                _spriteSequences = new TR3SpriteSequence[nSS];
                for (int i = 0; i < nSS; i++)
                    _spriteSequences[i] = new TR3SpriteSequence
                    {
                        SpriteID       = br.ReadInt32(),
                        NegativeLength = br.ReadInt16(),
                        Offset         = br.ReadInt16()
                    };
                _logger($"[TR3Parser] SpriteSequences: {nSS}");

                if (!_isTr2)
                {
                    uint nCam = br.ReadUInt32(); br.BaseStream.Seek(nCam * 16, SeekOrigin.Current);
                    uint nSndSrc = br.ReadUInt32(); br.BaseStream.Seek(nSndSrc * 16, SeekOrigin.Current);
                    uint nBoxes = br.ReadUInt32(); br.BaseStream.Seek(nBoxes * 8, SeekOrigin.Current);
                    uint nOverlaps = br.ReadUInt32(); br.BaseStream.Seek(nOverlaps * 2, SeekOrigin.Current);
                    br.BaseStream.Seek(nBoxes * 10 * 2, SeekOrigin.Current);
                    uint nAnimTex = br.ReadUInt32(); br.BaseStream.Seek(nAnimTex * 2, SeekOrigin.Current);
                    _logger($"[TR3Parser] Boxes:{nBoxes} Overlaps:{nOverlaps} AnimatedTextures:{nAnimTex}");

                    uint nOT = br.ReadUInt32();
                    _objectTextures = new TR3ObjectTexture[nOT];
                    for (int i = 0; i < nOT; i++)
                    {
                        ushort attr = br.ReadUInt16();
                        ushort tile = br.ReadUInt16();
                        var uv = new Vector2[4];
                        for (int v = 0; v < 4; v++)
                        {
                            ushort xc = br.ReadUInt16();
                            ushort yc = br.ReadUInt16();
                            float u = (xc >> 8) + (xc & 0xFF) / 256.0f;
                            float v2 = (yc >> 8) + (yc & 0xFF) / 256.0f;
                            uv[v] = new Vector2(u, v2);
                        }
                        _objectTextures[i] = new TR3ObjectTexture { Attribute = attr, Tile = tile, UV = uv };
                    }
                    _logger($"[TR3Parser] ObjectTextures: {nOT}");
                }

                _logger("[TR3Parser] Parsing completed — building Wad2...");
                return BuildWad2();
            }
            catch (Exception ex)
            {
                _logger($"[TR3Parser Error] {ex.GetType().Name}: {ex.Message}");
                _logger($"[TR3Parser] {ex.StackTrace?.Split('\n')[0].Trim()}");
                return null;
            }
        }

        private static byte MaxVal(byte[] arr) { byte m=0; foreach(var b in arr) if(b>m)m=b; return m; }

        private WadTexture[] BuildTileTextures(BinaryReader br, long tilesStart, int numTiles, byte[] pal8)
        {
            var result = new WadTexture[numTiles];
            long saved = br.BaseStream.Position;
            br.BaseStream.Position = tilesStart;

            for (int t = 0; t < numTiles; t++)
            {
                byte[] indexed = br.ReadBytes(65536);
                var img = ImageC.CreateNew(256, 256);
                for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                {
                    int ci  = indexed[y * 256 + x] * 3;
                    byte r = pal8[ci], g = pal8[ci+1], b = pal8[ci+2];
                    byte a = (indexed[y * 256 + x] == 0) ? (byte)0 : (byte)255;
                    img.SetPixel(x, y, new ColorC(r, g, b, a));
                }
                result[t] = new WadTexture(img);
            }

            br.BaseStream.Position = saved;
            return result;
        }

        private (WadTexture tex, TextureArea area) GetObjectTexture(int objTexIdx, bool isTriangle)
        {
            if (objTexIdx < 0 || objTexIdx >= _objectTextures.Length)
                return MakeFallbackArea();

            var ot = _objectTextures[objTexIdx];

            int tileIdx = ot.Tile & 0x7FFF;
            if (tileIdx < 0 || tileIdx >= _tileTextures.Length || _tileTextures.Length == 0)
                return MakeFallbackArea();

            var tileTex = _tileTextures[tileIdx];

            var area = new TextureArea();
            area.Texture      = tileTex;
            area.DoubleSided  = false;
            area.BlendMode    = ot.Attribute == 1
                ? BlendMode.AlphaTest
                : ot.Attribute == 2
                    ? BlendMode.Additive
                    : BlendMode.Normal;

            area.TexCoord0 = ot.UV[0];
            area.TexCoord1 = ot.UV[1];
            area.TexCoord2 = ot.UV[2];
            area.TexCoord3 = isTriangle ? ot.UV[2] : ot.UV[3];

            return (tileTex, area);
        }

        
        private void SkipRooms(BinaryReader br)
        {
            ushort n = br.ReadUInt16();
            _logger($"[TR3Parser] Rooms: {n} — skipping...");
            for (int r = 0; r < n; r++)
            {
                br.ReadBytes(16);                                                   // RoomInfo
                uint dw = br.ReadUInt32(); br.BaseStream.Seek(dw * 2, SeekOrigin.Current); // RoomData
                ushort nP = br.ReadUInt16(); br.BaseStream.Seek(nP * 32, SeekOrigin.Current); // Portals
                ushort nZ = br.ReadUInt16(), nX = br.ReadUInt16();
                br.BaseStream.Seek(nZ * nX * 8, SeekOrigin.Current);                // Sectors
                br.ReadInt16();                                                      // AmbientIntensity
                if (_isTr2) br.ReadInt16();                                          // AmbientIntensity2 (SOLO TR2)
                br.ReadInt16();                                                      // LightMode
                ushort nL = br.ReadUInt16(); br.BaseStream.Seek(nL * 24, SeekOrigin.Current); // Lights (tr2/tr3_room_light = 24 bytes)
                ushort nM = br.ReadUInt16(); br.BaseStream.Seek(nM * 20, SeekOrigin.Current); // Meshes (tr2/tr3_room_staticmesh = 20 bytes)
                br.ReadInt16(); br.ReadInt16();                                      // AlternateRoom, Flags
                if (!_isTr2) { br.ReadByte(); br.ReadByte(); br.ReadByte(); }        // WaterScheme, ReverbInfo, Filler (SOLO TR3+)
            }
            _logger($"[TR3Parser] Rooms skipped. Pos: {br.BaseStream.Position:N0}");
        }

        
        private Wad2 BuildWad2()
        {
            var wad = new Wad2();
            wad.GameVersion = _isTr2 ? TRVersion.Game.TR2 : TRVersion.Game.TR3;

            int ok = 0, fail = 0;
            for (int modelIdx = 0; modelIdx < _models.Length; modelIdx++)
            {
                var model = _models[modelIdx];
                try { wad.Moveables[new WadMoveableId(model.ID)] = BuildMoveable(model, modelIdx); ok++; }
                catch (Exception ex)
                {
                    fail++;
                    _logger($"[TR3Parser] Moveable {model.ID}: {ex.GetType().Name}: {ex.Message}");
                    var st = ex.StackTrace;
                    if (st != null)
                        foreach (var line in st.Split('\n'))
                            if (line.Contains("TR3Wad2Builder"))
                                _logger($"[TR3Parser]   {line.Trim()}");
                }
            }
            _logger($"[TR3Parser] Moveables: {ok} OK, {fail} fail");

            ok = 0; fail = 0;
            foreach (var s in _statics)
            {
                try { wad.Statics[new WadStaticId(s.ID)] = BuildStatic(s); ok++; }
                catch (Exception ex) { fail++; _logger($"[TR3Parser] Static {s.ID}: {ex.Message}"); }
            }
            _logger($"[TR3Parser] Statics: {ok} OK, {fail} fail");

            ok = 0; fail = 0;
            foreach (var seq in _spriteSequences)
            {
                try { wad.SpriteSequences[new WadSpriteSequenceId((uint)seq.SpriteID)] = BuildSpriteSequence(seq); ok++; }
                catch (Exception ex) { fail++; _logger($"[TR3Parser] Sprite {seq.SpriteID}: {ex.Message}"); }
            }
            _logger($"[TR3Parser] Sprites: {ok} OK, {fail} fail");

            _logger($"[TR3Parser] Wad2: {wad.Moveables.Count} moveables, {wad.Statics.Count} statics, {wad.SpriteSequences.Count} sprites.");
            return wad;
        }

        private WadMoveable BuildMoveable(TR3Model model, int modelIndex)
        {
            var mov = new WadMoveable(new WadMoveableId(model.ID));

            for (int m = 0; m < model.NumMeshes; m++)
            {
                int meshIdx = model.StartingMesh + m;
                var bone = new WadBone();
                bone.Name = $"bone_{m}";
                bone.Mesh = meshIdx < _meshPtrs.Length
                    ? ParseMesh((int)_meshPtrs[meshIdx])
                    : new WadMesh { Name = "mesh_empty" };

                if (m == 0)
                {
                    bone.Translation = Vector3.Zero;
                    bone.OpCode = WadLinkOpcode.NotUseStack;
                }
                else
                {
                    int mtOffset = (int)model.MeshTree + (m - 1) * 4;
                    int flags = 0;
                    Vector3 t = Vector3.Zero;
                    if (mtOffset + 3 < _meshTrees.Length)
                    {
                        flags = _meshTrees[mtOffset + 0];
                        t = new Vector3(_meshTrees[mtOffset + 1], -_meshTrees[mtOffset + 2], _meshTrees[mtOffset + 3]);
                    }
                    bone.Translation = t;
                    bone.OpCode = flags switch
                    {
                        1 => WadLinkOpcode.Pop,
                        2 => WadLinkOpcode.Push,
                        3 => WadLinkOpcode.Read,
                        _ => WadLinkOpcode.NotUseStack
                    };
                }

                mov.Bones.Add(bone);
            }

            if (model.Animation != 0xFFFF)
            {
                int firstAnim = model.Animation;
                int animCount = 1;

                for (int nextM = modelIndex + 1; nextM < _models.Length; nextM++)
                {
                    if (_models[nextM].Animation != 0xFFFF)
                    {
                        animCount = Math.Max(1, _models[nextM].Animation - firstAnim);
                        break;
                    }
                }
                if (animCount == 1 && firstAnim + 1 < _animFrameOffsets.Length)
                {
                    bool isLast = true;
                    for (int nextM = modelIndex + 1; nextM < _models.Length; nextM++)
                        if (_models[nextM].Animation != 0xFFFF) { isLast = false; break; }
                    if (isLast)
                        animCount = _animFrameOffsets.Length - firstAnim;
                }

                animCount = Math.Max(1, Math.Min(animCount, _animFrameOffsets.Length - firstAnim));

                for (int a = 0; a < animCount; a++)
                    mov.Animations.Add(ParseAnimation(firstAnim + a, model.NumMeshes));

                _logger($"[TR3Parser]   Moveable {model.ID}: {animCount} animations (from {firstAnim})");
            }
            else
            {
                var idle = new WadAnimation { Name = "anim_idle" };
                var kf = new WadKeyFrame();
                for (int b = 0; b < mov.Bones.Count; b++)
                {
                    int dummy = 0;
                    var zero = new List<short> { 0, 0 };
                    kf.Angles.Add(WadKeyFrameRotation.FromTrAngle(ref dummy, zero, false, false));
                }
                idle.KeyFrames.Add(kf);
                mov.Animations.Add(idle);
            }

            return mov;
        }

        private WadStatic BuildStatic(TR3StaticMesh s)
        {
            var stat = new WadStatic(new WadStaticId(s.ID));
            stat.Mesh = s.Mesh < _meshPtrs.Length
                ? ParseMesh((int)_meshPtrs[s.Mesh])
                : new WadMesh { Name = "mesh_empty" };
            stat.VisibilityBox = new BoundingBox(
                new Vector3(s.VisMinX, -s.VisMaxY, s.VisMinZ),
                new Vector3(s.VisMaxX, -s.VisMinY, s.VisMaxZ));
            stat.CollisionBox = new BoundingBox(
                new Vector3(s.ColMinX, -s.ColMaxY, s.ColMinZ),
                new Vector3(s.ColMaxX, -s.ColMinY, s.ColMaxZ));
            return stat;
        }

        private WadSpriteSequence BuildSpriteSequence(TR3SpriteSequence seq)
        {
            var sprSeq = new WadSpriteSequence(new WadSpriteSequenceId((uint)seq.SpriteID));
            int count = Math.Abs(seq.NegativeLength);
            for (int i = 0; i < count; i++)
            {
                int idx = seq.Offset + i;
                if (idx >= _spriteTextures.Length) continue;
                var st = _spriteTextures[idx];

                int tileIdx = st.Tile < _tileTextures.Length ? st.Tile : 0;
                if (_tileTextures.Length == 0) continue;
                int actualW = (st.Width  / 256) + 1;
                int actualH = (st.Height / 256) + 1;
                actualW = Math.Max(1, Math.Min(actualW, 256 - st.X));
                actualH = Math.Max(1, Math.Min(actualH, 256 - st.Y));

                var croppedImg = ImageC.CreateNew(actualW, actualH);
                var tileImg = _tileTextures[tileIdx].Image;
                for (int y = 0; y < actualH; y++)
                for (int x = 0; x < actualW; x++)
                {
                    int srcX = st.X + x;
                    int srcY = st.Y + y;
                    if (srcX < 256 && srcY < 256)
                        croppedImg.SetPixel(x, y, tileImg.GetPixel(srcX, srcY));
                }

                var spr = new WadSprite
                {
                    Texture = new WadTexture(croppedImg)
                };
                sprSeq.Sprites.Add(spr);
            }
            return sprSeq;
        }

        
        private WadMesh ParseMesh(int byteOffset)
        {
            var mesh = new WadMesh { Name = "mesh" };
            if (byteOffset < 0 || byteOffset >= _meshData.Length) return mesh;

            using var ms = new MemoryStream(_meshData, byteOffset, _meshData.Length - byteOffset);
            using var br = new BinaryReader(ms);

            short cx = br.ReadInt16(), cy = br.ReadInt16(), cz = br.ReadInt16();
            int   cr = br.ReadInt32();

            short nVerts = br.ReadInt16();
            for (int i = 0; i < nVerts; i++)
                mesh.VertexPositions.Add(new Vector3(br.ReadInt16(), -br.ReadInt16(), br.ReadInt16()));

            short nNormals = br.ReadInt16();
            if (nNormals > 0)
                for (int i = 0; i < nNormals; i++)
                    mesh.VertexNormals.Add(new Vector3(br.ReadInt16(), -br.ReadInt16(), br.ReadInt16()));
            else
            {
                for (int i = 0; i < Math.Abs(nNormals); i++) br.ReadInt16();
                for (int i = 0; i < nVerts; i++) mesh.VertexNormals.Add(Vector3.UnitY);
            }

            short nTQ = br.ReadInt16();
            for (int i = 0; i < nTQ; i++)
            {
                int v0=br.ReadUInt16(), v1=br.ReadUInt16(), v2=br.ReadUInt16(), v3=br.ReadUInt16();
                int ti = br.ReadUInt16() & 0x7FFF; 
                var (_, area) = ti < _objectTextures.Length
                    ? GetObjectTexture(ti, false)
                    : MakeFallbackArea();
                mesh.Polys.Add(new WadPolygon { Shape=WadPolygonShape.Quad,
                    Index0=v0, Index1=v1, Index2=v2, Index3=v3, Texture=area });
            }
            short nTT = br.ReadInt16();
            for (int i = 0; i < nTT; i++)
            {
                int v0=br.ReadUInt16(), v1=br.ReadUInt16(), v2=br.ReadUInt16();
                int ti = br.ReadUInt16() & 0x7FFF;
                var (_, area) = ti < _objectTextures.Length
                    ? GetObjectTexture(ti, true)
                    : MakeFallbackArea();
                mesh.Polys.Add(new WadPolygon { Shape=WadPolygonShape.Triangle,
                    Index0=v0, Index1=v1, Index2=v2, Texture=area });
            }

            short nCQ = br.ReadInt16();
            for (int i = 0; i < nCQ; i++)
            {
                int v0=br.ReadUInt16(), v1=br.ReadUInt16(), v2=br.ReadUInt16(), v3=br.ReadUInt16();
                int palIdx = br.ReadUInt16() & 0xFF;
                var solidArea = MakeSolidColorArea(palIdx, false);
                mesh.Polys.Add(new WadPolygon { Shape=WadPolygonShape.Quad,
                    Index0=v0, Index1=v1, Index2=v2, Index3=v3, Texture=solidArea });
            }

            short nCT = br.ReadInt16();
            for (int i = 0; i < nCT; i++)
            {
                int v0=br.ReadUInt16(), v1=br.ReadUInt16(), v2=br.ReadUInt16();
                int palIdx = br.ReadUInt16() & 0xFF;
                var solidArea = MakeSolidColorArea(palIdx, true);
                mesh.Polys.Add(new WadPolygon { Shape=WadPolygonShape.Triangle,
                    Index0=v0, Index1=v1, Index2=v2, Texture=solidArea });
            }

            mesh.BoundingBox    = new BoundingBox(new Vector3(cx-cr,cy-cr,cz-cr), new Vector3(cx+cr,cy+cr,cz+cr));
            mesh.BoundingSphere = new BoundingSphere(new Vector3(cx,cy,cz), cr);
            return mesh;
        }

        private (WadTexture tex, TextureArea area) MakeFallbackArea()
        {
            var fb = _tileTextures.Length > 0 ? _tileTextures[0] : new WadTexture(ImageC.CreateNew(1,1));
            var area = new TextureArea { Texture = fb };
            area.TexCoord0 = new Vector2(0, 0);
            area.TexCoord1 = new Vector2(1, 0);
            area.TexCoord2 = new Vector2(1, 1);
            area.TexCoord3 = new Vector2(0, 1);
            return (fb, area);
        }
        private readonly Dictionary<int, WadTexture> _solidColorCache = new();

        private TextureArea MakeSolidColorArea(int paletteIndex, bool isTriangle)
        {
            if (!_solidColorCache.TryGetValue(paletteIndex, out var tex))
            {
                var img = ImageC.CreateNew(1, 1);
                if (_palette8 != null && paletteIndex < 256)
                {
                    int ci = paletteIndex * 3;
                    img.SetPixel(0, 0, new ColorC(_palette8[ci], _palette8[ci+1], _palette8[ci+2], 255));
                }
                else
                {
                    img.SetPixel(0, 0, new ColorC(128, 128, 128, 255)); // fallback
                }

                tex = new WadTexture(img);
                _solidColorCache[paletteIndex] = tex;
            }

            var area = new TextureArea { Texture = tex };
            area.TexCoord0 = new Vector2(0, 0);
            area.TexCoord1 = new Vector2(1, 0);
            area.TexCoord2 = new Vector2(1, 1);
            area.TexCoord3 = isTriangle ? new Vector2(1, 1) : new Vector2(0, 1);
            return area;
        }

        private WadAnimation ParseAnimation(int animIdx, int numBones = 0)
        {
            var anim = new WadAnimation();
            int byteOff = animIdx * 32;
            if (byteOff + 32 > _animData.Length) return anim;

            using var ms = new MemoryStream(_animData, byteOff, 32);
            using var br = new BinaryReader(ms);

            uint   frameOffset = br.ReadUInt32();
            byte   frameRate   = br.ReadByte();
            byte   frameSizeRaw = br.ReadByte();
            ushort stateID     = br.ReadUInt16();
            br.ReadInt32(); br.ReadInt32();
            ushort frameStart  = br.ReadUInt16();
            ushort frameEnd    = br.ReadUInt16();
            ushort nextAnim    = br.ReadUInt16();
            ushort nextFrame   = br.ReadUInt16();
            br.ReadUInt16(); br.ReadUInt16(); br.ReadUInt16(); br.ReadUInt16();

            int frameSize = frameSizeRaw > 0 ? frameSizeRaw : Math.Max(9, 9 + numBones * 2);

            anim.Name          = $"anim_{animIdx}";
            anim.StateId       = stateID;
            anim.FrameRate     = frameRate > 0 ? frameRate : (byte)1;
            anim.NextAnimation = nextAnim;
            anim.NextFrame     = nextFrame;

            int numFrames = frameEnd >= frameStart ? frameEnd - frameStart + 1 : 1;
            int wordStart = (int)(frameOffset / 2);

            if (wordStart < 0 || wordStart + 9 > _frames.Length)
            {
                anim.EndFrame = 0;
                anim.KeyFrames.Add(new WadKeyFrame());
                return anim;
            }

            anim.EndFrame = (ushort)Math.Max(0, numFrames - 1);

            for (int f = 0; f < numFrames; f++)
            {
                int wOff = wordStart + f * frameSize;
                if (wOff < 0 || wOff + 9 > _frames.Length) break;
                anim.KeyFrames.Add(ParseKeyFrame(wOff, frameSize, numBones));
            }

            if (anim.KeyFrames.Count == 0)
                anim.KeyFrames.Add(new WadKeyFrame());

            return anim;
        }

        private WadKeyFrame ParseKeyFrame(int wordOffset, int frameSizeWords, int numBones = 0)
        {
            var kf = new WadKeyFrame();
            if (wordOffset < 0 || wordOffset + 9 > _frames.Length) return kf;

            kf.BoundingBox = new BoundingBox(
                new Vector3((short)_frames[wordOffset+0], NegateClamped(_frames[wordOffset+3]), (short)_frames[wordOffset+4]),
                new Vector3((short)_frames[wordOffset+1], NegateClamped(_frames[wordOffset+2]), (short)_frames[wordOffset+5]));
            kf.Offset = new Vector3(
                (short)_frames[wordOffset+6], NegateClamped(_frames[wordOffset+7]), (short)_frames[wordOffset+8]);

            int angleStart = wordOffset + 9;

            int totalAngleWords = Math.Max(0, frameSizeWords - 9);
            int safeWindow = Math.Max(totalAngleWords, numBones * 2);
            int available = Math.Max(0, _frames.Length - angleStart);
            int windowSize = Math.Min(safeWindow, available);

            var frameDataList = new List<short>(windowSize);
            for (int w = 0; w < windowSize; w++)
                frameDataList.Add((short)_frames[angleStart + w]);

            int angleIdx = 0;
            for (int b = 0; b < numBones && angleIdx < frameDataList.Count; b++)
            {
                int before = angleIdx;
                kf.Angles.Add(WadKeyFrameRotation.FromTrAngle(ref angleIdx, frameDataList, false, false));
                if (angleIdx <= before) { angleIdx = before + 1; } 
            }

            if (numBones > 0)
            {
                while (kf.Angles.Count < numBones)
                {
                    int dummy = 0;
                    var zero = new List<short> { 0, 0 };
                    kf.Angles.Add(WadKeyFrameRotation.FromTrAngle(ref dummy, zero, false, false));
                }
                if (kf.Angles.Count > numBones)
                    kf.Angles.RemoveRange(numBones, kf.Angles.Count - numBones);
            }

            return kf;
        }

        private static float NegateClamped(ushort raw)
        {
            short s = (short)raw;
            return s == short.MinValue ? short.MaxValue : -s;
        }
    }
}
