using ImGuiNET;
using Vec2 = System.Numerics.Vector2;
using Vec4 = System.Numerics.Vector4;

namespace SWGLCharacterCreator;

/// <summary>Center pane tile grids: the characters of a faction, the variants of a character.</summary>
public partial class Game1
{
    private const float TileIcon = 96;
    private const float TilePad = 8;

    /// <summary>Draws the grid for a faction (block or .fac file) or a character; false when the node has none.</summary>
    private bool DrawGridFor(Node n)
    {
        if (n.File == null) return false;
        bool fac = n.File.Entry.Path.EndsWith(".fac", StringComparison.OrdinalIgnoreCase);

        if (fac && (n.Kind == NodeKind.File || n.Block?.Type == "Faction"))
        {
            var blocks = n.Kind == NodeKind.File ? n.Children.Select(c => c.Block!) : new[] { n.Block! };
            var codes = blocks.Select(b => b.Get("code")).OfType<string>().Where(c => c.Length > 0).ToList();
            var icon = n.Block != null ? IconProp(n.Block)?.Value : null;
            DrawTileGrid("List of Character", icon, CharactersOf(codes));
            return true;
        }
        if (!fac && n.Kind == NodeKind.Block && n.Block!.Type != "Variant" && IsCharacterFile(n))
        {
            DrawTileGrid("List of Variants for this character", IconProp(n.Block)?.Value,
                n.Children.Where(c => c.Block!.Type == "Variant").ToList());
            return true;
        }
        return false;
    }

    /// <summary>Character nodes whose "factions" field lists one of <paramref name="codes"/>, sorted by name.</summary>
    private List<Node> CharactersOf(List<string> codes)
    {
        var result = new List<Node>();
        if (codes.Count == 0) return result;
        var root = _roots.FirstOrDefault(r => r.Title == "Characters");
        if (root == null) return result;
        foreach (var file in root.Children)
            foreach (var ch in file.Children)
                if (SplitList(ch.Block!.Get("factions") ?? "").Any(f => codes.Contains(f, StringComparer.OrdinalIgnoreCase)))
                    result.Add(ch);
        return result.OrderBy(TileLabel, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string TileLabel(Node n)
    {
        var b = n.Block!;
        var name = b.Get("name");
        return !string.IsNullOrWhiteSpace(name) ? name : b.Get("code") ?? "?";
    }

    private void DrawTileGrid(string title, string? headerIcon, List<Node> items)
    {
        var dl = ImGui.GetWindowDrawList();
        if (headerIcon != null && _images.Get(headerIcon) is { } hi)
        {
            float s = ImGui.GetTextLineHeight() * 2;
            ImGui.Image(hi.Id, new Vec2(s, s));
            ImGui.SameLine();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (s - ImGui.GetTextLineHeight()) / 2);
        }
        ImGui.TextUnformatted($"{title} ({items.Count})");
        ImGui.Separator();
        if (items.Count == 0) { ImGui.TextDisabled("None."); return; }

        float lineH = ImGui.GetTextLineHeight();
        var cell = new Vec2(TileIcon + TilePad * 2, TileIcon + TilePad * 3 + lineH);
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        Node? clicked = null;

        if (ImGui.BeginChild("##tiles", Vec2.Zero))
        {
            int cols = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / (cell.X + spacing)));
            uint frame = ImGui.GetColorU32(ImGuiCol.Border);
            uint text = ImGui.GetColorU32(ImGuiCol.Text);
            for (int i = 0; i < items.Count; i++)
            {
                var n = items[i];
                if (i % cols != 0) ImGui.SameLine();
                ImGui.PushID(i);
                var p0 = ImGui.GetCursorScreenPos();
                if (ImGui.InvisibleButton("##tile", cell)) clicked = n;
                bool hovered = ImGui.IsItemHovered();
                var label = TileLabel(n) + (n.File!.Dirty ? " *" : "");
                if (hovered)
                {   // not SetTooltip: names are not format strings
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted($"{label}\n{n.Block!.Get("code")}  -  {n.File.Name}");
                    ImGui.EndTooltip();
                }

                if (hovered || _selected == n)
                    dl.AddRectFilled(p0, p0 + cell, ImGui.GetColorU32(hovered ? ImGuiCol.HeaderHovered : ImGuiCol.Header), 4f);

                var q0 = p0 + new Vec2(TilePad, TilePad);
                var q1 = q0 + new Vec2(TileIcon, TileIcon);
                var img = IconProp(n.Block!) is { } ip ? _images.Get(ip.Value) : null;
                if (img != null) dl.AddImage(img.Id, q0, q1);
                else
                {
                    dl.AddRectFilled(q0, q1, ImGui.GetColorU32(new Vec4(0.2f, 0.2f, 0.24f, 1f)), 4f);
                    var qs = ImGui.CalcTextSize("?");
                    dl.AddText(q0 + (new Vec2(TileIcon, TileIcon) - qs) / 2, ImGui.GetColorU32(ImGuiCol.TextDisabled), "?");
                }
                dl.AddRect(q0, q1, frame);

                var shown = Ellipsize(label, cell.X - 4);
                var ts = ImGui.CalcTextSize(shown);
                dl.AddText(new Vec2(p0.X + (cell.X - ts.X) / 2, q1.Y + TilePad), text, shown);
                ImGui.PopID();
            }
        }
        ImGui.EndChild();

        if (clicked != null) SelectInExplorer(clicked);
    }

    private static string Ellipsize(string s, float width)
    {
        if (ImGui.CalcTextSize(s).X <= width) return s;
        for (int len = s.Length - 1; len > 0; len--)
        {
            var t = s[..len] + "...";
            if (ImGui.CalcTextSize(t).X <= width) return t;
        }
        return "...";
    }

    /// <summary>Selects <paramref name="n"/> and expands every explorer node above it.</summary>
    private void SelectInExplorer(Node n)
    {
        for (var p = FindParent(n); p != null; p = FindParent(p)) _expanded.Add(p);
        _selected = n;
    }
}
