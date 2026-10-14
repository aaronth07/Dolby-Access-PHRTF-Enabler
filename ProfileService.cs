using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace AtmosProfile;

public sealed record AudioDevice(string Id, string WinRtId, string Name) { public override string ToString() => Name; }
public sealed record ProfileStatus(bool Atmos, bool Personalized, bool CanRestore, string Message);
public sealed class BackupRecord
{
    public string Endpoint { get; set; } = "";
    public string Original { get; set; } = "";
    public string OriginalHash { get; set; } = "";
    public string AppliedHash { get; set; } = "";
}

public static class ProfileService
{
#if TESTING
    internal static Action? AfterWriteTest;
#endif
    public const string Context = "45da5c30-2837-4ac4-b1e2-50acc3865974";
    public const string Property = "{1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8},1";
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AtmosProfile");
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref ProfileControlVariant value);
    static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    static ProfileControlKey Key => new("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8", 1);
    static string EndpointGuid(AudioDevice device) => Guid.Parse(device.Id[(device.Id.LastIndexOf('.') + 1)..]).ToString("B");
    static string RegistryPath(AudioDevice device) => @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\" + EndpointGuid(device) + @"\FxProperties\{" + Context + @"}\User";
    static string StatePath(AudioDevice device) => Path.Combine(DataRoot, EndpointGuid(device), "original.json");

    public static async Task<List<AudioDevice>> Devices()
    {
        var list = await DeviceInformation.FindAllAsync(DeviceClass.AudioRender);
        return list.Select(d => (d, m: Regex.Match(d.Id, @"\{0\.0\.0\.[^}]+\}\.\{[0-9a-fA-F-]+\}")))
            .Where(x => x.m.Success).Select(x => new AudioDevice(x.m.Value, x.d.Id, x.d.Name)).OrderBy(d => d.Name).ToList();
    }

    sealed class Store : IDisposable
    {
        readonly IProfileControlEnumerator enumerator;
        IProfileControlDevice? device;
        IProfileControlEffects? effects;
        public IProfileControlStore? Value;
        public Store(AudioDevice selected, bool writable)
        {
            enumerator = (IProfileControlEnumerator)new ProfileControlEnumerator();
            try
            {
                Check(enumerator.GetDevice(selected.Id, out device));
                var p = new ProfileControlVariant { type = 72, pointer = Marshal.AllocCoTaskMem(16) };
                Marshal.Copy(new Guid(Context).ToByteArray(), 0, p.pointer, 16);
                IntPtr native = IntPtr.Zero;
                try { var iid = new Guid("302ae7f9-d7e0-43e4-971b-1f8293613d2a"); Check(device.Activate(ref iid, 1, ref p, out native)); effects = (IProfileControlEffects)Marshal.GetObjectForIUnknown(native); }
                finally { PropVariantClear(ref p); if (native != IntPtr.Zero) Marshal.Release(native); }
                Check(effects.OpenUserPropertyStore(writable ? 2u : 0u, out Value));
                var alt = new ProfileControlKey("70b95986-9ee3-4b43-acb2-de7aeb1a9509", 1);
                Check(Value.GetValue(ref alt, out var v));
                try { if (v.type != 0) throw new InvalidOperationException("This device uses an unsupported Dolby profile branch."); }
                finally { PropVariantClear(ref v); }
            }
            catch { Dispose(); throw; }
        }
        public byte[] Read()
        {
            var key = Key; Check(Value!.GetValue(ref key, out var v));
            try { if (v.type != 65 || v.length == 0 || v.length > 131072 || v.bytes == IntPtr.Zero) throw new InvalidOperationException("No supported Dolby profile property on this device."); var result = new byte[v.length]; Marshal.Copy(v.bytes, result, 0, result.Length); return result; }
            finally { PropVariantClear(ref v); }
        }
        public void Write(byte[] data)
        {
            var key = Key; var v = new ProfileControlVariant { type = 65, length = (uint)data.Length, bytes = Marshal.AllocCoTaskMem(data.Length) };
            try { Marshal.Copy(data, 0, v.bytes, data.Length); Check(Value!.SetValue(ref key, ref v)); Check(Value.Commit()); }
            finally { PropVariantClear(ref v); }
        }
        public void Dispose() { if (Value != null) Marshal.ReleaseComObject(Value); if (effects != null) Marshal.ReleaseComObject(effects); if (device != null) Marshal.ReleaseComObject(device); Marshal.ReleaseComObject(enumerator); }
    }

    static byte[] RegistryBytes(AudioDevice device)
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath(device));
        return key?.GetValue(Property) as byte[] ?? throw new InvalidOperationException("Dolby profile storage is missing.");
    }
    static Dictionary<string, string> Snapshot(AudioDevice device)
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath(device)) ?? throw new InvalidOperationException("Dolby context missing.");
        return key.GetValueNames().ToDictionary(n => n, n => key.GetValueKind(n) + ":" + Hash(key.GetValue(n) is byte[] b ? b : System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(key.GetValue(n)))));
    }
    static void VerifyRegistry(AudioDevice device, byte[] expected)
    {
        var bytes = RegistryBytes(device);
        if (bytes.Length != expected.Length + 8 || !bytes.AsSpan(0, 8).SequenceEqual(Convert.FromHexString("4100000001000000")) || !bytes.AsSpan(8).SequenceEqual(expected)) throw new InvalidOperationException("Persisted profile differs from API readback.");
    }
    static BackupRecord? LoadState(AudioDevice device)
    {
        var path = StatePath(device); if (!File.Exists(path)) return null;
        var state = JsonSerializer.Deserialize<BackupRecord>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid backup.");
        if (state.Endpoint != device.Id || Hash(Convert.FromBase64String(state.Original)) != state.OriginalHash) throw new InvalidDataException("Backup identity or checksum mismatch.");
        return state;
    }
    static void SaveState(AudioDevice device, BackupRecord state)
    {
        var path = StatePath(device); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state)); File.Move(temporary, path, true);
        _ = LoadState(device);
    }
    static BackupRecord EnsureOriginal(AudioDevice device, byte[] current)
    {
        var state = LoadState(device); if (state != null) return state;
        byte[] original = current;
        state = new() { Endpoint = device.Id, Original = Convert.ToBase64String(original), OriginalHash = Hash(original), AppliedHash = "" };
        SaveState(device, state); return state;
    }
    public static ProfileStatus Inspect(AudioDevice device)
    {
        var spatial = SpatialAudioDeviceConfiguration.GetForDeviceId(device.WinRtId);
        bool atmos = spatial.ActiveSpatialAudioFormat.Equals(SpatialAudioFormatSubtype.DolbyAtmosForHeadphones, StringComparison.OrdinalIgnoreCase)
            && spatial.DefaultSpatialAudioFormat.Equals(SpatialAudioFormatSubtype.DolbyAtmosForHeadphones, StringComparison.OrdinalIgnoreCase);
        using var store = new Store(device, false); var current = store.Read(); VerifyRegistry(device, current);
        var state = LoadState(device); string hash = Hash(current);
        bool personalized = (state != null && hash == state.AppliedHash && hash != state.OriginalHash);
        bool restore = state != null;
        return new(atmos, personalized, restore, atmos ? "Dolby Atmos for Headphones active" : "Enable Dolby Atmos for Headphones for this device in Windows sound settings.");
    }
    public static string Backup(AudioDevice device)
    {
        using var mutex = Acquire(); using var store = new Store(device, false); var current = store.Read(); VerifyRegistry(device, current);
        _ = EnsureOriginal(device, current); return SaveBackup(device, current);
    }
    static MutexLease Acquire() => new();
    sealed class MutexLease : IDisposable
    {
        readonly Mutex value = new(false, "Local\\DolbyPersonalizedProfileStereoConvolver");
        public MutexLease() { bool acquired; try { acquired = value.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; } if (!acquired) { value.Dispose(); throw new InvalidOperationException("Another profile operation is running."); } }
        public void Dispose() { value.ReleaseMutex(); value.Dispose(); }
    }
    static string SaveBackup(AudioDevice device, byte[] current)
    {
        string folder = Path.Combine(DataRoot, EndpointGuid(device), "backups", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder, "profile_api.bin"), current);
        File.WriteAllBytes(Path.Combine(folder, "profile_registry.bin"), RegistryBytes(device));
        File.WriteAllText(Path.Combine(folder, "context.json"), JsonSerializer.Serialize(Snapshot(device)));
        File.WriteAllText(Path.Combine(folder, "endpoint.txt"), device.Id);
        if (!File.ReadAllBytes(Path.Combine(folder, "profile_api.bin")).SequenceEqual(current)) throw new IOException("Backup verification failed.");
        return folder;
    }
    static string VerifyTool()
    {
        string tool = Path.Combine(AppContext.BaseDirectory, "Tools", "SoundVolumeView.exe");
        if (Hash(File.ReadAllBytes(tool)) != "b5af5bd60f7a29af8cb4d8a566382b90f0fe07cac97228d218cb913f3382d647") throw new InvalidDataException("Spatial sound helper integrity check failed.");
        return tool;
    }
    static void SetSpatial(AudioDevice device, bool enabled)
    {
        string tool = VerifyTool();
        var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(tool)! };
        start.ArgumentList.Add("/SetSpatial"); start.ArgumentList.Add(device.Id); start.ArgumentList.Add(enabled ? SpatialAudioFormatSubtype.DolbyAtmosForHeadphones : "");
        using var process = Process.Start(start) ?? throw new IOException("Could not start spatial sound helper.");
        if (!process.WaitForExit(15000)) { process.Kill(); throw new TimeoutException("Spatial sound operation timed out."); }
        if (process.ExitCode != 0) throw new IOException("Spatial sound helper failed.");
        for (int i = 0; i < 40; i++)
        {
            var s = SpatialAudioDeviceConfiguration.GetForDeviceId(device.WinRtId);
            string expected = enabled ? SpatialAudioFormatSubtype.DolbyAtmosForHeadphones : "{00000000-0000-0000-0000-000000000000}";
            if (s.ActiveSpatialAudioFormat.Equals(expected, StringComparison.OrdinalIgnoreCase) && s.DefaultSpatialAudioFormat.Equals(expected, StringComparison.OrdinalIgnoreCase)) return;
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("Spatial sound readback failed.");
    }
    public static string Switch(AudioDevice device, ProfileFile? profile, bool enabled)
    {
        using var mutex = Acquire();
        if (Process.GetProcessesByName("DolbyAccess").Length != 0) throw new InvalidOperationException("Close Dolby Access before switching the profile.");
        var status = Inspect(device); if (!status.Atmos) throw new InvalidOperationException(status.Message);
        _ = VerifyTool();
        using var store = new Store(device, true); var before = store.Read(); var snapshot = Snapshot(device);
        var state = EnsureOriginal(device, before); byte[] original = Convert.FromBase64String(state.Original);
        string hash = Hash(before);
        if (hash != state.OriginalHash && hash != state.AppliedHash) throw new InvalidOperationException("The profile changed outside this application. The saved original has been preserved.");
        var desired = enabled ? (profile ?? throw new InvalidOperationException("Select a valid personalized headphone profile.")).ApplyTo(original) : original;
        var backup = SaveBackup(device, before); bool attempted = false;
        try
        {
            SetSpatial(device, false); attempted = true; store.Write(desired);
#if TESTING
            AfterWriteTest?.Invoke();
#endif
            if (!store.Read().SequenceEqual(desired)) throw new InvalidOperationException("Profile API readback failed.");
            VerifyRegistry(device, desired);
            var after = Snapshot(device);
            if (snapshot.Count != after.Count || snapshot.Any(p => p.Key != Property && (!after.TryGetValue(p.Key, out var v) || v != p.Value))) throw new InvalidOperationException("Another Dolby property changed.");
            SetSpatial(device, true);
            if (!store.Read().SequenceEqual(desired)) throw new InvalidOperationException("Profile did not survive renderer restart.");
            VerifyRegistry(device, desired);
            if (enabled) state.AppliedHash = Hash(desired);
            SaveState(device, state); return backup;
        }
        catch (Exception error)
        {
            try
            {
                if (attempted)
                {
                    store.Write(before);
                    if (!store.Read().SequenceEqual(before)) throw new IOException("Rollback API mismatch.");
                    VerifyRegistry(device, before);
                    var restored = Snapshot(device);
                    if (restored.Count != snapshot.Count || snapshot.Any(p => !restored.TryGetValue(p.Key, out var v) || v != p.Value)) throw new IOException("Dolby context changed during rollback.");
                }
                SetSpatial(device, true);
            }
            catch (Exception restore) { throw new AggregateException("Profile switch and restoration failed. Backup: " + backup, error, restore); }
            throw;
        }
    }
}
