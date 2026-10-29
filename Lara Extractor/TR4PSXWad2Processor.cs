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
    /// Wad2 export for PlayStation TR4 levels stored in GAMEWAD.OBJ (one Wad2 per level).
    ///
    /// Formats (TOMB5 GAME/SETUP.C + SPEC_PSXPC_N/DRAWOBJ.C, validated on a TR4 GAMEWAD):
    ///  - Meshes: 8-byte vertices, optional normals, triangles/quads packed with 8-bit vertex indices and a texture index.
    ///    Quads are in GPU "Z" order and are re-ordered to an outline (A B D C).
    ///  - Textures: PSXTEXTSTRUCT (UVs inside a 4/8/15-bit tpage + CLUT), rendered from the level VRAM.
    ///  - Animations: same tables as TR4 PC (anims, state changes, dispatches, commands) plus fixed-size frames made of
    ///    a 9-word header and "super-packed" rotations.
    ///
    /// Not available in the level file: Lara, her weapons and other resident meshes (their mesh pointers are 0).
    /// Moveables with no mesh at all are skipped; the others get empty meshes for the missing bones.
    /// </summary>
    public sealed class TR4PSXWad2Processor
    {
        private readonly string _inputPath;
        private readonly string _outputWad2Path;
        private readonly Action<string> _logger;

        private PSXLevel _level = null!;
        private readonly Dictionary<(int Tex, bool Tri), (TextureArea Area, bool Ok)> _areaCache = new();
        private int _missingTextures;

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

            string dir = Path.GetDirectoryName(_outputWad2Path) ?? ".";
            string baseName = Path.GetFileNameWithoutExtension(_outputWad2Path);
            Directory.CreateDirectory(dir);

            int saved = 0;
            foreach (var entry in gamewad.Entries)
            {
                if (entry.Size < 0x10000) continue;
                var level = PSXLevel.TryParse(gamewad.Read(entry), out _);
                if (level == null) continue;

                try
                {
                    _level = level;
                    _areaCache.Clear();
                    _missingTextures = 0;

                    Wad2 wad = BuildWad2(entry.Index);
                    string path = Path.Combine(dir, $"{baseName}_Level_{entry.Index:D2}.wad2");
                    Wad2Writer.SaveToFile(wad, path);
                    saved++;
                    _logger($"[PSX Wad2] Level {entry.Index:D2}: saved {Path.GetFileName(path)} ({wad.Moveables.Count} moveables, {wad.Statics.Count} statics, {wad.SpriteSequences.Count} sprite sequences).");
                }
                catch (Exception ex)
                {
                    _logger($"[PSX Wad2 Error] Level {entry.Index:D2}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            _logger($"[PSX Wad2] ========== {saved} WAD2 FILE(S) SAVED IN: {dir} ==========");
        }

        // ---------------------------------------------------------------- Wad2

        private Wad2 BuildWad2(int levelIndex)
        {
            var wad = new Wad2 { GameVersion = TRVersion.Game.TR4 };
            var objects = PSXModelReader.ReadObjects(_level);

            // Animations of an object run from its anim index to the next object's anim index.
            var animStarts = objects.Where(o => o.NumMeshes > 0 && (o.AnimIndex > 0 || o.Id == 0))
                                    .Select(o => o.AnimIndex).Distinct().OrderBy(x => x).ToList();

            int moveables = 0, skipped = 0, failed = 0, partial = 0;
            foreach (var obj in objects.Where(o => o.NumMeshes > 0))
            {
                try
                {
                    var mov = BuildMoveable(obj, animStarts, out int missingMeshes);
                    if (mov == null) { skipped++; continue; }
                    if (missingMeshes > 0) partial++;
                    wad.Moveables[new WadMoveableId((uint)obj.Id)] = mov;
                    moveables++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger($"[PSX Wad2] Level {levelIndex:D2} moveable {obj.Id}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            int statics = 0, staticsSkipped = 0;
            foreach (var st in PSXModelReader.ReadStatics(_level))
            {
                var mesh = PSXModelReader.ReadMesh(_level, st.MeshNumber);
                if (mesh == null) { staticsSkipped++; continue; }

                var stat = new WadStatic(new WadStaticId((uint)st.Id))
                {
                    Mesh = BuildMesh(mesh, $"static_{st.Id}"),
                    Flags = st.Flags
                };
                stat.VisibilityBox = new BoundingBox(
                    new Vector3(st.Visibility[0], -st.Visibility[3], st.Visibility[4]),
                    new Vector3(st.Visibility[1], -st.Visibility[2], st.Visibility[5]));
                stat.CollisionBox = new BoundingBox(
                    new Vector3(st.Collision[0], -st.Collision[3], st.Collision[4]),
                    new Vector3(st.Collision[1], -st.Collision[2], st.Collision[5]));
                wad.Statics[new WadStaticId((uint)st.Id)] = stat;
                statics++;
            }

            int sprites = 0;
            var spriteTable = PSXModelReader.ReadSprites(_level);
            foreach (var obj in objects.Where(o => o.NumMeshes < 0))
            {
                var seq = BuildSpriteSequence(obj, spriteTable);
                if (seq.Sprites.Count == 0) continue;
                wad.SpriteSequences[new WadSpriteSequenceId((uint)obj.Id)] = seq;
                sprites++;
            }

            _logger($"[PSX Wad2] Level {levelIndex:D2}: moveables {moveables} ({partial} with missing meshes, {skipped} skipped: meshes not in the level file, {failed} failed), statics {statics} ({staticsSkipped} skipped), sprite sequences {sprites}.");
            if (_missingTextures > 0)
                _logger($"[PSX Wad2 Warning] Level {levelIndex:D2}: {_missingTextures} polygon(s) used a texture that could not be rendered.");
            return wad;
        }

        // ---------------------------------------------------------------- moveables

        private WadMoveable? BuildMoveable(PsxObject obj, List<int> animStarts, out int missingMeshes)
        {
            missingMeshes = 0;
            var meshes = new PsxMesh?[obj.NumMeshes];
            for (int i = 0; i < obj.NumMeshes; i++)
            {
                meshes[i] = PSXModelReader.ReadMesh(_level, obj.MeshIndex + i * 2);   // the pointer table holds 2 entries per mesh
                if (meshes[i] == null) missingMeshes++;
            }

            if (missingMeshes == obj.NumMeshes)
                return null;

            var mov = new WadMoveable(new WadMoveableId((uint)obj.Id));
            for (int i = 0; i < obj.NumMeshes; i++)
            {
                var bone = new WadBone
                {
                    Name = $"bone_{i}",
                    Mesh = meshes[i] != null ? BuildMesh(meshes[i]!, $"moveable_{obj.Id}_mesh_{i}") : new WadMesh { Name = "mesh_missing" }
                };

                if (i == 0)
                {
                    bone.Translation = Vector3.Zero;
                    bone.OpCode = WadLinkOpcode.NotUseStack;
                }
                else
                {
                    int b = obj.BoneIndex + (i - 1) * 4;
                    int flags = PSXModelReader.ReadBoneInt(_level, b);
                    bone.Translation = new Vector3(PSXModelReader.ReadBoneInt(_level, b + 1), -PSXModelReader.ReadBoneInt(_level, b + 2), PSXModelReader.ReadBoneInt(_level, b + 3));
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

            if (obj.AnimIndex > 0 || obj.Id == 0)
            {
                int first = obj.AnimIndex;
                int end = animStarts.FirstOrDefault(s => s > first);
                if (end == 0) end = _level.AnimCount;
                for (int a = first; a < end && a < _level.AnimCount; a++)
                {
                    var anim = BuildAnimation(a, first, end - first, obj.NumMeshes);
                    if (anim != null) mov.Animations.Add(anim);
                }
            }

            return mov;
        }

        private WadAnimation? BuildAnimation(int index, int firstAnim, int animCount, int numBones)
        {
            var src = PSXModelReader.ReadAnim(_level, index);
            if (src == null) return null;

            int rate = Math.Max(1, src.Rate);
            int length = Math.Max(0, src.FrameEnd - src.FrameStart);

            var anim = new WadAnimation
            {
                Name = $"Animation {index - firstAnim}",
                StateId = (ushort)src.State,
                FrameRate = (byte)rate,
                EndFrame = (ushort)length,
                NextAnimation = (ushort)Math.Clamp(src.JumpAnim - firstAnim, 0, Math.Max(0, animCount - 1)),
                // TR4 velocities are 16.16 fixed point and change by the acceleration every frame.
                StartVelocity = src.Velocity / 65536.0f,
                EndVelocity = (src.Velocity + (long)src.Acceleration * length) / 65536.0f,
                StartLateralVelocity = src.XVelocity / 65536.0f,
                EndLateralVelocity = (src.XVelocity + (long)src.XAcceleration * length) / 65536.0f
            };

            var target = PSXModelReader.ReadAnim(_level, src.JumpAnim);
            anim.NextFrame = (ushort)Math.Max(0, src.JumpFrame - (target?.FrameStart ?? 0));

            // Stored key frames: one every <rate> frames, plus one more to interpolate up to the last frame.
            int keyFrames = length / rate + 1 + (length % rate != 0 ? 1 : 0);
            int frameWords = src.FrameSizeWords;
            if (frameWords >= 9)
            {
                for (int f = 0; f < keyFrames; f++)
                {
                    // Read the frame plus a few extra words: bones with 2-word rotations can run past a too-small frame size.
                    long offset = src.FrameOffset + (long)f * frameWords * 2;
                    int want = frameWords + numBones * 2;
                    short[]? words = null;
                    while (want >= frameWords && (words = PSXModelReader.ReadFrameWords(_level, offset, want)) == null)
                        want -= 2;
                    if (words == null) break;
                    anim.KeyFrames.Add(BuildKeyFrame(words, numBones));
                }
            }
            if (anim.KeyFrames.Count == 0)
                anim.KeyFrames.Add(new WadKeyFrame());

            // State changes and dispatches (TR4 PC layout).
            for (int c = 0; c < src.NumChanges; c++)
            {
                var (state, count, firstDispatch) = PSXModelReader.ReadChange(_level, src.ChangeIndex + c);
                var change = new WadStateChange { StateId = (ushort)state };
                for (int d = 0; d < count; d++)
                {
                    var (low, high, nextAnim, nextFrame) = PSXModelReader.ReadDispatch(_level, firstDispatch + d);
                    var next = PSXModelReader.ReadAnim(_level, nextAnim);
                    change.Dispatches.Add(new WadAnimDispatch
                    {
                        InFrame = (ushort)Math.Max(0, low - src.FrameStart),
                        OutFrame = (ushort)Math.Max(0, high - src.FrameStart),
                        NextAnimation = (ushort)Math.Clamp(nextAnim - firstAnim, 0, Math.Max(0, animCount - 1)),
                        // TR4 dispatches have a single target frame: use it for both ends of the range.
                        NextLowFrame = (ushort)Math.Max(0, nextFrame - (next?.FrameStart ?? 0)),
                        NextHighFrame = (ushort)Math.Max(0, nextFrame - (next?.FrameStart ?? 0))
                    });
                }
                anim.StateChanges.Add(change);
            }

            // Animation commands.
            int ptr = src.CommandIndex;
            for (int c = 0; c < src.NumCommands && ptr < _level.CommandCount; c++)
            {
                short opcode = PSXModelReader.ReadCommandWord(_level, ptr++);
                var cmd = new WadAnimCommand { Type = (WadAnimCommandType)opcode };
                switch (opcode)
                {
                    case 1:
                        cmd.Parameter1 = PSXModelReader.ReadCommandWord(_level, ptr++);
                        cmd.Parameter2 = PSXModelReader.ReadCommandWord(_level, ptr++);
                        cmd.Parameter3 = PSXModelReader.ReadCommandWord(_level, ptr++);
                        break;
                    case 2:
                        cmd.Parameter1 = PSXModelReader.ReadCommandWord(_level, ptr++);
                        cmd.Parameter2 = PSXModelReader.ReadCommandWord(_level, ptr++);
                        break;
                    case 5:
                    case 6:
                        cmd.Parameter1 = PSXModelReader.ReadCommandWord(_level, ptr++);
                        cmd.Parameter2 = PSXModelReader.ReadCommandWord(_level, ptr++);
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
                BoundingBox = new BoundingBox(
                    new Vector3(words[0], -words[3], words[4]),
                    new Vector3(words[1], -words[2], words[5])),
                Offset = new Vector3(words[6], -words[7], words[8])
            };

            var data = new List<short>(words.Length - 9);
            for (int i = 9; i < words.Length; i++) data.Add(words[i]);
            while (data.Count < numBones * 2) data.Add(0);

            int index = 0;
            for (int b = 0; b < numBones; b++)
            {
                int before = index;
                kf.Angles.Add(WadKeyFrameRotation.FromTrAngle(ref index, data, false, true));
                if (index <= before) index = before + 1;
            }

            return kf;
        }

        // ---------------------------------------------------------------- meshes

        private WadMesh BuildMesh(PsxMesh src, string name)
            => PSXWadMeshBuilder.Build(src, name, (tex, tri) => GetTextureArea(tex, tri).Area);

        // ---------------------------------------------------------------- textures

        private (TextureArea Area, bool Ok) GetTextureArea(int texIndex, bool isTriangle)
        {
            if (_areaCache.TryGetValue((texIndex, isTriangle), out var cached))
                return cached;

            var t = _level.ObjectTextures[texIndex];
            var area = new TextureArea { DoubleSided = false };
            bool ok = _level.TryRender(t, out int w, out int h, out byte[] bgra, isTriangle);

            if (!ok)
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

                area.Texture = new WadTexture(ImageC.FromByteArray(bgra, w, h));
                area.BlendMode = transparent ? BlendMode.AlphaTest : BlendMode.Normal;   // PSX color 0x0000 is transparent

                var (u0, v0, u1, v1) = PSXLevel.GetRect(t, isTriangle);
                // Corner coordinates are pixel edges: the far corners sit one pixel further.
                Vector2 Corner(int i) => new(t.U[i] - u0 + (t.U[i] == u1 ? 1 : 0), t.V[i] - v0 + (t.V[i] == v1 ? 1 : 0));
                area.TexCoord0 = Corner(0);
                area.TexCoord1 = Corner(1);
                area.TexCoord2 = Corner(2);
                area.TexCoord3 = isTriangle ? area.TexCoord2 : Corner(3);
            }

            var result = (area, ok);
            _areaCache[(texIndex, isTriangle)] = result;
            return result;
        }

        // ---------------------------------------------------------------- sprites

        private WadSpriteSequence BuildSpriteSequence(PsxObject obj, PsxSprite[] table)
        {
            var seq = new WadSpriteSequence(new WadSpriteSequenceId((uint)obj.Id));
            int count = -obj.NumMeshes;

            for (int i = 0; i < count; i++)
            {
                int idx = obj.MeshIndex + i;
                if (idx < 0 || idx >= table.Length) continue;
                var s = table[idx];

                var tex = new PSXLevel.Texture(new[] { (byte)s.U1, (byte)s.U2, (byte)s.U1, (byte)s.U2 }, new[] { (byte)s.V1, (byte)s.V1, (byte)s.V2, (byte)s.V2 }, s.Clut, s.Tpage);
                if (!_level.TryRender(tex, out int w, out int h, out byte[] bgra, false))
                    continue;

                seq.Sprites.Add(new WadSprite { Texture = new WadTexture(ImageC.FromByteArray(bgra, w, h)) });
            }

            return seq;
        }
    }
}
