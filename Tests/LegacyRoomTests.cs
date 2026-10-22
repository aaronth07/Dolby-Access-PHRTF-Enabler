using AtmosProfile;
using System.Text.Json;

internal static class LegacyRoomTests
{
    internal static void Run(string[] args)
    {
        var passed = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); passed.Add(name); }
        void Reject(Action action, string name) { try { action(); } catch (ProfileOperationException) { passed.Add(name); return; } throw new Exception(name); }
        byte[] implicitTuning = [1,1,1,0,1,0,0,0,0,0,0,0,1,0];
        DolbyRoomService.VerifyPersonalizationPreserved(implicitTuning,implicitTuning.ToArray());
        Check(true,"Unchanged implicit initialization passes exact preservation check");
        var alteredInitialization=implicitTuning.ToArray();alteredInitialization[5]=1;
        Reject(()=>DolbyRoomService.VerifyPersonalizationPreserved(implicitTuning,alteredInitialization),"Implicit initialization flags cannot be mistaken for editable coefficient lanes");
        var words = new List<int> { 0x8042, 0, 0x8003, 2 };
        void Vector(int id, int count) { words.Add(0xc000 | id); words.Add(count); words.AddRange(Enumerable.Range(0, count).Select(i => (i % 2 == 0 ? 1 : -1) * (i + 1) * 100)); }
        Vector(19, 16); Vector(21, 8); Vector(34, 20); Vector(35, 20);
        var values = words.ToArray(); DolbyLegacyRoom.Checksum(values);
        byte[] baseline = DolbyLegacyRoom.Insert(implicitTuning, values);
        Check(!ProfileFile.HasPersonalization(baseline), "Default tuning expansion uses the generic field and keeps personalization absent");
        foreach (string mode in new[] { "On", "Off", "HalfFeedback", "Custom", "DoubleRoom", "QuadRoom" }) {
            var changed = DolbyRoomService.Create(baseline, mode, false, 125, 25);
            double directional = mode == "DoubleRoom" ? .5 : mode == "QuadRoom" ? .25 : 1;
            DolbyRoomService.VerifyPersonalizationPreserved(baseline, changed, false, directional);
            var a = DolbyRoomService.Parse(changed).Values; var table = DolbyLegacyRoom.Parse(a);
            var taps = table.Fields[21]; var feedback = table.Fields[19];
            double level = mode == "Off" ? 0 : mode == "Custom" ? 1.25 : 1;
            double loop = mode == "HalfFeedback" ? .5 : mode == "Custom" ? .25 : 1;
            Check(Enumerable.Range(taps.Start,taps.Count).All(i => a[i] == (int)Math.Round((short)values[i] * level,MidpointRounding.ToEven)), mode + " scales only the mapped legacy room output taps");
            Check(Enumerable.Range(feedback.Start,feedback.Count).All(i => a[i] == (int)Math.Round((short)values[i] * loop,MidpointRounding.ToEven)), mode + " retains the requested legacy feedback scaling");
            if (directional != 1) Check(DolbyRoomService.RestoreDirectionalGain(changed,baseline,directional).SequenceEqual(baseline), mode + " restores stock directional coefficients exactly");
            Check(DolbyRoomService.RestoreRoomFields(changed,baseline).SequenceEqual(directional == 1 ? baseline : DolbyRoomService.Create(baseline,mode)), mode + " room restoration retains directional gain selection");
        }
        var corrupt = values.ToArray(); corrupt[6] ^= 1;
        Reject(() => DolbyLegacyRoom.Parse(corrupt), "Legacy checksum corruption is rejected");
        Reject(() => DolbyLegacyRoom.Change(values,"Custom",double.NaN,50), "Legacy custom parameters reject nonfinite values");
        var overflow = values.ToArray(); overflow[DolbyLegacyRoom.Parse(values).Fields[21].Start] = short.MaxValue; DolbyLegacyRoom.Checksum(overflow);
        Reject(() => DolbyLegacyRoom.Change(overflow,"Custom",150,100), "Legacy room output overflow is rejected instead of clipped");
        Reject(() => DolbyRoomService.VerifyPersonalizationPreserved(baseline,DolbyRoomService.Create(baseline,"DoubleRoom")), "Ordinary legacy room edits cannot alter direct coefficients");
        string testFolder = Path.Combine(Path.GetTempPath(), "DolbyLegacyRoomTests-" + Guid.NewGuid().ToString("N"));
        string? previousRoot = SpatialFiles.TestRoot;
        try {
            SpatialFiles.TestRoot = testFolder;
            var device = new AudioDevice("{0.0.0.00000000}.{" + Guid.NewGuid() + "}", "", "Test device");
            var attenuated = DolbyRoomService.Create(baseline,"QuadRoom");
            string statePath = Path.Combine(SpatialFiles.Folder(device,"room"),"original.json");
            var state = new RoomBackup { Endpoint=device.Id, Original=implicitTuning, OriginalHash=ProfileService.Hash(implicitTuning), DefaultTuning=baseline, DefaultTuningHash=ProfileService.Hash(baseline), AppliedHash=ProfileService.Hash(attenuated), Mode="QuadRoom" };
            SpatialFiles.Save(statePath,state);
            Check(DolbyRoomService.LoadForChange(device,attenuated,false)?.Original.SequenceEqual(implicitTuning)==true,"Saved stock room state retains exact implicit initialization for Restore");
            Check(DolbyRoomService.PrepareReset(device,attenuated,false).SequenceEqual(baseline),"Stock reset removes room and directional changes using the saved full-gain template");
            state.DefaultTuningHash="invalid";SpatialFiles.Save(statePath,state);
            bool warned=false;try{DolbyRoomService.LoadForChange(device,attenuated,false);}catch(ProfileWarningException){warned=true;}
            Check(warned,"Corrupt stock tuning backup requests the existing force override");
            Check(DolbyRoomService.LoadForChange(device,attenuated,true)==null,"Force permits a new stock room baseline after backup corruption");
        } finally { SpatialFiles.TestRoot=previousRoot;if(Directory.Exists(testFolder))Directory.Delete(testFolder,true); }
        if (args.Length == 2) {
            string folder = Path.GetFullPath(args[1]); Directory.CreateDirectory(folder);
            var installed = DolbyLegacyRoom.WithDefaultTuning(implicitTuning);
            Check(DolbyRoomService.Create(implicitTuning,"Off").SequenceEqual(DolbyRoomService.Create(installed,"Off")),"Implicit stock Room Off reads installed default tuning and uses its native generic representation");
            Check(!ProfileFile.HasPersonalization(installed), "Installed stock resource expands without adding a personalized profile");
            Check(DolbyRoomService.Create(implicitTuning,"Restore").SequenceEqual(implicitTuning), "Restoring implicit tuning retains its exact initialization bytes");
            File.WriteAllBytes(Path.Combine(folder,"Original.bin"),installed);
            foreach (string mode in new[]{"On","Off","HalfFeedback","Custom","DoubleRoom","QuadRoom"}) {
                var changed=DolbyRoomService.Create(installed,mode,false,125,25);
                DolbyRoomService.VerifyPersonalizationPreserved(installed,changed,false,mode=="DoubleRoom"?.5:mode=="QuadRoom"?.25:1);
                File.WriteAllBytes(Path.Combine(folder,mode+".bin"),changed);
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(passed));
    }
}
