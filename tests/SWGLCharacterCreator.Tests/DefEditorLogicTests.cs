using SWGLCharacterCreator;
using Xunit;

namespace SWGLCharacterCreator.Tests;

public class DefEditorLogicTests
{
    [Fact]
    public void SaberColorLabel_maps_hex_to_custom_and_keeps_others()
    {
        Assert.Equal(DefEditorLogic.CustomColor, DefEditorLogic.SaberColorLabel("xFF00AA"));
        Assert.Equal("blue", DefEditorLogic.SaberColorLabel("blue"));
        Assert.Equal("weird", DefEditorLogic.SaberColorLabel("weird"));
        Assert.Equal("", DefEditorLogic.SaberColorLabel(null));
    }

    [Fact]
    public void CustomColorValue_keeps_hex_or_starts_white()
    {
        Assert.Equal("x112233", DefEditorLogic.CustomColorValue("x112233"));
        Assert.Equal("xFFFFFF", DefEditorLogic.CustomColorValue("blue"));
    }

    [Theory]
    [InlineData("7", 2, "5")]
    [InlineData("5", 2, "7")]
    [InlineData("0", 64, "64")]
    [InlineData("129", 1, "128")]   // unknown bit 128 preserved
    [InlineData(null, 4, "4")]
    public void ToggleBit_flips_only_that_bit(string? value, int bit, string expected) =>
        Assert.Equal(expected, DefEditorLogic.ToggleBit(value, bit));

    [Fact]
    public void ToByte_rounds_and_clamps()
    {
        Assert.Equal(255, DefEditorLogic.ToByte(1f));
        Assert.Equal(0, DefEditorLogic.ToByte(-1f));
        Assert.Equal(128, DefEditorLogic.ToByte(0.5f));
    }

    [Fact]
    public void CopySources_lists_coded_variants_sorted_and_filters()
    {
        var f = ConfigFile.Parse("{\ncode B\nVariant\n{\ncode X\n}\n}\n{\ncode A\nVariant\n{\ncode DEFAULT\n}\nVariant\n{\nname nocode\n}\n}\n{\nname nochar\nVariant\n{\ncode Z\n}\n}\n");
        var list = DefEditorLogic.CopySources(new[] { f });
        Assert.Equal(new[] { "A / DEFAULT", "B / X" }, list.Select(l => l.Label));
        Assert.Single(DefEditorLogic.FilterSources(list, "def"));
        Assert.Equal(2, DefEditorLogic.FilterSources(list, "").Count());
    }
}
