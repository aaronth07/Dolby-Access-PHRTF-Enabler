using System.Text.Json;

namespace AtmosProfile;

internal sealed record AtmosEqCurve(int[] Frequencies, int[] Gains, double MaximumErrorDb);
internal sealed record AtmosEqProperty(string Guid, ushort Type, byte[] Data, string OriginalHash, string AppliedHash);
internal sealed class AtmosEqBackup
{
    public string Endpoint { get; set; } = "";
    public AtmosEqProperty[] Properties { get; set; } = [];
    public HeadphoneEqProfile? Profile { get; set; }
    public bool HasEqSelection { get; set; }
    public double RoomGainDb { get; set; }
}
internal sealed record AtmosEqSnapshot(AudioDevice Device, AtmosEqProperty[] Properties, byte[]? State);

internal static class AtmosEqService
{
    internal const string Primary = "1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8", Alternate = "70b95986-9ee3-4b43-acb2-de7aeb1a9509";
    internal static Action? AfterWriteTest { get; set; }
    static string StatePath(AudioDevice d) => Path.Combine(SpatialFiles.Folder(d,"atmos-eq"),"original.json");
    internal static AtmosEqCurve Compile(HeadphoneEqProfile profile,double additionalGainDb=0)
    {
        HeadphoneEq.Validate(profile);
        if(profile.Bands.Any(b=>b.Type=="AP")) throw new ProfileOperationException("Atmos native EQ cannot reproduce an uploaded all-pass phase filter.");
        var filters=profile.Bands.Select(b=>Biquad.Create(b,48000)).ToArray();
        if(!double.IsFinite(additionalGainDb))throw new ArgumentOutOfRangeException(nameof(additionalGainDb));
        double Db(double f)=>profile.PreampDb+additionalGainDb+filters.Sum(b=>20*Math.Log10(Math.Max(1e-20,b.Response(f,48000).Magnitude)));
        int[] frequencies=Enumerable.Range(0,1001).Select(i=>(int)Math.Round(20*Math.Pow(1000,i/1000d))).Distinct().ToArray();
        var target=frequencies.Select(f=>Db(f)).ToArray();var selected=new SortedSet<int>{0,frequencies.Length-1};
        while(selected.Count<20) {
            var knots=selected.ToArray();int next=-1;double largest=-1;
            for(int k=1;k<knots.Length;k++) for(int i=knots[k-1]+1;i<knots[k];i++) {
                double t=Math.Log((double)frequencies[i]/frequencies[knots[k-1]])/Math.Log((double)frequencies[knots[k]]/frequencies[knots[k-1]]);
                double error=Math.Abs(target[i]-(target[knots[k-1]]*(1-t)+target[knots[k]]*t));
                if(error>largest){largest=error;next=i;}
            }
            if(next<0)break;
            if(largest<1e-8) {
                double span=-1;
                for(int k=1;k<knots.Length;k++) if(knots[k]-knots[k-1]>1) {
                    double gap=Math.Log((double)frequencies[knots[k]]/frequencies[knots[k-1]]);
                    if(gap>span){span=gap;double center=Math.Sqrt((double)frequencies[knots[k]]*frequencies[knots[k-1]]);next=Enumerable.Range(knots[k-1]+1,knots[k]-knots[k-1]-1).MinBy(i=>Math.Abs(frequencies[i]-center));}
                }
            }
            selected.Add(next);
        }
        int[] centers=selected.Select(i=>frequencies[i]).ToArray(),gains=selected.Select(i=>(int)Math.Round(target[i]*16)).ToArray();
        if(gains.Any(v=>v is < -576 or >576)) throw new ProfileOperationException("The EQ exceeds Atmos's native -36 to +36 dB band range. Reduce the filter gains.");
        double maximum=0;int band=0;
        for(int i=0;i<frequencies.Length;i++) {
            while(band<centers.Length-2 && frequencies[i]>centers[band+1])band++;
            double t=Math.Log((double)frequencies[i]/centers[band])/Math.Log((double)centers[band+1]/centers[band]);
            maximum=Math.Max(maximum,Math.Abs(target[i]-(gains[band]*(1-t)+gains[band+1]*t)/16));
        }
        return new(centers,gains,maximum);
    }

