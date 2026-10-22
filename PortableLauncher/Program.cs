using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr window, string text, string title, uint type);
    sealed record FileRecord(string Hash, long Size);
    static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    static string SafePath(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid cached file path.");
        return path;
    }
    static bool Valid(string folder, Dictionary<string, FileRecord> manifest)
    {
        try
        {
            foreach (var entry in manifest)
            {
                string path = SafePath(folder, entry.Key);
                if (!File.Exists(path) || new FileInfo(path).Length != entry.Value.Size) return false;
                using var stream = File.OpenRead(path); if (Hash(stream) != entry.Value.Hash) return false;
            }
            // Extra files in the payload root could change DLL resolution.
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count() == manifest.Count;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    static int Main(string[] args)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var payload = assembly.GetManifestResourceStream("payload.zip") ?? throw new InvalidDataException("Application payload missing.");
            string identity = Hash(payload); payload.Position = 0;
            using var reader = new StreamReader(assembly.GetManifestResourceStream("payload-manifest.txt")!);
            var manifest = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
            while (reader.ReadLine() is string line) { var fields = line.Split('\t'); if (fields.Length != 3) throw new InvalidDataException("Invalid payload manifest."); manifest.Add(fields[2], new(fields[0], long.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture))); }
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AtmosProfile", "cache");
            Directory.CreateDirectory(root);
            using var mutex = new Mutex(false, "Local\\DolbyAccessPhrtfEnablerCache_" + identity);
            bool acquired; try { acquired = mutex.WaitOne(TimeSpan.FromMinutes(2)); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("Another application extraction has not finished.");
            string folder;
            try
            {
                string pointer = Path.Combine(root, identity + ".active");
                string current = File.Exists(pointer) ? File.ReadAllText(pointer).Trim() : identity;
                if (current != Path.GetFileName(current) || !current.StartsWith(identity, StringComparison.Ordinal)) current = identity;
                folder = SafePath(root, current);
                if (!Valid(folder, manifest))
                {
                    folder = SafePath(root, identity + "_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
                    using var archive = new ZipArchive(payload, ZipArchiveMode.Read, true);
                    const string prefix = "DolbyAccessPhrtfEnabler/";
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith('/')) continue;
                        if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected payload root.");
                        string relative = entry.FullName[prefix.Length..];
                        if (!manifest.ContainsKey(relative)) throw new InvalidDataException("Unexpected payload file.");
                        string path = SafePath(folder, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        using var input = entry.Open(); using var output = new FileStream(path, FileMode.CreateNew); input.CopyTo(output);
                    }
                    if (!Valid(folder, manifest)) throw new InvalidDataException("Extracted application integrity check failed.");
                    string temporary = pointer + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(temporary, Path.GetFileName(folder), new UTF8Encoding(false)); File.Move(temporary, pointer, true);
                }
            }
            finally { mutex.ReleaseMutex(); }
            if (args.Contains("--extract-only")) { Console.WriteLine(folder); return 0; }
            var start = new ProcessStartInfo(Path.Combine(folder, "DolbyAccessPhrtfEnabler.exe")) { WorkingDirectory = folder, UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var child = Process.Start(start) ?? throw new IOException("Could not launch the application.");
            return 0;
        }
        catch (Exception error) { if(args.Contains("--extract-only"))Console.Error.WriteLine(error.Message);else MessageBoxW(IntPtr.Zero, error.Message, "Dolby Access PHRTF Enabler", 0x10); return 1; }
    }
}
