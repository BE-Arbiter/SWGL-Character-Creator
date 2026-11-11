namespace SWGLCharacterCreator;

/// <summary>State transitions for creating and renaming files while edits are staged in memory.</summary>
public static class EditStaging
{
    /// <summary>Registers a new empty in-memory file and stages it.</summary>
    public static Pk3Entry Create(PendingChanges pending, Pk3Index index, string path)
    {
        var content = Array.Empty<byte>();
        pending.Stage(path, content);
        return index.AddMemory(path, content);
    }

    /// <summary>
    /// Moves a file's content to <paramref name="newPath"/>. A previously staged (in-memory) source is dropped;
    /// a real source (pk3 or loose), or a real file hidden under a staged one, is staged for deletion. A case-only
    /// rename keeps the same path, so nothing is removed yet (the real file is remembered). Returns the new in-memory entry.
    /// </summary>
    public static Pk3Entry Rename(PendingChanges pending, Pk3Index index, Pk3Entry old, string newPath, byte[] content)
    {
        bool realUnder = old.Memory == null || pending.IsRealUnder(old.Path);
        if (!string.Equals(old.Path, newPath, StringComparison.OrdinalIgnoreCase))
        {
            if (old.Memory != null) { pending.Remove(old.Path); index.Remove(old.Path); }
            if (realUnder) pending.StageDelete(old.Path);
        }
        else if (realUnder) pending.MarkRealUnder(newPath); // case-only rename: the real file stays underneath
        pending.Stage(newPath, content);
        return index.AddMemory(newPath, content);
    }
}
