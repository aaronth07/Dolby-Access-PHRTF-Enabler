# Dolby Access PHRTF Enabler: technical report

The useful result from this investigation is a working way to apply an owned personalized headphone profile to consumer Dolby Atmos for Headphones on Windows. The profile is delivered to Dolby's native initialization property, then spatial processing is restarted. Controlled object-based playback produced repeatable changes in the recorded headphone output, and the original property could be restored exactly.

The investigation used AI-assisted analysis, Reverse Engineer Anything, and Ghidra. Static code traces established where the profile goes; endpoint readbacks and matched recording comparisons established that changing the payload affects playback. The application packages that operation into a device selector, profile selector, verifier, backup button, and personalization switch.

## The profile file

A `.personalized_headphone` file is a JSON document. Its important branches are:

```text
personalized_hrtf
  phrtf_capture_metadata
  virtualizer_parameters
    rosella_coefficients
    rosella_coefficients_version
    room_model
```

The metadata describes the capture, creation date, name, UUID, and software versions. The tuning is stored as signed 32-bit integers. In the inspected decoder path, each integer contributes its low 16 bits to the native coefficient container. These are a compact directional model and room tuning, rather than a list of measured directions that can be read as individual impulse responses.

The tested profile used coefficient version `1.1.0` and contained 15,691 integer entries. Its compact container had room and directional/PCA tables, plus an extended trailer. The magic value was `0x7072`. A low-word XOR checksum used seed `0xA569`; the extended trailer was included for this profile. Checking only the shorter core would reject this particular valid file.

The application checks the metadata branch, version encoding and length, integer types, table framing, expected length, checksum, and native memory bound. The inspected initialization reader bounded coefficient arrays at 32,768 integers and version text at 20 bytes. The application also bounds input file size. A filename extension is only a picker filter; it is the contents that determine acceptance.

## Where Dolby stores and loads the tuning

The successful route uses `IAudioSystemEffectsPropertyStore` on the selected Windows playback endpoint. The Dolby context is:

```text
{45da5c30-2837-4ac4-b1e2-50acc3865974}
```

The headphone initialization property is:

```text
{1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8},1
```

Static analysis identifies it as `PKEY_DAPVR_INITTIME_DAHP`. On the tested endpoint, the User store is persisted beneath:

```text
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\<endpoint GUID>
  \FxProperties\{45da5c30-2837-4ac4-b1e2-50acc3865974}\User
```

The application writes through the Windows effects-property interface and reads the registry representation to verify persistence. The API value is a `VT_BLOB`. Its registry serialization has an eight-byte prefix, `41 00 00 00 01 00 00 00`, ahead of the actual initialization payload. That prefix belongs to the persisted representation and must be kept out of the API payload.

The inspected original initialization payload was 14 bytes, or 22 bytes with the registry envelope. Adding the tested profile produced a 62,791-byte API payload and a 62,799-byte persisted value. Those lengths describe the investigated profile; they are not universal profile sizes.

An alternate property, `{70b95986-9ee3-4b43-acb2-de7aeb1a9509},1`, belongs to the alternate DAFM/Atmos for Monitors branch. Its selection depends on a native presence/status condition. The application rejects endpoints where that alternate branch is present, rather than treating both properties as interchangeable.

## The initialization and coefficient-loading path

The Access serializer and consumer DSP reader agree on a presence-prefixed initialization message. It contains two optional Boolean fields, an optional integer, three optional tuning representations, and a final optional Boolean. The tuning representations are an integer array, an integer array with version bytes, and an encoded blob-list representation with version bytes.

The native decoder gives tuning v3 priority over v2, and v2 priority over v1. The application replaces the tuning with v2 and clears the other two tuning representations so that an older value cannot take precedence. It preserves the unrelated initialization fields from the saved original.

The consumer path traced through `ApplyModuleParams`, `SetParams`, `SetInitTimeParams`, `ConfigureInitTime`, and `DecodeMessageTuning`. It reads the effects blob, deserializes the initialization message, and passes the room and PCA data into the native rendering state. Successful tuning decode requires both room and PCA pointers. A successful setter return alone is insufficient evidence: the native path can fall back after a decode failure. This is why the investigation also compared actual playback output.

The Dolby package registers `Dolby.DolbyAtmosForHeadphonesEncoder` in `DolbyHrtfEnc.dll` and `Dolby.DolbyAudioProcessingModule` in `DolbyAudioProcessing.dll`. Encoder code accepts object type, position, and gain. The native tests submitted dynamic objects through `ISpatialAudioClient` and `ISpatialAudioObjectRenderStream`, with Dolby Atmos for Headphones selected as the Windows Spatial Sound provider.

The inspected installation had Dolby Access `3.27.12250.0` and Atmos for Headphones `2.3.303.0`. The analyzed `DolbyAudioProcessing.dll` had SHA-256 `0fada40251880d03c657a357745d362799c93fb6024f733f1c8bf1057f5e4715`. Selected loading anchors in that exact binary were `ConfigureInitTime` at `0x180123260`, `DecodeMessageTuning` at `0x18011c450`, initialization reader `0x180150220`, bounded reader helper `0x1801509f0`, and native initialization `0x180004d40`. Addresses are specific to that binary build.

## Applying, verifying, and restoring a profile

The application selects an explicit playback endpoint. Before a switch, it checks that Dolby Access is closed, Atmos is active and selected, the normal Dolby property is available, the saved backup belongs to that endpoint, and the current payload still matches the saved or last-applied state. A mutex prevents simultaneous profile operations.

It saves the current API payload, serialized registry value, and hashes of the Dolby User context. It also retains a separate original for Off. Fresh transaction backups do not replace that original. The endpoint ID and original-payload hash are checked whenever the retained backup is loaded.

