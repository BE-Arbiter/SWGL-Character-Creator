using System.IO.Compression;

namespace SWGLCharacterCreator;

public sealed record ChangesPk3Result(string Pk3Path, bool Created);

/// <summary>Writes edits into the dated "changes" pk3, atomically (temp file then move).</summary>
public static class ChangesPk3
{
    public const string Prefix = "Z___SWGL_Characters_changes_";

    public static string FileName(DateTime date) => Prefix + date.ToString("yyMMdd") + ".pk3";

    /// <summary>
    /// Merges <paramref name="changes"/> (path -> bytes; empty array = zero-length entry) into the day's pk3.
    /// Existing entries not named in <paramref name="changes"/> (case-insensitive) are kept.
    /// </summary>
    public static ChangesPk3Result Write(string swglDir, DateTime date, IReadOnlyDictionary<string, byte[]> changes)
    {
        var target = Path.Combine(swglDir, FileName(date));
        var part = target + ".part";
        var created = !File.Exists(target);
        var replaced = new HashSet<string>(changes.Keys.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var outStream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var outZip = new ZipArchive(outStream, ZipArchiveMode.Create))
            {
                if (!created)
                {
                    using var inStream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var inZip = new ZipArchive(inStream, ZipArchiveMode.Read);
                    foreach (var e in inZip.Entries)
                    {
                        if (replaced.Contains(Normalize(e.FullName))) continue;
                        var copy = outZip.CreateEntry(e.FullName, CompressionLevel.Optimal);
                        using var src = e.Open();
                        using var dst = copy.Open();
                        src.CopyTo(dst);
                    }
                }
                foreach (var (path, data) in changes)
                {
                    var entry = outZip.CreateEntry(Normalize(path), CompressionLevel.Optimal);
                    using var dst = entry.Open();
                    dst.Write(data, 0, data.Length);
                }
            }
            File.Move(part, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
        return new ChangesPk3Result(target, created);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
