using AtmosProfile;
using System.Text.Json;
using System.Text.Json.Nodes;

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
Reject("Bad checksum", n => n["personalized_hrtf"]!["virtualizer_parameters"]!["rosella_coefficients"]![1] = 0);
Reject("Bad magic", n => n["personalized_hrtf"]!["virtualizer_parameters"]!["rosella_coefficients"]![0] = 0);
Reject("Wrong version", n => n["personalized_hrtf"]!["virtualizer_parameters"]!["rosella_coefficients_version"] = "9.0.0");
Reject("Truncated coefficients", n => n["personalized_hrtf"]!["virtualizer_parameters"]!["rosella_coefficients"]!.AsArray().RemoveAt(10));
Reject("Missing metadata", n => n["personalized_hrtf"]!.AsObject().Remove("phrtf_capture_metadata"));
Reject("Noninteger coefficient", n => n["personalized_hrtf"]!["virtualizer_parameters"]!["rosella_coefficients"]![10] = 1.5);
foreach (var bytes in new[] { original[..^1], original.Concat(new byte[] { 0 }).ToArray(), Array.Empty<byte>() }) { bool rejected = false; try { profile.ApplyTo(bytes); } catch { rejected = true; } Assert(rejected, "Reject malformed endpoint framing " + bytes.Length); }
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
File.WriteAllText(Path.Combine(temp, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(results));
