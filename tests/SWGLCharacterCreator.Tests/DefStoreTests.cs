using System.Text;
using SWGLCharacterCreator;
using Xunit;

namespace SWGLCharacterCreator.Tests;

public class DefStoreTests
{
    private const string Cha = "{\ncode AAYLA\nVariant\n{\ncode DEFAULT\n}\nVariant\n{\ncode ALT\n}\n}\n";

    private static Pk3Index MakeIndex()
    {
        var ix = new Pk3Index();
        ix.AddMemory(DefStore.PathFor("AAYLA", "DEFAULT"), Encoding.Latin1.GetBytes("ui_force 1"));
        ix.AddMemory(DefStore.PathFor("AAYLA", "ALT"), Encoding.Latin1.GetBytes("ui_force 2"));
        return ix;
    }

    private static (DefStore store, ConfigFile cha, Block ch, Block def, Block alt) Setup(string cha = Cha)
    {
        var store = new DefStore(MakeIndex());
        var file = ConfigFile.Parse(cha);
        store.Bind(file);
        var ch = file.Blocks[0];
        return (store, file, ch, ch.Children[0], ch.Children[1]);
    }

    private static void SetCode(Block b, string code) => b.Props.First(p => p.Key == "code").Value = code;
    private static string Text(byte[] b) => Encoding.Latin1.GetString(b);

    [Fact] public void Unchanged_NoChanges()
    {
        var (s, f, _, _, _) = Setup();
        Assert.Empty(s.CollectChanges(new[] { f }));
    }

    [Fact] public void RenameVariant_WritesNewAndEmptiesOld()
    {
        var (s, f, _, def, _) = Setup();
        SetCode(def, "NEW");
        var c = s.CollectChanges(new[] { f });
        Assert.Equal(2, c.Count);
        Assert.Equal("ui_force 1", Text(c[DefStore.PathFor("AAYLA", "NEW")]));
        Assert.Empty(c[DefStore.PathFor("AAYLA", "DEFAULT")]);
    }

    [Fact] public void RenameAndBack_NoChanges()
    {
        var (s, f, _, def, _) = Setup();
        SetCode(def, "NEW");
        SetCode(def, "DEFAULT");
        Assert.Empty(s.CollectChanges(new[] { f }));
    }

    [Fact] public void RenameCharacter_MovesEveryVariant()
    {
        var (s, f, ch, _, _) = Setup();
        SetCode(ch, "NEWCHAR");
        var c = s.CollectChanges(new[] { f });
        Assert.Equal(4, c.Count);
        Assert.Equal("ui_force 1", Text(c[DefStore.PathFor("NEWCHAR", "DEFAULT")]));
        Assert.Equal("ui_force 2", Text(c[DefStore.PathFor("NEWCHAR", "ALT")]));
        Assert.Empty(c[DefStore.PathFor("AAYLA", "DEFAULT")]);
        Assert.Empty(c[DefStore.PathFor("AAYLA", "ALT")]);
    }

    [Fact] public void DeleteVariant_EmptiesItsDef()
    {
        var (s, f, ch, _, alt) = Setup();
        ch.Children.Remove(alt);
        var c = s.CollectChanges(new[] { f });
        Assert.Single(c);
        Assert.Empty(c[DefStore.PathFor("AAYLA", "ALT")]);
    }

    [Fact] public void DeleteThenRenameIntoSamePath_WritesContentNotEmpty()
    {
        var (s, f, ch, def, alt) = Setup();
        ch.Children.Remove(def);
        SetCode(alt, "DEFAULT");
        var c = s.CollectChanges(new[] { f });
        Assert.Equal(2, c.Count);
        Assert.Equal("ui_force 2", Text(c[DefStore.PathFor("AAYLA", "DEFAULT")]));
        Assert.Empty(c[DefStore.PathFor("AAYLA", "ALT")]);
    }

    [Fact] public void CopyDefs_NewVariantGetsCopy()
    {
        var (s, f, ch, def, _) = Setup();
        var clone = ConfigFile.Parse(Cha).Blocks[0].Children[0];
        SetCode(clone, "COPY");
        ch.Children.Add(clone);
        s.CopyDefs(def, clone);
        var c = s.CollectChanges(new[] { f });
        Assert.Single(c);
        Assert.Equal("ui_force 1", Text(c[DefStore.PathFor("AAYLA", "COPY")]));
    }

    [Fact] public void UnbindAfterDelete_DoesNotDeleteDef()
    {
        var (s, f, ch, _, alt) = Setup();
        ch.Children.Remove(alt);
        s.Unbind(f);
        var fresh = ConfigFile.Parse(Cha);
        s.Bind(fresh);
        Assert.Empty(s.CollectChanges(new[] { fresh }));
    }

    [Fact] public void MissingDef_NoBindingContent_NoChange()
    {
        var (s, f, ch, _, _) = Setup();
        var v = ConfigFile.Parse("{\ncode X\nVariant\n{\ncode NOFILE\n}\n}\n").Blocks[0].Children[0];
        ch.Children.Add(v);
        s.Bind(f);
        Assert.Empty(s.CollectChanges(new[] { f }));
    }

    [Fact] public void AfterSave_ClearsState()
    {
        var (s, f, ch, def, alt) = Setup();
        SetCode(def, "NEW");
        ch.Children.Remove(alt);
        Assert.NotEmpty(s.CollectChanges(new[] { f }));
        s.AfterSave(new[] { f });
        Assert.Empty(s.CollectChanges(new[] { f }));
        Assert.False(s.AnyDirty);
    }

    [Fact] public void SetDef_MarksDirtyAndWritesAtCurrentPath()
    {
        var (s, f, _, def, _) = Setup();
        var d = DefFile.Parse("ui_force 9");
        s.SetDef(def, d);
        Assert.True(s.IsDirty(def));
        Assert.True(s.AnyDirty);
        var c = s.CollectChanges(new[] { f });
        Assert.Equal("ui_force 9", Text(c[DefStore.PathFor("AAYLA", "DEFAULT")]));
    }
}
