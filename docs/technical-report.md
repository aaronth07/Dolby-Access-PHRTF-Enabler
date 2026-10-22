# Version 2.2.0

Stock room support expands the installed default legacy tuning into the generic initialization field, preserving absence of personalization. Tagged fields determine feedback/output/directional regions; saved original initialization supports exact Restore. Native owned-process decoder tests cover default parity and all room modes. See [regression requirements](../regression.md).

# Local 2.1.0 beta update

Native Atmos EQ, combined room gain correction, DTS Spacious overdrive, and delay-aligned SOFA conversion are described with their measured results and limits in [the implementation status](native-renderer-eq-status.md). System Sonic replacement remains unverified and is excluded from the runnable candidate.

# Technical report

The application applies owned personalized headphone data through the selected Windows endpoint's native effects property store. Native object-rendering comparisons establish that these profiles affect recorded output. The app checks persisted configuration and exact transaction readback; those checks do not measure personal suitability.

## Dolby profile loading

A `.personalized_headphone` file contains integer coefficients and a version in `personalized_hrtf.virtualizer_parameters`. The tested 1.1.0 layout has 15,691 signed integer entries. Native decoding consumes their low 16 bits. Framing, integer types, array bounds, version encoding, and a low-word XOR checksum seeded with `0xA569` are validated. Extensions do not determine acceptance.

The Dolby effects context is `{45da5c30-2837-4ac4-b1e2-50acc3865974}`; the headphone initialization property is `{1b4dab55-b1fb-4d8c-8317-f2d4a96efbb8},1`. These are shared property identifiers, not device identifiers. The API blob differs from its persisted registry representation by an eight-byte envelope. The application writes through `IAudioSystemEffectsPropertyStore`, verifies both representations, restarts spatial processing on that endpoint, and verifies again. It preserves unrelated initialization fields and clears tuning representations that could take precedence over the selected versioned integer array.

Before mutation, automatic transaction backups retain the payload, persisted state, and effects-context snapshot. A separate original restore point survives subsequent imports. External changes trigger a warning; failed operations attempt and verify exact rollback. An explicit enabled flag distinguishes On from Off when the applied and baseline bytes are identical.

## Dolby room control

The room processor is separate from the directional/PCA coefficient region. Verified controls edit output taps at coefficient indices 1578-1833, feedback values at 1050-1561, and the room-path control at 15689, then recompute the checksum. Other coefficients remain protected by a byte comparison.

On selects the native room path. Off zeroes room output taps. Custom output level scales signed Q15 taps with nearest-even rounding; feedback scales its separate recursive coefficients. Output level is capped at 150%, and any signed overflow is rejected before writing. The 1.5-times candidate passed the research native decoder with unchanged directional/feedback/matrix hashes, and this release's live property test applies and reads it back while preserving PHRTF. A dedicated acoustic comparison of this gain setting has not been performed.

The predicted output-tap gain at 150% is `20 log10(1.5) = 3.522 dB`. It is not a calibrated vendor percentage. Two-times tap gain overflows the tested profile. A -6 dB direct / +6 dB overall implementation requires a verified overall gain control, which remains unmapped. Feedback is not doubled as a substitute.

Applying PHRTF defaults room processing On, or carries the prior app preference into the new profile. Room writes check for a known baseline replacing PHRTF and reapply the combined profile. A different listener's directional coefficients cannot be silently replaced. Reset restores saved room fields onto the current listener and establishes a new Off baseline.

## DTS SOFA and room processing

DTS uses the effects context `{08e54ab7-ff00-4b50-92bb-63d6dc1ef8ea}` with separate runtime SPAC, device RDEV, and transaction-counter properties. The converter reads the selected endpoint template, maps a complete binaural SOFA field to the native directions using common time-domain interpolation weights, resamples to 48 kHz, and retains the template origin response and panner. Sample delays are applied without independent ear alignment or normalization.

The SPAC FIR table is followed by two stereo auxiliary vector sets. Native analysis identifies the first set as sends to the recursive room processor. Its earlier interpretation as a dry/GPEQ return was incorrect. Conversion originally zeroed both sets, muting native room input. The correction copies only the first set from a compatible room template beside the personalized FIRs; the unresolved second set is preserved unchanged during room edits.

