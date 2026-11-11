using System.IO.Compression;

namespace SWGLCharacterCreator;

public sealed record Pk3Entry(string Path, string Pk3Name, string Pk3Folder, long Size, string Pk3FullPath, string EntryName, bool IsLoose = false);

/// <summary>
/// Virtual file system index over base/*.pk3 then SWGL/*.pk3 (alphabetical order).
/// A later pk3 overrides an earlier one for the same path.
/// </summary>
public sealed class Pk3Index
{
    private readonly Dictionary<string, Pk3Entry> _files = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, Pk3Entry> Files => _files;
    public int Pk3Count { get; private set; }
    public int Overridden { get; private set; }
    public int LooseCount { get; private set; }

    private readonly HashSet<string> _shadowed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when a loose file hides a different version of the same path stored in a pk3.</summary>
    public bool IsShadowed(string path) => _shadowed.Contains(path.Replace('\\', '/').TrimStart('/'));
    public List<string> Errors { get; } = new();

    public static readonly string[] Folders = { "base", "SWGL" };

    /// <summary>Pk3 files that will be indexed, in load order.</summary>
    public static List<(string Folder, string File)> GetLoadOrder(string gameDataDir)
    {
        var list = new List<(string, string)>();
        foreach (var folder in Folders)
        {
            var dir = Path.Combine(gameDataDir, folder);
            if (!Directory.Exists(dir)) continue;
            var files = Directory.EnumerateFiles(dir, "*.pk3", SearchOption.TopDirectoryOnly)
                .Where(f => string.Equals(Path.GetExtension(f), ".pk3", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) list.Add((folder, f));
        }
        return list;
    }

    public static Pk3Index Build(string gameDataDir, IProgress<(int done, int total, string current)>? progress = null,
        CancellationToken ct = default)
    {
        var index = new Pk3Index();
        var order = GetLoadOrder(gameDataDir);
        for (int i = 0; i < order.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (folder, file) = order[i];
            var name = Path.GetFileName(file);
            progress?.Report((i, order.Count, $"{folder}/{name}"));
            try
            {
                using var zip = ZipFile.OpenRead(file);
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.EndsWith('/')) continue; // directory entry
                    var key = e.FullName.Replace('\\', '/').TrimStart('/');
                    if (index._files.ContainsKey(key)) index.Overridden++;
                    index._files[key] = new Pk3Entry(key, name, folder, e.Length, file, e.FullName);
                }
                index.Pk3Count++;
            }
            catch (Exception ex)
            {
                index.Errors.Add($"{folder}/{name}: {ex.Message}");
            }
        }
        // Loose files override every pk3, but only those under SWGL/ (loose files in base/ are ignored).
        var swglDir = Path.Combine(gameDataDir, "SWGL");
        if (Directory.Exists(swglDir))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(swglDir, "*", SearchOption.AllDirectories))
                {
                    if (f.EndsWith(".pk3", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".pk3.part", StringComparison.OrdinalIgnoreCase)) continue;
                    index.AddLoose(Path.GetRelativePath(swglDir, f).Replace('\\', '/'), f);
                }
            }
            catch (Exception ex) { index.Errors.Add("SWGL/ (loose files): " + ex.Message); }
        }
        progress?.Report((order.Count, order.Count, ""));
        return index;
    }

    /// <summary>Registers (or replaces) a loose file under SWGL/ as the winning version of <paramref name="path"/>.</summary>
    public Pk3Entry AddLoose(string path, string diskPath)
    {
        var key = path.Replace('\\', '/').TrimStart('/');
        var fi = new FileInfo(diskPath);
        if (_files.TryGetValue(key, out var previous)) { Overridden++; if (!previous.IsLoose) _shadowed.Add(key); }
        var entry = new Pk3Entry(key, "(loose)", "SWGL", fi.Exists ? fi.Length : 0, diskPath, key, IsLoose: true);
        _files[key] = entry;
        LooseCount++;
        return entry;
    }

    public void Remove(string path)
    {
        var key = path.Replace('\\', '/').TrimStart('/');
        _files.Remove(key);
        _shadowed.Remove(key);
    }

    /// <summary>Files under <paramref name="dir"/> with the given extension.</summary>
    public IEnumerable<Pk3Entry> Find(string dir, string extension)
    {
        var prefix = dir.TrimEnd('/') + "/";
        return _files.Values
            .Where(e => e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && e.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase);
    }
}

public static class Pk3Reader
{
    public static byte[] ReadBytes(Pk3Entry entry)
    {
        if (entry.IsLoose) return File.ReadAllBytes(entry.Pk3FullPath);
        using var zip = ZipFile.OpenRead(entry.Pk3FullPath);
        var e = zip.GetEntry(entry.EntryName) ?? throw new FileNotFoundException(entry.EntryName);
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Reads an entry as Latin-1 so that every byte round-trips unchanged.</summary>
    public static string ReadText(Pk3Entry entry)
    {
        if (entry.IsLoose) return File.ReadAllText(entry.Pk3FullPath, System.Text.Encoding.Latin1);
        using var zip = ZipFile.OpenRead(entry.Pk3FullPath);
        var e = zip.GetEntry(entry.EntryName) ?? throw new FileNotFoundException(entry.EntryName);
        using var r = new StreamReader(e.Open(), System.Text.Encoding.Latin1);
        return r.ReadToEnd();
    }
}
