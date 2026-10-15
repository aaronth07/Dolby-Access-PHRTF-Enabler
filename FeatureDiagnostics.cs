namespace AtmosProfile;
internal static class FeatureDiagnostics
{
    public static void Save(string feature, Exception error)
    {
        try { Directory.CreateDirectory(ProfileService.DataRoot); File.WriteAllText(Path.Combine(ProfileService.DataRoot, feature + "-error.log"), error.ToString()); } catch { }
    }
}
