using SWGLCharacterCreator;
using Xunit;

namespace SWGLCharacterCreator.Tests;

public class Pk3IndexTests
{
    [Fact] public void AddMemory_WinsAndIsReadable() {
        var ix = new Pk3Index();
        var e = ix.AddMemory("gfx/a.png", new byte[] { 1, 2 });
        Assert.Same(e, ix.Files["GFX/A.PNG"]);
        Assert.Equal(new byte[] { 1, 2 }, Pk3Reader.ReadBytes(e));
        Assert.False(ix.IsShadowed("gfx/a.png"));
    }
    [Fact] public void ReadText_Memory_IsLatin1() {
        var ix = new Pk3Index();
        var e = ix.AddMemory("x.cha", new byte[] { 0xE9 });
        Assert.Equal("é", Pk3Reader.ReadText(e));
    }
    [Fact] public void AddPk3Entry_ReplacesMemoryEntry() {
        var ix = new Pk3Index();
        ix.AddMemory("a.cfg", new byte[] { 1 });
        var e = ix.AddPk3Entry("a.cfg", @"C:\g\SWGL\Z___SWGL_Characters_changes_261111.pk3", 1);
        Assert.Null(e.Memory);
        Assert.Equal("Z___SWGL_Characters_changes_261111.pk3", ix.Files["a.cfg"].Pk3Name);
    }
}
