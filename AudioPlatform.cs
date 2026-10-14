using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace AtmosProfile;

public static class AudioPlatform
{
    public const string Dolby = "{1459AC38-3875-49BF-BB59-0FE80F4D395D}";
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Collection { [PreserveSig] int GetCount(out uint count); [PreserveSig] int Item(uint index, out IProfileControlDevice device); }
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref ProfileControlVariant value);
    static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    static AudioDevice Describe(IProfileControlDevice device)
    {
        Check(device.GetId(out var id)); Check(device.OpenPropertyStore(0, out var store));
        try
        {
            var key = new ProfileControlKey("a45c254e-df1c-4efd-8020-67d146a850e0", 14); Check(store.GetValue(ref key, out var v));
            try { return new(id, @"\\?\SWD#MMDEVAPI#" + id + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}", v.type == 31 ? Marshal.PtrToStringUni(v.pointer) ?? id : id); }
            finally { PropVariantClear(ref v); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }
    public static List<AudioDevice> Devices()
    {
        var e = (IProfileControlEnumerator)new ProfileControlEnumerator(); IntPtr pointer = IntPtr.Zero; Collection? list = null;
        try
        {
            Check(e.EnumAudioEndpoints(0, 1, out pointer)); list = (Collection)Marshal.GetObjectForIUnknown(pointer); Check(list.GetCount(out uint count)); var result = new List<AudioDevice>();
            for (uint i = 0; i < count; i++) { Check(list.Item(i, out var d)); try { result.Add(Describe(d)); } finally { Marshal.ReleaseComObject(d); } }
            return result;
        }
        finally { if (list != null) Marshal.ReleaseComObject(list); if (pointer != IntPtr.Zero) Marshal.Release(pointer); Marshal.ReleaseComObject(e); }
    }
    public static AudioDevice Default()
    {
        var e = (IProfileControlEnumerator)new ProfileControlEnumerator();
        try { Check(e.GetDefaultAudioEndpoint(0, 1, out var d)); try { return Describe(d); } finally { Marshal.ReleaseComObject(d); } }
        finally { Marshal.ReleaseComObject(e); }
    }
    [DllImport("combase.dll")] static extern int RoInitialize(uint mode);
    [DllImport("combase.dll")] static extern void RoUninitialize();
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string value, uint count, out IntPtr text);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr text);
    [DllImport("combase.dll")] static extern IntPtr WindowsGetStringRawBuffer(IntPtr text, out uint count);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr type, ref Guid iid, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DeviceFactory(IntPtr self, IntPtr device, out IntPtr configuration);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Getter(IntPtr self, out IntPtr text);
    static T Slot<T>(IntPtr p, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(p), slot * IntPtr.Size));
    static string Get(IntPtr p, int slot) { Check(Slot<Getter>(p, slot)(p, out var text)); try { var buffer = WindowsGetStringRawBuffer(text, out uint count); return Marshal.PtrToStringUni(buffer, (int)count) ?? ""; } finally { WindowsDeleteString(text); } }
    public sealed record SpatialState(string ActiveSpatialAudioFormat, string DefaultSpatialAudioFormat);
    public static SpatialState Spatial(AudioDevice device)
    {
        int hr = RoInitialize(1); if (hr < 0 && hr != unchecked((int)0x80010106)) Check(hr);
        IntPtr name = IntPtr.Zero, id = IntPtr.Zero, factory = IntPtr.Zero, config = IntPtr.Zero;
        try
        {
            const string type = "Windows.Media.Audio.SpatialAudioDeviceConfiguration";
            Check(WindowsCreateString(type, (uint)type.Length, out name)); var iid = new Guid("3ec37f7b-936d-4e04-9728-2827d9f758c4"); Check(RoGetActivationFactory(name, ref iid, out factory));
            Check(WindowsCreateString(device.WinRtId, (uint)device.WinRtId.Length, out id)); Check(Slot<DeviceFactory>(factory, 6)(factory, id, out config));
            return new(Get(config, 9), Get(config, 10));
        }
        finally { if (config != IntPtr.Zero) Marshal.Release(config); if (factory != IntPtr.Zero) Marshal.Release(factory); if (name != IntPtr.Zero) WindowsDeleteString(name); if (id != IntPtr.Zero) WindowsDeleteString(id); if (hr >= 0) RoUninitialize(); }
    }
}

