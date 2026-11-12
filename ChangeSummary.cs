namespace SWGLCharacterCreator;

/// <summary>
/// One readable change ("Character AAYLA: icon "a" -> "b"") for the review-changes screen.
/// <see cref="Revert"/> undoes it on the live in-memory object (null when it cannot be undone from here);
/// <see cref="Impact"/> says what else the revert does, shown in the confirmation.
/// </summary>
public sealed record Change(string Text, string? Impact, Action? Revert);

/// <summary>
/// Compares the saved version of a file with its live (edited) version and lists the changes, each revertible
/// on the live object. Returns null from <see cref="Describe"/> for file types it does not understand.
/// </summary>
public static class ChangeSummary
{
    /// <summary>Read-only list (no reverts) from two texts.</summary>
    public static List<Change>? Describe(string path, string? oldText, string newText)
    {
        List<Change>? list = null;
        if (IsConfig(path))
            list = Config(ConfigFile.Parse(oldText ?? ""), ConfigFile.Parse(newText), IsCha(path));
        else if (IsDef(path))
            list = Def(path, oldText == null ? null : DefFile.Parse(oldText), DefFile.Parse(newText));
        else if (IsStr(path))
            list = Str(ParseStr(path, oldText ?? ""), ParseStr(path, newText));
        return list?.Select(c => c with { Revert = null, Impact = null }).ToList();
    }

    public static bool IsCha(string path) => EndsWith(path, ".cha");
    public static bool IsConfig(string path) => EndsWith(path, ".fac") || IsCha(path);
    public static bool IsDef(string path) => EndsWith(path, "_def.cfg");
    public static bool IsStr(string path) => EndsWith(path, ".str");

    private static bool EndsWith(string s, string suffix) => s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    private static string Q(string v) => "\"" + v + "\"";

