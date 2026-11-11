using SWGLCharacterCreator;
using Xunit;

namespace SWGLCharacterCreator.Tests;

public class EditStagingTests
{
    private static byte[] B(params byte[] b) => b;

    [Fact] public void RenameThenDiscard_KeepsNewContentStaged() {
        var p = new PendingChanges(); var ix = new Pk3Index();
        var real = ix.AddPk3Entry("a/x.cha", "p.pk3", 1);
        var e = EditStaging.Rename(p, ix, real, "a/y.cha", B(1));
        Assert.Equal(B(1), p.Items["a/y.cha"]);
        Assert.Empty(p.Items["a/x.cha"]);
        Assert.Same(e, ix.Files["a/y.cha"]);
    }
    [Fact] public void CreateStagesEmptyContent() {
        var p = new PendingChanges(); var ix = new Pk3Index();
        var e = EditStaging.Create(p, ix, "a/n.cha");
        Assert.NotNull(e.Memory);
        Assert.Empty(p.Items["a/n.cha"]);
    }
    [Fact] public void DoubleRename_NoGhost() {
        var p = new PendingChanges(); var ix = new Pk3Index();
        var x = ix.AddPk3Entry("a/x.cha", "p.pk3", 1);
        var y = EditStaging.Rename(p, ix, x, "a/y.cha", B(1));
        EditStaging.Rename(p, ix, y, "a/z.cha", B(2));
        Assert.Equal(2, p.Items.Count);
        Assert.Empty(p.Items["a/x.cha"]);
        Assert.Equal(B(2), p.Items["a/z.cha"]);
        Assert.False(p.Items.ContainsKey("a/y.cha"));
        Assert.False(ix.Files.ContainsKey("a/y.cha"));
    }
    [Fact] public void CaseOnlyRename_NoDelete() {
        var p = new PendingChanges(); var ix = new Pk3Index();
        var x = ix.AddPk3Entry("a/x.cha", "p.pk3", 1);
        EditStaging.Rename(p, ix, x, "a/X.cha", B(3));
        Assert.Single(p.Items);
        Assert.Equal(B(3), p.Items["a/x.cha"]);
    }
}
