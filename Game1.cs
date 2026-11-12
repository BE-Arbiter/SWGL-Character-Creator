using System.Runtime.CompilerServices;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Vec2 = System.Numerics.Vector2;
using Vec4 = System.Numerics.Vector4;

namespace SWGLCharacterCreator;

public partial class Game1 : Game
{
    private const string CharactersDir = "ext_data/characters";
    private const string ImagePickerTitle = "Choose an indexed file";
    private const int MaxImageMatches = 500;

    private readonly GraphicsDeviceManager _graphics;
    private ImGuiRenderer _imgui = null!;
    private ImageStore _images = null!;
    private readonly Settings _settings = Settings.Load();

    // Game folder / index state
    private string? _gameDir;
    private Pk3Index? _index;
    private List<Node> _roots = new();
    private volatile bool _indexing;
    private (int done, int total, string current) _progress;
    private string _status = NoFolderMessage;
    private DefStore? _defs;   // variant <-> _def.cfg bindings, created with the index
    private GameDataCatalog? _catalog;   // weapons / sabers / NPC types from the game data, created with the index
    private PendingChanges _pending = new();   // edits staged in memory, reset whenever the folder is re-indexed
    private int _loadGeneration;   // bumped by every SetGameDir so that a stale indexing run is ignored

    private static readonly string NoFolderMessage =
        $"No SWGL game folder selected. Use File > Select SWGL folder... and choose the folder that contains {GameLocator.ExeName} " +
        "(the \"GameData\" folder of the Steam game \"Jedi Academy - SWGL_DEV\").";

    // Explorer / editor state
    private string _filter = "";
    private readonly HashSet<Node> _expanded = new();
    private Node? _selected;

    // Icon pickers
    private bool _openImagePicker;
    private string[] _pickerExts = Array.Empty<string>();
    private Action<Pk3Entry>? _pickerSelect;
    private string _imageFilter = "";
    private string _imageMatchesFor = "\u0000";    // filter the cached matches were built for
    private List<Pk3Entry> _imageMatches = new();
    private int _imageTotal;

    // Folder browser
    private bool _openBrowser;
    private string _browsePath = GameLocator.DefaultPath;
    private string _browseInput = GameLocator.DefaultPath;
    private string _browseError = "";

