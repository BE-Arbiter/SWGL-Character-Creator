namespace SWGLCharacterCreator;

public static class GameLocator
{
    public const string ExeName = "SWGL_SP.x86_64.exe";
    public const string DefaultPath = @"C:\Program Files (x86)\Steam\steamapps\common\Jedi Academy - SWGL_DEV\GameData\";

    /// <summary>
    /// Finds the folder containing the SWGL executable starting from <paramref name="selected"/>:
    /// the folder itself, then its "GameData" child, then each parent folder.
    /// </summary>
    public static string? Resolve(string selected)
    {
        if (string.IsNullOrWhiteSpace(selected) || !Directory.Exists(selected)) return null;
        var dir = new DirectoryInfo(selected);

        if (File.Exists(Path.Combine(dir.FullName, ExeName))) return dir.FullName;

        var child = Path.Combine(dir.FullName, "GameData");
        if (File.Exists(Path.Combine(child, ExeName))) return child;

        for (var p = dir.Parent; p != null; p = p.Parent)
            if (File.Exists(Path.Combine(p.FullName, ExeName))) return p.FullName;

        return null;
    }

    /// <summary>
    /// Full disk path of <paramref name="gamePath"/> under the game's SWGL folder. Throws if the path is rooted
    /// or escapes that folder (".."), so that an edit can never write outside of SWGL/.
    /// </summary>
    public static string LoosePath(string gameDir, string gamePath)
    {
        var root = Path.GetFullPath(Path.Combine(gameDir, "SWGL")) + Path.DirectorySeparatorChar;
        var rel = gamePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(rel) || rel.Contains(':'))
            throw new InvalidOperationException("The path must be relative to the game folder (e.g. gfx/icons/name.png).");
        var full = Path.GetFullPath(Path.Combine(root, rel));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The path must stay inside the game folder (no \"..\").");
        return full;
    }
}
