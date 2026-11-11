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
}
