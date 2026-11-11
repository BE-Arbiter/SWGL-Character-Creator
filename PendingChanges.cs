namespace SWGLCharacterCreator;

/// <summary>
/// Edits staged in memory until they are saved into the changes pk3.
/// An empty content array marks a deletion (see <see cref="StageDelete"/>).
/// </summary>
public sealed class PendingChanges
{
    private readonly Dictionary<string, byte[]> _items = new(StringComparer.OrdinalIgnoreCase);

    // Paths where an in-memory entry replaced a real (pk3 or loose) file of the same path, e.g. by a case-only rename.
    private readonly HashSet<string> _realUnder = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, byte[]> Items => _items;
    public bool Any => _items.Count > 0;

    private static string Key(string path) => path.Trim().Replace('\\', '/').TrimStart('/');

    public void Stage(string path, byte[] content) => _items[Key(path)] = content;

    public void StageDelete(string path) => _items[Key(path)] = Array.Empty<byte>();

    public bool Remove(string path) => _items.Remove(Key(path));

    /// <summary>Remembers that a real file still lies underneath the in-memory entry at <paramref name="path"/>.</summary>
    public void MarkRealUnder(string path) => _realUnder.Add(Key(path));

    public bool IsRealUnder(string path) => _realUnder.Contains(Key(path));

    /// <summary>Registers every staged item (deletions included, as zero-length entries) in a freshly built index.</summary>
    public void ReplayInto(Pk3Index index)
    {
        foreach (var (path, data) in _items) index.AddMemory(path, data);
    }

    public void Clear() { _items.Clear(); _realUnder.Clear(); }
}
