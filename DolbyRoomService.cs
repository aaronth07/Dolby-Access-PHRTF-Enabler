using System.Text.Json;

namespace AtmosProfile;

internal sealed class RoomBackup
{
    public string Endpoint { get; set; } = "";
    public byte[] Original { get; set; } = [];
    public string OriginalHash { get; set; } = "";
    public string AppliedHash { get; set; } = "";
    public string Mode { get; set; } = "Original";
    public double LevelPercent { get; set; } = 100;
    public double FeedbackPercent { get; set; } = 100;
    public bool GainCorrection { get; set; }
    public byte[]? DefaultTuning { get; set; }
    public string? DefaultTuningHash { get; set; }
}

internal static class DolbyRoomService
{
    internal static Action? AfterWriteTest { get; set; }
    const string Property = "1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8";
    internal static bool UsesBuiltInTuning(byte[] api)
    {
        try {
            using var r = new BinaryReader(new MemoryStream(api));
            byte Flag() { byte b = r.ReadByte(); if (b > 1) throw new InvalidDataException(); return b; }
            for (int i = 0; i < 2; i++) if (Flag() != 0) _ = Flag();
            if (Flag() != 0) _ = r.ReadUInt32();
            if (Flag() != 0 || Flag() != 0 || Flag() != 0) return false;
            if (Flag() != 0) _ = Flag();
            return r.BaseStream.Position == api.Length;
        } catch (Exception e) when (e is EndOfStreamException or InvalidDataException) { return false; }
    }
    public static (int Start, int[] Values) Parse(byte[] api, bool force = false)
    {
        if (UsesBuiltInTuning(api)) return (-1, []);
        using var reader = new BinaryReader(new MemoryStream(api));
        byte Flag() { byte b = reader.ReadByte(); if (b > 1) throw new InvalidDataException(); return b; }
        for (int i = 0; i < 2; i++) if (Flag() != 0) _ = Flag();
        if (Flag() != 0) _ = reader.ReadUInt32();
        int start = -1; int[]? values = null; string? version = null;
        int[] Table() {
            uint count = reader.ReadUInt32();
            if (count is < 2 or > 32768) throw new ProfileOperationException("This room coefficient layout is not supported.");
            start = (int)reader.BaseStream.Position;
            var table = new int[count]; for (int i = 0; i < table.Length; i++) table[i] = reader.ReadInt32();
            return table;
        }
        if (Flag() != 0) values = Table();
        if (Flag() != 0) {
            if (values != null) throw new ProfileOperationException("Multiple Dolby tuning tables are present. Their active selection cannot be determined.");
            values = Table();
            uint size = reader.ReadUInt32();
            if (size == 0 || size > 20) throw new ProfileOperationException("This room profile is not supported.");
            var bytes = reader.ReadBytes((int)size);
            if (bytes.Length != size || bytes.Any(b => b < 32 || b > 126)) throw new ProfileOperationException("This room profile is not supported.");
            version = System.Text.Encoding.ASCII.GetString(bytes);
        }
        if (Flag() != 0) throw new ProfileOperationException("An alternate Dolby tuning format is present. Its room layout is not supported.");
        if (Flag() != 0) _ = Flag();
        if (reader.BaseStream.Position != api.Length) throw new ProfileOperationException("This room profile is not supported.");
        if (values == null) throw new ProfileOperationException("Dolby is using its built-in headphone tuning. Room adjustment requires an explicit supported Dolby coefficient table.");
        if (DolbyLegacyRoom.IsLegacy(values)) {
            _ = DolbyLegacyRoom.Parse(values);
            if (!force && version != null && version != "1.1.0") throw new ProfileWarningException("This Dolby room version has not been verified. Allow to use the supported coefficient layout anyway.");
            return (start, values);
        }
        if (values.Length != 15691) throw new ProfileOperationException("This room coefficient layout is not supported.");
        int[] header = [0x7072, 0, 1, 1, 0, 64, 3, 0, 1, 25, 20, 11, 36];
        if (header.Where((v, i) => i != 1 && (ushort)values[i] != v).Any()) throw new ProfileOperationException("This room profile is not supported.");
        int checksum = 0xa569; foreach (var v in values.Skip(2)) checksum ^= (ushort)v;
        if (checksum != (ushort)values[1]) throw new ProfileOperationException("The room profile could not be verified.");
        if (!force && version != null && version != "1.1.0")
            throw new ProfileWarningException("This Dolby room version has not been verified. Allow to use the supported coefficient layout anyway.");
        return (start, values);
    }
    static double DirectionalGain(string mode) => mode == "DoubleRoom" ? .5 : mode == "QuadRoom" ? .25 : 1;
    public static byte[] Create(byte[] original, string mode, bool force = false, double levelPercent = 100, double feedbackPercent = 100)
    {
        if (mode == "Restore") { _ = Parse(original, force); return original.ToArray(); }
        if (mode is not ("On" or "Off" or "HalfFeedback" or "DoubleRoom" or "QuadRoom" or "Custom")) throw new ArgumentException("Unknown room setting.");
        if (UsesBuiltInTuning(original)) original = DolbyLegacyRoom.WithDefaultTuning(original);
        var (start, values) = Parse(original, force);
        if (DolbyLegacyRoom.IsLegacy(values)) {
            var changed = DolbyLegacyRoom.Change(values, mode, levelPercent, feedbackPercent); var legacyResult = original.ToArray();
            for (int i = 0; i < changed.Length; i++) BitConverter.GetBytes(changed[i]).CopyTo(legacyResult, start + i * 4);
            _ = Parse(legacyResult, force); return legacyResult;
        }
        if (mode != "Custom") { levelPercent = mode == "Off" ? 0 : 100; feedbackPercent = mode == "HalfFeedback" ? 50 : 100; }
        if (!double.IsFinite(levelPercent) || !double.IsFinite(feedbackPercent) || levelPercent < 0 || levelPercent > 150 || feedbackPercent < 0 || feedbackPercent > 100)
            throw new ProfileOperationException("Room output level must be 0-150% and feedback 0-100%.");
        if (DirectionalGain(mode) != 1)
            for (int i = 4500; i < 15588; i++) values[i] = (int)Math.Round((short)values[i] * DirectionalGain(mode), MidpointRounding.ToEven);
        values[15689] = 0;
        for (int i = 1578; levelPercent != 100 && i < 1834; i++) {
            double scaled = Math.Round((short)values[i] * levelPercent / 100, MidpointRounding.ToEven);
            if (scaled < short.MinValue || scaled > short.MaxValue)
                throw new ProfileOperationException("This room level exceeds the profile's native coefficient range. Reduce the room level.");
            values[i] = (int)scaled;
        }
        for (int i = 1050; feedbackPercent != 100 && i < 1562; i++) values[i] = (int)Math.Round((short)values[i] * feedbackPercent / 100, MidpointRounding.ToEven);
        int checksum = 0xa569; foreach (var v in values.Skip(2)) checksum ^= (ushort)v; values[1] = checksum;
        var result = original.ToArray();
        for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(result, start + 4 * i);
        _ = Parse(result, force); return result;
    }
    static string StatePath(AudioDevice d) => Path.Combine(SpatialFiles.Folder(d, "room"), "original.json");
    static RoomBackup? Load(AudioDevice d, byte[]? current = null)
    {
        var pendingPath = Path.Combine(SpatialFiles.Folder(d, "room"), "pending.json");
        if (current != null && File.Exists(pendingPath)) {
            var pending = JsonSerializer.Deserialize<RoomBackup>(File.ReadAllText(pendingPath));
            if (pending != null && pending.Endpoint == d.Id && ProfileService.Hash(current) == pending.AppliedHash && ProfileService.Hash(pending.Original) == pending.OriginalHash) {
                _ = Parse(pending.Original, true); SpatialFiles.Save(StatePath(d), pending); File.Delete(pendingPath);
            }
        }
        var path = StatePath(d); if (!File.Exists(path)) return null;
        var state = JsonSerializer.Deserialize<RoomBackup>(File.ReadAllText(path))!;
        if (state == null || state.Endpoint != d.Id || ProfileService.Hash(state.Original) != state.OriginalHash || state.Mode is not ("Original" or "On" or "Off" or "HalfFeedback" or "DoubleRoom" or "QuadRoom" or "Custom" or "Restore")) throw new ProfileOperationException("The room backup could not be verified.");
        _ = Parse(state.Original, true);
        if (state.DefaultTuning != null) {
            if (!UsesBuiltInTuning(state.Original) || UsesBuiltInTuning(state.DefaultTuning) || ProfileService.Hash(state.DefaultTuning) != state.DefaultTuningHash) throw new ProfileOperationException("The saved built-in Dolby tuning could not be verified.");
            _ = Parse(state.DefaultTuning, true);
        }
        return state;
    }
    static byte[] Read(SpatialStore store, AudioDevice d, bool force = false)
    {
        if (store.Read("70b95986-9ee3-4b43-acb2-de7aeb1a9509").Type != 0 && !force) throw new ProfileWarningException("Additional Dolby configuration is present. Allow to change the room settings while preserving those properties.");
        var api = store.Blob(Property); if (!UsesBuiltInTuning(api)) _ = Parse(api, force); SpatialFiles.VerifyPersisted(d, ProfileService.Context, Property, api); return api;
    }
    internal static RoomBackup? LoadForChange(AudioDevice d, byte[] before, bool force)
    {
        try { return Load(d, before); }
        catch (Exception e) when (e is JsonException or InvalidDataException or FormatException or ProfileOperationException or ArgumentNullException) {
            if (!force) throw new ProfileWarningException("The room backup could not be verified. Allow to use the current profile as a new baseline.");
            return null;
        }
    }
    public static (string Mode, double Level, double Feedback, bool GainCorrection) Settings(AudioDevice selected)
    {
        var d = ProfileService.Resolve(selected); using var operation = new SpatialOperation();
        using var store = new SpatialStore(d, ProfileService.Context, false, true);
        var current = store.Blob(Property);
        if (UsesBuiltInTuning(current)) return ("On", 100, 100, false);
        var state = Load(d, current);
        return (state?.Mode ?? "On", state?.LevelPercent ?? 100, state?.FeedbackPercent ?? 100,state?.GainCorrection??false);
    }
    internal static double SelectedGainCorrection(AudioDevice d,byte[] before,bool force)
    {
        var state=LoadForChange(d,before,force);
        return state?.GainCorrection==true?(state.Mode=="DoubleRoom"?6:state.Mode=="QuadRoom"?12:0):0;
    }
    public static string Inspect(AudioDevice selected)
    {
        var d = ProfileService.Resolve(selected); using var operation = new SpatialOperation(); SpatialFiles.RequireProvider(d, AudioPlatform.Dolby, "Dolby Atmos for Headphones");
        using var store = new SpatialStore(d, ProfileService.Context, false, true); var api = Read(store, d, true);
        if (UsesBuiltInTuning(api)) return "Built-in Dolby headphone tuning active.";
        var state = Load(d, api); if (state == null) return "Room profile verified.";
        string hash = ProfileService.Hash(api);
        return hash == state.OriginalHash ? "Original room settings." : hash == state.AppliedHash ? state.Mode switch { "On" => "Room on.", "Off" => "Room off.", "HalfFeedback" => "Room feedback: 50%.", "DoubleRoom" => $"200% room/direct (experimental). Directional fields: -6.02 dB. Gain correction: {(state.GainCorrection ? "+6" : "0")} dB.", "QuadRoom" => $"400% room/direct (experimental). Directional fields: -12.04 dB. Gain correction: {(state.GainCorrection ? "+12" : "0")} dB.", "Custom" => $"Room level: {state.LevelPercent:g}%. Feedback: {state.FeedbackPercent:g}%.", _ => "Room profile verified." } : "The device profile changed outside this tab.";
    }
    public static string Change(AudioDevice selected, string mode, bool force = false, double levelPercent = 100, double feedbackPercent = 100,bool gainCorrection=false)
    {
        var d = ProfileService.Resolve(selected); using var operation = new SpatialOperation();
        SpatialFiles.RequireProvider(d, AudioPlatform.Dolby, "Dolby Atmos for Headphones");
        if (System.Diagnostics.Process.GetProcessesByName("DolbyAccess").Length != 0 && !force) throw new ProfileWarningException("Dolby Access is open. Allow to change the room settings.");
        using var store = new SpatialStore(d, ProfileService.Context, true, true); var before = Read(store, d, force);
        var snapshot = SpatialFiles.Snapshot(d, ProfileService.Context);
        var primaryPath = Path.GetFullPath(Path.Combine(SpatialFiles.Folder(d, "room"), "..", "original.json"));
        var savedPrimary = File.Exists(primaryPath) ? File.ReadAllBytes(primaryPath) : null;
        var primary = LoadPrimaryForChange(d, savedPrimary, force);
        var state = LoadForChange(d, before, force);
        var savedState = File.Exists(StatePath(d)) ? File.ReadAllBytes(StatePath(d)) : null;
        if (state == null) {
            if (mode == "Restore") throw new ProfileOperationException("No verified room backup is available. Apply a room setting to save a new baseline.");
            if (File.Exists(StatePath(d))) File.Copy(StatePath(d), StatePath(d) + ".invalid." + Guid.NewGuid().ToString("N"));
            var pending = Path.Combine(SpatialFiles.Folder(d, "room"), "pending.json");
            if (File.Exists(pending)) File.Move(pending, pending + ".invalid." + Guid.NewGuid().ToString("N"));
            var template = UsesBuiltInTuning(before) ? DolbyLegacyRoom.WithDefaultTuning(before) : null;
            state = new() { Endpoint = d.Id, Original = before, OriginalHash = ProfileService.Hash(before), DefaultTuning = template, DefaultTuningHash = template == null ? null : ProfileService.Hash(template) }; SpatialFiles.Save(StatePath(d), state);
        }
        var hash = ProfileService.Hash(before);
        if (hash != state.OriginalHash && hash != state.AppliedHash && !force) throw new ProfileWarningException("The device profile changed outside this tab. Allow to overwrite it.");
        // A newly applied PHRTF must keep its own directional coefficients.
        if (force && mode != "Restore" && hash != state.OriginalHash && hash != state.AppliedHash) {
            byte[] baseline = before; bool attenuated = false;
            if (DirectionalGain(state.Mode) != 1) {
                try { baseline = RestoreDirectionalGain(before, state.DefaultTuning ?? state.Original, DirectionalGain(state.Mode)); attenuated = true; }
                catch (ProfileOperationException) { /* A different listener becomes the explicitly approved baseline. */ }
            }
            var template = UsesBuiltInTuning(baseline) ? DolbyLegacyRoom.WithDefaultTuning(baseline) : null;
            state = new() { Endpoint = d.Id, Original = baseline, OriginalHash = ProfileService.Hash(baseline), Mode = attenuated ? state.Mode : "Original", AppliedHash = attenuated ? hash : "", DefaultTuning = template, DefaultTuningHash = template == null ? null : ProfileService.Hash(template) };
        }
        var roomBaseline = state.DefaultTuning ?? state.Original;
        var desired = mode == "Restore" ? state.Original.ToArray() : Create(roomBaseline, mode, force, levelPercent, feedbackPercent);
        double roomGain=gainCorrection?(mode=="DoubleRoom"?6:mode=="QuadRoom"?12:0):0;
        var eqSnapshot=AtmosEqService.RoomGain(d,force)==roomGain?null:AtmosEqService.Capture(d);
        if (!UsesBuiltInTuning(before)) VerifyPersonalizationPreserved(roomBaseline, before, force, DirectionalGain(state.Mode));
        if (!UsesBuiltInTuning(desired)) VerifyPersonalizationPreserved(roomBaseline, desired, force, DirectionalGain(mode));
        var backup = Path.Combine(SpatialFiles.Folder(d, "room"), "backups", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "_" + Guid.NewGuid().ToString("N"));
        SpatialFiles.Save(Path.Combine(backup, "before.json"), new { Endpoint = d.Id, Api = before, Hash = hash, Context = snapshot });
        try {
            if (!before.SequenceEqual(desired)) store.Write(Property, desired);
            AfterWriteTest?.Invoke();
            for (int check = 0; check < 3; check++) {
                var observed = store.Blob(Property);
                if (!observed.AsSpan().SequenceEqual(desired) && primary?.Enabled == true) {
                    bool baseline = ProfileService.Hash(observed) == primary.OriginalHash || ProfileService.Hash(observed) == primary.RestoredHash;
                    bool dropped = !ProfileFile.HasPersonalization(observed);
                    if (!baseline && !dropped) VerifyPersonalizationPreserved(before, observed, force);
                    store.Write(Property, desired);
                }
                if (!Read(store, d, true).SequenceEqual(desired)) throw new ProfileOperationException("The room setting could not be verified.");
                if (check < 2) Thread.Sleep(150);
            }
            if(eqSnapshot!=null)AtmosEqService.ChangeRoomGain(d,roomGain,force);
            SpatialFiles.VerifyUnchanged(snapshot, SpatialFiles.Snapshot(d, ProfileService.Context),eqSnapshot==null?[ProfileService.Property]:[ProfileService.Property,"{"+AtmosEqService.Primary+"},2","{"+AtmosEqService.Alternate+"},2"]);
            SpatialFiles.RequireProvider(d, AudioPlatform.Dolby, "Dolby Atmos for Headphones");
            state.AppliedHash = ProfileService.Hash(desired); state.Mode = mode; state.LevelPercent = levelPercent; state.FeedbackPercent = feedbackPercent;state.GainCorrection=roomGain!=0;
            if (primary != null) { UpdatePrimaryHashes(primary, before, desired); SpatialFiles.Save(primaryPath, primary); }
            SpatialFiles.Save(StatePath(d), state); return Inspect(d);
        } catch {
            var current = store.Blob(Property);
            if (!current.SequenceEqual(before) && !current.SequenceEqual(desired)) throw new ProfileOperationException("The profile changed during the operation. The backup is saved.");
            store.Write(Property, before); if (!Read(store, d, true).SequenceEqual(before)) throw new ProfileOperationException("The original room settings could not be restored.");
            if(eqSnapshot!=null)AtmosEqService.Restore(eqSnapshot);
            SpatialFiles.VerifyUnchanged(snapshot, SpatialFiles.Snapshot(d, ProfileService.Context));
            if (savedState != null) File.WriteAllBytes(StatePath(d), savedState);
            if (savedPrimary != null) File.WriteAllBytes(primaryPath, savedPrimary);
            throw;
        }
    }
    internal static BackupRecord? LoadPrimaryForChange(AudioDevice d, byte[]? saved, bool force)
    {
        if (saved == null) return null;
        try {
            var state = JsonSerializer.Deserialize<BackupRecord>(saved);
            if (state == null || state.Endpoint != d.Id || ProfileService.Hash(Convert.FromBase64String(state.Original)) != state.OriginalHash)
                throw new InvalidDataException();
            return state;
        } catch (Exception e) when (e is JsonException or InvalidDataException or FormatException or ArgumentNullException) {
            if (!force) throw new ProfileWarningException("The PHRTF backup could not be verified. Allow to change room settings while keeping that backup unchanged.");
            return null;
        }
    }
    internal static void UpdatePrimaryHashes(BackupRecord state, byte[] before, byte[] current)
    {
        bool enabled = ProfileService.PersonalizationEnabled(state, before);
        if (enabled) state.AppliedHash = ProfileService.Hash(current);
        else state.RestoredHash = ProfileService.Hash(current);
        state.Enabled = enabled;
    }
    internal static byte[] PrepareReset(AudioDevice d, byte[] current, bool force)
    {
        var state = LoadForChange(d, current, force);
        if (state == null) return current.ToArray();
        if (UsesBuiltInTuning(current)) return current.ToArray();
        var baseline = state.DefaultTuning ?? state.Original;
        var restored = RestoreRoomFields(current, baseline);
        if (DirectionalGain(state.Mode) != 1 && ProfileService.Hash(current) == state.AppliedHash) {
            VerifyPersonalizationPreserved(baseline, current, true, DirectionalGain(state.Mode));
            restored = RestoreDirectionalGain(restored, baseline, DirectionalGain(state.Mode));
            VerifyPersonalizationPreserved(baseline, restored, true);
        }
        return restored;
    }
    internal static byte[] RestoreDirectionalGain(byte[] current, byte[] original, double directionalGain = .5)
    {
        if (directionalGain is not (.5 or .25)) throw new ArgumentOutOfRangeException(nameof(directionalGain));
        var a = Parse(current, true); var b = Parse(original, true);
        if (DolbyLegacyRoom.IsLegacy(a.Values) || DolbyLegacyRoom.IsLegacy(b.Values)) {
            var fields = DolbyLegacyRoom.Parse(b.Values).Fields; var currentFields = DolbyLegacyRoom.Parse(a.Values).Fields;
            if (a.Values.Length != b.Values.Length || new[] {34,35}.Any(id => fields[id] != currentFields[id])) throw new ProfileOperationException("Directional fields no longer match the experimental baseline.");
            var values = a.Values.ToArray();
            foreach (int id in new[] {34, 35}) {
                var field = fields[id];
                for (int i = field.Start; i < field.Start + field.Count; i++) {
                    if (a.Values[i] != (int)Math.Round((short)b.Values[i] * directionalGain, MidpointRounding.ToEven)) throw new ProfileOperationException("Directional fields no longer match the experimental baseline.");
                    values[i] = b.Values[i];
                }
            }
            DolbyLegacyRoom.Checksum(values); var legacyResult = current.ToArray();
            for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(legacyResult, a.Start + i * 4);
            return legacyResult;
        }
        for (int i = 4500; i < 15588; i++)
            if (a.Values[i] != (int)Math.Round((short)b.Values[i] * directionalGain, MidpointRounding.ToEven))
                throw new ProfileOperationException("Directional fields no longer match the experimental baseline.");
        var restored = current.ToArray();
        for (int i = 4500; i < 15588; i++) BitConverter.GetBytes(b.Values[i]).CopyTo(restored, a.Start + i * 4);
        int sum = 0xa569;
        for (int i = 2; i < a.Values.Length; i++) sum ^= (ushort)BitConverter.ToInt32(restored, a.Start + i * 4);
        BitConverter.GetBytes(sum).CopyTo(restored, a.Start + 4);
        return restored;
    }
    internal static byte[] RestoreRoomFields(byte[] current, byte[] saved)
    {
        var a = Parse(current, true); var b = Parse(saved, true);
        if (DolbyLegacyRoom.IsLegacy(a.Values) || DolbyLegacyRoom.IsLegacy(b.Values)) {
            var currentFields = DolbyLegacyRoom.Parse(a.Values).Fields; var savedFields = DolbyLegacyRoom.Parse(b.Values).Fields;
            var values = a.Values.ToArray();
            foreach (int id in new[] {19, 21}) {
                var currentField = currentFields[id]; var savedField = savedFields[id];
                if (currentField.Count != savedField.Count) throw new ProfileOperationException("The saved Dolby room layout differs from the current tuning.");
                Array.Copy(b.Values, savedField.Start, values, currentField.Start, currentField.Count);
            }
            DolbyLegacyRoom.Checksum(values); var legacyResult = current.ToArray();
            for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(legacyResult, a.Start + i * 4);
            VerifyPersonalizationPreserved(current, legacyResult, true); return legacyResult;
        }
        var result = current.ToArray();
        foreach (int lane in Enumerable.Range(1050, 512).Concat(Enumerable.Range(1578, 256)).Append(15689))
            BitConverter.GetBytes(b.Values[lane]).CopyTo(result, a.Start + 4 * lane);
        int checksum = 0xa569;
        for (int i = 2; i < a.Values.Length; i++) checksum ^= (ushort)BitConverter.ToInt32(result, a.Start + i * 4);
        BitConverter.GetBytes(checksum).CopyTo(result, a.Start + 4);
        VerifyPersonalizationPreserved(current, result, true);
        return result;
    }
    public static byte[] PreserveSetting(AudioDevice d, byte[] personalized, bool force = false)
    {
        using var store = new SpatialStore(d, ProfileService.Context, false, true);
        var value = store.Read(Property);
        var current = value.Type == 0 ? Array.Empty<byte>() : value.Data;
        var state = LoadForChange(d, current, force);
        if (state?.Mode == "Restore") return personalized;
        string mode = state == null || state.Mode == "Original" ? "On" : state.Mode;
        var adjusted = Create(personalized, mode, force, state?.LevelPercent ?? 100, state?.FeedbackPercent ?? 100);
        VerifyPersonalizationPreserved(personalized, adjusted, force, DirectionalGain(mode));
        SpatialFiles.Save(Path.Combine(SpatialFiles.Folder(d, "room"), "pending.json"), new RoomBackup {
            Endpoint = d.Id, Original = personalized, OriginalHash = ProfileService.Hash(personalized),
            AppliedHash = ProfileService.Hash(adjusted), Mode = mode, LevelPercent = state?.LevelPercent ?? 100, FeedbackPercent = state?.FeedbackPercent ?? 100,GainCorrection=state?.GainCorrection??false
        });
        return adjusted;
    }
    public static void VerifyPersonalizationPreserved(byte[] before, byte[] after, bool force = false, double directionalGain = 1)
    {
        if (UsesBuiltInTuning(before) || UsesBuiltInTuning(after)) {
            if (!before.SequenceEqual(after)) throw new ProfileOperationException("The built-in Dolby initialization changed outside its saved tuning baseline.");
            return;
        }
        var a = Parse(before, force); var b = Parse(after, force);
        if (a.Start != b.Start || before.Length != after.Length) throw new ProfileOperationException("The personalized profile changed. Apply a room setting to save a new baseline.");
        if (directionalGain is not (1 or .5 or .25)) throw new ArgumentOutOfRangeException(nameof(directionalGain));
        if (DolbyLegacyRoom.IsLegacy(a.Values) || DolbyLegacyRoom.IsLegacy(b.Values)) {
            var fields = DolbyLegacyRoom.Parse(a.Values).Fields; _ = DolbyLegacyRoom.Parse(b.Values);
            var allowed = new HashSet<int> { 1 };
            foreach (int id in new[] { 19, 21 }.Concat(directionalGain == 1 ? Array.Empty<int>() : new[] { 34, 35 })) {
                var field = fields[id];
                for (int i = field.Start; i < field.Start + field.Count; i++) allowed.Add(i);
                if (id is 34 or 35) for (int i = field.Start; i < field.Start + field.Count; i++)
                    if (b.Values[i] != (int)Math.Round((short)a.Values[i] * directionalGain, MidpointRounding.ToEven)) throw new ProfileOperationException("The experimental directional gain does not match the saved profile.");
            }
            for (int i = 0; i < before.Length; i++) if (!(i >= a.Start && i < a.Start + a.Values.Length * 4 && allowed.Contains((i - a.Start) / 4)) && before[i] != after[i])
                throw new ProfileOperationException("The Dolby tuning changed outside its room fields.");
            return;
        }
        if (directionalGain != 1)
            for (int lane = 4500; lane < 15588; lane++)
                if (b.Values[lane] != (int)Math.Round((short)a.Values[lane] * directionalGain, MidpointRounding.ToEven))
                    throw new ProfileOperationException("The experimental directional gain does not match the saved profile.");
        for (int i = 0; i < before.Length; i++) {
            int lane = (i - a.Start) / 4;
            bool roomField = i >= a.Start && (lane == 1 || lane == 15689 || lane >= 1050 && lane < 1562 || lane >= 1578 && lane < 1834 || directionalGain != 1 && lane >= 4500 && lane < 15588);
            if (!roomField && before[i] != after[i]) throw new ProfileOperationException("The personalized profile changed. Apply a room setting to save a new baseline.");
        }
    }
}
