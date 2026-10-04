using System.Text.RegularExpressions;
using ImGuiNET;
using Vec2 = System.Numerics.Vector2;
using Vec4 = System.Numerics.Vector4;

namespace SWGLCharacterCreator;

/// <summary>Field table with per-field edit controls (pencil / V), menus and modals.</summary>
public partial class Game1
{
    private const string ModelsRoot = "models/players";

    private ModelViewer _viewer = null!;
    private List<string>? _knownFactions;
    private List<string>? _knownTags;
    private Dictionary<string, string>? _factionTitles;
    private string _newTag = "";

    // Edit state: fields shown as editors / translations shown as editors.
    private readonly HashSet<Prop> _editingProps = new();
    private readonly HashSet<Prop> _editingTr = new();

    // 3D preview
    private string? _modelError;
    private string? _modelFailedKey;
    private string _modelCaption = "";
    private bool _debugDone;

    // Index refresh (model folder modal)
    private volatile Pk3Index? _pendingIndex;
    private volatile bool _refreshing;

    // Modals
    private string? _openModalName;
    private const string ModalCreateTr = "Create translation";
    private const string ModalAddLang = "Add language";
    private const string ModalImport = "Import image";
    private const string ModalMove = "Move image location";
    private const string ModalModel = "Choose model folder";

    private Prop? _mProp;                 // field the open modal acts on
    private Block? _mBlock;
    private ParsedFile? _mFile;
    private string _mStrName = "";
    private string _mRef = "";
    private string _mText = "";
    private bool _mConvertName;
    private string _mError = "";
    private StrFile? _mStr;
    private StrEntry? _mEntry;
    private string _mLang = "";
    private string _mDest = "";
    private string _mSource = "";
    private volatile string? _mPicked;
    private List<string> _mFolders = new();
    private string _mFolderFilter = "";
    private bool _mFoldersStale = true;

    private static readonly string[] Separators = { "," };

    private static List<string> SplitList(string value) =>
        value.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool IsCharacterFile(Node n) =>
        n.File!.Entry.Path.EndsWith(".cha", StringComparison.OrdinalIgnoreCase);

    private static string EscapeStr(string text) =>
        Regex.Replace(text.Replace("\r", "").Replace("\n", "\\n"), "(?<!\\\\)\"", "\\\"");

    private static string Unescaped(string raw) => raw.Replace("\\n", " ").Replace("\\\"", "\"");

    // ---------------------------------------------------------------- icon button

