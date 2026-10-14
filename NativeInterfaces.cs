using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class ProfileControlEnumerator {}
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IProfileControlEnumerator {
 [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr collection);
 [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IProfileControlDevice device);
 [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IProfileControlDevice device);
}
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IProfileControlDevice {
 [PreserveSig] int Activate(ref Guid iid, uint context, ref ProfileControlVariant parameters, out IntPtr result);
 [PreserveSig] int OpenPropertyStore(uint mode, out IProfileControlStore store);
 [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
 [PreserveSig] int GetState(out uint state);
}
[ComImport, Guid("302AE7F9-D7E0-43E4-971B-1F8293613D2A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IProfileControlEffects {
 [PreserveSig] int OpenDefaultPropertyStore(uint mode, out IProfileControlStore store);
 [PreserveSig] int OpenUserPropertyStore(uint mode, out IProfileControlStore store);
}
[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IProfileControlStore {
 [PreserveSig] int GetCount(out uint count);
 [PreserveSig] int GetAt(uint index, out ProfileControlKey key);
 [PreserveSig] int GetValue(ref ProfileControlKey key, out ProfileControlVariant value);
 [PreserveSig] int SetValue(ref ProfileControlKey key, ref ProfileControlVariant value);
 [PreserveSig] int Commit();
}
[StructLayout(LayoutKind.Sequential)]
struct ProfileControlKey { public Guid format; public uint id; public ProfileControlKey(string guid, uint pid) {format=new Guid(guid);id=pid;} }
[StructLayout(LayoutKind.Explicit, Size=24)]
struct ProfileControlVariant {
 [FieldOffset(0)] public ushort type;
 [FieldOffset(8)] public IntPtr pointer;
 [FieldOffset(8)] public uint length;
 [FieldOffset(16)] public IntPtr bytes;
}
