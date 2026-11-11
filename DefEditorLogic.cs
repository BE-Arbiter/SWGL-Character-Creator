using System.Globalization;

namespace SWGLCharacterCreator;

/// <summary>Display labels, value mapping and "copy from" listing for the Config tab (no UI).</summary>
public static class DefEditorLogic
{
    public const string CustomColor = "Custom…";

    public static readonly string[] PowerLabels =
    {
        "Jump", "Push", "Pull", "Speed", "Sense", "Absorb", "Heal", "Protect", "Mind trick", "Stasis", "Blast",
        "Grasp", "Grip", "Lightning", "Drain", "Rage", "Destruction", "Fear", "Strike",
    };

    public static readonly string[] SaberCombatLabels = { "Throw", "Offense", "Defense" };

    /// <summary>Named + special saber colors, then "Custom…".</summary>
    public static IReadOnlyList<string> SaberColorChoices { get; } =
        DefRules.SaberColorsNamed.Concat(DefRules.SaberColorsSpecial).Append(CustomColor).ToArray();

    /// <summary>What the saber color combo shows: the value itself, or "Custom…" for an x?????? hex value.</summary>
    public static string SaberColorLabel(string? value) =>
        string.IsNullOrEmpty(value) ? "" : DefRules.TryParseHex(value, out _, out _, out _) ? CustomColor : value;

    public static bool IsCustomColor(string? value) =>
        !string.IsNullOrEmpty(value) && DefRules.TryParseHex(value, out _, out _, out _);

    /// <summary>Value to store when "Custom…" is picked: keeps an existing hex value, otherwise starts from white.</summary>
    public static string CustomColorValue(string? current) =>
        IsCustomColor(current) ? current! : DefRules.FormatHex(255, 255, 255);

    public static int ParseInt(string? v, int fallback = 0) =>
        int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    /// <summary>Flips only <paramref name="bit"/> of the numeric value, keeping every other (also unknown) bit.</summary>
    public static string ToggleBit(string? value, int bit) =>
        (ParseInt(value) ^ bit).ToString(CultureInfo.InvariantCulture);

    public static byte ToByte(float f) => (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);

    /// <summary>Every live variant with a def-capable code pair, labelled "CHAR / VARIANT", sorted by label.</summary>
    public static List<(string Label, Block Variant)> CopySources(IEnumerable<ConfigFile> chaFiles)
    {
        var list = new List<(string, Block)>();
        foreach (var cha in chaFiles)
            foreach (var ch in cha.Blocks)
            {
                var chCode = ch.Get("code");
                if (string.IsNullOrEmpty(chCode)) continue;
                foreach (var v in ch.Children)
                {
                    var vCode = v.Get("code");
                    if (v.Type != "Variant" || string.IsNullOrEmpty(vCode)) continue;
                    list.Add(($"{chCode} / {vCode}", v));
                }
            }
        return list.OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IEnumerable<(string Label, Block Variant)> FilterSources(
        IEnumerable<(string Label, Block Variant)> sources, string filter) =>
        filter.Length == 0 ? sources : sources.Where(s => s.Label.Contains(filter, StringComparison.OrdinalIgnoreCase));
}
