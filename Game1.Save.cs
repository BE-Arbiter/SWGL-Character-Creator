using ImGuiNET;
using Vec2 = System.Numerics.Vector2;

namespace SWGLCharacterCreator;

public partial class Game1
{
    private const string ModalLooseMask = "Loose files hide these changes";
    private const string ModalSaved = "Changes pk3 created";

    private Dictionary<string, byte[]>? _saveChanges;   // collected changes waiting for the loose-files answer
    private List<string> _saveMasking = new();
    private string _savedPath = "";
    private bool _openLooseMask, _openSaved;

    private IEnumerable<ParsedFile> AllParsedFiles() =>
        _roots.SelectMany(r => r.Children).Where(c => c.File != null).Select(c => c.File!);

    private bool HasPendingChanges() =>
        _pending.Any || AllParsedFiles().Any(f => f.Dirty) || _strCache.Values.Any(s => s.Dirty) || (_defs?.AnyDirty ?? false);

    /// <summary>All unsaved edits as path -> bytes. The single place where the change sources are merged (later sources win).</summary>
    private Dictionary<string, byte[]> CollectChanges()
    {
        var changes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (p, b) in _pending.Items) changes[p] = b;
        foreach (var f in AllParsedFiles().Where(f => f.Dirty && f.Error == null))
            changes[f.Entry.Path] = System.Text.Encoding.Latin1.GetBytes(f.Config.Serialize());
        foreach (var s in _strCache.Values.Where(s => s.Dirty))
            changes[s.Entry.Path] = System.Text.Encoding.Latin1.GetBytes(s.Serialize());
        if (_defs != null)
            foreach (var (p, b) in _defs.CollectChanges(ChaConfigs())) changes[p] = b;
        return changes;
    }

    private IEnumerable<ConfigFile> ChaConfigs() =>
        _roots.Where(r => r.Title == "Characters").SelectMany(r => r.Children)
              .Where(c => c.File != null && c.File.Error == null).Select(c => c.File!.Config);

    private void BeginSave()
    {
        if (_gameDir == null || _index == null || _indexing) return;
        var changes = CollectChanges();
        if (changes.Count == 0) return;
        List<string> masking;
        try { masking = SavePlan.FindMaskingLoose(_gameDir, changes.Keys); }
        catch (Exception ex) { _status = "Save failed: " + ex.Message; return; }
        if (masking.Count == 0) { ExecuteSave(changes, false); return; }
        _saveChanges = changes;
        _saveMasking = masking;
        _openLooseMask = true;
    }

    private void ExecuteSave(Dictionary<string, byte[]> changes, bool deleteLoose)
    {
        SaveOutcome outcome;
        try { outcome = SavePlan.Commit(_gameDir!, DateTime.Now, changes, deleteLoose, _index!); }
        catch (Exception ex) { _status = "Save failed: " + ex.Message; return; }

        foreach (var f in AllParsedFiles().Where(f => changes.ContainsKey(f.Entry.Path)))
        {
            f.Entry = _index!.Files[f.Entry.Path];
            f.Dirty = false;
            f.IsNew = false;
        }
        foreach (var k in _strCache.Keys.ToList())
        {
            var s = _strCache[k];
            if (changes.ContainsKey(s.Entry.Path)) { s.Entry = _index!.Files[s.Entry.Path]; s.Dirty = false; }
            else if (!s.Dirty) _strCache.Remove(k);
        }
        _defs?.AfterSave(ChaConfigs());
        _pending.Clear();
        _strByName = null;
        _factionTitles = null;

        _status = $"Saved {changes.Count} file(s) to {Path.GetFileName(outcome.Result.Pk3Path)}." +
                  (outcome.DeleteErrors.Count > 0 ? " Could not delete loose file(s): " + string.Join("; ", outcome.DeleteErrors) : "");
        if (outcome.Result.Created) { _savedPath = outcome.Result.Pk3Path; _openSaved = true; }
    }

    private void HandleSaveShortcut()
    {
        var io = ImGui.GetIO();
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S, false) && !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId)
            && HasPendingChanges()) BeginSave();
    }

    private void DrawSaveModals()
    {
        if (_openLooseMask) { _openLooseMask = false; ImGui.OpenPopup(ModalLooseMask); }
        if (_openSaved) { _openSaved = false; ImGui.OpenPopup(ModalSaved); }

        CenterNextWindow(560, 300);
        bool open = true;
        if (ImGui.BeginPopupModal(ModalLooseMask, ref open))
        {
            if (ImGui.BeginChild("##masklist", new Vec2(0, -ImGui.GetFrameHeightWithSpacing() * 3), true))
                foreach (var p in _saveMasking) ImGui.TextUnformatted(p);
            ImGui.EndChild();
            ImGui.TextWrapped("These loose files in SWGL/ override every pk3, so the saved changes would not be visible in game.");
            if (ImGui.Button("Delete loose files and save"))
            {
                var c = _saveChanges; _saveChanges = null;
                ImGui.CloseCurrentPopup();
                if (c != null) ExecuteSave(c, true);
            }
            ImGui.SameLine();
            if (ImGui.Button("Save and keep them"))
            {
                var c = _saveChanges; _saveChanges = null;
                ImGui.CloseCurrentPopup();
                if (c != null) ExecuteSave(c, false);
            }
            ImGui.EndPopup();
        }

        CenterNextWindow(560, 170);
        open = true;
        if (ImGui.BeginPopupModal(ModalSaved, ref open))
        {
            ImGui.TextWrapped($"Created {_savedPath}.");
            ImGui.TextUnformatted("Restart the game to see the changes.");
            ImGui.TextWrapped("Ship this pk3 with the mod so other players get them too.");
            if (ImGui.Button("OK")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }
}
