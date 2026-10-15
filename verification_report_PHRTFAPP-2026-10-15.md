# PHRTF App Verification Report — 2026-10-15

## Scope

Tested the local `v1.1.1` PHRTF application and its existing automated test modes. No source files were changed, no configuration was reset, and no changes were published. Tests that modify room or DTS endpoint state ran through the supplied integration harness, which snapshots and restores endpoint state. Hardware Apply/Enable/Restore/Reset actions were not triggered directly from the UI.

## Results

- **Automated review suite — passed:** 31 assertions covering DTS template overrides, unknown encoder native-offset safety, counter/device/runtime changes, room-state preservation, reset preservation of room/DTS data, reset failure rollback, and backup-corruption overrides.
- **Feature suite — passed:** 18 assertions covering Dolby room On/Off/50% feedback/Restore, directional preservation, checksum/truncation rejection, unfamiliar room-version override, and malformed SPAC rejection.
- **Native integration suite — passed:** 44 assertions covering native room-profile readback for On/Off/50%/Restore, personalized coefficient preservation, forced rollback, PHRTF apply preservation of room settings, DTS custom-profile write, injected DTS failure rollback, exact runtime/counter/device restoration, and Dolby/DTS context preservation. The captured initial and final `Spatial` state matched; DTS remained active and default, and room/DTS context matched after restore.
- **PHRTF regression suite — passed:** 24 assertions using the supplied personalized profile and regression inputs.
- **PHRTF UI — passed for tested paths:** A valid personalized profile loaded, displayed its metadata and “Profile verified.”, and enabled Apply. A nonexistent profile displayed “The profile could not be verified.” and left Apply disabled. The valid profile was restored afterward. The profile picker opened and cancelled without selecting a file.
- **Atmos gating — observed:** Atmos was inactive while the personalized-profile toggle was On. Apply exposed the warning to enable Dolby Atmos in Windows sound settings, with an “Allow anyways” option. Neither override nor apply was activated.
- **SOFA conversion — passed for supplied input:** The 710-direction, 48,000 Hz SOFA converted. The expanded technical details identified 22 directional DTS nodes, 4,096 samples per ear, common time-domain interpolation weights, 0 source-sample added common delay, and successful native format validation with the installed DTS encoder.
- **Invalid SOFA — rejected with a specific reason:** A text file named `.sofa` failed conversion. The Error details panel reported NetCDF error `-51`, “Unknown file format.” No diagnosis or remediation was attempted.
- **DTS passive status — passed:** “Check status” reported “DTS profile structure and property readback checked.” The status text distinguished DTS activity from device-profile loading (“DTS Headphone:X is not active. Device profile loaded.”).
- **DTS picker — opened and cancelled:** No file was selected.

## Coverage limits

This run covered the existing test suite and the listed UI paths, but it did not enumerate every possible combination of all UI states and external device/driver configurations. Direct UI activation of Apply, Enable DTS, Restore, Reset, and “Allow anyways” was intentionally avoided because those operations affect the live Windows audio configuration. No actual playback or listening test was performed. Results therefore establish the stated assertions and UI behavior only; they do not establish audio output or listening quality.

## Artifacts

Automated integration output is in `release/review-2026-10-15/native-feature-results.jsonl`. The UI captures and the invalid SOFA test input are in `release/review-2026-10-15/`.