Balanced and Spacious map to the installed SD1 and SD2 Windows headphone assets. The mapping is established through shared app preset objects, localized resource keys, and filename construction. The named presets share the tested 23-node panner and recursive room parameters while their sends differ. Whole stock carriers are never imported over personalized FIRs.

The app reads installed preset assets locally. It verifies Sound Unbound app SHA-256 `374fd16ed693cf79fc63e4c401e97a9a4e17f462c986e2579bab2eedbf6ddac5`, extracts the decoding material from that installed binary, decodes the installed asset and checks its hash. No binary asset or decoding key is shipped. Compatibility checks cover sample rate, table mode, node count, recursive parameters, and panner. The final calibration word stays with the personalized carrier. An unavailable mapping offers an override that retains the original sends.

Original copies the saved pre-change room sends; Off zeroes only those sends. Changes reapply the full runtime carrier, current device configuration, and counter in one guarded transaction. The explicit personalization state prevents a stock room-only edit from being labeled a personalized SOFA profile.

## DTS headphone EQ

RDEV v2 embeds RGPQ v2 device correction data. Processing delays and biquad/FIR filter counts are at bytes 32-55. SOFA apply zeroes those fields while preserving other device bytes; the native RDEV validator accepts the result. An active-filter warning is shown when supported device data indicates enabled correction. This operates on endpoint filters; it does not synchronize the vendor app's UI checkbox or select a named vendor EQ profile. Restore returns the saved device bytes exactly.

Native format validation calls only the hash-pinned installed DTS encoder (`90e62a7f6d044b43e53413f1f0933647725838f60904de37cd4b49f3852a604c`), with SPAC validator RVA `0xbec8` and RDEV validator RVA `0xc2c4`. An unfamiliar version requires an override and skips private native calls. Structural checks and transaction readback remain active.

## Measurement evidence

The included [graphs](index.html#measurements) are derived from matched dynamic Windows spatial-object captures through virtual-endpoint recording. They show mean binaural power, 1/6-octave smoothing, matched axes, 2 ms energy blocks, and no gain normalization. The position is 30 degrees left, 0 degrees elevation, 1 m. The earlier Off/On DTS graph used muted personalized room sends and is labeled accordingly.

The room-preservation experiment retained identical personalized FIR bytes. Restoring sends changed the first 5 ms energy by -0.000540 to +0.000027 dB and increased 20-250 ms energy by 14.251-15.227 dB across the tested directions. The second Balanced/Spacious experiment changed first 5 ms energy by -0.001701 to +0.000061 dB and increased 20-250 ms energy by 9.819-9.866 dB with Spacious. These windows include the measurement response and do not establish a calibrated reverberation time or perceptual preference.

Both source experiments record exact saved runtime/counter restoration, equal configuration snapshots, unchanged global defaults, and no cleanup errors. The release tests independently exercise application readback and rollback. [Graph provenance](graph-provenance.json) records source hashes. Private capture arrays, raw recordings, profile coefficients, endpoint identifiers, and registry dumps are excluded.

## Scope

Tested Dolby coefficient version: 1.1.0. Tested DTS preset mapping: Sound Unbound 2026.3.31.0 with the compatible Windows headphone grid. Tests cover one Windows system; other drivers and future vendor versions may differ. Stored endpoint settings survive application exit, but vendor profile changes, device recreation, or audio reinitialization may replace them. Reapply when that occurs. No background monitor runs after exit.

## Dolby room/direct modes in 2.0

200% and 400% scale both directional fields (indices 4500-15587) by 0.5 and 0.25 using signed nearest-even rounding. Room taps, feedback, matrices, phase and distance controls remain at their saved values. The checksum is recomputed. Makeup gain remains 0 dB. The full-gain reference is retained across repeated Apply, mode switching, and profile imports. On, Restore, and reset undo the attenuation. Native readback and UI transitions passed; the user verified both experimental modes. No new acoustic measurement is bundled.
