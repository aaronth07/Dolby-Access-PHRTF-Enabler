# Dolby Access PHRTF Enabler

I was able to get a personalized `.personalized_headphone` profile working with Dolby Atmos for Headphones on Windows. This application applies the profile through Dolby's native system-effects property, so Windows Spatial Sound can use it for object-based headphone rendering.

The interface is deliberately simple: select a playback device, select your profile, verify Atmos, and switch personalization on or off. A backup is created automatically before each switch.

## Download and use

Download the ZIP from [Releases](../../releases/latest), extract the complete folder, and run `DolbyAccessPhrtfEnabler.exe`. Keep the extracted files together. The release includes the .NET and Windows App SDK runtimes.

You need Windows x64, Dolby Atmos for Headphones installed and enabled on the selected device, and your own personalized headphone profile. Coefficient version `1.1.0` is supported.

To enable Atmos, open **Windows Settings → System → Sound**, select the output device, and choose **Dolby Atmos for Headphones** under **Spatial sound**. Complete any required setup or activation in Dolby Access, then close it before using the enabler.

1. Close Dolby Access.
2. Select the playback device that you want to use. This does not change the Windows default device.
3. Select your `.personalized_headphone` file. The picker also allows other file extensions; the application checks the contents, metadata, coefficient layout, and checksum.
4. Click **Verify Atmos** to check the selected provider and Dolby profile storage.
5. Switch **Personalized profile** on.

**Off** restores the profile saved before the application's first import on that device. Atmos stays enabled. If the device already had a custom profile when that backup was created, Off returns to that profile. The application briefly restarts spatial processing when switching.

**Back up** saves an additional copy of the current state. The retained original and transaction backups are stored under `%LOCALAPPDATA%\AtmosProfile\<device GUID>`. Later imports and manual backups preserve the retained original. Keep that folder if you want to retain the original across application reinstalls.

The profile can also change the room response. In the tested setup, applying it produced a noticeably shorter response tail. The [technical report](report.md) explains what was found about the directional coefficients, room tuning, loading path, and reverb.

## Build

Install .NET SDK 10 and the Windows SDK, then run from the repository folder:

```powershell
dotnet publish AtmosProfile.csproj -c Release -r win-x64 -p:Platform=x64 -o release/DolbyAccessPhrtfEnabler
```

Run `release/DolbyAccessPhrtfEnabler/DolbyAccessPhrtfEnabler.exe`. The publish target includes the application's PRI and XBF resources required by WinUI.

The application uses C# and native WinUI 3 controls. Profile parsing and endpoint operations run off the UI thread. Spatial-format switching uses the bundled SoundVolumeView helper; its executable is checked against a pinned hash before use.

## Testing

Backend testing covered exact payload reproduction, extension-independent file identification, malformed metadata and coefficients, real device enumeration, backup readback, an actual Off/On cycle, and rollback after an intentionally injected post-write failure. The final tested device ended with its initial personalized profile and Atmos restored.

The test program accepts private fixtures supplied locally:

```powershell
dotnet run --project Tests/Tests.csproj -- <profile-file> <original-api-payload> <expected-personalized-api-payload>
```

An optional endpoint ID and `--live` add device testing. Live testing requires an existing verified original backup, an initially personalized device with Atmos active, Dolby Access closed, and exclusive use of that audio device during the test. It switches Off, On, and deliberately exercises rollback. Private profiles and payload fixtures are excluded from this repository.

The published app launches successfully. Frontend interaction testing remains with the user. The native import was tested on one Windows system; other device and driver combinations may use different Dolby property branches.

## Distribution

The source in this repository is dedicated to this application. The release contains the complete, unmodified freeware SoundVolumeView distribution, with its license in `Tools/readme.txt`. Its terms allow free noncommercial redistribution. Microsoft runtime notices remain with their distributed files. Dolby Access and personalized profiles are supplied separately by the user.

This is an independent project. Dolby and Dolby Access are trademarks of their respective owner.
