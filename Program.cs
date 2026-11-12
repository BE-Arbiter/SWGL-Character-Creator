using System.Runtime.InteropServices;

try
{
    NativeLibraries.Preload();
    using var game = new SWGLCharacterCreator.Game1();
    game.Run();
}
catch (Exception ex)
{
    // A WinExe has no console: without this, a startup failure just makes the app vanish.
    var log = Path.Combine(Path.GetTempPath(), "SWGLCharacterCreator-crash.txt");
    try { File.WriteAllText(log, $"{DateTime.Now:O}\r\n{ex}"); } catch { /* best effort */ }
    NativeLibraries.MessageBox(IntPtr.Zero, $"SWGL Character Creator crashed:\r\n\r\n{ex.Message}\r\n\r\nDetails: {log}",
        "SWGL Character Creator", 0x10 /* MB_ICONERROR */);
    Environment.Exit(1);
}

static class NativeLibraries
{
    /// <summary>
    /// The single-file exe extracts its native libraries to a temp folder that only .NET's own probing knows about,
    /// but MonoGame loads SDL2/OpenAL itself with LoadLibrary (exe folder + PATH). Loading them here first puts them
    /// in the process, so MonoGame's LoadLibrary("SDL2.dll") gets the already-loaded module instead of failing.
    /// </summary>
    public static void Preload()
    {
        var dirs = (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in new[] { "SDL2.dll", "openal.dll" })
        {
            var path = dirs.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
            if (path != null) NativeLibrary.TryLoad(path, out _);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
