using System;
using System.Numerics;
using TombLib;
using TombLib.LevelData;
using TombLib.Utils;
using TombLib.Wad;

namespace Lara_Extractor
{
    /// <summary>
    /// Converts a PlayStation mesh (PsxMesh) to a TombLib WadMesh. Shared by the TR1-3 and TR4/5 PSX Wad2 exporters.
    ///
    /// Conventions (checked against the stored normals of the TR4 PSX meshes and against the TombEngine reference):
    ///  - Y is negated, polygon index order is kept;
    ///  - quads are stored in GPU "Z" order (A B C D) and become the outline A B D C, with their texture corners swapped the same way.
    /// </summary>
    internal static class PSXWadMeshBuilder
    {
        public static WadMesh Build(PsxMesh src, string name, Func<int, bool, TextureArea> textureFor)
        {
            var mesh = new WadMesh { Name = name, LightingType = src.PreLit ? WadMeshLightingType.VertexColors : WadMeshLightingType.Normals };

            int count = src.Vertices.Length;
            for (int i = 0; i < count; i++)
            {
                var v = src.Vertices[i];
                mesh.VertexPositions.Add(new Vector3(v.X, -v.Y, v.Z));

                if (src.PreLit)
                {
                    // Pre-lit vertices carry a 5-5-5 color (assumed scale 31 = full intensity).
                    int w = (ushort)v.W;
                    mesh.VertexColors.Add(new Vector3((w & 31) / 31.0f, ((w >> 5) & 31) / 31.0f, ((w >> 10) & 31) / 31.0f));
                }
                else
                {
                    mesh.VertexColors.Add(Vector3.One);
                }
            }

            foreach (var t in src.Triangles)
            {
                mesh.Polys.Add(new WadPolygon
                {
                    Shape = WadPolygonShape.Triangle,
                    Index0 = t.A, Index1 = t.B, Index2 = t.C,
                    Texture = textureFor(t.Tex, true)
                });
            }

            foreach (var q in src.Quads)
            {
                var area = textureFor(q.Tex, false);
                (area.TexCoord2, area.TexCoord3) = (area.TexCoord3, area.TexCoord2);
                mesh.Polys.Add(new WadPolygon
                {
                    Shape = WadPolygonShape.Quad,
                    Index0 = q.A, Index1 = q.B, Index2 = q.D, Index3 = q.C,
                    Texture = area
                });
            }

            // Normals: stored ones when available, otherwise averaged face normals (the winding matches the stored normals).
            var normals = new Vector3[count];
            if (src.Normals != null)
            {
                for (int i = 0; i < count; i++)
                {
                    var n = src.Normals[i];
                    var v = new Vector3(n.X, -n.Y, n.Z);
                    normals[i] = v.LengthSquared() > 1e-6f ? Vector3.Normalize(v) : Vector3.UnitY;
                }
            }
            else
            {
                foreach (var p in mesh.Polys)
                {
                    var p0 = mesh.VertexPositions[p.Index0];
                    var n = Vector3.Cross(mesh.VertexPositions[p.Index1] - p0, mesh.VertexPositions[p.Index2] - p0);
                    normals[p.Index0] += n; normals[p.Index1] += n; normals[p.Index2] += n;
                    if (p.Shape == WadPolygonShape.Quad) normals[p.Index3] += n;
                }
                for (int i = 0; i < count; i++)
                    normals[i] = normals[i].LengthSquared() > 1e-6f ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
            }
            foreach (var n in normals) mesh.VertexNormals.Add(n);

            mesh.BoundingSphere = new BoundingSphere(new Vector3(src.CenterX, -src.CenterY, src.CenterZ), src.Radius);
            mesh.BoundingBox = mesh.CalculateBoundingBox();
            return mesh;
        }
    }
}
