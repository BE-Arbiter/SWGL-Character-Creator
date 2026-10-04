namespace SWGLCharacterCreator;

/// <summary>A .fac / .cha file read from a pk3 and parsed, with its pending edits.</summary>
public sealed class ParsedFile
{
    public required Pk3Entry Entry { get; set; }
    public ConfigFile Config { get; set; } = new();
    public string? Error { get; set; }
    public bool Dirty { get; set; }

    /// <summary>Created in the explorer and not written to disk yet.</summary>
    public bool IsNew { get; set; }
    public string Name => System.IO.Path.GetFileName(Entry.Path);
}

public enum NodeKind { Category, File, Block }

/// <summary>Explorer tree node: Category -> File -> Block (-> child Block).</summary>
public sealed class Node
{
    public NodeKind Kind;
    public string Title = "";
    public ParsedFile? File;
    public Block? Block;
    public List<Node> Children = new();

    public string Label
    {
        get
        {
            switch (Kind)
            {
                case NodeKind.File: return File!.Name + (File.Dirty ? " *" : "");
                case NodeKind.Block:
                    var code = Block!.Get("code") ?? "?";
                    var name = Block.Get("name");
                    return name != null && name != code ? $"{code}  ({name})" : $"{code}";
                default: return Title;
            }
        }
    }

    public static Node BuildBlock(Block b, ParsedFile f)
    {
        var n = new Node { Kind = NodeKind.Block, Block = b, File = f };
        foreach (var c in b.Children) n.Children.Add(BuildBlock(c, f));
        return n;
    }

    public static Node BuildFile(ParsedFile f)
    {
        var n = new Node { Kind = NodeKind.File, File = f };
        foreach (var b in f.Config.Blocks) n.Children.Add(BuildBlock(b, f));
        return n;
    }

    public bool Matches(string filter)
    {
        if (Label.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var c in Children) if (c.Matches(filter)) return true;
        return false;
    }
}
