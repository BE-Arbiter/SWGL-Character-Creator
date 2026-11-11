# Character config (`_def.cfg`) editor and pk3 change saving — design

Date: 2026-11-11

## Why

Every character variant in SWGL has a `ext_data/characters_conf/<CHAR>_<VARIANT>_def.cfg` file that sets its
stats, powers, weapons and sabers. Today they are edited by hand in a text editor, without knowing which keys
exist or which values are valid (exact weapon names, saber names, what `ui_saber_styles 71` means). That is how
the existing files ended up with inconsistent weapon casing (`weapon_DLT20A` / `weapon_dlt20a`) and malformed
hex colors (`x00FFFFF`).

The editor makes the format invisible: every option is shown with a readable label and a control that only
accepts valid values, with the lists of weapons, sabers and NPC types read from the game data so that newly
added content (a new `.wpn`, `.sab`, `.npc`) shows up without changing the app.

At the same time, saving changes moves from "one loose file per edited file" to "save all pending changes into
a dated changes pk3", for every file type the app edits.

This is two pieces of work, in this order:

1. **Saving changes into a pk3** (applies to the whole app).
2. **The `_def.cfg` editor** (builds on 1).

## Goals

- Edit every option of a variant's `_def.cfg` from the app, without knowing the file format.
- Only valid values can be entered; lists come from the game folder, not hard-coded lists.
- Flag problems already present in a file (unknown NPC type, malformed color, weapon name casing).
- Keep the `_def.cfg` in sync when a variant or character is renamed, moved, copied or deleted.
- One save action writes all pending changes of the session into a dated changes pk3 in `SWGL/` (name below).

## Non-goals

- No loose-file saving anymore. Existing loose files are still read (the game reads them), but the app never
  writes new ones.
- Original pk3 files are never modified.
- The faction-prefixed copies (`REP_AAYLA_DEFAULT_def.cfg`, 1 432 files) and stray files
  (`REB_K2SO_default_def - Copy.cfg`) are ignored: neither shown nor edited nor cleaned up.
- No bulk fixing of existing data (the editor reports problems; it does not rewrite files nobody edited).

## Anti-goals (failure even if it "works")

- A corrupted or half-written pk3 after a crash or a locked file.
- Losing data: an unknown key in a `_def.cfg`, or file content the app does not understand, disappearing on save.
- A hard-coded list of weapons/sabers/NPCs that goes stale when the mod adds content.
- UI in French: the app UI is English.

---

## Part 1 — Saving changes into a pk3

### Behaviour (decided)

