using AtmosProfile;
using System.Text.Json;

internal static class ReviewTests
{
    public static void Run()
    {
        var passed = new List<string>();
        void Assert(bool value, string name) { if (!value) throw new Exception(name); passed.Add(name); }
        void Reject<T>(Action action, string name) where T : Exception {
            try { action(); } catch (T) { passed.Add(name); return; }
            throw new Exception(name);
        }
        byte[] Spac(int samples = 8) {
            const int count = 2;
            int table = 96 + (samples + 2) * count * 8, end = 36 + table;
            var data = new byte[end + 24 + count * 16];
            void U(int offset, uint value) => BitConverter.GetBytes(value).CopyTo(data, offset);
            U(0, (uint)data.Length); U(4, 0x43415053); U(8, 2); U(12, (uint)(24 + table)); U(16, 0x4e484352);
            U(20, 2); U(24, 48000); U(28, count); U(32, 1); U(36, (uint)table); U(40, (uint)samples); U(44, count);
            U(end, 24 + count * 16); U(end + 4, 0x4e415052); U(end + 8, count);
            U(end + 24 + 20, BitConverter.SingleToUInt32Bits(-1));
            return data;
        }
        var emptyInitialization = Convert.FromHexString("0101010001000000000000000100");
        var fixtureProfile = new ProfileFile("Test", "1.1.0", "Test", [1, 2, 3]);
        Assert(ProfileService.CompatibilityWarning([]) != null, "Missing Dolby initialization exposes a compatibility warning");
        Reject<ProfileWarningException>(() => ProfileService.PreparePersonalization(fixtureProfile, [], false), "Missing Dolby initialization requires explicit force");
        Assert(ProfileService.PreparePersonalization(fixtureProfile, [], true).SequenceEqual(fixtureProfile.ApplyTo(emptyInitialization)), "Force initializes a missing Dolby property from a device-independent envelope");
        byte[] opaqueInitialization = [255, 12, 34];
        Reject<ProfileWarningException>(() => ProfileService.PreparePersonalization(fixtureProfile, opaqueInitialization, false), "Unfamiliar Dolby framing requests an override");
        Assert(ProfileService.PreparePersonalization(fixtureProfile, opaqueInitialization, true).SequenceEqual(fixtureProfile.ApplyTo(emptyInitialization)), "Force replaces unfamiliar initialization without interpreting its bytes");
        Assert(opaqueInitialization.SequenceEqual(new byte[] {255,12,34}), "Override leaves the original unfamiliar bytes unchanged for backup");
        Assert(ProfileService.CompatibilityWarning(emptyInitialization, true) != null, "Additional Dolby configuration exposes a warning rather than a fatal error");
        Assert(ProfileService.CompatibilityWarning(emptyInitialization) == null, "Known Dolby initialization needs no override");
        SpatialFiles.VerifyUnchanged(new() { ["keep"] = "a" }, new() { ["keep"] = "a", ["allowed"] = "b" }, "allowed");
        SpatialFiles.VerifyUnchanged(new() { ["keep"] = "a", ["allowed"] = "b" }, new() { ["keep"] = "a" }, "allowed");
        Assert(true, "Snapshot verification permits creating or removing only the explicitly allowed property");
        Reject<ProfileOperationException>(() => SpatialFiles.VerifyUnchanged(new() { ["keep"] = "a" }, new() { ["keep"] = "a", ["unexpected"] = "b" }, "allowed"), "Snapshot verification still rejects unrelated added properties");
        Reject<ProfileOperationException>(() => SpatialFiles.VerifyUnchanged(new() { ["keep"] = "a", ["unexpected"] = "b" }, new() { ["keep"] = "a" }, "allowed"), "Snapshot verification still rejects unrelated removed properties");
        var original = Spac(); var applied = Spac(16); applied[135] = 0x3f;
        DtsProfileService.VerifyTemplate(original, applied);
        Assert(true, "DTS template check permits new FIRs and response lengths");
        var otherGrid = applied.ToArray(); otherGrid[^1] ^= 1;
        Reject<ProfileWarningException>(() => DtsProfileService.VerifyTemplate(original, otherGrid), "Changed DTS grid requests explicit override");
        DtsProfileService.VerifyTemplate(original, otherGrid, true);
        Assert(true, "Explicit override permits a structurally valid changed DTS grid");
        Reject<ProfileOperationException>(() => DtsProfileService.VerifyTemplate(original, [], true), "Override still rejects malformed DTS data");
        Assert(DtsProfileService.RequireNativeValidation(DtsProfileService.EncoderHash, false), "Known DTS encoder uses native validation");
        Reject<ProfileWarningException>(() => DtsProfileService.RequireNativeValidation("different-version", false), "Unknown DTS encoder requests override");
        Assert(!DtsProfileService.RequireNativeValidation("different-version", true), "Unknown DTS encoder override never calls the private native offset");
        var device = new byte[] { 1, 2, 3 };
        var state = new DtsBackup { OriginalHash = ProfileService.Hash(original), AppliedHash = ProfileService.Hash(applied), DeviceHash = ProfileService.Hash(device), Counter = uint.MaxValue, AppliedCounter = 0 };
        Assert(DtsProfileService.MatchesState(state, original, device, uint.MaxValue), "Original DTS state matches");
        Assert(DtsProfileService.MatchesState(state, applied, device, 0), "Applied DTS state matches after counter wrap");
        Assert(!DtsProfileService.MatchesState(state, applied, device, 1), "External DTS counter change is detected");
        Assert(!DtsProfileService.MatchesState(state, applied, [1], 0), "External DTS device data change is detected");
        Assert(!DtsProfileService.MatchesState(state, otherGrid, device, 0), "External DTS runtime change is detected");
        Assert(SpatialFiles.CounterMatches(-1, Microsoft.Win32.RegistryValueKind.DWord, uint.MaxValue), "Persisted DTS counter supports unsigned DWORD values");
        Assert(!SpatialFiles.CounterMatches(1, Microsoft.Win32.RegistryValueKind.DWord, 2), "Persisted DTS counter mismatch is rejected");
        Assert(!SpatialFiles.CounterMatches(1L, Microsoft.Win32.RegistryValueKind.QWord, 1), "Unexpected DTS counter registry type is rejected");
        var inactive = DtsProfileService.Describe(null, original, device, 1, false);
        Assert(!inactive.Custom && inactive.Message.Contains("is not active") && !inactive.Message.Contains("verified"), "DTS status does not claim installation verification when inactive");
        var active = DtsProfileService.Describe(state, applied, device, 0, true);
        Assert(active.Custom && active.Message.Contains("active") && active.Message.Contains("Custom profile loaded"), "DTS status separates active provider from custom profile presence");
        Assert(DtsProfileService.Describe(state, applied, device, 1, true).Message.Contains("changed outside"), "DTS status reports unexpected state rather than success");
        state.Enabled = false;
        Assert(!DtsProfileService.Describe(state, applied, device, 0, true).Custom, "A stock room-only change is not labeled personalized");
        state.Enabled = true;
        state.OriginalHash = state.AppliedHash;
        Assert(DtsProfileService.Describe(state, applied, device, 0, true).Custom, "Explicit DTS On survives an identical original and applied hash");
        var roomTemplate = original.ToArray(); var firstSends = DtsRoomService.Sends(roomTemplate);
        BitConverter.GetBytes(.25f).CopyTo(roomTemplate, firstSends.Offset);
        var roomCombined = DtsRoomService.CopyRoom(applied, roomTemplate); var finalSends = DtsRoomService.Sends(applied);
        Assert(roomCombined.AsSpan(finalSends.Offset, finalSends.Length).SequenceEqual(roomTemplate.AsSpan(firstSends.Offset, firstSends.Length)) && roomCombined.AsSpan(0, finalSends.Offset).SequenceEqual(applied.AsSpan(0, finalSends.Offset)) && roomCombined.AsSpan(finalSends.Offset + finalSends.Length).SequenceEqual(applied.AsSpan(finalSends.Offset + finalSends.Length)), "Room copying preserves every byte outside first sends");
        Reject<ProfileOperationException>(() => DtsRoomService.Apply(applied, original, "Unsupported", true, out _), "Unknown room labels rejected under override");
        var mutedRoom = DtsRoomService.Apply(roomCombined, original, "Off", false, out _);
        Assert(mutedRoom.AsSpan(finalSends.Offset, finalSends.Length).IndexOfAnyExcept((byte)0) < 0 && mutedRoom.AsSpan(finalSends.Offset + finalSends.Length).SequenceEqual(roomCombined.AsSpan(finalSends.Offset + finalSends.Length)), "DTS room Off preserves unresolved second sends and panner");
        Reject<ProfileWarningException>(() => DtsRoomService.Apply(applied, original, "Balanced", false, out _), "Unmapped or incompatible installed preset requests an override");
        var fallback = DtsRoomService.Apply(applied, original, "Balanced", true, out var fallbackMode);
        Assert(fallbackMode == "Original" && fallback.SequenceEqual(DtsRoomService.CopyRoom(applied, original)), "Preset override keeps Original sends and reports the actual selection");
        var primary = new BackupRecord { OriginalHash = ProfileService.Hash(original), AppliedHash = ProfileService.Hash(applied) };
        var identicalBaseline = new BackupRecord { OriginalHash = ProfileService.Hash(original), AppliedHash = ProfileService.Hash(original), Enabled = true };
        Assert(ProfileService.PersonalizationEnabled(identicalBaseline, original), "On remains On when the applied profile equals the Off baseline");
        identicalBaseline.Enabled = false;
        Assert(!ProfileService.PersonalizationEnabled(identicalBaseline, original), "Off remains Off with identical applied and baseline bytes");
        DolbyRoomService.UpdatePrimaryHashes(primary, original, otherGrid);
        Assert(primary.RestoredHash == ProfileService.Hash(otherGrid) && primary.AppliedHash == ProfileService.Hash(applied), "Room edit preserves PHRTF Off state and applied hash");
        DolbyRoomService.UpdatePrimaryHashes(primary, otherGrid, device);
        Assert(primary.RestoredHash == ProfileService.Hash(device), "Repeated room edit preserves PHRTF Off state");
        primary.Enabled = true;
        DolbyRoomService.UpdatePrimaryHashes(primary, applied, otherGrid);
        Assert(primary.AppliedHash == ProfileService.Hash(otherGrid) && primary.RestoredHash == ProfileService.Hash(device), "Room edit preserves PHRTF On state and restored hash");

        var root = Path.Combine(Path.GetTempPath(), "AtmosProfileReview_" + Guid.NewGuid().ToString("N"));
        var d = new AudioDevice("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000001}", "", "Test endpoint");
        ProfileService.TestDataRoot = root;
        var folder = Path.Combine(root, "{00000000-0000-0000-0000-000000000001}");
        try {
            Directory.CreateDirectory(folder);
            void Put(string name, string value) { var p = Path.Combine(folder, name); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, value); }
            string Get(string name) => File.ReadAllText(Path.Combine(folder, name));
            Put("original.json", "old-primary"); Put("backups/old.bin", "old-backup");
            Put("room/original.json", "room-original"); Put("room/pending.json", "room-pending"); Put("dts/original.json", "dts-original"); Put("dts/conversions/candidate.bin", "dts-candidate");
            ProfileService.ResetStateFiles(d, () => { Put("original.json", "new-primary"); Put("backups/new.bin", "new-backup"); });
            Assert(Get("original.json") == "new-primary" && !File.Exists(Path.Combine(folder, "backups/old.bin")), "Dolby reset replaces PHRTF state and backups");
            Assert(!Directory.Exists(Path.Combine(folder, "room")), "Dolby reset clears room original and pending state");
            Put("room/original.json", "room-original"); Put("room/pending.json", "room-pending");
            Assert(Get("dts/original.json") == "dts-original" && Get("dts/conversions/candidate.bin") == "dts-candidate", "Reset preserves DTS restore and conversion data");
            Reject<IOException>(() => ProfileService.ResetStateFiles(d, () => { Put("original.json", "failed"); Put("backups/failed.bin", "failed"); throw new IOException("Injected initialization failure"); }), "Reset initialization failure is propagated");
            Assert(Get("original.json") == "new-primary" && Get("backups/new.bin") == "new-backup" && !File.Exists(Path.Combine(folder, "backups/failed.bin")), "Failed reset restores prior primary state exactly");
            using (var locked = new FileStream(Path.Combine(folder, "original.json"), FileMode.Open, FileAccess.Read, FileShare.None)) {
                Reject<IOException>(() => ProfileService.ResetStateFiles(d, () => throw new Exception("Must not initialize")), "Locked PHRTF state stops reset before initialization");
            }
            Assert(Get("original.json") == "new-primary" && Get("backups/new.bin") == "new-backup", "Failed reset staging preserves unmoved files");
            Assert(Get("room/original.json") == "room-original" && Get("dts/original.json") == "dts-original", "Failed Dolby reset restores room state and preserves DTS");
            ProfileService.ResetStateFiles(d, () => Put("dts/original.json", "fresh-dts"), "dts");
            Assert(Get("dts/original.json") == "fresh-dts" && !Directory.Exists(Path.Combine(folder, "dts/conversions")), "DTS reset clears only DTS conversions and saved state");
            Assert(Get("original.json") == "new-primary" && Get("room/original.json") == "room-original", "DTS reset preserves PHRTF and room state");
            Reject<IOException>(() => ProfileService.ResetStateFiles(d, () => { Put("dts/original.json", "failed-dts"); throw new IOException("Injected DTS reset failure"); }, "dts"), "DTS reset initialization failure is propagated");
            Assert(Get("dts/original.json") == "fresh-dts" && Get("room/original.json") == "room-original", "Failed DTS reset restores DTS state and preserves Dolby");
            Put("dts/original.json", "dts-original");
            Reject<ProfileWarningException>(() => DolbyRoomService.LoadForChange(d, [], false), "Corrupt room backup requests an override");
            Assert(DolbyRoomService.LoadForChange(d, [], true) == null, "Room override permits a new baseline after backup corruption");
            Reject<ProfileWarningException>(() => DtsProfileService.LoadForChange(d, false), "Corrupt DTS backup requests an override");
            Assert(DtsProfileService.LoadForChange(d, true) == null, "DTS override permits a new baseline after backup corruption");
            Assert(Get("room/original.json") == "room-original" && Get("dts/original.json") == "dts-original", "Reading corrupt backups for override preserves their original bytes");
            var corruptPrimary = System.Text.Encoding.UTF8.GetBytes("{\"Original\":null}");
            Reject<ProfileWarningException>(() => DolbyRoomService.LoadPrimaryForChange(d, corruptPrimary, false), "Invalid primary backup warns before room mutation");
            Assert(DolbyRoomService.LoadPrimaryForChange(d, corruptPrimary, true) == null, "Room override can proceed without rewriting an invalid primary backup");
        } finally { ProfileService.TestDataRoot = null; if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine(JsonSerializer.Serialize(passed));
    }
}
