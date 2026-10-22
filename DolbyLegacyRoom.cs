using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;

namespace AtmosProfile;

internal static class DolbyLegacyRoom
{
    internal sealed record Table(Dictionary<int, (int Start, int Count)> Fields);
    internal static bool IsLegacy(int[] values) => values.Length >= 2 && (ushort)values[0] == 0x8042;
    internal static Table Parse(int[] values)
    {
        if (!IsLegacy(values) || values.Length > 32768) throw new ProfileOperationException("This built-in Dolby tuning format is not supported.");
        int checksum = 0xa569; foreach (int value in values.Skip(2)) checksum ^= (ushort)value;
        if (checksum != (ushort)values[1]) throw new ProfileOperationException("The built-in Dolby tuning checksum is invalid.");
        var fields = new Dictionary<int, (int Start, int Count)>();
        for (int i = 0; i < values.Length;) {
            int tag = (ushort)values[i++], id = tag & 0x3fff, type = tag >> 14;
            if (id > 74 || id == 66 && i != 1) throw new ProfileOperationException("This built-in Dolby tuning format is not supported.");
            if (type == 3 && i == values.Length) throw new ProfileOperationException("The built-in Dolby tuning is truncated.");
            int count = type == 2 ? 1 : type == 3 && i < values.Length ? (ushort)values[i++] : 0;
            if (i + count > values.Length) throw new ProfileOperationException("The built-in Dolby tuning is truncated.");
            if (id is 3 or 19 or 21 or 34 or 35 && fields.ContainsKey(id)) throw new ProfileOperationException("Multiple built-in Dolby room fields are present.");
            fields[id] = (i, count); i += count;
        }
        if (!fields.TryGetValue(3, out var bands) || bands.Count != 1 || values[bands.Start] is < 1 or > 77 ||
            !fields.TryGetValue(19, out var feedback) || feedback.Count != values[bands.Start] * 8 ||
            !fields.TryGetValue(21, out var taps) || taps.Count != values[bands.Start] * 4 ||
            !fields.TryGetValue(34, out var left) || !fields.TryGetValue(35, out var right) || left.Count < 1 || left.Count != right.Count)
            throw new ProfileOperationException("This built-in Dolby room layout is not supported.");
        return new(fields);
    }
    internal static int[] Change(int[] original, string mode, double level, double feedback)
    {
        var table = Parse(original); var values = original.ToArray();
        if (mode != "Custom") { level = mode == "Off" ? 0 : 100; feedback = mode == "HalfFeedback" ? 50 : 100; }
        if (!double.IsFinite(level) || level is < 0 or > 150 || !double.IsFinite(feedback) || feedback is < 0 or > 100)
            throw new ProfileOperationException("Room output level must be 0-150% and feedback 0-100%.");
        void Scale(int id, double factor) {
            var field = table.Fields[id];
            for (int i = field.Start; i < field.Start + field.Count; i++) {
                double value = Math.Round((short)values[i] * factor, MidpointRounding.ToEven);
                if (value is < short.MinValue or > short.MaxValue) throw new ProfileOperationException("This room level exceeds the profile's native coefficient range. Reduce the room level.");
                values[i] = (int)value;
            }
        }
        if (level != 100) Scale(21, level / 100);
        if (feedback != 100) Scale(19, feedback / 100);
        if (mode is "DoubleRoom" or "QuadRoom") { double gain = mode == "DoubleRoom" ? .5 : .25; Scale(34, gain); Scale(35, gain); }
        Checksum(values); _ = Parse(values); return values;
    }
    internal static void Checksum(int[] values) { int sum = 0xa569; foreach (int value in values.Skip(2)) sum ^= (ushort)value; values[1] = sum; }
    internal static byte[] WithDefaultTuning(byte[] original)
    {
        if (!DolbyRoomService.UsesBuiltInTuning(original)) throw new ProfileOperationException("Dolby is not using implicit built-in tuning.");
        byte[] resource = ReadDefaultResource();
        return Insert(original, DecodeResource(resource));
    }
    internal static int[] DecodeResource(byte[] resource)
    {
        if (resource.Length is < 5 or > 1_000_000) throw new ProfileOperationException("The built-in Dolby tuning resource is invalid.");
        string text = Encoding.ASCII.GetString(resource).TrimEnd('\0');
        if (!text.StartsWith("{\"", StringComparison.Ordinal) || !text.EndsWith("\"}", StringComparison.Ordinal)) throw new ProfileOperationException("This built-in Dolby tuning resource format is not supported.");
        byte[] packed;
        try { packed = Convert.FromBase64String(text[2..^2]); }
        catch (FormatException) { throw new ProfileOperationException("This built-in Dolby tuning resource format is not supported."); }
        if (packed.Length % 2 != 0 || packed.Length > 65536) throw new ProfileOperationException("The built-in Dolby tuning resource is invalid.");
        var values = Enumerable.Range(0, packed.Length / 2).Select(i => (int)BitConverter.ToUInt16(packed, i * 2)).ToArray();
        _ = Parse(values); return values;
    }
    internal static byte[] Insert(byte[] original, int[] values)
    {
        _ = Parse(values);
        if (!DolbyRoomService.UsesBuiltInTuning(original)) throw new ProfileOperationException("Dolby is not using implicit built-in tuning.");
        using var r = new BinaryReader(new MemoryStream(original));
        for (int i = 0; i < 2; i++) if (r.ReadByte() != 0) _ = r.ReadByte();
        if (r.ReadByte() != 0) _ = r.ReadUInt32();
        int start = (int)r.BaseStream.Position;
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(original[..start]); w.Write((byte)1); w.Write((uint)values.Length);
        foreach (int value in values) w.Write(value);
        w.Write(original[(start + 1)..]); return stream.ToArray();
    }
    static byte[] ReadDefaultResource()
    {
        using var packages = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        if (packages != null) foreach (string name in packages.GetSubKeyNames().Where(n => n.StartsWith("DolbyLaboratories.DolbyAccess_", StringComparison.OrdinalIgnoreCase)).OrderByDescending(n => Version.TryParse(n.Split('_').ElementAtOrDefault(1), out var version) ? version : new Version())) {
            using var key = packages.OpenSubKey(name);
            if (key?.GetValue("PackageRootFolder") is not string folder) continue;
            string file = Path.Combine(folder, "DLLs", "x64", "DolbyAudioProcessing.dll"); if (!File.Exists(file)) continue;
            IntPtr module = LoadLibraryEx(file, IntPtr.Zero, 0x22); // Data and image resources only; DLL code is not executed.
            if (module == IntPtr.Zero) continue;
            try {
                IntPtr resource = FindResource(module, (IntPtr)254, (IntPtr)255); uint size = resource == IntPtr.Zero ? 0 : SizeofResource(module, resource);
                if (size is < 5 or > 1_000_000) continue;
                IntPtr pointer = LockResource(LoadResource(module, resource)); if (pointer == IntPtr.Zero) continue;
                var bytes = new byte[size]; Marshal.Copy(pointer, bytes, 0, bytes.Length);
                try { _ = DecodeResource(bytes); return bytes; } catch (ProfileOperationException) { continue; }
            } finally { FreeLibrary(module); }
        }
        throw new ProfileOperationException("The installed Dolby Access built-in headphone tuning could not be read. Install Dolby Access before adjusting its built-in room.");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryExW")] static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindResourceW")] static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
}
