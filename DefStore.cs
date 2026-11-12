using System.Text;

namespace SWGLCharacterCreator;

/// <summary>
/// Binds each character variant (a "Variant" child block of a character with a code) to its
/// ext_data/characters_conf/CHAR_VARIANT_def.cfg, and turns renames, moves, copies and deletes of variants
/// into file writes and empties at save time.
/// </summary>
public sealed class DefStore
{
    public const string Dir = "ext_data/characters_conf/";

    private sealed class Binding
    {
        public string? Origin;     // path the def was read from (null when the variant has no file yet)
        public DefFile? Def;       // loaded or edited def
        public bool Dirty;
    }

    private Pk3Index _index;
    private readonly Dictionary<Block, Binding> _bindings = new();
    private readonly Dictionary<ConfigFile, List<Block>> _byFile = new();

    public DefStore(Pk3Index index) { _index = index; }

    /// <summary>Points the store at a refreshed index (bindings and edits are kept).</summary>
    public void Rebase(Pk3Index index) => _index = index;

    public static string PathFor(string characterCode, string variantCode) =>
        $"{Dir}{characterCode}_{variantCode}_def.cfg";

    public bool AnyDirty => _bindings.Values.Any(b => b.Dirty);

    public void Bind(ConfigFile cha)
    {
        Unbind(cha);
        var blocks = new List<Block>();
        foreach (var (variant, path) in Variants(cha))
        {
            var origin = _index.Files.TryGetValue(path, out var e) && e.Size > 0 ? path : null;
            _bindings[variant] = new Binding { Origin = origin };
            blocks.Add(variant);
        }
        _byFile[cha] = blocks;
    }

    /// <summary>Drops the bindings of every block of the file, including variants already removed from it.</summary>
    public void Unbind(ConfigFile cha)
    {
        if (_byFile.Remove(cha, out var blocks))
            foreach (var b in blocks) _bindings.Remove(b);
        foreach (var (variant, _) in Variants(cha)) _bindings.Remove(variant);
    }

    public string? CurrentPath(ConfigFile cha, Block variant)
    {
        foreach (var (v, path) in Variants(cha))
            if (ReferenceEquals(v, variant)) return path;
        return null;
    }

    public DefFile? Load(Block variant)
    {
        if (!_bindings.TryGetValue(variant, out var b)) return null;
        if (b.Def != null) return b.Def;
        if (b.Origin == null || !_index.Files.TryGetValue(b.Origin, out var entry)) return null;
        return b.Def = DefFile.Parse(Pk3Reader.ReadText(entry));
    }

    public void SetDef(Block variant, DefFile def)
    {
        var b = GetOrAdd(variant);
        b.Def = def;
        b.Dirty = true;
    }

    /// <summary>
    /// Drops the unsaved config edits of one variant: back to the file it was read from, or removed when it never had one.
    /// </summary>
    public void ResetBinding(Block variant)
    {
        if (!_bindings.TryGetValue(variant, out var b)) return;
        if (b.Origin == null) { _bindings.Remove(variant); return; }
        b.Def = null;
        b.Dirty = false;
    }

    /// <summary>
    /// Drops the unsaved config edits of the live variants whose def is written at <paramref name="path"/>.
    /// Writes caused only by a variant rename, move or delete are left alone (they follow the .cha file). Returns true if any was reset.
    /// </summary>
    public bool ResetAt(IEnumerable<ConfigFile> chaFiles, string path)
    {
        bool any = false;
        foreach (var (variant, p) in chaFiles.SelectMany(Variants).ToList())
            if (p.Equals(path, StringComparison.OrdinalIgnoreCase) && IsDirty(variant)) { ResetBinding(variant); any = true; }
        return any;
    }

    /// <summary>The live variant whose edited (dirty) def is written at <paramref name="path"/>, if any.</summary>
    public Block? DirtyVariantAt(IEnumerable<ConfigFile> chaFiles, string path) =>
        chaFiles.SelectMany(Variants).Where(v => v.path.Equals(path, StringComparison.OrdinalIgnoreCase) && IsDirty(v.variant))
                .Select(v => v.variant).FirstOrDefault();

