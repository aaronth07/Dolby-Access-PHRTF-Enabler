using System.Runtime.InteropServices;
namespace AtmosProfile;
internal static class FeatureFilePicker
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    sealed class OpenFileName
    {
        public int Size = Marshal.SizeOf<OpenFileName>();
        public IntPtr Owner, Instance;
        public string Filter = "Personalized headphone profiles (*.personalized_headphone)\0*.personalized_headphone\0All files (*.*)\0*.*\0\0";
        public IntPtr CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex = 1;
        public IntPtr File;
        public int MaxFile = 32768;
        public IntPtr FileTitle;
        public int MaxFileTitle;
        public IntPtr InitialDirectory;
        public string Title = "Select personalized headphone profile";
        public int Flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008;
        public short FileOffset, FileExtension;
        public IntPtr DefaultExtension, CustomData, Hook, Template, Reserved;
        public int ReservedValue, FlagsEx;
    }
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetOpenFileNameW([In, Out] OpenFileName value);
    [DllImport("comdlg32.dll")] static extern int CommDlgExtendedError();
    public static string? Pick(IntPtr owner, string extension, string title)
    {
        var dialog = new OpenFileName { Owner = owner, Filter = title + "\0*" + extension + "\0All files (*.*)\0*.*\0\0", Title = title };
        dialog.File = Marshal.AllocHGlobal(dialog.MaxFile * 2);
        Marshal.WriteInt16(dialog.File, 0);
        try {
            if (GetOpenFileNameW(dialog)) return Marshal.PtrToStringUni(dialog.File);
            int error = CommDlgExtendedError();
            if (error != 0) throw new ProfileOperationException("The file picker could not be opened.");
            return null;
        } finally { Marshal.FreeHGlobal(dialog.File); }
    }
}
