# `_def.cfg` Editor and pk3 Change Saving — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Save every edit of the app into a dated changes pk3 instead of loose files, then add a "Config" tab that edits each variant's `_def.cfg` with controls fed by the game data.

**Architecture:** Edits stay in memory until one global save. Images and renames are "staged" bytes that also appear in the `Pk3Index` as in-memory entries. Text files keep their Dirty flags. The save collects everything into `path → bytes`, writes `Z___SWGL_Characters_changes_yymmdd.pk3` atomically, then repoints the index at it. `_def.cfg` files are bound to variant `Block` objects (`DefStore`), so renames, moves, copies and deletes are derived at save time by comparing each variant's current path with the path it was loaded from.

**Tech Stack:** C# / .NET 9, MonoGame DesktopGL, ImGui.NET 1.89.7, `System.IO.Compression`; new xUnit test project.

**Spec:** `docs/superpowers/specs/2026-11-11-def-cfg-editor-design.md`

## Global Constraints

- Original pk3 files are never modified. The app never writes loose files anymore (it may only delete loose files the user agreed to delete in the save dialog).
- Changes pk3: `<GameData>/SWGL/Z___SWGL_Characters_changes_yymmdd.pk3` (three underscores, `yyMMdd` local date). Updated if it exists, created otherwise. Temp file: same name + `.part`.
- A deleted or renamed-away path is a zero-length entry in the changes pk3.
- Text is read and written as Latin-1 (`System.Text.Encoding.Latin1`).
- `_def.cfg` path: `ext_data/characters_conf/<CHARACTER_CODE>_<VARIANT_CODE>_def.cfg`, case-insensitive. Prefixed copies (`REP_…`) and other files in that folder are ignored.
- `_def.cfg` writing: CRLF between lines, no final newline, space before a value that parses as an integer, tab otherwise.
- Weapon, saber and NPC lists are read from the game data, never hard-coded.
- All UI text in English.

## Review Focus

- The game is running, so today's pk3 is locked: the save must fail with a message, leave the existing pk3 byte-identical, leave no `.part`, and keep everything dirty. → Task 2 test `Write_TargetLocked_ThrowsAndLeavesTargetUntouched`.
- A variant code is renamed and then renamed back before saving: this must produce no change and no deletion. → Task 6 test `RenameAndBack_NoChanges`.
- Variant A is deleted and variant B is renamed to A's code: path A must get B's content, not an empty entry. → Task 6 test `DeleteThenRenameIntoSamePath_WritesContentNotEmpty`.
- A file with an unknown key, `ui_saber ""` and no `ui_npc_type` is edited on one value: every other line must stay as it was. → Task 4 test `Edit_PreservesUnknownKeysAndOddValues`.
- A variant is deleted, then its `.cha` is reverted with "Discard changes": its `_def.cfg` must not be deleted on the next save. → Task 6 test `UnbindAfterDelete_DoesNotDeleteDef`.

---

## File Structure

| File | Responsibility |
|---|---|
| `tests/SWGLCharacterCreator.Tests/SWGLCharacterCreator.Tests.csproj` (new) | xUnit project referencing the app |
| `Pk3Index.cs` (modify) | in-memory and pk3-entry registration, reading in-memory entries |
| `ChangesPk3.cs` (new) | naming + atomic create/update of the changes pk3 |
| `PendingChanges.cs` (new) | staged bytes (images, deletions) not yet saved |
| `Game1.Save.cs` (new, partial `Game1`) | global save: collect, loose-file dialog, write, repoint index, warning dialog |
| `Game1.cs`, `Game1.Editors.cs`, `ImageStore.cs` (modify) | remove loose writes, use staging |
| `DefFile.cs` (new) | `_def.cfg` parse/serialize, key order, empty template |
| `GameDataCatalog.cs` (new) | weapons / sabers / NPC types from game data |
| `DefRules.cs` (new) | value lists, saber colors, saber-style bits, validation |
| `DefStore.cs` (new) | variant ↔ `_def.cfg` binding, change collection |
| `Game1.Config.cs` (new, partial `Game1`) | Config tab UI, add-variant modal |

---

### Task 1: Test project and in-memory index entries

**Files:**
- Create: `tests/SWGLCharacterCreator.Tests/SWGLCharacterCreator.Tests.csproj`, `tests/SWGLCharacterCreator.Tests/Pk3IndexTests.cs`
- Modify: `SWGLCharacterCreator.csproj`, `SWGLCharacterCreator.slnx`, `.github/workflows/build.yml`, `Pk3Index.cs`

