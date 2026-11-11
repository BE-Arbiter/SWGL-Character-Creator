using System.IO.Compression;
using SWGLCharacterCreator;
using Xunit;

namespace SWGLCharacterCreator.Tests;

public sealed class ChangesPk3Tests : IDisposable
{
    private static readonly DateTime D = new(2026, 11, 11);
    private readonly string dir = Path.Combine(Path.GetTempPath(), "swgl_cpk_" + Guid.NewGuid().ToString("N"));
    private string Target => Path.Combine(dir, ChangesPk3.FileName(D));

    public ChangesPk3Tests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    private static Dictionary<string, byte[]> ReadAll(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            map[e.FullName] = ms.ToArray();
        }
        return map;
    }

    [Fact]
    public void FileName_UsesThreeUnderscoresAndYyMmDd() =>
        Assert.Equal("Z___SWGL_Characters_changes_261111.pk3", ChangesPk3.FileName(D));

    [Fact]
    public void Write_CreatesPk3WithEntriesAndEmptyDeletes()
    {
        var r = ChangesPk3.Write(dir, D, new Dictionary<string, byte[]>
        {
            ["ext_data/a.cha"] = "x"u8.ToArray(),
            ["ext_data/old.cha"] = [],
        });
        Assert.True(r.Created);
        Assert.Equal(Target, r.Pk3Path);
        var all = ReadAll(Target);
        Assert.Equal(2, all.Count);
        Assert.Equal("x"u8.ToArray(), all["ext_data/a.cha"]);
        Assert.Empty(all["ext_data/old.cha"]);
    }

    [Fact]
    public void Write_SameDay_UpdatesKeepingOtherEntries()
    {
        ChangesPk3.Write(dir, D, new Dictionary<string, byte[]> { ["a"] = "1"u8.ToArray(), ["b"] = "1"u8.ToArray() });
        var r = ChangesPk3.Write(dir, D, new Dictionary<string, byte[]> { ["A"] = "2"u8.ToArray() });
        Assert.False(r.Created);
        var all = ReadAll(Target);
        Assert.Equal(2, all.Count);
        Assert.Equal("2"u8.ToArray(), all["a"]);
        Assert.Equal("1"u8.ToArray(), all["b"]);
    }

    [Fact]
    public void Write_TargetLocked_ThrowsAndLeavesTargetUntouched()
    {
        ChangesPk3.Write(dir, D, new Dictionary<string, byte[]> { ["a"] = "1"u8.ToArray() });
        var before = File.ReadAllBytes(Target);
        using (File.Open(Target, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() =>
                ChangesPk3.Write(dir, D, new Dictionary<string, byte[]> { ["a"] = "2"u8.ToArray() }));
        Assert.Equal(before, File.ReadAllBytes(Target));
        Assert.False(File.Exists(Target + ".part"));
    }
}
