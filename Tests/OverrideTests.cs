using AtmosProfile;
using System.Text.Json;
internal static class OverrideTests
{
    public static void Run(string[] args)
    {
        var d = AudioPlatform.Devices().Single(d => d.Id == args[1]);
        var profile = ProfileFile.Load(args[2]);
        var output = Path.Combine(args[3], "release", "override-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var before = ProfileService.ReadInitializationForTest(d);
        var context = SpatialFiles.Snapshot(d, ProfileService.Context);
        var spatial = AudioPlatform.Spatial(d);
        var passed = new List<string>();
        void Assert(bool value, string name) { if (!value) throw new Exception(name); passed.Add(name); }
        void Reject(Action action, string name) { try { action(); } catch (ProfileWarningException) { passed.Add(name); return; } throw new Exception(name); }
        ProfileService.ToolRoot = DtsProfileService.ToolRoot = args[3];
        File.WriteAllText(Path.Combine(output, "before.json"), JsonSerializer.Serialize(new { before, context, spatial }));
        ProfileService.TestDataRoot = SpatialFiles.TestRoot = Path.Combine(output, "state");
        ProfileService.PersonalizationTransform = DolbyRoomService.PreserveSetting;
        try {
            DtsProfileService.SelectProvider(d, AudioPlatform.Dolby, "Atmos");
            ProfileService.WriteInitializationForTest(d, []);
            Assert(ProfileService.ReadInitializationForTest(d).Length == 0, "Missing native initialization fixture is absent");
            var status = ProfileService.Inspect(d);
            Assert(status.Warning != null && !status.Personalized, "Missing native initialization returns actionable status");
            Reject(() => ProfileService.Switch(d, profile, true), "Missing native initialization offers force before any profile write");
            Assert(ProfileService.ReadInitializationForTest(d).Length == 0, "Declining force leaves absent property unchanged");
            ProfileService.Switch(d, profile, true, force: true);
            Assert(ProfileService.Inspect(d).Personalized, "Force initializes absent native property and remains On");
            ProfileService.Switch(d, profile, false, force: true);
            Assert(ProfileService.ReadInitializationForTest(d).Length == 0, "Off restores the original absent native property");
            Assert(!ProfileService.Inspect(d).Personalized, "Status remains Off after restoring absent property");
            ProfileService.AfterWriteTest = () => throw new IOException("Injected initialization failure");
            try { ProfileService.Switch(d, profile, true, force: true); throw new Exception("Failure hook missed"); }
            catch (IOException) { passed.Add("Initialization write failure propagated after rollback"); }
            finally { ProfileService.AfterWriteTest = null; }
            Assert(ProfileService.ReadInitializationForTest(d).Length == 0, "Failed initialization restores absent property exactly");
            ProfileService.Reset(d, true);
            Assert(ProfileService.Inspect(d).Warning == null, "Force reset initializes missing native configuration");
        } finally {
            ProfileService.AfterWriteTest = null;
            ProfileService.WriteInitializationForTest(d, before);
            DtsProfileService.SelectProvider(d, spatial.DefaultSpatialAudioFormat, "original provider");
            Assert(ProfileService.ReadInitializationForTest(d).SequenceEqual(before), "Native override tests restore starting property bytes exactly");
            SpatialFiles.VerifyUnchanged(context, SpatialFiles.Snapshot(d, ProfileService.Context));
            Assert(AudioPlatform.Spatial(d) == spatial, "Native override tests restore starting provider exactly");
            ProfileService.TestDataRoot = SpatialFiles.TestRoot = null;
            ProfileService.PersonalizationTransform = null;
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(passed));
        }
        Console.WriteLine(JsonSerializer.Serialize(passed));
    }
}
