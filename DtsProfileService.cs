using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace AtmosProfile;

internal sealed class ProfileToolException(string message, string details) : ProfileOperationException(message)
{
    public string Details { get; } = details;
    public override string ToString() => base.ToString() + Environment.NewLine + Details;
}
internal sealed record DtsStatus(bool Custom, bool Active, string EqMode, string RoomMode, string Message);

internal sealed class DtsBackup
{
    public string Endpoint { get; set; } = "";
    public byte[] Original { get; set; } = [];
    public byte[] Device { get; set; } = [];
    public uint Counter { get; set; }
    public string OriginalHash { get; set; } = "";
    public string DeviceHash { get; set; } = "";
    public string AppliedHash { get; set; } = "";
    public bool? Enabled { get; set; }
    public string RoomMode { get; set; } = "Original";
    public string AppliedDeviceHash { get; set; } = "";
    public uint AppliedCounter { get; set; }
}

internal static class DtsProfileService
{
    internal static Action? AfterWriteTest { get; set; }
    public const string Provider = "{4444ACB0-8DC0-4C2C-A0D8-2C76DB470F86}";
    public const string Context = "08e54ab7-ff00-4b50-92bb-63d6dc1ef8ea";
    public const string Runtime = "db6bf241-2068-4aa3-b112-fd2a7058ad0c";
    public const string Device = "f01f13b3-a580-4fdc-9060-22049c7a2353";
    public const string Counter = "22789cc6-8567-4046-acc4-b80f43bd9298";
    public const string EncoderHash = "90e62a7f6d044b43e53413f1f0933647725838f60904de37cd4b49f3852a604c";
    public static string ToolRoot = AppContext.BaseDirectory;
    static string ConverterRoot => File.Exists(Path.Combine(ToolRoot, "DtsTools", "DtsSofaConverter.exe")) ? Path.Combine(ToolRoot, "DtsTools") : Path.Combine(ToolRoot, "DtsTools", "bin");
    static string StatePath(AudioDevice d) => Path.Combine(SpatialFiles.Folder(d, "dts"), "original.json");
    public static string FindEncoder()
    {
        using var packages = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        if (packages != null) foreach (string name in packages.GetSubKeyNames().Where(n => n.StartsWith("DTSInc.DTSSoundUnbound_", StringComparison.OrdinalIgnoreCase)).OrderByDescending(n => n)) {
            using var key = packages.OpenSubKey(name);
            if (key?.GetValue("PackageRootFolder") is string folder) {
                var path = Path.Combine(folder, "x64", "DtsHrtfEnc.dll");
                if (File.Exists(path)) return path;
            }
        }
        throw new ProfileOperationException("DTS Sound Unbound could not be found.");
    }
    public static async Task<string> RunTool(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start profile conversion.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            throw new ProfileToolException("The DTS operation timed out.", "The external tool exceeded its three-minute time limit.");
        }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        string text = await output; string diagnostic = await error;
        if (process.ExitCode != 0) {
            Directory.CreateDirectory(SpatialFiles.Root); File.WriteAllText(Path.Combine(SpatialFiles.Root, "dts-error.log"), diagnostic + text);
            string message = Path.GetFileName(executable) switch {
                "DtsSofaConverter.exe" => "SOFA conversion failed.",
                "validate_dts_native.exe" => "DTS native format validation failed.",
                _ => "The spatial sound operation failed."
            };
            string details = (diagnostic + "\n" + text).Trim();
            throw new ProfileToolException(message, details.Length == 0 ? $"The tool exited with code {process.ExitCode}." : details[..Math.Min(details.Length, 6000)]);
        }
        return text;
    }
    public static void ValidateSpac(byte[] data)
    {
        uint U(int offset) => offset >= 0 && offset + 4 <= data.Length ? BitConverter.ToUInt32(data, offset) : throw new InvalidDataException();
        if (data.Length < 60 || data.Length > 16 * 1024 * 1024 || U(0) != data.Length || U(4) != 0x43415053 || U(8) != 2 || U(16) != 0x4e484352 || U(20) != 2 || U(24) != 48000 || U(28) is 0 or > 32 || U(32) != 1)
            throw new ProfileOperationException("This DTS profile is not supported.");
        uint count = U(28), samples = U(40); long table = 96L + (samples + 2L) * count * 8L;
        long end = 36L + table;
        if (samples is 0 or > 65536 || U(44) != count || U(36) != table || U(12) != 24 + table || end + 24 > data.Length || U((int)end + 4) != 0x4e415052 || U((int)end) != 24 + count * 16 || end + 24 + count * 16 != data.Length)
            throw new ProfileOperationException("The DTS profile could not be verified.");
        long groupCount = 0; for (int i = 8; i < 24; i += 4) { uint group = U((int)end + i); if (group > 100) throw new ProfileOperationException("The DTS profile could not be verified."); groupCount += group; }
        if (groupCount != count) throw new ProfileOperationException("The DTS profile could not be verified.");
        for (int i = (int)end + 24; i < end + 24 + count * 12; i += 4) { uint word = U(i); if ((word & 0x7f800000) == 0x7f800000 || (word & 0x7f800000) == 0 && (word & 0x7fffffff) != 0) throw new ProfileOperationException("The DTS profile could not be verified."); }
        for (int i = 132; i < end; i += 4) { uint word = U(i); if ((word & 0x7f800000) == 0x7f800000 || (word & 0x7f800000) == 0 && (word & 0x7fffffff) != 0) throw new ProfileOperationException("The DTS profile could not be verified."); }
    }
    public static bool ValidateNative(string path, bool force = false)
    {
        return ValidateNative(File.ReadAllBytes(path), force);
    }
    internal static bool RequireNativeValidation(string encoderHash, bool force)
    {
        if (encoderHash == EncoderHash) return true;
        if (!force) throw new ProfileWarningException("This DTS version has not been verified. Allow to continue without native validation.");
        return false;
    }
    static bool ValidateNative(byte[] bytes, bool force, bool deviceProfile = false)
    {
        if (deviceProfile) ValidateDevice(bytes); else ValidateSpac(bytes);
        var encoder = FindEncoder();
        if (!RequireNativeValidation(ProfileService.Hash(File.ReadAllBytes(encoder)), force)) return false;
        var folder = Path.Combine(SpatialFiles.Root, "validation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "runtime_SPAC.bin");
        try {
            File.WriteAllBytes(path, bytes);
            // Validate the same snapshot that will be written, even if the input file changes.
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            RunTool(Path.Combine(ConverterRoot, "validate_dts_native.exe"), deviceProfile ? new[] { encoder, path, "--device" } : new[] { encoder, path }).GetAwaiter().GetResult();
            return true;
        } finally { Directory.Delete(folder, true); }
    }
    internal static void ValidateDevice(byte[] bytes)
    {
        if (bytes.Length < 64 || bytes.Length > 16 * 1024 * 1024 || BitConverter.ToUInt32(bytes, 0) != bytes.Length || BitConverter.ToUInt32(bytes, 4) != 0x56454452 || BitConverter.ToUInt32(bytes, 8) != 2 || BitConverter.ToUInt32(bytes, 20) != 0x51504752 || BitConverter.ToUInt32(bytes, 24) != 2 || BitConverter.ToUInt32(bytes, 28) != 48000)
            throw new ProfileOperationException("This DTS device EQ format is not supported.");
        long left = BitConverter.ToUInt32(bytes, 44), right = BitConverter.ToUInt32(bytes, 48), fir = BitConverter.ToUInt32(bytes, 52);
        if (left > 64 || right > 64 || fir > 65536 || 56 + (left + right) * 24 + fir * 8 > bytes.Length)
            throw new ProfileOperationException("The DTS device EQ data is truncated or unsupported.");
    }
    internal static byte[] EqOff(byte[] original)
    {
        ValidateDevice(original);
        var result = original.ToArray();
        // RGPQ v2: zero processing delays and no biquad or FIR filters. Retain all other bytes.
        Array.Clear(result, 32, 24);
        return result;
    }
    public static string ChangeEq(AudioDevice selected, string mode, bool force = false) => Change(selected, null, true, force, mode, true);
    public static string ChangeRoom(AudioDevice selected, string mode, bool force = false) => Change(selected, null, true, force, "Current", true, mode);
    static DtsBackup? Load(AudioDevice d)
    {
        if (!File.Exists(StatePath(d))) return null;
        var state = JsonSerializer.Deserialize<DtsBackup>(File.ReadAllText(StatePath(d)));
        if (state == null || state.Endpoint != d.Id || ProfileService.Hash(state.Original) != state.OriginalHash || ProfileService.Hash(state.Device) != state.DeviceHash) throw new ProfileOperationException("The DTS backup could not be verified.");
        ValidateSpac(state.Original); return state;
    }
    internal static DtsBackup? LoadForChange(AudioDevice d, bool force)
    {
        try { return Load(d); }
        catch (Exception e) when (e is JsonException or InvalidDataException or FormatException or ProfileOperationException or ArgumentNullException) {
            if (!force) throw new ProfileWarningException("The DTS backup could not be verified. Allow to use the current profile as a new baseline.");
            return null;
        }
    }
    static (byte[] Runtime, byte[] Device, uint Counter) Read(SpatialStore store, AudioDevice d, bool force = false)
    {
        var runtime = store.Blob(Runtime); ValidateSpac(runtime);
        var device = store.Blob(Device); var counter = store.Counter(Counter);
        if (!force && store.Read("7922336d-e1db-432e-ba26-286256bfe2bf").Type != 0) throw new ProfileWarningException("This DTS device has additional configuration. Allow to preserve it while applying the profile.");
        SpatialFiles.VerifyPersisted(d, Context, Runtime, runtime); SpatialFiles.VerifyPersisted(d, Context, Device, device);
        SpatialFiles.VerifyPersistedCounter(d, Context, Counter, counter);
        return (runtime, device, counter);
    }
    public static DtsStatus Inspect(AudioDevice selected)
    {
        var d = ProfileService.Resolve(selected); using var store = new SpatialStore(d, Context, false, false); var current = Read(store, d, true); var state = Load(d);
        var spatial = AudioPlatform.Spatial(d);
        bool active = spatial.ActiveSpatialAudioFormat.Equals(Provider, StringComparison.OrdinalIgnoreCase) && spatial.DefaultSpatialAudioFormat.Equals(Provider, StringComparison.OrdinalIgnoreCase);
        return Describe(state, current.Runtime, current.Device, current.Counter, active);
    }
    internal static DtsStatus Describe(DtsBackup? state, byte[] runtime, byte[] device, uint counter, bool active)
    {
        string hash = ProfileService.Hash(runtime);
        bool custom = state != null && hash == state.AppliedHash && (state.Enabled ?? hash != state.OriginalHash);
        string profile = state != null && !MatchesState(state, runtime, device, counter) ? "Profile changed outside this application." : custom ? "Custom profile loaded." : "Device profile loaded.";
        string eqMode;
        try { ValidateDevice(device); eqMode = device.AsSpan(44, 12).IndexOfAnyExcept((byte)0) < 0 ? "Off" : "Original"; } catch (ProfileOperationException) { eqMode = "Unknown"; }
        return new(custom, active, eqMode, state != null && hash == state.AppliedHash ? state.RoomMode : "Original", (active ? "DTS Headphone:X active. " : "DTS Headphone:X is not active. ") + profile);
    }
    public static async Task<(string Path, string Summary, bool NativeValidated)> Convert(AudioDevice selected, string sofa, bool force = false)
    {
        var d = ProfileService.Resolve(selected);
        if (!File.Exists(sofa)) throw new ProfileToolException("The SOFA file was not found.", sofa);
        if (new FileInfo(sofa).Length > 1024L * 1024 * 1024) throw new ProfileOperationException("The SOFA file exceeds the 1 GiB size limit.");
        var folder = Path.Combine(SpatialFiles.Folder(d, "dts"), "conversions", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        using (var store = new SpatialStore(d, Context, false, false)) { var current = Read(store, d, force); File.WriteAllBytes(Path.Combine(folder, "template.bin"), LoadForChange(d, force)?.Original ?? current.Runtime); }
        await RunTool(Path.Combine(ConverterRoot, "DtsSofaConverter.exe"), sofa, "--template", Path.Combine(folder, "template.bin"), "--output", Path.Combine(folder, "converted"));
        var path = Path.Combine(folder, "converted", "runtime_SPAC.bin"); bool nativeValidated = ValidateNative(path, force);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "converted", "manifest.json"))); var r = doc.RootElement;
        SpatialFiles.Save(Path.Combine(folder, "endpoint.json"), new { Endpoint = d.Id, Hash = ProfileService.Hash(File.ReadAllBytes(path)) });
        string summary = $"SOFA: {r.GetProperty("source_directions").GetInt32()} directions, {r.GetProperty("source_rate_hz").GetInt32():N0} Hz\n" +
            $"DTS: {r.GetProperty("converted_directional_positions").GetInt32()} directional nodes, {r.GetProperty("response_samples").GetInt32():N0} samples per ear, 48,000 Hz\n" +
            $"Interpolation: common time-domain weights for both ears\n" +
            $"Added common delay: {r.GetProperty("added_common_delay_source_samples").GetInt32()} source samples\n" +
            (nativeValidated ? "Native format validation: passed with the installed DTS encoder." : "Native format validation: skipped by override for an unverified encoder version.");
        return (path, summary, nativeValidated);
    }
    public static string Change(AudioDevice selected, string? candidate, bool enabled, bool force = false, string eqMode = "Off", bool settingsOnly = false, string? roomMode = null)
    {
        var d = ProfileService.Resolve(selected); using var operation = new SpatialOperation();
        SpatialFiles.RequireProvider(d, Provider, "DTS Headphone:X");
        byte[]? desired = null;
        if (enabled && !settingsOnly) { if (candidate == null) throw new ProfileOperationException("Select and verify a SOFA file."); desired = File.ReadAllBytes(candidate); ValidateNative(desired, force); }
        using var store = new SpatialStore(d, Context, true, false); var before = Read(store, d, force); var snapshot = SpatialFiles.Snapshot(d, Context);
        if (desired != null) VerifyTemplate(before.Runtime, desired, force);
        var state = LoadForChange(d, force);
        if (state == null) {
            if (!enabled) throw new ProfileOperationException("No verified DTS backup is available. Apply a profile to save a new baseline.");
            if (File.Exists(StatePath(d))) File.Copy(StatePath(d), StatePath(d) + ".invalid." + Guid.NewGuid().ToString("N"));
            state = new() { Endpoint = d.Id, Original = before.Runtime, Device = before.Device, Counter = before.Counter, OriginalHash = ProfileService.Hash(before.Runtime), DeviceHash = ProfileService.Hash(before.Device) }; SpatialFiles.Save(StatePath(d), state);
        }
        string hash = ProfileService.Hash(before.Runtime);
        if (!force && !MatchesState(state, before.Runtime, before.Device, before.Counter)) throw new ProfileWarningException("The DTS profile changed outside this tab. Allow to overwrite it.");
        desired ??= settingsOnly ? before.Runtime : state.Original;
        byte[] desiredDevice = state.Device;
        bool validateDevice = true;
        if (enabled) {
            if (eqMode is not ("Off" or "Original" or "Current")) throw new ProfileOperationException("Unknown DTS EQ setting.");
            if (eqMode == "Current") desiredDevice = before.Device;
            if (eqMode == "Off") {
                try { desiredDevice = EqOff(state.Device); }
                catch (ProfileOperationException) {
                    if (!force) throw new ProfileWarningException("This DTS EQ format is not supported. Allow to apply without changing device EQ.");
                    desiredDevice = before.Device; validateDevice = false;
                }
            }
            if (validateDevice) {
                try { ValidateDevice(desiredDevice); }
                catch (ProfileOperationException) {
                    if (!force) throw new ProfileWarningException("This saved DTS EQ format is unsupported. Allow to restore its unchanged data without native validation.");
                    validateDevice = false;
                }
                if (validateDevice) ValidateNative(desiredDevice, force, true);
            }
        }
        string appliedRoom = state.RoomMode;
        if (enabled && (!settingsOnly || roomMode != null)) {
            desired = DtsRoomService.Apply(desired, state.Original, roomMode ?? state.RoomMode, force, out appliedRoom);
            ValidateNative(desired, force);
        }
        uint counter = enabled ? unchecked(before.Counter + 1) : state.Counter;
        var folder = Path.Combine(SpatialFiles.Folder(d, "dts"), "backups", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "_" + Guid.NewGuid().ToString("N"));
        SpatialFiles.Save(Path.Combine(folder, "before.json"), new { Endpoint = d.Id, Runtime = before.Runtime, Device = before.Device, Counter = before.Counter, Context = snapshot });
        try {
            store.Write(Runtime, desired);
            var intermediate = Read(store, d, force);
            if (!intermediate.Runtime.SequenceEqual(desired) || !intermediate.Device.SequenceEqual(before.Device) || intermediate.Counter != before.Counter) throw new ProfileOperationException("The DTS profile could not be verified.");
            store.Write(Device, desiredDevice);
            store.Write(Counter, counter);
            AfterWriteTest?.Invoke();
            var after = Read(store, d, force);
            if (!after.Runtime.SequenceEqual(desired) || !after.Device.SequenceEqual(desiredDevice) || after.Counter != counter) throw new ProfileOperationException("The DTS profile could not be verified.");
            SpatialFiles.VerifyUnchanged(snapshot, SpatialFiles.Snapshot(d, Context), "{" + Runtime + "},1", "{" + Device + "},1", "{" + Counter + "},1");
            SpatialFiles.RequireProvider(d, Provider, "DTS Headphone:X");

            if (enabled) { state.Enabled = settingsOnly ? state.Enabled ?? hash == state.AppliedHash && hash != state.OriginalHash : true; state.RoomMode = appliedRoom; state.AppliedHash = ProfileService.Hash(desired); state.AppliedCounter = counter; state.AppliedDeviceHash = ProfileService.Hash(desiredDevice); }
            if (!enabled) state.Enabled = false;
            SpatialFiles.Save(StatePath(d), state); return settingsOnly ? roomMode != null ? "DTS room updated. Profile preserved." : "DTS EQ updated. Profile preserved." : enabled ? "Custom DTS profile on." : "Saved DTS profile restored.";
        } catch {
            var current = Read(store, d, force);
            if ((!current.Runtime.SequenceEqual(before.Runtime) && !current.Runtime.SequenceEqual(desired)) || current.Counter != before.Counter && current.Counter != counter || !current.Device.AsSpan().SequenceEqual(before.Device) && !current.Device.AsSpan().SequenceEqual(desiredDevice)) throw new ProfileOperationException("The DTS profile changed during the operation. The backup is saved.");
            store.Write(Runtime, before.Runtime); store.Write(Device, before.Device); store.Write(Counter, before.Counter);
            var restored = Read(store, d, force);
            if (!restored.Runtime.SequenceEqual(before.Runtime) || !restored.Device.SequenceEqual(before.Device) || restored.Counter != before.Counter) throw new ProfileOperationException("The DTS profile could not be restored.");
            SpatialFiles.VerifyUnchanged(snapshot, SpatialFiles.Snapshot(d, Context)); throw;
        }
    }
    internal static bool MatchesState(DtsBackup state, byte[] runtime, byte[] device, uint counter)
    {
        string hash = ProfileService.Hash(runtime);
        return (ProfileService.Hash(device) == state.DeviceHash || ProfileService.Hash(device) == state.AppliedDeviceHash) &&
            (hash == state.OriginalHash && counter == state.Counter || hash == state.AppliedHash && counter == state.AppliedCounter);
    }
    internal static void VerifyTemplate(byte[] template, byte[] candidate, bool force = false)
    {
        ValidateSpac(template); ValidateSpac(candidate);
        int a = checked(12 + (int)BitConverter.ToUInt32(template, 12));
        int b = checked(12 + (int)BitConverter.ToUInt32(candidate, 12));
        if (!force && (!template.AsSpan(a).SequenceEqual(candidate.AsSpan(b)) || !template.AsSpan(48, 84).SequenceEqual(candidate.AsSpan(48, 84))))
            throw new ProfileWarningException("The DTS device template changed. Convert again, or allow to apply the verified profile anyway.");
    }
    public static void Enable(AudioDevice selected) => SelectProvider(selected, Provider, "DTS Headphone:X");
    public static void Reset(AudioDevice selected, bool force = false)
    {
        var d = ProfileService.Resolve(selected); using var operation = new SpatialOperation();
        using var store = new SpatialStore(d, Context, true, false);
        var before = Read(store, d, force); var snapshot = SpatialFiles.Snapshot(d, Context);
        var state = LoadForChange(d, force);
        if (state != null && !force && !MatchesState(state, before.Runtime, before.Device, before.Counter))
            throw new ProfileWarningException("The DTS profile changed outside this tab. Allow to reset it using the saved original.");
        var resetDevice = state?.Device ?? before.Device;
        var desired = state?.Original ?? before.Runtime; uint counter = state?.Counter ?? before.Counter;
        var backup = Path.Combine(SpatialFiles.Folder(d, "dts"), "backups", Guid.NewGuid().ToString("N"), "before.json");
        SpatialFiles.Save(backup, new { Endpoint = d.Id, Runtime = before.Runtime, Device = before.Device, Counter = before.Counter, Context = snapshot });
        try {
            if (!desired.AsSpan().SequenceEqual(before.Runtime)) store.Write(Runtime, desired);
            if (!resetDevice.AsSpan().SequenceEqual(before.Device)) store.Write(Device, resetDevice);
            if (counter != before.Counter) store.Write(Counter, counter);
            AfterWriteTest?.Invoke();
            var after = Read(store, d, force);
            if (!after.Runtime.AsSpan().SequenceEqual(desired) || after.Counter != counter || !after.Device.AsSpan().SequenceEqual(resetDevice))
                throw new ProfileOperationException("The reset DTS profile could not be verified.");
            SpatialFiles.VerifyUnchanged(snapshot, SpatialFiles.Snapshot(d, Context), "{" + Runtime + "},1", "{" + Device + "},1", "{" + Counter + "},1");
            ProfileService.ResetStateFiles(d, () => SpatialFiles.Save(StatePath(d), new DtsBackup {
                Endpoint = d.Id, Original = desired, Device = resetDevice, Counter = counter,
                OriginalHash = ProfileService.Hash(desired), DeviceHash = ProfileService.Hash(resetDevice)
            }), "dts");
        } catch (Exception error) {
            try {
                var current = Read(store, d, force);
                if ((!current.Runtime.AsSpan().SequenceEqual(before.Runtime) && !current.Runtime.AsSpan().SequenceEqual(desired)) || current.Counter != before.Counter && current.Counter != counter || !current.Device.AsSpan().SequenceEqual(before.Device) && !current.Device.AsSpan().SequenceEqual(resetDevice))
                    throw new ProfileOperationException("The DTS profile changed during reset. The backup is saved.");
                store.Write(Runtime, before.Runtime); store.Write(Device, before.Device); store.Write(Counter, before.Counter);
                var restored = Read(store, d, force);
                if (!restored.Runtime.AsSpan().SequenceEqual(before.Runtime) || restored.Counter != before.Counter || !restored.Device.AsSpan().SequenceEqual(before.Device)) throw new IOException("DTS reset rollback failed.");
                SpatialFiles.VerifyUnchanged(snapshot, SpatialFiles.Snapshot(d, Context));
            } catch (Exception restore) { throw new AggregateException("DTS reset and restoration failed. Backup: " + backup, error, restore); }
            throw;
        }
    }
    public static void SelectProvider(AudioDevice selected, string provider, string name)
    {
        var d = ProfileService.Resolve(selected); using var operation = new SpatialOperation();
        var tool = Path.Combine(ToolRoot, "Tools", "SoundVolumeView.exe");
        if (ProfileService.Hash(File.ReadAllBytes(tool)) != "b5af5bd60f7a29af8cb4d8a566382b90f0fe07cac97228d218cb913f3382d647") throw new ProfileOperationException("The audio helper could not be verified.");
        var original = AudioPlatform.Spatial(d);
        try {
            RunTool(tool, "/SetSpatial", d.Id, provider).GetAwaiter().GetResult();
            for (int i = 0; i < 40; i++) { var s = AudioPlatform.Spatial(d); if (s.ActiveSpatialAudioFormat.Equals(provider, StringComparison.OrdinalIgnoreCase) && s.DefaultSpatialAudioFormat.Equals(provider, StringComparison.OrdinalIgnoreCase)) return; Thread.Sleep(100); }
            throw new ProfileOperationException(name + " could not be enabled.");
        } catch {
            RunTool(tool, "/SetSpatial", d.Id, original.DefaultSpatialAudioFormat).GetAwaiter().GetResult();
            for (int i = 0; i < 40; i++) { var s = AudioPlatform.Spatial(d); if (s.ActiveSpatialAudioFormat.Equals(original.ActiveSpatialAudioFormat, StringComparison.OrdinalIgnoreCase) && s.DefaultSpatialAudioFormat.Equals(original.DefaultSpatialAudioFormat, StringComparison.OrdinalIgnoreCase)) throw new ProfileOperationException(name + " could not be enabled."); Thread.Sleep(100); }
            throw new ProfileOperationException("The spatial sound setting could not be restored.");
        }
    }
}
