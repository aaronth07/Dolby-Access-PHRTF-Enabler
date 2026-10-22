using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AtmosProfile;

public sealed record EqBand(string Type, double FrequencyHz, double GainDb, double Q);
public sealed record HeadphoneEqProfile(string Id, string Name, double PreampDb, EqBand[] Bands)
{
    public override string ToString() => Name;
}

public static class HeadphoneEq
{
    static readonly Regex Filter = new(@"^Filter(?:\s+\d+)?\s*:\s*(?<state>ON|OFF)\s+(?<type>PK|PEQ|Modal|LSC?|HSC?|LPQ?|HPQ?|NO|AP|BP)(?:\s+(?<slope>[\d.,eE+-]+)\s+dB)?\s+Fc\s+(?<frequency>[\d.,eE+\-\u00a0]+)\s+Hz(?:\s+Gain\s+(?<gain>[\d.,eE+-]+)\s+dB)?(?:\s+(?:Q\s+(?<q>[\d.,eE+-]+)|BW\s+Oct\s+(?<bw>[\d.,eE+-]+)))?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static readonly Regex Preamp = new(@"^Preamp\s*:\s*([\d.,eE+-]+)\s+dB\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static double Number(string text) => double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : throw new InvalidDataException("Invalid EQ number: " + text);
    static double Frequency(string text)
    {
        string value=text.Replace("\u00a0", "").Replace(',', '.');
        double frequency=Number(value);
        if(value.Length>=5 && !value.Contains('e',StringComparison.OrdinalIgnoreCase) && value[^4]=='.') frequency*=1000;
        return frequency;
    }
    public static HeadphoneEqProfile Import(string text, string name)
    {
        if (text.Length > 128_000) throw new InvalidDataException("The EQ file exceeds 128 KB.");
        var bands = new List<EqBand>(); double preamp = 0; bool hasPreamp = false, hasFilter = false;
        int line = 0;
        foreach (var raw in text.Split('\n')) {
            line++;
            string value = raw.Split('#')[0].Trim();
            if (value.Length == 0 || value.StartsWith(';')) continue;
            var p = Preamp.Match(value);
            if (p.Success) {
                if (hasPreamp) throw new InvalidDataException("The EQ contains more than one preamp setting.");
                preamp = Number(p.Groups[1].Value); hasPreamp = true; continue;
            }
            var f = Filter.Match(value);
            if (!f.Success) throw new InvalidDataException($"Unsupported EQ syntax on line {line}. Import parametric filters in Equalizer APO text format.");
            hasFilter = true;
            if (f.Groups["state"].Value.Equals("OFF", StringComparison.OrdinalIgnoreCase)) continue;
            string type = f.Groups["type"].Value.ToUpperInvariant();
            type = type switch { "PEQ" or "MODAL" => "PK", "LPQ" => "LP", "HPQ" => "HP", _ => type };
            bool shelf=type is "LS" or "HS" or "LSC" or "HSC";
            double gain = f.Groups["gain"].Success ? Number(f.Groups["gain"].Value) : 0;
            if ((type == "PK" || shelf) && !f.Groups["gain"].Success) throw new InvalidDataException($"Missing filter gain on line {line}.");
            if ((type is "PK" or "AP") && !f.Groups["q"].Success && !f.Groups["bw"].Success) throw new InvalidDataException($"Missing filter Q or bandwidth on line {line}.");
            double frequency=Frequency(f.Groups["frequency"].Value), a=Math.Pow(10,gain/40);
            if(f.Groups["slope"].Success && (!shelf || f.Groups["q"].Success || f.Groups["bw"].Success) || shelf && f.Groups["bw"].Success)
                throw new InvalidDataException($"Conflicting or unsupported filter slope/bandwidth on line {line}.");
            double q;
            if(f.Groups["q"].Success) q=Number(f.Groups["q"].Value);
            else if(f.Groups["bw"].Success) {
                double bw=Number(f.Groups["bw"].Value),w=2*Math.PI*frequency/48000;
                if(bw<=0 || frequency<=0 || frequency>=24000) throw new InvalidDataException($"Invalid bandwidth or frequency on line {line}.");
                q=1/(2*Math.Sinh(Math.Log(2)/2*bw*w/Math.Sin(w)));
            } else if(shelf) {
                double slope=f.Groups["slope"].Success ? Number(f.Groups["slope"].Value)/12 : .9;
                if(slope<=0) throw new InvalidDataException($"Invalid shelf slope on line {line}.");
                q=1/Math.Sqrt((a+1/a)*(1/slope-1)+2);
                // Equalizer APO leaves omitted-parameter shelves at their center frequency.
                if(!f.Groups["slope"].Success) type=type=="LS" ? "LSC" : type=="HS" ? "HSC" : type;
            } else q=type=="NO" ? 30 : Math.Sqrt(.5);
            if(!shelf && type!="PK") gain=0;
            bands.Add(new(type, frequency, gain, q));
        }
        if (!hasPreamp && !hasFilter) throw new InvalidDataException("The file contains no parametric EQ settings.");
        var profile = new HeadphoneEqProfile(Guid.NewGuid().ToString("N"), name.Trim(), preamp, bands.ToArray());
        Validate(profile); foreach(var band in profile.Bands) _ = Biquad.Create(band,48000); return profile;
    }
    public static void Validate(HeadphoneEqProfile profile)
    {
        if (!Guid.TryParseExact(profile.Id, "N", out _) || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 120)
            throw new InvalidDataException("EQ profile identity or name is invalid.");
        if (!double.IsFinite(profile.PreampDb) || profile.PreampDb is < -60 or > 12 || profile.Bands == null || profile.Bands.Length > 32)
            throw new InvalidDataException("EQ supports up to 32 filters and a preamp from -60 to +12 dB.");
        foreach (var band in profile.Bands) {
            if (band == null || band.Type is not ("PK" or "LS" or "HS" or "LSC" or "HSC" or "LP" or "HP" or "NO" or "AP" or "BP") || !double.IsFinite(band.FrequencyHz) || band.FrequencyHz is < 10 or > 24000 || !double.IsFinite(band.GainDb) || band.GainDb is < -30 or > 30 || !double.IsFinite(band.Q) || band.Q is < .05 or > 30)
                throw new InvalidDataException("Invalid EQ filter. Frequency must be 10-24000 Hz, gain -30 to +30 dB, and Q 0.05-30.");
        }
    }
    #if TESTING
    internal static string? TestFolder;
#endif
    public static string Folder =>
#if TESTING
        TestFolder ??
#endif
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AtmosProfile", "headphone-eq");
    public static void Save(HeadphoneEqProfile profile)
    {
        Validate(profile); foreach(var band in profile.Bands) _ = Biquad.Create(band,48000); Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, profile.Id + ".json"), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.WriteAllText(temporary, JsonSerializer.Serialize(profile));
            Validate(JsonSerializer.Deserialize<HeadphoneEqProfile>(File.ReadAllText(temporary)) ?? throw new InvalidDataException("EQ profile could not be saved."));
            File.Move(temporary, path, true);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static HeadphoneEqProfile[] Profiles() => LoadProfiles(out _);
    public static HeadphoneEqProfile[] LoadProfiles(out string[] problems)
    {
        var valid=new List<HeadphoneEqProfile>(); var invalid=new List<string>();
        if(Directory.Exists(Folder)) foreach(string path in Directory.GetFiles(Folder,"*.json")) {
            try {
                var p=JsonSerializer.Deserialize<HeadphoneEqProfile>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty profile");
                Validate(p);
                if(!System.IO.Path.GetFileNameWithoutExtension(path).Equals(p.Id,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Profile identity mismatch");
                foreach(var band in p.Bands) _=Biquad.Create(band,48000);
                valid.Add(p);
            }catch(Exception e) when(e is JsonException or InvalidDataException or IOException or UnauthorizedAccessException) {invalid.Add(System.IO.Path.GetFileName(path));}
        }
        problems=invalid.ToArray();return valid.OrderBy(p=>p.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public static void Delete(HeadphoneEqProfile profile) { Validate(profile); File.Delete(Path.Combine(Folder, profile.Id + ".json")); }
    internal static byte[] DtsDevice(byte[] original, HeadphoneEqProfile profile)
    {
        Validate(profile); DtsProfileService.ValidateDevice(original);
        long oldCount=(long)BitConverter.ToUInt32(original,44)+BitConverter.ToUInt32(original,48);
        long oldExtent=56+oldCount*24+(long)BitConverter.ToUInt32(original,52)*8;
        bool noFilters=oldCount==0 && BitConverter.ToUInt32(original,52)==0;
        if(oldExtent!=original.Length && !noFilters) throw new ProfileOperationException("This DTS EQ contains an unfamiliar extension. Its device data was left unchanged.");
        var filters=profile.Bands.Select(b=>Biquad.Create(b,48000)).Prepend(new Biquad(Math.Pow(10,profile.PreampDb/20),0,0,0,0)).ToArray();
        var result=new byte[56+filters.Length*2*24]; Array.Copy(original,result,56);
        BitConverter.GetBytes((uint)result.Length).CopyTo(result,0); Array.Clear(result,32,24);
        BitConverter.GetBytes((uint)filters.Length).CopyTo(result,44); BitConverter.GetBytes((uint)filters.Length).CopyTo(result,48);
        int offset=56;
        for(int ear=0;ear<2;ear++) foreach(var f in filters) {
            float[] v=[(float)f.B0,(float)f.B1,(float)f.B2,1,(float)f.A1,(float)f.A2];
            if(Math.Abs(v[5])>=1 || 1d+v[4]+v[5]<=0 || 1d-v[4]+v[5]<=0)
                throw new ProfileOperationException("An EQ filter loses stability at native float precision. Reduce Q or move its frequency away from DC/Nyquist.");
            foreach(float value in v){BitConverter.GetBytes(value).CopyTo(result,offset);offset+=4;}
        }
        DtsProfileService.ValidateDevice(result); return result;
    }
}

public readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2)
{
    public static Biquad Create(EqBand band, int rate)
    {
        HeadphoneEq.Validate(new(Guid.NewGuid().ToString("N"), "EQ", 0, [band]));
        double frequency=band.FrequencyHz;
        if (band.Type is "LS" or "HS") {
            double shelfA=Math.Pow(10,band.GainDb/40);
            double reciprocalSlope=(1/(band.Q*band.Q)-2)/(shelfA+1/shelfA)+1;
            double factor=Math.Pow(10,Math.Abs(band.GainDb)*reciprocalSlope/80);
            frequency=band.Type=="LS" ? frequency*factor : frequency/factor;
        }
        if (rate is < 8000 or > 384000 || !double.IsFinite(frequency) || frequency <= 0 || frequency >= rate * .5) throw new InvalidDataException("An EQ frequency is at or above the output Nyquist frequency.");
        double w = 2 * Math.PI * frequency / rate, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * band.Q), a = Math.Pow(10, band.GainDb / 40), t = 2 * Math.Sqrt(a) * alpha;
        double b0, b1, b2, a0, a1, a2;
        switch (band.Type) {
            case "PK": b0=1+alpha*a; b1=-2*c; b2=1-alpha*a; a0=1+alpha/a; a1=-2*c; a2=1-alpha/a; break;
            case "LS": case "LSC": b0=a*((a+1)-(a-1)*c+t); b1=2*a*((a-1)-(a+1)*c); b2=a*((a+1)-(a-1)*c-t); a0=(a+1)+(a-1)*c+t; a1=-2*((a-1)+(a+1)*c); a2=(a+1)+(a-1)*c-t; break;
            case "HS": case "HSC": b0=a*((a+1)+(a-1)*c+t); b1=-2*a*((a-1)+(a+1)*c); b2=a*((a+1)+(a-1)*c-t); a0=(a+1)-(a-1)*c+t; a1=2*((a-1)-(a+1)*c); a2=(a+1)-(a-1)*c-t; break;
            case "LP": b0=(1-c)/2; b1=1-c; b2=b0; a0=1+alpha; a1=-2*c; a2=1-alpha; break;
            case "HP": b0=(1+c)/2; b1=-(1+c); b2=b0; a0=1+alpha; a1=-2*c; a2=1-alpha; break;
            case "BP": b0=alpha; b1=0; b2=-alpha; a0=1+alpha; a1=-2*c; a2=1-alpha; break;
            case "AP": b0=1-alpha; b1=-2*c; b2=1+alpha; a0=1+alpha; a1=-2*c; a2=1-alpha; break;
            default: b0=1; b1=-2*c; b2=1; a0=1+alpha; a1=-2*c; a2=1-alpha; break;
        }
        var result = new Biquad(b0/a0,b1/a0,b2/a0,a1/a0,a2/a0);
        if (!new[] {result.B0,result.B1,result.B2,result.A1,result.A2}.All(double.IsFinite) || Math.Abs(result.A2) >= 1 || 1+result.A1+result.A2 <= 0 || 1-result.A1+result.A2 <= 0)
            throw new InvalidDataException("The EQ filter is unstable at the selected output rate.");
        return result;
    }
    public Complex Response(double frequency, int rate)
    {
        var z=Complex.FromPolarCoordinates(1,-2*Math.PI*frequency/rate);
        return (B0+B1*z+B2*z*z)/(1+A1*z+A2*z*z);
    }
}

public sealed class ParametricEq
{
    readonly Biquad[] filters;
    readonly double[,] z1, z2;
    readonly double gain;
    readonly int channels;
    public ParametricEq(HeadphoneEqProfile profile, int sampleRate, int channelCount)
    {
        HeadphoneEq.Validate(profile);
        if (channelCount is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(channelCount));
        channels=channelCount; gain=Math.Pow(10,profile.PreampDb/20);
        filters=profile.Bands.Select(b=>Biquad.Create(b,sampleRate)).ToArray(); z1=new double[channels,filters.Length];z2=new double[channels,filters.Length];
    }
    public void Process(Span<float> samples)
    {
        if (samples.Length % channels != 0) throw new ArgumentException("Incomplete interleaved audio frame.");
        foreach (float value in samples) if (!float.IsFinite(value)) throw new InvalidDataException("Nonfinite input audio.");
        for(int frame=0;frame<samples.Length;frame+=channels) for(int channel=0;channel<channels;channel++) {
            double x=samples[frame+channel]*gain;
            for(int i=0;i<filters.Length;i++) {
                var f=filters[i];double y=f.B0*x+z1[channel,i];
                z1[channel,i]=f.B1*x-f.A1*y+z2[channel,i];z2[channel,i]=f.B2*x-f.A2*y;x=y;
            }
            samples[frame+channel]=(float)x;
        }
    }
}