**Interfaces:**
- Produces:
  - `record Pk3Entry(string Path, string Pk3Name, string Pk3Folder, long Size, string Pk3FullPath, string EntryName, bool IsLoose = false, byte[]? Memory = null)`
  - `Pk3Entry Pk3Index.AddMemory(string path, byte[] data)`: `Pk3Name = "(unsaved)"`, `Pk3Folder = "SWGL"`, replaces the current winner and does not mark it as shadowed.
  - `Pk3Entry Pk3Index.AddPk3Entry(string path, string pk3FullPath, long size)`: `Pk3Name` = file name, `Pk3Folder = "SWGL"`, `EntryName` = path; replaces the current winner.
  - `Pk3Reader.ReadBytes` / `ReadText` return `Memory` when it is not null.

- [ ] **Step 1: Scaffold.** Create the xUnit project (`net9.0`, `Microsoft.NET.Test.Sdk` 17.11.1, `xunit` 2.9.2, `xunit.runner.visualstudio` 2.8.2, `ProjectReference` to `../../SWGLCharacterCreator.csproj`). In the app csproj add `<Compile Remove="tests/**" /><None Remove="tests/**" />`. Add the test project to the `.slnx`. In `build.yml`, add a step `dotnet test tests/SWGLCharacterCreator.Tests -c Release` before "Publish".
- [ ] **Step 2: Write the failing tests** in `Pk3IndexTests.cs`:

```csharp
[Fact] public void AddMemory_WinsAndIsReadable() {
    var ix = new Pk3Index();
    var e = ix.AddMemory("gfx/a.png", new byte[] { 1, 2 });
    Assert.Same(e, ix.Files["GFX/A.PNG"]);
    Assert.Equal(new byte[] { 1, 2 }, Pk3Reader.ReadBytes(e));
    Assert.False(ix.IsShadowed("gfx/a.png"));
}
[Fact] public void ReadText_Memory_IsLatin1() {
    var ix = new Pk3Index();
    var e = ix.AddMemory("x.cha", new byte[] { 0xE9 });
    Assert.Equal("é", Pk3Reader.ReadText(e));
}
[Fact] public void AddPk3Entry_ReplacesMemoryEntry() {
    var ix = new Pk3Index();
    ix.AddMemory("a.cfg", new byte[] { 1 });
    var e = ix.AddPk3Entry("a.cfg", @"C:\g\SWGL\Z___SWGL_Characters_changes_261111.pk3", 1);
    Assert.Null(e.Memory);
    Assert.Equal("Z___SWGL_Characters_changes_261111.pk3", ix.Files["a.cfg"].Pk3Name);
}
```

- [ ] **Step 3: Run** `dotnet test tests/SWGLCharacterCreator.Tests`. Expected: build FAIL (`AddMemory` undefined).
- [ ] **Step 4: Implement** the members listed in Interfaces in `Pk3Index.cs`.
- [ ] **Step 5: Run** the same command. Expected: 3 passed. Also `dotnet build` of the app succeeds.
- [ ] **Step 6: Commit** `feat: test project and in-memory pk3 index entries`.

### Task 2: Changes pk3 writer

**Files:** Create `ChangesPk3.cs`, `tests/.../ChangesPk3Tests.cs`

**Interfaces:**
- Produces:
  - `static class ChangesPk3 { const string Prefix = "Z___SWGL_Characters_changes_"; static string FileName(DateTime date); static ChangesPk3Result Write(string swglDir, DateTime date, IReadOnlyDictionary<string, byte[]> changes); }`
  - `record ChangesPk3Result(string Pk3Path, bool Created)`.
- `Write` copies every entry of the existing target whose path (case-insensitive) is not in `changes`, adds `changes` (empty array = zero-length entry, forward-slash names), and writes to `<target>.part`. It then calls `File.Move(part, target, overwrite: true)`. On any exception it deletes the `.part` and rethrows.

- [ ] **Step 1: Write the failing tests** (each in a fresh temp directory):

