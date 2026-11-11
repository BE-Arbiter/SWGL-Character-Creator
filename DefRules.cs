using System.Globalization;

namespace SWGLCharacterCreator;

/// <summary>Allowed values of a _def.cfg and the validation of a file against the game data.</summary>
public static class DefRules
{
    public static readonly string[] Teams = { "player", "enemy", "free", "neutral" };
    public static readonly string[] SaberColorsNamed = { "blue", "red", "green", "purple", "yellow", "orange", "black" };
    public static readonly string[] SaberColorsSpecial = { "random", "prequel_random", "swtor_sith_random", "unstable_red", "darksaber" };
    public static readonly string[] LightningColors = { "blue", "purple", "green", "black", "red", "yellow", "white", "orange" };
    public static readonly (string Name, int Bit)[] SaberStyles =
        { ("Fast", 1), ("Medium", 2), ("Strong", 4), ("Desann", 8), ("Tavion", 16), ("Dual", 32), ("Staff", 64) };

    /// <summary>Exactly "x" followed by 6 hex digits (either case).</summary>
    public static bool TryParseHex(string v, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (v.Length != 7 || (v[0] != 'x' && v[0] != 'X')) return false;
        return byte.TryParse(v.AsSpan(1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(v.AsSpan(3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(v.AsSpan(5, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out b);
    }

    public static string FormatHex(byte r, byte g, byte b) => $"x{r:X2}{g:X2}{b:X2}";

    public static List<string> Validate(DefFile def, GameDataCatalog cat)
    {
        var m = new List<string>();

        foreach (var key in DefKeys.Powers.Concat(DefKeys.SaberCombat))
            if (Number(def, key, m) is int n && (n < 0 || n > 3)) m.Add($"{key}: {n} is out of range (0-3)");
        if (Number(def, "ui_health", m) is int h && h < 1) m.Add($"ui_health: {h} is out of range (min 1)");
        if (Number(def, "ui_force", m) is int f && f < 0) m.Add($"ui_force: {f} is out of range (min 0)");
        foreach (var key in new[] { "ui_char_color_red", "ui_char_color_green", "ui_char_color_blue" })
            if (Number(def, key, m) is int n && (n < 0 || n > 255)) m.Add($"{key}: {n} is out of range (0-255)");
        Number(def, "ui_saber_styles", m);

        foreach (var key in DefKeys.WeaponSlots.Prepend("ui_npc_weapon"))
        {
            var v = def.Get(key);
            if (string.IsNullOrEmpty(v) || v == "\"\"") continue;
            var w = cat.FindWeapon(v);
            if (w == null) m.Add($"{key}: unknown weapon \"{v}\"");
            else if (w.Name != v) m.Add($"{key}: weapon \"{v}\" is defined as \"{w.Name}\"");
        }

        foreach (var key in new[] { "ui_saber", "ui_saber2" })
        {
            var v = def.Get(key);
            if (string.IsNullOrEmpty(v) || v == "\"\"" || v.Equals("empty", StringComparison.OrdinalIgnoreCase)) continue;
            if (cat.FindSaber(v) == null) m.Add($"{key}: unknown saber \"{v}\"");
        }

        var npc = def.Get("ui_npc_type");
        if (!string.IsNullOrEmpty(npc) && npc != "\"\"" && cat.FindNpc(npc) == null)
            m.Add($"ui_npc_type: unknown NPC type \"{npc}\"");

        foreach (var key in new[] { "ui_saber_color", "ui_saber2_color" })
        {
            var v = def.Get(key);
            if (string.IsNullOrEmpty(v)) continue;
            if (!SaberColorsNamed.Concat(SaberColorsSpecial).Contains(v, StringComparer.OrdinalIgnoreCase)
                && !TryParseHex(v, out _, out _, out _))
                m.Add($"{key}: invalid color \"{v}\"");
        }

        var lc = def.Get("ui_lightning_color");
        if (!string.IsNullOrEmpty(lc) && !LightningColors.Contains(lc, StringComparer.OrdinalIgnoreCase))
            m.Add($"ui_lightning_color: unknown lightning color \"{lc}\"");

        return m;
    }

    // Parses a numeric value; reports '"X" is not a number' and returns null when it is not one. Absent/empty is fine.
    private static int? Number(DefFile def, string key, List<string> m)
    {
        var v = def.Get(key);
        if (string.IsNullOrEmpty(v)) return null;
        if (int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) return n;
        m.Add($"{key}: \"{v}\" is not a number");
        return null;
    }
}
