using ImGuiNET;
using Vec2 = System.Numerics.Vector2;
using Vec3 = System.Numerics.Vector3;
using Vec4 = System.Numerics.Vector4;

namespace SWGLCharacterCreator;

/// <summary>"Config" tab of a variant: edits its ext_data/characters_conf/CHAR_VARIANT_def.cfg.</summary>
public partial class Game1
{
    private const string ModalNewVariantConfig = "New variant config";
    private const int MaxCopyRows = 200;
    private const int CopyLoadsPerFrame = 8;

    private static readonly Vec4 WarnColor = new(1f, 0.65f, 0.2f, 1f);

    private readonly Dictionary<uint, string> _comboFilters = new();

    private Block? _newCfgVariant;
    private string _copyFilter = "";
    private List<(string Label, Block Variant)> _copySources = new();
    private readonly Dictionary<Block, bool> _copyHasDef = new();

    // ---------------------------------------------------------------- generic widgets

    /// <summary>Combo with a filter box at the top. Shows <paramref name="current"/> even when it is not in the list.</summary>
    private bool FilterCombo(string id, string current, IReadOnlyList<string> items, out string chosen)
    {
        chosen = current;
        var key = ImGui.GetID(id);
        if (!ImGui.BeginCombo("##" + id, current.Length > 0 ? current : "(none)", ImGuiComboFlags.HeightLargest))
        {
            _comboFilters.Remove(key);
            return false;
        }
        _comboFilters.TryGetValue(key, out var filter);
        filter ??= "";
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.InputTextWithHint("##flt", "Filter...", ref filter, 64);
        _comboFilters[key] = filter;

        bool changed = false;
        if (ImGui.BeginChild("##list", new Vec2(0, 240)))
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (filter.Length > 0 && !item.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                ImGui.PushID(i);
                if (ImGui.Selectable(item, item.Equals(current, StringComparison.OrdinalIgnoreCase)) &&
                    item != current)
                {
                    chosen = item;
                    changed = true;
                }
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        ImGui.EndCombo();
        return changed;
    }

    private static bool PlainCombo(string id, string current, IReadOnlyList<string> items, out string chosen)
    {
        chosen = current;
        bool changed = false;
        if (!ImGui.BeginCombo("##" + id, current.Length > 0 ? current : "(none)")) return false;
        foreach (var item in items)
            if (ImGui.Selectable(item, item.Equals(current, StringComparison.OrdinalIgnoreCase)) && item != current)
            {
                chosen = item;
                changed = true;
            }
        ImGui.EndCombo();
        return changed;
    }

    private static void CfgRow(string label)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.TableNextColumn();
    }

