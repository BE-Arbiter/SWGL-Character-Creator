using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SWGLCharacterCreator;

public sealed class GlmSurface
{
    public string Name = "";
    public uint Flags;
    public VertexPositionNormalTexture[] Verts = Array.Empty<VertexPositionNormalTexture>();
    public short[] Indices = Array.Empty<short>();

    // Skinning data (bind-pose vertices are kept in Verts; Bone/Weight hold 4 slots per vertex).
    public int[] BoneRefs = Array.Empty<int>();
    public int[] Bone = Array.Empty<int>();
    public float[] Weight = Array.Empty<float>();

    /// <summary>Bolt-on tags (0x1) and surfaces switched off by default (0x2) are not part of the visible mesh.</summary>
    public bool Hidden => (Flags & 0x3) != 0;
}

/// <summary>
/// Ghoul2 mesh (.glm, version 6), LOD 0, vertices in the bind pose (no animation file needed).
/// Layout: header, surface hierarchy (inline, variable size), LOD table, surfaces with 32-byte
/// vertices followed by their texture coordinates.
/// </summary>
public sealed class GlmModel
{
    public List<GlmSurface> Surfaces { get; } = new();
    public Vector3 Min { get; private set; }
    public Vector3 Max { get; private set; }

    /// <summary>Path (without extension) of the .gla skeleton this mesh is skinned to.</summary>
    public string AnimName { get; private set; } = "";

    public static GlmModel Parse(byte[] b)
    {
        if (b.Length < 164 || Encoding.ASCII.GetString(b, 0, 4) != "2LGM")
            throw new InvalidDataException("Not a GLM file.");
        int version = Int(b, 4);
        if (version != 6) throw new InvalidDataException($"Unsupported GLM version {version}.");

        int numLods = Int(b, 144), ofsLods = Int(b, 148), numSurfaces = Int(b, 152), ofsHierarchy = Int(b, 156);
        if (numLods < 1) throw new InvalidDataException("GLM has no LOD.");

        var model = new GlmModel { AnimName = CStr(b, 72, 64) };
        var names = new List<(string name, uint flags)>();
        int p = ofsHierarchy;
        for (int i = 0; i < numSurfaces; i++)
        {
            names.Add((CStr(b, p, 64), BitConverter.ToUInt32(b, p + 64)));
            p += 144 + 4 * Int(b, p + 140); // childIndexes follow the fixed part
        }

        int table = ofsLods + 4; // LOD0: ofsEnd, then one offset per surface (relative to the table)
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < numSurfaces; i++)
        {
            int s = table + Int(b, table + 4 * i);
            int numVerts = Int(b, s + 12), ofsVerts = Int(b, s + 16);
            int numTris = Int(b, s + 20), ofsTris = Int(b, s + 24);

            var surf = new GlmSurface { Name = names[i].name, Flags = names[i].flags };
            surf.Verts = new VertexPositionNormalTexture[numVerts];
            surf.Bone = new int[numVerts * 4];
            surf.Weight = new float[numVerts * 4];
            int numRefs = Int(b, s + 28), ofsRefs = Int(b, s + 32);
            surf.BoneRefs = new int[numRefs];
            for (int r = 0; r < numRefs; r++) surf.BoneRefs[r] = Int(b, s + ofsRefs + 4 * r);

            int vbase = s + ofsVerts, tbase = vbase + numVerts * 32;
            for (int v = 0; v < numVerts; v++)
            {
                int o = vbase + v * 32;

                // Packed: bits 30-31 = weights-1, 5 bits per bone reference (index into BoneRefs),
                // bits 20-29 = top 2 bits of each weight whose low 8 bits are the trailing bytes (/1023).
                uint packed = BitConverter.ToUInt32(b, o + 24);
                int count = (int)(packed >> 30) + 1;
                float total = 0;
                for (int k = 0; k < count; k++)
                {
                    int refIndex = (int)((packed >> (5 * k)) & 31);
                    surf.Bone[v * 4 + k] = refIndex < numRefs ? surf.BoneRefs[refIndex] : 0;
                    float w;
                    if (k == count - 1) w = 1f - total;
                    else
                    {
                        int raw = b[o + 28 + k] | (int)(((packed >> (20 + 2 * k)) & 3) << 8);
                        w = raw / 1023f;
                        total += w;
                    }
                    surf.Weight[v * 4 + k] = w;
                }

                var normal = new Vector3(Float(b, o), Float(b, o + 4), Float(b, o + 8));
                var pos = new Vector3(Float(b, o + 12), Float(b, o + 16), Float(b, o + 20));
                var uv = new Vector2(Float(b, tbase + v * 8), Float(b, tbase + v * 8 + 4));
                surf.Verts[v] = new VertexPositionNormalTexture(pos, normal, uv);
                if (!surf.Hidden) { min = Vector3.Min(min, pos); max = Vector3.Max(max, pos); }
            }

            surf.Indices = new short[numTris * 3];
            for (int t = 0; t < numTris * 3; t++) surf.Indices[t] = (short)(ushort)Int(b, s + ofsTris + 4 * t);
            model.Surfaces.Add(surf);
        }

        if (min.X > max.X) { min = -Vector3.One; max = Vector3.One; }
        model.Min = min;
        model.Max = max;
        return model;
    }

    private static int Int(byte[] b, int o) => BitConverter.ToInt32(b, o);
    private static float Float(byte[] b, int o) => BitConverter.ToSingle(b, o);

    private static string CStr(byte[] b, int o, int len)
    {
        int end = Array.IndexOf(b, (byte)0, o, len);
        return Encoding.ASCII.GetString(b, o, (end < 0 ? o + len : end) - o);
    }
}

/// <summary>A .skin file: "surface,shader" lines. Shaders "off" / "*off" hide the surface.</summary>
public static class SkinFile
{
    public static Dictionary<string, string> Parse(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            int comma = line.IndexOf(',');
            if (comma <= 0) continue;
            map[line[..comma].Trim()] = line[(comma + 1)..].Trim();
        }
        return map;
    }
}
