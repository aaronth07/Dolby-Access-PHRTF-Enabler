using System.Security.Cryptography;
using System.Text;

namespace AtmosProfile;

internal static class DtsRoomService
{
    // Installed DTS Sound Unbound 2026.3.31.0. No preset or decoding key is bundled.
    const string AppHash = "374fd16ed693cf79fc63e4c401e97a9a4e17f462c986e2579bab2eedbf6ddac5";
    internal static byte[] InstalledPreset(string mode)
    {
        if (mode is not ("Balanced" or "Spacious")) throw new ProfileOperationException("Unknown DTS room preset.");
        string package = Directory.GetParent(Path.GetDirectoryName(DtsProfileService.FindEncoder())!)!.FullName;
        var app = File.ReadAllBytes(Path.Combine(package, "DTSSoundUnbound2.dll"));
        if (ProfileService.Hash(app) != AppHash) throw new ProfileOperationException("This DTS version has no verified room preset mapping.");
        string name = mode == "Balanced" ? "02-SPAC-HqHeightAndHgNf_SD1_Hp_Normal_v4_RC2.SPAC.crypt" : "04-SPAC-HqHeightAndHgNf_SD2_Hp_Normal_v4_RC2.SPAC.crypt";
        byte[] encrypted = File.ReadAllBytes(Path.Combine(package, "Data", "SAD", name));
        string expected = mode == "Balanced" ? "acd9c8784dc638fb35451cd61b724aec861b45f87068e2e755dda3645b4f9d5f" : "82feb52d3a0a3ffc50ef1700516a00c2923ac960be8627d4598140daf9df8881";
        if (ProfileService.Hash(encrypted) != expected) throw new ProfileOperationException("The installed DTS room preset changed.");
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(Encoding.Unicode.GetString(app, 0x79cac8 - 0x1800 + 12, 32));
        aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.PKCS7;
        using var decoder = aes.CreateDecryptor();
        byte[] plain = decoder.TransformFinalBlock(encrypted, 0, encrypted.Length);
        int length = checked((int)BitConverter.ToUInt32(plain));
        if (length < 60 || length > plain.Length - 4) throw new ProfileOperationException("The DTS room preset length is invalid.");
        var carrier = plain.AsSpan(4, length).ToArray(); DtsProfileService.ValidateSpac(carrier);
        string decodedHash = mode == "Balanced" ? "967db8f6796f59a500f42024fa4eb841925ba54d7aa3e13b69493a3a2a8bc0e7" : "42c3a4a498b11a5c3b5932b632a70099da12c5002d004b13e503b6924e5a56e3";
        if (ProfileService.Hash(carrier) != decodedHash) throw new ProfileOperationException("The decoded DTS room preset could not be verified.");
        return carrier;
    }
    internal static (int Offset, int Length) Sends(byte[] profile)
    {
        DtsProfileService.ValidateSpac(profile);
        int count = checked((int)BitConverter.ToUInt32(profile, 28));
        int samples = checked((int)BitConverter.ToUInt32(profile, 40));
        return (checked(132 + count * samples * 8), count * 8);
    }
    internal static byte[] CopyRoom(byte[] profile, byte[] template)
    {
        var a = Sends(profile); var b = Sends(template);
        int pa = checked(12 + (int)BitConverter.ToUInt32(profile, 12));
        int pb = checked(12 + (int)BitConverter.ToUInt32(template, 12));
        if (a.Length != b.Length || !profile.AsSpan(24, 12).SequenceEqual(template.AsSpan(24, 12)) ||
            !profile.AsSpan(44, 84).SequenceEqual(template.AsSpan(44, 84)) || !profile.AsSpan(pa).SequenceEqual(template.AsSpan(pb)))
            throw new ProfileOperationException("The DTS room preset uses a different grid or room processor. Select Original or Off.");
        var result = profile.ToArray(); template.AsSpan(b.Offset, b.Length).CopyTo(result.AsSpan(a.Offset, a.Length));
        return result;
    }
    internal static byte[] Apply(byte[] profile, byte[] original, string mode, bool force, out string applied)
    {
        applied = mode;
        if (mode == "Off") { var s = Sends(profile); var result = profile.ToArray(); Array.Clear(result, s.Offset, s.Length); return result; }
        if (mode == "Original") return CopyRoom(profile, original);
        if (mode is not ("Balanced" or "Spacious" or "Spacious200" or "Spacious400")) throw new ProfileOperationException("Unknown DTS room preset.");
        try { var result=CopyRoom(profile, InstalledPreset(mode.StartsWith("Spacious",StringComparison.Ordinal)?"Spacious":mode));return ScaleRoom(result,mode=="Spacious200"?2:mode=="Spacious400"?4:1); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or ProfileOperationException) {
            if (!force) throw new ProfileWarningException(e.Message + " Allow to keep the original room sends instead.");
            applied = "Original"; return CopyRoom(profile, original);
        }
    }
    internal static byte[] ScaleRoom(byte[] profile,double gain)
    {
        if(gain is not(1 or 2 or 4))throw new ArgumentOutOfRangeException(nameof(gain));
        var result=profile.ToArray();var sends=Sends(result);
        for(int offset=sends.Offset;offset<sends.Offset+sends.Length;offset+=4){float value=(float)(BitConverter.ToSingle(result,offset)*gain);if(!float.IsFinite(value))throw new ProfileOperationException("The DTS room gain exceeds native float range.");BitConverter.GetBytes(value).CopyTo(result,offset);}
        return result;
    }
}
