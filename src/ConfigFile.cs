using System.Text;

namespace SWGLCharacterCreator;

/// <summary>One "key value" line inside a block. Comment lines use the key "//".</summary>
public sealed class Prop
{
    public string Key = "";
    public string Value = "";
    public bool Quoted;
    public int Line = -1;          // source line (only tracked by StrFile, for patch-style saving)
    public string? Original;       // value as read, to detect edits
    public bool IsComment => Key == "//";
}

/// <summary>A "{ ... }" block with an optional type name (Faction, Variant...) before the brace.</summary>
public sealed class Block
{
    public string Type = "";
    public List<Prop> Props = new();
    public List<Block> Children = new();
    public List<string> Leading = new();   // comment lines that preceded a top-level block

    public string? Get(string key) =>
        Props.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;
}

/// <summary>Parser/serializer for the SWGL .fac / .cha text format.</summary>
public sealed class ConfigFile
{
    public List<Block> Blocks = new();

    public static ConfigFile Parse(string text)
    {
        var file = new ConfigFile();
        var stack = new Stack<Block>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string pendingType = "";
        var pendingComments = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("//"))
            {
                if (stack.Count > 0) stack.Peek().Props.Add(new Prop { Key = "//", Value = line[2..].Trim() });
                else pendingComments.Add(line[2..].Trim());
                continue;
            }

            if (line == "{" || line.EndsWith('{'))
            {
                var type = line == "{" ? pendingType : line[..^1].Trim();
                pendingType = "";
                var b = new Block { Type = type };
                if (stack.Count > 0) stack.Peek().Children.Add(b);
                else { b.Leading.AddRange(pendingComments); pendingComments.Clear(); file.Blocks.Add(b); }
                stack.Push(b);
                continue;
            }

            if (line == "}")
            {
                if (stack.Count > 0) stack.Pop();
                continue;
            }


            // A lone word followed by "{" names the block (Faction, Variant...), at any depth.
            bool lone = line.IndexOfAny(new[] { ' ', '\t' }) < 0;
            if (stack.Count == 0 || (lone && NextLineIsBrace(lines, i)))
            {
                pendingType = line;
                continue;
            }
            int sp = line.IndexOfAny(new[] { ' ', '\t' });
            var prop = new Prop();
            if (sp < 0) prop.Key = line;
            else
            {
                prop.Key = line[..sp];
                var v = line[sp..].Trim();
                if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') { v = v[1..^1]; prop.Quoted = true; }
                prop.Value = v;
            }
            stack.Peek().Props.Add(prop);
        }
        return file;
    }

    private static bool NextLineIsBrace(string[] lines, int i)
    {
        for (int j = i + 1; j < lines.Length; j++)
        {
            var next = lines[j].Trim();
            if (next.Length == 0 || next.StartsWith("//")) continue;
            return next == "{";
        }
        return false;
    }

    public string Serialize()
    {
        var sb = new StringBuilder();
        foreach (var b in Blocks) Write(sb, b, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, Block b, int depth)
    {
        var tabs = new string('\t', depth);
        sb.Append('\n');
        foreach (var c in b.Leading) sb.Append("// ").Append(c).Append('\n');
        if (b.Type.Length > 0) sb.Append(tabs).Append(b.Type).Append('\n');
        sb.Append(tabs).Append("{\n");
        foreach (var p in b.Props)
        {
            if (p.IsComment) { sb.Append(tabs).Append("\t// ").Append(p.Value).Append('\n'); continue; }
            var v = p.Value;
            bool quote = p.Quoted || v.Contains(' ') || v.Contains('\t');
            sb.Append(tabs).Append('\t').Append(p.Key).Append("\t\t")
              .Append(quote ? "\"" + v + "\"" : v).Append('\n');
        }
        foreach (var c in b.Children) Write(sb, c, depth + 1);
        sb.Append(tabs).Append("}\n");
    }
}
