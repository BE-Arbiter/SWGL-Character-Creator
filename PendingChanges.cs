namespace SWGLCharacterCreator;

/// <summary>
/// Edits staged in memory until they are saved into the changes pk3.
/// An empty content array marks a deletion (see <see cref="StageDelete"/>).
/// </summary>
public sealed class PendingChanges
{
    private readonly Dictionary<string, byte[]> _items = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, byte[]> Items => _items;
    public bool Any => _items.Count > 0;

    private static string Key(string path) => path.Trim().Replace('\\', '/').TrimStart('/');

    public void Stage(string path, byte[] content) => _items[Key(path)] = content;

    public void StageDelete(string path) => _items[Key(path)] = Array.Empty<byte>();

    public bool Remove(string path) => _items.Remove(Key(path));

    public void Clear() => _items.Clear();
}
