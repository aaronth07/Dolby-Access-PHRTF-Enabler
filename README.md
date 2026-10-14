# Dolby Access PHRTF Enabler

Enables personalized `.personalized_headphone` profiles in Dolby Atmos for Headphones on Windows. Profiles are applied through Dolby’s native system-effects property and used by Windows Spatial Sound for object-based binaural rendering.

Supports device selection, profile and renderer verification, one click personalization toggle, and backups.

## Installation

Download `DolbyAccessPhrtfEnabler-Portable.exe` from [Releases](../../releases/latest) and run it. It extracts and verifies the bundled application automatically under `%LOCALAPPDATA%\AtmosProfile\cache`, alongside the backup directories. The launcher includes the application and its required runtimes. The folder ZIP remains available for manual extraction.

Requirements:
- Windows x64
- Dolby Atmos for Headphones installed and enabled on the selected playback device
- A personalized headphone profile (`1.1.0` tested; other versions show a warning)

To enable Atmos, open **Windows Settings → System → Sound**, select the playback device, and set **Spatial sound** to **Dolby Atmos for Headphones**. _Note: This will only work with an activated copy of Dolby Atmos for Headphones as Atmos must be unlocked and activated on the selected device to be able to replace the rendering data to necessary for using the personalized profile._
## Usage

1. Close Dolby Access.
2. Select the playback device.
3. Select the `.personalized_headphone` file.
4. Click **Verify Atmos**.
5. Enable **Personalized profile**.

**Default playback device** is the initial selection and follows the current Windows default at the start of each operation. A named selection targets that device directly. For playback applications that use the Windows default device, ensure Atmos is enabled on that device.

Profile verification checks file contents, metadata, coefficient layout, and checksum. Files with other extensions are accepted when their contents validate. **Verify Atmos** checks the selected spatial sound provider and Dolby profile storage.

Disabling **Personalized profile** restores the profile retained before the first import on that device. If a custom profile was already present, that profile becomes the restore point. Atmos remains enabled, with spatial processing briefly restarted during each switch.

### Backups

A transaction backup is created automatically before each switch. **Back up** saves an additional copy of the current state.

Backups are stored in `%LOCALAPPDATA%\AtmosProfile\<device GUID>\`. The original backup is preserved across subsequent imports and manual backups. 

### Room response

Importing a profile may also affect room processing. The tested configuration produced a substantially shorter response tail, indicating that room response, reflections, and reverb may be somewhat or entirely removed by applying this profile. Further research is underway on restoring this behavior. See  [technical report](report.md) for findings on directional coefficients, room tuning, the loading path, and reverberation.

## Build

Install .NET SDK 10 and the Windows SDK, then run from the repository directory:

```powershell
dotnet publish AtmosProfile.csproj -c Release -r win-x64 -p:Platform=x64 -o release/DolbyAccessPhrtfEnabler
```

Run `release/DolbyAccessPhrtfEnabler/DolbyAccessPhrtfEnabler.exe`. The publish target includes the PRI and XBF resources required by WinUI.

The application uses C# and native WinUI 3 controls. Profile parsing and endpoint operations run off the UI thread. Spatial-format switching uses the bundled SoundVolumeView executable.

## Validation

Backend validation covered:
- Exact payload reproduction and extension-independent profile identification
- Warnings for unfamiliar metadata and rejection of malformed coefficients
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

Releases include the complete, unmodified freeware SoundVolumeView distribution and its license in `Tools/readme.txt`. Its terms permit free noncommercial redistribution. Microsoft runtime notices are included with the corresponding files. Dolby Access, Dolby Atmos for Headphones, and personalized profiles must be obtained separately and are not bundled.

This project is independently developed. Dolby and Dolby Access are trademarks of their respective owner.


## PowerShell version

Download the PowerShell source ZIP from Releases, or use the repository source. Use 64-bit PowerShell 7.4 or later. The script shares the application backend and compiles the included C# source in memory.

```powershell
pwsh -File scripts/Set-DolbyPersonalizedProfile.ps1 -Mode List
pwsh -File scripts/Set-DolbyPersonalizedProfile.ps1 -Mode Verify
pwsh -File scripts/Set-DolbyPersonalizedProfile.ps1 -Mode On -ProfilePath "my-profile.personalized_headphone"
pwsh -File scripts/Set-DolbyPersonalizedProfile.ps1 -Mode Off
```

The default device is selected unless you supply `-DeviceId` from the List output. `-Mode Validate -ProfilePath <file>` checks a profile without accessing an endpoint, and `-Mode Backup` saves a backup.

By default, On and Off prompt you to turn spatial sound Off and then re-enable Dolby Atmos for Headphones in Windows settings. Each change is verified before proceeding. This mode runs no downloaded executable. Add `-AutomaticRestart` to use the bundled SoundVolumeView helper for those steps. Both modes retain automatic backups and rollback.

The coefficient checksum is recalculated from the selected profile and compared with that profile's stored checksum. Each valid profile can have its own checksum; it is not compared with one fixed reference profile.

### Build the portable EXE

The launcher is compiled with .NET Native AOT. Install the .NET 10 SDK and MSVC C++ build tools. Download the v1.0.2 Windows ZIP into `release`, then run:

```powershell
pwsh -File PortableLauncher/build-portable.ps1
```

The build script creates the file-integrity manifest and embeds the ZIP. The output is `release/portable/DolbyAccessPhrtfEnabler-Portable.exe`. Cached files are checked before launch; a damaged cache is replaced by a fresh extraction. Backups remain in their existing directories.
