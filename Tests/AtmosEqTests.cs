using AtmosProfile;
using System.Text.Json;
internal static class AtmosEqTests
{
    public static void Run()
    {
        var passed=new List<string>();void Check(bool ok,string name){if(!ok)throw new Exception(name);passed.Add(name);}
        void Reject(Action a,string name){try{a();}catch(ProfileOperationException){passed.Add(name);return;}throw new Exception(name);}
        var flat=AtmosEqService.Compile(HeadphoneEq.Import("Preamp: -6 dB","Flat"));
        Check(flat.Frequencies.Length==20 && flat.Frequencies.Zip(flat.Frequencies.Skip(1)).All(p=>p.First<p.Second),"Atmos target has 20 strictly increasing native frequencies");
        Check(flat.Gains.All(g=>g==-96),"Atmos preamp is included in every native band at 16 units per dB");
        Check(flat.MaximumErrorDb<1e-12,"Flat Atmos target remains flat after quantization");
        var corrected=AtmosEqService.Compile(HeadphoneEq.Import("Preamp: -6 dB","Flat"),12);
        Check(corrected.Gains.All(g=>g==96),"Room gain and uploaded EQ preamp are composed instead of overwriting each other");
        var vendor=AtmosEqService.Create([],flat);
        var raised=AtmosEqService.Prefix(AtmosEqService.WithRoomGain(vendor,vendor,6),out _);
        Check(Enumerable.Range(0,20).All(i=>BitConverter.ToInt32(raised[32],9+8*i)==0),"Room gain is added to every original vendor EQ band");
        Check(AtmosEqService.WithRoomGain(AtmosEqService.WithRoomGain(vendor,vendor,12),vendor,0).SequenceEqual(vendor),"Removing room gain restores the original native EQ byte for byte");
        var peak=AtmosEqService.Compile(HeadphoneEq.Import("Filter 1: ON PK Fc 1000 Hz Gain -6 dB Q 1","Peak"));
        Check(peak.MaximumErrorDb<.3 && peak.Frequencies.Any(f=>Math.Abs(f-1000)<60),"Adaptive Atmos bands retain the requested peaking response");
        var created=AtmosEqService.Create([],flat);var fields=AtmosEqService.Prefix(created,out var tail);
        Check(fields[31].SequenceEqual(new byte[]{1,1})&&fields[32].Length==165,"Atmos runtime encoding enables EQ with 20 eight-byte records");
        Check(BitConverter.ToInt32(fields[32],9)==-96,"Atmos native band stores frequency followed by signed Q4 dB gain");
        Check(fields[3].SequenceEqual(new byte[]{0}),"Atmos EQ preserves the existing native postgain field");
        Check(tail.Length==0,"Minimal Atmos runtime encoding has no extra data");
        byte[][] arbitrary=Enumerable.Range(0,33).Select(_=>new byte[]{0}).ToArray();
        arbitrary[0]=[1,7,0,0,0];arbitrary[1]=[1,2,0,0,0,1,0,0,0,2,0,0,0];arbitrary[3]=[1,13,0,0,0];arbitrary[6]=[2,2];
        using(var ms=new MemoryStream()){using var w=new BinaryWriter(ms);w.Write((byte)1);w.Write(2u);w.Write(100);w.Write(200);for(int ch=0;ch<10;ch++){w.Write((byte)(ch==3?1:0));if(ch==3){w.Write(3);w.Write(4);}}arbitrary[7]=ms.ToArray();}
        var original=arbitrary.SelectMany(x=>x).Concat(new byte[]{9,8,7,6}).ToArray();
        var changed=AtmosEqService.Prefix(AtmosEqService.Create(original,peak),out var unchangedTail);
        Check(Enumerable.Range(0,31).All(i=>changed[i].SequenceEqual(arbitrary[i])),"Atmos EQ preserves every preceding field including channel arrays and noncanonical Booleans");
        Check(unchangedTail.SequenceEqual(new byte[]{9,8,7,6}),"Atmos EQ preserves unknown trailing runtime fields verbatim");
        var off=AtmosEqService.Prefix(AtmosEqService.Create(original,null),out _);Check(off[31].SequenceEqual(new byte[]{1,0}),"Atmos EQ off explicitly disables equalization");
        Reject(()=>AtmosEqService.Prefix([1,0],out _),"Truncated Atmos optional integer is rejected");
        Reject(()=>AtmosEqService.Prefix([0,1,255,255,255,255],out _),"Oversized Atmos runtime arrays are rejected before allocation");
        Reject(()=>AtmosEqService.Compile(HeadphoneEq.Import("Preamp: -60 dB","Cut")),"Atmos band range cannot silently clamp uploaded gain");
        Reject(()=>AtmosEqService.Compile(HeadphoneEq.Import("Filter 1: ON AP Fc 1000 Hz Q 1","Phase")),"Atmos magnitude equalizer cannot silently discard all-pass phase");
        Console.WriteLine(JsonSerializer.Serialize(passed));
    }
}
