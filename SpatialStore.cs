using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace AtmosProfile;

// Shared only by the room and DTS tabs. The original PHRTF transaction is unchanged.
internal sealed class SpatialStore : IDisposable
{
    readonly IProfileControlEnumerator enumerator = (IProfileControlEnumerator)new ProfileControlEnumerator();
    IProfileControlDevice? device;
    IProfileControlEffects? effects;
    IProfileControlStore? store;
    readonly bool commit;
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref ProfileControlVariant value);
    static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    public SpatialStore(AudioDevice endpoint, string context, bool writable, bool commit)
    {
        this.commit = commit;
        try {
            Check(enumerator.GetDevice(endpoint.Id, out device));
            var parameters = new ProfileControlVariant { type = 72, pointer = Marshal.AllocCoTaskMem(16) };
            Marshal.Copy(new Guid(context).ToByteArray(), 0, parameters.pointer, 16);
            IntPtr native = IntPtr.Zero;
            try {
                var iid = new Guid("302ae7f9-d7e0-43e4-971b-1f8293613d2a");
                Check(device.Activate(ref iid, 1, ref parameters, out native));
                effects = (IProfileControlEffects)Marshal.GetObjectForIUnknown(native);
            } finally { PropVariantClear(ref parameters); if (native != IntPtr.Zero) Marshal.Release(native); }
            Check(effects.OpenUserPropertyStore(writable ? 2u : 0u, out store));
        } catch { Dispose(); throw; }
    }
    public (ushort Type, byte[] Data) Read(string guid, uint propertyId = 1)
    {
        var key = new ProfileControlKey(guid, propertyId); Check(store!.GetValue(ref key, out var value));
        try {
            if (value.type == 0) return (0, []);
            if (value.type == 19) return (19, BitConverter.GetBytes(value.length));
            if (value.type != 65 || value.length == 0 || value.length > 16 * 1024 * 1024 || value.bytes == IntPtr.Zero)
                throw new ProfileOperationException("The device profile cannot be verified.");
            byte[] bytes = new byte[value.length]; Marshal.Copy(value.bytes, bytes, 0, bytes.Length); return (65, bytes);
        } finally { PropVariantClear(ref value); }
    }
    public byte[] Blob(string guid) { var v = Read(guid); if (v.Type != 65) throw new ProfileOperationException("The device profile could not be found."); return v.Data; }
    public uint Counter(string guid) { var v = Read(guid); if (v.Type != 19) throw new ProfileOperationException("DTS could not be verified."); return BitConverter.ToUInt32(v.Data); }
    public void Write(string guid, byte[] bytes, uint propertyId = 1)
    {
        var key = new ProfileControlKey(guid, propertyId);
        var value = new ProfileControlVariant { type = 65, length = (uint)bytes.Length, bytes = Marshal.AllocCoTaskMem(bytes.Length) };
        try { Marshal.Copy(bytes, 0, value.bytes, bytes.Length); Check(store!.SetValue(ref key, ref value)); if (commit) Check(store.Commit()); }
        finally { PropVariantClear(ref value); }
    }
    public void Clear(string guid, uint propertyId = 1)
    {
        var key = new ProfileControlKey(guid, propertyId); var value = new ProfileControlVariant();
        Check(store!.SetValue(ref key, ref value)); if (commit) Check(store.Commit());
    }
    public void Write(string guid, uint number)
    {
        var key = new ProfileControlKey(guid, 1); var value = new ProfileControlVariant { type = 19, length = number };
        Check(store!.SetValue(ref key, ref value));
    }
    public void Dispose() { if (store != null) Marshal.ReleaseComObject(store); if (effects != null) Marshal.ReleaseComObject(effects); if (device != null) Marshal.ReleaseComObject(device); Marshal.ReleaseComObject(enumerator); }
}

internal sealed class SpatialOperation : IDisposable
{
    readonly Mutex mutex = new(false, "Local\\DolbyPersonalizedProfileOperation");
    public SpatialOperation() { bool acquired; try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; } if (!acquired) { mutex.Dispose(); throw new ProfileOperationException("Another profile operation is running."); } }
    public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
}

internal static class SpatialFiles
{
    internal static string? TestRoot { get; set; }
    public static string Root => TestRoot ?? ProfileService.DataRoot;
    public static string Folder(AudioDevice d, string feature) => Path.Combine(Root, Guid.Parse(d.Id[(d.Id.LastIndexOf('.') + 1)..]).ToString("B"), feature);
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var text = JsonSerializer.Serialize(value); File.WriteAllText(temporary, text);
        if (File.ReadAllText(temporary) != text) throw new IOException("The backup could not be verified.");
        File.Move(temporary, path, true);
    }
    public static Dictionary<string, string> Snapshot(AudioDevice d, string context)
    {
        var guid = Guid.Parse(d.Id[(d.Id.LastIndexOf('.') + 1)..]).ToString("B");
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\" + guid + @"\FxProperties\{" + context + @"}\User")
            ?? throw new ProfileOperationException("The device profile could not be found.");
        return key.GetValueNames().ToDictionary(n => n, n => key.GetValueKind(n) + ":" + ProfileService.Hash(key.GetValue(n) is byte[] b ? b : System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(key.GetValue(n)))));
    }
    public static void VerifyUnchanged(Dictionary<string, string> before, Dictionary<string, string> after, params string[] allowed)
    {
        bool Excluded(string key) => allowed.Contains(key, StringComparer.OrdinalIgnoreCase);
        if (before.Count(p => !Excluded(p.Key)) != after.Count(p => !Excluded(p.Key)) || before.Any(p => !Excluded(p.Key) && (!after.TryGetValue(p.Key, out var value) || p.Value != value)))
            throw new ProfileOperationException("Other device settings changed.");
    }
    public static void VerifyPersisted(AudioDevice d, string context, string guid, byte[] expected, uint propertyId = 1)
    {
        var endpoint = Guid.Parse(d.Id[(d.Id.LastIndexOf('.') + 1)..]).ToString("B");
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\" + endpoint + @"\FxProperties\{" + context + @"}\User");
        var value = key?.GetValue("{" + guid + "}," + propertyId) as byte[];
        if (value == null || value.Length != expected.Length + 8 || !value.AsSpan(0, 8).SequenceEqual(Convert.FromHexString("4100000001000000")) || !value.AsSpan(8).SequenceEqual(expected))
            throw new ProfileOperationException("The saved profile could not be verified.");
    }
    public static void RequireProvider(AudioDevice d, string guid, string name)
    {
        var s = AudioPlatform.Spatial(d);
        if (!s.ActiveSpatialAudioFormat.Equals(guid, StringComparison.OrdinalIgnoreCase) || !s.DefaultSpatialAudioFormat.Equals(guid, StringComparison.OrdinalIgnoreCase))
            throw new ProfileOperationException("Enable " + name + " for this device.");
    }
    internal static bool CounterMatches(object? value, RegistryValueKind kind, uint expected) =>
        kind == RegistryValueKind.DWord && value is int number && unchecked((uint)number) == expected;
    public static void VerifyPersistedCounter(AudioDevice d, string context, string guid, uint expected)
    {
        var endpoint = Guid.Parse(d.Id[(d.Id.LastIndexOf('.') + 1)..]).ToString("B");
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\" + endpoint + @"\FxProperties\{" + context + @"}\User");
        string name = "{" + guid + "},1";
        var value = key?.GetValue(name);
        if (value == null || !CounterMatches(value, key!.GetValueKind(name), expected))
            throw new ProfileOperationException("The saved DTS change counter could not be verified.");
    }
}
