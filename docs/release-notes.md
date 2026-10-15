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
