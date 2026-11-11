using System.Text;
using SWGLCharacterCreator;
using Xunit;

namespace SWGLCharacterCreator.Tests;

public class DefRulesTests
{
    private static GameDataCatalog Cat()
    {
        var i = new Pk3Index();
        i.AddMemory("ext_data/weapons.dat", Encoding.Latin1.GetBytes("{ weaponclass weapon_DLT20A } { weaponclass weapon_melee }"));
        i.AddMemory("ext_data/sabers/a.sab", Encoding.Latin1.GetBytes("kyle { }"));
        i.AddMemory("ext_data/npcs/a.npc", Encoding.Latin1.GetBytes("jedi { }"));
        return GameDataCatalog.Build(i);
    }

    [Fact] public void TryParseHex()
    {
        Assert.True(DefRules.TryParseHex("xadd8e6", out var r, out var g, out var b));
        Assert.Equal((0xad, 0xd8, 0xe6), (r, g, b));
        Assert.True(DefRules.TryParseHex("xADD8E6", out _, out _, out _));
        Assert.False(DefRules.TryParseHex("x00FFFFF", out _, out _, out _));
        Assert.False(DefRules.TryParseHex("#8CCFFF", out _, out _, out _));
        Assert.Equal("xADD8E6", DefRules.FormatHex(0xad, 0xd8, 0xe6));
    }

    [Fact] public void Validate_ReportsEachProblem()
    {
        var d = DefFile.Parse("ui_npc_type tusken_king\nui_weaponOne weapon_dlt20a\nui_saber_color x00FFFFF\nui_jump_level 5\nui_health abc\nui_saber \"\"");
        var m = DefRules.Validate(d, Cat());
        foreach (var k in new[] { "ui_npc_type", "ui_weaponOne", "ui_saber_color", "ui_jump_level", "ui_health" })
            Assert.Contains(m, x => x.StartsWith(k + ": "));
        Assert.DoesNotContain(m, x => x.StartsWith("ui_saber:"));
        Assert.Equal(5, m.Count);
        Assert.Contains("ui_npc_type: unknown NPC type \"tusken_king\"", m);
        Assert.Contains("ui_weaponOne: weapon \"weapon_dlt20a\" is defined as \"weapon_DLT20A\"", m);
        Assert.Contains("ui_jump_level: 5 is out of range (0-3)", m);
        Assert.Contains("ui_health: \"abc\" is not a number", m);
    }

    [Fact] public void Validate_ValidFile_NoMessages()
    {
        var d = DefFile.CreateEmpty();
        d.Set("ui_saber", "kyle"); d.Set("ui_npc_type", "JEDI"); d.Set("ui_saber_color", "xADD8E6");
        Assert.Empty(DefRules.Validate(d, Cat()));
    }

    [Fact] public void Validate_RangesAndUnknowns()
    {
        var d = DefFile.Parse("ui_health 0\nui_force -1\nui_char_color_red 256\nui_saboff_level 4\nui_lightning_color pink\nui_saber nope\nui_saber2 empty\nui_npc_weapon weapon_zzz");
        var m = DefRules.Validate(d, Cat());
        Assert.Equal(7, m.Count);
        Assert.Contains("ui_saber: unknown saber \"nope\"", m);
        Assert.Contains("ui_lightning_color: unknown lightning color \"pink\"", m);
        Assert.Contains("ui_npc_weapon: unknown weapon \"weapon_zzz\"", m);
    }
}
