using AtmosProfile;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length > 0 && args[0] == "--legacy-room") { LegacyRoomTests.Run(args); return; }
if (args.Length > 0 && args[0] == "--eq-native") { EqNativeTests.Run(args); return; }
if (args.Length > 0 && args[0] == "--atmos-eq-probe") { AtmosEqProbe.Run(args); return; }
if (args.Length > 0 && args[0] == "--eq") { EqTests.Run(); return; }
if (args.Length > 0 && args[0] == "--atmos-eq") { AtmosEqTests.Run(); return; }
if (args.Length > 0 && args[0] == "--override-live") { OverrideTests.Run(args); return; }
if (args.Length > 0 && args[0] == "--features") { await FeatureTests.Run(args); return; }
if (args.Length > 0 && args[0] == "--review") { ReviewTests.Run(); return; }

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: Tests <profile> <original-api-payload> <expected-personalized-api-payload> [endpoint-id] [--live]");
    return;
}
string profilePath = Path.GetFullPath(args[0]);
string originalPath = Path.GetFullPath(args[1]);
string expectedPath = Path.GetFullPath(args[2]);
string? endpointId = args.Length > 3 && !args[3].StartsWith("--") ? args[3] : null;
var results = new List<object>();
void Assert(bool condition, string name) { if (!condition) throw new Exception(name); results.Add(new { test = name, passed = true }); }
var profile = ProfileFile.Load(profilePath);
var original = File.ReadAllBytes(originalPath);
var candidate = File.ReadAllBytes(expectedPath);
Assert(profile.ApplyTo(original).SequenceEqual(candidate), "Exact existing verified payload reproduction");
Assert(profile.ApplyTo(candidate).SequenceEqual(candidate), "Existing personalization replaces without trailing data");
var temp = Path.Combine(AppContext.BaseDirectory, "artifacts"); Directory.CreateDirectory(temp);
string anyFile = Path.Combine(temp, "valid-profile.bin"); File.Copy(profilePath, anyFile, true);
Assert(ProfileFile.Load(anyFile).ApplyTo(original).SequenceEqual(candidate), "File detection independent of extension");
void Reject(string name, Action<JsonObject> edit)
{
    var node = JsonNode.Parse(File.ReadAllText(profilePath))!.AsObject(); edit(node); string path = Path.Combine(temp, name + ".json"); File.WriteAllText(path, node.ToJsonString());
    bool rejected = false; try { _ = ProfileFile.Load(path); } catch { rejected = true; } Assert(rejected, name);
}
void Warn(string name, Action<JsonObject> edit)
{
    var node = JsonNode.Parse(File.ReadAllText(profilePath))!.AsObject(); edit(node); string path = Path.Combine(temp, name + ".json"); File.WriteAllText(path, node.ToJsonString());
    var loaded = ProfileFile.Load(path); Assert(loaded.Warning != null && loaded.Coefficients.SequenceEqual(profile.Coefficients), name);
    Assert(loaded.ApplyTo(original).Length > original.Length, name + " serializes successfully");
}
Reject("Bad checksum", n => n["personalized_hrtf"]!["virtualizer_parameters"]!["rosella_coefficients"]![1] = 0);
Reject("Bad magic", n => n["personalized_hrtf"]!["virtualizer_parameters"]!["rosella_coefficients"]![0] = 0);
Warn("Unfamiliar version", n => n["personalized_hrtf"]!["virtualizer_parameters"]!["rosella_coefficients_version"] = "9.0.0");
Reject("Truncated coefficients", n => n["personalized_hrtf"]!["virtualizer_parameters"]!["rosella_coefficients"]!.AsArray().RemoveAt(10));
Warn("Missing metadata", n => n["personalized_hrtf"]!.AsObject().Remove("phrtf_capture_metadata"));
Warn("Missing room metadata", n => n["personalized_hrtf"]!["virtualizer_parameters"]!.AsObject().Remove("room_model"));
Reject("Oversized version", n => n["personalized_hrtf"]!["virtualizer_parameters"]!["rosella_coefficients_version"] = new string('1', 21));
Reject("Missing coefficient version", n => n["personalized_hrtf"]!["virtualizer_parameters"]!.AsObject().Remove("rosella_coefficients_version"));
Reject("Noninteger coefficient", n => n["personalized_hrtf"]!["virtualizer_parameters"]!["rosella_coefficients"]![10] = 1.5);
foreach (var bytes in new[] { original[..^1], original.Concat(new byte[] { 0 }).ToArray(), Array.Empty<byte>() }) { bool rejected = false; try { profile.ApplyTo(bytes); } catch { rejected = true; } Assert(rejected, "Reject malformed endpoint framing " + bytes.Length); }
var changed = JsonNode.Parse(File.ReadAllText(profilePath))!.AsObject();
var array = changed["personalized_hrtf"]!["virtualizer_parameters"]!["rosella_coefficients"]!.AsArray();
array[100] = array[100]!.GetValue<int>() ^ 1;
int checksum = 0xa569; for (int i = 2; i < array.Count; i++) checksum ^= (ushort)array[i]!.GetValue<int>();
array[1] = checksum; string changedPath = Path.Combine(temp, "different-valid-checksum.json"); File.WriteAllText(changedPath, changed.ToJsonString());
Assert(ProfileFile.Load(changedPath).Coefficients[1] != profile.Coefficients[1], "A different profile checksum is accepted when correctly calculated");
Assert(ProfileService.Resolve(ProfileService.DefaultDevice).Id == AudioPlatform.Default().Id, "Default selection resolves current multimedia endpoint");
var devices = await ProfileService.Devices(); Assert(devices.Count > 0, "Playback device enumeration");
if (endpointId != null)
{
var selected = devices.Single(d => d.Id == endpointId);
var before = ProfileService.Inspect(selected); Assert(before.Atmos, "Actual Atmos provider active"); Assert(before.Personalized && before.CanRestore, "Existing personalized state has a verified saved original");
if (args.Contains("--live"))
{
    Assert(System.Diagnostics.Process.GetProcessesByName("DolbyAccess").Length == 0, "Dolby Access closed");
    string backup = ProfileService.Backup(selected); Assert(File.Exists(Path.Combine(backup, "profile_api.bin")), "Readback-verified real backup");
    try
    {
        ProfileService.Switch(selected, null, false); var off = ProfileService.Inspect(selected); Assert(off.Atmos && !off.Personalized, "Live personalization OFF with Atmos active");
        ProfileService.Switch(selected, profile, true); var on = ProfileService.Inspect(selected); Assert(on.Atmos && on.Personalized, "Live personalization ON with Atmos active");
        var method = typeof(ProfileService).GetMethod("SetSpatial", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Action<bool> manual = enabled => method.Invoke(null, new object[] { selected, enabled });
        ProfileService.Switch(selected, null, false, manual); Assert(!ProfileService.Inspect(selected).Personalized, "Manual restart callback OFF path");
        ProfileService.Switch(selected, profile, true, manual); Assert(ProfileService.Inspect(selected).Personalized, "Manual restart callback ON path");
        ProfileService.AfterWriteTest = () => throw new IOException("Intentional post-write failure");
        bool failed = false;
        try { ProfileService.Switch(selected, null, false); } catch (IOException e) when (e.Message == "Intentional post-write failure") { failed = true; }
        finally { ProfileService.AfterWriteTest = null; }
        Assert(failed, "Injected post-write failure reached rollback");
        var restored = ProfileService.Inspect(selected); Assert(restored.Atmos && restored.Personalized, "Rollback restored personalized payload and Atmos");
    }
    finally { if (before.Personalized && !ProfileService.Inspect(selected).Personalized) ProfileService.Switch(selected, profile, true); }
}
}
Assert(!ProfileFile.HasPersonalization(original), "Original payload has no personalized coefficient block");
Assert(ProfileFile.HasPersonalization(candidate), "Personalized coefficient presence detected independently of saved hash");
Assert(ProfileService.Inspect(ProfileService.DefaultDevice) == ProfileService.Inspect(ProfileService.Resolve(ProfileService.DefaultDevice)), "Default and explicit endpoint report identical state");
if (args.Contains("--recovery") && endpointId != null)
{
    var selected = devices.Single(d => d.Id == endpointId);
    string realRoot = ProfileService.DataRoot;
    string isolated = Path.Combine(temp, "recovery-" + Guid.NewGuid().ToString("N"));
    string guid = Guid.Parse(selected.Id[(selected.Id.LastIndexOf('.') + 1)..]).ToString("B");
    string realState = Path.Combine(realRoot, guid, "original.json");
    var saved = File.ReadAllBytes(realState);
    Directory.CreateDirectory(Path.Combine(isolated, guid));
    string statePath = Path.Combine(isolated, guid, "original.json");
    File.WriteAllBytes(statePath, saved);
    ProfileService.TestDataRoot = isolated;
    try {
        var state = JsonSerializer.Deserialize<BackupRecord>(File.ReadAllText(statePath))!;
        state.AppliedHash = "external-change"; File.WriteAllText(statePath, JsonSerializer.Serialize(state));
        Assert(ProfileService.Inspect(selected).Personalized, "External personalization remains detected");
        bool warned = false; try { ProfileService.Switch(selected, null, false); } catch (ProfileWarningException) { warned = true; }
        Assert(warned, "External changes require explicit override");
        ProfileService.Switch(selected, null, false, force: true);
        Assert(!ProfileService.Inspect(selected).Personalized, "Forced restore after external change");
        ProfileService.Switch(selected, profile, true);
        File.WriteAllText(statePath, "broken JSON");
        Assert(ProfileService.Inspect(selected).Personalized, "Invalid configuration does not block inspection");
        warned = false; try { ProfileService.Switch(selected, profile, true); } catch (ProfileWarningException) { warned = true; }
        Assert(warned, "Invalid configuration requires explicit override");
        ProfileService.Switch(selected, profile, true, force: true);
        Assert(File.ReadAllText(statePath).Contains("OriginalHash"), "Forced apply repairs invalid configuration");
        File.WriteAllText(statePath, "broken JSON");
        warned = false; try { ProfileService.Switch(selected, null, false); } catch (ProfileWarningException) { warned = true; }
        Assert(warned, "Invalid state restore offers verified backup override");
        ProfileService.Switch(selected, null, false, force: true);
        Assert(ProfileService.Inspect(selected).Atmos, "Forced restore from verified transaction backup");
        ProfileService.Switch(selected, profile, true, force: true);
        string backup = ProfileService.Reset(selected);
        Assert(File.Exists(Path.Combine(backup, "profile_api.bin")) && ProfileService.Inspect(selected).Atmos, "Reset restarts Atmos and saves fresh backup");
        Assert(Directory.GetDirectories(Path.Combine(isolated, guid, "backups")).Length == 1, "Reset deletes old endpoint backups");
    } finally {
        ProfileService.TestDataRoot = null;
        if (!ProfileService.Inspect(selected).Personalized) ProfileService.Switch(selected, profile, true, force: true);
        Assert(File.ReadAllBytes(realState).SequenceEqual(saved), "Real application configuration preserved by isolated tests");
    }
}
File.WriteAllText(Path.Combine(temp, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(results));
