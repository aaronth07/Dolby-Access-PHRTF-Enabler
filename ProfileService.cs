using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;



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

public class ProfileOperationException(string message) : InvalidOperationException(message);
public sealed class ProfileWarningException(string message) : ProfileOperationException(message);

public static class ProfileService
{
#if TESTING
    internal static Action? AfterWriteTest;
#endif
    public const string Context = "45da5c30-2837-4ac4-b1e2-50acc3865974";
    public const string Property = "{1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8},1";
    #if TESTING
    internal static string? TestDataRoot;
#endif
    public static string DataRoot =>
#if TESTING
        TestDataRoot ??
#endif
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AtmosProfile");
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref ProfileControlVariant value);
    static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    static ProfileControlKey Key => new("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8", 1);
    static string EndpointGuid(AudioDevice device) => Guid.Parse(device.Id[(device.Id.LastIndexOf('.') + 1)..]).ToString("B");
    static string RegistryPath(AudioDevice device) => @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\" + EndpointGuid(device) + @"\FxProperties\{" + Context + @"}\User";
    static string StatePath(AudioDevice device) => Path.Combine(DataRoot, EndpointGuid(device), "original.json");

    public static AudioDevice DefaultDevice { get; } = new("Default", "", "Default playback device");
    public static AudioDevice Resolve(AudioDevice device) => device.Id == "Default" ? AudioPlatform.Default() : device;
    public static Task<List<AudioDevice>> Devices() => Task.FromResult(AudioPlatform.Devices().OrderBy(d => d.Name).ToList());
    public static string ToolRoot { get; set; } = AppContext.BaseDirectory;

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
                try { if (v.type != 0) throw new ProfileOperationException("The Dolby profile cannot be verified on this device."); }
                finally { PropVariantClear(ref v); }
            }
            catch { Dispose(); throw; }
        }
        public byte[] Read()
        {
            var key = Key; Check(Value!.GetValue(ref key, out var v));
            try { if (v.type != 65 || v.length == 0 || v.length > 131072 || v.bytes == IntPtr.Zero) throw new ProfileOperationException("The Dolby profile cannot be verified on this device."); var result = new byte[v.length]; Marshal.Copy(v.bytes, result, 0, result.Length); return result; }
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
        return key?.GetValue(Property) as byte[] ?? throw new ProfileOperationException("The Dolby profile could not be found.");
    }
    static Dictionary<string, string> Snapshot(AudioDevice device)
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath(device)) ?? throw new ProfileOperationException("The Dolby profile could not be found.");
        return key.GetValueNames().ToDictionary(n => n, n => key.GetValueKind(n) + ":" + Hash(key.GetValue(n) is byte[] b ? b : System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(key.GetValue(n)))));
    }
    static void VerifyRegistry(AudioDevice device, byte[] expected)
    {
        var bytes = RegistryBytes(device);
        if (bytes.Length != expected.Length + 8 || !bytes.AsSpan(0, 8).SequenceEqual(Convert.FromHexString("4100000001000000")) || !bytes.AsSpan(8).SequenceEqual(expected)) throw new ProfileOperationException("The saved profile could not be verified.");
    }
    static BackupRecord? LoadState(AudioDevice device)
    {
        var path = StatePath(device); if (!File.Exists(path)) return null;
        var state = JsonSerializer.Deserialize<BackupRecord>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid backup.");
        if (state.Endpoint != device.Id || Hash(Convert.FromBase64String(state.Original)) != state.OriginalHash) throw new InvalidDataException("Backup identity or checksum mismatch.");
        return state;
    }
    static BackupRecord? LoadStateForRestore(AudioDevice device)
    {
        try { return LoadState(device); }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or FormatException) { return null; }
    }
    static BackupRecord? RecoverState(AudioDevice device)
    {
        var root = Path.Combine(DataRoot, EndpointGuid(device), "backups");
        if (!Directory.Exists(root)) return null;
        foreach (var folder in Directory.GetDirectories(root).OrderBy(p => p, StringComparer.Ordinal)) {
            try {
                if (File.ReadAllText(Path.Combine(folder, "endpoint.txt")) != device.Id) continue;
                var api = File.ReadAllBytes(Path.Combine(folder, "profile_api.bin"));
                var registry = File.ReadAllBytes(Path.Combine(folder, "profile_registry.bin"));
                if (registry.Length != api.Length + 8 || !registry.AsSpan(0, 8).SequenceEqual(Convert.FromHexString("4100000001000000")) || !registry.AsSpan(8).SequenceEqual(api)) continue;
                _ = ProfileFile.HasPersonalization(api);
                return new() { Endpoint = device.Id, Original = Convert.ToBase64String(api), OriginalHash = Hash(api) };
            } catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
        return null;
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
        device = Resolve(device);
        var spatial = AudioPlatform.Spatial(device);
        bool atmos = spatial.ActiveSpatialAudioFormat.Equals(AudioPlatform.Dolby, StringComparison.OrdinalIgnoreCase)
            && spatial.DefaultSpatialAudioFormat.Equals(AudioPlatform.Dolby, StringComparison.OrdinalIgnoreCase);
        using var store = new Store(device, false); var current = store.Read(); VerifyRegistry(device, current);
        BackupRecord? state = null; string? warning = null;
        try { state = LoadState(device); } catch (Exception) { warning = "Saved configuration could not be verified."; }
        string hash = Hash(current);
        bool personalized = state != null && hash == state.OriginalHash ? false : ProfileFile.HasPersonalization(current);
        bool restore = state != null;
        return new(atmos, personalized, restore, warning ?? (atmos ? "Dolby Atmos for Headphones active" : "Enable Dolby Atmos for Headphones for this device in Windows sound settings."));
    }
    public static string Backup(AudioDevice device)
    {
        device = Resolve(device);
        using var mutex = Acquire(); using var store = new Store(device, false); var current = store.Read(); VerifyRegistry(device, current);
        _ = EnsureOriginal(device, current); return SaveBackup(device, current);
    }
    public static string Reset(AudioDevice device)
    {
        device = Resolve(device);
        using var mutex = Acquire();
        _ = VerifyTool();
        using (var store = new Store(device, false)) { var current = store.Read(); VerifyRegistry(device, current); }
        // Restart first. Existing backups remain available if the restart fails.
        try { SetSpatial(device, false); SetSpatial(device, true); }
        catch { SetSpatial(device, true); throw; }
        using var freshStore = new Store(device, false);
        var fresh = freshStore.Read(); VerifyRegistry(device, fresh);
        var folder = Path.GetDirectoryName(StatePath(device))!;
        var previous = folder + ".reset." + Guid.NewGuid().ToString("N");
        bool existed = Directory.Exists(folder);
        if (existed) Directory.Move(folder, previous);
        try {
            EnsureOriginal(device, fresh);
            var backup = SaveBackup(device, fresh);
            if (existed) Directory.Delete(previous, true);
            return backup;
        } catch {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            if (existed) Directory.Move(previous, folder);
            throw;
        }
    }
    static MutexLease Acquire() => new();
    sealed class MutexLease : IDisposable
    {
        readonly Mutex value = new(false, "Local\\DolbyPersonalizedProfileOperation");
        public MutexLease() { bool acquired; try { acquired = value.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; } if (!acquired) { value.Dispose(); throw new ProfileOperationException("Another profile operation is running."); } }
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
        string tool = Path.Combine(ToolRoot, "Tools", "SoundVolumeView.exe");
        if (Hash(File.ReadAllBytes(tool)) != "b5af5bd60f7a29af8cb4d8a566382b90f0fe07cac97228d218cb913f3382d647") throw new InvalidDataException("Spatial sound helper integrity check failed.");
        return tool;
    }
    static void SetSpatial(AudioDevice device, bool enabled)
    {
        string tool = VerifyTool();
        var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(tool)! };
        start.ArgumentList.Add("/SetSpatial"); start.ArgumentList.Add(device.Id); start.ArgumentList.Add(enabled ? AudioPlatform.Dolby : "");
        using var process = Process.Start(start) ?? throw new IOException("Could not start spatial sound helper.");
        if (!process.WaitForExit(15000)) { process.Kill(); throw new TimeoutException("Spatial sound operation timed out."); }
        if (process.ExitCode != 0) throw new IOException("Spatial sound helper failed.");
        VerifySpatial(device, enabled);
    }
    static void VerifySpatial(AudioDevice device, bool enabled)
    {
        for (int i = 0; i < 40; i++)
        {
            var s = AudioPlatform.Spatial(device);
            string expected = enabled ? AudioPlatform.Dolby : "{00000000-0000-0000-0000-000000000000}";
            if (s.ActiveSpatialAudioFormat.Equals(expected, StringComparison.OrdinalIgnoreCase) && s.DefaultSpatialAudioFormat.Equals(expected, StringComparison.OrdinalIgnoreCase)) return;
            Thread.Sleep(100);
        }
        throw new ProfileOperationException("Atmos could not be verified.");
    }
    public static string Switch(AudioDevice device, ProfileFile? profile, bool enabled, Action<bool>? manualRestart = null, bool force = false)
    {
        device = Resolve(device);
        using var mutex = Acquire();
        if (!force && Process.GetProcessesByName("DolbyAccess").Length != 0) throw new ProfileWarningException("Close Dolby Access before switching the profile.");
        var status = Inspect(device); if (!status.Atmos && !force) throw new ProfileWarningException(status.Message);
        if (manualRestart == null) _ = VerifyTool();
        void Restart(bool on) { if (manualRestart == null) SetSpatial(device, on); else { manualRestart(on); VerifySpatial(device, on); } }
        using var store = new Store(device, true); var before = store.Read(); var snapshot = Snapshot(device);
        if (!enabled && LoadStateForRestore(device) == null) {
            if (!force) throw new ProfileWarningException("Saved configuration could not be verified. Allow to restore the earliest verified backup.");
            var recovered = RecoverState(device) ?? throw new ProfileOperationException("No backup could be verified. Reset configuration.");
            SaveState(device, recovered);
        }
        BackupRecord state;
        try { state = EnsureOriginal(device, before); }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or FormatException) {
            if (!force) throw new ProfileWarningException("Saved configuration could not be verified. Allow to save a new baseline.");
            SaveBackup(device, before);
            var oldPath = StatePath(device); File.Move(oldPath, oldPath + ".invalid." + Guid.NewGuid().ToString("N"));
            state = EnsureOriginal(device, before);
        }
        byte[] original = Convert.FromBase64String(state.Original);
        string hash = Hash(before);
        if (!force && hash != state.OriginalHash && hash != state.AppliedHash) throw new ProfileWarningException("The device profile changed outside this application.");
        var desired = enabled ? (profile ?? throw new ProfileOperationException("Select a valid personalized headphone profile.")).ApplyTo(original) : original;
        var backup = SaveBackup(device, before); bool attempted = false;
        try
        {
            Restart(false); attempted = true; store.Write(desired);
#if TESTING
            AfterWriteTest?.Invoke();
#endif
            if (!store.Read().SequenceEqual(desired)) throw new ProfileOperationException("The profile could not be verified.");
            VerifyRegistry(device, desired);
            var after = Snapshot(device);
            if (snapshot.Count != after.Count || snapshot.Any(p => p.Key != Property && (!after.TryGetValue(p.Key, out var v) || v != p.Value))) throw new ProfileOperationException("Another Dolby property changed.");
            Restart(true);
            if (!store.Read().SequenceEqual(desired)) throw new ProfileOperationException("The profile could not be verified after restarting Atmos.");
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
                Restart(true);
            }
            catch (Exception restore) { throw new AggregateException("Profile switch and restoration failed. Backup: " + backup, error, restore); }
            throw;
        }
    }
}
