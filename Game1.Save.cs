using ImGuiNET;
using Vec2 = System.Numerics.Vector2;
using Vec4 = System.Numerics.Vector4;

namespace SWGLCharacterCreator;

public partial class Game1
{
    private const string ModalLooseMask = "Loose files hide these changes";
    private const string ModalSaved = "Changes pk3 created";

    private Dictionary<string, byte[]>? _saveChanges;   // collected changes waiting for the loose-files answer
    private List<string> _saveMasking = new();
    private const string ModalUnsaved = "Unsaved changes";
    private Action? _afterUnsaved;   // the quit / re-index / folder change waiting for the unsaved-changes answer
    private Action? _afterSave;      // runs once a save started from the unsaved-changes dialog has fully succeeded
    private bool _openUnsaved, _allowExit;
    private volatile int _saveGeneration;   // bumped by every successful save so that an older index refresh is ignored
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

    /// <summary>
    /// Starts the save flow with the review-changes screen. <paramref name="then"/> runs only after a successful save
    /// that leaves nothing pending (or, from the review screen, once every change was reverted).
    /// </summary>
    private void BeginSave(Action? then = null)
    {
        _afterSave = null;
        if (_gameDir == null || _index == null || _indexing) return;
        _defs?.Prune(ChaConfigs());
        if (CollectChanges().Count == 0) { _status = "Nothing to save."; return; }
        _reviewThen = then;
        _reviewStale = true;
        _reviewSel = 0;
        _openReview = true;
    }

    /// <summary>Second half of the save flow, after the review: loose-file check, then the write.</summary>
    private void ProceedSave(Action? then)
    {
        _afterSave = null;
        if (_gameDir == null || _index == null || _indexing) return;
        _defs?.Prune(ChaConfigs());
        var changes = CollectChanges();
        if (changes.Count == 0) { _status = "Nothing to save."; return; }
        List<string> masking;
        try { masking = SavePlan.FindMaskingLoose(_gameDir, changes.Keys); }
        catch (Exception ex) { _status = "Save failed: " + ex.Message; return; }
        _afterSave = then;
        if (masking.Count == 0) { ExecuteSave(changes, false); return; }
        _saveChanges = changes;
        _saveMasking = masking;
        _openLooseMask = true;
    }

    private void ExecuteSave(Dictionary<string, byte[]> changes, bool deleteLoose)
    {
        SaveOutcome outcome;
        try { outcome = SavePlan.Commit(_gameDir!, DateTime.Now, changes, deleteLoose, _index!); }
        catch (Exception ex) { _status = "Save failed: " + ex.Message; _afterSave = null; return; }

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
        _saveGeneration++;
        _pendingIndex = null;
        _strByName = null;
        _factionTitles = null;

        _status = $"Saved {changes.Count} file(s) to {Path.GetFileName(outcome.Result.Pk3Path)}." +
                  (outcome.DeleteErrors.Count > 0 ? " Could not delete loose file(s): " + string.Join("; ", outcome.DeleteErrors) : "");
        if (outcome.Result.Created) { _savedPath = outcome.Result.Pk3Path; _openSaved = true; }

        var then = _afterSave;
        _afterSave = null;
        if (then != null && !HasPendingChanges()) then();
    }

    /// <summary>Runs <paramref name="action"/> now, or after the user answers the unsaved-changes dialog.</summary>
    private void RunOrConfirm(Action action)
    {
        if (HasPendingChanges()) { _afterUnsaved = action; _openUnsaved = true; }
        else action();
    }