- Saving writes **all pending changes** (every dirty `.fac`, `.cha`, `.str`, `_def.cfg`, imported image) in one
  operation. The per-file "Save" button is replaced by a single global save (File menu + Ctrl+S; exact
  placement is the builder's call).
- Destination: `<GameData>/SWGL/Z___SWGL_Characters_changes_yymmdd.pk3` (three underscores), where `yymmdd`
  is the local date of the save (e.g. `Z___SWGL_Characters_changes_261111.pk3`).
  - Why this name *(user decided: change the prefix so it loads last; exact spelling is Claude's call)*: the
    engine sorts pk3 files with `FS_PathCmp`, which compares names **uppercased**, so `_` (0x5F) sorts after
    every letter. With the originally proposed `Z_SWGL_Characters_changes_…`, the existing
    `Z_SWGL_dw_plasma_grenade.pk3` would load after it. `Z___` sorts after every `Z_<letter>…` and `ZZ…` name.
    `Pk3Index` already uses `StringComparer.OrdinalIgnoreCase`, which also uppercases, so the app and the game
    agree.
  - No post-save check for later pk3 files overriding a saved path (user decided).
- If today's pk3 already exists, it is **updated**: entries for the saved paths are replaced, other entries
  are kept. The next day a new pk3 is created; previous days' pk3 files stay and load in date order, so the
  most recent wins.
- When a save creates a new day's pk3, the user is **warned** with a dialog giving its full path (and,
  builder's call on wording, that the game must be restarted and that the pk3 must be shipped with the mod for
  others to see the changes).
- Loose-file saving is removed.

### Details (builder's call unless stated)

- Each changed file is stored whole, at its game path (`ext_data/characters/SWGL_A.cha`,
  `ext_data/characters_conf/AAYLA_DEFAULT_def.cfg`, `strings/...`, `gfx/...`).
- A deleted or renamed-away path is stored as an **empty entry** at the old path in the changes pk3, so it
  overrides the original in an earlier pk3. This is the same trick the app already uses with empty loose files.
- Writing must be atomic: build the new archive in a temporary file in `SWGL/` (e.g.
  `Z___SWGL_Characters_changes_yymmdd.pk3.part`, already skipped by the indexer), then replace the target. On
  failure (file locked because the game is running, disk full…), the previous pk3 is left untouched, nothing is
  marked clean, and the error is shown.
- Text files keep the current byte-preserving behaviour (Latin-1 round-trip).
- After a successful save, the in-memory index points the saved paths at the changes pk3 (as `AddLoose` does
  today for loose files), dirty flags are cleared, and "Revert" reloads from that entry.
- The indexer already loads `SWGL/*.pk3` alphabetically, so the changes pk3 is picked up on re-index without
  changes.

### Loose files that would hide a saved change (decided)

In the game (and in `Pk3Index`), a loose file under `SWGL/` overrides every pk3, including the changes pk3.

- Before writing, the save lists every path being saved (including empty "deleted" entries) that also exists
  as a loose file under `SWGL/`.
- If there are any, a dialog shows that list and offers to **delete those loose files** (Delete / Keep).
  - Delete: the pk3 is written first; only after it was written successfully are the loose files deleted. A
    loose file that cannot be deleted is reported; the save itself stays done.
  - Keep: the save goes ahead and the user is warned that those changes will not be visible in game while the
    loose files exist.
- The index is then updated so that the winning entry for each path is the loose file if it was kept, the
  changes pk3 otherwise.
- The existing code paths that write or delete loose files (`WriteLoose`, `RenameFile`, `SaveStr`, image import
  and move in `ImageStore`) are converted to the pending-change model; nothing else deletes loose files.

---

## Part 2 — The `_def.cfg` editor

### File format (from a review of the 2 871 files in `SWGL_Characters.pk3`)

- One `key value` per line. No blocks, comments, quotes or duplicate keys.
- Separator: space before numbers, tab before strings (usage is not strict). Mostly CRLF, mostly no final
  newline.
- Keys are case-insensitive in practice; `ui_weaponOne`…`ui_weaponSix` are camelCase, all others lowercase.
- Two main key orders exist (the `ui_char_color_*` lines either after `ui_team` or after `ui_npc_type`).

### Which file a variant uses (decided)

`ext_data/characters_conf/<CHARACTER_CODE>_<VARIANT_CODE>_def.cfg`, matched case-insensitively, using the
normal index priority (loose > changes pk3 > other pk3 files). Exactly like the game. Every one of the 1 438
existing variants has one.

### The 42 keys and their controls

| Section | Key(s) | Label | Control / values |
|---|---|---|---|
| Stats | `ui_health` | Health | integer ≥ 1 (seen: 10 – 10 000) |
| | `ui_force` | Force pool | integer ≥ 0 (seen: 0 – 600) |
| | `ui_team` | Team | `player`, `enemy`, `free`, `neutral` |
| | `ui_npc_type` | NPC type | filterable list of NPC names from every `ext_data/npcs/*.npc` |
| | `ui_char_color_red/green/blue` | Character tint | color picker, 0 – 255 per channel |
| Weapons | `ui_npc_weapon` | NPC weapon | all weapons |
| | `ui_weaponOne` … `ui_weaponSix` | Slot 1 … 6 | weapons with `playerUsable 1`, plus `weapon_none` |
| Sabers | `ui_saber`, `ui_saber2` | Saber 1 / 2 | filterable list of saber names from every `ext_data/sabers/*.sab`, plus `empty` |
| | `ui_saber_color`, `ui_saber2_color` | Color 1 / 2 | named color (`blue`, `red`, `green`, `purple`, `yellow`, `orange`, `black`), special value (`random`, `prequel_random`, `swtor_sith_random`, `unstable_red`, `darksaber`) or custom hex written as `xRRGGBB` |
| | `ui_saber_styles` | Styles | checkboxes, bitmask: Fast 1, Medium 2, Strong 4, Desann 8, Tavion 16, Dual 32, Staff 64 |
| | `ui_lightning_color` | Lightning color | `blue`, `purple`, `green`, `black`, `red`, `yellow`, `white`, `orange` |
| Powers (0 – 3) | `ui_jump/push/pull/speed/sense/absorb/heal/protect/mindtrick/stasis/blast/grasp/grip/lightning/drain/rage/destruction/fear/strike_level` | Jump … Strike | segmented 0 / 1 / 2 / 3, two columns, in file order |
| Saber combat (0 – 3) | `ui_sabthrow_level`, `ui_saboff_level`, `ui_sabdef_level` | Throw, Offense, Defense | segmented 0 / 1 / 2 / 3 |

Notes:

- The saber-style bitmask is **inferred** from the values seen (7, 32, 64, 71, 127 …), not confirmed in game
  code. Values above 127 or bits not listed must be preserved as they are, not dropped.
- Named and special saber/lightning colors are the values seen in the data; a value not in the list is kept and
  shown as-is (with a warning), never silently replaced.

### Lists read from the game (decided: never hard-coded)

- **Weapons**: the `weaponclass` of every block in `ext_data/weapons.dat` and of every `ext_data/*.wpn`, across
  all indexed pk3 files and loose files. A later definition of the same weapon overrides an earlier one (as in
  the game). Today: 43 in `weapons.dat` + 31 `.wpn` files. The `playerUsable` field (case-insensitive) decides
  whether a weapon is offered for slots 1 – 6 (e.g. `weapon_clonerandom` has `playerUsable 0` and is only
  offered as NPC weapon). Default when the field is absent: builder's call, based on how the base game treats it.
- **Sabers**: the top-level names of every `ext_data/sabers/*.sab` (385 today).
- **NPC types**: the top-level names of every `ext_data/npcs/*.npc` (1 653 today).
- Names are written with the casing of their definition (`weapon_DLT20A`).

### Validation (shown as warnings at the top of the tab; never blocks saving)

- NPC type, saber or weapon not found in the game data (today: 5 NPC types in 10 files, e.g. `tusken_king`).
- Weapon name whose casing differs from its definition.
- Malformed hex color (`x00FFFFF`, `#8CCFFF`).
- Value out of range / not a number.
- Empty values that exist in the data (`ui_saber ""`, missing `ui_npc_type` in the 12 "random" Jedi/Sith
  templates) are allowed and shown as empty, not as errors (builder's call whether to show a soft note).
- Missing `_def.cfg` for a variant.

### UI (decided)

- When a **variant** is selected in the explorer, the right-hand editor panel shows two tabs: **Variant** (the
  current fields) and **Config** (this editor). The 3D preview in the center stays visible.
- Config tab content, top to bottom: file path and its source (pk3 name or "changes pk3"), warnings, then
  sections **Stats**, **Weapons**, **Sabers**, **Powers**, **Saber combat**, as in the approved mockup.
- English labels.
- An unsaved config shows `*` on the tab and on the variant in the tree, like other dirty files.

### Writing a `_def.cfg`

- Keep the key order of the file that was read; values replaced in place.
- New file: the majority order (2 035 files):
  powers, `ui_health`, `ui_team`, `ui_char_color_red/green/blue`, `ui_npc_weapon`, `ui_weaponOne…Six`,
  `ui_lightning_color`, `ui_saber`, `ui_saber_color`, `ui_saber2`, `ui_saber2_color`, `ui_saber_styles`,
  `ui_npc_type`, `ui_force`.
- Space separator before numbers, tab before strings, CRLF, keys spelled as in the existing files.
- Unknown keys found in a file are kept verbatim and in place.

### Lifecycle with variants and characters (decided)

- **Add variant**: the user chooses between an **empty config** (neutral values like
  `0_0_0_DEFAULT_def.cfg`: powers 0, health 200 *(builder's call: match that file)*, team `neutral`, tint
  255/255/255, NPC weapon `weapon_none`, slot 1 `weapon_melee`, others `weapon_none`, sabers `empty`, colors
  blue/red, styles 0, NPC type empty, force 0) or a **copy of an existing `_def.cfg`**, chosen in a filterable
  list of variants.
- **Rename** of a character code or variant code, **move** of a variant to another character, **copy** of a
  variant or character (Ctrl+drag, which may give a new unique code): the `_def.cfg` follows, i.e. written
  under the new name; on rename/move the old path becomes an empty entry.
- **Delete** of a variant or character: its `_def.cfg` path(s) become empty entries.
- Renaming a character renames the `_def.cfg` of each of its variants.

### What's left to the builder

- Code structure: a separate small parser/serializer for `_def.cfg` (it is not the block format of
  `ConfigFile`), a game-data catalog for weapons/sabers/NPCs (parsing `.sab`/`.npc`/`.wpn`/`weapons.dat` needs
  a tolerant tokenizer: `//` comments, quoted strings, nested blocks).
- Exact control widgets in ImGui (combo with filter, color picker, etc.) within the approved layout.
- Where the global save lives and its shortcut.
- Caching of the catalogs (loaded with the index or lazily on first use).