    public static StrFile ParseStr(string path, string text)
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes(text);
        return StrFile.Load(new Pk3Entry(path, "", "", bytes.Length, "", path, Memory: bytes));
    }

    // ---------------------------------------------------------------- .fac / .cha

    private static string Kind(Block b, bool cha) =>
        b.Type == "Variant" ? "variant" : b.Type.Length > 0 ? b.Type.ToLowerInvariant() : cha ? "character" : "block";

    private static string Name(Block b, bool cha)
    {
        var code = b.Get("code");
        var name = b.Get("name");
        var id = string.IsNullOrEmpty(code) ? "(no code)" : code;
        return $"{Kind(b, cha)} {id}" + (!string.IsNullOrEmpty(name) && name != code ? $" ({name})" : "");
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static int VariantCount(Block b) => b.Children.Count(c => c.Type == "Variant");

    /// <summary>Changes from <paramref name="old"/> to <paramref name="live"/>; reverts edit <paramref name="live"/>.</summary>
    public static List<Change> Config(ConfigFile old, ConfigFile live, bool cha)
    {
        var lines = new List<Change>();
        Blocks(old.Blocks, live.Blocks, "", cha, lines);
        return lines;
    }

    /// <summary>Pairs blocks by code (then leftovers in order, as renames) and reports additions, removals and edits.</summary>
    private static void Blocks(List<Block> olds, List<Block> lives, string prefix, bool cha, List<Change> lines)
    {
        var leftOld = olds.ToList();
        var pairs = new List<(Block? o, Block? n)>();
        var unmatched = new List<Block>();
        foreach (var n in lives)
        {
            var code = n.Get("code");
            var o = code == null ? null : leftOld.FirstOrDefault(x => string.Equals(x.Get("code"), code, StringComparison.OrdinalIgnoreCase) && x.Type == n.Type);
            if (o != null) { leftOld.Remove(o); pairs.Add((o, n)); }
            else unmatched.Add(n);
        }
        // Leftovers of the same type at both sides: a code change (rename) rather than remove + add.
        foreach (var n in unmatched)
        {
            var o = leftOld.FirstOrDefault(x => x.Type == n.Type);
            if (o != null) { leftOld.Remove(o); pairs.Add((o, n)); }
            else pairs.Add((null, n));
        }
        foreach (var o in leftOld) pairs.Add((o, null));

        foreach (var (o, n) in pairs)
        {
            if (o == null)
            {
                var added = n!;
                int v = VariantCount(added);
                lines.Add(new Change(
                    $"{prefix}Added {Name(added, cha)}" + (v > 0 ? $" with {v} variant(s)" : ""),
                    AddedImpact(added, cha),
                    () => lives.Remove(added)));
                continue;
            }
            if (n == null)
            {
                var removed = o;
                int at = olds.IndexOf(removed);
                lines.Add(new Change(
                    $"{prefix}Removed {Name(removed, cha)}",
                    RemovedImpact(removed, cha),
                    () => lives.Insert(Math.Min(at, lives.Count), removed)));
                continue;
            }

            var label = Cap(Name(n, cha));
            var oc = o.Get("code") ?? "";
            var nc = n.Get("code") ?? "";
            if (!oc.Equals(nc, StringComparison.Ordinal))
            {
                var live = n;
                lines.Add(new Change($"{prefix}{label}: code {Q(oc)} -> {Q(nc)}", CodeImpact(n, oc),
                    () => SetProp(live, "code", oc, false)));
            }
            Props(o, n, $"{prefix}{label}: ", lines);
            Blocks(o.Children, n.Children, $"{prefix}{label}: ", cha, lines);
        }
    }

    private static string AddedImpact(Block b, bool cha)
    {
        if (b.Type == "Variant") return $"Removes variant {b.Get("code")}, with its unsaved config changes.";
        if (b.Type == "Faction") return $"Removes faction {b.Get("code")}. Characters that list it keep the code in their factions field.";
        int v = VariantCount(b);
        return cha
            ? $"Removes character {b.Get("code")}" + (v > 0 ? $" and its {v} variant(s), with their unsaved config changes." : ".")
            : $"Removes {Kind(b, cha)} {b.Get("code")}.";
    }

    private static string RemovedImpact(Block b, bool cha)
    {
        if (b.Type == "Variant") return $"Restores variant {b.Get("code")} as saved, with its saved config.";
        int v = VariantCount(b);
        return $"Restores {Kind(b, cha)} {b.Get("code")} as saved" + (v > 0 ? $", with its {v} variant(s) and their saved configs." : ".");
    }

    private static string? CodeImpact(Block b, string oldCode)
    {
        if (b.Type == "Variant") return $"Its _def.cfg is written under the code {oldCode} again.";
        int v = VariantCount(b);
        return v > 0 ? $"The _def.cfg of its {v} variant(s) follow the code back to {oldCode}." : null;
    }

    private static void SetProp(Block b, string key, string value, bool quoted)
    {
        var p = b.Props.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (p == null) b.Props.Insert(0, new Prop { Key = key, Value = value, Quoted = quoted });
        else p.Value = value;
    }

    private static void Props(Block oldBlock, Block liveBlock, string prefix, List<Change> lines)
    {
        static bool Field(Prop p) => !p.IsComment && !p.Key.Equals("code", StringComparison.OrdinalIgnoreCase);
        var left = oldBlock.Props.Where(Field).ToList();
        foreach (var p in liveBlock.Props.Where(Field).ToList())
        {
            var match = left.FirstOrDefault(x => x.Key.Equals(p.Key, StringComparison.OrdinalIgnoreCase));
            var live = p;
            if (match == null)
            {
                lines.Add(new Change($"{prefix}added {p.Key} = {Q(p.Value)}", null, () => liveBlock.Props.Remove(live)));
                continue;
            }
            left.Remove(match);
            if (match.Value != p.Value)
            {
                var before = match;
                lines.Add(new Change($"{prefix}{p.Key} {Q(match.Value)} -> {Q(p.Value)}", null,
                    () => { live.Value = before.Value; live.Quoted = before.Quoted; }));
            }
        }
        foreach (var gone in left)
        {
            int at = oldBlock.Props.IndexOf(gone);
            var restored = gone;
            lines.Add(new Change($"{prefix}removed {gone.Key} (was {Q(gone.Value)})", null,
                () => liveBlock.Props.Insert(Math.Min(at, liveBlock.Props.Count),
                    new Prop { Key = restored.Key, Value = restored.Value, Quoted = restored.Quoted })));
        }
    }

    // ---------------------------------------------------------------- _def.cfg

    /// <summary>Changes of a variant config; reverts edit <paramref name="live"/>. A null <paramref name="old"/> is a new file.</summary>
    public static List<Change> Def(string path, DefFile? old, DefFile live)
    {
        var file = System.IO.Path.GetFileName(path);
        var who = file.Length > "_def.cfg".Length ? file[..^"_def.cfg".Length].Replace('_', ' ') : file;
        if (old == null) return new List<Change> { new($"Config of {who}: created", null, null) };
        var lines = new List<Change>();
        foreach (var l in live.Lines.ToList())
        {
            var before = old.Get(l.Key);
            var key = l.Key;
            if (before == null)
                lines.Add(new Change($"Config of {who}: added {l.Key} = {l.Value}", null, () => live.Set(key, "")));
            else if (before != l.Value)
                lines.Add(new Change($"Config of {who}: {l.Key} {before} -> {l.Value}", null, () => live.Set(key, before)));
        }
        foreach (var l in old.Lines.Where(l => live.Get(l.Key) == null))
        {
            var (key, value) = (l.Key, l.Value);
            lines.Add(new Change($"Config of {who}: removed {l.Key} (was {l.Value})", null, () => live.Set(key, value)));
        }
        return lines;
    }

    // ---------------------------------------------------------------- .str

    /// <summary>Changes of a translation file; reverts edit <paramref name="live"/>.</summary>
    public static List<Change> Str(StrFile old, StrFile live)
    {
        var lines = new List<Change>();
        foreach (var e in live.Entries.ToList())
        {
            var o = old.Find(e.Reference);
            var entry = e;
            if (o == null)
            {
                lines.Add(new Change(
                    $"Added translation {e.Reference}: " + string.Join(", ", e.Languages.Select(l => $"{l.Key[5..]} {Q(l.Value)}")),
                    $"Fields that point to the translation {e.Reference} will no longer find a text.",
                    () => live.Entries.Remove(entry)));
                continue;
            }
            foreach (var l in e.Languages.ToList())
            {
                var lang = l.Key[5..];
                var before = o.Language(lang)?.Value;
                var prop = l;
                if (before == null)
                    lines.Add(new Change($"Translation {e.Reference}: added {lang} {Q(l.Value)}", null, () => entry.Props.Remove(prop)));
                else if (before != l.Value)
                    lines.Add(new Change($"Translation {e.Reference}: {lang} {Q(before)} -> {Q(l.Value)}", null, () => prop.Value = before));
            }
        }
        foreach (var o in old.Entries.Where(o => live.Find(o.Reference) == null))
            lines.Add(new Change($"Removed translation {o.Reference}", null, null));
        return lines;
    }
}