    // DAP-VR 2.1.1 runtime optionals through GEQ; retain the remaining bytes verbatim.
    internal static byte[][] Prefix(byte[] original,out byte[] tail)
    {
        if(original.Length>1_000_000)throw new ProfileOperationException("The Atmos runtime configuration is too large.");
        using var r=new BinaryReader(new MemoryStream(original));var fields=new byte[33][];
        byte Flag(){if(r.BaseStream.Position==r.BaseStream.Length)return 0;return r.ReadByte();}
        byte[] Bytes(int count){var data=r.ReadBytes(count);if(data.Length!=count)throw new ProfileOperationException("Truncated Atmos runtime configuration.");return data;}
        uint Count(int limit){uint count=r.ReadUInt32();if(count>limit)throw new ProfileOperationException("Unsupported Atmos runtime array size.");return count;}
        int[] bools=[6,8,14,15,16,17,21,23,25,27,31];
        try { for(int i=0;i<33;i++) {
            using var ms=new MemoryStream();using var w=new BinaryWriter(ms);byte present=Flag();w.Write(present);
            if(present!=0) {
                if(bools.Contains(i))w.Write(r.ReadByte());
                else if(i is 1 or 24 or 32){uint n=Count(i==1?120:20);w.Write(n);w.Write(Bytes(checked((int)n*(i==1?4:8))));}
                else if(i==7) {
                    uint n=Count(20);w.Write(n);w.Write(Bytes((int)n*4));
                    for(int channel=0;channel<10;channel++){byte b=Flag();w.Write(b);if(b!=0)w.Write(Bytes((int)n*4));}
                } else w.Write(Bytes(4));
            }
            fields[i]=ms.ToArray();
        }} catch(EndOfStreamException){throw new ProfileOperationException("Truncated Atmos runtime configuration.");}
        tail=r.ReadBytes(checked((int)(r.BaseStream.Length-r.BaseStream.Position)));return fields;
    }
    internal static byte[] Create(byte[] original,AtmosEqCurve? curve)
    {
        var fields=Prefix(original,out var tail);
        fields[31]=[1,(byte)(curve==null?0:1)];
        if(curve!=null){using var ms=new MemoryStream();using var w=new BinaryWriter(ms);w.Write((byte)1);w.Write((uint)curve.Frequencies.Length);for(int i=0;i<curve.Frequencies.Length;i++){w.Write(curve.Frequencies[i]);w.Write(curve.Gains[i]);}fields[32]=ms.ToArray();}
        return fields.SelectMany(x=>x).Concat(tail).ToArray();
    }
    static AtmosEqBackup? Load(AudioDevice d)
    {
        if(!File.Exists(StatePath(d)))return null;
        var state=JsonSerializer.Deserialize<AtmosEqBackup>(File.ReadAllText(StatePath(d)));
        if(state==null || state.Endpoint!=d.Id || state.Properties==null || state.Properties.Length is <1 or >2 || state.Properties.Any(p=>p==null||p.Guid is not(Primary or Alternate)||p.Type is not(0 or 65)||p.Data==null||p.Type==0&&p.Data.Length!=0||ProfileService.Hash(p.Data)!=p.OriginalHash) || state.Properties.Select(p=>p.Guid).Distinct().Count()!=state.Properties.Length)throw new ProfileOperationException("The Atmos EQ backup could not be verified.");
        if(state.Profile!=null)HeadphoneEq.Validate(state.Profile);
        if(state.RoomGainDb is not(0 or 6 or 12))throw new ProfileOperationException("The saved Atmos room gain is invalid.");
        return state;
    }
    public static string Inspect(AudioDevice selected)
    {
        var d=ProfileService.Resolve(selected);var state=Load(d);if(state==null)return "Dolby Atmos for Headphones active.";
        using var store=new SpatialStore(d,ProfileService.Context,false,true);
        if(state.Properties.Any(p=>ProfileService.Hash(store.Read(p.Guid,2).Data)!=p.AppliedHash))return "Atmos EQ changed outside this application.";
        return (state.Profile!=null?"Dolby Atmos for Headphones active. EQ: "+state.Profile.Name+" (20-band native approximation).":state.HasEqSelection?"Dolby Atmos for Headphones active. Uploaded EQ off.":"Dolby Atmos for Headphones active. Original headphone EQ retained.")+(state.RoomGainDb==0?"":$" Room gain correction: +{state.RoomGainDb:g} dB.");
    }
    static AtmosEqBackup? LoadForChange(AudioDevice d,bool force)
    {
        try{return Load(d);}
        catch(Exception e) when(e is JsonException or ProfileOperationException or ArgumentException) {
            if(!force)throw new ProfileWarningException("The Atmos EQ backup could not be verified. Allow to use the current runtime settings as a new baseline.");
            string path=StatePath(d);if(File.Exists(path))File.Move(path,path+".invalid."+System.Guid.NewGuid().ToString("N"));
            return null;
        }
    }
    internal static double RoomGain(AudioDevice d,bool force=false)=>LoadForChange(d,force)?.RoomGainDb??0;
    internal static AtmosEqSnapshot Capture(AudioDevice d)
    {
        using var store=new SpatialStore(d,ProfileService.Context,false,true);
        var properties=new[]{Primary,Alternate}.Select(g=>{var v=store.Read(g,2);string h=ProfileService.Hash(v.Data);return new AtmosEqProperty(g,v.Type,v.Data,h,h);}).ToArray();
        return new(d,properties,File.Exists(StatePath(d))?File.ReadAllBytes(StatePath(d)):null);
    }
    internal static void Restore(AtmosEqSnapshot snapshot)
    {
        var state=Load(snapshot.Device);using var store=new SpatialStore(snapshot.Device,ProfileService.Context,true,true);
        foreach(var p in snapshot.Properties){var current=store.Read(p.Guid,2);if(!current.Data.SequenceEqual(p.Data)&&state?.Properties.SingleOrDefault(x=>x.Guid==p.Guid)?.AppliedHash!=ProfileService.Hash(current.Data))throw new ProfileOperationException("Atmos EQ changed during restoration. The backup was preserved.");}
        foreach(var p in snapshot.Properties){if(p.Type==0)store.Clear(p.Guid,2);else store.Write(p.Guid,p.Data,2);var after=store.Read(p.Guid,2);if(after.Type!=p.Type||!after.Data.SequenceEqual(p.Data))throw new ProfileOperationException("Atmos EQ restoration could not be verified.");}
        if(snapshot.State==null)File.Delete(StatePath(snapshot.Device));else File.WriteAllBytes(StatePath(snapshot.Device),snapshot.State);
    }
    public static string Change(AudioDevice selected,HeadphoneEqProfile? profile,bool force=false)=>ChangeCore(selected,profile,force,null,false);
    internal static void ChangeRoomGain(AudioDevice selected,double db,bool force=false)
    {
        if(db is not(0 or 6 or 12))throw new ArgumentOutOfRangeException(nameof(db));
        var d=ProfileService.Resolve(selected);var state=LoadForChange(d,force);
        if((state?.RoomGainDb??0)==db)return;
        _=ChangeCore(d,state?.Profile,force,db,true);
    }
    internal static byte[] WithRoomGain(byte[] current,byte[] baseline,double db)
    {
        var fields=Prefix(current,out var tail);var original=Prefix(baseline,out _);
        if(db==0){fields[31]=original[31];fields[32]=original[32];return fields.SelectMany(x=>x).Concat(tail).ToArray();}
        if(original[31].Length<2 || original[31][1]==0 || original[32].Length<5)
            return Create(current,Compile(new(System.Guid.NewGuid().ToString("N"),"Room gain correction",db,[])));
        int count=BitConverter.ToInt32(original[32],1);
        if(count!=20)throw new ProfileOperationException("Gain correction requires a supported 20-band native Atmos EQ baseline.");
        var gains=original[32].ToArray();for(int i=0;i<count;i++){int offset=9+i*8;int gain=checked(BitConverter.ToInt32(gains,offset)+(int)(db*16));if(gain is <-576 or >576)throw new ProfileOperationException("Room gain correction exceeds Atmos's native EQ range. Reduce the uploaded EQ gain.");BitConverter.GetBytes(gain).CopyTo(gains,offset);}
        fields[31]=[1,1];fields[32]=gains;return fields.SelectMany(x=>x).Concat(tail).ToArray();
    }
    static string ChangeCore(AudioDevice selected,HeadphoneEqProfile? profile,bool force,double? roomGain,bool roomOnly)
    {
        var d=ProfileService.Resolve(selected);using var operation=new SpatialOperation();SpatialFiles.RequireProvider(d,AudioPlatform.Dolby,"Dolby Atmos for Headphones");
        var state=LoadForChange(d,force);double gain=roomGain??state?.RoomGainDb??0;
        var curve=profile==null?(gain==0?null:Compile(new(System.Guid.NewGuid().ToString("N"),"Room gain correction",gain,[]))):Compile(profile,gain);
        if(curve?.MaximumErrorDb>.75 && !force)throw new ProfileWarningException($"Atmos uses 20 native EQ bands. This profile's target approximation differs by up to {curve.MaximumErrorDb:0.##} dB. Allow to apply it anyway.");
        using var store=new SpatialStore(d,ProfileService.Context,true,true);var snapshot=SpatialFiles.Snapshot(d,ProfileService.Context);
        if(state==null) {
            string[] guids=store.Read(Alternate).Type!=0 || store.Read(Alternate,2).Type!=0?[Primary,Alternate]:[Primary];
            state=new(){Endpoint=d.Id,Properties=guids.Select(g=>{var v=store.Read(g,2);string h=ProfileService.Hash(v.Data);return new AtmosEqProperty(g,v.Type,v.Data,h,h);}).ToArray()};
            SpatialFiles.Save(StatePath(d),state);
        }
        var before=state.Properties.Select(p=>(p.Guid,Value:store.Read(p.Guid,2))).ToArray();
        if(!force && before.Any(p=>ProfileService.Hash(p.Value.Data)!=state.Properties.Single(x=>x.Guid==p.Guid).AppliedHash))throw new ProfileWarningException("Atmos runtime settings changed outside this application. Allow to preserve those settings while replacing EQ.");
        var desired=before.Select(p=>(p.Guid,Data:roomOnly&&!state.HasEqSelection?WithRoomGain(p.Value.Data,state.Properties.Single(x=>x.Guid==p.Guid).Data,gain):Create(p.Value.Data,curve))).ToArray();
        var backup=Path.Combine(SpatialFiles.Folder(d,"atmos-eq"),"backups",System.Guid.NewGuid().ToString("N")+".json");SpatialFiles.Save(backup,new{Before=before.Select(p=>new{p.Guid,p.Value.Type,p.Value.Data,Hash=ProfileService.Hash(p.Value.Data)}).ToArray(),Context=snapshot});
        try {
            foreach(var p in desired)store.Write(p.Guid,p.Data,2);
            AfterWriteTest?.Invoke();
            foreach(var p in desired){if(!store.Read(p.Guid,2).Data.SequenceEqual(p.Data))throw new ProfileOperationException("The Atmos EQ runtime data could not be verified.");SpatialFiles.VerifyPersisted(d,ProfileService.Context,p.Guid,p.Data,2);}
            SpatialFiles.VerifyUnchanged(snapshot,SpatialFiles.Snapshot(d,ProfileService.Context),desired.Select(p=>"{"+p.Guid+"},2").ToArray());
            SpatialFiles.RequireProvider(d,AudioPlatform.Dolby,"Dolby Atmos for Headphones");
            state.Properties=state.Properties.Select(p=>p with{AppliedHash=ProfileService.Hash(desired.Single(x=>x.Guid==p.Guid).Data)}).ToArray();state.Profile=profile;state.HasEqSelection|=!roomOnly;state.RoomGainDb=gain;SpatialFiles.Save(StatePath(d),state);
            return profile==null?"Atmos headphone EQ off.":"Atmos headphone EQ applied using 20 native bands.";
        } catch(Exception error) {
            try {
                foreach(var p in before){var now=store.Read(p.Guid,2);var expected=desired.Single(x=>x.Guid==p.Guid).Data;if(!now.Data.SequenceEqual(expected)&&!now.Data.SequenceEqual(p.Value.Data))throw new IOException("Atmos runtime data changed during rollback.");}
                foreach(var p in before){if(p.Value.Type==0)store.Clear(p.Guid,2);else store.Write(p.Guid,p.Value.Data,2);}
                SpatialFiles.VerifyUnchanged(snapshot,SpatialFiles.Snapshot(d,ProfileService.Context));
            }catch(Exception restore){throw new AggregateException("Atmos EQ failed and could not be restored. Backup: "+backup,error,restore);}
            throw;
        }
    }
}