    /// <summary>Small square button drawn as a pencil, or as a "V" when <paramref name="check"/> is set.</summary>
    private static bool IconButton(string id, bool check)
    {
        float s = ImGui.GetFrameHeight();
        var pos = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(id, new Vec2(s, s));
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vec2(s, s),
            ImGui.GetColorU32(ImGui.IsItemHovered() ? ImGuiCol.ButtonHovered : ImGuiCol.Button), 3f);
        uint fg = ImGui.GetColorU32(ImGuiCol.Text);
        if (check)
        {
            var ts = ImGui.CalcTextSize("V");
            dl.AddText(pos + new Vec2((s - ts.X) / 2, (s - ts.Y) / 2), fg, "V");
        }
        else
        {
            dl.AddLine(pos + new Vec2(s * 0.34f, s * 0.66f), pos + new Vec2(s * 0.72f, s * 0.28f), fg, 3f);
            dl.AddTriangleFilled(pos + new Vec2(s * 0.22f, s * 0.80f), pos + new Vec2(s * 0.27f, s * 0.60f),
                pos + new Vec2(s * 0.42f, s * 0.75f), fg);
            dl.AddLine(pos + new Vec2(s * 0.68f, s * 0.22f), pos + new Vec2(s * 0.78f, s * 0.32f), fg, 1.5f);
        }
        return clicked;
    }

    // ---------------------------------------------------------------- field table

    private void DrawBlockFields(Node n)
    {
        var b = n.Block!;
        var f = n.File!;
        ImGui.TextUnformatted(b.Type.Length > 0 ? b.Type : "Character");
        if (b.Children.Count > 0) { ImGui.SameLine(); ImGui.TextDisabled($"- {b.Children.Count} sub-item(s)"); }
        DrawTransferButtons(n);

        const ImGuiTableFlags tf = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("##fields", 3, tf))
        {
            ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.None, 2f);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.None, 5f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight() + 4);
            ImGui.TableHeadersRow();

            for (int i = 0; i < b.Props.Count; i++)
            {
                var p = b.Props[i];
                if (p.IsComment) continue;
                ImGui.PushID(i);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(p.Key);
                DrawFieldRow(b, f, p);
                ImGui.PopID();
            }
            ImGui.EndTable();
        }

        DrawBlockExtras(n);

        // Translations of every key field (read-only unless "Edit Translation" was chosen).
        foreach (var p in b.Props.ToList())
            if (IsKeyField(b, p) && p.Value.Length > 0)
                DrawTranslation(p);
    }

    private static bool IsKeyField(Block b, Prop p) =>
        p.Key.EndsWith("Key", StringComparison.OrdinalIgnoreCase);

    private static bool IsName(Block b, Prop p) =>
        b.Type != "Faction" && (p.Key.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                                p.Key.Equals("nameKey", StringComparison.OrdinalIgnoreCase));

    /// <summary>Value cell + edit-control cell for one field, chosen by field name.</summary>
    private void DrawFieldRow(Block b, ParsedFile f, Prop p)
    {
        ImGui.TableNextColumn();
        switch (p.Key.ToLowerInvariant())
        {
            case "factions":
                ImGui.AlignTextToFramePadding();
                var titles = SplitList(p.Value).Select(FactionTitle).ToList();
                ImGui.TextWrapped(titles.Count > 0 ? string.Join(", ", titles) : "(none)");
                ImGui.TableNextColumn();
                if (IconButton("##e", false)) ImGui.OpenPopup("##facpop");
                DrawFactionPopup(f, p);
                return;

            case "tags":
                if (_editingProps.Contains(p)) DrawTagsEditor(f, p);
                else
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(p.Value.Length > 0 ? p.Value : "none");
                }
                DrawToggleControl(p);
                return;

            case "random":
                bool on = p.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                bool editing = _editingProps.Contains(p);
                if (!editing) ImGui.BeginDisabled();
                if (ImGui.Checkbox("##random", ref on)) { p.Value = on ? "true" : "false"; f.Dirty = true; }
                if (!editing) ImGui.EndDisabled();
                DrawToggleControl(p);
                return;

            case "model":
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(p.Value.Length > 0 ? p.Value : "(none)");
                ImGui.TableNextColumn();
                if (IconButton("##e", false)) OpenModelModal(b, f, p);
                return;

            case "skin":
                DrawSkinCombo(b, f, p);
                ImGui.TableNextColumn();
                return;

            case "icon":
                DrawTextValue(f, p);
                ImGui.TableNextColumn();
                if (IconButton("##e", false)) ImGui.OpenPopup("##menu");
                if (ImGui.BeginPopup("##menu")) { DrawIconMenu(b, f, p); ImGui.EndPopup(); }
                return;
        }

        DrawTextValue(f, p);
        ImGui.TableNextColumn();

        if (IsName(b, p)) { DrawEditControl(p, () => DrawNameMenu(b, f, p)); return; }
        if (IsKeyField(b, p)) { DrawEditControl(p, () => DrawKeyMenu(b, f, p)); return; }
        DrawEditControl(p, null);
    }

    /// <summary>Plain text: label when read-only, input when the field is being edited.</summary>
    private void DrawTextValue(ParsedFile f, Prop p)
    {
        if (_editingProps.Contains(p))
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##v", ref p.Value, 1024)) f.Dirty = true;
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            if (p.Value.Length > 0) ImGui.TextUnformatted(p.Value); else ImGui.TextDisabled("(empty)");
        }
    }

    /// <summary>Last cell: "V" while editing, otherwise a pencil that edits directly or opens a menu.</summary>
    private void DrawEditControl(Prop p, Action? menu)
    {
        if (_editingProps.Contains(p))
        {
            if (IconButton("##v", true)) _editingProps.Remove(p);
            return;
        }
        if (IconButton("##e", false))
        {
            if (menu == null) _editingProps.Add(p); else ImGui.OpenPopup("##menu");
        }
        if (menu != null && ImGui.BeginPopup("##menu")) { menu(); ImGui.EndPopup(); }
    }

    /// <summary>Control cell for fields whose editor is drawn in the value cell (tags, random).</summary>
    private void DrawToggleControl(Prop p)
    {
        ImGui.TableNextColumn();
        if (IconButton("##t", _editingProps.Contains(p)))
        {
            if (!_editingProps.Remove(p)) _editingProps.Add(p);
        }
    }

    // ---------------------------------------------------------------- menus

    private void DrawNameMenu(Block b, ParsedFile f, Prop p)
    {
        bool translated = p.Key.Equals("nameKey", StringComparison.OrdinalIgnoreCase);
        if (!translated)
        {
            if (ImGui.MenuItem("Add Translation")) OpenCreateTranslation(b, f, p, convertName: true);
            if (ImGui.MenuItem("Edit")) _editingProps.Add(p);
            return;
        }

        var res = ResolveKey(p.Value);
        if (res == null && ImGui.MenuItem("Create Translation")) OpenCreateTranslation(b, f, p, convertName: false);
        if (ImGui.MenuItem("Add Language", null, false, res != null)) OpenAddLanguage(res!.Value.file, res.Value.entry);
        if (ImGui.MenuItem("Edit Translation", null, false, res != null))
            _editingTr.Add(p);
        if (ImGui.MenuItem("Edit Code")) _editingProps.Add(p);
        if (ImGui.MenuItem("Remove Translation"))
        {
            // Back to plain text, starting from the English translation when there is one.
            var text = res?.entry.Language("ENGLISH")?.Value ?? "";
            p.Key = "name";
            p.Value = text;
            p.Quoted = true;
            f.Dirty = true;
        }
    }

    private void DrawKeyMenu(Block b, ParsedFile f, Prop p)
    {
        var res = ResolveKey(p.Value);
        if (res == null)
        {
            if (ImGui.MenuItem("Create Translation")) OpenCreateTranslation(b, f, p, convertName: false);
            return;
        }
        if (ImGui.MenuItem("Add Language")) OpenAddLanguage(res.Value.file, res.Value.entry);
        if (ImGui.MenuItem("Edit Translation")) _editingTr.Add(p);
        if (ImGui.MenuItem("Edit Code")) _editingProps.Add(p);
    }

    private void DrawIconMenu(Block b, ParsedFile f, Prop icon)
    {
        if (ImGui.MenuItem("Pick from files"))
            OpenPicker((Path.GetDirectoryName(icon.Value.Replace('\\', '/')) ?? "").Replace('\\', '/'),
                new[] { ".png", ".tga" }, e => { icon.Value = e.Path; f.Dirty = true; });
        if (ImGui.MenuItem("Import New"))
        {
            _mProp = icon; _mFile = f; _mSource = ""; _mError = ""; _mDest = icon.Value;
            _openModalName = ModalImport;
        }
        if (ImGui.MenuItem("Move Image Location", null, false, icon.Value.Length > 0))
        {
            _mProp = icon; _mFile = f; _mError = ""; _mDest = icon.Value;
            _openModalName = ModalMove;
        }
    }

    // ---------------------------------------------------------------- factions / tags / skin

    private List<string> KnownFactions()
    {
        if (_knownFactions != null) return _knownFactions;
        var list = new List<string>();
        var root = _roots.FirstOrDefault(r => r.Title == "Factions");
        if (root != null)
            foreach (var file in root.Children)
                foreach (var block in file.Children)
                    if (block.Block!.Get("code") is { Length: > 0 } code && !list.Contains(code, StringComparer.OrdinalIgnoreCase))
                        list.Add(code);
        return _knownFactions = list;
    }

    /// <summary>Display title of a faction code: its English translation, or the code itself.</summary>
    private string FactionTitle(string code)
    {
        if (_factionTitles == null)
        {
            _factionTitles = new(StringComparer.OrdinalIgnoreCase);
            var root = _roots.FirstOrDefault(r => r.Title == "Factions");
            if (root != null)
                foreach (var file in root.Children)
                    foreach (var block in file.Children)
                    {
                        var fc = block.Block!.Get("code");
                        var key = block.Block.Get("nameKey");
                        if (fc == null || key == null) continue;
                        var text = ResolveKey(key)?.entry.Language("ENGLISH")?.Value;
                        if (!string.IsNullOrEmpty(text)) _factionTitles[fc] = Unescaped(text);
                    }
        }
        return _factionTitles.TryGetValue(code, out var t) ? t : code;
    }

    private List<string> KnownTags()
    {
        if (_knownTags != null) return _knownTags;
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = _roots.FirstOrDefault(r => r.Title == "Characters");
        if (root != null)
            foreach (var file in root.Children)
                foreach (var block in file.Children)
                    foreach (var t in SplitList(block.Block!.Get("tags") ?? ""))
                        if (!t.Equals("none", StringComparison.OrdinalIgnoreCase)) set.Add(t);
        return _knownTags = set.ToList();
    }

    /// <summary>Multi-select among the indexed factions, shown by title; stored as a comma separated list of codes.</summary>
    private void DrawFactionPopup(ParsedFile f, Prop p)
    {
        if (!ImGui.BeginPopup("##facpop")) return;
        var selected = SplitList(p.Value);
        var options = KnownFactions()
            .Concat(selected.Where(s => !KnownFactions().Contains(s, StringComparer.OrdinalIgnoreCase)))
            .ToList(); // unknown codes stay visible so they are not lost

        foreach (var code in options)
        {
            bool on = selected.Contains(code, StringComparer.OrdinalIgnoreCase);
            if (!ImGui.Checkbox(FactionTitle(code) + "##" + code, ref on)) continue;
            if (on) selected.Add(code);
            else selected.RemoveAll(s => s.Equals(code, StringComparison.OrdinalIgnoreCase));
            p.Value = string.Join(",", selected);
            f.Dirty = true;
        }
        ImGui.EndPopup();
    }

    /// <summary>Tag chips with a remove button, a picker for known tags and a free entry. Empty list is "none".</summary>
    private void DrawTagsEditor(ParsedFile f, Prop p)
    {
        var tags = SplitList(p.Value).Where(t => !t.Equals("none", StringComparison.OrdinalIgnoreCase)).ToList();
        bool changed = false;

        for (int i = 0; i < tags.Count; i++)
        {
            if (i > 0) ImGui.SameLine();
            if (ImGui.SmallButton($"{tags[i]}  x##tag{i}")) { tags.RemoveAt(i); changed = true; break; }
        }
        if (tags.Count == 0) ImGui.TextDisabled("none");

        ImGui.SetNextItemWidth(130);
        if (ImGui.BeginCombo("##knowntag", "Add known tag..."))
        {
            foreach (var t in KnownTags().Where(t => !tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                if (ImGui.Selectable(t)) { tags.Add(t); changed = true; }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130);
        bool enter = ImGui.InputTextWithHint("##newtag", "New tag + Enter", ref _newTag, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        var clean = _newTag.Trim().Replace(",", "").Replace(" ", "_");
        if (enter && clean.Length > 0 && !tags.Contains(clean, StringComparer.OrdinalIgnoreCase))
        {
            tags.Add(clean);
            changed = true;
        }
        if (enter) _newTag = "";

        if (changed)
        {
            p.Value = tags.Count == 0 ? "none" : string.Join(",", tags);
            f.Dirty = true;
            _knownTags = null;
        }
    }

    /// <summary>Drop-down of the .skin files found in the model's folder.</summary>
    private void DrawSkinCombo(Block b, ParsedFile f, Prop p)
    {
        var model = b.Get("model") ?? "";
        var prefix = $"{ModelsRoot}/{model}/";
        var skins = _index!.Files.Values
            .Where(e => e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        e.Path.EndsWith(".skin", StringComparison.OrdinalIgnoreCase) &&
                        e.Path.IndexOf('/', prefix.Length) < 0)
            .Select(e => Path.GetFileNameWithoutExtension(e.Path))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##skin", p.Value.Length > 0 ? p.Value : "(none)")) return;
        foreach (var s in skins)
            if (ImGui.Selectable(s, s.Equals(p.Value, StringComparison.OrdinalIgnoreCase)) &&
                !s.Equals(p.Value, StringComparison.OrdinalIgnoreCase))
            {
                p.Value = s;
                f.Dirty = true;
            }
        if (skins.Count == 0) ImGui.TextDisabled("No .skin in this model folder");
        ImGui.EndCombo();
    }

    // ---------------------------------------------------------------- variants

    /// <summary>Variant list under a character. Variants cannot contain variants.</summary>
    private void DrawBlockExtras(Node n)
    {
        if (n.Block!.Type != "Variant" && IsCharacterFile(n)) DrawVariantList(n);
    }

    private void DrawVariantList(Node n)
    {
        var b = n.Block!;
        var f = n.File!;
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled($"Variants ({n.Children.Count})");

        Node? remove = null;
        foreach (var c in n.Children)
        {
            ImGui.PushID(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c));
            if (ImGui.Selectable(c.Label, false, ImGuiSelectableFlags.None, new Vec2(ImGui.GetContentRegionAvail().X - 30, 0)))
            {
                _expanded.Add(n);
                _selected = c;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("x")) remove = c;
            ImGui.PopID();
        }
        if (remove != null)
        {
            b.Children.Remove(remove.Block!);
            n.Children.Remove(remove);
            f.Dirty = true;
        }

        if (ImGui.Button("Add variant"))
        {
            var first = b.Children.FirstOrDefault();
            string code = "VARIANT" + (b.Children.Count + 1);
            var v = new Block { Type = "Variant" };
            v.Props.Add(new Prop { Key = "code", Value = code });
            v.Props.Add(new Prop { Key = "name", Value = b.Get("name") ?? code, Quoted = true });
            v.Props.Add(new Prop { Key = "icon", Value = first?.Get("icon") ?? b.Get("icon") ?? "" });
            v.Props.Add(new Prop { Key = "model", Value = first?.Get("model") ?? "" });
            v.Props.Add(new Prop { Key = "skin", Value = first?.Get("skin") ?? "model_default" });
            v.Props.Add(new Prop { Key = "descriptionKey", Value = first?.Get("descriptionKey") ?? "" });
            b.Children.Add(v);
            var node = Node.BuildBlock(v, f);
            n.Children.Add(node);
            _expanded.Add(n);
            _selected = node;
            f.Dirty = true;
        }
    }

    // ---------------------------------------------------------------- translation block

    /// <summary>Texts of the translation a key field points to; editable after "Edit Translation".</summary>
    private void DrawTranslation(Prop keyProp)
    {
        ImGui.Spacing();
        ImGui.Separator();
        var res = ResolveKey(keyProp.Value);
        if (res == null)
        {
            ImGui.TextDisabled($"Translation for {keyProp.Key}: \"{keyProp.Value}\" not found in the .str files.");
            return;
        }

        var (sf, entry) = res.Value;
        bool editing = _editingTr.Contains(keyProp);
        ImGui.PushID("tr_" + keyProp.Key);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"Translation - {keyProp.Key}" + (sf.Dirty ? " *" : ""));
        if (editing)
        {
            ImGui.SameLine();
            if (IconButton("##commit", true))
            {
                // V validates: writes a loose copy of the .str with only the edited lines changed.
                if (!sf.Dirty || SaveStr(sf)) _editingTr.Remove(keyProp);
            }
        }
        ImGui.TextDisabled($"{sf.Entry.Path}  ({sf.Entry.Pk3Folder}/{sf.Entry.Pk3Name})  REFERENCE {entry.Reference}");

        foreach (var p in entry.Languages.ToList())
        {
            ImGui.PushID(p.Key);
            ImGui.TextUnformatted(p.Key.Substring(5));
            // "\n" is stored as a literal escape in the file; edit it as a real line break.
            var text = p.Value.Replace("\\n", "\n");
            float h = ImGui.GetTextLineHeight() * Math.Clamp(text.Count(c => c == '\n') + 2, 2, 12) + 8;
            var flags = editing ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly;
            if (ImGui.InputTextMultiline("##t", ref text, 8192, new Vec2(-1, h), flags) && editing)
            {
                p.Value = EscapeStr(text);
                sf.Dirty = true;
            }
            ImGui.PopID();
        }
        ImGui.PopID();
    }

    /// <summary>Writes the .str as a loose file and forces a reload from it.</summary>
    private bool SaveStr(StrFile sf)
    {
        if (WriteLoose(sf.Entry.Path, sf.Serialize()) == null) return false;
        sf.Dirty = false;
        _strCache.Remove(sf.Entry.Path);
        _factionTitles = null;
        return true;
    }

    // ---------------------------------------------------------------- modals

    private void OpenCreateTranslation(Block b, ParsedFile f, Prop p, bool convertName)
    {
        _mBlock = b; _mFile = f; _mProp = p; _mConvertName = convertName; _mError = "";
        var names = EnglishStrNames();
        var key = convertName ? "" : p.Value;

        // Default target: the .str whose name prefixes the current key, else a sensible per-field default.
        string? match = names.Where(nm => key.StartsWith(nm + "_", StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(nm => nm.Length).FirstOrDefault();
        _mStrName = match ?? (b.Type == "Faction" ? "swgl_factions" : "swgl_characters");
        _mRef = match != null ? key[(match.Length + 1)..] : (b.Get("code") ?? "");
        _mText = convertName ? p.Value : "";
        _openModalName = ModalCreateTr;
    }

    private void OpenAddLanguage(StrFile sf, StrEntry entry)
    {
        _mStr = sf; _mEntry = entry; _mError = ""; _mText = "";
        _mLang = StrFile.KnownLanguages.FirstOrDefault(l => entry.Language(l) == null) ?? "";
        _openModalName = ModalAddLang;
    }

    private void OpenModelModal(Block b, ParsedFile f, Prop p)
    {
        _mBlock = b; _mFile = f; _mProp = p; _mError = "";
        _mFoldersStale = true;
        _openModalName = ModalModel;
    }

    /// <summary>Names of the .str files that exist under strings/English.</summary>
    private List<string> EnglishStrNames() =>
        StrByName().Where(kv => kv.Value.Any(e => e.Path.StartsWith("strings/english/", StringComparison.OrdinalIgnoreCase)))
                   .Select(kv => kv.Key).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

    private static void CenterNextWindow(float w, float h)
    {
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowSize(new Vec2(Math.Min(w, vp.Size.X - 40), Math.Min(h, vp.Size.Y - 40)), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(vp.Size / 2, ImGuiCond.Appearing, new Vec2(0.5f, 0.5f));
    }

    private void DrawModals()
    {
        if (_openModalName != null) { ImGui.OpenPopup(_openModalName); _openModalName = null; }
        DrawCreateTranslationModal();
        DrawAddLanguageModal();
        DrawImportModal();
        DrawMoveModal();
        DrawModelModal();
        DrawTransferModal();
        DrawFileNameModal();
    }

    private void ModalError()
    {
        if (_mError.Length > 0) ImGui.TextColored(new Vec4(1f, 0.4f, 0.4f, 1f), _mError);
    }

    private void DrawCreateTranslationModal()
    {
        CenterNextWindow(560, 340);
        bool open = true;
        if (!ImGui.BeginPopupModal(ModalCreateTr, ref open)) return;

        ImGui.TextWrapped("Adds a new entry to a .str file (saved as a loose copy in SWGL/) and points the field at it.");
        var names = EnglishStrNames();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("String file", _mStrName))
        {
            foreach (var nm in names)
                if (ImGui.Selectable(nm, nm.Equals(_mStrName, StringComparison.OrdinalIgnoreCase))) _mStrName = nm;
            ImGui.EndCombo();
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("Reference", ref _mRef, 128);
        ImGui.TextUnformatted("English text");
        ImGui.InputTextMultiline("##text", ref _mText, 4096, new Vec2(-1, 90));
        ModalError();

        if (ImGui.Button("Create"))
        {
            try { CreateTranslation(); ImGui.CloseCurrentPopup(); }
            catch (Exception ex) { _mError = ex.Message; }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void CreateTranslation()
    {
        var reference = _mRef.Trim().Replace(' ', '_');
        if (reference.Length == 0) throw new InvalidOperationException("The reference is empty.");
        var entryFile = StrByName()[_mStrName].First(e => e.Path.StartsWith("strings/english/", StringComparison.OrdinalIgnoreCase));
        var sf = LoadStr(entryFile);
        if (sf.Error != null) throw new InvalidOperationException(sf.Error);
        if (sf.Find(reference) != null) throw new InvalidOperationException($"{reference} already exists in {_mStrName}.");

        sf.AddEntry(reference, "ENGLISH", EscapeStr(_mText));
        if (!SaveStr(sf)) throw new InvalidOperationException(_status);

        var p = _mProp!;
        bool upper = p.Key.StartsWith("description", StringComparison.OrdinalIgnoreCase);
        var prefix = upper ? _mStrName.ToUpperInvariant() : _mStrName.ToLowerInvariant();
        var refPart = upper ? reference.ToUpperInvariant() : reference.ToLowerInvariant();
        if (_mConvertName) { p.Key = "nameKey"; p.Quoted = false; }
        p.Value = prefix + "_" + refPart;
        _mFile!.Dirty = true;
    }

    private void DrawAddLanguageModal()
    {
        CenterNextWindow(520, 280);
        bool open = true;
        if (!ImGui.BeginPopupModal(ModalAddLang, ref open)) return;

        var free = StrFile.KnownLanguages.Where(l => _mEntry != null && _mEntry.Language(l) == null).ToList();
        ImGui.TextUnformatted(_mEntry != null ? $"Entry {_mEntry.Reference}" : "");
        if (free.Count == 0) ImGui.TextDisabled("All known languages are already translated.");
        else
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("Language", _mLang))
            {
                foreach (var l in free) if (ImGui.Selectable(l, l == _mLang)) _mLang = l;
                ImGui.EndCombo();
            }
            ImGui.InputTextMultiline("##text", ref _mText, 4096, new Vec2(-1, 100));
        }
        ModalError();

        if (free.Count > 0 && ImGui.Button("Add"))
        {
            _mStr!.AddLanguage(_mEntry!, _mLang, EscapeStr(_mText));
            if (SaveStr(_mStr)) ImGui.CloseCurrentPopup();
        }
        if (free.Count > 0) ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawImportModal()
    {
        CenterNextWindow(560, 230);
        bool open = true;
        if (!ImGui.BeginPopupModal(ModalImport, ref open)) return;

        var picked = _mPicked;
        if (picked != null) { _mSource = picked; _mPicked = null; }

        if (ImGui.Button("Choose image file...")) FilePicker.PickImage(file => _mPicked = file);
        ImGui.SameLine();
        ImGui.TextWrapped(_mSource.Length > 0 ? _mSource : "(no file chosen)");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("Save as", ref _mDest, 512);
        ImGui.TextDisabled("Game path of the image (png or tga). Saved as a loose file in SWGL/; converted if the format differs.");
        ModalError();

        if (_mSource.Length == 0) ImGui.BeginDisabled();
        if (ImGui.Button("Import"))
        {
            try
            {
                _mProp!.Value = _images.Import(_mDest, _mSource);
                _mFile!.Dirty = true;
                _status = "Image imported: " + _mProp.Value;
                ImGui.CloseCurrentPopup();
            }
            catch (Exception ex) { _mError = ex.Message; }
        }
        if (_mSource.Length == 0) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawMoveModal()
    {
        CenterNextWindow(560, 190);
        bool open = true;
        if (!ImGui.BeginPopupModal(ModalMove, ref open)) return;

        ImGui.TextWrapped("Current: " + _mProp?.Value);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("New location", ref _mDest, 512);
        ImGui.TextDisabled("A copy is written to SWGL/ and the field is updated. An image inside a pk3 stays there.");
        ModalError();

        if (ImGui.Button("Move"))
        {
            try
            {
                _mProp!.Value = _images.Move(_mProp.Value, _mDest);
                _mFile!.Dirty = true;
                _status = "Image moved: " + _mProp.Value;
                ImGui.CloseCurrentPopup();
            }
            catch (Exception ex) { _mError = ex.Message; }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawModelModal()
    {
        CenterNextWindow(520, 480);
        bool open = true;
        if (!ImGui.BeginPopupModal(ModalModel, ref open)) return;

        ImGui.TextWrapped($"Folders under {ModelsRoot}/. Choosing one resets the icon to its default path.");
        if (_mFoldersStale)
        {
            _mFolders = _index!.Files.Keys
                .Where(k => k.StartsWith(ModelsRoot + "/", StringComparison.OrdinalIgnoreCase) &&
                            k.EndsWith(".glm", StringComparison.OrdinalIgnoreCase))
                .Select(k => k.Split('/')[2])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _mFoldersStale = false;
        }

        if (ImGui.Button(_refreshing ? "Refreshing..." : "Refresh")) RefreshIndex();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##mf", "Filter...", ref _mFolderFilter, 128);

        float footer = ImGui.GetFrameHeightWithSpacing() + 4;
        if (ImGui.BeginChild("##folders", new Vec2(0, -footer), true))
        {
            foreach (var folder in _mFolders)
            {
                if (_mFolderFilter.Length > 0 && !folder.Contains(_mFolderFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (!ImGui.Selectable(folder, folder.Equals(_mProp?.Value, StringComparison.OrdinalIgnoreCase))) continue;
                ApplyModelFolder(folder);
                ImGui.CloseCurrentPopup();
                break;
            }
        }
        ImGui.EndChild();

        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void ApplyModelFolder(string folder)
    {
        var b = _mBlock!;
        _mProp!.Value = folder;

        // The icon goes back to the default location for this model.
        var icon = IconProp(b);
        if (icon != null) icon.Value = $"gfx/characters/{folder}/default.tga";

        // Keep the skin when the new folder has it, otherwise fall back to model_default or the first skin.
        var skin = b.Props.FirstOrDefault(x => x.Key.Equals("skin", StringComparison.OrdinalIgnoreCase));
        if (skin != null)
        {
            var prefix = $"{ModelsRoot}/{folder}/";
            var skins = _index!.Files.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.EndsWith(".skin", StringComparison.OrdinalIgnoreCase))
                .Select(k => Path.GetFileNameWithoutExtension(k)).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            if (skins.Count > 0 && !skins.Contains(skin.Value, StringComparer.OrdinalIgnoreCase))
                skin.Value = skins.FirstOrDefault(s => s.Equals("model_default", StringComparison.OrdinalIgnoreCase)) ?? skins[0];
        }
        _mFile!.Dirty = true;
    }

    // ---------------------------------------------------------------- new / rename file

    private Node? _fnNode;
    private bool _fnRename;
    private string _fnName = "";
    private string _fnError = "";
    private string _fnTitle = "New file";

    private static bool IsFileCategory(Node n) =>
        n.Kind == NodeKind.Category && (n.Title == "Characters" || n.Title == "Factions");

    /// <summary>Right-click menu of the explorer: "New file..." on a category, "Rename file..." on a file.</summary>
    private void DrawNodeContextMenu(Node n)
    {
        bool category = IsFileCategory(n);
        if (!category && n.Kind != NodeKind.File) return;
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _selected = n;
        if (!ImGui.BeginPopupContextItem("##ctx")) return;

        if (category && ImGui.MenuItem("New file...")) OpenFileName(n, rename: false);
        if (!category && ImGui.MenuItem("Rename file...")) OpenFileName(n, rename: true);
        ImGui.EndPopup();
    }

    private Node CategoryOf(Node fileNode) => _roots.First(r => r.Children.Contains(fileNode));

    private static string ExtensionOf(Node category) => category.Title == "Factions" ? ".fac" : ".cha";

    private void OpenFileName(Node n, bool rename)
    {
        _fnNode = n;
        _fnRename = rename;
        _fnError = "";
        _fnName = rename ? Path.GetFileNameWithoutExtension(n.File!.Entry.Path) : "";
        _fnTitle = rename ? "Rename file" : "New file";
        _openModalName = _fnTitle;
    }

    private void DrawFileNameModal()
    {
        if (_fnNode == null) return;
        CenterNextWindow(480, 170);
        bool open = true;
        if (!ImGui.BeginPopupModal(_fnTitle, ref open)) return;

        var category = _fnRename ? CategoryOf(_fnNode) : _fnNode;
        var ext = ExtensionOf(category);
        ImGui.TextDisabled($"{CharactersDir}/");
        ImGui.SetNextItemWidth(-ImGui.CalcTextSize(ext).X - 16);
        bool enter = ImGui.InputText("##fname", ref _fnName, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        ImGui.TextUnformatted(ext);
        if (_fnError.Length > 0) ImGui.TextColored(new Vec4(1f, 0.4f, 0.4f, 1f), _fnError);

        if (ImGui.Button(_fnRename ? "Rename" : "Create") || enter)
        {
            try
            {
                if (_fnRename) RenameFile(_fnNode, category, _fnName); else CreateFile(category, _fnName);
                ImGui.CloseCurrentPopup();
            }
            catch (Exception ex) { _fnError = ex.Message; }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    /// <summary>Validates a typed file name (extension optional) and returns the game path for it.</summary>
    private string ValidatedPath(Node category, string typed, ParsedFile? except)
    {
        var ext = ExtensionOf(category);
        var name = typed.Trim();
        if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name = name[..^ext.Length];
        if (name.Length == 0) throw new InvalidOperationException("The file name is empty.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("The name contains invalid characters.");

        var path = $"{CharactersDir}/{name}{ext}";
        if (_roots.SelectMany(r => r.Children).Any(c => c.File != except &&
                c.File!.Entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{name}{ext} already exists.");
        return path;
    }

    private void InsertSorted(Node category, Node file)
    {
        var index = category.Children.FindIndex(c =>
            string.Compare(c.File!.Name, file.File!.Name, StringComparison.OrdinalIgnoreCase) > 0);
        if (index < 0) category.Children.Add(file); else category.Children.Insert(index, file);
    }

    /// <summary>New empty file in the explorer; it is written to SWGL/ with the next Save.</summary>
    private void CreateFile(Node category, string typed)
    {
        var path = ValidatedPath(category, typed, null);
        var disk = Path.Combine(_gameDir!, "SWGL", path.Replace('/', Path.DirectorySeparatorChar));
        var pf = new ParsedFile
        {
            Entry = new Pk3Entry(path, "(new)", "SWGL", 0, disk, path, IsLoose: true),
            IsNew = true,
            Dirty = true,
        };
        var node = Node.BuildFile(pf);
        InsertSorted(category, node);
        _expanded.Add(category);
        _selected = node;
        _status = $"Created {pf.Name} (not saved yet).";
    }

    /// <summary>
    /// Renames by writing the current content under the new name. The old path is deleted when it was only a
    /// loose file; otherwise (it lives in a pk3, which is never modified) it is overridden by an empty loose file.
    /// </summary>
    private void RenameFile(Node fileNode, Node category, string typed)
    {
        var pf = fileNode.File!;
        var newPath = ValidatedPath(category, typed, pf);
        var old = pf.Entry;
        if (newPath.Equals(old.Path, StringComparison.OrdinalIgnoreCase) && newPath == old.Path) return;

        var created = WriteLoose(newPath, pf.Config.Serialize()) ?? throw new InvalidOperationException(_status);

        if (!pf.IsNew)
        {
            if (old.IsLoose && !_index!.IsShadowed(old.Path))
            {
                try { File.Delete(old.Pk3FullPath); } catch { /* the empty override below is used instead */ }
                if (File.Exists(old.Pk3FullPath)) WriteLoose(old.Path, "");
                else _index.Remove(old.Path);
            }
            else WriteLoose(old.Path, "");
        }

        pf.Entry = created;
        pf.IsNew = false;
        pf.Dirty = false;
        category.Children.Remove(fileNode);
        InsertSorted(category, fileNode);
        _selected = fileNode;
        _status = $"Renamed to {pf.Name}.";
    }

    // ---------------------------------------------------------------- copy to / move to

    private Node? _tNode;           // character or variant being transferred
    private bool _tMove;
    private string _tTitle = "Copy to";
    private string _tFilter = "";

    private static bool IsTransferable(Node n) => n.Kind == NodeKind.Block && IsCharacterFile(n);

    /// <summary>"Copy to" / "Move to" above the properties of a character (target: a file) or a variant (target: a character).</summary>
    private void DrawTransferButtons(Node n)
    {
        if (!IsTransferable(n)) return;
        bool variant = n.Block!.Type == "Variant";
        if (ImGui.Button("Copy to")) OpenTransfer(n, move: false);
        ImGui.SameLine();
        if (ImGui.Button("Move to")) OpenTransfer(n, move: true);
        ImGui.SameLine();
        ImGui.TextDisabled(variant ? "(to another character)" : "(to a file)");
    }

    private void OpenTransfer(Node n, bool move)
    {
        _tNode = n;
        _tMove = move;
        _tFilter = "";
        _tTitle = (move ? "Move to" : "Copy to") + (n.Block!.Type == "Variant" ? " character" : " file");
        _openModalName = _tTitle;
    }

    private Node? FindParent(Node target, Node? from = null)
    {
        foreach (var root in from == null ? _roots : from.Children)
        {
            if (root.Children.Contains(target)) return root;
            if (FindParent(target, root) is { } p) return p;
        }
        return null;
    }

    private static Block CloneBlock(Block b)
    {
        var c = new Block { Type = b.Type };
        foreach (var p in b.Props) c.Props.Add(new Prop { Key = p.Key, Value = p.Value, Quoted = p.Quoted });
        foreach (var child in b.Children) c.Children.Add(CloneBlock(child));
        return c;
    }

    private static string UniqueCode(string code, ICollection<string> taken)
    {
        if (!taken.Contains(code, StringComparer.OrdinalIgnoreCase)) return code;
        for (int i = 1; ; i++)
        {
            var candidate = i == 1 ? code + "_COPY" : code + "_COPY" + i;
            if (!taken.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return candidate;
        }
    }

    private void DrawTransferModal()
    {
        if (_tNode == null) return;
        CenterNextWindow(560, 460);
        bool open = true;
        if (!ImGui.BeginPopupModal(_tTitle, ref open)) return;

        var src = _tNode;
        bool variant = src.Block!.Type == "Variant";
        var sourceParent = FindParent(src);
        ImGui.TextUnformatted($"{(_tMove ? "Move" : "Copy")} {src.Label}");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##tf", "Filter...", ref _tFilter, 128);

        float footer = ImGui.GetFrameHeightWithSpacing() + 4;
        Node? chosen = null;
        if (ImGui.BeginChild("##targets", new Vec2(0, -footer), true))
        {
            var root = _roots.FirstOrDefault(r => r.Title == "Characters");
            var files = root?.Children ?? new List<Node>();
            if (!variant)
            {
                foreach (var file in files)
                {
                    if (_tMove && file == sourceParent) continue; // moving into its own file is a no-op
                    if (_tFilter.Length > 0 && !file.Label.Contains(_tFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ImGui.Selectable($"{file.Label}  ({file.Children.Count})")) chosen = file;
                }
            }
            else
            {
                foreach (var file in files)
                    foreach (var ch in file.Children)
                    {
                        if (_tMove && ch == sourceParent) continue;
                        var label = $"{ch.Label}   [{file.File!.Name}]";
                        if (_tFilter.Length > 0 && !label.Contains(_tFilter, StringComparison.OrdinalIgnoreCase)) continue;
                        if (ImGui.Selectable(label)) chosen = ch;
                    }
            }
        }
        ImGui.EndChild();

        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();

        if (chosen != null)
        {
            Transfer(src, sourceParent, chosen, variant);
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    /// <summary>
    /// Clones the block into the target (a file node for characters, a character node for variants);
    /// a move then removes the original. Copies get a unique code.
    /// </summary>
    private void Transfer(Node src, Node? sourceParent, Node target, bool variant)
    {
        var clone = CloneBlock(src.Block!);
        var codeProp = clone.Props.FirstOrDefault(p => p.Key.Equals("code", StringComparison.OrdinalIgnoreCase));

        // Characters share one code space; variants only need to be unique inside their character.
        var taken = variant
            ? target.Block!.Children.Select(c => c.Get("code") ?? "").ToList()
            : _roots.Where(r => r.Title == "Characters").SelectMany(r => r.Children).SelectMany(fn => fn.Children)
                    .Select(c => c.Block!.Get("code") ?? "").ToList();
        if (_tMove) taken.RemoveAll(c => c.Equals(src.Block.Get("code") ?? "", StringComparison.OrdinalIgnoreCase));
        if (codeProp != null) codeProp.Value = UniqueCode(codeProp.Value, taken);

        var targetFile = target.File!;
        if (variant) target.Block!.Children.Add(clone); else targetFile.Config.Blocks.Add(clone);
        var node = Node.BuildBlock(clone, targetFile);
        target.Children.Add(node);
        targetFile.Dirty = true;

        if (_tMove && sourceParent != null)
        {
            if (variant) sourceParent.Block!.Children.Remove(src.Block!);
            else sourceParent.File!.Config.Blocks.Remove(src.Block!);
            sourceParent.Children.Remove(src);
            sourceParent.File!.Dirty = true;
        }

        // Show the result: expand its ancestors and select it.
        _expanded.Add(target);
        if (variant && FindParent(target) is { } file) _expanded.Add(file);
        _selected = node;
        _status = $"{(_tMove ? "Moved" : "Copied")} {node.Label} to {(variant ? target.Label : target.File!.Name)}.";
        _knownTags = null;
        _tNode = null;
    }

    // ---------------------------------------------------------------- index refresh

    private void RefreshIndex()
    {
        if (_refreshing || _gameDir == null) return;
        _refreshing = true;
        var dir = _gameDir;
        Task.Run(() =>
        {
            try { _pendingIndex = Pk3Index.Build(dir); }
            catch (Exception ex) { _status = "Refresh failed: " + ex.Message; }
            finally { _refreshing = false; }
        });
    }

    /// <summary>Swaps in a refreshed index on the main thread (the explorer tree and unsaved edits are kept).</summary>
    private void ApplyPendingIndex()
    {
        var idx = _pendingIndex;
        if (idx == null) return;
        _pendingIndex = null;
        _index = idx;
        _strByName = null;
        foreach (var k in _strCache.Where(kv => !kv.Value.Dirty).Select(kv => kv.Key).ToList()) _strCache.Remove(k);
        _factionTitles = null;
        _images.Invalidate();
        _mFoldersStale = true;
        _status = $"Index refreshed: {idx.Files.Count} files.";
    }

    // ---------------------------------------------------------------- 3D preview

    /// <summary>models/players/&lt;model&gt;/&lt;skin&gt;.glm if indexed, otherwise the first glm of that folder.</summary>
    private Pk3Entry? ResolveGlm(string model, string skin)
    {
        var files = _index!.Files;
        var prefix = $"{ModelsRoot}/{model}/";
        if (files.TryGetValue(prefix + skin + ".glm", out var exact)) return exact;
        return files.Values
            .Where(e => e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        e.Path.EndsWith(".glm", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    // ---------------------------------------------------------------- skeleton & animations

    private readonly Dictionary<string, (GlaFile gla, List<AnimDef> anims)> _glaCache = new();
    private List<AnimDef> _anims = new();
    private string? _selectedAnimName = "BOTH_WALK1"; // animation currently played (null = bind pose); walk by default
    private string _animFilter = "";

    /// <summary>
    /// Loads the skeleton a mesh is skinned to (its animName + ".gla") and the animation.cfg next to it.
    /// Also publishes the animation list shown in the bottom-left explorer.
    /// </summary>
    private GlaFile? LoadGla(string animName)
    {
        _anims = new();
        animName = animName.Replace('\\', '/').TrimStart('/'); // some models store backslashes or a leading "/"
        if (animName.Length == 0 || !_index!.Files.TryGetValue(animName + ".gla", out var entry)) return null;

        var key = $"{entry.Path}|{entry.Pk3Name}|{entry.Size}";
        if (!_glaCache.TryGetValue(key, out var cached))
        {
            try
            {
                var gla = GlaFile.Parse(Pk3Reader.ReadBytes(entry));
                var dir = animName[..animName.LastIndexOf('/')];
                var anims = _index.Files.TryGetValue(dir + "/animation.cfg", out var cfg)
                    ? AnimationCfg.Parse(Pk3Reader.ReadText(cfg))
                    : new List<AnimDef>();
                _glaCache[key] = cached = (gla, anims);
            }
            catch (Exception ex)
            {
                _status = $"Skeleton {entry.Path}: {ex.Message}";
                return null;
            }
        }
        _anims = cached.anims;
        return cached.gla;
    }

    /// <summary>Second explorer (bottom left): animations of the loaded skeleton; picking one plays it in a loop.</summary>
    private void DrawAnimationList()
    {
        ImGui.TextUnformatted($"Animations ({_anims.Count})");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##animfilter", "Filter...", ref _animFilter, 64);
        if (ImGui.BeginChild("##anims", new Vec2(0, 0)))
        {
            if (_anims.Count == 0) ImGui.TextDisabled("Select a variant with a skeleton.");
            else if (ImGui.Selectable("(bind pose)", _selectedAnimName == null))
            {
                _selectedAnimName = null;
                _viewer.SetAnimation(null);
            }

            foreach (var a in _anims)
            {
                if (_animFilter.Length > 0 && !a.Name.Contains(_animFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (!ImGui.Selectable($"{a.Name}##{a.Start}", a.Name == _selectedAnimName)) continue;
                _selectedAnimName = a.Name;
                _viewer.SetAnimation(a);
            }
        }
        ImGui.EndChild();
    }

    /// <summary>Makes sure the viewer shows the model of <paramref name="b"/>; false when nothing can be shown.</summary>
    private bool EnsureModelLoaded(Block b)
    {
        var model = b.Get("model") ?? "";
        var skin = b.Get("skin") ?? "model_default";
        var glm = model.Length > 0 ? ResolveGlm(model, skin) : null;
        if (glm == null)
        {
            _viewer.Clear();
            _modelError = model.Length == 0 ? "No model set." : $"No GLM found in {ModelsRoot}/{model}/";
            return false;
        }

        _index!.Files.TryGetValue($"{ModelsRoot}/{model}/{skin}.skin", out var skinEntry);
        var key = $"{glm.Path}|{glm.Pk3Name}|{glm.Size}|{skinEntry?.Path}|{skinEntry?.Pk3Name}";
        if (key == _modelFailedKey) return false;

        if (_viewer.NeedsLoad(key))
        {
            try
            {
                var mesh = GlmModel.Parse(Pk3Reader.ReadBytes(glm));
                var skinMap = skinEntry != null ? SkinFile.Parse(Pk3Reader.ReadText(skinEntry)) : new();
                var gla = LoadGla(mesh.AnimName);
                _viewer.Load(key, mesh, skinMap, gla);
                _viewer.Anims = _anims;
                _viewer.SetAnimation(_anims.FirstOrDefault(a => a.Name == _selectedAnimName));
                _modelFailedKey = null;
                _modelCaption = $"{glm.Path}   skin: {(skinEntry != null ? skinEntry.Path : "none")}   " +
                                $"skeleton: {(gla != null ? mesh.AnimName + $" ({gla.Bones.Count} bones)" : "none")}";
            }
            catch (Exception ex)
            {
                _modelFailedKey = key;
                _modelError = $"{glm.Path}: {ex.Message}";
                _viewer.Clear();
                return false;
            }
        }
        _modelError = null;
        return true;
    }

    /// <summary>Center pane for a variant: orbitable model with the icon as a small square top right.</summary>
    private void DrawModelPreview(Block b)
    {
        var avail = ImGui.GetContentRegionAvail();
        int w = (int)avail.X - 2, h = (int)avail.Y - 2;
        if (w < 32 || h < 32) return;

        bool loaded = EnsureModelLoaded(b);
        var p0 = ImGui.GetCursorScreenPos();
        var id = _viewer.Render(w, h);
        ImGui.Image(id, new Vec2(w, h));
        ImGui.SetCursorScreenPos(p0);
        ImGui.InvisibleButton("##modelview", new Vec2(w, h), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        var io = ImGui.GetIO();
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left)) _viewer.Orbit(io.MouseDelta);
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Right)) _viewer.Pan(io.MouseDelta);
        if (ImGui.IsItemHovered()) _viewer.Zoom(io.MouseWheel);

        var dl = ImGui.GetWindowDrawList();
        uint white = ImGui.GetColorU32(new Vec4(1, 1, 1, 0.9f));
        const float iconSize = 72;
        var icon = IconProp(b) is { } ip ? _images.Get(ip.Value) : null;
        if (icon != null)
        {
            var q0 = new Vec2(p0.X + w - iconSize - 8, p0.Y + 8);
            var q1 = q0 + new Vec2(iconSize, iconSize);
            dl.AddImage(icon.Id, q0, q1);
            dl.AddRect(q0, q1, white);
        }
        dl.AddText(new Vec2(p0.X + 8, p0.Y + h - 20), white, loaded ? _modelCaption : (_modelError ?? ""));
    }

    // ---------------------------------------------------------------- diagnostics

    private void DebugBatchIfRequested()
    {
        var outPath = Environment.GetEnvironmentVariable("SWGL_DEBUG_BATCH");
        if (outPath == null || _debugDone || _indexing || _index == null) return;
        _debugDone = true;
        var sb = new System.Text.StringBuilder();
        var folders = _index.Files.Keys.Where(k => k.StartsWith("models/players/", StringComparison.OrdinalIgnoreCase) && k.EndsWith(".glm", StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Split('/')[2]).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
        foreach (var folder in folders)
        {
            try
            {
                var glm = ResolveGlm(folder, "model_default")!;
                var mesh = GlmModel.Parse(Pk3Reader.ReadBytes(glm));
                var gla = LoadGla(mesh.AnimName);
                var size = mesh.Max - mesh.Min;
                string posed = "no-gla";
                if (gla != null)
                {
                    var anim = _anims.FirstOrDefault(a => a.Name == "BOTH_WALK1");
                    var mats = gla.Pose(new FrameSource(0, anim?.Length ?? 1, anim?.Start ?? 0), new FrameSource(0, 1, 0));
                    var min = new System.Numerics.Vector3(float.MaxValue); var max = new System.Numerics.Vector3(float.MinValue);
                    foreach (var s in mesh.Surfaces.Where(s => !s.Hidden))
                        for (int v = 0; v < s.Verts.Length; v++)
                        {
                            var p = new System.Numerics.Vector3(s.Verts[v].Position.X, s.Verts[v].Position.Y, s.Verts[v].Position.Z);
                            System.Numerics.Vector3 acc = default;
                            for (int k = 0; k < 4; k++)
                            {
                                float w = s.Weight[v * 4 + k]; if (w <= 0) continue;
                                int b = s.Bone[v * 4 + k]; if (b < 0 || b >= mats.Length) { acc += w * p; continue; }
                                acc += w * System.Numerics.Vector3.Transform(p, mats[b]);
                            }
                            min = System.Numerics.Vector3.Min(min, acc); max = System.Numerics.Vector3.Max(max, acc);
                        }
                    var ps = max - min;
                    int badBone = 0, negW = 0, totalV = 0;
                    foreach (var s in mesh.Surfaces.Where(s => !s.Hidden))
                        for (int v = 0; v < s.Verts.Length; v++)
                        {
                            totalV++;
                            bool bad = false, neg = false;
                            for (int k = 0; k < 4; k++) { if (s.Weight[v * 4 + k] > 0 && s.Bone[v * 4 + k] >= gla.Bones.Count) bad = true; if (s.Weight[v * 4 + k] < -0.001f) neg = true; }
                            if (bad) badBone++; if (neg) negW++;
                        }
                    posed = $"posed {ps.X:F0}x{ps.Y:F0}x{ps.Z:F0} z[{min.Z:F0},{max.Z:F0}] verts={totalV} badBone={badBone} negW={negW}";
                }
                sb.AppendLine($"{folder,-28} gla={mesh.AnimName,-45} bind {size.X:F0}x{size.Y:F0}x{size.Z:F0} z[{mesh.Min.Z:F0},{mesh.Max.Z:F0}]  {posed}");
            }
            catch (Exception ex) { sb.AppendLine($"{folder,-28} ERROR {ex.Message}"); }
        }
        {
            var gla = LoadGla("models/players/_humanoid/_humanoid")!;
            var glm2 = ResolveGlm("cybernetic_maul", "model_default")!;
            var mesh2 = GlmModel.Parse(Pk3Reader.ReadBytes(glm2));
            {
                var anim2 = _anims.FirstOrDefault(a => a.Name == "BOTH_WALK1")!;
                var alert = _anims.First(a => a.Name == "FACE_ALERT");
                var mats2 = gla.Pose(new FrameSource(0, anim2.Length, anim2.Start), new FrameSource(0, 1, alert.Start));
                var hs = mesh2.Surfaces.First(x => x.Name == "head");
                var list = new List<(float d, int v)>();
                for (int v = 0; v < hs.Verts.Length; v++)
                {
                    var p = new System.Numerics.Vector3(hs.Verts[v].Position.X, hs.Verts[v].Position.Y, hs.Verts[v].Position.Z);
                    System.Numerics.Vector3 acc = default;
                    for (int k = 0; k < 4; k++) { float w = hs.Weight[v * 4 + k]; if (w > 0) acc += w * System.Numerics.Vector3.Transform(p, mats2[hs.Bone[v * 4 + k]]); }
                    list.Add(((acc - p).Length(), v));
                }
                sb.AppendLine("head displacement avg " + list.Average(x => x.d).ToString("F1") + " max " + list.Max(x => x.d).ToString("F1"));
                foreach (var (d, v) in list.OrderBy(x => -x.d).Take(8))
                {
                    var pp = Enumerable.Range(0, 4).Where(k => hs.Weight[v * 4 + k] > 0).Select(k => $"{gla.Bones[hs.Bone[v * 4 + k]].Name}:{hs.Weight[v * 4 + k]:F2}");
                    sb.AppendLine($"  v{v} disp={d:F1} z={hs.Verts[v].Position.Z:F1} {string.Join(" ", pp)}");
                }
                foreach (int bi in new[] { 13, 14, 15, 52, 17 })
                {
                    var m = mats2[bi];
                    sb.AppendLine($"skin {gla.Bones[bi].Name}: T=({m.M41:F1},{m.M42:F1},{m.M43:F1}) row0=({m.M11:F2},{m.M12:F2},{m.M13:F2}) row1=({m.M21:F2},{m.M22:F2},{m.M23:F2}) row2=({m.M31:F2},{m.M32:F2},{m.M33:F2})");
                }
            }
            foreach (var s in mesh2.Surfaces.Where(s => !s.Hidden && s.Name.StartsWith("head")))
            {
                sb.AppendLine($"surface {s.Name} verts={s.Verts.Length} refs=[{string.Join(",", s.BoneRefs.Select(r => gla.Bones[Math.Min(r, gla.Bones.Count - 1)].Name))}]");
                foreach (var v in Enumerable.Range(0, s.Verts.Length).OrderBy(i => s.Verts[i].Position.Z).Take(6))
                {
                    var parts2 = Enumerable.Range(0, 4).Where(k => s.Weight[v * 4 + k] > 0).Select(k => $"{gla.Bones[Math.Min(s.Bone[v * 4 + k], gla.Bones.Count - 1)].Name}:{s.Weight[v * 4 + k]:F2}");
                    sb.AppendLine($"  v{v} z={s.Verts[v].Position.Z:F1} {string.Join(" ", parts2)}");
                }
            }
        }
        File.WriteAllText(outPath, sb.ToString());
        Exit();
    }

    private void DebugBonesIfRequested()
    {
        var outPath = Environment.GetEnvironmentVariable("SWGL_DEBUG_BONES");
        if (outPath == null || _debugDone || _indexing || _index == null) return;
        _debugDone = true;
        var gla = LoadGla("models/players/_humanoid/_humanoid")!;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < gla.Bones.Count; i++) sb.AppendLine($"{i,3} {gla.Bones[i].Name,-24} parent {gla.Bones[i].Parent}");
        sb.AppendLine("FACE anims: " + string.Join(", ", _anims.Where(a => a.Name.StartsWith("FACE_")).Select(a => $"{a.Name}[{a.Start},{a.Length}]")));
        sb.AppendLine("--- face bones at FACE_ALERT frame 0 (delta rotation angle deg, translation) ---");
        foreach (int bi in new[] { 13, 14, 15, 52, 16, 17, 18, 19, 20, 21, 22, 23 })
        {
            gla.GetLocal(0, bi, out var q, out var t);
            float angle = 2f * MathF.Acos(Math.Clamp(q.W, -1f, 1f)) * 180f / MathF.PI;
            sb.AppendLine($"{bi,3} {gla.Bones[bi].Name,-14} angle={angle,7:F2} t=({t.X:F2},{t.Y:F2},{t.Z:F2})");
        }
        foreach (int fr in new[] { 14651, 20438 })
            foreach (int bi in new[] { 15, 52, 17 })
            {
                gla.GetLocal(fr, bi, out var q, out var t);
                float angle = 2f * MathF.Acos(Math.Clamp(q.W, -1f, 1f)) * 180f / MathF.PI;
                sb.AppendLine($"frame {fr} {bi,3} {gla.Bones[bi].Name,-14} angle={angle,7:F2} t=({t.X:F2},{t.Y:F2},{t.Z:F2})");
            }
        File.WriteAllText(outPath, sb.ToString());
        Exit();
    }

    private void DebugParseIfRequested()
    {
        var outPath = Environment.GetEnvironmentVariable("SWGL_DEBUG_PARSE");
        if (outPath == null || _debugDone || _indexing || _index == null) return;
        _debugDone = true;
        var sb = new System.Text.StringBuilder();
        string Norm(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        int files = 0, bad = 0, variants = 0, chars = 0;
        foreach (var ext in new[] { ".cha", ".fac" })
            foreach (var e in _index.Find(CharactersDir, ext))
            {
                files++;
                var text = Pk3Reader.ReadText(e);
                var cfg = ConfigFile.Parse(text);
                foreach (var blk in cfg.Blocks) { chars++; variants += blk.Children.Count(c => c.Type == "Variant"); if (blk.Props.Any(p => p.Key == "Variant")) bad++; }
                if (Norm(cfg.Serialize()) != Norm(text)) { bad++; sb.AppendLine("roundtrip differs: " + e.Path); }
            }
        sb.AppendLine($"files={files} topBlocks={chars} variantBlocks={variants} problems={bad}");
        File.WriteAllText(outPath, sb.ToString());
        Exit();
    }

    /// <summary>SWGL_DEBUG_RENDER="model|skin|out.png" renders that model once to a png and exits.</summary>
    private void DebugRenderIfRequested()
    {
        var spec = Environment.GetEnvironmentVariable("SWGL_DEBUG_RENDER");
        if (spec == null || _debugDone || _indexing || _index == null) return;
        _debugDone = true;

        var parts = spec.Split('|');
        var b = new Block();
        b.Props.Add(new Prop { Key = "model", Value = parts[0] });
        b.Props.Add(new Prop { Key = "skin", Value = parts[1] });
        bool ok = EnsureModelLoaded(b);
        if (ok && parts.Length > 3)
        {
            var anim = _anims.FirstOrDefault(a => a.Name == parts[3]);
            _viewer.SetAnimation(anim);
        }
        int dw = 600, dh = 800;
        if (parts.Length > 4) { var wh = parts[4].Split('x'); dw = int.Parse(wh[0]); dh = int.Parse(wh[1]); }
        if (parts.Length > 5) { _viewer.DebugTime = double.Parse(parts[5], System.Globalization.CultureInfo.InvariantCulture); _viewer.SetCamera(parts.Length > 6 ? float.Parse(parts[6], System.Globalization.CultureInfo.InvariantCulture) : 0f, 0.05f, parts.Length > 7 ? float.Parse(parts[7], System.Globalization.CultureInfo.InvariantCulture) : 1f, parts.Length > 8 ? float.Parse(parts[8], System.Globalization.CultureInfo.InvariantCulture) : 0f); }
        if (ok) _viewer.SavePng(parts[2], dw, dh);
        File.WriteAllText(parts[2] + ".txt", (ok ? "ok: " + _modelCaption : "failed: " + _modelError));
        Exit();
    }
}
