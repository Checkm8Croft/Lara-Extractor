using System;
using System.Collections.Generic;

namespace Lara_Extractor
{
    /// <summary>One entry of the object table (struct object_info, 64 bytes). Negative NumMeshes = sprite sequence.</summary>
    internal readonly record struct PsxObject(int Id, int NumMeshes, int MeshIndex, int BoneIndex, int AnimIndex);

    /// <summary>One entry of the static table (struct static_info, 28 bytes). Boxes are X1 X2 Y1 Y2 Z1 Z2.</summary>
    internal readonly record struct PsxStatic(int Id, int MeshNumber, short Flags, short[] Visibility, short[] Collision);

    /// <summary>One entry of psxspriteinfo (16 bytes).</summary>
    internal readonly record struct PsxSprite(ushort Clut, ushort Tpage, int U1, int V1, int U2, int V2);

    internal sealed class PsxMesh
    {
        public short CenterX, CenterY, CenterZ, Radius;
        public int Flags;
        public bool PreLit;                                   // flag 0x80: 4th short of each vertex is a 5-5-5 color
        public (short X, short Y, short Z, short W)[] Vertices = Array.Empty<(short, short, short, short)>();
        public (short X, short Y, short Z)[]? Normals;        // 4096 = 1.0, only when not pre-lit
        public List<(int A, int B, int C, int Tex)> Triangles = new();
        /// <summary>Quads store their vertices in GPU "Z" order (A B C D): the outline is A B D C.</summary>
        public List<(int A, int B, int C, int D, int Tex)> Quads = new();
    }

    internal sealed class PsxAnim
    {
        public uint FrameOffset;
        public int FrameSizeWords, Rate;
        public int State, Velocity, Acceleration, XVelocity, XAcceleration;
        public int FrameStart, FrameEnd, JumpAnim, JumpFrame;
        public int NumChanges, ChangeIndex, NumCommands, CommandIndex;
    }

    /// <summary>
    /// Reads the model-related tables of a PSXLevel: objects, statics, meshes, animations (layouts from TOMB5's
    /// GAME/SETUP.C, SPEC_PSX/STYPES.H and SPEC_PSXPC_N/DRAWOBJ.C, checked on a TR4 GAMEWAD).
    /// </summary>
    internal static class PSXModelReader
    {
        public static List<PsxObject> ReadObjects(PSXLevel level)
        {
            var list = new List<PsxObject>();
            byte[] d = level.Data;
            for (int i = 0; i < PSXLevel.NumObjects; i++)
            {
                int p = level.ObjectTableOffset + i * PSXLevel.ObjectInfoSize;
                if (p + PSXLevel.ObjectInfoSize > d.Length) break;
                short nm = BitConverter.ToInt16(d, p);
                if (nm == 0) continue;
                list.Add(new PsxObject(i, nm, BitConverter.ToInt16(d, p + 2), BitConverter.ToInt32(d, p + 4), BitConverter.ToInt16(d, p + 38)));
            }
            return list;
        }

        public static List<PsxStatic> ReadStatics(PSXLevel level)
        {
            var list = new List<PsxStatic>();
            byte[] d = level.Data;
            int start = level.ObjectTableOffset + PSXLevel.NumObjects * PSXLevel.ObjectInfoSize;
            for (int i = 0; i < PSXLevel.NumStaticObjects; i++)
            {
                int p = start + i * PSXLevel.StaticInfoSize;
                if (p + PSXLevel.StaticInfoSize > d.Length) break;
                short meshNumber = BitConverter.ToInt16(d, p);
                if (meshNumber == 0) continue;

                var vis = new short[6];
                var col = new short[6];
                for (int k = 0; k < 6; k++)
                {
                    vis[k] = BitConverter.ToInt16(d, p + 4 + k * 2);
                    col[k] = BitConverter.ToInt16(d, p + 16 + k * 2);
                }
                list.Add(new PsxStatic(i, meshNumber, BitConverter.ToInt16(d, p + 2), vis, col));
            }
            return list;
        }

        public static PsxSprite[] ReadSprites(PSXLevel level)
        {
            var result = new PsxSprite[level.SpriteInfoCount];
            byte[] d = level.Data;
            for (int i = 0; i < result.Length; i++)
            {
                int p = level.SpriteInfoOffset + i * 16;
                // x1 y1 x2 y2 (screen offsets) | clut | tpage | u1 v1 u2 v2
                result[i] = new PsxSprite(BitConverter.ToUInt16(d, p + 8), BitConverter.ToUInt16(d, p + 10), d[p + 12], d[p + 13], d[p + 14], d[p + 15]);
            }
            return result;
        }

