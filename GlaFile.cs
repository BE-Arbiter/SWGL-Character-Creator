using System.Numerics;
using System.Text;

namespace SWGLCharacterCreator;

public sealed class GlaBone
{
    public string Name = "";
    public int Parent = -1;

    /// <summary>The face bone and the bones below it (eyes, brows, lips, jaw) are driven by their own animation.</summary>
    public bool IsFace;
}

/// <summary>A possibly fractional frame of an animation made of <see cref="Count"/> frames starting at <see cref="First"/>.</summary>
public readonly record struct FrameSource(float Frame, int Count, int First);

/// <summary>
/// Ghoul2 animation/skeleton file (.gla, version 6): bone hierarchy and, per frame, one compressed
/// transform per bone (14 bytes in a shared pool, referenced by 3-byte indexes).
/// Each transform is a delta from the bind pose, so the skinning matrix of a bone is its own delta
/// composed with its parent's, applied directly to the bind-pose vertices of the .glm.
/// </summary>
public sealed class GlaFile
{
    private readonly byte[] _data;
    private readonly int _ofsFrames, _ofsPool;

    public int NumFrames { get; }
    public List<GlaBone> Bones { get; } = new();

    // Bone indexes with every parent before its children (a parent may have a higher index, e.g. "face").
    private int[] _order = Array.Empty<int>();

    private GlaFile(byte[] data)
    {
        _data = data;
        if (data.Length < 100 || Encoding.ASCII.GetString(data, 0, 4) != "2LGA")
            throw new InvalidDataException("Not a GLA file.");
        if (Int(4) != 6) throw new InvalidDataException("Unsupported GLA version.");

        NumFrames = Int(76);
        _ofsFrames = Int(80);
        int numBones = Int(84);
        _ofsPool = Int(88);

        // Bone table: offsets (relative to the table) right after the 100-byte header; entries are variable size.
        const int table = 100;
        for (int i = 0; i < numBones; i++)
        {
            int p = table + Int(table + 4 * i);
            Bones.Add(new GlaBone { Name = CStr(p, 64), Parent = Int(p + 68) });
        }
        _order = ParentFirstOrder();
        foreach (var b in Bones)
            for (var a = b; a != null; a = a.Parent >= 0 ? Bones[a.Parent] : null)
                if (a.Name.Equals("face", StringComparison.OrdinalIgnoreCase)) { b.IsFace = true; break; }
    }

    private int[] ParentFirstOrder()
    {
        var order = new List<int>(Bones.Count);
        var done = new bool[Bones.Count];
        void Visit(int i)
        {
            if (done[i]) return;
            done[i] = true; // also guards against cycles in malformed files
            int p = Bones[i].Parent;
            if (p >= 0 && p < Bones.Count) Visit(p);
            order.Add(i);
        }
        for (int i = 0; i < Bones.Count; i++) Visit(i);
        return order.ToArray();
    }

    public static GlaFile Parse(byte[] data) => new(data);

    private int Int(int o) => BitConverter.ToInt32(_data, o);

    private string CStr(int o, int len)
    {
        int end = Array.IndexOf(_data, (byte)0, o, len);
        return Encoding.ASCII.GetString(_data, o, (end < 0 ? o + len : end) - o);
    }

    /// <summary>Rotation and translation delta of a bone at a frame.</summary>
    public void GetLocal(int frame, int bone, out Quaternion q, out Vector3 t)
    {
        frame = Math.Clamp(frame, 0, NumFrames - 1);
        int idxPos = _ofsFrames + (frame * Bones.Count + bone) * 3;
        int index = _data[idxPos] | (_data[idxPos + 1] << 8) | (_data[idxPos + 2] << 16);
        int p = _ofsPool + index * 14;
        float U(int i) => BitConverter.ToUInt16(_data, p + 2 * i);
        q = Quaternion.Normalize(new Quaternion(U(1) / 16383f - 2f, U(2) / 16383f - 2f, U(3) / 16383f - 2f, U(0) / 16383f - 2f));
        t = new Vector3(U(4) / 64f - 512f, U(5) / 64f - 512f, U(6) / 64f - 512f);
    }

    /// <summary>
    /// Skinning matrices (bind space to posed model space, row-vector convention).
    /// Face bones follow <paramref name="face"/> (as in game, where facial animations run separately from
    /// body animations); every other bone follows <paramref name="body"/>.
    /// </summary>
    public Matrix4x4[] Pose(FrameSource body, FrameSource face)
    {
        var skin = new Matrix4x4[Bones.Count];
        foreach (int i in _order)
        {
            var src = Bones[i].IsFace ? face : body;
            int count = Math.Max(src.Count, 1);
            int whole = (int)MathF.Floor(src.Frame);
            int f0 = src.First + whole % count;
            int f1 = src.First + (whole + 1) % count;
            float k = src.Frame - whole;

            GetLocal(f0, i, out var qa, out var ta);
            GetLocal(f1, i, out var qb, out var tb);
            if (Quaternion.Dot(qa, qb) < 0) qb = Quaternion.Negate(qb);
            var q = Quaternion.Normalize(Quaternion.Lerp(qa, qb, k));
            var t = Vector3.Lerp(ta, tb, k);

            var local = RotationFromQuat(q);
            local.M41 = t.X; local.M42 = t.Y; local.M43 = t.Z;
            int parent = Bones[i].Parent;
            // Row-vector order: this bone's delta first, then its parent's chain.
            skin[i] = parent < 0 ? local : local * skin[parent];
        }
        return skin;
    }

    /// <summary>Standard column-vector rotation matrix of the quaternion, stored row-vector style (transposed).</summary>
    private static Matrix4x4 RotationFromQuat(Quaternion q)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        float r00 = 1 - 2 * (y * y + z * z), r01 = 2 * (x * y - w * z), r02 = 2 * (x * z + w * y);
        float r10 = 2 * (x * y + w * z), r11 = 1 - 2 * (x * x + z * z), r12 = 2 * (y * z - w * x);
        float r20 = 2 * (x * z - w * y), r21 = 2 * (y * z + w * x), r22 = 1 - 2 * (x * x + y * y);
        return new Matrix4x4(
            r00, r10, r20, 0,
            r01, r11, r21, 0,
            r02, r12, r22, 0,
            0, 0, 0, 1);
    }
}

public sealed record AnimDef(string Name, int Start, int Length, int Loop, float Fps);

/// <summary>animation.cfg: "name start length loop fps" lines; other lines (sex, soundpath...) are ignored.</summary>
public static class AnimationCfg
{
    public static List<AnimDef> Parse(string text)
    {
        var list = new List<AnimDef>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 5) continue;
            if (!int.TryParse(t[1], out var start) || !int.TryParse(t[2], out var len) ||
                !int.TryParse(t[3], out var loop) ||
                !float.TryParse(t[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fps))
                continue;
            list.Add(new AnimDef(t[0], start, Math.Max(len, 1), loop, fps));
        }
        return list;
    }
}
