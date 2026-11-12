using SWGLCharacterCreator;
using Xunit;

namespace SWGLCharacterCreator.Tests;

public class ChangeSummaryTests
{
    private const string Cha =
        "{\ncode AAYLA\nname \"Aayla\"\nicon a.png\nVariant\n{\ncode DEFAULT\nskin model_default\n}\n}\n";

    private static List<string> Texts(List<Change>? c) => c!.Select(x => x.Text).ToList();

    private static List<string> Cha2(string newText) => Texts(ChangeSummary.Describe("ext_data/characters/x.cha", Cha, newText));

    /// <summary>Applies the edit to a live copy, reverts every change, and checks the result equals the original.</summary>
    private static void AssertRevertsAll(string newText)
    {
        var live = ConfigFile.Parse(newText);
        var changes = ChangeSummary.Config(ConfigFile.Parse(Cha), live, cha: true);
        Assert.NotEmpty(changes);
        foreach (var c in changes) { Assert.NotNull(c.Revert); c.Revert!(); }
        Assert.Equal(ConfigFile.Parse(Cha).Serialize(), live.Serialize());
    }

    [Fact] public void Unchanged_Empty() => Assert.Empty(Cha2(Cha));

    [Fact] public void CharacterField()
    {
        Assert.Equal(new[] { "Character AAYLA (Aayla): icon \"a.png\" -> \"b.png\"" }, Cha2(Cha.Replace("icon a.png", "icon b.png")));
    }

    [Fact] public void VariantField()
    {
        Assert.Equal(new[] { "Character AAYLA (Aayla): Variant DEFAULT: skin \"model_default\" -> \"blue\"" },
            Cha2(Cha.Replace("skin model_default", "skin blue")));
    }

    [Fact] public void AddedVariant()
    {
        Assert.Equal(new[] { "Character AAYLA (Aayla): Added variant ALT" },
            Cha2(Cha.Replace("}\n}\n", "}\nVariant\n{\ncode ALT\n}\n}\n")));
    }

    [Fact] public void RemovedVariant()
    {
        Assert.Equal(new[] { "Character AAYLA (Aayla): Removed variant DEFAULT" },
            Cha2("{\ncode AAYLA\nname \"Aayla\"\nicon a.png\n}\n"));
    }

    [Fact] public void RenamedVariant()
    {
        Assert.Equal(new[] { "Character AAYLA (Aayla): Variant MAIN: code \"DEFAULT\" -> \"MAIN\"" },
            Cha2(Cha.Replace("code DEFAULT", "code MAIN")));
    }

    [Fact] public void AddedAndRemovedField()
    {
        var lines = Cha2(Cha.Replace("icon a.png\n", "specie \"twilek\"\n"));
        Assert.Contains("Character AAYLA (Aayla): added specie = \"twilek\"", lines);
        Assert.Contains("Character AAYLA (Aayla): removed icon (was \"a.png\")", lines);
    }

    [Fact] public void NewFile_AddedCharacter()
    {
        Assert.Equal(new[] { "Added character AAYLA (Aayla) with 1 variant(s)" },
            Texts(ChangeSummary.Describe("ext_data/characters/x.cha", null, Cha)));
    }

    [Fact] public void Faction()
    {
        Assert.Equal(new[] { "Faction JEDI: icon \"a\" -> \"b\"" },
            Texts(ChangeSummary.Describe("ext_data/characters/x.fac", "Faction\n{\ncode JEDI\nicon a\n}\n", "Faction\n{\ncode JEDI\nicon b\n}\n")));
    }

    [Fact] public void DefConfig()
    {
        Assert.Equal(new[] { "Config of AAYLA DEFAULT: ui_force 1 -> 3" },
            Texts(ChangeSummary.Describe("ext_data/characters_conf/AAYLA_DEFAULT_def.cfg", "ui_force 1\r\nui_health 200", "ui_force 3\r\nui_health 200")));
    }

    [Fact] public void Str()
    {
        const string old = "VERSION 1\nREFERENCE TOR\nLANG_ENGLISH \"Old\"\n";
        Assert.Equal(new[] { "Translation TOR: ENGLISH \"Old\" -> \"New\"" },
            Texts(ChangeSummary.Describe("strings/english/swgl_factions.str", old, old.Replace("\"Old\"", "\"New\""))));
    }

    [Fact] public void OtherFile_Null() => Assert.Null(ChangeSummary.Describe("gfx/a.png", null, ""));

    [Fact] public void Revert_Field() => AssertRevertsAll(Cha.Replace("icon a.png", "icon b.png"));
    [Fact] public void Revert_VariantField() => AssertRevertsAll(Cha.Replace("skin model_default", "skin blue"));
    [Fact] public void Revert_AddedVariant() => AssertRevertsAll(Cha.Replace("}\n}\n", "}\nVariant\n{\ncode ALT\n}\n}\n"));
    [Fact] public void Revert_RemovedVariant() => AssertRevertsAll("{\ncode AAYLA\nname \"Aayla\"\nicon a.png\n}\n");
    [Fact] public void Revert_RenamedVariant() => AssertRevertsAll(Cha.Replace("code DEFAULT", "code MAIN"));
    [Fact] public void Revert_AddedAndRemovedField() => AssertRevertsAll(Cha.Replace("icon a.png\n", "specie \"twilek\"\n"));

    [Fact] public void Revert_OneOfTwo_KeepsOther()
    {
        var live = ConfigFile.Parse(Cha.Replace("icon a.png", "icon b.png").Replace("skin model_default", "skin blue"));
        var changes = ChangeSummary.Config(ConfigFile.Parse(Cha), live, cha: true);
        changes.Single(c => c.Text.Contains("icon")).Revert!();
        Assert.Equal(new[] { "Character AAYLA (Aayla): Variant DEFAULT: skin \"model_default\" -> \"blue\"" },
            ChangeSummary.Config(ConfigFile.Parse(Cha), live, cha: true).Select(c => c.Text));
    }

    [Fact] public void AddedCharacter_ImpactNamesVariants()
    {
        var changes = ChangeSummary.Config(new ConfigFile(), ConfigFile.Parse(Cha), cha: true);
        Assert.Contains("1 variant(s)", changes.Single().Impact);
    }

    [Fact] public void Revert_DefValue()
    {
        var live = DefFile.Parse("ui_force 3\r\nui_health 200");
        var changes = ChangeSummary.Def("x/AAYLA_DEFAULT_def.cfg", DefFile.Parse("ui_force 1\r\nui_health 200"), live);
        changes.Single().Revert!();
        Assert.Equal("1", live.Get("ui_force"));
    }

    [Fact] public void Revert_StrValueAndAddedEntry()
    {
        const string text = "VERSION 1\nREFERENCE TOR\nLANG_ENGLISH \"Old\"\n";
        var live = ChangeSummary.ParseStr("a.str", text);
        live.Find("TOR")!.Language("ENGLISH")!.Value = "New";
        live.AddEntry("SITH", "ENGLISH", "Sith");
        var changes = ChangeSummary.Str(ChangeSummary.ParseStr("a.str", text), live);
        Assert.Equal(2, changes.Count);
        foreach (var c in changes) c.Revert!();
        Assert.Equal(text, live.Serialize());
    }
}