        /// <summary>
        /// Reads the mesh at the given pointer-table index. Returns null if the pointer is 0 (the mesh is not in the
        /// level file: Lara and weapon meshes are resident) or if the data fails validation.
        /// </summary>
        public static PsxMesh? ReadMesh(PSXLevel level, int pointerIndex)
        {
            if (pointerIndex < 0 || pointerIndex >= level.MeshPointerCount) return null;

            byte[] d = level.Data;
            uint ptr = BitConverter.ToUInt32(d, level.MeshPointerOffset + pointerIndex * 4);
            if (ptr == 0 || ptr >= level.MeshBaseLength) return null;

            try
            {
                int m = level.MeshBaseOffset + (int)ptr;
                int limit = level.MeshBaseOffset + level.MeshBaseLength;
                if (m + 12 > limit) return null;

                var mesh = new PsxMesh
                {
                    CenterX = BitConverter.ToInt16(d, m),
                    CenterY = BitConverter.ToInt16(d, m + 2),
                    CenterZ = BitConverter.ToInt16(d, m + 4),
                    Radius = BitConverter.ToInt16(d, m + 6)
                };

                uint header = BitConverter.ToUInt32(d, m + 8);
                int vertexCount = (int)(header & 0xFF);
                mesh.Flags = (int)((header >> 8) & 0xFF);
                mesh.PreLit = (mesh.Flags & 0x80) != 0;
                int polyOffset = (int)(header >> 16);

                // Some meshes have 0 in the vertex-count byte: the count is then implied by the polygon offset
                // (vertices take 8 bytes when pre-lit, 16 bytes with normals). Verified on 468/468 such meshes of a TR4 GAMEWAD.
                if (vertexCount == 0)
                    vertexCount = mesh.PreLit ? polyOffset / 8 : polyOffset / 16;
                if (vertexCount == 0) return null;

                int vertexStart = m + 12;
                if (vertexStart + vertexCount * 8 > limit) return null;

                mesh.Vertices = new (short, short, short, short)[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    int p = vertexStart + i * 8;
                    mesh.Vertices[i] = (BitConverter.ToInt16(d, p), BitConverter.ToInt16(d, p + 2), BitConverter.ToInt16(d, p + 4), BitConverter.ToInt16(d, p + 6));
                }

                if (!mesh.PreLit && vertexStart + vertexCount * 16 <= limit)
                {
                    mesh.Normals = new (short, short, short)[vertexCount];
                    for (int i = 0; i < vertexCount; i++)
                    {
                        int p = vertexStart + vertexCount * 8 + i * 8;
                        mesh.Normals[i] = (BitConverter.ToInt16(d, p), BitConverter.ToInt16(d, p + 2), BitConverter.ToInt16(d, p + 4));
                    }
                }

                int ps = vertexStart + polyOffset;
                if (ps + 4 > limit) return null;
                int triCount = BitConverter.ToUInt16(d, ps);
                int quadCount = BitConverter.ToUInt16(d, ps + 2);
                int q = ps + 4;
                int textures = level.ObjectTextures.Count;

                // Triangles: a word with 4 texture low bytes, then up to 4 words of vertex indices (3 bytes) + texture high nibble.
                for (int i = 0; i < triCount;)
                {
                    if (q + 4 > limit) return null;
                    uint texWord = BitConverter.ToUInt32(d, q); q += 4;
                    for (int k = 0; k < 4 && i < triCount; k++, i++)
                    {
                        if (q + 4 > limit) return null;
                        uint vw = BitConverter.ToUInt32(d, q); q += 4;
                        int tex = (int)(((texWord >> (8 * k)) & 0xFF) | (((vw >> 24) & 0xF) << 8));
                        int a = (int)(vw & 0xFF), b = (int)((vw >> 8) & 0xFF), c = (int)((vw >> 16) & 0xFF);
                        if (a >= vertexCount || b >= vertexCount || c >= vertexCount || tex >= textures) return null;
                        mesh.Triangles.Add((a, b, c, tex));
                    }
                }

                // Quads: a word with 2 x 16 bits (12-bit texture + 4 flag bits), then up to 2 words of 4 vertex indices.
                for (int i = 0; i < quadCount;)
                {
                    if (q + 4 > limit) return null;
                    uint texWord = BitConverter.ToUInt32(d, q); q += 4;
                    for (int k = 0; k < 2 && i < quadCount; k++, i++)
                    {
                        if (q + 4 > limit) return null;
                        uint vw = BitConverter.ToUInt32(d, q); q += 4;
                        int tex = (int)((texWord >> (16 * k)) & 0xFFF);
                        int a = (int)(vw & 0xFF), b = (int)((vw >> 8) & 0xFF), c = (int)((vw >> 16) & 0xFF), dd = (int)(vw >> 24);
                        if (a >= vertexCount || b >= vertexCount || c >= vertexCount || dd >= vertexCount || tex >= textures) return null;
                        mesh.Quads.Add((a, b, c, dd, tex));
                    }
                }

                return mesh;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        public static PsxAnim? ReadAnim(PSXLevel level, int index)
        {
            if (index < 0 || index >= level.AnimCount) return null;
            byte[] d = level.Data;
            int p = level.AnimOffset + index * 40;
            int interpolation = BitConverter.ToUInt16(d, p + 4);

            return new PsxAnim
            {
                FrameOffset = BitConverter.ToUInt32(d, p),
                FrameSizeWords = interpolation >> 8,
                Rate = interpolation & 0xFF,
                State = BitConverter.ToInt16(d, p + 6),
                Velocity = BitConverter.ToInt32(d, p + 8),
                Acceleration = BitConverter.ToInt32(d, p + 12),
                XVelocity = BitConverter.ToInt32(d, p + 16),
                XAcceleration = BitConverter.ToInt32(d, p + 20),
                FrameStart = BitConverter.ToInt16(d, p + 24),
                FrameEnd = BitConverter.ToInt16(d, p + 26),
                JumpAnim = BitConverter.ToInt16(d, p + 28),
                JumpFrame = BitConverter.ToInt16(d, p + 30),
                NumChanges = BitConverter.ToInt16(d, p + 32),
                ChangeIndex = BitConverter.ToInt16(d, p + 34),
                NumCommands = BitConverter.ToInt16(d, p + 36),
                CommandIndex = BitConverter.ToUInt16(d, p + 38)
            };
        }

        /// <summary>Reads <paramref name="words"/> 16-bit words of frame data at a byte offset, or null if out of range.</summary>
        public static short[]? ReadFrameWords(PSXLevel level, long byteOffset, int words)
        {
            if (byteOffset < 0 || words <= 0 || byteOffset + words * 2L > level.FrameDataLength) return null;
            var result = new short[words];
            Buffer.BlockCopy(level.Data, level.FrameDataOffset + (int)byteOffset, result, 0, words * 2);
            return result;
        }

        public static int ReadBoneInt(PSXLevel level, int intIndex)
            => (intIndex < 0 || intIndex >= level.BoneIntCount) ? 0 : BitConverter.ToInt32(level.Data, level.BoneOffset + intIndex * 4);

        public static short ReadCommandWord(PSXLevel level, int index)
            => (index < 0 || index >= level.CommandCount) ? (short)0 : BitConverter.ToInt16(level.Data, level.CommandOffset + index * 2);

        /// <summary>State change record: state id, number of dispatches, index of the first dispatch.</summary>
        public static (int State, int Count, int First) ReadChange(PSXLevel level, int index)
        {
            if (index < 0 || index >= level.ChangeCount) return (0, 0, 0);
            int p = level.ChangeOffset + index * 6;
            return (BitConverter.ToInt16(level.Data, p), BitConverter.ToInt16(level.Data, p + 2), BitConverter.ToInt16(level.Data, p + 4));
        }

        /// <summary>Dispatch record: low frame, high frame, next anim, next frame.</summary>
        public static (int Low, int High, int NextAnim, int NextFrame) ReadDispatch(PSXLevel level, int index)
        {
            if (index < 0 || index >= level.DispatchCount) return (0, 0, 0, 0);
            int p = level.DispatchOffset + index * 8;
            byte[] d = level.Data;
            return (BitConverter.ToInt16(d, p), BitConverter.ToInt16(d, p + 2), BitConverter.ToInt16(d, p + 4), BitConverter.ToInt16(d, p + 6));
        }
    }
}
