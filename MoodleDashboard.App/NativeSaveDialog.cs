using System.Runtime.InteropServices;

namespace MoodleDashboard.App;

/// <summary>
/// Classic Windows "Save as" dialog. Unlike WinRT's FileSavePicker it can start in an arbitrary folder.
/// </summary>
public static class NativeSaveDialog
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OPENFILENAME
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

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetSaveFileName(ref OPENFILENAME ofn);

    const int OFN_OVERWRITEPROMPT = 0x2, OFN_NOCHANGEDIR = 0x8, OFN_PATHMUSTEXIST = 0x800, OFN_EXPLORER = 0x80000;

    /// <returns>The chosen path, or null if cancelled.</returns>
    public static string? Show(IntPtr owner, string? initialDir, string suggestedName, string filter = "Word document (*.docx)\0*.docx\0", string defExt = "docx")
    {
        const int Max = 1024;
        var buffer = Marshal.AllocHGlobal(Max * 2);
        try
        {
            var name = suggestedName.Length >= Max ? suggestedName[..(Max - 1)] : suggestedName;
            var chars = (name + "\0").ToCharArray();
            Marshal.Copy(chars, 0, buffer, chars.Length);
            // zero the rest
            for (var i = chars.Length; i < Max; i++) Marshal.WriteInt16(buffer, i * 2, 0);

            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(), hwndOwner = owner, lpstrFilter = filter + "\0",
                lpstrFile = buffer, nMaxFile = Max, lpstrInitialDir = initialDir, lpstrDefExt = defExt,
                Flags = OFN_OVERWRITEPROMPT | OFN_NOCHANGEDIR | OFN_PATHMUSTEXIST | OFN_EXPLORER, nFilterIndex = 1,
            };
            return GetSaveFileName(ref ofn) ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
