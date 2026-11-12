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

    private static (DefStore s, ConfigFile f, Block ch, Block def) Setup2()
    {
        var (s, f, ch, def, _) = Setup();
        return (s, f, ch, def);
    }

    private static Block CloneVariant(DefStore s, Block src, ConfigFile into, Block intoChar, string code)
    {
        var clone = ConfigFile.Parse(Cha).Blocks[0].Children[0];
        SetCode(clone, code);
        intoChar.Children.Add(clone);
        s.CopyDefs(src, clone);
        return clone;
    }

    [Fact] public void Prune_DropsOrphanCopy()
    {
        var (s, f, ch, def) = Setup2();
        var clone = CloneVariant(s, def, f, ch, "COPY");
        ch.Children.Remove(clone);
        s.Prune(new[] { f });
        Assert.False(s.AnyDirty);
        Assert.Empty(s.CollectChanges(new[] { f }));
    }

    [Fact] public void UnbindBind_AfterOrphanCopy_NotDirty()
    {
        var (s, f, ch, def) = Setup2();
        var clone = CloneVariant(s, def, f, ch, "COPY");
        ch.Children.Remove(clone);
        s.Prune(new[] { f });
        s.Unbind(f);
        var fresh = ConfigFile.Parse(Cha);
        s.Bind(fresh);
        Assert.False(s.AnyDirty);
    }

    [Fact] public void Prune_KeepsNonLiveBindingWithOrigin()
    {
        var (s, f, ch, _, alt) = Setup();
        ch.Children.Remove(alt);
        s.Prune(new[] { f });
        var c = s.CollectChanges(new[] { f });
        Assert.Single(c);
        Assert.Empty(c[DefStore.PathFor("AAYLA", "ALT")]);
    }

    [Fact] public void CrossFileMove_OneWriteOneEmpty()
    {
        var (s, f, ch, def) = Setup2();
        var other = ConfigFile.Parse("{\ncode OTHER\n}\n");
        s.Bind(other);
        var clone = CloneVariant(s, def, other, other.Blocks[0], "DEFAULT");
        ch.Children.Remove(def);
        s.Prune(new[] { f, other });
        var c = s.CollectChanges(new[] { f, other });
        Assert.Equal(2, c.Count);
        Assert.Equal("ui_force 1", Text(c[DefStore.PathFor("OTHER", "DEFAULT")]));
        Assert.Empty(c[DefStore.PathFor("AAYLA", "DEFAULT")]);
    }

    [Fact] public void DirtyDef_ThenUnbindBind_NotDirty()
    {
        var (s, f, _, def) = Setup2();
        s.SetDef(def, DefFile.Parse("ui_force 9"));
        s.Unbind(f);
        var fresh = ConfigFile.Parse(Cha);
        s.Bind(fresh);
        Assert.False(s.AnyDirty);
        Assert.Empty(s.CollectChanges(new[] { fresh }));
    }

    [Fact] public void ResetBinding_RestoresOriginal()
    {
        var (s, f, _, def, _) = Setup();
        s.SetDef(def, DefFile.Parse("ui_force 9"));
        Assert.True(s.IsDirty(def));
        s.ResetBinding(def);
        Assert.False(s.IsDirty(def));
        Assert.Empty(s.CollectChanges(new[] { f }));
        Assert.Equal("ui_force 1", s.Load(def)!.Serialize().Trim());
    }

    [Fact] public void ResetBinding_NoOriginRemovesBinding()
    {
        var (s, f, ch, _, _) = Setup();
        var v = new Block { Type = "Variant" };
        s.SetDef(v, DefFile.Parse("ui_force 5"));
        s.ResetBinding(v);
        Assert.False(s.IsDirty(v));
        Assert.Null(s.Load(v));
    }

    [Fact] public void Rebase_UsesNewIndexForLoads()
    {
        var (s, f, _, def, _) = Setup();
        var ix2 = new Pk3Index();
        ix2.AddMemory(DefStore.PathFor("AAYLA", "DEFAULT"), Encoding.Latin1.GetBytes("ui_force 7"));
        s.Rebase(ix2);
        Assert.Contains("7", s.Load(def)!.Serialize());
    }

    [Fact] public void ResetAt_ResetsDirtyVariantAtPath()
    {
        var (s, f, _, def, alt) = Setup();
        s.SetDef(def, DefFile.Parse("ui_force 9"));
        s.SetDef(alt, DefFile.Parse("ui_force 8"));
        var path = DefStore.PathFor("AAYLA", "DEFAULT");
        Assert.True(s.CanResetAt(new[] { f }, path));
        Assert.True(s.ResetAt(new[] { f }, path));
        Assert.False(s.IsDirty(def));
        Assert.True(s.IsDirty(alt));
        Assert.Contains("1", s.Load(def)!.Serialize());
        Assert.False(s.CanResetAt(new[] { f }, path));
    }

    [Fact] public void ResetAt_IgnoresRenameOnlyWrites()
    {
        var (s, f, _, def, _) = Setup();
        SetCode(def, "NEW");
        Assert.False(s.CanResetAt(new[] { f }, DefStore.PathFor("AAYLA", "NEW")));
        Assert.False(s.ResetAt(new[] { f }, DefStore.PathFor("AAYLA", "DEFAULT")));
    }
}
