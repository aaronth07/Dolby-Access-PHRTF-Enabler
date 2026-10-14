# Dolby Access PHRTF Enabler

Enables personalized `.personalized_headphone` profiles in Dolby Atmos for Headphones on Windows. Profiles are applied through Dolby’s native system-effects property and used by Windows Spatial Sound for object-based binaural rendering.

Supports device selection, profile and renderer verification, one click personalization toggle, and backups.

## Installation

Download the ZIP from [Releases](../../releases/latest), extract the complete folder, and run `DolbyAccessPhrtfEnabler.exe`. Keep the extracted files together. The release includes the required .NET and Windows App SDK runtimes.

Requirements:
- Windows x64
- Dolby Atmos for Headphones installed and enabled on the selected playback device
- A personalized headphone profile using coefficient version `1.1.0`

To enable Atmos, open **Windows Settings → System → Sound**, select the playback device, and set **Spatial sound** to **Dolby Atmos for Headphones**. Complete any required activation in Dolby Access, then close Dolby Access before importing a profile.

## Usage

1. Close Dolby Access.
2. Select the playback device.
3. Select the `.personalized_headphone` file.
4. Click **Verify Atmos**.
5. Enable **Personalized profile**.

Device selection is independent of the Windows default device. Profile validation checks file contents, metadata, coefficient layout, and checksum regardless of the file extension. **Verify Atmos** checks the selected spatial sound provider and Dolby profile storage.

Disabling **Personalized profile** restores the profile retained before the first import on that device. If a custom profile was already present, that profile becomes the restore point. Atmos remains enabled, with spatial processing briefly restarted during each switch.

### Backups

A transaction backup is created automatically before each switch. **Back up** saves an additional copy of the current state.

Backups are stored in `%LOCALAPPDATA%\AtmosProfile\<device GUID>\`. The original restore point is preserved across subsequent imports and manual backups. Retain this folder when reinstalling the application to preserve that restore point.

### Room response

Importing a profile may also affect room processing. The tested configuration produced a substantially shorter response tail, indicating that room response, reflections, and reverb may be somewhat or entirely removed by applying this profile. Further research is underway on restoring this behavior. See  [technical report](report.md) for findings on directional coefficients, room tuning, the loading path, and reverberation.

## Build

Install .NET SDK 10 and the Windows SDK, then run from the repository directory:

```powershell
dotnet publish AtmosProfile.csproj -c Release -r win-x64 -p:Platform=x64 -o release/DolbyAccessPhrtfEnabler
```

Run `release/DolbyAccessPhrtfEnabler/DolbyAccessPhrtfEnabler.exe`. The publish target includes the PRI and XBF resources required by WinUI.

The application uses C# and native WinUI 3 controls. Profile parsing and endpoint operations run off the UI thread. Spatial-format switching uses the bundled SoundVolumeView executable, verified against a pinned hash before execution.

## Validation

Backend validation covered:
- Exact payload reproduction and extension-independent profile identification
- Rejection of malformed metadata and coefficients
- Device enumeration and backup readback
- Live personalization Off/On switching
- Rollback following an injected post-write failure

The tested device’s initial personalized profile and Atmos state were restored at the end of testing.

Run the test program with locally supplied fixtures:

```powershell
dotnet run --project Tests/Tests.csproj -- <profile-file> <original-api-payload> <expected-personalized-api-payload>
```

An optional endpoint ID and `--live` enable device testing. Live tests require a verified original backup, an initially personalized device with Atmos active, Dolby Access closed, and exclusive use of the selected audio device. The sequence switches personalization Off and On, then tests rollback after an injected failure.

Private profiles and payload fixtures are excluded from the repository.

The published application has passed launch testing. Frontend interaction testing remains pending. Native profile import has been tested on one Windows system; other device and driver combinations may use different Dolby property branches.

## Distribution

Releases include the complete, unmodified freeware SoundVolumeView distribution and its license in `Tools/readme.txt`. Its terms permit free noncommercial redistribution. Microsoft runtime notices are included with the corresponding files. Dolby Access and personalized profiles must be obtained separately.

This project is independently developed. Dolby and Dolby Access are trademarks of their respective owner.

