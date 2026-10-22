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
public sealed record ProfileStatus(bool Atmos, bool Personalized, bool CanRestore, string Message, string? Warning = null);
public sealed class BackupRecord
{
    public string Endpoint { get; set; } = "";
    public string Original { get; set; } = "";
    public string OriginalHash { get; set; } = "";
    public string AppliedHash { get; set; } = "";
    public string RestoredHash { get; set; } = "";
    public bool? Enabled { get; set; }
}

public class ProfileOperationException(string message) : InvalidOperationException(message);
public sealed class ProfileWarningException(string message) : ProfileOperationException(message);

public static class ProfileService
{
    public static Func<AudioDevice, byte[], bool, byte[]>? PersonalizationTransform { get; set; }
#if TESTING
    internal static Action? AfterWriteTest;
    internal static Action? AfterResetWriteTest;
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
    public static Task<List<AudioDevice>> Devices() => Task.Run(() => AudioPlatform.Devices().OrderBy(d => d.Name).ToList());
    public static string ToolRoot { get; set; } = AppContext.BaseDirectory;

    sealed class Store : IDisposable
    {
        readonly IProfileControlEnumerator enumerator;
        IProfileControlDevice? device;
        IProfileControlEffects? effects;
        public IProfileControlStore? Value;
        public bool AlternateConfiguration { get; private set; }
        public Store(AudioDevice selected, bool writable, bool force = false)
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
                try { AlternateConfiguration = v.type != 0; if (AlternateConfiguration && !force) throw new ProfileWarningException("Additional Dolby configuration is present. Allow to apply the selected profile while preserving those properties."); }
                finally { PropVariantClear(ref v); }
            }
            catch { Dispose(); throw; }
        }
        public byte[] Read(bool force = false)
        {
            var key = Key; Check(Value!.GetValue(ref key, out var v));
            try { if (v.type == 0) { if (force) return []; throw new ProfileWarningException("No Dolby initialization is saved on this device. Allow to initialize it from the selected profile."); } if (v.type != 65 || v.length == 0 || v.length > 16 * 1024 * 1024 || v.bytes == IntPtr.Zero) throw new ProfileOperationException("The Dolby profile cannot be verified on this device."); var result = new byte[v.length]; Marshal.Copy(v.bytes, result, 0, result.Length); return result; }
            finally { PropVariantClear(ref v); }
        }
        public void Write(byte[] data)
        {
            var key = Key; var v = data.Length == 0 ? new ProfileControlVariant() : new ProfileControlVariant { type = 65, length = (uint)data.Length, bytes = Marshal.AllocCoTaskMem(data.Length) };
            try { if (data.Length != 0) Marshal.Copy(data, 0, v.bytes, data.Length); Check(Value!.SetValue(ref key, ref v)); Check(Value.Commit()); }
            finally { PropVariantClear(ref v); }
        }
        public void Dispose() { if (Value != null) Marshal.ReleaseComObject(Value); if (effects != null) Marshal.ReleaseComObject(effects); if (device != null) Marshal.ReleaseComObject(device); Marshal.ReleaseComObject(enumerator); }
    }

#if TESTING
    internal static void WriteInitializationForTest(AudioDevice device, byte[] bytes) { using var store = new Store(device, true, true); store.Write(bytes); }
    internal static byte[] ReadInitializationForTest(AudioDevice device) { using var store = new Store(device, false, true); return store.Read(true); }
