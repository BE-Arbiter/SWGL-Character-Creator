using Microsoft.Xna.Framework.Graphics;
using StbImageSharp;
using StbImageWriteSharp;

namespace SWGLCharacterCreator;

public sealed record Img(IntPtr Id, int Width, int Height, string Source, Texture2D Texture);

/// <summary>
/// Loads png/tga/jpg assets (loose files under SWGL/ win over pk3 entries) as ImGui textures.
/// Textures dropped by <see cref="Invalidate"/> are disposed on the next <see cref="Flush"/>
/// so that a texture is never destroyed while the current frame still references it.
/// </summary>
public sealed class ImageStore
{
    private static readonly string[] Extensions = { ".tga", ".png", ".jpg" };

    private readonly GraphicsDevice _gd;
    private readonly ImGuiRenderer _ui;
    private readonly Func<Pk3Index?> _index;
    private readonly Func<string?> _gameDir;
    private readonly Dictionary<string, Img?> _cache = new();

    /// <summary>Incremented whenever cached textures are dropped; holders of textures must reload.</summary>
    public int Generation { get; private set; }
    private readonly List<(Img img, Texture2D tex)> _live = new();
    private readonly List<(Img img, Texture2D tex)> _dead = new();

    public ImageStore(GraphicsDevice gd, ImGuiRenderer ui, Func<Pk3Index?> index, Func<string?> gameDir)
    {
        _gd = gd; _ui = ui; _index = index; _gameDir = gameDir;
    }

    public static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimStart('/');

    /// <summary>Image for a game path, or null if the path is empty, unknown or not a decodable image.</summary>
    public Img? Get(string path)
    {
        var key = Normalize(path).ToLowerInvariant();
        if (key.Length == 0) return null;
        if (_cache.TryGetValue(key, out var hit)) return hit;
        return _cache[key] = Load(Normalize(path));
    }

    private Img? Load(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        // Like the engine: the exact path first, then the same name with another image extension.
        var stem = Extensions.Contains(ext) ? path[..^ext.Length] : path;
        var candidates = (Extensions.Contains(ext) ? new[] { path } : Array.Empty<string>())
            .Concat(Extensions.Select(e => stem + e)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var c in candidates)
        {
            var bytes = ReadBytes(c, out var source);
            if (bytes == null) continue;
            try
            {
                var img = ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                var tex = new Texture2D(_gd, img.Width, img.Height, false, SurfaceFormat.Color);
                tex.SetData(img.Data);
                var result = new Img(_ui.BindTexture(tex), img.Width, img.Height, source, tex);
                _live.Add((result, tex));
                return result;
            }
            catch { /* not a decodable image: treated as missing */ }
        }
        return null;
    }

    private byte[]? ReadBytes(string path, out string source)
    {
        source = "";
        // The index already resolves priority: loose files in SWGL/ over pk3 entries.
        if (_index()?.Files.TryGetValue(path, out var entry) == true)
        {
            source = $"{entry.Pk3Folder}/{entry.Pk3Name}";
            return Pk3Reader.ReadBytes(entry);
        }
        return null;
    }

    /// <summary>Forgets every cached image (after an override or a re-index).</summary>
    public void Invalidate()
    {
        _dead.AddRange(_live);
        _live.Clear();
        _cache.Clear();
        Generation++;
    }

    /// <summary>Disposes invalidated textures; call before building a new frame.</summary>
    public void Flush()
    {
        foreach (var (img, tex) in _dead) { _ui.UnbindTexture(img.Id); tex.Dispose(); }
        _dead.Clear();
    }

    /// <summary>The indexed file an image path currently resolves to (tries the other image extensions too).</summary>
    public Pk3Entry? FindEntry(string gamePath)
    {
        var path = Normalize(gamePath);
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var stem = Extensions.Contains(ext) ? path[..^ext.Length] : path;
        var index = _index();
        if (index == null) return null;
        foreach (var c in new[] { path }.Concat(Extensions.Select(e => stem + e)))
            if (index.Files.TryGetValue(c, out var entry)) return entry;
        return null;
    }

    /// <summary>
    /// Writes <paramref name="sourceFile"/> as the loose asset for <paramref name="gamePath"/>,
    /// converting between png and tga when the extensions differ. Returns the game path actually used.
    /// </summary>
    public string Import(string gamePath, string sourceFile)
    {
        var bytes = File.ReadAllBytes(sourceFile);
        return Write(bytes, System.IO.Path.GetExtension(sourceFile).ToLowerInvariant(), gamePath);
    }

    /// <summary>
    /// Copies the current image of <paramref name="oldPath"/> to <paramref name="newPath"/> as a loose file.
    /// A loose source file is deleted; a source inside a pk3 cannot be removed and stays there.
    /// </summary>
    public string Move(string oldPath, string newPath)
    {
        var entry = FindEntry(oldPath) ?? throw new InvalidOperationException("The current image was not found.");
        var result = Write(Pk3Reader.ReadBytes(entry), System.IO.Path.GetExtension(entry.Path).ToLowerInvariant(), newPath);
        if (entry.IsLoose && !string.Equals(entry.Path, result, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(entry.Pk3FullPath); } catch { /* leave it in place */ }
            _index()?.Remove(entry.Path);
        }
        Invalidate();
        return result;
    }

    private string Write(byte[] bytes, string srcExt, string gamePath)
    {
        var target = Normalize(gamePath);
        if (target.Length == 0) throw new InvalidOperationException("The image path is empty.");
        var dir = _gameDir() ?? throw new InvalidOperationException("No SWGL folder selected.");

        var dstExt = System.IO.Path.GetExtension(target).ToLowerInvariant();
        if (dstExt.Length == 0) { target += srcExt; dstExt = srcExt; }
        if (dstExt != ".png" && dstExt != ".tga")
            throw new InvalidOperationException($"Unsupported destination extension: {dstExt} (png or tga).");

        var decoded = ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha); // validates the image
        if (srcExt != dstExt)
        {
            using var ms = new MemoryStream();
            var w = new ImageWriter();
            if (dstExt == ".png") w.WritePng(decoded.Data, decoded.Width, decoded.Height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, ms);
            else w.WriteTga(decoded.Data, decoded.Width, decoded.Height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, ms);
            bytes = ms.ToArray();
        }

        var path = System.IO.Path.Combine(dir, "SWGL", target.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        _index()?.AddLoose(target, path);
        Invalidate();
        return target;
    }
}
