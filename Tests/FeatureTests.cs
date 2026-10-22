using AtmosProfile;
using System.Text.Json;

internal static class FeatureTests
{
    public static async Task Run(string[] args)
    {
        var results = new List<string>();
        void Assert(bool value, string name) { if (!value) throw new Exception(name); results.Add(name); }
        void Reject(Action action, string name) { bool rejected = false; try { action(); } catch { rejected = true; } Assert(rejected, name); }
        byte[] Synthetic(int variant)
        {
            var values = Enumerable.Range(0, 15691).Select(i => (i * 97 + variant) % 65536).ToArray();
            int[] header = [0x7072, 0, 1, 1, 0, 64, 3, 0, 1, 25, 20, 11, 36]; Array.Copy(header, values, header.Length);
            int sum = 0xa569; foreach (var v in values.Skip(2)) sum ^= (ushort)v; values[1] = sum;
            using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
            writer.Write(new byte[] { 1, 1, 1, 1, 1 }); writer.Write(0u); writer.Write((byte)0); writer.Write((byte)1); writer.Write((uint)values.Length);
            foreach (var value in values) writer.Write(value); writer.Write(5u); writer.Write(System.Text.Encoding.ASCII.GetBytes("1.1.0")); writer.Write(new byte[] { 0, 1, 1 }); return output.ToArray();
        }
        var original = Synthetic(1);
        foreach (string mode in new[] { "On", "Off", "HalfFeedback", "Restore" }) {
            var changed = DolbyRoomService.Create(original, mode); DolbyRoomService.VerifyPersonalizationPreserved(original, changed);
            var a = DolbyRoomService.Parse(original).Values; var b = DolbyRoomService.Parse(changed).Values;
            Assert(a[1834..15624].SequenceEqual(b[1834..15624]), mode + " preserves all personalized directional coefficients");
            Assert(mode == "Restore" ? changed.SequenceEqual(original) : b[15689] == 0, mode + " uses expected room control");
            if (mode == "Off") Assert(b[1578..1834].All(v => v == 0), "Off mutes room output taps");
            if (mode == "HalfFeedback") Assert(Enumerable.Range(1050, 512).All(i => b[i] == (int)Math.Round((short)a[i] * .5, MidpointRounding.ToEven)), "Half feedback matches signed nearest-even reference");
        }
        Reject(() => DolbyRoomService.Create(original[..^1], "On"), "Truncated room profile rejected");
        var corrupt = original.ToArray(); corrupt[100] ^= 1;
        Reject(() => DolbyRoomService.Create(corrupt, "On"), "Room checksum corruption rejected");
        Reject(() => DolbyRoomService.VerifyPersonalizationPreserved(original, Synthetic(2)), "Restore cannot replace a different personalized profile");
        var unfamiliar = original.ToArray();
        System.Text.Encoding.ASCII.GetBytes("9.9.9").CopyTo(unfamiliar, unfamiliar.Length - 8);
        bool warned = false;
        try { DolbyRoomService.Create(unfamiliar, "Off"); } catch (ProfileWarningException) { warned = true; }
        Assert(warned, "Unfamiliar room version requests explicit override");
        var overridden = DolbyRoomService.Create(unfamiliar, "Off", true);
        DolbyRoomService.VerifyPersonalizationPreserved(unfamiliar, overridden, true);
        Assert(DolbyRoomService.Parse(overridden, true).Values[1578..1834].All(v => v == 0), "Room version override works with supported coefficient layout");
        Reject(() => DolbyRoomService.VerifyPersonalizationPreserved(original, Synthetic(2), true), "Version override cannot replace another listener's coefficients");
        Reject(() => DolbyRoomService.Create(corrupt, "On", true), "Version override still rejects room checksum corruption");
        var otherListener = Synthetic(2);
        var resetRoom = DolbyRoomService.RestoreRoomFields(otherListener, DolbyRoomService.Create(original, "Off"));
        DolbyRoomService.VerifyPersonalizationPreserved(otherListener, resetRoom);
        Assert(DolbyRoomService.Parse(resetRoom).Values[1578..1834].All(v => v == 0), "Dolby reset restores saved room fields onto the current listener");
        Assert(DolbyRoomService.Parse(resetRoom).Values[1834..15624].SequenceEqual(DolbyRoomService.Parse(otherListener).Values[1834..15624]), "Dolby reset cannot replace current directional coefficients");
        var custom = DolbyRoomService.Create(original, "Custom", false, 50, 25);
        var customValues = DolbyRoomService.Parse(custom).Values;
        var originalValues = DolbyRoomService.Parse(original).Values;
        Assert(Enumerable.Range(1578, 256).All(i => customValues[i] == (int)Math.Round((short)originalValues[i] * .5, MidpointRounding.ToEven)), "Custom room level scales only output taps");
        Assert(Enumerable.Range(1050, 512).All(i => customValues[i] == (int)Math.Round((short)originalValues[i] * .25, MidpointRounding.ToEven)), "Custom feedback scales signed coefficients");
        Reject(() => DolbyRoomService.Create(original, "Custom", false, double.NaN, 100), "Nonfinite custom room level rejected");
        Reject(() => DolbyRoomService.Create(original, "Custom", false, 100, 101), "Feedback above original rejected");
        byte[] deviceEq = new byte[104];
        void Word(int offset, uint value) => BitConverter.GetBytes(value).CopyTo(deviceEq, offset);
        Word(0,104); Word(4,0x56454452); Word(8,2); Word(16,40); Word(20,0x51504752); Word(24,2); Word(28,48000); Word(44,1); Word(48,1);
        DtsProfileService.ValidateDevice(deviceEq);
        var eqOff = DtsProfileService.EqOff(deviceEq);
        Assert(eqOff.AsSpan(32,24).ToArray().All(v => v == 0), "DTS EQ Off clears processing delays and filter counts");
        Assert(eqOff.AsSpan(0,32).SequenceEqual(deviceEq.AsSpan(0,32)) && eqOff.AsSpan(56).SequenceEqual(deviceEq.AsSpan(56)), "DTS EQ Off preserves all other device data");
        Reject(() => DtsProfileService.EqOff(deviceEq[..^1]), "Truncated DTS device EQ rejected");
        Reject(() => DolbyRoomService.Create(original, "Custom", false, 200, 100), "Custom room output above 150% is rejected");
        var quadRoom = DolbyRoomService.Create(original, "QuadRoom");
        DolbyRoomService.VerifyPersonalizationPreserved(original, quadRoom, false, .25);
        Assert(DolbyRoomService.RestoreDirectionalGain(quadRoom, original, .25).SequenceEqual(DolbyRoomService.Create(original, "On")), "400% restores full directional gain exactly");
        Reject(() => DolbyRoomService.VerifyPersonalizationPreserved(original, quadRoom, false, .5), "400% cannot pass the 200% gain guard");
        var doubleRoom = DolbyRoomService.Create(original, "DoubleRoom");
        DolbyRoomService.VerifyPersonalizationPreserved(original, doubleRoom, false, .5);
        var doubleValues = DolbyRoomService.Parse(doubleRoom).Values;
        Assert(Enumerable.Range(4500, 11088).All(i => doubleValues[i] == (int)Math.Round((short)originalValues[i] * .5, MidpointRounding.ToEven)), "Experimental 200% halves both signed directional fields with nearest-even rounding");
        Assert(doubleValues[14..1834].SequenceEqual(originalValues[14..1834]), "Experimental 200% preserves all room parameters, taps and feedback");
        Assert(doubleValues[1834..4500].SequenceEqual(originalValues[1834..4500]) && doubleValues[15588..15689].SequenceEqual(originalValues[15588..15689]), "Experimental mode preserves matrices, phase and distance controls");
        Reject(() => DolbyRoomService.VerifyPersonalizationPreserved(original, doubleRoom), "Ordinary room edits still reject directional scaling");
        var otherHalf = DolbyRoomService.Create(Synthetic(2), "DoubleRoom");
        Reject(() => DolbyRoomService.VerifyPersonalizationPreserved(original, otherHalf, true, .5), "Experimental guard rejects another listener even under override");
        var halfWithNewRoom = DolbyRoomService.Create(doubleRoom, "Custom", false, 50, 75);
        var recoveredFullGain = DolbyRoomService.RestoreDirectionalGain(halfWithNewRoom, original);
        DolbyRoomService.VerifyPersonalizationPreserved(original, recoveredFullGain);
        Assert(DolbyRoomService.Parse(recoveredFullGain).Values[1050..1834].SequenceEqual(DolbyRoomService.Parse(halfWithNewRoom).Values[1050..1834]), "Full-gain restoration preserves externally changed room settings");
        Reject(() => DolbyRoomService.RestoreDirectionalGain(otherHalf, original), "Gain restoration cannot overwrite a different listener");
        Reject(() => DtsProfileService.ValidateSpac([]), "Truncated DTS carrier rejected");
        if (args.Length < 4) { Console.WriteLine(JsonSerializer.Serialize(results)); return; }
        string repo = Path.GetFullPath(args[1]); string sofa = Path.GetFullPath(args[2]); string endpoint = args[3];
        DtsProfileService.ToolRoot = repo;
        var d = (await ProfileService.Devices()).Single(v => v.Id == endpoint);
        string isolated = Path.Combine(repo, "release", "feature-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated); SpatialFiles.TestRoot = isolated; ProfileService.TestDataRoot = isolated;
        var initialSpatial = AudioPlatform.Spatial(d);
        byte[] initialRoom;
        using (var store = new SpatialStore(d, ProfileService.Context, false, true)) initialRoom = store.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8");
        byte[] initialDts, initialDevice; uint initialCounter;
        using (var store = new SpatialStore(d, DtsProfileService.Context, false, false)) { initialDts = store.Blob(DtsProfileService.Runtime); initialDevice = store.Blob(DtsProfileService.Device); initialCounter = store.Counter(DtsProfileService.Counter); }
        var initialRoomContext = SpatialFiles.Snapshot(d, ProfileService.Context); var initialDtsContext = SpatialFiles.Snapshot(d, DtsProfileService.Context);
        SpatialFiles.Save(Path.Combine(isolated, "initial-state.json"), new {
            Endpoint = d.Id, Spatial = initialSpatial, Room = initialRoom,
            Dts = initialDts, Device = initialDevice, Counter = initialCounter,
            RoomContext = initialRoomContext, DtsContext = initialDtsContext
        });
        var conversion = await DtsProfileService.Convert(d, sofa);
        var bytes = File.ReadAllBytes(conversion.Path); DtsProfileService.ValidateSpac(bytes); DtsProfileService.ValidateNative(conversion.Path);
        Assert(bytes.Length > 0, "Standalone SOFA conversion and installed native DTS validator pass");
        bytes = DtsRoomService.CopyRoom(bytes, initialDts);
        foreach (string preset in new[] { "Balanced", "Spacious" }) {
            var installed = DtsRoomService.InstalledPreset(preset);
            Assert(installed.Length > 0, "Installed " + preset + " preset decodes with verified hash");
            var combined = DtsRoomService.Apply(bytes, initialDts, preset, false, out var selectedRoom);
            var sends = DtsRoomService.Sends(bytes);
            Assert(combined.AsSpan(0, sends.Offset).SequenceEqual(bytes.AsSpan(0, sends.Offset)) && combined.AsSpan(sends.Offset + sends.Length).SequenceEqual(bytes.AsSpan(sends.Offset + sends.Length)), preset + " changes only first room-send vectors");
            var incompatible = installed.ToArray(); incompatible[48] ^= 1;
            Reject(() => DtsRoomService.CopyRoom(bytes, incompatible), "Incompatible " + preset + " room parameters rejected");
        }
        var originalHash = ProfileService.Hash(initialDts);
        Assert(ProfileService.Hash(File.ReadAllBytes(Path.Combine(Directory.GetParent(Path.GetDirectoryName(conversion.Path)!)!.FullName, "template.bin"))) == originalHash, "Conversion uses selected endpoint template");
        if (!args.Contains("--live")) { Console.WriteLine(JsonSerializer.Serialize(results)); return; }
        bool dtsAttempted = false;
        try {
            await DtsProfileService.RunTool(Path.Combine(repo, "Tools", "SoundVolumeView.exe"), "/SetSpatial", d.Id, AudioPlatform.Dolby);
            for (int attempt = 0; attempt < 40; attempt++) { var active = AudioPlatform.Spatial(d); if (active.ActiveSpatialAudioFormat.Equals(AudioPlatform.Dolby, StringComparison.OrdinalIgnoreCase) && active.DefaultSpatialAudioFormat.Equals(AudioPlatform.Dolby, StringComparison.OrdinalIgnoreCase)) break; await Task.Delay(100); }
            foreach (string mode in new[] { "On", "Off", "HalfFeedback", "Restore" }) {
                _ = DolbyRoomService.Change(d, mode);
                using var store = new SpatialStore(d, ProfileService.Context, false, true);
                var current = store.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8");
                Assert(current.SequenceEqual(DolbyRoomService.Create(initialRoom, mode)), "Native room " + mode + " exact readback");
                DolbyRoomService.VerifyPersonalizationPreserved(initialRoom, current);
                Assert(true, "Native room " + mode + " preserves personalized coefficients");
            }
            string roomState = Path.Combine(SpatialFiles.Folder(d, "room"), "original.json");
            File.WriteAllText(roomState, "invalid room backup");
            bool roomBackupWarned = false;
            try { _ = DolbyRoomService.Change(d, "Off"); } catch (ProfileWarningException) { roomBackupWarned = true; }
            Assert(roomBackupWarned, "Corrupt room backup requests explicit override without writing");
            _ = DolbyRoomService.Change(d, "Off", true);
            _ = DolbyRoomService.Change(d, "Restore");
            Assert(Directory.GetFiles(Path.GetDirectoryName(roomState)!, "original.json.invalid.*").Any(p => File.ReadAllText(p) == "invalid room backup"), "Room baseline override archives the invalid backup");
            using (var store = new SpatialStore(d, ProfileService.Context, false, true)) Assert(store.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8").SequenceEqual(initialRoom), "Room baseline recovery restores original native bytes");
            DolbyRoomService.AfterWriteTest = () => throw new IOException("Injected room failure");
            Reject(() => DolbyRoomService.Change(d, "Off"), "Room write failure reaches rollback");
            DolbyRoomService.AfterWriteTest = null;
            using (var store = new SpatialStore(d, ProfileService.Context, false, true)) Assert(store.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8").SequenceEqual(initialRoom), "Room failure restores exact original bytes");
            _ = DolbyRoomService.Change(d, "Off");
            var newPersonal = initialRoom.ToArray(); var decoded = DolbyRoomService.Parse(newPersonal);
            BitConverter.GetBytes(decoded.Values[2000] ^ 1).CopyTo(newPersonal, decoded.Start + 2000 * 4);
            int sum = 0xa569; for (int i = 2; i < decoded.Values.Length; i++) sum ^= (ushort)BitConverter.ToInt32(newPersonal, decoded.Start + i * 4); BitConverter.GetBytes(sum).CopyTo(newPersonal, decoded.Start + 4);
            var transformed = DolbyRoomService.PreserveSetting(d, newPersonal);
            DolbyRoomService.VerifyPersonalizationPreserved(newPersonal, transformed);
            Assert(DolbyRoomService.Parse(transformed).Values[1578..1834].All(v => v == 0), "Changing PHRTF retains selected room Off");
            // Exercise pending-state promotion exactly as a successful profile write does.
            using (var store = new SpatialStore(d, ProfileService.Context, true, true)) store.Write("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8", transformed);
            _ = DolbyRoomService.Inspect(d);
            _ = DolbyRoomService.Change(d, "On");
            using (var store = new SpatialStore(d, ProfileService.Context, false, true)) Assert(DolbyRoomService.Parse(store.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8")).Values[2000] == (decoded.Values[2000] ^ 1), "Subsequent room On retains newly applied personalized coefficient");
            using (var store = new SpatialStore(d, ProfileService.Context, true, true)) store.Write("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8", initialRoom);
            int profileIndex = Array.IndexOf(args, "--profile");
            if (profileIndex >= 0 && profileIndex + 1 < args.Length) {
                var profile = ProfileFile.Load(args[profileIndex + 1]);
                var guid = Guid.Parse(d.Id[(d.Id.LastIndexOf('.') + 1)..]).ToString("B");
                string realStatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AtmosProfile", guid, "original.json");
                var appState = JsonSerializer.Deserialize<BackupRecord>(File.ReadAllText(realStatePath))!;
                appState.AppliedHash = ProfileService.Hash(initialRoom);
                SpatialFiles.Save(Path.Combine(isolated, guid, "original.json"), appState);
                _ = DolbyRoomService.Change(d, "On", true);
                ProfileService.PersonalizationTransform = DolbyRoomService.PreserveSetting;
                ProfileService.Switch(d, profile, true);
                Assert(DolbyRoomService.Inspect(d) == "Room on.", "Actual PHRTF apply preserves room On");
                _ = DolbyRoomService.Change(d, "Custom", false, 150, 100);
                using (var louder = new SpatialStore(d, ProfileService.Context, false, true)) {
                    var louderBytes = louder.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8");
                    DolbyRoomService.VerifyPersonalizationPreserved(profile.ApplyTo(Convert.FromBase64String(appState.Original)), louderBytes);
                    Assert(DolbyRoomService.Inspect(d) == "Room level: 150%. Feedback: 100%.", "Native 150% room output level applies without changing PHRTF");
                }
                DolbyRoomService.AfterWriteTest = () => { using var dropped = new SpatialStore(d, ProfileService.Context, true, true); dropped.Write("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8", Convert.FromBase64String(appState.Original)); };
                _ = DolbyRoomService.Change(d, "Custom", false, 50, 75);
                DolbyRoomService.AfterWriteTest = null;
                Assert(ProfileService.Inspect(d).Personalized, "Room change automatically reapplies dropped PHRTF");
                ProfileService.Switch(d, profile, true);
                Assert(DolbyRoomService.Inspect(d) == "Room level: 50%. Feedback: 75%.", "PHRTF apply retains custom room settings");
                _ = DolbyRoomService.Change(d, "Off");
                Assert(ProfileService.Inspect(d).Personalized, "Room Off retains PHRTF enabler On state");
                ProfileService.Switch(d, profile, true);
                Assert(DolbyRoomService.Inspect(d) == "Room off.", "Actual PHRTF apply preserves room Off");
                _ = DolbyRoomService.Change(d, "Restore");
                using var personalStore = new SpatialStore(d, ProfileService.Context, false, true);
                Assert(personalStore.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8").SequenceEqual(profile.ApplyTo(Convert.FromBase64String(appState.Original))), "Room Restore retains newly selected profile exactly");
                File.Delete(Path.Combine(SpatialFiles.Folder(d, "room"), "original.json"));
                File.Delete(Path.Combine(SpatialFiles.Folder(d, "room"), "pending.json"));
                ProfileService.Switch(d, profile, true);
                Assert(DolbyRoomService.Inspect(d) == "Room on.", "PHRTF apply defaults room On when no preference exists");
                ProfileService.PersonalizationTransform = null;
                var identical = profile.ApplyTo(initialRoom);
                SpatialFiles.Save(Path.Combine(isolated, guid, "original.json"), new BackupRecord {
                    Endpoint = d.Id, Original = Convert.ToBase64String(identical), OriginalHash = ProfileService.Hash(identical), Enabled = false
                });
                using (var sameStore = new SpatialStore(d, ProfileService.Context, true, true)) sameStore.Write("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8", identical);
                ProfileService.Switch(d, profile, true);
                Assert(ProfileService.Inspect(d).Personalized, "Native On stays On when the selected profile equals the Off baseline");
                ProfileService.Switch(d, null, false);
                Assert(!ProfileService.Inspect(d).Personalized, "Native Off stays Off when the baseline is already personalized");
                SpatialFiles.Save(Path.Combine(SpatialFiles.Folder(d, "room"), "original.json"), new RoomBackup {
                    Endpoint = d.Id, Original = identical, OriginalHash = ProfileService.Hash(identical)
                });
                _ = DolbyRoomService.Change(d, "Off", true);
                byte[] muted = DolbyRoomService.Create(identical, "Off");
                ProfileService.AfterResetWriteTest = () => throw new IOException("Injected Dolby reset failure");
                Reject(() => ProfileService.Reset(d), "Dolby reset failure reaches native rollback");
                ProfileService.AfterResetWriteTest = null;
                using (var sameStore = new SpatialStore(d, ProfileService.Context, false, true)) Assert(sameStore.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8").SequenceEqual(muted), "Failed Dolby reset restores exact prior room bytes");
                _ = ProfileService.Reset(d);
                using (var sameStore = new SpatialStore(d, ProfileService.Context, false, true)) Assert(sameStore.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8").SequenceEqual(identical), "Dolby reset restores saved room settings with current personalized coefficients");
                Assert(!ProfileService.Inspect(d).Personalized && !Directory.Exists(SpatialFiles.Folder(d, "room")), "Dolby reset clears room state and establishes an Off baseline");
                ProfileService.Switch(d, profile, true);
                Assert(ProfileService.Inspect(d).Personalized, "On stays On after reset with the same profile");
            }
            var doubleReference = DolbyRoomService.Create(initialRoom, "On");
            using (var expStore = new SpatialStore(d, ProfileService.Context, true, true)) expStore.Write("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8", doubleReference);
            string experimentalRoomState = Path.Combine(SpatialFiles.Folder(d, "room"), "original.json");
            if (File.Exists(experimentalRoomState)) File.Delete(experimentalRoomState);
            var expPending = Path.Combine(SpatialFiles.Folder(d, "room"), "pending.json"); if (File.Exists(expPending)) File.Delete(expPending);
            _ = DolbyRoomService.Change(d, "DoubleRoom", true);
            byte[] firstDouble;
            using (var expStore = new SpatialStore(d, ProfileService.Context, false, true)) firstDouble = expStore.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8");
            Assert(firstDouble.SequenceEqual(DolbyRoomService.Create(doubleReference, "DoubleRoom")), "Native experimental 200% exact field scaling and readback");
            _ = DolbyRoomService.Change(d, "DoubleRoom");
            using (var expStore = new SpatialStore(d, ProfileService.Context, false, true)) Assert(expStore.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8").SequenceEqual(firstDouble), "Repeated 200% apply does not halve twice");
            _ = DolbyRoomService.Change(d, "QuadRoom");
            byte[] firstQuad;
            using (var expStore = new SpatialStore(d, ProfileService.Context, false, true)) firstQuad = expStore.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8");
            Assert(firstQuad.SequenceEqual(DolbyRoomService.Create(doubleReference, "QuadRoom")), "200% to 400% uses full-gain reference");
            _ = DolbyRoomService.Change(d, "QuadRoom");
            using (var expStore = new SpatialStore(d, ProfileService.Context, false, true)) Assert(expStore.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8").SequenceEqual(firstQuad), "Repeated 400% apply does not accumulate");
            _ = DolbyRoomService.Change(d, "Restore");
            using (var expStore = new SpatialStore(d, ProfileService.Context, false, true)) Assert(expStore.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8").SequenceEqual(doubleReference), "400% Restore returns exact full-gain reference");
            _ = DolbyRoomService.Change(d, "QuadRoom"); ProfileService.Reset(d, true);
            using (var expStore = new SpatialStore(d, ProfileService.Context, false, true)) Assert(expStore.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8").SequenceEqual(doubleReference), "400% reset restores full gain");
            _ = DolbyRoomService.Change(d, "QuadRoom", true); _ = DolbyRoomService.Change(d, "DoubleRoom");
            using (var expStore = new SpatialStore(d, ProfileService.Context, false, true)) Assert(expStore.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8").SequenceEqual(firstDouble), "400% to 200% uses full-gain reference");
            DolbyRoomService.AfterWriteTest = () => throw new IOException("Injected experimental transition failure");
            Reject(() => DolbyRoomService.Change(d, "On"), "Experimental On transition failure reaches rollback");
            DolbyRoomService.AfterWriteTest = null;
            using (var expStore = new SpatialStore(d, ProfileService.Context, false, true)) Assert(expStore.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8").SequenceEqual(firstDouble), "Experimental transition failure restores half-gain bytes exactly");
            _ = DolbyRoomService.Change(d, "On");
            using (var expStore = new SpatialStore(d, ProfileService.Context, false, true)) Assert(expStore.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8").SequenceEqual(doubleReference), "On restores original directional gain exactly");
            _ = DolbyRoomService.Change(d, "DoubleRoom"); _ = DolbyRoomService.Change(d, "Restore");
            using (var expStore = new SpatialStore(d, ProfileService.Context, false, true)) Assert(expStore.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8").SequenceEqual(doubleReference), "Restore restores original full-gain profile exactly");
            _ = DolbyRoomService.Change(d, "DoubleRoom"); ProfileService.Reset(d, true);
            using (var expStore = new SpatialStore(d, ProfileService.Context, false, true)) Assert(expStore.Blob("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8").SequenceEqual(doubleReference), "Reset removes experimental directional attenuation");
            dtsAttempted = true; DtsProfileService.Enable(d);
            uint dtsBaselineCounter;using(var store=new SpatialStore(d,DtsProfileService.Context,false,false))dtsBaselineCounter=store.Counter(DtsProfileService.Counter);
            _ = DtsProfileService.Change(d, conversion.Path, true);
            using (var store = new SpatialStore(d, DtsProfileService.Context, false, false)) Assert(store.Blob(DtsProfileService.Runtime).SequenceEqual(bytes), "Native DTS custom profile exact readback");
            using (var store = new SpatialStore(d, DtsProfileService.Context, false, false)) Assert(store.Blob(DtsProfileService.Device).SequenceEqual(DtsProfileService.EqOff(initialDevice)), "SOFA apply disables device EQ by default");
            foreach (string roomMode in new[] { "Balanced", "Spacious", "Spacious200", "Spacious400", "Off", "Original" }) {
                _ = DtsProfileService.ChangeRoom(d, roomMode);
                var expectedRoom = DtsRoomService.Apply(bytes, initialDts, roomMode, false, out _);
                using var roomStore = new SpatialStore(d, DtsProfileService.Context, false, false);
                Assert(roomStore.Blob(DtsProfileService.Runtime).SequenceEqual(expectedRoom), "Native DTS room " + roomMode + " exact readback");
                Assert(roomStore.Blob(DtsProfileService.Device).SequenceEqual(DtsProfileService.EqOff(initialDevice)) && DtsProfileService.Inspect(d).Custom, "DTS room " + roomMode + " retains personalized FIRs and disabled EQ");
            }
            _ = DtsProfileService.ChangeEq(d, "Original");
            using (var store = new SpatialStore(d, DtsProfileService.Context, false, false)) Assert(store.Blob(DtsProfileService.Device).SequenceEqual(initialDevice) && store.Blob(DtsProfileService.Runtime).SequenceEqual(bytes), "Restoring original EQ preserves the SOFA-derived profile");
            _ = DtsProfileService.ChangeEq(d, "Off");
            using (var store = new SpatialStore(d, DtsProfileService.Context, false, false)) Assert(store.Blob(DtsProfileService.Device).SequenceEqual(DtsProfileService.EqOff(initialDevice)) && store.Blob(DtsProfileService.Runtime).SequenceEqual(bytes), "EQ Off preserves the SOFA-derived profile");
            using (var store = new SpatialStore(d, DtsProfileService.Context, true, false)) store.Write(DtsProfileService.Counter, unchecked(dtsBaselineCounter + 2));
            bool counterWarned = false;
            try { _ = DtsProfileService.Change(d, null, false); } catch (ProfileWarningException) { counterWarned = true; }
            Assert(counterWarned, "External DTS counter requests an override before restoration");
            _ = DtsProfileService.Change(d, null, false, true);
            using (var store = new SpatialStore(d, DtsProfileService.Context, false, false)) Assert(store.Counter(DtsProfileService.Counter) == dtsBaselineCounter && store.Blob(DtsProfileService.Runtime).SequenceEqual(initialDts), "Explicit DTS override restores runtime and counter");
            _ = DtsProfileService.Change(d, conversion.Path, true);
            DtsProfileService.AfterWriteTest = () => throw new IOException("Injected DTS failure");
            Reject(() => DtsProfileService.Change(d, null, false), "DTS write failure reaches rollback");
            DtsProfileService.AfterWriteTest = null;
            using (var store = new SpatialStore(d, DtsProfileService.Context, false, false)) Assert(store.Blob(DtsProfileService.Runtime).SequenceEqual(bytes), "DTS failure restores custom profile");
            _ = DtsProfileService.Change(d, null, false);
            using (var store = new SpatialStore(d, DtsProfileService.Context, false, false)) Assert(store.Blob(DtsProfileService.Runtime).SequenceEqual(initialDts) && store.Counter(DtsProfileService.Counter) == dtsBaselineCounter && store.Blob(DtsProfileService.Device).SequenceEqual(initialDevice), "DTS restore retains original runtime, counter and device data");
            string dtsState = Path.Combine(SpatialFiles.Folder(d, "dts"), "original.json");
            File.WriteAllText(dtsState, "invalid DTS backup");
            bool dtsBackupWarned = false;
            try { _ = DtsProfileService.Change(d, conversion.Path, true); } catch (ProfileWarningException) { dtsBackupWarned = true; }
            Assert(dtsBackupWarned, "Corrupt DTS backup requests explicit override without writing");
            _ = DtsProfileService.Change(d, conversion.Path, true, true);
            _ = DtsProfileService.Change(d, null, false);
            Assert(Directory.GetFiles(Path.GetDirectoryName(dtsState)!, "original.json.invalid.*").Any(p => File.ReadAllText(p) == "invalid DTS backup"), "DTS baseline override archives the invalid backup");
            using (var store = new SpatialStore(d, DtsProfileService.Context, false, false)) Assert(store.Blob(DtsProfileService.Runtime).SequenceEqual(initialDts) && store.Counter(DtsProfileService.Counter) == dtsBaselineCounter, "DTS baseline recovery restores original native bytes and counter");
            _ = DtsProfileService.Change(d, conversion.Path, true);
            DtsProfileService.AfterWriteTest = () => throw new IOException("Injected DTS reset failure");
            Reject(() => DtsProfileService.Reset(d), "DTS reset failure reaches native rollback");
            DtsProfileService.AfterWriteTest = null;
            using (var store = new SpatialStore(d, DtsProfileService.Context, false, false)) Assert(store.Blob(DtsProfileService.Runtime).SequenceEqual(bytes), "Failed DTS reset restores the custom runtime");
            var primaryStatePath = Path.Combine(SpatialFiles.Folder(d, "dts"), "..", "original.json");
            var primaryBeforeReset = File.Exists(primaryStatePath) ? File.ReadAllBytes(primaryStatePath) : null;
            DtsProfileService.Reset(d);
            using (var store = new SpatialStore(d, DtsProfileService.Context, false, false)) Assert(store.Blob(DtsProfileService.Runtime).SequenceEqual(initialDts) && store.Counter(DtsProfileService.Counter) == dtsBaselineCounter, "DTS reset restores the saved runtime and counter");
            Assert(!File.Exists(conversion.Path) && !DtsProfileService.Inspect(d).Custom, "DTS reset clears conversions and custom profile state");
            Assert(primaryBeforeReset == null || File.ReadAllBytes(primaryStatePath).SequenceEqual(primaryBeforeReset), "DTS reset preserves primary Dolby configuration");
        } catch (Exception failure) { Console.Error.WriteLine("Feature failure before cleanup: " + failure); throw; } finally {
            using (var store = new SpatialStore(d, ProfileService.Context, true, true)) store.Write("1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8", initialRoom);
            if (dtsAttempted) {
                using (var store = new SpatialStore(d, DtsProfileService.Context, true, false)) { store.Write(DtsProfileService.Runtime, initialDts); store.Write(DtsProfileService.Device, initialDevice); store.Write(DtsProfileService.Counter, initialCounter); }
            }
            await DtsProfileService.RunTool(Path.Combine(repo, "Tools", "SoundVolumeView.exe"), "/SetSpatial", d.Id, initialSpatial.DefaultSpatialAudioFormat);
            for (int attempt = 0; attempt < 40 && AudioPlatform.Spatial(d) != initialSpatial; attempt++) await Task.Delay(100);
            SpatialFiles.VerifyUnchanged(initialRoomContext, SpatialFiles.Snapshot(d, ProfileService.Context));
            SpatialFiles.VerifyUnchanged(initialDtsContext, SpatialFiles.Snapshot(d, DtsProfileService.Context));
            var final = AudioPlatform.Spatial(d);
            Console.WriteLine(JsonSerializer.Serialize(new { InitialSpatial = initialSpatial, FinalSpatial = final }));
            Assert(final.ActiveSpatialAudioFormat.Equals(initialSpatial.ActiveSpatialAudioFormat, StringComparison.OrdinalIgnoreCase) && final.DefaultSpatialAudioFormat.Equals(initialSpatial.DefaultSpatialAudioFormat, StringComparison.OrdinalIgnoreCase), "Original spatial provider restored");
            Assert(true, "Dolby and DTS context values restored exactly");
            ProfileService.TestDataRoot = null; SpatialFiles.TestRoot = null;
            File.WriteAllText(Path.Combine(isolated, "results.json"), JsonSerializer.Serialize(results));
        }
        Console.WriteLine(JsonSerializer.Serialize(results));
    }
}
