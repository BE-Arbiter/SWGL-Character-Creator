using System.Text;
using SWGLCharacterCreator;
using Xunit;

namespace SWGLCharacterCreator.Tests;

public class GameDataCatalogTests
{
    private static void Add(Pk3Index i, string path, string text) => i.AddMemory(path, Encoding.Latin1.GetBytes(text));

    private static GameDataCatalog Sample()
    {
        var i = new Pk3Index();
        Add(i, "ext_data/weapons.dat", "// weapons\n{ weaponclass weapon_melee // trailing\n playerusable 1\n attackDefinition main { damage 5 } }\n{ weaponclass weapon_DLT20A playerUsable 1 }");
        Add(i, "ext_data/weapon_clonerandom.wpn", "{ weaponclass weapon_clonerandom playerUsable 0 }");
        Add(i, "ext_data/weapon_z6.wpn", "{ weaponclass weapon_z6 }{ weaponclass weapon_z7 }");
        Add(i, "ext_data/sabers/a.sab", "/* c */ Zeta { saberType SABER_SINGLE }\n\"Alpha\" { name \"x y\" }");
        Add(i, "ext_data/npcs/a.npc", "Tusken_Raider { playerModel x sub { a 1 } }\nAardvark { }");
        Add(i, "ext_data/zero.wpn", "");
        return GameDataCatalog.Build(i);
    }

    [Fact] public void ParseBlocks_NamedAndAnonymous()
    {
        var b = GameDataCatalog.ParseBlocks("// c\nfoo { a 1 sub { b 2 } }\n{ weaponclass weapon_x playerUsable 0 }");
        Assert.Equal(2, b.Count);
        Assert.Equal("foo", b[0].Name);
        Assert.Equal("1", b[0].Props["a"]);
        Assert.False(b[0].Props.ContainsKey("b"));
        Assert.Null(b[1].Name);
        Assert.Equal("weapon_x", b[1].Props["weaponclass"]);
    }

    [Fact] public void Build_WeaponsFromDatAndWpn_WithPlayerUsable()
    {
        var c = Sample();
        Assert.False(c.FindWeapon("weapon_clonerandom")!.PlayerUsable);
        Assert.True(c.FindWeapon("weapon_z6")!.PlayerUsable);
        Assert.True(c.FindWeapon("weapon_z7")!.PlayerUsable);
        Assert.True(c.FindWeapon("weapon_melee")!.PlayerUsable);
        Assert.NotNull(c.FindWeapon("weapon_none"));
    }

    [Fact] public void Build_SabersAndNpcs()
    {
        var c = Sample();
        Assert.Equal(new[] { "Alpha", "Zeta" }, c.Sabers);
        Assert.Equal(new[] { "Aardvark", "Tusken_Raider" }, c.NpcTypes);
        Assert.Equal("Zeta", c.FindSaber("zeta"));
        Assert.Equal("Tusken_Raider", c.FindNpc("TUSKEN_raider"));
        Assert.Null(c.FindNpc("nope"));
    }

    [Fact] public void FindWeapon_IsCaseInsensitive_ReturnsDefinedCasing() =>
        Assert.Equal("weapon_DLT20A", Sample().FindWeapon("weapon_dlt20a")!.Name);

    [Fact] public void Build_LaterDefinitionReplacesEarlier()
    {
        var i = new Pk3Index();
        Add(i, "ext_data/weapons.dat", "{ weaponclass weapon_a playerUsable 1 }");
        Add(i, "ext_data/weapon_a.wpn", "{ weaponclass weapon_A playerUsable 0 }");
        var w = GameDataCatalog.Build(i).FindWeapon("weapon_a")!;
        Assert.Equal("weapon_A", w.Name); Assert.False(w.PlayerUsable);
        Assert.Equal(2, GameDataCatalog.Build(i).Weapons.Count); // weapon_A + weapon_none
    }
}
