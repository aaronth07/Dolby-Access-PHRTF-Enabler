using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace AtmosProfile;

public sealed record ProfileFile(string Name, string Version, string Room, int[] Coefficients)
{
    public string? Warning { get; init; }
    public static ProfileFile Load(string path)
    {
        if (new FileInfo(path).Length > 4_000_000) throw new InvalidDataException("Profile exceeds the supported file size.");
        var text = File.ReadAllText(path, Encoding.UTF8);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement.GetProperty("personalized_hrtf");
        bool hasMetadata = root.TryGetProperty("phrtf_capture_metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object;
        var warnings = new List<string>();
        if (!hasMetadata) warnings.Add("Capture metadata is missing or unfamiliar.");
        var parameters = root.GetProperty("virtualizer_parameters");
        var version = parameters.GetProperty("rosella_coefficients_version").GetString();
        if (string.IsNullOrWhiteSpace(version) || version.Length > 20 || version.Any(c => c > 127)) throw new InvalidDataException("Coefficient version must contain 1-20 ASCII bytes.");
        if (version != "1.1.0") warnings.Add($"Coefficient version {version} has not been tested.");
        var values = parameters.GetProperty("rosella_coefficients").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        if (values.Length < 14 || values.Length > 32768) throw new InvalidDataException("Invalid coefficient count.");
        var w = values.Select(v => (int)(ushort)v).ToArray();
        if (w[0] != 0x7072 || w[2] != 1 || w[3] != 1 || w[4] != 0) throw new InvalidDataException("Unsupported Dolby room/PCA table layout.");
        long count = 13L + 79 + 2L * (w[6] + 14L * w[5]) + 171L * w[7] + w[10] + 380L * w[12] + w[11] + 79 + 11;
        if (count != values.Length) throw new InvalidDataException("Coefficient framing or length is invalid.");
        int checksum = 0xa569;
        for (int i = 2; i < values.Length; i++) checksum ^= w[i];
        if (checksum != w[1]) throw new InvalidDataException("Coefficient checksum is invalid.");
        long memory = w[7] * 708L + w[6] * 8L + 0x2525 + w[12] * (2L * 0x280 + 0x120) + 0x39f + (w[10] + w[11]) * 4L;
        if (memory > 0x21380) throw new InvalidDataException("Profile exceeds the native decoder's memory bound.");
        string? room = parameters.TryGetProperty("room_model", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        string? name = hasMetadata && metadata.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(room)) warnings.Add("Room metadata is missing or unfamiliar.");
        if (hasMetadata && string.IsNullOrWhiteSpace(name)) warnings.Add("Profile name is missing or unfamiliar.");
        return new(name ?? Path.GetFileName(path), version, room ?? "Unspecified room", values) { Warning = warnings.Count == 0 ? null : string.Join(" ", warnings) };
    }

    public static bool HasPersonalization(byte[] payload)
    {
        using var reader = new BinaryReader(new MemoryStream(payload));
        byte Flag() { byte v = reader.ReadByte(); if (v > 1) throw new InvalidDataException("Invalid initialization flag."); return v; }
        for (int i = 0; i < 2; i++) if (Flag() != 0) _ = Flag();
        if (Flag() != 0) _ = reader.ReadUInt32();
        if (Flag() != 0) { uint count = reader.ReadUInt32(); if (count > 32768 || reader.ReadBytes((int)count * 4).Length != count * 4) throw new InvalidDataException("Invalid tuning size."); }
        return Flag() != 0;
    }

    public byte[] ApplyTo(byte[] original)
    {
        using var input = new BinaryReader(new MemoryStream(original));
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        byte Flag() { var b = input.ReadByte(); if (b > 1) throw new InvalidDataException("Invalid initialization flag."); return b; }
        void CopyBool() { byte p = Flag(); writer.Write(p); if (p != 0) writer.Write(Flag()); }
        void SkipArray() { uint n = input.ReadUInt32(); if (n > 32768) throw new InvalidDataException("Invalid tuning size."); if (input.ReadBytes(checked((int)n * 4)).Length != n * 4) throw new EndOfStreamException(); }
        void SkipVersion() { uint n = input.ReadUInt32(); if (n > 20 || input.ReadBytes((int)n).Length != n) throw new InvalidDataException("Invalid version size."); }
        CopyBool(); CopyBool(); byte integer = Flag(); writer.Write(integer); if (integer != 0) writer.Write(input.ReadUInt32());
        if (Flag() != 0) SkipArray(); writer.Write((byte)0);
        if (Flag() != 0) { SkipArray(); SkipVersion(); }
        writer.Write((byte)1); writer.Write((uint)Coefficients.Length); foreach (var v in Coefficients) writer.Write(v);
        var version = Encoding.ASCII.GetBytes(Version); writer.Write((uint)version.Length); writer.Write(version);
        if (Flag() != 0) { uint n = input.ReadUInt32(), v = input.ReadUInt32(); if (n > 65536 || v > 20 || input.ReadBytes((int)(n + v)).Length != n + v) throw new InvalidDataException("Invalid alternate tuning."); }
        writer.Write((byte)0); CopyBool();
        if (input.BaseStream.Position != input.BaseStream.Length) throw new InvalidDataException("Unsupported initialization trailing data.");
        return output.ToArray();
    }
}
