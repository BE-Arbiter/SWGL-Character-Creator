using System.Text.RegularExpressions;

namespace SWGLCharacterCreator;

/// <summary>One "REFERENCE x / LANG_xxx "text"" entry of a .str file. Props[0] is the REFERENCE line.</summary>
public sealed class StrEntry
{
    public string Reference = "";
    public List<Prop> Props = new();
    public int LastLine = -1;   // last source line of the entry (-1 for entries added in memory)

    public IEnumerable<Prop> Languages =>
        Props.Where(p => p.Key.StartsWith("LANG_", StringComparison.OrdinalIgnoreCase));

    public Prop? Language(string lang) =>
        Languages.FirstOrDefault(p => p.Key.Equals("LANG_" + lang, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A parsed strings/&lt;language&gt;/*.str file (translation table). Saving produces an exact copy of the
/// source text in which only edited LANG_ lines change and added languages / entries are inserted.
/// </summary>
public sealed class StrFile
{
    public static readonly string[] KnownLanguages =
        { "ENGLISH", "FRENCH", "GERMAN", "SPANISH", "ITALIAN", "PORTUGUESE", "POLISH", "RUSSIAN" };

    private static readonly Regex QuotedLine = new("^(\\s*\\S+\\s+\")(.*)(\"\\s*)$", RegexOptions.Compiled);

    public required Pk3Entry Entry { get; init; }
    public List<StrEntry> Entries = new();
    public bool Dirty;
    public string? Error;
    private string[] _lines = Array.Empty<string>();
    private bool _crlf;

    public StrEntry? Find(string reference) =>
        Entries.FirstOrDefault(e => string.Equals(e.Reference, reference, StringComparison.OrdinalIgnoreCase));

    public static StrFile Load(Pk3Entry entry)
    {
        var f = new StrFile { Entry = entry };
        try { f.Parse(Pk3Reader.ReadText(entry)); }
        catch (Exception ex) { f.Error = ex.Message; }
        return f;
    }

    /// <summary>Adds a new entry with a single quoted language line.</summary>
    public StrEntry AddEntry(string reference, string language, string text)
    {
        var e = new StrEntry { Reference = reference };
        e.Props.Add(new Prop { Key = "REFERENCE", Value = reference });
        e.Props.Add(new Prop { Key = "LANG_" + language.ToUpperInvariant(), Value = text, Quoted = true });
        Entries.Add(e);
        Dirty = true;
        return e;
    }

    public Prop AddLanguage(StrEntry entry, string language, string text)
    {
        var p = new Prop { Key = "LANG_" + language.ToUpperInvariant(), Value = text, Quoted = true };
        entry.Props.Add(p);
        Dirty = true;
        return p;
    }

    private void Parse(string text)
    {
        _lines = text.Split('\n'); // lines keep their '\r' so that line endings survive a save
        _crlf = _lines.Length > 0 && _lines[0].EndsWith('\r');
        StrEntry? cur = null;
        for (int i = 0; i < _lines.Length; i++)
        {
            var line = _lines[i].Trim();
            if (line.Length == 0) continue;

            int sp = line.IndexOfAny(new[] { ' ', '\t' });
            var key = sp < 0 ? line : line[..sp];
            var val = sp < 0 ? "" : line[sp..].Trim();

            if (key.Equals("REFERENCE", StringComparison.OrdinalIgnoreCase))
            {
                cur = new StrEntry { Reference = val, LastLine = i };
                cur.Props.Add(new Prop { Key = key, Value = val, Original = val, Line = i });
                Entries.Add(cur);
                continue;
            }
            if (cur == null) continue; // header

            var p = new Prop { Key = key, Value = val, Line = i };
            if (val.Length >= 2 && val[0] == '"' && val[^1] == '"') { p.Value = val[1..^1]; p.Quoted = true; }
            p.Original = p.Value;
            cur.Props.Add(p);
            cur.LastLine = i;
        }
    }

    public string Serialize()
    {
        var eol = _crlf ? "\r" : "";
        var lines = (string[])_lines.Clone();
        var inserts = new Dictionary<int, List<string>>();
        var appended = new List<string>();

        foreach (var e in Entries)
        {
            if (e.LastLine < 0 && e.Props.Count > 0 && e.Props[0].Line < 0)
            {
                // Entry added in memory: appended at the end of the file.
                appended.Add(eol);
                foreach (var p in e.Props) appended.Add(FormatLine(p) + eol);
                continue;
            }
            foreach (var p in e.Props)
            {
                if (p.Line >= 0)
                {
                    if (p.Value == p.Original || !p.Quoted) continue;
                    var m = QuotedLine.Match(lines[p.Line]);
                    if (m.Success) lines[p.Line] = m.Groups[1].Value + p.Value + m.Groups[3].Value;
                }
                else
                {
                    // Language added to an existing entry: inserted right after its last line.
                    if (!inserts.TryGetValue(e.LastLine, out var list)) inserts[e.LastLine] = list = new();
                    list.Add(FormatLine(p) + eol);
                }
            }
        }

        var result = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            result.Add(lines[i]);
            if (inserts.TryGetValue(i, out var add)) result.AddRange(add);
        }
        if (appended.Count > 0)
        {
            bool trailingEmpty = result.Count > 0 && result[^1].Length == 0;
            if (trailingEmpty) result.RemoveAt(result.Count - 1);
            result.AddRange(appended);
            if (trailingEmpty) result.Add("");
        }
        return string.Join('\n', result);
    }

    private static string FormatLine(Prop p) =>
        p.Key + "\t\t" + (p.Key.StartsWith("LANG_", StringComparison.OrdinalIgnoreCase) || p.Quoted ? "\"" + p.Value + "\"" : p.Value);
}