    private void HandleSaveShortcut()
    {
        var io = ImGui.GetIO();
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S, false) && !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId)
            && HasPendingChanges()) BeginSave();
    }

    // ---------------------------------------------------------------- review changes

    private const string ModalReview = "Review changes";

    private sealed class ReviewItem
    {
        public required string Path;
        public byte[] NewBytes = Array.Empty<byte>();
        public readonly List<string> Sources = new();
        public readonly List<Action> Reverts = new();
        public string? Kept;                 // why part of the change cannot be reverted from here
        public string Status = "";           // New / Modified / Deleted
        public string? OldText;              // saved version, null when there is none (new file or binary)
        public bool Binary;
        public List<DiffLine?>? Diff;        // built when the item is first shown
        public List<Change>? Changes;        // readable, individually revertible changes; null for file types without one
        public string? Impact;               // what "Revert this file" does besides undoing the listed changes
    }

    /// <summary>A revert waiting for confirmation: what is undone and what else it does.</summary>
    private sealed record PendingRevert(string What, List<string> Impacts, Action Run);

    private const string ModalConfirmRevert = "Confirm revert";
    private PendingRevert? _confirmRevert;
    private bool _openConfirmRevert;

    private List<ReviewItem> _review = new();
    private bool _openReview, _reviewStale;
    private int _reviewSel;
    private Action? _reviewThen;

    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".tga", ".bmp" };

    /// <summary>.fac / .cha files under the characters folder: created, renamed and deleted from the explorer only.</summary>
    private static bool IsExplorerFilePath(string path) =>
        path.StartsWith(CharactersDir + "/", StringComparison.OrdinalIgnoreCase) &&
        (path.EndsWith(".fac", StringComparison.OrdinalIgnoreCase) || IsChaPath(path));

    /// <summary>One entry per path that would be saved, with what produced it and how to undo it.</summary>
    private List<ReviewItem> BuildReview()
    {
        _defs?.Prune(ChaConfigs());
        var changes = CollectChanges();
        var items = new Dictionary<string, ReviewItem>(StringComparer.OrdinalIgnoreCase);
        ReviewItem Item(string p) => items.TryGetValue(p, out var it) ? it : items[p] = new ReviewItem { Path = p };

        foreach (var (p, b) in _pending.Items)
        {
            var it = Item(p);
            it.Sources.Add(b.Length == 0 ? "staged deletion / empty file" : "staged file");
            if (IsExplorerFilePath(p)) it.Kept = "File created, renamed or deleted in the explorer: undo that in the explorer.";
            else it.Reverts.Add(() => { _pending.Remove(p); RefreshIndex(); });
        }
        var liveFiles = new Dictionary<string, ParsedFile>(StringComparer.OrdinalIgnoreCase);
        var liveStrs = new Dictionary<string, StrFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in AllParsedFiles().Where(f => f.Dirty && f.Error == null).ToList())
        {
            var it = Item(f.Entry.Path);
            liveFiles[f.Entry.Path] = f;
            if (IsChaPath(f.Entry.Path)) it.Impact = "Also discards the unsaved Config changes of this file's variants.";
            it.Sources.Add("field edits");
            it.Reverts.Add(() => RevertFileKeepSelection(f));
        }
        foreach (var s in _strCache.Values.Where(s => s.Dirty).ToList())
        {
            var it = Item(s.Entry.Path);
            liveStrs[s.Entry.Path] = s;
            it.Sources.Add("translations");
            var key = s.Entry.Path;
            it.Reverts.Add(() => { _strCache.Remove(key); _factionTitles = null; });
        }
        if (_defs != null)
        {
            var cha = ChaConfigs().ToList();
            foreach (var p in _defs.CollectChanges(cha).Keys)
            {
                var it = Item(p);
                it.Sources.Add("variant config");
                if (_defs.CanResetAt(cha, p)) it.Reverts.Add(() => _defs.ResetAt(ChaConfigs(), p));
                else it.Kept ??= "Follows a variant renamed, moved or deleted in its .cha file: undo that change to revert it.";
            }
        }

        var openPaths = new HashSet<string>(AllParsedFiles().Select(f => f.Entry.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var it in items.Values)
        {
            it.NewBytes = changes.TryGetValue(it.Path, out var nb) ? nb : Array.Empty<byte>();
            it.Binary = ImageExts.Any(x => it.Path.EndsWith(x, StringComparison.OrdinalIgnoreCase)) ||
                        Array.IndexOf(it.NewBytes, (byte)0) >= 0;
            if (_index!.Files.TryGetValue(it.Path, out var e) && e.Memory == null && !it.Binary)
            {
                try { it.OldText = Pk3Reader.ReadText(e); } catch { it.OldText = null; }
            }
            bool hasSaved = e != null && e.Memory == null;
            bool deletion = it.NewBytes.Length == 0 && !openPaths.Contains(it.Path);   // an open, emptied file is not a deletion
            it.Status = deletion ? "Deleted" : hasSaved ? "Modified" : "New";
            if (!it.Binary && !deletion)
            {
                try { it.Changes = LiveChanges(it, liveFiles.GetValueOrDefault(it.Path), liveStrs.GetValueOrDefault(it.Path)); }
                catch { it.Changes = null; }   // unreadable content: the text diff is shown instead
            }
        }
        return items.Values.Where(i => changes.ContainsKey(i.Path))
                    .OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The item's changes, compared with the live object that produces its content (edited .fac/.cha, .str or
    /// _def.cfg), so that each can be reverted on its own. Other files get a read-only list.
    /// </summary>
    private List<Change>? LiveChanges(ReviewItem it, ParsedFile? file, StrFile? str)
    {
        var oldText = it.OldText;
        if (file != null)
        {
            var list = ChangeSummary.Config(ConfigFile.Parse(oldText ?? ""), file.Config, IsChaPath(file.Entry.Path));
            return Wrap(list, () => AfterConfigRevert(file, oldText));
        }
        if (str != null)
        {
            var list = ChangeSummary.Str(ChangeSummary.ParseStr(it.Path, oldText ?? ""), str);
            return Wrap(list, () =>
            {
                if (oldText != null && str.Serialize() == oldText) str.Dirty = false;
                _factionTitles = null;
            });
        }
        if (_defs != null && ChangeSummary.IsDef(it.Path) && _defs.DirtyVariantAt(ChaConfigs(), it.Path) is { } variant
            && _defs.Load(variant) is { } def)
        {
            var defs = _defs;
            var list = ChangeSummary.Def(it.Path, oldText == null ? null : DefFile.Parse(oldText), def);
            if (oldText == null)   // a new config: reverting it drops the file
                list = list.Select(c => c with { Impact = "The variant goes back to having no _def.cfg.", Revert = () => defs.ResetBinding(variant) }).ToList();
            return Wrap(list, () =>
            {
                if (oldText != null && def.Serialize() == DefFile.Parse(oldText).Serialize()) defs.ResetBinding(variant);
            });
        }
        return ChangeSummary.Describe(it.Path, oldText, System.Text.Encoding.Latin1.GetString(it.NewBytes));
    }

    private List<Change> Wrap(List<Change> list, Action after) =>
        list.Select(c => c.Revert == null ? c : c with { Revert = () => { c.Revert(); after(); _reviewStale = true; } }).ToList();

    /// <summary>After a single change of a .fac/.cha was undone: tree, variant configs and caches follow the file.</summary>
    private void AfterConfigRevert(ParsedFile f, string? oldText)
    {
        if (!f.IsNew && oldText != null && ConfigFile.Parse(oldText).Serialize() == f.Config.Serialize()) f.Dirty = false;
        if (IsChaPath(f.Entry.Path)) { _defs?.EnsureBound(f.Config); _defs?.Prune(ChaConfigs()); }
        RebuildFileNode(f);
        _knownFactions = null;
        _knownTags = null;
        _factionTitles = null;
    }

    private static IEnumerable<Node> Descendants(Node n) => n.Children.SelectMany(c => Descendants(c).Prepend(c));

    /// <summary>Rebuilds a file's subtree from its blocks, keeping expanded nodes and the selection by block.</summary>
    private void RebuildFileNode(ParsedFile f)
    {
        var node = _roots.SelectMany(r => r.Children).FirstOrDefault(c => c.File == f);
        if (node == null) return;
        bool selInside = _selected != null && _selected != node && _selected.File == f;
        var selBlock = _selected?.Block;
        var open = _expanded.Where(n => n.File == f && n.Block != null).Select(n => n.Block!).ToHashSet();
        _expanded.RemoveWhere(n => n.File == f && n.Block != null);

        node.Children = Node.BuildFile(f).Children;
        if (selInside) _selected = node;   // unless the selected block still exists below
        foreach (var n in Descendants(node))
        {
            if (open.Contains(n.Block!)) _expanded.Add(n);
            if (selInside && n.Block == selBlock) _selected = n;
        }
    }

    /// <summary>Like <see cref="RevertFile"/>, but keeps the explorer selection when it is still in the tree.</summary>
    private void RevertFileKeepSelection(ParsedFile f)
    {
        var keep = _selected;
        RevertFile(f);
        if (keep != null && (_roots.Contains(keep) || FindParent(keep) != null)) _selected = keep;
    }

    private void RevertReviewItem(ReviewItem it)
    {
        foreach (var r in it.Reverts) r();
        _reviewStale = true;
    }

    private static List<string> FileImpacts(ReviewItem it)
    {
        var list = new List<string>();
        if (it.Changes != null) list.AddRange(it.Changes.Select(c => c.Impact).OfType<string>());
        if (it.Impact != null) list.Add(it.Impact);
        if (it.Kept != null) list.Add("Kept: " + it.Kept);
        return list.Distinct().ToList();
    }

    private void AskRevert(string what, IEnumerable<string> impacts, Action run)
    {
        _confirmRevert = new PendingRevert(what, impacts.ToList(), run);
        _openConfirmRevert = true;
    }

    /// <summary>Confirmation shown over the review screen before any revert, with what else the revert does.</summary>
    private void DrawConfirmRevertModal()
    {
        if (_openConfirmRevert) { _openConfirmRevert = false; ImGui.OpenPopup(ModalConfirmRevert); }
        CenterNextWindow(560, 260);
        bool open = true;
        if (!ImGui.BeginPopupModal(ModalConfirmRevert, ref open))
        {
            if (!_openConfirmRevert) _confirmRevert = null;
            return;
        }
        var c = _confirmRevert;
        if (c == null) { ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }

        ImGui.TextWrapped(c.What.Replace("%", "%%"));
        ImGui.Spacing();
        if (c.Impacts.Count == 0) ImGui.TextDisabled("No other change.");
        else
        {
            ImGui.TextUnformatted("This will also:");
            ImGui.PushStyleColor(ImGuiCol.Text, WarnColor);
            foreach (var i in c.Impacts) { ImGui.Bullet(); ImGui.TextWrapped(i.Replace("%", "%%")); }
            ImGui.PopStyleColor();
        }
        ImGui.Spacing();
        ImGui.TextDisabled("Unsaved edits that are reverted cannot be restored.");
        ImGui.Separator();
        if (ImGui.Button("Revert"))
        {
            _confirmRevert = null;
            ImGui.CloseCurrentPopup();
            c.Run();
            _reviewStale = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) { _confirmRevert = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    private static Vec4 StatusColor(string status) => status switch
    {
        "New" => new Vec4(0.45f, 0.9f, 0.45f, 1f),
        "Deleted" => new Vec4(1f, 0.45f, 0.45f, 1f),
        _ => new Vec4(1f, 0.8f, 0.35f, 1f),
    };

    private void DrawReviewModal()
    {
        CenterNextWindow(1000, 640);
        bool open = true;
        if (!ImGui.BeginPopupModal(ModalReview, ref open))
        {
            if (!_openReview && (_reviewThen != null || _review.Count > 0)) { _reviewThen = null; _review = new(); }   // closed with the window's X
            return;
        }

        if (_reviewStale)
        {
            _review = BuildReview();
            _reviewStale = false;
            _reviewSel = Math.Clamp(_reviewSel, 0, Math.Max(0, _review.Count - 1));
        }

        ImGui.TextUnformatted(_review.Count == 0
            ? "No changes left to save."
            : $"{_review.Count} file(s) will be written to the changes pk3. Check them, revert what you do not want, then save.");
        ImGui.Separator();

        float footer = ImGui.GetFrameHeightWithSpacing() + 6;
        var avail = ImGui.GetContentRegionAvail();
        float listW = Math.Max(220, avail.X * 0.32f);

        ReviewItem? revert = null;
        if (ImGui.BeginChild("##reviewlist", new Vec2(listW, -footer), true))
        {
            for (int i = 0; i < _review.Count; i++)
            {
                var it = _review[i];
                ImGui.PushID(i);
                ImGui.TextColored(StatusColor(it.Status), it.Status.Substring(0, 1));
                ImGui.SameLine();
                if (ImGui.Selectable(it.Path, i == _reviewSel)) _reviewSel = i;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{it.Status}: {it.Path}");
                ImGui.PopID();
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("##reviewdiff", new Vec2(0, -footer), true))
        {
            if (_reviewSel < _review.Count)
            {
                var it = _review[_reviewSel];
                ImGui.TextColored(StatusColor(it.Status), it.Status);
                ImGui.SameLine();
                ImGui.TextUnformatted(it.Path);
                ImGui.TextDisabled("Changed by: " + string.Join(", ", it.Sources));

                bool cannot = it.Reverts.Count == 0;   // captured once: the revert rebuilds the list before EndDisabled
                if (cannot) ImGui.BeginDisabled();
                if (ImGui.Button("Revert this file")) revert = it;
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(cannot ? "Nothing in this file can be reverted from here." : "Undoes every change of this file (asks first).");
                if (cannot) ImGui.EndDisabled();
                if (it.Kept != null) { ImGui.PushStyleColor(ImGuiCol.Text, WarnColor); ImGui.TextWrapped(it.Kept); ImGui.PopStyleColor(); }
                ImGui.Separator();
                DrawReviewContent(it);
            }
        }
        ImGui.EndChild();
        if (revert != null)
        {
            var target = revert;
            AskRevert($"Revert every change of {target.Path}?", FileImpacts(target), () => RevertReviewItem(target));
        }

        bool nothing = _review.Count == 0;
        if (nothing && _reviewThen == null) ImGui.BeginDisabled();
        if (ImGui.Button(nothing ? "Continue" : "Save"))
        {
            var then = _reviewThen; _reviewThen = null;
            ImGui.CloseCurrentPopup();
            if (!nothing) ProceedSave(then);
            else if (!HasPendingChanges()) then?.Invoke();
        }
        if (nothing && _reviewThen == null) ImGui.EndDisabled();
        ImGui.SameLine();
        bool noRevert = _review.All(i => i.Reverts.Count == 0);
        if (noRevert) ImGui.BeginDisabled();
        if (ImGui.Button("Revert all"))
        {
            var all = _review.ToList();
            AskRevert($"Revert every change of the {all.Count} file(s)?", all.SelectMany(FileImpacts).Distinct(),
                () => { foreach (var it in all) RevertReviewItem(it); });
        }
        if (noRevert) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) { _reviewThen = null; ImGui.CloseCurrentPopup(); }
        DrawConfirmRevertModal();   // nested: opened from inside the review screen
        ImGui.EndPopup();
    }

    private void DrawReviewContent(ReviewItem it)
    {
        if (it.Status == "Deleted")
        {
            ImGui.TextWrapped("This file will be emptied in the changes pk3, which hides the version stored in the other pk3s.");
            return;
        }
        if (it.NewBytes.Length == 0) { ImGui.TextDisabled("Empty file."); return; }
        if (it.Binary)
        {
            ImGui.TextDisabled($"Binary file, {it.NewBytes.Length:N0} bytes.");
            if (ImageExts.Any(x => it.Path.EndsWith(x, StringComparison.OrdinalIgnoreCase)) && _images.Get(it.Path) is { } img)
            {
                float side = Math.Min(256, ImGui.GetContentRegionAvail().X);
                float scale = side / Math.Max(img.Width, img.Height);
                ImGui.Image(img.Id, new Vec2(img.Width * scale, img.Height * scale));
            }
            return;
        }

        var newText = System.Text.Encoding.Latin1.GetString(it.NewBytes);
        if (it.Changes != null)
        {
            if (it.Changes.Count == 0) ImGui.TextDisabled("No value changed (formatting only).");
            Change? asked = null;
            for (int i = 0; i < it.Changes.Count; i++)
            {
                var c = it.Changes[i];
                ImGui.PushID(i);
                bool cannot = c.Revert == null;
                if (cannot) ImGui.BeginDisabled();
                if (ImGui.SmallButton("Revert")) asked = c;
                if (cannot) ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(cannot ? "This change cannot be reverted on its own here." : "Revert this change (asks first).");
                ImGui.SameLine();
                ImGui.TextWrapped(c.Text.Replace("%", "%%"));
                ImGui.PopID();
            }
            if (asked != null)
            {
                var c = asked;
                AskRevert("Revert this change?\n" + c.Text, c.Impact == null ? Array.Empty<string>() : new[] { c.Impact }, c.Revert!);
            }
            ImGui.Spacing();
            if (!ImGui.CollapsingHeader("Text diff")) return;
        }

        it.Diff ??= TextDiff.WithContext(TextDiff.Compute(it.OldText ?? "", newText));
        if (it.Diff.Count == 0) { ImGui.TextDisabled("No visible difference (whitespace or line endings only)."); return; }

        var added = new Vec4(0.45f, 0.9f, 0.45f, 1f);
        var removed = new Vec4(1f, 0.45f, 0.45f, 1f);
        foreach (var l in it.Diff)
        {
            if (l == null) { ImGui.TextDisabled("   ..."); continue; }
            var d = l.Value;
            var num = d.Kind == DiffKind.Added ? d.NewLine : d.OldLine;
            var text = $"{num,5} {(d.Kind == DiffKind.Added ? '+' : d.Kind == DiffKind.Removed ? '-' : ' ')} {d.Text}";
            if (d.Kind == DiffKind.Same) ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            else ImGui.PushStyleColor(ImGuiCol.Text, d.Kind == DiffKind.Added ? added : removed);
            ImGui.TextUnformatted(text);   // not a format string: game files may contain '%'
            ImGui.PopStyleColor();
        }
    }

    private void DrawSaveModals()
    {
        if (_openReview) { _openReview = false; ImGui.OpenPopup(ModalReview); }
        DrawReviewModal();
        if (_openLooseMask) { _openLooseMask = false; ImGui.OpenPopup(ModalLooseMask); }
        if (_openSaved) { _openSaved = false; ImGui.OpenPopup(ModalSaved); }
        if (_openUnsaved) { _openUnsaved = false; ImGui.OpenPopup(ModalUnsaved); }

        CenterNextWindow(460, 150);
        bool open = true;
        if (ImGui.BeginPopupModal(ModalUnsaved, ref open))
        {
            ImGui.TextWrapped("You have unsaved changes. Save them before continuing?");
            if (ImGui.Button("Save"))
            {
                var a = _afterUnsaved; _afterUnsaved = null;
                ImGui.CloseCurrentPopup();
                BeginSave(a);
            }
            ImGui.SameLine();
            if (ImGui.Button("Discard"))
            {
                var a = _afterUnsaved; _afterUnsaved = null;
                ImGui.CloseCurrentPopup();
                a?.Invoke();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) { _afterUnsaved = null; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
        else if (!_openUnsaved) _afterUnsaved = null;   // closed with the window's X

        CenterNextWindow(560, 300);
        open = true;
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
        else if (_saveChanges != null && !_openLooseMask) { _saveChanges = null; _afterSave = null; }   // dialog dismissed: save cancelled

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
