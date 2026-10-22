using AtmosProfile;
using System.Text.Json;
internal static class AtmosEqProbe
{
    const string Guid="1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8";
    public static void Run(string[] args)
    {
        var d=AudioPlatform.Devices().Single(x=>x.Id==args[1]);string repo=args[2],output=args[3];Directory.CreateDirectory(output);
        ProfileService.ToolRoot=DtsProfileService.ToolRoot=repo;
        var initial=ProfileService.ReadInitializationForTest(d);var snapshot=SpatialFiles.Snapshot(d,ProfileService.Context);var spatial=AudioPlatform.Spatial(d);
        using var store=new SpatialStore(d,ProfileService.Context,true,true);var runtime=store.Read(Guid,2);
        File.WriteAllText(Path.Combine(output,"before.json"),JsonSerializer.Serialize(new{initial,Runtime=new{runtime.Type,runtime.Data,Hash=ProfileService.Hash(runtime.Data)},snapshot,spatial}));
        ProfileService.TestDataRoot=SpatialFiles.TestRoot=Path.Combine(output,"state");
        var passed=new List<string>();void Check(bool ok,string name){if(!ok)throw new Exception(name);passed.Add(name);}
        void Capture(string name){if(args[4]=="-")return;string folder=Path.Combine(output,name);Directory.CreateDirectory(folder);string root=Path.GetFullPath(Path.Combine(repo,"../.."));DtsProfileService.RunTool(args[4],"--record",folder,Path.Combine(output,"grid.csv"),Path.Combine(root,"SpatialRendererProfileImport/dts/prepared_capture_20261014/stimulus.f32"),"paired","Dolby",ProfileService.Hash(ProfileService.ReadInitializationForTest(d))).GetAwaiter().GetResult();}
        try {
            DtsProfileService.SelectProvider(d,AudioPlatform.Dolby,"Atmos");
            if(args.Length>5)ProfileService.WriteInitializationForTest(d,File.ReadAllBytes(args[5]));
            var headphone=ProfileService.ReadInitializationForTest(d);
            ProfileService.WriteInitializationForTest(d,DolbyRoomService.Create(headphone,"On",true));
            headphone=ProfileService.ReadInitializationForTest(d);
            var flat=HeadphoneEq.Import("Preamp: 0 dB","Flat");AtmosEqService.Change(d,flat,true);Capture("flat");
            var cut=HeadphoneEq.Import("Preamp: -6 dB","Preamp -6");AtmosEqService.Change(d,cut,true);Capture("minus6");
            Check(ProfileService.ReadInitializationForTest(d).SequenceEqual(headphone),"Atmos EQ leaves PHRTF and room initialization unchanged");
            var peak=HeadphoneEq.Import("Filter 1: ON PK Fc 1000 Hz Gain -6 dB Q 1","Peak -6");AtmosEqService.Change(d,peak,true);Capture("peak");
            var applied=store.Read(Guid,2).Data;File.WriteAllBytes(Path.Combine(output,"applied-runtime.bin"),applied);
            Check(AtmosEqService.Inspect(d).Contains("Peak -6"),"Atmos status identifies the applied EQ profile");
            DolbyRoomService.Change(d,"Off",true);Check(store.Read(Guid,2).Data.SequenceEqual(applied),"Dolby room changes preserve native EQ");
            DolbyRoomService.Change(d,"On",true);Check(store.Read(Guid,2).Data.SequenceEqual(applied),"Restoring Dolby room preserves native EQ");
            AtmosEqService.AfterWriteTest=()=>throw new IOException("Injected EQ failure");
            try{AtmosEqService.Change(d,null,true);throw new Exception("Failure injection missed");}catch(IOException){passed.Add("Injected Atmos EQ failure reaches rollback");}finally{AtmosEqService.AfterWriteTest=null;}
            Check(store.Read(Guid,2).Data.SequenceEqual(applied),"Failed Atmos EQ write restores exact native runtime bytes");
            AtmosEqService.Change(d,null,true);var off=AtmosEqService.Prefix(store.Read(Guid,2).Data,out _);
            Check(off[31].SequenceEqual(new byte[]{1,0}),"Atmos EQ off disables the native equalizer");
            AtmosEqService.Change(d,flat,true);
            foreach(var mode in new[]{"DoubleRoom","QuadRoom"}) {
                int gain=mode=="DoubleRoom"?6:12;
                DolbyRoomService.Change(d,mode,true,100,100,true);
                var corrected=AtmosEqService.Prefix(store.Read(Guid,2).Data,out _);
                Check(Enumerable.Range(0,20).All(i=>BitConverter.ToInt32(corrected[32],9+8*i)==gain*16),mode+" applies native gain correction");
                Check(DolbyRoomService.Inspect(d).Contains("+"+gain),mode+" status reports the actual gain correction");
                Capture(mode.ToLowerInvariant());
                AtmosEqService.Change(d,cut,true);
                var combined=AtmosEqService.Prefix(store.Read(Guid,2).Data,out _);
                Check(Enumerable.Range(0,20).All(i=>BitConverter.ToInt32(combined[32],9+8*i)==(gain-6)*16),mode+" combines new uploaded EQ with existing room correction");
                var beforeFailure=store.Read(Guid,2).Data;
                DolbyRoomService.AfterWriteTest=()=>throw new IOException("Injected room failure");
                try{DolbyRoomService.Change(d,"On",true);throw new Exception("Room injection missed");}catch(IOException){passed.Add(mode+" room failure invokes rollback");}finally{DolbyRoomService.AfterWriteTest=null;}
                Check(AtmosEqService.RoomGain(d)==gain && store.Read(Guid,2).Data.SequenceEqual(beforeFailure),mode+" failure preserves room correction and uploaded EQ");
                var roomBeforeFailure=ProfileService.ReadInitializationForTest(d);
                AtmosEqService.AfterWriteTest=()=>throw new IOException("Injected combined EQ failure");
                try{DolbyRoomService.Change(d,"On",true);throw new Exception("Combined injection missed");}catch(IOException){passed.Add(mode+" combined room/EQ failure invokes rollback");}finally{AtmosEqService.AfterWriteTest=null;}
                Check(ProfileService.ReadInitializationForTest(d).SequenceEqual(roomBeforeFailure)&&store.Read(Guid,2).Data.SequenceEqual(beforeFailure)&&AtmosEqService.RoomGain(d)==gain,mode+" combined failure restores exact initialization, EQ, and gain state");
                DolbyRoomService.Change(d,"On",true);
                var restored=AtmosEqService.Prefix(store.Read(Guid,2).Data,out _);
                Check(AtmosEqService.RoomGain(d)==0&&Enumerable.Range(0,20).All(i=>BitConverter.ToInt32(restored[32],9+8*i)==-96),"On removes "+mode+" correction and preserves uploaded EQ");
                AtmosEqService.Change(d,flat,true);
            }
            DolbyRoomService.Change(d,"QuadRoom",true,100,100,true);
            ProfileService.Reset(d,true);
            Check(AtmosEqService.RoomGain(d)==0&&AtmosEqService.Inspect(d).Contains("Flat"),"Dolby reset removes room gain and preserves independent uploaded EQ");

            if(args.Length>6) {
                var profile=ProfileFile.Load(args[6]);ProfileService.PersonalizationTransform=DolbyRoomService.PreserveSetting;
                ProfileService.Switch(d,profile,true,force:true);DolbyRoomService.Change(d,"QuadRoom",true,100,100,true);
                ProfileService.Switch(d,null,false,force:true);
                Check(AtmosEqService.RoomGain(d)==0,"PHRTF off removes the room correction when restoring the original full-gain profile");
                ProfileService.Switch(d,profile,true,force:true);
                Check(AtmosEqService.RoomGain(d)==12,"PHRTF on restores the selected room correction with the attenuated directional profile");
                DolbyRoomService.Change(d,"On",true);ProfileService.PersonalizationTransform=null;
            }
            string eqState=Path.Combine(SpatialFiles.Folder(d,"atmos-eq"),"original.json");var beforeOverride=store.Read(Guid,2).Data;
            File.WriteAllText(eqState,"invalid EQ backup");
            try{AtmosEqService.Change(d,cut);throw new Exception("Corrupt EQ state failed to warn");}catch(ProfileWarningException){passed.Add("Corrupt Atmos EQ backup offers an explicit override");}
            Check(store.Read(Guid,2).Data.SequenceEqual(beforeOverride),"Unapproved EQ backup override leaves runtime unchanged");
            AtmosEqService.Change(d,cut,true);
            Check(Directory.GetFiles(Path.GetDirectoryName(eqState)!,"original.json.invalid.*").Length==1,"Approved EQ backup override retains the invalid record");
        } finally {
            AtmosEqService.AfterWriteTest=null;
            DolbyRoomService.AfterWriteTest=null;
            ProfileService.PersonalizationTransform=null;
            if(runtime.Type==0)store.Clear(Guid,2);else store.Write(Guid,runtime.Data,2);
            ProfileService.WriteInitializationForTest(d,initial);
            DtsProfileService.SelectProvider(d,spatial.DefaultSpatialAudioFormat,"starting provider");
            SpatialFiles.VerifyUnchanged(snapshot,SpatialFiles.Snapshot(d,ProfileService.Context));
            if(AudioPlatform.Spatial(d)!=spatial || !ProfileService.ReadInitializationForTest(d).SequenceEqual(initial))throw new Exception("Starting audio state restoration failed");
            ProfileService.TestDataRoot=SpatialFiles.TestRoot=null;
            passed.Add("Exact starting runtime, initialization, property context and provider restored");
            File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(passed));
            Console.WriteLine("Exact starting runtime, initialization, property context and provider restored.");
        }
    }
}
