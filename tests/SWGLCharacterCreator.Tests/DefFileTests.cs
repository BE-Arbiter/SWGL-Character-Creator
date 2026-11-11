using SWGLCharacterCreator;
using Xunit;

namespace SWGLCharacterCreator.Tests;

public class DefFileTests
{
    [Fact] public void RoundTrip_UnchangedFile_IsIdentical() {
        var t = "ui_jump_level 3\r\nui_team player\r\nui_npc_weapon\tweapon_saber\r\nui_force 200";
        Assert.Equal(t, DefFile.Parse(t).Serialize());
    }
    [Fact] public void Edit_PreservesUnknownKeysAndOddValues() {
        var d = DefFile.Parse("ui_custom_x\tfoo bar\r\nui_saber\t\"\"\r\nui_health 300");
        d.Set("UI_HEALTH", "400");
        Assert.Equal("ui_custom_x\tfoo bar\r\nui_saber\t\"\"\r\nui_health 400", d.Serialize());
    }
    [Fact] public void Set_MissingKey_AppendsWithCanonicalSpelling() {
        var d = DefFile.Parse("ui_health 1"); d.Set("ui_weaponone", "weapon_saber");
        Assert.Equal("ui_health 1\r\nui_weaponOne\tweapon_saber", d.Serialize());
    }
    [Fact] public void Set_Empty_RemovesLine() { var d = DefFile.Parse("ui_npc_type\tX\r\nui_force 0"); d.Set("ui_npc_type", ""); Assert.Equal("ui_force 0", d.Serialize()); }
    [Fact] public void CreateEmpty_MatchesTemplate() {
        var d = DefFile.CreateEmpty();
        Assert.Equal(41, d.Lines.Count); Assert.Null(d.Get("ui_npc_type"));
        Assert.Equal("200", d.Get("ui_health")); Assert.Equal("weapon_melee", d.Get("ui_weaponOne")); Assert.Equal("ui_force", d.Lines[^1].Key);
    }
    [Fact] public void Parse_LfAndTrailingNewline_Tolerated() => Assert.Equal("1", DefFile.Parse("ui_force 1\n").Get("ui_force"));

    [Fact] public void Edit_KeepsOtherLinesOriginalSeparator() {
        var d = DefFile.Parse("ui_saber_styles\t0\r\nui_health 1");
        d.Set("ui_health", "2");
        Assert.Equal("ui_saber_styles\t0\r\nui_health 2", d.Serialize());
    }
    [Fact] public void Set_ReplacingValue_KeepsThatLinesSeparator() {
        var d = DefFile.Parse("ui_saber_styles\t\t0");
        d.Set("ui_saber_styles", "7");
        Assert.Equal("ui_saber_styles\t\t7", d.Serialize());
    }
    [Fact] public void Serialize_LfInput_WritesCrlfWithoutFinalNewline() =>
        Assert.Equal("ui_force 1\r\nui_health 2", DefFile.Parse("ui_force 1\nui_health 2\n").Serialize());
    [Fact] public void Get_IsCaseInsensitive_AndMissingIsNull() {
        var d = DefFile.Parse("ui_weaponOne weapon_melee");
        Assert.Equal("weapon_melee", d.Get("UI_WEAPONONE")); Assert.Null(d.Get("ui_force"));
    }
    [Fact] public void Parse_KeyWithoutValue_HasEmptyValue() {
        var d = DefFile.Parse("ui_force"); Assert.Equal("", d.Get("ui_force")); Assert.Equal("ui_force", d.Serialize());
    }
    [Fact] public void Clone_IsIndependent() {
        var a = DefFile.Parse("ui_force 1"); var b = a.Clone(); b.Set("ui_force", "2");
        Assert.Equal("1", a.Get("ui_force"));
    }
    [Fact] public void Set_NewNonIntegerValue_AppendsTabAndIntegerAppendsSpace() {
        var d = new DefFile(); d.Set("ui_force", "5"); d.Set("ui_team", "player");
        Assert.Equal("ui_force 5\r\nui_team\tplayer", d.Serialize());
    }
}
