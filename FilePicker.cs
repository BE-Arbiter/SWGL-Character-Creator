using System.Runtime.InteropServices;

namespace SWGLCharacterCreator;

/// <summary>Native Windows "Open file" dialog (comdlg32), run on its own STA thread.</summary>
public static class FilePicker
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner, hInstance;
        public string lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter, nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public string? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir, lpstrTitle;
        public int Flags;
        public short nFileOffset, nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData, lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved, FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW")]
    private static extern bool GetOpenFileName(ref OpenFileName ofn);

    private const int OFN_FILEMUSTEXIST = 0x1000, OFN_PATHMUSTEXIST = 0x800, OFN_NOCHANGEDIR = 0x8;

    /// <summary>Shows the dialog on a background thread and calls <paramref name="done"/> with the chosen path (or null).</summary>
    public static void PickImage(Action<string?> done)
    {
        var t = new Thread(() => done(Show()));
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }

    private static string? Show()
    {
        const int size = 1024;
        var buffer = Marshal.AllocHGlobal(size * 2);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                lpstrFilter = "Images (*.png;*.tga)\0*.png;*.tga\0All files\0*.*\0\0",
                nFilterIndex = 1,
                lpstrFile = buffer,
                nMaxFile = size,
                lpstrTitle = "Choose an image (png or tga)",
                Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
            };
            return GetOpenFileName(ref ofn) ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
