using System.Text;

namespace SWGLCharacterCreator;

public sealed record WeaponDef(string Name, bool PlayerUsable);

/// <summary>One top-level { } block of a game data text file; Props holds depth-1 "key value" pairs only.</summary>
public sealed record TextBlock(string? Name, Dictionary<string, string> Props);

/// <summary>Weapons, sabers and NPC types defined by the game data (pk3 contents), for validating _def.cfg values.</summary>
public sealed class GameDataCatalog
{
    private readonly Dictionary<string, WeaponDef> _weapons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sabers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _npcs = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<WeaponDef> Weapons { get; private set; } = Array.Empty<WeaponDef>();
    public IReadOnlyList<string> Sabers { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> NpcTypes { get; private set; } = Array.Empty<string>();

    public WeaponDef? FindWeapon(string name) => _weapons.GetValueOrDefault(name);
    public string? FindSaber(string name) => _sabers.GetValueOrDefault(name);
    public string? FindNpc(string name) => _npcs.GetValueOrDefault(name);

    public static GameDataCatalog Build(Pk3Index index)
    {
        var c = new GameDataCatalog();
        c._weapons["weapon_none"] = new WeaponDef("weapon_none", true);

        var weaponFiles = new List<Pk3Entry>();
        if (index.Files.TryGetValue("ext_data/weapons.dat", out var dat)) weaponFiles.Add(dat);
        weaponFiles.AddRange(index.Find("ext_data", ".wpn"));
        foreach (var e in weaponFiles)
            foreach (var b in Blocks(e))
                if (b.Props.TryGetValue("weaponclass", out var name) && name.Length > 0)
                {
                    bool usable = !(b.Props.TryGetValue("playerUsable", out var pu) && pu.Trim() == "0");
                    c._weapons[name] = new WeaponDef(name, usable);
                }

        foreach (var e in index.Find("ext_data/sabers", ".sab"))
            foreach (var b in Blocks(e)) if (!string.IsNullOrEmpty(b.Name)) c._sabers[b.Name] = b.Name;
        foreach (var e in index.Find("ext_data/npcs", ".npc"))
            foreach (var b in Blocks(e)) if (!string.IsNullOrEmpty(b.Name)) c._npcs[b.Name] = b.Name;

        c.Weapons = c._weapons.Values.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();
        c.Sabers = c._sabers.Values.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        c.NpcTypes = c._npcs.Values.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        return c;
    }

    private static List<TextBlock> Blocks(Pk3Entry e)
    {
        if (e.Size == 0) return new();
        try { return ParseBlocks(Pk3Reader.ReadText(e)); }
        catch { return new(); }   // an unreadable file must not hide the rest of the catalog
    }

    /// <summary>Tolerant parser: skips // and /* */ comments, handles quoted strings and nested blocks (ignored).</summary>
    public static List<TextBlock> ParseBlocks(string text)
    {
        var toks = Tokenize(text);
        var blocks = new List<TextBlock>();
        string? last = null;
        int i = 0;
        while (i < toks.Count)
        {
            var (t, quoted) = toks[i];
            if (!quoted && t == "{")
            {
                i++;
                var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int depth = 1;
                while (i < toks.Count && depth > 0)
                {
                    var (k, kq) = toks[i];
                    if (!kq && k == "{") { depth++; i++; continue; }
                    if (!kq && k == "}") { depth--; i++; continue; }
                    i++;
                    if (depth != 1) continue;
                    if (i < toks.Count && (toks[i].Quoted || (toks[i].Text != "{" && toks[i].Text != "}")))
                    {
                        props[k] = toks[i].Text;
                        i++;
                    }
                }
                blocks.Add(new TextBlock(last, props));
                last = null;
            }
            else
            {
                if (quoted || t != "}") last = t;
                i++;
            }
        }
        return blocks;
    }

    private static List<(string Text, bool Quoted)> Tokenize(string s)
    {
        var list = new List<(string, bool)>();
        int i = 0;
        while (i < s.Length)
        {
            char ch = s[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            if (ch == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n' && s[i] != '\r') i++;
                continue;
            }
            if (ch == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 2;
                continue;
            }
            if (ch == '{' || ch == '}') { list.Add((ch.ToString(), false)); i++; continue; }
            if (ch == '"')
            {
                int end = s.IndexOf('"', i + 1);
                if (end < 0) end = s.Length;
                list.Add((s.Substring(i + 1, end - i - 1), true));
                i = Math.Min(end + 1, s.Length);
                continue;
            }
            var sb = new StringBuilder();
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}' && s[i] != '"'
                   && !(s[i] == '/' && i + 1 < s.Length && (s[i + 1] == '/' || s[i + 1] == '*')))
                sb.Append(s[i++]);
            list.Add((sb.ToString(), false));
        }
        return list;
    }
}