    /// <summary>Binds the variants of <paramref name="cha"/> that have no binding yet (e.g. restored by a revert) to their saved def.</summary>
    public void EnsureBound(ConfigFile cha)
    {
        if (!_byFile.TryGetValue(cha, out var blocks)) _byFile[cha] = blocks = new List<Block>();
        foreach (var (variant, path) in Variants(cha))
        {
            if (_bindings.ContainsKey(variant)) continue;
            var origin = _index.Files.TryGetValue(path, out var e) && e.Size > 0 ? path : null;
            _bindings[variant] = new Binding { Origin = origin };
            blocks.Add(variant);
        }
    }

    /// <summary>True when <see cref="ResetAt"/> would reset something.</summary>
    public bool CanResetAt(IEnumerable<ConfigFile> chaFiles, string path) =>
        chaFiles.SelectMany(Variants).Any(v => v.path.Equals(path, StringComparison.OrdinalIgnoreCase) && IsDirty(v.variant));

    public void MarkDirty(Block variant) => GetOrAdd(variant).Dirty = true;

    public bool IsDirty(Block variant) => _bindings.TryGetValue(variant, out var b) && b.Dirty;

    /// <summary>Copies the defs of a variant, or of every variant of a character (children paired by index), onto a clone.</summary>
    public void CopyDefs(Block source, Block clone)
    {
        if (source.Type == "Variant")
        {
            if (Load(source) is { } def) SetDef(clone, def.Clone());
            return;
        }
        for (int i = 0; i < Math.Min(source.Children.Count, clone.Children.Count); i++)
            CopyDefs(source.Children[i], clone.Children[i]);
    }

    public Dictionary<string, byte[]> CollectChanges(IEnumerable<ConfigFile> chaFiles)
    {
        var live = chaFiles.SelectMany(Variants).ToList();
        var livePaths = new HashSet<string>(live.Select(l => l.path), StringComparer.OrdinalIgnoreCase);
        var liveBlocks = new HashSet<Block>(live.Select(l => l.variant));
        var changes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();

        foreach (var (variant, path) in live)
        {
            if (!_bindings.TryGetValue(variant, out var b)) continue;
            bool moved = b.Origin != null && !b.Origin.Equals(path, StringComparison.OrdinalIgnoreCase);
            if (b.Dirty || moved)
            {
                var def = b.Def ?? Load(variant);
                if (def != null) changes[path] = Encoding.Latin1.GetBytes(def.Serialize());
            }
            if (moved) candidates.Add(b.Origin!);
        }
        foreach (var (block, b) in _bindings)
            if (!liveBlocks.Contains(block) && b.Origin != null) candidates.Add(b.Origin);

        foreach (var c in candidates)
            if (!changes.ContainsKey(c) && !livePaths.Contains(c)) changes[c] = Array.Empty<byte>();
        return changes;
    }

    public void AfterSave(IEnumerable<ConfigFile> chaFiles)
    {
        var live = chaFiles.SelectMany(Variants).ToList();
        var liveBlocks = new HashSet<Block>(live.Select(l => l.variant));
        foreach (var block in _bindings.Keys.Where(k => !liveBlocks.Contains(k)).ToList()) _bindings.Remove(block);
        foreach (var (variant, path) in live)
        {
            if (!_bindings.TryGetValue(variant, out var b)) continue;
            if (b.Origin != null || (b.Dirty && b.Def != null)) b.Origin = path;
            b.Dirty = false;
        }
    }

    /// <summary>Drops bindings that are no longer in any file and have no origin: they can never produce output.</summary>
    public void Prune(IEnumerable<ConfigFile> chaFiles)
    {
        var liveBlocks = new HashSet<Block>(chaFiles.SelectMany(Variants).Select(l => l.variant));
        foreach (var block in _bindings.Where(kv => kv.Value.Origin == null && !liveBlocks.Contains(kv.Key)).Select(kv => kv.Key).ToList())
            _bindings.Remove(block);
    }

    private Binding GetOrAdd(Block variant)
    {
        if (!_bindings.TryGetValue(variant, out var b)) _bindings[variant] = b = new Binding();
        return b;
    }

    /// <summary>Every variant of the file that has a character code and a variant code, with its current def path.</summary>
    private static IEnumerable<(Block variant, string path)> Variants(ConfigFile cha)
    {
        foreach (var ch in cha.Blocks)
        {
            var chCode = ch.Get("code");
            if (string.IsNullOrEmpty(chCode)) continue;
            foreach (var v in ch.Children)
            {
                if (v.Type != "Variant") continue;
                var vCode = v.Get("code");
                if (string.IsNullOrEmpty(vCode)) continue;
                yield return (v, PathFor(chCode, vCode));
            }
        }
    }
}
