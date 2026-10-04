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
}
