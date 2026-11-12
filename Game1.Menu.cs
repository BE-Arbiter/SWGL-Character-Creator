using ImGuiNET;
using Vec2 = System.Numerics.Vector2;

namespace SWGLCharacterCreator;

/// <summary>Main menu "Add" and "Selected Character / Variant", plus adding and deleting blocks.</summary>
public partial class Game1
{
    private const string ModalDelete = "Delete";
    private Node? _deleteNode;

    private Node? Root(string title) => _roots.FirstOrDefault(r => r.Title == title);

    /// <summary>The file node of the selection when it is (or is inside) a file with <paramref name="ext"/>.</summary>
    private Node? SelectedFile(string ext)
    {
        var n = _selected;
        if (n?.File == null || !n.File.Entry.Path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return null;
        return n.Kind == NodeKind.File ? n : _roots.SelectMany(r => r.Children).FirstOrDefault(c => c.File == n.File);
    }

    private Node? SelectedCharacter() =>
        _selected is { Kind: NodeKind.Block } n && n.Block!.Type != "Variant" && IsCharacterFile(n) ? n : null;

    /// <summary>A menu item that is greyed out with an explanation when it does not apply to the selection.</summary>
    private static bool MenuItemWhen(string label, bool enabled, string disabledTip)
    {
        bool clicked = ImGui.MenuItem(label, null, false, enabled);
        if (!enabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(disabledTip);
        return clicked && enabled;
    }

    private void DrawMainMenus()
    {
        bool ready = _index != null && !_indexing;
        if (ImGui.BeginMenu("Add", ready))
        {
            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("Factions") && Root("Factions") is { } f) OpenFileName(f, rename: false);
                if (ImGui.MenuItem("Characters") && Root("Characters") is { } c) OpenFileName(c, rename: false);
                ImGui.EndMenu();
            }
            ImGui.Separator();
            var fac = SelectedFile(".fac");
            if (MenuItemWhen("Faction", fac != null, "Select a faction file (.fac), or a faction inside one, in the explorer.")) AddFaction(fac!);
            var cha = SelectedFile(".cha");
            if (MenuItemWhen("Character", cha != null, "Select a character file (.cha), or something inside one, in the explorer.")) AddCharacter(cha!);
            var ch = SelectedCharacter();
            if (MenuItemWhen("Variant", ch != null, "Select a character in the explorer.")) AddVariant(ch!);
            ImGui.EndMenu();
        }

        var sel = _selected;
        bool transferable = ready && sel != null && IsTransferable(sel);
        bool variant = transferable && sel!.Block!.Type == "Variant";
        if (ImGui.BeginMenu((variant ? "Selected Variant" : "Selected Character") + "###selected", transferable))
        {
            if (ImGui.MenuItem("Move...")) OpenTransfer(sel!, move: true);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(variant ? "Move this variant to another character." : "Move this character to another .cha file.");
            if (ImGui.MenuItem("Copy...")) OpenTransfer(sel!, move: false);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(variant ? "Copy this variant to a character." : "Copy this character to a .cha file.");
            ImGui.Separator();
            if (ImGui.MenuItem("Delete...")) AskDelete(sel!);
            ImGui.EndMenu();
        }
        if (!transferable && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Select a character or a variant in the explorer.");
    }

    // ---------------------------------------------------------------- add

    /// <summary>A new block with the same fields as an existing block of the same kind (empty values), and a unique code.</summary>
    private static Block NewBlockLike(Block? template, string type, string code, string[] fallbackKeys)
    {
        var b = new Block { Type = type };
        b.Props.Add(new Prop { Key = "code", Value = code });
        var keys = template?.Props.Where(p => !p.IsComment && !p.Key.Equals("code", StringComparison.OrdinalIgnoreCase))
                           .Select(p => (p.Key, p.Quoted)).ToList()
                   ?? fallbackKeys.Select(k => (k, false)).ToList();
        foreach (var (key, quoted) in keys)
            b.Props.Add(new Prop
            {
                Key = key,
                Value = key.Equals("tags", StringComparison.OrdinalIgnoreCase) ? "none" : "",
                Quoted = quoted,
            });
        return b;
    }

    private static IEnumerable<Block> BlocksOf(Node? root) =>
        root?.Children.SelectMany(f => f.Children).Select(n => n.Block!) ?? Enumerable.Empty<Block>();

    private void AddTopBlock(Node fileNode, Block b)
    {
        fileNode.File!.Config.Blocks.Add(b);
        var node = Node.BuildBlock(b, fileNode.File);
        fileNode.Children.Add(node);
        fileNode.File.Dirty = true;
        _expanded.Add(fileNode);
        if (FindParent(fileNode) is { } cat) _expanded.Add(cat);
        _selected = node;
    }

    private void AddFaction(Node fileNode)
    {
        var all = BlocksOf(Root("Factions")).ToList();
        var code = UniqueCode("NEW_FACTION", all.Select(b => b.Get("code") ?? "").ToList());
        AddTopBlock(fileNode, NewBlockLike(all.FirstOrDefault(b => b.Type == "Faction"), "Faction", code,
            new[] { "nameKey", "icon" }));
        _knownFactions = null;
        _factionTitles = null;
        _status = $"Added faction {code} to {fileNode.File!.Name} (not saved yet).";
    }

    private void AddCharacter(Node fileNode)
    {
        var all = BlocksOf(Root("Characters")).ToList();
        var code = UniqueCode("NEW_CHARACTER", all.Select(b => b.Get("code") ?? "").ToList());
        AddTopBlock(fileNode, NewBlockLike(all.FirstOrDefault(b => b.Type.Length == 0), "", code,
            new[] { "name", "icon", "factions", "tags" }));
        _status = $"Added character {code} to {fileNode.File!.Name} (not saved yet).";
    }

    /// <summary>New variant under a character, prefilled from its first variant; opens the config choice.</summary>
    private void AddVariant(Node n)
    {
        var b = n.Block!;
        var f = n.File!;
        var first = b.Children.FirstOrDefault();
        string code = UniqueCode("VARIANT" + (b.Children.Count + 1), b.Children.Select(c => c.Get("code") ?? "").ToList());
        var v = new Block { Type = "Variant" };
        v.Props.Add(new Prop { Key = "code", Value = code });
        v.Props.Add(new Prop { Key = "name", Value = b.Get("name") ?? code, Quoted = true });
        v.Props.Add(new Prop { Key = "icon", Value = first?.Get("icon") ?? b.Get("icon") ?? "" });
        v.Props.Add(new Prop { Key = "model", Value = first?.Get("model") ?? "" });
        v.Props.Add(new Prop { Key = "skin", Value = first?.Get("skin") ?? "model_default" });
        v.Props.Add(new Prop { Key = "descriptionKey", Value = first?.Get("descriptionKey") ?? "" });
        v.Props.Add(new Prop { Key = "specie", Value = first?.Get("specie") ?? "", Quoted = true });
        b.Children.Add(v);
        var node = Node.BuildBlock(v, f);
        n.Children.Add(node);
        _expanded.Add(n);
        _selected = node;
        f.Dirty = true;
        OpenNewVariantConfig(v);
    }

    // ---------------------------------------------------------------- delete

    private void AskDelete(Node n)
    {
        _deleteNode = n;
        _openModalName = ModalDelete;
    }

    private void DrawDeleteModal()
    {
        if (_deleteNode == null) return;
        CenterNextWindow(520, 230);
        bool open = true;
        if (!ImGui.BeginPopupModal(ModalDelete, ref open))
        {
            if (_openModalName != ModalDelete) _deleteNode = null;   // closed with the window's X
            return;
        }

        var n = _deleteNode;
        bool variant = n.Block!.Type == "Variant";
        var parent = FindParent(n);
        ImGui.TextWrapped((variant
            ? $"Delete variant {n.Label} of {parent?.Label}?"
            : $"Delete character {n.Label} from {n.File!.Name}?").Replace("%", "%%"));
        ImGui.Spacing();
        ImGui.TextUnformatted("This will also:");
        ImGui.PushStyleColor(ImGuiCol.Text, WarnColor);
        int variants = n.Block.Children.Count(c => c.Type == "Variant");
        if (!variant && variants > 0) { ImGui.Bullet(); ImGui.TextWrapped($"delete its {variants} variant(s);"); }
        ImGui.Bullet();
        ImGui.TextWrapped(variant
            ? "empty its _def.cfg when you save (if it has one)."
            : "empty the _def.cfg of its variants when you save (those that have one).");
        ImGui.PopStyleColor();
        ImGui.TextDisabled("Until you save, it can be restored from Review changes.");
        ImGui.Separator();

        if (ImGui.Button("Delete"))
        {
            if (parent != null) DeleteNode(n, parent);
            _deleteNode = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) { _deleteNode = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    private void DeleteNode(Node n, Node parent)
    {
        var f = n.File!;
        if (n.Block!.Type == "Variant") parent.Block!.Children.Remove(n.Block);
        else f.Config.Blocks.Remove(n.Block);
        parent.Children.Remove(n);
        f.Dirty = true;
        _defs?.Prune(ChaConfigs());
        if (_selected == n || (_selected != null && Descendants(n).Contains(_selected))) _selected = parent;
        _knownTags = null;
        _status = $"Deleted {n.Label} (not saved yet).";
    }
}