```csharp
[Fact] public void FileName_UsesThreeUnderscoresAndYyMmDd() =>
    Assert.Equal("Z___SWGL_Characters_changes_261111.pk3", ChangesPk3.FileName(new DateTime(2026, 11, 11)));
[Fact] public void Write_CreatesPk3WithEntriesAndEmptyDeletes() {
    var r = ChangesPk3.Write(dir, D, new Dictionary<string, byte[]> { ["ext_data/a.cha"] = "x"u8.ToArray(), ["ext_data/old.cha"] = [] });
    Assert.True(r.Created);
    // open zip: "ext_data/a.cha" == "x", "ext_data/old.cha" Length == 0
}
[Fact] public void Write_SameDay_UpdatesKeepingOtherEntries() {
    ChangesPk3.Write(dir, D, new() { ["a"] = "1"u8.ToArray(), ["b"] = "1"u8.ToArray() });
    var r = ChangesPk3.Write(dir, D, new() { ["A"] = "2"u8.ToArray() });
    Assert.False(r.Created); // zip has exactly a == "2" and b == "1"
}
[Fact] public void Write_TargetLocked_ThrowsAndLeavesTargetUntouched() {
    ChangesPk3.Write(dir, D, new() { ["a"] = "1"u8.ToArray() });
    var before = File.ReadAllBytes(target);
    using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.None))
        Assert.ThrowsAny<IOException>(() => ChangesPk3.Write(dir, D, new() { ["a"] = "2"u8.ToArray() }));
    Assert.Equal(before, File.ReadAllBytes(target));
    Assert.False(File.Exists(target + ".part"));
}
```

- [ ] **Step 2: Run** `dotnet test --filter ChangesPk3Tests`. Expected: FAIL (type missing).
- [ ] **Step 3: Implement** `ChangesPk3` as described in Interfaces (`ZipArchive`, `CompressionLevel.Optimal`).
- [ ] **Step 4: Run** the same command. Expected: 4 passed.
- [ ] **Step 5: Commit** `feat: atomic writer for the dated changes pk3`.

### Task 3: Staging instead of loose writes

**Files:** Create `PendingChanges.cs`, `tests/.../PendingChangesTests.cs`. Modify `ImageStore.cs`, `Game1.cs` (remove `SaveFile`, `WriteLoose`, the per-file "Save" button and its tooltip in `DrawFileHeader`; keep "Discard changes"), `Game1.Editors.cs` (`SaveStr`, `RenameFile`, `CreateFile`, translation edit at ~547 and `CreateTranslation` ~698/733).

