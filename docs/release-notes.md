# Version 2.2.0

Dolby room controls now work with the built-in headphone tuning, without requiring a personalized profile. This release starts from the preserved 2.1.0-beta.1 source and keeps Sonic research separate.

- Support stock legacy and explicit generic Dolby tuning for On, Off, 50% feedback, custom output/feedback, and experimental 200%/400% room/direct modes.
- Read default tuning from installed Dolby Access resources without executing the vendor DLL. Preserve personalization absence, unrelated tuning fields, checksums, and exact original initialization for Restore.
- Retain saved default tuning so restoration does not depend on reading a newly installed package version.
- Make the window resizable and clamp initial/minimum dimensions to the display work area; wrap page titles and keep each page scrollable.
- Disable EQ browse/import controls during operations and add stable automation identifiers.
- Preserve beta.1 native Atmos 20-band EQ, DTS biquads, saved EQ profiles, time-aligned SOFA conversion, native DTS overdrive room presets, and optional Dolby gain correction.

Validation uses real WinUI controls on an isolated desktop, light/dark themes at two window sizes, pure compatibility/EQ/room tests, and the installed Dolby decoder in an owned process. No live endpoint settings are changed by this release validation. Inactive-desktop image capture omits parts of native control composition, so screenshots are private diagnostics rather than new marketing screenshots. Auditory confirmation on physical headphones remains a user test.

## Changes since the previously published 2.0

The release includes the stock-room fix and UI changes above, plus native Atmos/DTS uploaded parametric EQ workflows, saved EQ profiles and response preview, time-aligned SOFA interpolation, the 56-byte empty-DTS-EQ compatibility fix, DTS Spacious 200%/400%, and optional +6/+12 dB Dolby room gain correction. Full earlier notes follow.

# Local 2.1.0-beta.1

Adds native Atmos EQ, fixes DTS EQ application, and extends native room and SOFA conversion controls. This candidate is local and unpublished.

- Apply uploaded headphone EQ to native Atmos using an adaptive 20-band magnitude approximation, composed with Dolby room gain correction and preserved through PHRTF/room changes.
- Accept valid 56-byte empty DTS device EQ payloads; prevent skipped EQ-only changes from reporting success.
- Add Time-aligned SOFA interpolation with preserved ITD, full tails, exact-node responses, common padding, ambiguous-arrival fallback, and the previous Linear option.
- Add native Spacious 200%/400% DTS room sends while preserving the FIR bank, feedback, panner, and uploaded EQ.
- Add optional +6/+12 dB Dolby gain correction; include clipping guidance in the experimental confirmation and remove correction on On, Restore, or Dolby reset.
- Preserve explicit compatibility overrides and invalid backup records; test combined writes, exact rollback, independent EQ, and reset behavior.
- Include native measured response/decay plots and refreshed local documentation. Sonic remains a component prototype because system-host installation and object interception are unverified and endpoint registration is denied by the current token.

[Measurements and validation limits](native-renderer-eq-status.md). New WinUI resources compile; the GUI was not opened under the no-focus-change constraint.

# Version 2.0

Version 2.0 adds native DTS room presets while preserving personalized filters, improves Dolby room output control, and simplifies configuration status and persistence guidance.

- Add experimental Dolby 200% and 400% room/direct modes, with saved-reference scaling, repeated-Apply protection, and full-gain restoration.
- Add Original, Balanced, Spacious, and Off DTS room presets. Extract compatible sends from the installed, hash-verified DTS package and combine them with the current personalized FIRs.
- Restore native DTS room sends by default on SOFA apply. Room selection reuses the complete profile transaction and preserves the second auxiliary vectors, panner, room parameters, and calibration word.
- Disable detected DTS headphone-correction filters when applying a SOFA; show a warning when active. Remove the separate EQ dropdown.
- Raise Dolby custom room output level to 150% where signed coefficient headroom permits it. Feedback remains a separate 0-100% control. Preserve room preferences and verify PHRTF after room changes.
- Correct reset-label encoding and use one persistence footer across the app.
- Retain explicit version/configuration overrides, automatic backups, separate resets, and restoration checks. Unknown DTS preset mappings retain Original sends under override.
- Refresh the user guide, technical report, reproducible graph provenance, three recorded-response plots, app screenshots, and GitHub Pages site.

## Validation and limits

The release has been built and run locally. Native tests cover conversion, preset extraction, exact room edits, personalized FIR preservation, EQ disabling, reset scope, counter handling, injected failures, and restoration. UI tests exercise PHRTF switching, custom Dolby room level, SOFA conversion, every DTS room selection, removal of EQ controls, reset label, and shared footer. Final counts and artifact hashes are included with the release.

The included measurements come from the supplied, restoration-verified research captures. The earlier DTS Off/On plot used room sends Off; the new Balanced/Spacious plot retains identical personalized FIRs. No new acoustic capture is claimed for the release's 150% Dolby setting. 200% and 400% room/direct modes attenuate directional fields by 6.02 and 12.04 dB with 0 dB makeup gain; the user verified the experimental modes. Native testing covers one Windows system; future vendor versions or different grids may need an override or be unsupported.

## Full changelog since 1.0.3

- Harden profile parsing and version overrides, accept valid profile contents independent of extension, and expose specific SOFA conversion errors.
- Fix portable conversion packaging and pin native encoder calls to the known installed binary.
- Verify runtime properties, persisted bytes, and transaction counters; restore exact pre-operation state on guarded failures.
- Make the PHRTF toggle apply/restore directly, including identical baseline/applied payloads, with automatic backups.
- Add Dolby room controls and independent Dolby/DTS resets, preserving the current listener's directional data.
- Add the room, EQ, status, documentation, and release assets listed above.
