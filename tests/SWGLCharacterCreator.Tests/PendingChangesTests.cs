using SWGLCharacterCreator;
using Xunit;

namespace SWGLCharacterCreator.Tests;

public class PendingChangesTests
{
    [Fact] public void Stage_NormalizesAndIsCaseInsensitive() {
        var p = new PendingChanges();
        var x = new byte[] { 1 };
        p.Stage("\\Gfx/A.png", x);
        Assert.Same(x, p.Items["gfx/a.png"]);
        Assert.True(p.Any);
    }
    [Fact] public void StageDelete_StoresEmpty() {
        var p = new PendingChanges();
        p.StageDelete("/a/b.cha");
        Assert.Empty(p.Items["a/b.cha"]);
    }
    [Fact] public void Remove_ReturnsFalseWhenAbsent() {
        var p = new PendingChanges();
        Assert.False(p.Remove("nope"));
        p.Stage("a", new byte[0]);
        Assert.True(p.Remove("A"));
        Assert.False(p.Any);
    }

    [Fact] public void ReplayInto_AddsStagedAndDeletedAsMemoryEntries()
    {
        var p = new PendingChanges(); var ix = new Pk3Index();
        ix.AddPk3Entry("a/old.cha", "p.pk3", 5);
        p.Stage("a/new.cha", new byte[] { 1, 2 });
        p.StageDelete("a/old.cha");
        p.ReplayInto(ix);
        Assert.Equal(new byte[] { 1, 2 }, ix.Files["a/new.cha"].Memory);
        Assert.Empty(ix.Files["a/old.cha"].Memory!);
    }
}