**Interfaces:**
- Produces: `sealed class PendingChanges { void Stage(string path, byte[] content); void StageDelete(string path); bool Remove(string path); IReadOnlyDictionary<string, byte[]> Items { get; } bool Any { get; } void Clear(); }`. Keys are normalized (`\` → `/`, leading `/` trimmed) and case-insensitive.
- `ImageStore` gets a constructor parameter `Action<string, byte[]> stage`. `Write` calls `stage(target, bytes)` instead of writing to disk. `Game1` passes `(p, b) => { _pending.Stage(p, b); _index!.AddMemory(p, b); }`. `Move` no longer deletes a loose source. If the source entry has `Memory != null`, it calls `_pending.Remove` and `_index.Remove` on it.
- `SaveStr(sf)` is replaced by: `sf.Dirty` stays true and the str stays in `_strCache`. The existing eviction at ~1265 already keeps dirty entries. `CreateTranslation` no longer throws on save.
- `RenameFile`: `pf.Entry = _index.AddMemory(newPath, Latin1(pf.Config.Serialize()))`, `pf.Dirty = true`, `_pending.Remove(newPath)`. If `!pf.IsNew`, also `_pending.StageDelete(old.Path)`.
- `CreateFile`: `Entry = _index.AddMemory(path, [])`, `IsNew = true`, `Dirty = true`.

- [ ] **Step 1: Write the failing tests:** `Stage_NormalizesAndIsCaseInsensitive` (`Stage("\\Gfx/A.png", x)` then `Items["gfx/a.png"]` is `x`), `StageDelete_StoresEmpty`, `Remove_ReturnsFalseWhenAbsent`.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** `PendingChanges` and the call-site changes above. `grep -n "WriteLoose\|File.Delete\|WriteAllBytes" *.cs` must show no hit outside `Game1.Editors.cs` debug dumps (~1523–1597) and `Settings.cs`.
- [ ] **Step 4: Run** tests (pass) and `dotnet build` (0 errors).
- [ ] **Step 5: Commit** `refactor: stage edits in memory instead of writing loose files`.

### Task 4: Global save

**Files:** Create `Game1.Save.cs`, `tests/.../SaveTests.cs`. Modify `Game1.cs` (File menu, Ctrl+S, `RevertFile`, `ImGuiRenderer` key map at line ~84: add `Keys.S`).

**Interfaces:**
- Consumes: `ChangesPk3.Write`, `PendingChanges`, `Pk3Index.AddPk3Entry/AddLoose`, `GameLocator.LoosePath`.
- Produces:
  - `static class SavePlan { static List<string> FindMaskingLoose(string gameDir, IEnumerable<string> paths); }`: the paths for which a file exists at `GameLocator.LoosePath(gameDir, path)`.
  - In `Game1.Save.cs`: `bool HasPendingChanges()` (any dirty `ParsedFile`, any dirty `StrFile` in `_strCache`, `_pending.Any`, or `_defs.AnyDirty` once Task 6 exists); `Dictionary<string, byte[]> CollectChanges()`; `void BeginSave()`; `void ExecuteSave(Dictionary<string, byte[]> changes, bool deleteLoose)`.
- Flow of `ExecuteSave`:
  1. `ChangesPk3.Write`. On exception, `_status = "Save failed: " + message` and stop: nothing is cleared.
  2. If `deleteLoose`, delete each masking loose file. Report failures in `_status`; the save stays done.
  3. For each path: `AddLoose` if its loose file still exists, otherwise `AddPk3Entry`.
  4. Dirty files get `Entry = _index.Files[path]`, `Dirty = false`, `IsNew = false`. Then `_pending.Clear()`, clear `_strCache` non-dirty entries, `_factionTitles = null`.
  5. If `Created`, open the "Changes pk3 created" modal.
- Dialog copy (exact):
  - Modal **"Loose files hide these changes"**: the list of paths, the text `These loose files in SWGL/ override every pk3, so the saved changes would not be visible in game.`, and the buttons **"Delete loose files and save"** and **"Save and keep them"**.
  - Modal **"Changes pk3 created"**: `Created {path}.` / `Restart the game to see the changes.` / `Ship this pk3 with the mod so other players get them too.`, then an **OK** button.
- File menu: **"Save changes"** with shortcut text `Ctrl+S`, enabled when `HasPendingChanges()`. Ctrl+S triggers the same action.
- `RevertFile` of a never-saved file (`IsNew`) still reverts to empty.

- [ ] **Step 1: Write the failing tests:** `FindMaskingLoose_ReturnsOnlyExistingLooseFiles` (temp GameData with `SWGL/ext_data/a.cha` on disk; paths `ext_data/a.cha` and `ext_data/b.cha` → `["ext_data/a.cha"]`), and `FindMaskingLoose_RejectsEscapingPath` (`"../x"` throws `InvalidOperationException`).
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** `SavePlan` and `Game1.Save.cs` as above.
- [ ] **Step 4: Run** tests (pass), then the manual check against a scratch GameData folder (never the real one). Create the folder: a copy containing `SWGL_SP.x86_64.exe` (an empty file is enough), an empty `base/`, and `SWGL/` with copies of `SWGL_Characters.pk3`, `SWGL_Weapons.pk3`, `SWGL_Saber_Hilts.pk3` and `SWGL_NPC_REPUBLIC.pk3`. Edit the name of AAYLA's DEFAULT variant, then Ctrl+S → `SWGL/Z___SWGL_Characters_changes_<today>.pk3` exists and the created dialog shows. Re-index → the edit is still there. Save again → no dialog. Put a loose `SWGL/ext_data/characters/SWGL_A.cha` on disk, edit and save → the loose-files dialog lists it.
- [ ] **Step 5: Commit** `feat: save all pending changes into the dated changes pk3`.

### Task 5: `_def.cfg` model

**Files:** Create `DefFile.cs`, `tests/.../DefFileTests.cs`

**Interfaces:**
- Produces:
  - `sealed class DefLine { string Key; string Value; }`
  - `sealed class DefFile { List<DefLine> Lines; static DefFile Parse(string text); string Serialize(); string? Get(string key); void Set(string key, string value); DefFile Clone(); static DefFile CreateEmpty(); }`
  - `static class DefKeys { static readonly string[] Order; static readonly string[] Powers; static readonly string[] SaberCombat; static readonly string[] WeaponSlots; }`
- `Parse` rules:
  - Split on `\r\n`, `\n` or `\r`; skip blank lines.
  - The key is the first token; the value is the rest, trimmed. `""` is kept literally.
  - A line with no value gets `Value = ""`.
- `Get` is case-insensitive.
- `Set` rules:
  - It replaces the value in place and keeps the existing key spelling.
  - A missing key is appended with the spelling from `DefKeys.Order`.
  - `Set(key, "")` removes the line.
- `Serialize`: see Global Constraints.
- `DefKeys.Order` (spec majority order): the 22 power keys in file order (`ui_jump_level` … `ui_strike_level`, `ui_sabthrow_level`, `ui_saboff_level`, `ui_sabdef_level`), then `ui_health`, `ui_team`, `ui_char_color_red`, `ui_char_color_green`, `ui_char_color_blue`, `ui_npc_weapon`, `ui_weaponOne`…`ui_weaponSix`, `ui_lightning_color`, `ui_saber`, `ui_saber_color`, `ui_saber2`, `ui_saber2_color`, `ui_saber_styles`, `ui_npc_type`, `ui_force`.
- `CreateEmpty()`: values of `0_0_0_DEFAULT_def.cfg`, in `Order`, without a `ui_npc_type` line:
  - powers 0; health 200; team `neutral`; colors 255/255/255;
  - NPC weapon `weapon_none`; slot 1 `weapon_melee`; slots 2–6 `weapon_none`;
  - lightning `blue`; sabers `empty`; colors `blue` / `red`; styles 0; force 0.

- [ ] **Step 1: Write the failing tests:**

```csharp
[Fact] public void RoundTrip_UnchangedFile_IsIdentical() {
    var t = "ui_jump_level 3\r\nui_team player\r\nui_npc_weapon\tweapon_saber\r\nui_force 200";
    Assert.Equal(t, DefFile.Parse(t).Serialize());
}
[Fact] public void Edit_PreservesUnknownKeysAndOddValues() {
    var d = DefFile.Parse("ui_custom_x\tfoo bar\r\nui_saber\t\"\"\r\nui_health 300");
    d.Set("UI_HEALTH", "400");
    Assert.Equal("ui_custom_x\tfoo bar\r\nui_saber\t\"\"\r\nui_health 400", d.Serialize());
}
[Fact] public void Set_MissingKey_AppendsWithCanonicalSpelling() {
    var d = DefFile.Parse("ui_health 1"); d.Set("ui_weaponone", "weapon_saber");
    Assert.Equal("ui_health 1\r\nui_weaponOne\tweapon_saber", d.Serialize());
}
[Fact] public void Set_Empty_RemovesLine() { var d = DefFile.Parse("ui_npc_type\tX\r\nui_force 0"); d.Set("ui_npc_type", ""); Assert.Equal("ui_force 0", d.Serialize()); }
[Fact] public void CreateEmpty_MatchesTemplate() {
    var d = DefFile.CreateEmpty();
    Assert.Equal(41, d.Lines.Count); Assert.Null(d.Get("ui_npc_type"));
    Assert.Equal("200", d.Get("ui_health")); Assert.Equal("weapon_melee", d.Get("ui_weaponOne")); Assert.Equal("ui_force", d.Lines[^1].Key);
}
[Fact] public void Parse_LfAndTrailingNewline_Tolerated() => Assert.Equal("1", DefFile.Parse("ui_force 1\n").Get("ui_force"));
```

- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** `DefFile` and `DefKeys`.
- [ ] **Step 4: Run.** Expected: 6 passed.
- [ ] **Step 5: Commit** `feat: _def.cfg parser and serializer`.

### Task 6: Variant ↔ `_def.cfg` binding

**Files:** Create `DefStore.cs`, `tests/.../DefStoreTests.cs`

**Interfaces:**
- Consumes: `Pk3Index`, `DefFile`, `ConfigFile`/`Block` (a character is a top-level block with a `code`; its variants are children with `Type == "Variant"`).
- Produces:
  - `sealed class DefStore`:
    - `const string Dir = "ext_data/characters_conf/"`; `static string PathFor(string characterCode, string variantCode)`
    - `DefStore(Pk3Index index)`
    - `void Bind(ConfigFile cha)`: each variant gets `OriginPath = PathFor(...)` when the index has that path with `Size > 0`, otherwise null.
    - `void Unbind(ConfigFile cha)`: drops the bindings of every block of that file, including variants already removed from it. Track bindings per `ConfigFile`.
    - `string? CurrentPath(ConfigFile cha, Block variant)`
    - `DefFile? Load(Block variant)`: lazy, from the origin.
    - `void SetDef(Block variant, DefFile def)`: binding created if needed, `Dirty = true`.
    - `void MarkDirty(Block variant)`; `bool IsDirty(Block variant)`; `bool AnyDirty { get; }`
    - `void CopyDefs(Block source, Block clone)`: for a variant, or recursively for a character, pairs children by index and calls `SetDef(cloneChild, Load(srcChild).Clone())` when the source has a def.
    - `Dictionary<string, byte[]> CollectChanges(IEnumerable<ConfigFile> chaFiles)`
    - `void AfterSave(IEnumerable<ConfigFile> chaFiles)`
- `CollectChanges` algorithm (paths compared case-insensitively):
  1. `live` = every (variant, current path) found in `chaFiles`.
  2. For each live variant with a binding, write `(Def ?? Load()).Serialize()` (Latin-1) at its current path when `Dirty`, or when origin ≠ current path and the def exists.
  3. Candidate deletions: the origin of a live variant whose path changed, and the origin of every binding whose block is no longer live.
  4. Each candidate that is neither a written path nor any live current path becomes an empty entry.
- `AfterSave`: origin = current path for live bindings, `Dirty = false`, non-live bindings dropped.

- [ ] **Step 1: Write the failing tests.** Fixture: an index where `AddMemory` creates `…/AAYLA_DEFAULT_def.cfg = "ui_force 1"` and `…/AAYLA_ALT_def.cfg = "ui_force 2"`, plus a `ConfigFile` parsed from a `.cha` with character `AAYLA` and variants `DEFAULT` and `ALT`.
  - `Unchanged_NoChanges`: `CollectChanges` is empty.
  - `RenameVariant_WritesNewAndEmptiesOld`: `DEFAULT` → `NEW` gives `AAYLA_NEW_def.cfg = "ui_force 1"` and `AAYLA_DEFAULT_def.cfg = []`.
  - `RenameAndBack_NoChanges`
  - `RenameCharacter_MovesEveryVariant`: 2 writes and 2 empties.
  - `DeleteVariant_EmptiesItsDef`
  - `DeleteThenRenameIntoSamePath_WritesContentNotEmpty`: remove `DEFAULT`, rename `ALT` → `DEFAULT` gives `AAYLA_DEFAULT_def.cfg = "ui_force 2"` and `AAYLA_ALT_def.cfg = []`.
  - `CopyDefs_NewVariantGetsCopy`: the clone with code `COPY` gives `AAYLA_COPY_def.cfg = "ui_force 1"` and no deletion.
  - `UnbindAfterDelete_DoesNotDeleteDef`: remove `ALT`, `Unbind`, re-parse the original `.cha`, `Bind` → no changes.
  - `MissingDef_NoBindingContent_NoChange`: a variant without a file produces no entry.
  - `AfterSave_ClearsState`: a second `CollectChanges` is empty.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** `DefStore`. In `Game1`:
  - create `_defs` at index time and `Bind` each `.cha` (`CreateFile` of a `.cha` too);
  - in `RevertFile`, `Unbind(old)` then `Bind(new)`;
  - in `Transfer`, call `_defs.CopyDefs(src.Block, clone)`;
  - in `CollectChanges` (Task 4), merge `_defs.CollectChanges(chaFiles)` (def entries override staged entries for the same path);
  - in `ExecuteSave`, call `_defs.AfterSave(chaFiles)`;
  - in `HasPendingChanges`, include `_defs.AnyDirty`.
- [ ] **Step 4: Run** tests (10 passed) and `dotnet build`.
- [ ] **Step 5: Commit** `feat: bind _def.cfg files to variants and derive renames/deletes`.

### Task 7: Game data catalog and value rules

**Files:** Create `GameDataCatalog.cs`, `DefRules.cs`, `tests/.../GameDataCatalogTests.cs`, `tests/.../DefRulesTests.cs`

**Interfaces:**
- Produces:
  - `record WeaponDef(string Name, bool PlayerUsable)`
  - `record TextBlock(string? Name, Dictionary<string, string> Props)`: props at depth 1 only, case-insensitive keys.
  - `sealed class GameDataCatalog`:
    - `IReadOnlyList<WeaponDef> Weapons`, `IReadOnlyList<string> Sabers`, `IReadOnlyList<string> NpcTypes`: sorted case-insensitively, original casing kept.
    - `WeaponDef? FindWeapon(string name)`, `string? FindSaber(string name)`, `string? FindNpc(string name)`: case-insensitive.
    - `static GameDataCatalog Build(Pk3Index index)`
    - `static List<TextBlock> ParseBlocks(string text)`: tolerant tokenizer that skips `//` and `/* */` comments, handles quoted strings and nested blocks; `Name` is the token just before a depth-0 `{`.
  - `static class DefRules`:
    - `Teams = { "player", "enemy", "free", "neutral" }`
    - `SaberColorsNamed = { "blue", "red", "green", "purple", "yellow", "orange", "black" }`
    - `SaberColorsSpecial = { "random", "prequel_random", "swtor_sith_random", "unstable_red", "darksaber" }`
    - `LightningColors = { "blue", "purple", "green", "black", "red", "yellow", "white", "orange" }`
    - `SaberStyles = { ("Fast",1), ("Medium",2), ("Strong",4), ("Desann",8), ("Tavion",16), ("Dual",32), ("Staff",64) }`
    - `bool TryParseHex(string v, out byte r, out byte g, out byte b)`: exactly `x` + 6 hex digits, either case.
    - `string FormatHex(byte r, byte g, byte b)`: `x` + uppercase digits.
    - `List<string> Validate(DefFile def, GameDataCatalog cat)`
- `Build` sources:
  - weapons: `weaponclass` of each block of `ext_data/weapons.dat` then of `index.Find("ext_data", ".wpn")`. A later definition replaces an earlier one with the same name. `playerUsable` absent → true *(builder's call from the spec)*.
  - sabers: block names of `index.Find("ext_data/sabers", ".sab")`.
  - NPCs: block names of `index.Find("ext_data/npcs", ".npc")`.
  - `weapon_none` is always present.
  - Zero-length entries are skipped.
- `Validate` messages, one per problem, exact format `"<key>: <problem>"`:
  - `unknown NPC type "X"`, `unknown saber "X"`, `unknown weapon "X"`
  - `weapon "x" is defined as "X"` (casing differs)
  - `invalid color "X"`: for a saber color that is neither named, special nor valid hex
  - `unknown lightning color "X"`
  - `"X" is not a number`; `X is out of range (0-3)` for powers; health < 1; force < 0; tint outside 0–255
  - `no _def.cfg for this variant` is produced by the UI, not here
  - Empty / `""` values for `ui_saber`, `ui_saber2` and `ui_npc_type`, and `empty` for sabers, are not errors.

- [ ] **Step 1: Write the failing tests:**
  - `ParseBlocks_NamedAndAnonymous`: `"// c\nfoo { a 1 sub { b 2 } }\n{ weaponclass weapon_x playerUsable 0 }"` gives 2 blocks: `foo` with `a=1` only, and an unnamed block with `weaponclass=weapon_x`.
  - `Build_WeaponsFromDatAndWpn_WithPlayerUsable`: in-memory `ext_data/weapons.dat` with `weapon_melee`, `ext_data/weapon_clonerandom.wpn` with `playerUsable 0`, and `ext_data/weapon_z6.wpn` without the field give `clonerandom` false, `z6` true and `weapon_none` present.
  - `Build_SabersAndNpcs`
  - `FindWeapon_IsCaseInsensitive_ReturnsDefinedCasing`: `"weapon_dlt20a"` → `"weapon_DLT20A"`.
  - `TryParseHex`: `xadd8e6` true, `x00FFFFF` false, `#8CCFFF` false.
  - `Validate_ReportsEachProblem`: `ui_npc_type tusken_king` (unknown), `ui_weaponOne weapon_dlt20a` (casing), `ui_saber_color x00FFFFF`, `ui_jump_level 5`, `ui_health abc` each produce a message starting with their key; `ui_saber ""` produces none.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** both files. In `Game1`, build `_catalog` in the indexing task after `Pk3Index.Build`.
- [ ] **Step 4: Run.** Expected: all pass.
- [ ] **Step 5: Commit** `feat: game data catalog and _def.cfg value rules`.

### Task 8: Config tab and add-variant choice

**Files:** Create `Game1.Config.cs`. Modify `Game1.cs` (`DrawEditor`, tree label in `DrawNode`), `Game1.Editors.cs` ("Add variant" button ~501).

**Interfaces:**
- Consumes: `_defs` (Task 6), `_catalog`, `DefRules`, `DefKeys`, `DefFile`.
- Produces:
  - `void DrawConfigTab(Node variantNode)`
  - `bool FilterCombo(string id, string current, IReadOnlyList<string> items, out string chosen)`: a combo with an `InputTextWithHint("Filter...")` at the top. It shows `current` even when it is not in `items`.
  - `void OpenNewVariantConfig(Block variant)`: modal **"New variant config"** with the choices **"Empty config"** and **"Copy from…"** (a filterable list of every live variant, `CHAR / VARIANT`, that has a def).

- [ ] **Step 1: Wire the tabs.** In `DrawEditor`'s `NodeKind.Block` case, when `b.Type == "Variant"`, wrap the existing header and fields in `BeginTabBar("##variant")` with the tabs `"Variant"` and `(_defs.IsDirty(b) ? "Config *" : "Config") + "###config"`. In `DrawNode`, append `" *"` to a variant label when `_defs.IsDirty(block)`.
- [ ] **Step 2: Implement `DrawConfigTab`**, sections in this order with these labels:
  - Header: the def path and `Source: <folder>/<pk3>`, or `(new, not saved)`. Then each `DefRules.Validate` message in orange.
  - No def: the text `No _def.cfg for this variant.` and the buttons **Create empty** / **Copy from…**, which open the modal.
  - **Stats**:
    - Health: `InputInt`, min 1. Force pool: min 0.
    - Team: combo.
    - NPC type: `FilterCombo` over `NpcTypes`, with a clear button.
    - Character tint: `ColorEdit3` ↔ 0–255 ints.
  - **Weapons**: NPC weapon (all weapons); Slot 1 … Slot 6 (`PlayerUsable` + `weapon_none`). Write the defined casing.
  - **Sabers**:
    - Saber 1 / Saber 2: `FilterCombo` over `empty` + `Sabers`.
    - Color 1 / Color 2: combo over named + special + `Custom…`. Custom shows `ColorEdit3`, which writes `FormatHex`.
    - Styles: 7 checkboxes toggling bits with `^`, keeping unknown bits.
    - Lightning color: combo.
  - **Powers (0-3)**: a 2-column table, labels Jump, Push, Pull, Speed, Sense, Absorb, Heal, Protect, Mind trick, Stasis, Blast, Grasp, Grip, Lightning, Drain, Rage, Destruction, Fear, Strike. Each row has four toggle buttons 0–3, the current one highlighted.
  - **Saber combat (0-3)**: Throw, Offense, Defense.
  - Every change goes through `def.Set(...)` + `_defs.MarkDirty(b)`. The first edit loads the def via `_defs.Load`.
- [ ] **Step 3: Add variant.** After the "Add variant" button creates `v`, call `OpenNewVariantConfig(v)`. Closing the modal without choosing leaves the variant without a def; the Config tab shows the "No _def.cfg" state.
- [ ] **Step 4: Verify manually** on the scratch GameData from Task 4:
  - AAYLA › DEFAULT › Config shows health 300, force 200, Fast/Medium/Strong checked, saber `aayla_secura`.
  - Set Jump to 1 → `Config *` and `DEFAULT *`.
  - A character with `ui_npc_type tusken_king` shows the unknown-NPC warning.
  - Add a variant → Copy from AAYLA / DEFAULT.
  - Rename AAYLA's code to `AAYLA2`, Ctrl+S, then list the changes pk3 (`unzip -l`): it contains `ext_data/characters_conf/AAYLA2_DEFAULT_def.cfg` with content, `AAYLA_DEFAULT_def.cfg` with size 0, and the new variant's def.
- [ ] **Step 5: Commit** `feat: Config tab for variant _def.cfg files`.

### Task 9: Final verification

- [ ] **Step 1:** `dotnet test tests/SWGLCharacterCreator.Tests`. Expected: all pass.
- [ ] **Step 2:** `dotnet publish SWGLCharacterCreator.csproj -c Release -r win-x64 -o publish`. Expected: succeeds, and no test file is compiled into the app.
- [ ] **Step 3:** Re-run the manual checks of Tasks 4 and 8 on the scratch GameData, then confirm that the real GameData folder has no new file (`ls "<GameData>/SWGL" | grep -i changes` is empty).
