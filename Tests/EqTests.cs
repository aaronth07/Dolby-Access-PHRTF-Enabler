using AtmosProfile;
using System.Globalization;
using System.Text.Json;
internal static class EqTests
{
    public static void Run()
    {
        var passed=new List<string>();
        void Check(bool value,string name){if(!value)throw new Exception(name);passed.Add(name);}
        void Reject(Action action,string name){try{action();}catch(InvalidDataException){passed.Add(name);return;}throw new Exception(name);}
        var profile=HeadphoneEq.Import("Preamp: -6 dB\nFilter 1: ON PK Fc 1000 Hz Gain 6 dB Q 1\nFilter 2: OFF PK Fc 200 Hz Gain 4 dB Q 2", "Headphones");
        byte[] emptyDevice=Convert.FromHexString("3800000052444556020000000100000028000000524750510200000080bb0000000000000000000000000000000000000000000000000000");
        DtsProfileService.ValidateDevice(emptyDevice);
        Check(HeadphoneEq.DtsDevice(emptyDevice,profile).Length==152,"Header-only 56-byte DTS EQ accepts uploaded parametric filters");
        var padded=emptyDevice.Concat(new byte[24]).ToArray();BitConverter.GetBytes((uint)padded.Length).CopyTo(padded,0);
        Check(HeadphoneEq.DtsDevice(padded,profile).SequenceEqual(HeadphoneEq.DtsDevice(emptyDevice,profile)),"Inactive trailing EQ data from older EQ-off writes does not block import");
        Check(profile.Bands.Length==1 && profile.PreampDb==-6,"Equalizer APO import preserves preamp and skips disabled filters");
        var originalCulture=CultureInfo.CurrentCulture;
        try{CultureInfo.CurrentCulture=new("de-DE");Check(HeadphoneEq.Import("Filter 1: ON PK Fc 1200.5 Hz Gain -2.5 dB Q 1.4","EQ").Bands[0].FrequencyHz==1200.5,"EQ numbers use invariant decimal syntax");}finally{CultureInfo.CurrentCulture=originalCulture;}
        Reject(()=>HeadphoneEq.Import("Include: config.txt","EQ"),"Unsupported Equalizer APO directives are rejected");
        Reject(()=>HeadphoneEq.Import("Preamp: 0 dB\nPreamp: -1 dB","EQ"),"Duplicate preamps are rejected");
        Reject(()=>HeadphoneEq.Import("Filter 1: ON PK Fc 1000 Hz Q 1","EQ"),"Missing peaking gain is rejected");
        Reject(()=>HeadphoneEq.Import("Filter 1: ON PK Fc 1000 Hz Gain 1 dB Q 0","EQ"),"Invalid Q is rejected");
        Reject(()=>HeadphoneEq.Import("Preamp: 99 dB","EQ"),"Out-of-range preamp is rejected");
        Reject(()=>HeadphoneEq.Import("Preamp: 1e999 dB","EQ"),"Nonfinite EQ numbers are rejected");
        Reject(()=>HeadphoneEq.Import("Preamp: 1.2.3 dB","EQ"),"Malformed EQ numbers are rejected");
        Reject(()=>HeadphoneEq.Import("# comment only","EQ"),"Empty EQ files are rejected");
        Reject(()=>HeadphoneEq.Import(string.Join('\n',Enumerable.Range(1,33).Select(i=>$"Filter {i}: ON PK Fc 1000 Hz Gain -1 dB Q 1")),"EQ"),"More than 32 active filters are rejected");
        Reject(()=>HeadphoneEq.Import("Filter 1: ON PK Fc 1000 Hz Gain -1 dB","EQ"),"Peaking filter without Q is rejected");
        Reject(()=>HeadphoneEq.Import("Filter 1: ON PK Fc 24000 Hz Gain -1 dB Q 1","EQ"),"Import rejects Nyquist filters before graphing or saving");
        var bandwidth=HeadphoneEq.Import("Filter: ON Modal Fc 1000 Hz Gain -6 dB BW Oct 1.5","EQ").Bands[0];
        double omega=2*Math.PI*1000/48000;
        Check(bandwidth.Type=="PK" && Math.Abs(bandwidth.Q-1/(2*Math.Sinh(Math.Log(2)/2*1.5*omega/Math.Sin(omega))))<1e-12,"Modal and octave bandwidth normalize to the native 48 kHz Q");
        Check(HeadphoneEq.Import("Filter 1: ON LPQ Fc 1000 Hz Q 0.707\nFilter 2: ON HPQ Fc 100 Hz Q 0.8","EQ").Bands.Select(b=>b.Type).SequenceEqual(new[]{"LP","HP"}),"Q pass-filter aliases are accepted");
        var slopeShelf=HeadphoneEq.Import("Filter 1: ON LSC 6 dB Fc 100 Hz Gain 6 dB","EQ").Bands[0];
        Check(Math.Abs(slopeShelf.Q-1/Math.Sqrt((Math.Pow(10,6/40d)+Math.Pow(10,-6/40d))*(1/.5-1)+2))<1e-12,"Shelf slope syntax converts to the equivalent Q");
        Check(HeadphoneEq.Import("Filter 1: ON LS Fc 100 Hz Gain 6 dB","EQ").Bands[0].Type=="LSC","Omitted shelf parameters retain Equalizer APO center-frequency behavior");
        Check(HeadphoneEq.Import("Preamp: -2,5 dB\nFilter 1: ON PK Fc 1.234 Hz Gain -1,5 dB Q 1,4","EQ").Bands[0].FrequencyHz==1234,"Equalizer APO decimal comma and frequency thousands separator are accepted");
        Reject(()=>HeadphoneEq.Import("Filter 1: ON LSC -6 dB Fc 100 Hz Gain 6 dB","EQ"),"Negative shelf slope is rejected");
        Reject(()=>HeadphoneEq.Import("Filter 1: ON BP Fc 1000 Hz BW Oct 0","EQ"),"Zero octave bandwidth is rejected");
        var corner=Biquad.Create(new("LS",100,6,Math.Sqrt(.5)),48000);
        double center=100*Math.Pow(10,6/80d);
        var centered=Biquad.Create(new("LSC",center,6,Math.Sqrt(.5)),48000);
        Check(Math.Abs((corner.Response(100,48000)-centered.Response(100,48000)).Magnitude)<1e-12,"LS corner and LSC center frequency semantics match Equalizer APO");
        var peak=Biquad.Create(profile.Bands[0],48000);
        Check(Math.Abs(20*Math.Log10(peak.Response(1000,48000).Magnitude)-6)<1e-8,"Peaking filter center gain matches 6 dB");
        foreach(string type in new[]{"PK","LS","HS","LSC","HSC","LP","HP","NO","AP","BP"}){
            var band=new EqBand(type,1000,6,.7071067811865476);var filter=Biquad.Create(band,48000);
            var eq=new ParametricEq(new(Guid.NewGuid().ToString("N"),type,0,[band]),48000,2);
            float[] impulse=new float[32768];impulse[0]=1;eq.Process(impulse);
            Check(impulse.All(float.IsFinite)&&Enumerable.Range(0,impulse.Length/2).All(i=>impulse[i*2+1]==0),type+" impulse remains finite with isolated channel state");
            if(type=="LSC")Check(Math.Abs(20*Math.Log10(filter.Response(0,48000).Magnitude)-6)<1e-7,"Low shelf DC gain matches requested gain");
            if(type=="HSC")Check(Math.Abs(20*Math.Log10(filter.Response(24000,48000).Magnitude)-6)<1e-7,"High shelf Nyquist gain matches requested gain");
        }
        Reject(()=>Biquad.Create(new("PK",24000,0,1),48000),"Nyquist-frequency filter is rejected");
        var a=new ParametricEq(profile,48000,2);var b=new ParametricEq(profile,48000,2);
        var source=Enumerable.Range(0,2000).Select(i=>(float)Math.Sin(i*.37)*.1f).ToArray();var full=source.ToArray();var chunks=source.ToArray();a.Process(full);b.Process(chunks.AsSpan(0,246));b.Process(chunks.AsSpan(246));
        Check(full.SequenceEqual(chunks),"EQ state is independent of callback block boundaries");
        var path=Path.Combine(Path.GetTempPath(),"AtmosProfile-EqTests-"+Guid.NewGuid().ToString("N"));HeadphoneEq.TestFolder=path;
        try{HeadphoneEq.Save(profile);Check(HeadphoneEq.Profiles().Single().Bands.SequenceEqual(profile.Bands),"Saved EQ profile round trips all bands");File.WriteAllText(Path.Combine(path,"broken.json"),"bad JSON");Check(HeadphoneEq.LoadProfiles(out var invalid).Length==1&&invalid.Length==1&&File.ReadAllText(Path.Combine(path,"broken.json"))=="bad JSON","A corrupt saved profile does not block other EQ profiles and remains unchanged");HeadphoneEq.Delete(profile);Check(HeadphoneEq.Profiles().Length==0,"Deleting an EQ profile affects only its app-owned file");}finally{HeadphoneEq.TestFolder=null;if(Directory.Exists(path))Directory.Delete(path,true);}
        Console.WriteLine(JsonSerializer.Serialize(passed));
    }
}
