using AtmosProfile;
using System.Text.Json;
internal static class EqNativeTests
{
    public static void Run(string[] args)
    {
        var d=AudioPlatform.Devices().Single(d=>d.Id==args[1]);string repo=args[2],output=args[3];Directory.CreateDirectory(output);
        ProfileService.ToolRoot=DtsProfileService.ToolRoot=repo;
        byte[] runtime,device;uint counter;
        using(var s=new SpatialStore(d,DtsProfileService.Context,false,false)){runtime=s.Blob(DtsProfileService.Runtime);device=s.Blob(DtsProfileService.Device);counter=s.Counter(DtsProfileService.Counter);}
        var spatial=AudioPlatform.Spatial(d);var snapshot=SpatialFiles.Snapshot(d,DtsProfileService.Context);
        File.WriteAllText(Path.Combine(output,"before.json"),JsonSerializer.Serialize(new{runtime,device,counter,spatial,snapshot}));
        SpatialFiles.TestRoot=ProfileService.TestDataRoot=Path.Combine(output,"state");HeadphoneEq.TestFolder=Path.Combine(output,"eq");
        var passed=new List<string>();void Check(bool value,string name){if(!value)throw new Exception(name);passed.Add(name);}
        byte[] eqBaseline=device;
        byte[] CurrentDevice(){using var s=new SpatialStore(d,DtsProfileService.Context,false,false);return s.Blob(DtsProfileService.Device);}
        string CurrentRuntimeHash(){using var s=new SpatialStore(d,DtsProfileService.Context,false,false);return ProfileService.Hash(s.Blob(DtsProfileService.Runtime));}
        void Capture(string label){if(args.Length<5 || args[4].StartsWith("--",StringComparison.Ordinal))return;string folder=Path.Combine(output,label);Directory.CreateDirectory(folder);string root=Path.GetFullPath(Path.Combine(repo,"../.."));DtsProfileService.RunTool(args[4],"--record",folder,Path.Combine(output,"grid.csv"),Path.Combine(root,"SpatialRendererProfileImport/dts/prepared_capture_20261014/stimulus.f32"),"paired","DTS",CurrentRuntimeHash()).GetAwaiter().GetResult();}
        try{
            DtsProfileService.SelectProvider(d,DtsProfileService.Provider,"DTS");
            if(args.Contains("--empty-device")) {
                var empty=DtsProfileService.EqOff(device)[..56];BitConverter.GetBytes(56u).CopyTo(empty,0);
                eqBaseline=empty;
                Check(DtsProfileService.ValidateNative(empty,false,true),"Installed native DTS encoder accepts the 56-byte no-EQ payload");
                using var s=new SpatialStore(d,DtsProfileService.Context,true,false);s.Write(DtsProfileService.Device,empty);
            }
            DtsProfileService.ChangeEq(d,"Off",true);if(args.Contains("--spacious"))DtsProfileService.ChangeRoom(d,"Spacious");Capture("eq_off");
            var flat=HeadphoneEq.Import("Preamp: -6 dB","Native test");HeadphoneEq.Save(flat);
            var desired=HeadphoneEq.DtsDevice(device,flat);File.WriteAllBytes(Path.Combine(output,"generated-eq-device.bin"),desired);Check(DtsProfileService.ValidateNative(desired,false,true),"Native DTS encoder accepts the generated RDEV EQ");
            DtsProfileService.ChangeEq(d,"Profile:"+flat.Id);Check(CurrentDevice().SequenceEqual(desired),"Uploaded EQ has exact native device readback");Capture("flat_minus6");
            Check(DtsProfileService.Inspect(d).EqMode=="Custom","DTS status distinguishes app EQ from vendor EQ");
            var peak=HeadphoneEq.Import("Filter 1: ON PK Fc 1000 Hz Gain -6 dB Q 1","Native peak test");HeadphoneEq.Save(peak);
            DtsProfileService.ChangeEq(d,"Profile:"+peak.Id);Capture("peak_minus6");
            var baked=CurrentDevice();DtsProfileService.ChangeRoom(d,"Balanced");Check(CurrentDevice().SequenceEqual(baked),"Changing DTS room preserves uploaded EQ exactly");
            foreach(string mode in new[]{"Spacious","Spacious200","Spacious400"}) {
                DtsProfileService.ChangeRoom(d,mode);
                using var current=new SpatialStore(d,DtsProfileService.Context,false,false);var wet=current.Blob(DtsProfileService.Runtime);
                var sends=DtsRoomService.Sends(wet);var native=DtsRoomService.InstalledPreset("Spacious");var source=DtsRoomService.Sends(native);int scale=mode=="Spacious200"?2:mode=="Spacious400"?4:1;
                Check(Enumerable.Range(0,sends.Length/4).All(i=>BitConverter.ToSingle(wet,sends.Offset+4*i)==BitConverter.ToSingle(native,source.Offset+4*i)*scale),mode+" uses actual native Spacious sends at the requested multiplier");
                Check(CurrentDevice().SequenceEqual(baked),mode+" preserves uploaded EQ");
                Check(wet.AsSpan(132,sends.Offset-132).SequenceEqual(runtime.AsSpan(132,sends.Offset-132)),mode+" preserves the entire FIR bank");
                Capture(mode.ToLowerInvariant());
            }
            string candidate=Path.Combine(output,"candidate.bin");File.WriteAllBytes(candidate,runtime);DtsProfileService.Change(d,candidate,true,true);
            Check(CurrentDevice().SequenceEqual(baked),"Reapplying a DTS SOFA carrier retains the selected EQ snapshot");
            HeadphoneEq.Delete(peak);DtsProfileService.Change(d,candidate,true,true);
            Check(CurrentDevice().SequenceEqual(baked),"Deleting a library preset does not invalidate the applied EQ snapshot");
            DtsProfileService.AfterWriteTest=()=>throw new IOException("Injected EQ failure");
            try{DtsProfileService.ChangeEq(d,"Off");throw new Exception("Failure hook missed");}catch(IOException){passed.Add("Native EQ failure invokes rollback");}finally{DtsProfileService.AfterWriteTest=null;}
            Check(CurrentDevice().SequenceEqual(baked),"Failed EQ change restores exact device EQ bytes");
            DtsProfileService.ChangeEq(d,"Off");Check(CurrentDevice().SequenceEqual(DtsProfileService.EqOff(eqBaseline)),"EQ off removes the custom filters");
            HeadphoneEq.Save(peak);DtsProfileService.ChangeEq(d,"Profile:"+peak.Id);
            DtsProfileService.Change(d,null,false);
            DtsProfileService.ChangeRoom(d,"Balanced");
            Check(DtsProfileService.Inspect(d).EqMode!="Custom","Room changes after profile restore do not mislabel restored vendor EQ as an uploaded profile");
        }finally{
            DtsProfileService.AfterWriteTest=null;
            using(var s=new SpatialStore(d,DtsProfileService.Context,true,false)){s.Write(DtsProfileService.Runtime,runtime);s.Write(DtsProfileService.Device,device);s.Write(DtsProfileService.Counter,counter);}
            DtsProfileService.SelectProvider(d,spatial.DefaultSpatialAudioFormat,"original provider");SpatialFiles.VerifyUnchanged(snapshot,SpatialFiles.Snapshot(d,DtsProfileService.Context));
            Check(AudioPlatform.Spatial(d)==spatial,"Native EQ tests restore the original provider and complete DTS context");
            SpatialFiles.TestRoot=ProfileService.TestDataRoot=HeadphoneEq.TestFolder=null;
            File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(passed));
        }
        Console.WriteLine(JsonSerializer.Serialize(passed));
    }
}
