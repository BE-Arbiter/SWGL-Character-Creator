using SWGLCharacterCreator;
using Xunit;

namespace SWGLCharacterCreator.Tests;

public sealed class SaveTests : IDisposable
{
    private static readonly DateTime D = new(2026, 11, 11);
    private readonly string dir = Path.Combine(Path.GetTempPath(), "swgl_save_" + Guid.NewGuid().ToString("N"));

    public SaveTests() => Directory.CreateDirectory(Path.Combine(dir, "SWGL", "ext_data"));
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    private string Loose(string path) => Path.Combine(dir, "SWGL", path.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void FindMaskingLoose_ReturnsOnlyExistingLooseFiles()
    {
        File.WriteAllText(Loose("ext_data/a.cha"), "x");
        var found = SavePlan.FindMaskingLoose(dir, new[] { "ext_data/a.cha", "ext_data/b.cha" });
        Assert.Equal(new[] { "ext_data/a.cha" }, found);
    }

    [Fact]
    public void FindMaskingLoose_RejectsEscapingPath() =>
        Assert.Throws<InvalidOperationException>(() => SavePlan.FindMaskingLoose(dir, new[] { "../x" }));

    [Fact]
    public void Commit_WritesPk3AndRepointsIndex()
    {
        var index = new Pk3Index();
        index.AddMemory("ext_data/a.cha", new byte[] { 1 });
        var changes = new Dictionary<string, byte[]> { ["ext_data/a.cha"] = new byte[] { 1, 2, 3 } };

        var outcome = SavePlan.Commit(dir, D, changes, deleteLoose: false, index);

        Assert.True(outcome.Result.Created);
        var e = index.Files["ext_data/a.cha"];
        Assert.Null(e.Memory);
        Assert.False(e.IsLoose);
        Assert.Equal(outcome.Result.Pk3Path, e.Pk3FullPath);
        Assert.Equal(new byte[] { 1, 2, 3 }, Pk3Reader.ReadBytes(e));
    }

    [Fact]
    public void Commit_DeleteLoose_RemovesMaskAndPointsAtPk3()
    {
        File.WriteAllText(Loose("ext_data/a.cha"), "old");
        var index = new Pk3Index();
        index.AddLoose("ext_data/a.cha", Loose("ext_data/a.cha"));

        var outcome = SavePlan.Commit(dir, D, new() { ["ext_data/a.cha"] = "new"u8.ToArray() }, deleteLoose: true, index);

        Assert.False(File.Exists(Loose("ext_data/a.cha")));
        Assert.Empty(outcome.DeleteErrors);
        Assert.False(index.Files["ext_data/a.cha"].IsLoose);
        Assert.Equal("new", Pk3Reader.ReadText(index.Files["ext_data/a.cha"]));
    }

    [Fact]
    public void Commit_KeepLoose_IndexStaysOnLooseFile()
    {
        File.WriteAllText(Loose("ext_data/a.cha"), "old");
        var index = new Pk3Index();
        index.AddLoose("ext_data/a.cha", Loose("ext_data/a.cha"));

        SavePlan.Commit(dir, D, new() { ["ext_data/a.cha"] = "new"u8.ToArray() }, deleteLoose: false, index);

        Assert.True(File.Exists(Loose("ext_data/a.cha")));
        Assert.True(index.Files["ext_data/a.cha"].IsLoose);
    }

    [Fact]
    public void Commit_SecondSaveSameDay_NotCreated()
    {
        var index = new Pk3Index();
        SavePlan.Commit(dir, D, new() { ["a"] = new byte[] { 1 } }, false, index);
        var o = SavePlan.Commit(dir, D, new() { ["b"] = new byte[] { 2 } }, false, index);
        Assert.False(o.Result.Created);
    }
}