    public Game1()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
        };
        IsMouseVisible = true;
        Window.AllowUserResizing = true;
        Window.Title = "SWGL Character Creator";
    }

    protected override void LoadContent()
    {
        _imgui = new ImGuiRenderer(this);
        _images = new ImageStore(GraphicsDevice, _imgui, () => _index,
            (p, b) => { _pending.Stage(p, b); _index!.AddMemory(p, b); }, p => _pending.Remove(p));
        _viewer = new ModelViewer(GraphicsDevice, _imgui, _images);

        var saved = _settings.GameDataPath;
        var start = string.IsNullOrEmpty(saved) ? GameLocator.DefaultPath : saved;
        var resolved = GameLocator.Resolve(start);
        if (resolved == null && !string.IsNullOrEmpty(saved) && saved != GameLocator.DefaultPath)
            resolved = GameLocator.Resolve(GameLocator.DefaultPath); // the remembered folder is gone: try the Steam default

        if (resolved != null)
        {
            _browsePath = _browseInput = resolved;
            SetGameDir(resolved);
            return;
        }

        // No access to the game: ask for the folder right away and say exactly what is needed.
        _browsePath = _browseInput = Directory.Exists(start) ? start : "";
        _status = string.IsNullOrEmpty(saved)
            ? NoFolderMessage
            : $"The previously selected folder is no longer valid: {GameLocator.ExeName} was not found in \"{saved}\". " + NoFolderMessage;
        _openBrowser = true;
    }

    protected override void OnExiting(object sender, ExitingEventArgs args)
    {
        if (!_allowExit && HasPendingChanges())
        {
            args.Cancel = true;   // window closed with unsaved edits: ask first
            RunOrConfirm(QuitNow);
        }
        base.OnExiting(sender, args);
    }

    private void QuitNow()
    {
        _allowExit = true;
        Exit();
    }

    private void SetGameDir(string dir)
    {
        _gameDir = dir;
        _pending = new PendingChanges();
        _settings.GameDataPath = dir;
        _settings.Save();
        _index = null;
        _defs = null;
        _catalog = null;
        _roots = new();
        _selected = null;
        _expanded.Clear();
        _strByName = null;
        _strCache.Clear();
        _images.Invalidate();
        _viewer.Clear();
        _knownFactions = null;
        _knownTags = null;
        _factionTitles = null;
        _modelFolders = null;
        _imageMatchesFor = "\u0000";
        _pendingIndex = null;
        _indexing = true;
        _progress = (0, 1, "");
        _status = $"Indexing {dir}...";
        int generation = ++_loadGeneration;

        var progress = new Progress<(int, int, string)>(p => { if (generation == _loadGeneration) _progress = p; });
        Task.Run(() =>
        {
            try
            {
                var index = Pk3Index.Build(dir, progress);
                var facNode = new Node { Kind = NodeKind.Category, Title = "Factions" };
                var chaNode = new Node { Kind = NodeKind.Category, Title = "Characters" };
                int errors = 0;
                foreach (var (ext, root) in new[] { (".fac", facNode), (".cha", chaNode) })
                    foreach (var entry in index.Find(CharactersDir, ext))
                    {
                        var pf = LoadFile(entry);
                        if (pf.Error != null) errors++;
                        root.Children.Add(Node.BuildFile(pf));
                    }

                if (generation != _loadGeneration) return; // another folder was selected meanwhile

                var defs = new DefStore(index);
                foreach (var cf in chaNode.Children.Where(c => c.File!.Error == null)) defs.Bind(cf.File!.Config);
                _defs = defs;
                _catalog = GameDataCatalog.Build(index);
                _index = index;
                _roots = new List<Node> { facNode, chaNode };
                _status = index.Pk3Count == 0
                    ? $"{dir}: no .pk3 file found in its \"base\" and \"SWGL\" subfolders - is this really the SWGL GameData folder?"
                    : $"{dir} - {index.Pk3Count} pk3, {index.Files.Count} files " +
                          $"({index.Overridden} overridden, {index.LooseCount} loose), " +
                          $"{facNode.Children.Count} .fac, {chaNode.Children.Count} .cha" +
                          (index.Errors.Count + errors > 0 ? $" - {index.Errors.Count + errors} error(s)" : "");
            }
            catch (Exception ex)
            {
                if (generation == _loadGeneration) _status = "Indexing failed: " + ex.Message;
            }
            finally
            {
                if (generation == _loadGeneration) _indexing = false;
            }
        });
    }

    private static bool IsChaPath(string path) => path.EndsWith(".cha", StringComparison.OrdinalIgnoreCase);

    private static ParsedFile LoadFile(Pk3Entry entry)
    {
        var pf = new ParsedFile { Entry = entry };
        try { pf.Config = ConfigFile.Parse(Pk3Reader.ReadText(entry)); }
        catch (Exception ex) { pf.Error = ex.Message; }
        return pf;
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(30, 30, 34));

        _images.Flush();
        DebugRenderIfRequested();
        DebugParseIfRequested();
        DebugBonesIfRequested();
        DebugBatchIfRequested();
        ApplyPendingIndex();
        _imgui.BeginFrame(gameTime);
        DrawUi();
        _imgui.EndFrame();

        base.Draw(gameTime);
    }

    private void DrawUi()
    {
        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("Select SWGL folder...")) _openBrowser = true;
                if (ImGui.MenuItem("Re-index", null, false, _gameDir != null && !_indexing)) RunOrConfirm(() => SetGameDir(_gameDir!));
                ImGui.Separator();
                if (ImGui.MenuItem("Save changes", "Ctrl+S", false, HasPendingChanges() && !_indexing)) BeginSave();
                ImGui.Separator();
                if (ImGui.MenuItem("Quit")) RunOrConfirm(QuitNow);
                ImGui.EndMenu();
            }
            ImGui.EndMainMenuBar();
        }

        HandleSaveShortcut();
        if (_openBrowser)
        {
            _openBrowser = false;
            ImGui.OpenPopup("Select SWGL folder");
        }
        DrawFolderBrowser();

        if (_openImagePicker)
        {
            _openImagePicker = false;
            ImGui.OpenPopup(ImagePickerTitle);
        }
        DrawImagePicker();
        DrawModals();
        DrawSaveModals();

        var vp = ImGui.GetMainViewport();
        float menuH = ImGui.GetFrameHeight();
        ImGui.SetNextWindowPos(new Vec2(0, menuH));
        ImGui.SetNextWindowSize(new Vec2(vp.Size.X, vp.Size.Y - menuH));
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
                                       | ImGuiWindowFlags.NoBringToFrontOnFocus;
        if (ImGui.Begin("##main", flags))
        {
            if (_indexing)
            {
                var (done, total, current) = _progress;
                ImGui.TextUnformatted(_status);
                ImGui.ProgressBar(total == 0 ? 0 : (float)done / total, new Vec2(-1, 0), $"{done}/{total}  {current}");
            }
            else
            {
                ImGui.TextWrapped(_status);
                if (_index != null) DrawWorkspace();
                else if (_gameDir == null && ImGui.Button("Select SWGL folder...")) _openBrowser = true;
            }
        }
        ImGui.End();
    }

    // ---------------------------------------------------------------- workspace

    /// <summary>Three panes: explorer 20%, asset preview 50%, field editor for the rest.</summary>
    private void DrawWorkspace()
    {
        ImGui.Separator();
        var avail = ImGui.GetContentRegionAvail();
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float usable = avail.X - spacing * 2;
        float leftW = usable * 0.20f, midW = usable * 0.50f, rightW = usable - leftW - midW;

        if (ImGui.BeginChild("##left", new Vec2(leftW, avail.Y), false, ImGuiWindowFlags.NoScrollbar))
        {
        if (ImGui.BeginChild("##explorer", new Vec2(0, avail.Y * 0.62f), true))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##filter", "Filter...", ref _filter, 128);
            ImGui.Separator();
            if (ImGui.BeginChild("##tree", new Vec2(0, 0)))
            {
                foreach (var root in _roots) DrawNode(root, _filter.Length > 0);
                ApplyPendingDrop(); // after the loop: a drop changes the lists being enumerated
                if (_index!.Errors.Count > 0)
                {
                    ImGui.Separator();
                    foreach (var e in _index.Errors) ImGui.TextColored(new Vec4(1f, 0.4f, 0.4f, 1f), e);
                }
            }
            ImGui.EndChild();
        }
        ImGui.EndChild();

        if (ImGui.BeginChild("##animbox", new Vec2(0, 0), true)) DrawAnimationList();
        ImGui.EndChild();
        }
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("##assets", new Vec2(midW, avail.Y), true)) DrawAssetPreview();
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("##editor", new Vec2(rightW, avail.Y), true)) DrawEditor();
        ImGui.EndChild();
    }

    private void DrawNode(Node n, bool filtering)
    {
        if (filtering && !n.Matches(_filter)) return;

        ImGui.PushID(RuntimeHelpers.GetHashCode(n));
        bool hasChildren = n.Children.Count > 0;
        bool expanded = hasChildren && (filtering || _expanded.Contains(n));

        float btnW = ImGui.CalcTextSize("[+]").X + ImGui.GetStyle().FramePadding.X * 2;
        if (hasChildren)
        {
            if (ImGui.SmallButton(expanded ? "[-]" : "[+]"))
            {
                if (!_expanded.Remove(n)) _expanded.Add(n);
            }
        }
        else ImGui.Dummy(new Vec2(btnW, 1));

        ImGui.SameLine();
        var label = n.Kind == NodeKind.Category ? $"{n.Label} ({n.Children.Count})" : n.Label;
        if (n.Kind == NodeKind.Block && n.Block!.Type == "Variant" && _defs?.IsDirty(n.Block) == true) label += " *";
        if (ImGui.Selectable(label, _selected == n)) _selected = n;
        DrawNodeContextMenu(n);
        DrawTransferDragDrop(n);

        if (expanded)
        {
            ImGui.Indent(btnW);
            foreach (var c in n.Children) DrawNode(c, filtering);
            ImGui.Unindent(btnW);
        }
        ImGui.PopID();
    }

    // ---------------------------------------------------------------- asset preview

    private static Prop? IconProp(Block b) =>
        b.Props.FirstOrDefault(p => p.Key.Equals("icon", StringComparison.OrdinalIgnoreCase));

    /// <summary>Shows the selected element's icon; an invalid path shows nothing.</summary>
    private void DrawAssetPreview()
    {
        if (_selected is { } node && DrawGridFor(node)) return;

        if (_selected?.Block is { } sel && sel.Get("model") != null)
        {
            DrawModelPreview(sel);
            return;
        }

        var icon = _selected?.Block is { } b ? IconProp(b) : null;
        if (icon == null) return;

        var img = _images.Get(icon.Value);
        if (img == null) return;

        // Same top-right overlay as a variant's model preview.
        var p0 = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        var q0 = new Vec2(p0.X + avail.X - PreviewIconSize - 8, p0.Y + 8);
        var q1 = q0 + new Vec2(PreviewIconSize, PreviewIconSize);
        var dl = ImGui.GetWindowDrawList();
        dl.AddImage(img.Id, q0, q1);
        dl.AddRect(q0, q1, ImGui.GetColorU32(new Vec4(1, 1, 1, 0.9f)));
        ImGui.TextDisabled($"{img.Width} x {img.Height}  -  {img.Source}");
    }

    // ---------------------------------------------------------------- editor

    private void DrawEditor()
    {
        var n = _selected;
        if (n == null) { ImGui.TextDisabled("Select an item in the explorer."); return; }

        switch (n.Kind)
        {
            case NodeKind.Category:
                ImGui.TextUnformatted($"{n.Title}: {n.Children.Count} file(s)");
                break;

            case NodeKind.File:
                DrawFileHeader(n.File!);
                ImGui.TextDisabled($"{n.Children.Count} entries");
                break;

            case NodeKind.Block:
                if (n.Block!.Type == "Variant" && _defs != null && _catalog != null)
                {
                    if (!ImGui.BeginTabBar("##variant")) break;
                    if (ImGui.BeginTabItem("Variant"))
                    {
                        DrawFileHeader(n.File!);
                        DrawBlockFields(n);
                        ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem((_defs.IsDirty(n.Block) ? "Config *" : "Config") + "###config"))
                    {
                        DrawFileHeader(n.File!);
                        DrawConfigTab(n);
                        ImGui.EndTabItem();
                    }
                    ImGui.EndTabBar();
                    break;
                }
                DrawFileHeader(n.File!);
                DrawBlockFields(n);
                break;
        }
    }

    private void DrawFileHeader(ParsedFile f)
    {
        ImGui.TextUnformatted(f.Entry.Path);
        ImGui.TextDisabled($"Source: {f.Entry.Pk3Folder}/{f.Entry.Pk3Name}");
        if (f.Error != null) ImGui.TextColored(new Vec4(1f, 0.4f, 0.4f, 1f), "Read error: " + f.Error);

        bool clean = !f.Dirty;   // captured once: RevertFile clears Dirty before EndDisabled
        if (clean) ImGui.BeginDisabled();
        if (ImGui.Button("Discard file changes")) RevertFile(f);
        if (clean) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Reverts this file to its last saved state. Also discards the Config changes of this file's variants.");
        ImGui.Separator();
    }

    // ---------------------------------------------------------------- icon pickers

    private void DrawImagePicker()
    {
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowSize(new Vec2(Math.Min(700, vp.Size.X - 40), Math.Min(520, vp.Size.Y - 40)), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(vp.Size / 2, ImGuiCond.Appearing, new Vec2(0.5f, 0.5f));
        bool open = true;
        if (!ImGui.BeginPopupModal(ImagePickerTitle, ref open)) return;

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##imgfilter", "Filter...", ref _imageFilter, 256);

        if (_imageMatchesFor != _imageFilter)
        {
            var all = _index!.Files.Values
                .Where(e => _pickerExts.Any(x => e.Path.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                .Where(e => _imageFilter.Length == 0 || e.Path.Contains(_imageFilter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _imageTotal = all.Count;
            _imageMatches = all.Take(MaxImageMatches).ToList();
            _imageMatchesFor = _imageFilter;
        }

        ImGui.TextDisabled(_imageTotal > MaxImageMatches
            ? $"{_imageTotal} matches, first {MaxImageMatches} shown - refine the filter"
            : $"{_imageTotal} matches");

        float footer = ImGui.GetFrameHeightWithSpacing() + 4;
        if (ImGui.BeginChild("##imglist", new Vec2(0, -footer), true))
        {
            foreach (var e in _imageMatches)
            {
                if (!ImGui.Selectable(e.Path)) continue;
                _pickerSelect?.Invoke(e);
                ImGui.CloseCurrentPopup();
                break;
            }
        }
        ImGui.EndChild();

        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    // ---------------------------------------------------------------- translations (.str)

    private Dictionary<string, List<Pk3Entry>>? _strByName;
    private readonly Dictionary<string, StrFile> _strCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Indexes strings/&lt;language&gt;/&lt;name&gt;.str by name, English first.</summary>
    private Dictionary<string, List<Pk3Entry>> StrByName()
    {
        if (_strByName != null) return _strByName;
        _strByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _index!.Files.Values)
        {
            if (!e.Path.StartsWith("strings/", StringComparison.OrdinalIgnoreCase) ||
                !e.Path.EndsWith(".str", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileNameWithoutExtension(e.Path);
            if (!_strByName.TryGetValue(name, out var list)) _strByName[name] = list = new();
            list.Add(e);
        }
        foreach (var list in _strByName.Values)
            list.Sort((a, b) => (IsEnglish(a) ? 0 : 1).CompareTo(IsEnglish(b) ? 0 : 1));
        return _strByName;

        static bool IsEnglish(Pk3Entry e) => e.Path.StartsWith("strings/english/", StringComparison.OrdinalIgnoreCase);
    }

    private StrFile LoadStr(Pk3Entry e)
    {
        if (!_strCache.TryGetValue(e.Path, out var f)) _strCache[e.Path] = f = StrFile.Load(e);
        return f;
    }

    /// <summary>Key "swgl_factions_tor" = file "swgl_factions" + REFERENCE "tor"; try every "_" split.</summary>
    private (StrFile file, StrEntry entry)? ResolveKey(string key)
    {
        var map = StrByName();
        for (int i = key.IndexOf('_'); i > 0; i = key.IndexOf('_', i + 1))
        {
            if (!map.TryGetValue(key[..i], out var candidates)) continue;
            var reference = key[(i + 1)..];
            foreach (var c in candidates)
            {
                var sf = LoadStr(c);
                var entry = sf.Find(reference);
                if (entry != null) return (sf, entry);
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- saving

    private void RevertFile(ParsedFile f)
    {
        var fresh = f.IsNew ? new ParsedFile { Entry = f.Entry } : LoadFile(f.Entry); // a never-saved file reverts to empty
        if (f.IsNew)
        {   // the content captured at creation/rename must not be saved any more
            _pending.Stage(f.Entry.Path, Array.Empty<byte>());
            f.Entry = _index!.AddMemory(f.Entry.Path, Array.Empty<byte>());
        }
        if (IsChaPath(f.Entry.Path)) _defs?.Unbind(f.Config);
        f.Config = fresh.Config;
        f.Error = fresh.Error;
        f.Dirty = false;
        if (IsChaPath(f.Entry.Path) && f.Error == null) _defs?.Bind(f.Config);
        if (IsChaPath(f.Entry.Path)) _defs?.Prune(ChaConfigs());

        // Rebuild the file's subtree against the reloaded blocks, keeping the file node itself.
        var node = _roots.SelectMany(r => r.Children).First(c => c.File == f);
        var rebuilt = Node.BuildFile(f);
        node.Children = rebuilt.Children;
        _selected = node;
    }

    // ---------------------------------------------------------------- folder browser

    private void DrawFolderBrowser()
    {
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowSize(new Vec2(Math.Min(700, vp.Size.X - 40), Math.Min(520, vp.Size.Y - 40)), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(vp.Size / 2, ImGuiCond.Appearing, new Vec2(0.5f, 0.5f));
        bool open = true;
        if (!ImGui.BeginPopupModal("Select SWGL folder", ref open)) return;

        ImGui.TextWrapped($"Select the SWGL \"GameData\" folder: the folder that contains {GameLocator.ExeName} " +
                          "and the \"base\" and \"SWGL\" subfolders.");
        ImGui.TextDisabled($"Usually: {GameLocator.DefaultPath}");
        ImGui.TextDisabled("The game's root folder (parent of GameData) is also accepted.");

        if (ImGui.Button("Up"))
        {
            var parent = Directory.GetParent(_browsePath.TrimEnd('\\', '/'));
            if (parent != null) NavigateTo(parent.FullName);
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##path", ref _browseInput, 512, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            if (Directory.Exists(_browseInput)) NavigateTo(_browseInput);
            else _browseError = $"Folder not found: {_browseInput}";
        }

        float footer = ImGui.GetFrameHeightWithSpacing() * 2 + 4;
        if (ImGui.BeginChild("##dirs", new Vec2(0, -footer), true))
        {
            var found = GameLocator.Resolve(_browsePath);
            if (found != null) ImGui.TextColored(new Vec4(0.4f, 1f, 0.4f, 1f), $"SWGL found: {found}");
            else ImGui.TextColored(new Vec4(1f, 0.8f, 0.3f, 1f), $"{GameLocator.ExeName} not found in this folder yet - keep browsing.");

            if (string.IsNullOrEmpty(_browsePath))
                foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
                {
                    if (ImGui.Selectable(d.Name)) NavigateTo(d.Name);
                }
            else
            {
                foreach (var d in SafeSubdirs(_browsePath))
                    if (ImGui.Selectable("[+] " + Path.GetFileName(d))) { NavigateTo(d); break; }
            }
        }
        ImGui.EndChild();

        if (ImGui.Button("Drives")) NavigateTo("");
        ImGui.SameLine();
        if (ImGui.Button("Select this folder"))
        {
            var resolved = GameLocator.Resolve(_browsePath);
            if (resolved == null) _browseError = $"{GameLocator.ExeName} was not found in this folder, in its \"GameData\" subfolder or in any parent folder. " +
                                                 "Select the SWGL \"GameData\" folder.";
            else
            {
                _browseError = "";
                ImGui.CloseCurrentPopup();
                RunOrConfirm(() => SetGameDir(resolved));
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        if (_browseError.Length > 0) ImGui.TextColored(new Vec4(1f, 0.4f, 0.4f, 1f), _browseError);
        ImGui.EndPopup();
    }

    private void OpenPicker(string initialFilter, string[] extensions, Action<Pk3Entry> onSelect)
    {
        _imageFilter = initialFilter;
        _pickerExts = extensions;
        _pickerSelect = onSelect;
        _imageMatchesFor = "\u0000";
        _openImagePicker = true;
    }

    private void NavigateTo(string path)
    {
        _browsePath = path;
        _browseInput = path;
        _browseError = "";
    }

    private static List<string> SafeSubdirs(string path)
    {
        try { return Directory.EnumerateDirectories(path).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { return new(); }
    }
}