#endif
    static byte[] RegistryBytes(AudioDevice device)
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath(device));
        var value = key?.GetValue(Property);
        if (value == null) return [];
        return value as byte[] ?? throw new ProfileOperationException("The saved Dolby property has an unsupported registry type.");
    }
    static Dictionary<string, string> Snapshot(AudioDevice device)
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath(device));
        if (key == null) return new();
        return key.GetValueNames().ToDictionary(n => n, n => key.GetValueKind(n) + ":" + Hash(key.GetValue(n) is byte[] b ? b : System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(key.GetValue(n)))));
    }
    static void VerifyRegistry(AudioDevice device, byte[] expected)
    {
        var bytes = RegistryBytes(device);
        if (expected.Length == 0 && bytes.Length == 0) return;
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
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or FormatException or ArgumentNullException) { return null; }
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
                if (api.Length == 0 && registry.Length == 0) return new() { Endpoint = device.Id, Original = "", OriginalHash = Hash(api) };
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
        state = new() { Endpoint = device.Id, Original = Convert.ToBase64String(original), OriginalHash = Hash(original), AppliedHash = "", Enabled = false };
        SaveState(device, state); return state;
    }
    public static ProfileStatus Inspect(AudioDevice device)
    {
        device = Resolve(device);
        var spatial = AudioPlatform.Spatial(device);
        bool atmos = spatial.ActiveSpatialAudioFormat.Equals(AudioPlatform.Dolby, StringComparison.OrdinalIgnoreCase)
            && spatial.DefaultSpatialAudioFormat.Equals(AudioPlatform.Dolby, StringComparison.OrdinalIgnoreCase);
        using var store = new Store(device, false, true); var current = store.Read(true); VerifyRegistry(device, current);
        BackupRecord? state = null; string? warning = null;
        try { state = LoadState(device); } catch (Exception) { warning = "Saved configuration could not be verified."; }
        string hash = Hash(current);
        string? compatibility = CompatibilityWarning(current, store.AlternateConfiguration);
        bool personalized;
        try { personalized = PersonalizationEnabled(state, current); } catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { personalized = false; }
        bool restore = state != null;
        return new(atmos, personalized, restore, (atmos ? "Dolby Atmos for Headphones active" : "Enable Dolby Atmos for Headphones for this device in Windows sound settings.") + (warning != null ? ". " + warning : compatibility != null ? ". Saved Dolby configuration requires an override." : ""), compatibility);
    }
    internal static string? CompatibilityWarning(byte[] current, bool alternate = false)
    {
        if (current.Length == 0) return "No Dolby initialization is saved on this device. Allow to initialize it from the selected profile.";
        try { _ = new ProfileFile("", "1.1.0", "", []).ApplyTo(current); }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or OverflowException) {
            return "The existing Dolby initialization has an unfamiliar format. Allow to replace it with the selected profile; the original bytes will be backed up.";
        }
        return alternate ? "Additional Dolby configuration is present. Allow to apply the selected profile while preserving those properties." : null;
    }
    internal static byte[] PreparePersonalization(ProfileFile profile, byte[] original, bool force)
    {
        var warning = CompatibilityWarning(original);
        if (warning != null && !force) throw new ProfileWarningException(warning);
        // A documented empty initialization envelope, independent of any device or supplied profile.
        return profile.ApplyTo(warning == null ? original : Convert.FromHexString("0101010001000000000000000100"));
    }
    internal static bool PersonalizationEnabled(BackupRecord? state, byte[] current)
    {
        string hash = Hash(current);
        if (state != null) {
            bool restored = hash == state.OriginalHash || hash == state.RestoredHash;
            bool applied = hash == state.AppliedHash;
            if (restored || applied) return state.Enabled ?? !restored;
        }
        return ProfileFile.HasPersonalization(current);
    }
    public static string Backup(AudioDevice device)
    {
        device = Resolve(device);
        using var mutex = Acquire(); using var store = new Store(device, false); var current = store.Read(); VerifyRegistry(device, current);
        _ = EnsureOriginal(device, current); return SaveBackup(device, current);
    }
    public static string Reset(AudioDevice device, bool force = false)
    {
        device = Resolve(device);
        using var mutex = Acquire();
        _ = VerifyTool();
        using var store = new Store(device, true, force);
        var before = store.Read(force); VerifyRegistry(device, before);
        var compatibility = CompatibilityWarning(before);
        if (compatibility != null && !force) throw new ProfileWarningException(compatibility);
        var desired = compatibility == null ? DolbyRoomService.PrepareReset(device, before, force) : Convert.FromHexString("0101010001000000000000000100");
        var spatial = AudioPlatform.Spatial(device); var snapshot = Snapshot(device);
        var eqSnapshot=AtmosEqService.RoomGain(device,force)==0?null:AtmosEqService.Capture(device);
        var backup = SaveBackup(device, before); bool attempted = false;
        try {
            SetSpatial(device, false); attempted = true;
            if (!before.AsSpan().SequenceEqual(desired)) store.Write(desired);
#if TESTING
            AfterResetWriteTest?.Invoke();
#endif
            SetSpatial(device, true);
            if(eqSnapshot!=null)AtmosEqService.ChangeRoomGain(device,0,force);
            var fresh = store.Read(); VerifyRegistry(device, fresh);
            if (!fresh.AsSpan().SequenceEqual(desired)) throw new ProfileOperationException("The reset Dolby profile could not be verified.");
            SpatialFiles.VerifyUnchanged(snapshot, Snapshot(device),eqSnapshot==null?[Property]:[Property,"{"+AtmosEqService.Primary+"},2","{"+AtmosEqService.Alternate+"},2"]);
            ResetStateFiles(device, () => {
                EnsureOriginal(device, fresh);
                backup = SaveBackup(device, fresh);
            });
            return backup;
        } catch (Exception error) {
            try {
                if (attempted) { store.Write(before); VerifyRegistry(device, before); if (!store.Read(true).AsSpan().SequenceEqual(before)) throw new IOException("Dolby reset rollback failed."); }
                if(eqSnapshot!=null)AtmosEqService.Restore(eqSnapshot);
                DtsProfileService.SelectProvider(device, spatial.DefaultSpatialAudioFormat, "the original spatial provider");
                SpatialFiles.VerifyUnchanged(snapshot, Snapshot(device));
            } catch (Exception restore) { throw new AggregateException("Dolby reset and restoration failed. Backup: " + backup, error, restore); }
            throw;
        }
    }
    // Dolby reset includes room state; DTS reset owns only its independent subtree.
    internal static void ResetStateFiles(AudioDevice device, Action initialize, string feature = "")
    {
        var folder = Path.GetDirectoryName(StatePath(device))!;
        if (feature != "" && feature != "dts") throw new ArgumentException("Unknown reset scope.");
        if (feature == "dts") folder = Path.Combine(folder, "dts");
        Directory.CreateDirectory(folder);
        var previous = folder + ".reset." + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(previous);
        var paths = (feature == "dts" ? Directory.GetFileSystemEntries(folder) : Directory.GetFiles(folder, "original.json*")).Select(Path.GetFileName).Cast<string>().ToList();
        if (feature == "") foreach (string name in new[] { "backups", "room" }) if (Directory.Exists(Path.Combine(folder, name))) paths.Add(name);
        var moved = new List<string>();
        bool initializing = false;
        try {
            foreach (var name in paths) {
                var source = Path.Combine(folder, name); var target = Path.Combine(previous, name);
                if (Directory.Exists(source)) Directory.Move(source, target); else File.Move(source, target);
                moved.Add(name);
            }
            initializing = true; initialize();
        } catch {
            // Remove only the new PHRTF state, never another tab's backups.
            if (initializing) {
                var generated = feature == "dts" ? Directory.GetFileSystemEntries(folder) : Directory.GetFiles(folder, "original.json*").Concat(new[] { Path.Combine(folder, "backups"), Path.Combine(folder, "room") }).ToArray();
                foreach (var path in generated) {
                    if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path);
                }
            }
            foreach (var name in moved) {
                var source = Path.Combine(previous, name); var target = Path.Combine(folder, name);
                if (Directory.Exists(source)) Directory.Move(source, target); else File.Move(source, target);
            }
            throw;
        }
        // Cleanup failure must not undo a successfully initialized configuration.
        try { Directory.Delete(previous, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
        var startingSpatial = AudioPlatform.Spatial(device);
        ProfileStatus status;
        try { status = Inspect(device); }
        catch (Exception e) when (e is ProfileOperationException or InvalidDataException or EndOfStreamException or COMException) {
            if (!force) throw new ProfileWarningException("The existing Dolby configuration could not be inspected. Allow to attempt applying the selected profile with an automatic backup. " + e.Message);
            bool active = startingSpatial.ActiveSpatialAudioFormat.Equals(AudioPlatform.Dolby, StringComparison.OrdinalIgnoreCase)
                && startingSpatial.DefaultSpatialAudioFormat.Equals(AudioPlatform.Dolby, StringComparison.OrdinalIgnoreCase);
            status = new(active, false, false, "Dolby configuration inspection unavailable.");
        }
        if (!force && (status.Warning != null || !status.Atmos)) throw new ProfileWarningException(status.Warning ?? status.Message);
        if (manualRestart == null) _ = VerifyTool();
        void Restart(bool on) { if (manualRestart == null) SetSpatial(device, on); else { manualRestart(on); VerifySpatial(device, on); } }
        using var store = new Store(device, true, force); var before = store.Read(force); VerifyRegistry(device, before); var snapshot = Snapshot(device);
        if (!enabled && LoadStateForRestore(device) == null) {
            if (!force) throw new ProfileWarningException("Saved configuration could not be verified. Allow to restore the earliest verified backup.");
            var recovered = RecoverState(device) ?? throw new ProfileOperationException("No backup could be verified. Reset configuration.");
            SaveState(device, recovered);
        }
        BackupRecord state;
        try { state = EnsureOriginal(device, before); }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or FormatException or ArgumentNullException) {
            if (!force) throw new ProfileWarningException("Saved configuration could not be verified. Allow to save a new baseline.");
            SaveBackup(device, before);
            var oldPath = StatePath(device); File.Move(oldPath, oldPath + ".invalid." + Guid.NewGuid().ToString("N"));
            state = EnsureOriginal(device, before);
        }
        byte[] original = Convert.FromBase64String(state.Original);
        string hash = Hash(before);
        if (!force && hash != state.OriginalHash && hash != state.AppliedHash && hash != state.RestoredHash) throw new ProfileWarningException("The device profile changed outside this application.");
        var desired = enabled ? PreparePersonalization(profile ?? throw new ProfileOperationException("Select a valid personalized headphone profile."), original, force) : original;
        if (enabled && PersonalizationTransform != null) desired = PersonalizationTransform(device, desired, force);
        double roomGain=enabled&&PersonalizationTransform!=null?DolbyRoomService.SelectedGainCorrection(device,before,force):0;
        var eqSnapshot=AtmosEqService.RoomGain(device,force)==roomGain?null:AtmosEqService.Capture(device);
        var backup = SaveBackup(device, before); bool attempted = false;
        try
        {
            Restart(false); attempted = true; store.Write(desired);
#if TESTING
            AfterWriteTest?.Invoke();
#endif
            if (!store.Read(true).SequenceEqual(desired)) throw new ProfileOperationException("The profile could not be verified.");
            VerifyRegistry(device, desired);
            var after = Snapshot(device);
            if (snapshot.Where(p => p.Key != Property).Count() != after.Where(p => p.Key != Property).Count() || snapshot.Any(p => p.Key != Property && (!after.TryGetValue(p.Key, out var v) || v != p.Value))) throw new ProfileOperationException("Another Dolby property changed.");
            Restart(true);
            if (!store.Read(true).SequenceEqual(desired)) throw new ProfileOperationException("The profile could not be verified after restarting Atmos.");
            VerifyRegistry(device, desired);
            if(eqSnapshot!=null)AtmosEqService.ChangeRoomGain(device,roomGain,force);
            if (enabled) state.AppliedHash = Hash(desired);
            state.Enabled = enabled;
            SaveState(device, state); return backup;
        }
        catch (Exception error)
        {
            try
            {
                if (attempted)
                {
                    store.Write(before);
                    if (!store.Read(true).SequenceEqual(before)) throw new IOException("Rollback API mismatch.");
                    VerifyRegistry(device, before);
                    if(eqSnapshot!=null)AtmosEqService.Restore(eqSnapshot);
                    var restored = Snapshot(device);
                    if (restored.Count != snapshot.Count || snapshot.Any(p => !restored.TryGetValue(p.Key, out var v) || v != p.Value)) throw new IOException("Dolby context changed during rollback.");
                }
                if (manualRestart == null) DtsProfileService.SelectProvider(device, startingSpatial.DefaultSpatialAudioFormat, "the original spatial provider");
                else Restart(status.Atmos);
                if (!store.Read(true).SequenceEqual(before)) throw new IOException("Rollback profile changed after provider restoration.");
                VerifyRegistry(device, before);
            }
            catch (Exception restore) { throw new AggregateException("Profile switch and restoration failed. Backup: " + backup, error, restore); }
            throw;
        }
    }
}
