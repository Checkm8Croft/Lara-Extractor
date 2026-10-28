using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using TombLib;
using TombLib.LevelData;
using TombLib.Utils;
using TombLib.Wad;

namespace Lara_Extractor
{
    /// <summary>
    /// Wad2 export for a PlayStation Tomb Raider 1-3 level (.PSX). See PSXClassicLevel for the file layout.
    /// Moveables (with bones and animations), statics and sprites are converted; textures come from the 4-bit
    /// textiles and CLUTs. Only the TR3 format has been tested on a real file.
    /// </summary>
    public sealed class TR123PSXWad2Processor
    {
        private readonly string _inputPath;
        private readonly string _outputWad2Path;
        private readonly Action<string> _logger;

        private PSXClassicLevel _level = null!;
        private readonly Dictionary<(int Tex, bool Tri), TextureArea> _areaCache = new();
        private int _missingTextures;

        public TR123PSXWad2Processor(string inputPath, string outputWad2Path, Action<string> logger)
        {
            _inputPath = inputPath;
            _outputWad2Path = outputWad2Path;
            _logger = logger;
        }

        public void ExtractWad2()
        {
            _logger("[PSX Wad2] ========== STARTING PSX TR1-3 WAD2 PIPELINE ==========");

            if (!File.Exists(_inputPath))
            {
                _logger("[PSX Wad2 Error] Input level file not found.");
                return;
            }

            var level = PSXClassicLevel.TryParse(File.ReadAllBytes(_inputPath), out string error);
            if (level == null)
            {
                _logger($"[PSX Wad2 Error] Cannot read the level: {error}.");
                return;
            }

            _level = level;
            Wad2 wad = BuildWad2();

            string? dir = Path.GetDirectoryName(_outputWad2Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Wad2Writer.SaveToFile(wad, _outputWad2Path);
            _logger($"[PSX Wad2] ========== WAD2 SAVED TO: {_outputWad2Path} ==========");
        }

        private Wad2 BuildWad2()
        {
            var wad = new Wad2 { GameVersion = _level.Game switch { PSXClassicLevel.GameKind.TR1 => TRVersion.Game.TR1, PSXClassicLevel.GameKind.TR2 => TRVersion.Game.TR2, _ => TRVersion.Game.TR3 } };

            var animStarts = _level.Models.Where(m => m.Animation != 0xFFFF).Select(m => m.Animation).Distinct().OrderBy(x => x).ToList();

            int moveables = 0, failed = 0, missingMeshes = 0;
            foreach (var model in _level.Models)
            {
                try
                {
                    wad.Moveables[new WadMoveableId((uint)model.Id)] = BuildMoveable(model, animStarts, ref missingMeshes);
                    moveables++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger($"[PSX Wad2] Moveable {model.Id}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            int statics = 0;
            foreach (var st in _level.Statics)
            {
                var mesh = _level.ReadMesh(st.Mesh);
                if (mesh == null) continue;

                var stat = new WadStatic(new WadStaticId((uint)st.Id))
                {
                    Mesh = PSXWadMeshBuilder.Build(mesh, $"static_{st.Id}", GetTextureArea),
                    Flags = (short)st.Flags
                };
                // Boxes are minX maxX minY maxY minZ maxZ, with Y pointing down in the file.
                stat.VisibilityBox = new BoundingBox(new Vector3(st.Visibility[0], -st.Visibility[3], st.Visibility[4]), new Vector3(st.Visibility[1], -st.Visibility[2], st.Visibility[5]));
                stat.CollisionBox = new BoundingBox(new Vector3(st.Collision[0], -st.Collision[3], st.Collision[4]), new Vector3(st.Collision[1], -st.Collision[2], st.Collision[5]));
                wad.Statics[new WadStaticId((uint)st.Id)] = stat;
                statics++;
            }

            int sprites = 0;
            foreach (var seq in _level.SpriteSequences)
            {
                var wadSeq = new WadSpriteSequence(new WadSpriteSequenceId((uint)seq.Id));
                for (int i = 0; i < seq.Count; i++)
                {
                    int index = seq.Offset + i;
                    if (index < 0 || index >= _level.SpriteTextures.Count) continue;
                    var s = _level.SpriteTextures[index];
                    if (!_level.TryRenderRect(s.Tile, s.Clut, s.U0, s.V0, s.U1, s.V1, out int w, out int h, out byte[] bgra)) continue;
                    wadSeq.Sprites.Add(new WadSprite { Texture = new WadTexture(ImageC.FromByteArray(bgra, w, h)) });
                }
                if (wadSeq.Sprites.Count == 0) continue;
                wad.SpriteSequences[new WadSpriteSequenceId((uint)seq.Id)] = wadSeq;
                sprites++;
            }

            _logger($"[PSX Wad2] Moveables {moveables} ({failed} failed, {missingMeshes} bone mesh(es) missing), statics {statics}, sprite sequences {sprites}.");
            if (_missingTextures > 0)
                _logger($"[PSX Wad2 Warning] {_missingTextures} polygon(s) used a texture that could not be rendered.");
            return wad;
        }

        // ---------------------------------------------------------------- moveables

        private WadMoveable BuildMoveable(PSXClassicLevel.Model model, List<int> animStarts, ref int missingMeshes)
        {
            var mov = new WadMoveable(new WadMoveableId((uint)model.Id));

            for (int i = 0; i < model.NumMeshes; i++)
            {
                var src = _level.ReadMesh(model.StartingMesh + i);
                if (src == null) missingMeshes++;

                var bone = new WadBone
                {
                    Name = $"bone_{i}",
                    Mesh = src != null ? PSXWadMeshBuilder.Build(src, $"moveable_{model.Id}_mesh_{i}", GetTextureArea) : new WadMesh { Name = "mesh_missing" }
                };

                if (i == 0)
                {
                    bone.Translation = Vector3.Zero;
                    bone.OpCode = WadLinkOpcode.NotUseStack;
                }
                else
                {
                    int b = model.MeshTree + (i - 1) * 4;
                    int flags = _level.ReadMeshTreeInt(b);
                    bone.Translation = new Vector3(_level.ReadMeshTreeInt(b + 1), -_level.ReadMeshTreeInt(b + 2), _level.ReadMeshTreeInt(b + 3));
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
                int first = model.Animation;
                int end = animStarts.FirstOrDefault(s => s > first);
                if (end == 0) end = _level.AnimCount;
                for (int a = first; a < end && a < _level.AnimCount; a++)
                {
                    var anim = BuildAnimation(a, first, end - first, model.NumMeshes);
                    if (anim != null) mov.Animations.Add(anim);
                }
            }

            return mov;
        }

        private WadAnimation? BuildAnimation(int index, int firstAnim, int animCount, int numBones)
        {
            var src = _level.ReadAnim(index);
            if (src == null) return null;

            int rate = Math.Max(1, src.Rate);
            int length = Math.Max(0, src.FrameEnd - src.FrameStart);

            var anim = new WadAnimation
            {
                Name = $"Animation {index - firstAnim}",
                StateId = (ushort)src.State,
                FrameRate = (byte)rate,
                EndFrame = (ushort)length,
                NextAnimation = (ushort)Math.Clamp(src.NextAnim - firstAnim, 0, Math.Max(0, animCount - 1)),
                // 16.16 fixed point, changing by the acceleration every frame.
                StartVelocity = src.Speed / 65536.0f,
                EndVelocity = (src.Speed + (long)src.Accel * length) / 65536.0f
            };

            var target = _level.ReadAnim(src.NextAnim);
            anim.NextFrame = (ushort)Math.Max(0, src.NextFrame - (target?.FrameStart ?? 0));

            // Key frames are stored every <rate> frames, plus one more to interpolate up to the last frame.
            int keyFrames = length / rate + 1 + (length % rate != 0 ? 1 : 0);
            int frameWords = src.FrameSizeWords;
            if (frameWords >= 9)
            {
                for (int f = 0; f < keyFrames; f++)
                {
                    long offset = src.FrameByteOffset + (long)f * frameWords * 2;
                    int want = frameWords + numBones * 2;
                    short[]? words = null;
                    while (want >= frameWords && (words = _level.ReadFrameWords(offset, want)) == null)
                        want -= 2;
                    if (words == null) break;
                    anim.KeyFrames.Add(BuildKeyFrame(words, numBones));
                }
            }
            if (anim.KeyFrames.Count == 0)
                anim.KeyFrames.Add(new WadKeyFrame());

            for (int c = 0; c < src.NumChanges; c++)
            {
                var (state, count, firstDispatch) = _level.ReadChange(src.ChangeIndex + c);
                var change = new WadStateChange { StateId = (ushort)state };
                for (int d = 0; d < count; d++)
                {
                    var (low, high, nextAnim, nextFrame) = _level.ReadDispatch(firstDispatch + d);
                    var next = _level.ReadAnim(nextAnim);
                    ushort nextLocal = (ushort)Math.Max(0, nextFrame - (next?.FrameStart ?? 0));
                    change.Dispatches.Add(new WadAnimDispatch
                    {
                        InFrame = (ushort)Math.Max(0, low - src.FrameStart),
                        OutFrame = (ushort)Math.Max(0, high - src.FrameStart),
                        NextAnimation = (ushort)Math.Clamp(nextAnim - firstAnim, 0, Math.Max(0, animCount - 1)),
                        NextLowFrame = nextLocal,
                        NextHighFrame = nextLocal
                    });
                }
                anim.StateChanges.Add(change);
            }

            int ptr = src.CommandIndex;
            for (int c = 0; c < src.NumCommands && ptr < _level.CommandCount; c++)
            {
                short opcode = _level.ReadCommand(ptr++);
                var cmd = new WadAnimCommand { Type = (WadAnimCommandType)opcode };
                switch (opcode)
                {
                    case 1:
                        cmd.Parameter1 = _level.ReadCommand(ptr++);
                        cmd.Parameter2 = _level.ReadCommand(ptr++);
                        cmd.Parameter3 = _level.ReadCommand(ptr++);
                        break;
                    case 2:
                        cmd.Parameter1 = _level.ReadCommand(ptr++);
                        cmd.Parameter2 = _level.ReadCommand(ptr++);
                        break;
                    case 5:
                    case 6:
                        cmd.Parameter1 = _level.ReadCommand(ptr++);
                        cmd.Parameter2 = _level.ReadCommand(ptr++);
                        cmd.ConvertLegacyConditions();
                        break;
                }
                anim.AnimCommands.Add(cmd);
            }

            return anim;
        }

        private static WadKeyFrame BuildKeyFrame(short[] words, int numBones)
        {
            // Header: min X, max X, min Y, max Y, min Z, max Z, root X, root Y, root Z.
            var kf = new WadKeyFrame
            {
                BoundingBox = new BoundingBox(new Vector3(words[0], -words[3], words[4]), new Vector3(words[1], -words[2], words[5])),
                Offset = new Vector3(words[6], -words[7], words[8])
            };

            var data = new List<short>(words.Length - 9);
            for (int i = 9; i < words.Length; i++) data.Add(words[i]);
            while (data.Count < numBones * 2) data.Add(0);

            int index = 0;
            for (int b = 0; b < numBones; b++)
            {
                int before = index;
                kf.Angles.Add(WadKeyFrameRotation.FromTrAngle(ref index, data, false, false));   // TR2/TR3 packed angles
                if (index <= before) index = before + 1;
            }

            return kf;
        }

        // ---------------------------------------------------------------- textures

        private TextureArea GetTextureArea(int texIndex, bool isTriangle)
        {
            if (_areaCache.TryGetValue((texIndex, isTriangle), out var cached))
                return cached;

            var area = new TextureArea { DoubleSided = false };
            var t = _level.ObjectTextures[texIndex];

            if (!_level.TryRender(t, isTriangle, out int w, out int h, out byte[] bgra))
            {
                _missingTextures++;
                var img = ImageC.CreateNew(2, 2);
                img.Fill(new ColorC(255, 0, 255, 255));
                area.Texture = new WadTexture(img);
                area.BlendMode = BlendMode.Normal;
                area.TexCoord0 = new Vector2(0, 0); area.TexCoord1 = new Vector2(2, 0);
                area.TexCoord2 = new Vector2(2, 2); area.TexCoord3 = new Vector2(0, 2);
            }
            else
            {
                bool transparent = false;
                for (int i = 3; i < bgra.Length; i += 4)
                    if (bgra[i] == 0) { transparent = true; break; }

                // Draw mode 2 means additive blending (as used by trview), color 0x0000 is transparent.
                int draw = (_level.Game == PSXClassicLevel.GameKind.TR2 ? t.Attribute : (isTriangle ? t.TriDraw : t.QuadDraw)) & 3;
                area.Texture = new WadTexture(ImageC.FromByteArray(bgra, w, h));
                area.BlendMode = draw == 2 ? BlendMode.Additive : transparent ? BlendMode.AlphaTest : BlendMode.Normal;

                var (u0, v0, u1, v1) = PSXClassicLevel.GetRect(t, isTriangle);
                Vector2 Corner(int i) => new(t.U[i] - u0 + (t.U[i] == u1 ? 1 : 0), t.V[i] - v0 + (t.V[i] == v1 ? 1 : 0));
                area.TexCoord0 = Corner(0);
                area.TexCoord1 = Corner(1);
                area.TexCoord2 = Corner(2);
                area.TexCoord3 = isTriangle ? area.TexCoord2 : Corner(3);
            }

            _areaCache[(texIndex, isTriangle)] = area;
            return area;
        }
    }
}