    private static bool BeginCfgTable(string id)
    {
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingStretchProp)) return false;
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.None, 1.2f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.None, 4f);
        return true;
    }

    // ---------------------------------------------------------------- tab

    private void DrawConfigTab(Node variantNode)
    {
        var b = variantNode.Block!;
        var defs = _defs!;
        var cat = _catalog!;
        var path = defs.CurrentPath(variantNode.File!.Config, b);
        if (path == null)
        {
            ImGui.TextDisabled("This variant needs a character code and a variant code to have a _def.cfg.");
            return;
        }

        ImGui.TextUnformatted(path);
        ImGui.TextDisabled(_index!.Files.TryGetValue(path, out var entry) && entry.Size > 0
            ? $"Source: {entry.Pk3Folder}/{entry.Pk3Name}"
            : "(new, not saved)");
        if (!defs.IsDirty(b)) ImGui.BeginDisabled();
        if (ImGui.Button("Discard config changes")) defs.ResetBinding(b);
        if (!defs.IsDirty(b)) ImGui.EndDisabled();

        var def = defs.Load(b);
        if (def == null)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("No _def.cfg for this variant.");
            if (ImGui.Button("Create empty")) defs.SetDef(b, DefFile.CreateEmpty());
            ImGui.SameLine();
            if (ImGui.Button("Copy from…")) OpenNewVariantConfig(b);
            return;
        }

        foreach (var msg in DefRules.Validate(def, cat)) ImGui.TextColored(WarnColor, msg);
        ImGui.Separator();

        void Set(string key, string value)
        {
            def.Set(key, value);
            defs.MarkDirty(b);
        }

        DrawConfigStats(def, cat, Set);
        DrawConfigWeapons(def, cat, Set);
        DrawConfigSabers(def, cat, Set);
        DrawLevelTable("Powers (0-3)", "##powers", DefEditorLogic.PowerLabels, DefKeys.Powers, def, Set);
        DrawLevelTable("Saber combat (0-3)", "##sabcombat", DefEditorLogic.SaberCombatLabels, DefKeys.SaberCombat, def, Set);
    }

    private void DrawConfigStats(DefFile def, GameDataCatalog cat, Action<string, string> set)
    {
        if (!ImGui.CollapsingHeader("Stats", ImGuiTreeNodeFlags.DefaultOpen) || !BeginCfgTable("##stats")) return;

        CfgRow("Health");
        if (IntField("##health", def.Get("ui_health"), 1, out var health)) set("ui_health", health);
        CfgRow("Force pool");
        if (IntField("##force", def.Get("ui_force"), 0, out var force)) set("ui_force", force);

        CfgRow("Team");
        ImGui.SetNextItemWidth(-1);
        if (PlainCombo("team", def.Get("ui_team") ?? "", DefRules.Teams, out var team)) set("ui_team", team);

        CfgRow("NPC type");
        var npc = def.Get("ui_npc_type") ?? "";
        ImGui.SetNextItemWidth(-(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X));
        if (FilterCombo("npc", npc, cat.NpcTypes, out var npcChosen)) set("ui_npc_type", npcChosen);
        ImGui.SameLine();
        if (npc.Length == 0) ImGui.BeginDisabled();
        if (ImGui.Button("x##clearnpc")) set("ui_npc_type", "");
        if (npc.Length == 0) ImGui.EndDisabled();

        CfgRow("Character tint");
        int r = DefEditorLogic.ParseInt(def.Get("ui_char_color_red"), 255);
        int g = DefEditorLogic.ParseInt(def.Get("ui_char_color_green"), 255);
        int bl = DefEditorLogic.ParseInt(def.Get("ui_char_color_blue"), 255);
        var col = new Vec3(Math.Clamp(r, 0, 255) / 255f, Math.Clamp(g, 0, 255) / 255f, Math.Clamp(bl, 0, 255) / 255f);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.ColorEdit3("##tint", ref col))
        {
            set("ui_char_color_red", DefEditorLogic.ToByte(col.X).ToString());
            set("ui_char_color_green", DefEditorLogic.ToByte(col.Y).ToString());
            set("ui_char_color_blue", DefEditorLogic.ToByte(col.Z).ToString());
        }
        ImGui.EndTable();
    }

    private readonly Dictionary<uint, int> _intScratch = new();

    /// <summary>
    /// Integer text field (no +/- buttons). The typed value lives in a scratch int while the field is being edited and is
    /// committed, clamped to <paramref name="min"/>, on Enter or when the field loses focus.
    /// </summary>
    private bool IntField(string id, string? value, int min, out string result)
    {
        result = "";
        var key = ImGui.GetID(id);
        int v = _intScratch.TryGetValue(key, out var scratch) ? scratch : DefEditorLogic.ParseInt(value, min);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt(id, ref v, 0, 0)) _intScratch[key] = v;
        bool enter = ImGui.IsItemActive() && (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter));
        if (!ImGui.IsItemDeactivatedAfterEdit() && !enter) return false;
        _intScratch.Remove(key);
        result = Math.Max(v, min).ToString();
        return result != value;
    }

    private void DrawConfigWeapons(DefFile def, GameDataCatalog cat, Action<string, string> set)
    {
        if (!ImGui.CollapsingHeader("Weapons", ImGuiTreeNodeFlags.DefaultOpen) || !BeginCfgTable("##weapons")) return;

        var all = cat.Weapons.Select(w => w.Name).ToList();
        if (!all.Contains("weapon_none")) all.Insert(0, "weapon_none");
        var usable = cat.Weapons.Where(w => w.PlayerUsable).Select(w => w.Name).Prepend("weapon_none").Distinct().ToList();

        CfgRow("NPC weapon");
        ImGui.SetNextItemWidth(-1);
        if (FilterCombo("npcweapon", def.Get("ui_npc_weapon") ?? "", all, out var nw)) set("ui_npc_weapon", nw);

        for (int i = 0; i < DefKeys.WeaponSlots.Length; i++)
        {
            CfgRow($"Slot {i + 1}");
            var key = DefKeys.WeaponSlots[i];
            ImGui.SetNextItemWidth(-1);
            if (FilterCombo("slot" + i, def.Get(key) ?? "", usable, out var w)) set(key, w);
        }
        ImGui.EndTable();
    }

    private void DrawConfigSabers(DefFile def, GameDataCatalog cat, Action<string, string> set)
    {
        if (!ImGui.CollapsingHeader("Sabers", ImGuiTreeNodeFlags.DefaultOpen) || !BeginCfgTable("##sabers")) return;

        var sabers = cat.Sabers.Prepend("empty").ToList();
        var colorKeys = new[] { "ui_saber_color", "ui_saber2_color" };
        var saberKeys = new[] { "ui_saber", "ui_saber2" };
        for (int i = 0; i < 2; i++)
        {
            CfgRow($"Saber {i + 1}");
            ImGui.SetNextItemWidth(-1);
            if (FilterCombo("saber" + i, def.Get(saberKeys[i]) ?? "", sabers, out var s)) set(saberKeys[i], s);

            CfgRow($"Color {i + 1}");
            var cur = def.Get(colorKeys[i]) ?? "";
            var label = DefEditorLogic.SaberColorLabel(cur);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##color" + i, label.Length > 0 ? label : "(none)"))
            {
                foreach (var item in DefEditorLogic.SaberColorChoices)
                    if (ImGui.Selectable(item, item.Equals(label, StringComparison.OrdinalIgnoreCase)) &&
                        !item.Equals(label, StringComparison.OrdinalIgnoreCase))
                        set(colorKeys[i], item == DefEditorLogic.CustomColor ? DefEditorLogic.CustomColorValue(cur) : item);
                ImGui.EndCombo();
            }
            if (DefRules.TryParseHex(cur, out var cr, out var cg, out var cb))
            {
                CfgRow("");
                var c = new Vec3(cr / 255f, cg / 255f, cb / 255f);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.ColorEdit3("##customcolor" + i, ref c))
                    set(colorKeys[i], DefRules.FormatHex(DefEditorLogic.ToByte(c.X), DefEditorLogic.ToByte(c.Y), DefEditorLogic.ToByte(c.Z)));
            }
        }

        CfgRow("Styles");
        var styles = def.Get("ui_saber_styles");
        int bits = DefEditorLogic.ParseInt(styles);
        foreach (var (name, bit) in DefRules.SaberStyles)
        {
            bool on = (bits & bit) != 0;
            if (ImGui.Checkbox(name, ref on)) set("ui_saber_styles", DefEditorLogic.ToggleBit(styles, bit));
            ImGui.SameLine();
        }
        ImGui.NewLine();

        CfgRow("Lightning color");
        ImGui.SetNextItemWidth(-1);
        if (PlainCombo("lightning", def.Get("ui_lightning_color") ?? "", DefRules.LightningColors, out var lc))
            set("ui_lightning_color", lc);
        ImGui.EndTable();
    }

    private static void DrawLevelTable(string title, string id, string[] labels, string[] keys, DefFile def, Action<string, string> set)
    {
        if (!ImGui.CollapsingHeader(title, ImGuiTreeNodeFlags.DefaultOpen) || !BeginCfgTable(id)) return;
        for (int i = 0; i < keys.Length; i++)
        {
            CfgRow(labels[i]);
            var cur = def.Get(keys[i]);
            for (int lvl = 0; lvl <= 3; lvl++)
            {
                bool active = cur == lvl.ToString();
                if (active) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
                if (ImGui.Button($"{lvl}##{keys[i]}", new Vec2(32, 0)) && !active) set(keys[i], lvl.ToString());
                if (active) ImGui.PopStyleColor();
                if (lvl < 3) ImGui.SameLine();
            }
        }
        ImGui.EndTable();
    }

    // ---------------------------------------------------------------- new variant config

    private void OpenNewVariantConfig(Block variant)
    {
        _newCfgVariant = variant;
        _copyFilter = "";
        _copyHasDef.Clear();
        _copySources = DefEditorLogic.CopySources(ChaConfigs());
        _openModalName = ModalNewVariantConfig;
    }

    private void DrawNewVariantConfigModal()
    {
        CenterNextWindow(460, 420);
        bool open = true;
        if (!ImGui.BeginPopupModal(ModalNewVariantConfig, ref open)) return;

        var target = _newCfgVariant;
        if (target == null || _defs == null) { ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }

        if (ImGui.Button("Empty config"))
        {
            _defs.SetDef(target, DefFile.CreateEmpty());
            ImGui.CloseCurrentPopup();
        }
        ImGui.Separator();
        ImGui.TextUnformatted("Copy from…");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##copyfilter", "Filter...", ref _copyFilter, 64);

        if (ImGui.BeginChild("##copylist", new Vec2(0, ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing())))
        {
            int shown = 0, loads = 0;
            bool more = false;
            foreach (var (label, variant) in DefEditorLogic.FilterSources(_copySources, _copyFilter))
            {
                if (ReferenceEquals(variant, target)) continue;
                if (!_copyHasDef.TryGetValue(variant, out var has))
                {
                    if (loads++ >= CopyLoadsPerFrame) { more = true; break; }   // keep the frame short; continues next frame
                    _copyHasDef[variant] = has = _defs.Load(variant) != null;
                }
                if (!has) continue;
                if (shown++ >= MaxCopyRows) { ImGui.TextDisabled("Refine the filter to see more."); break; }
                ImGui.PushID(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(variant));
                if (ImGui.Selectable(label) && _defs.Load(variant) is { } src)
                {
                    _defs.SetDef(target, src.Clone());
                    ImGui.CloseCurrentPopup();
                }
                ImGui.PopID();
            }
            if (more) ImGui.TextDisabled("Loading...");
        }
        ImGui.EndChild();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}
