namespace SWGLCharacterCreator;

/// <summary>One "key value" line of a _def.cfg. Untouched lines serialize to their exact original text.</summary>
public sealed class DefLine
{
    public string Key = "";
    public string Value = "";

    // Original source text and the parsed values it corresponds to; used only while Key/Value are unchanged.
    internal string? Raw;
    internal string RawKey = "";
    internal string RawValue = "";
    internal string Separator = "";   // exact whitespace between key and value, as read

    internal string Text() => Raw != null && Key == RawKey && Value == RawValue
        ? Raw
        : Key + (Separator.Length > 0 ? Separator : DefFile.DefaultSeparator(Value)) + Value;
}

/// <summary>Canonical key lists of a _def.cfg.</summary>
public static class DefKeys
{
    public static readonly string[] Powers =
    {
        "ui_jump_level", "ui_push_level", "ui_pull_level", "ui_speed_level", "ui_sense_level", "ui_absorb_level",
        "ui_heal_level", "ui_protect_level", "ui_mindtrick_level", "ui_stasis_level", "ui_blast_level",
        "ui_grasp_level", "ui_grip_level", "ui_lightning_level", "ui_drain_level", "ui_rage_level",
        "ui_destruction_level", "ui_fear_level", "ui_strike_level",
    };

    public static readonly string[] SaberCombat = { "ui_sabthrow_level", "ui_saboff_level", "ui_sabdef_level" };

    public static readonly string[] WeaponSlots =
    {
        "ui_weaponOne", "ui_weaponTwo", "ui_weaponThree", "ui_weaponFour", "ui_weaponFive", "ui_weaponSix",
    };

    public static readonly string[] Order = Powers.Concat(SaberCombat).Concat(new[]
    {
        "ui_health", "ui_team", "ui_char_color_red", "ui_char_color_green", "ui_char_color_blue", "ui_npc_weapon",
    }).Concat(WeaponSlots).Concat(new[]
    {
        "ui_lightning_color", "ui_saber", "ui_saber_color", "ui_saber2", "ui_saber2_color", "ui_saber_styles",
        "ui_npc_type", "ui_force",
    }).ToArray();
}

/// <summary>
/// A parsed ext_data/characters_conf/*_def.cfg. Unchanged lines (including their separators and unknown keys)
/// are written back exactly; Serialize joins lines with CRLF and writes no final newline.
/// </summary>
public sealed class DefFile
{
    public List<DefLine> Lines = new();

    public static DefFile Parse(string text)
    {
        var d = new DefFile();
        foreach (var raw in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            if (raw.Trim().Length == 0) continue;
            var s = raw.TrimStart();
            int i = 0;
            while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
            int j = i;
            while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
            var key = s[..i];
            var value = s[j..].Trim();
            d.Lines.Add(new DefLine
            {
                Key = key, Value = value, Raw = raw, RawKey = key, RawValue = value,
                Separator = value.Length > 0 ? s[i..j] : "",
            });
        }
        return d;
    }

    public string Serialize() => string.Join("\r\n", Lines.Select(l => l.Text()));

    public string? Get(string key) => Find(key)?.Value;

    public void Set(string key, string value)
    {
        var line = Find(key);
        if (value.Length == 0) { if (line != null) Lines.Remove(line); return; }
        if (line != null) { line.Value = value; return; }
        var canonical = DefKeys.Order.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
        Lines.Add(new DefLine { Key = canonical, Value = value });
    }

    public DefFile Clone()
    {
        var c = new DefFile();
        foreach (var l in Lines)
            c.Lines.Add(new DefLine { Key = l.Key, Value = l.Value, Raw = l.Raw, RawKey = l.RawKey, RawValue = l.RawValue, Separator = l.Separator });
        return c;
    }

    /// <summary>A new variant's file: the values of 0_0_0_DEFAULT_def.cfg, in canonical order, without ui_npc_type.</summary>
    public static DefFile CreateEmpty()
    {
        var d = new DefFile();
        foreach (var key in DefKeys.Order)
        {
            var v = key switch
            {
                "ui_npc_type" => null,
                "ui_health" => "200",
                "ui_team" => "neutral",
                "ui_char_color_red" or "ui_char_color_green" or "ui_char_color_blue" => "255",
                "ui_npc_weapon" => "weapon_none",
                "ui_weaponOne" => "weapon_melee",
                "ui_lightning_color" or "ui_saber_color" => "blue",
                "ui_saber" or "ui_saber2" => "empty",
                "ui_saber2_color" => "red",
                _ when key.StartsWith("ui_weapon", StringComparison.Ordinal) => "weapon_none",
                _ => "0",   // powers, saber combat, ui_saber_styles, ui_force
            };
            if (v != null) d.Lines.Add(new DefLine { Key = key, Value = v });
        }
        return d;
    }

    /// <summary>Separator for lines the editor writes: a space before an integer value, a tab otherwise.</summary>
    internal static string DefaultSeparator(string value) =>
        value.Length > 0 && value.All(char.IsAsciiDigit) ? " " : "\t";

    private DefLine? Find(string key) =>
        Lines.FirstOrDefault(l => l.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}
