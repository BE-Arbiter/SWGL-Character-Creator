namespace SWGLCharacterCreator;

public sealed record SaveOutcome(ChangesPk3Result Result, List<string> DeleteErrors);

/// <summary>The non-UI parts of "Save changes": finding loose files that would hide the save, and writing + repointing the index.</summary>
public static class SavePlan
{
    /// <summary>Paths for which a loose file exists under SWGL/ (loose files override every pk3).</summary>
    public static List<string> FindMaskingLoose(string gameDir, IEnumerable<string> paths) =>
        paths.Where(p => File.Exists(GameLocator.LoosePath(gameDir, p))).ToList();

    /// <summary>
    /// Writes <paramref name="changes"/> into the dated changes pk3 (throws on failure, nothing else is touched), optionally
    /// deletes the masking loose files (failures are reported, not thrown), then repoints every saved path in
    /// <paramref name="index"/> at the loose file that still wins, or at the new pk3 entry.
    /// </summary>
    public static SaveOutcome Commit(string gameDir, DateTime date, Dictionary<string, byte[]> changes, bool deleteLoose, Pk3Index index)
    {
        var result = ChangesPk3.Write(Path.Combine(gameDir, "SWGL"), date, changes);
        var errors = new List<string>();
        if (deleteLoose)
            foreach (var p in FindMaskingLoose(gameDir, changes.Keys))
                try { File.Delete(GameLocator.LoosePath(gameDir, p)); }
                catch (Exception ex) { errors.Add($"{p}: {ex.Message}"); }

        foreach (var (path, data) in changes)
        {
            var loose = GameLocator.LoosePath(gameDir, path);
            if (File.Exists(loose)) index.AddLoose(path, loose);
            else index.AddPk3Entry(path, result.Pk3Path, data.Length);
        }
        return new SaveOutcome(result, errors);
    }
}