The switch then briefly disables spatial processing on that endpoint, commits the desired property, verifies API bytes and persisted serialization, checks the other Dolby User values, and enables Atmos again. It rechecks the profile after the restart. If an operation fails, rollback writes the previous payload and verifies restoration. Helper integrity and input checks run before mutation.

The Atmos verifier checks the selected and active spatial subtype, the normal effects property, its persisted bytes, and the application's known applied-profile identity. That is an operational check; audible suitability still depends on the profile and listener. On another driver, a different context or property branch may prevent this application from using the same route.

Backend tests reproduced the reviewed payload byte for byte and rejected damaged framing, invalid version encoding/length, bad checksums, and noninteger coefficients. Live testing completed an Off/On cycle with Atmos active, then deliberately failed after a property write. Rollback restored the previous personalized payload and Atmos. An inactive or unsupported endpoint was rejected before mutation. The original/native comparisons were restored exactly in the earlier bounded playback tests.

## Why the room response changed

The uploaded tuning includes the room model alongside the personalized directional data. Replacing the profile therefore changes more than directional filtering. Native initialization copies both pointers into processing state, and the rendering code consumes both. Table A includes delays, allpass sections, feedback filters, output filters, a mixing matrix, and output taps. That structure can generate a decaying response; describing the entire table as only early reflections would be too narrow.

In a controlled original → personalized → original → personalized sequence, identical object positions and sweeps were recorded twice per state. Other Dolby User values, device defaults, and persisted formats stayed fixed. The median weighted magnitude difference between profiles was about 4.02 dB, while repeat differences within a state were about 0.009 dB.

Across the six cardinal directions, the personalized response had approximately 10.6–12.2 dB less energy in the 20–80 ms window and 6.0–6.3 dB less in the 80–300 ms window. After 300 ms, both reached essentially the same recorded background. That matches the reported shorter, drier sound. These windows include filtering and the measurement response, so they do not isolate a reverb component or establish a precise reverberation time.

Returning to the saved original profile reproducibly returns to its previous response. Keeping the personalized directional data while restoring only the original room response remains an unresolved operation. The native code has an alternate room-pointer path that retains PCA, but the numeric route conditions and their object/channel meanings are not fully mapped.

Dynamic speaker optimization is a separate internal feature from dynamic audio objects. Its disabled setting removes an additional native state, while another room-related state is allocated independently. It therefore cannot be treated as a general reverb-off switch.

The inspected native defaults include a source-distance integer of 103, far/mid/near scale integers of 32568/6698/5090, a near-is-anechoic flag of 1, and three grouped rendering modes of 1. Static analysis also found their requested-to-current field copy and a runtime setter with bounds. Physical units and mode names remain unresolved, and these defaults were not read back as current live overrides. A radius-only object test at one, two, and four metres produced no meaningful level change in the tested front/left responses. Position radius alone is therefore not an established room-control method here.

## Dolby Access's cached profile workflow

The earlier consumer workflow also has a package-local cache and configuration model. Static storage calls resolve the root to `ApplicationData.Current.LocalCacheFolder`, with derived names `phrtf.json` and `phrtf_<UUID>.json`. LocalCache is excluded from Windows' normal application backup/restore behavior, so reconstructing that route would need explicit file backups.

The configuration maps its activation state to `phrtf_active`, settings filename to `settings_file`, and checksum string to `correlation_id`. Optional fields include server response and account/profile identifiers. The activation converter writes a Boolean from Active=1/Inactive=0. UUID serialization uses the dashed `D` format. `HasSyncedProfile` is a separate settings value and should not be substituted for the activation state.

The digest helper uses MD5 over US-ASCII bytes and emits uppercase hexadecimal. The traced ASCII fallback replaces unrepresentable characters with `?`. A global date converter uses invariant culture and UTC conversion, but the capture date properties also carry their own ISO converter attributes. Exact effective date formatting, final member ordering, and a complete accepted cache document were not established. Simply hashing the input file's JSON ordering would be unjustified.

The successful implementation uses the native endpoint property instead of reconstructing that cached upload workflow. It preserves the application's settings hive and cache files. Reopening Dolby Access, changing devices, resetting the application, rebooting, or receiving a driver update may cause the provider to rewrite or reload state; use the verifier after those events. Persistence across the tested spatial-provider restart is established, while reboot and update persistence remain open.

The published material contains source code and this technical summary. Personalized coefficients, capture metadata, account data, registry exports, endpoint identifiers, proprietary Dolby binaries, and private research recordings are excluded.

## Default device and script operation

The default selector follows the Windows multimedia playback endpoint at the start of each operation. Once a transaction starts, it retains that resolved endpoint through backup, writing, verification, and rollback. Named selections continue to target their explicit endpoint.

The PowerShell script uses the same C# parser and property service, compiled in memory by PowerShell. Native MMDevice enumeration and WinRT spatial-state getters are shared with the application. The default script mode asks the user to change the spatial format in Windows settings and verifies each change. Automatic restart is optional and uses the bundled helper. Script and application backups are compatible.

## Configuration and profile changes

The application identifies a personalized coefficient block directly in the device profile. Selecting Default playback device resolves the current Windows multimedia endpoint and uses the same inspection as selecting that endpoint explicitly.

External profile changes prompt for an explicit overwrite or restore. Invalid saved configuration can be replaced after confirmation. If configuration is unreadable during restore, the application offers the earliest transaction backup whose endpoint and API/registry copies agree. Reset restarts Atmos, deletes the selected device's old app state and backups, and saves its current profile as the new Off baseline.
