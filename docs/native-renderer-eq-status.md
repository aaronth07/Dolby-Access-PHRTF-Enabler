# Native renderer and EQ status

The measurements below were recorded for local 2.1.0-beta.1 on 21 October 2026 and are retained in 2.2.0. Version 2.2.0 additionally verifies stock-room candidate decoding and isolated WinUI interactions; it does not add new endpoint measurements. See [release notes](release-notes.md) and [regression requirements](../regression.md).

| Path | Current behavior | Validation limit |
| --- | --- | --- |
| Dolby Atmos | Uploaded EQ through native 20-band magnitude equalization; optional +6/+12 dB room gain correction composed with EQ | Magnitude approximation, not an arbitrary biquad or all-pass upload |
| DTS Headphone:X | Native parametric EQ; actual Balanced/Spacious presets; Spacious 200%/400%; time-aligned or linear SOFA conversion | Tested encoder and compatible 23-node headphone grid |
| Sonic, owned process | Full SOFA FIR replacement plus EQ inside the real HrtfApo custom DSP factory; native Sonic plus EQ also tested | Component behavior only |
| Sonic, other applications | Inert host-admission prototype | System endpoint FX registration is denied by the current process token; no admitted system renderer or decoded-object launch-out route |

## Native measurements

Only a virtual test endpoint was changed. Every native test saved the starting configuration, used isolated app metadata, and verified exact restoration of runtime and initialization bytes, transaction counters, remaining property context, and active/default provider. Physical headphone endpoints and Equalizer APO's master configuration were unchanged. No GUI was opened.

Two repeated Windows spatial-object recordings per setting, 30 degrees left, 0 degrees elevation, 1 m, 48 kHz:

| Setting | Direct energy, 0–5 ms | Late energy, 20–150 ms |
| --- | --- | --- |
| Atmos EQ preamp -6 dB, relative to flat EQ | -5.9999 dB | -5.9522 dB |
| Atmos 200% with +6 dB correction, relative to room On | -0.0356 dB | +5.9646 dB |
| Atmos 400% with +12 dB correction, relative to room On | -0.0559 dB | +11.9553 dB |
| DTS Spacious 200%, relative to Spacious | +0.0002 dB | +5.9984 dB |
| DTS Spacious 400%, relative to Spacious | +0.0002 dB | +12.0134 dB |

Atmos's -6 dB preamp measured -6.0003 dB median across 50 Hz–16 kHz with 1/6-octave power smoothing. A separate -6 dB, 1 kHz, Q 1 EQ capture verifies native frequency-dependent uptake. No gain normalization was applied. Timing correction uses one common stereo lag per repetition, preserving ear timing. The published plots use mean left/right power, 1/6-octave frequency smoothing, and 2 ms energy blocks after the earliest direct peak. These are renderer measurements, not listening or personal-fit results.

![Atmos EQ and room correction](images/native-atmos-eq-room.png)

![DTS native room overdrive](images/native-dts-overdrive.png)

## EQ and transactions

The Headphone EQ tab saves named Equalizer APO Preamp/Filter text profiles and previews the response including preamp. DTS encodes supported normalized biquad cascades directly in native RDEV/RGPQ. A valid empty native device payload is 56 bytes; correcting the previous 64-byte minimum resolves the repeated unsupported-EQ error. A force operation that cannot apply an EQ-only request now fails explicitly, while a partial SOFA update reports a warning.

Atmos runtime equalization uses a separate native property from PHRTF/room initialization. Twenty adaptive frequency/gain targets approximate the uploaded magnitude response at 1/16 dB resolution. Preamp and room correction are included in every target; the native postgain field does not affect the tested spatial-object route and is preserved. Approximation error over 0.75 dB requests explicit confirmation. Native target range is -36 to +36 dB; all-pass phase filters are unsupported by this path. Other runtime fields and trailing bytes remain intact.

Room changes, EQ changes, PHRTF reapplication, restore, and reset preserve the independent selected settings. Native tests cover combined room/EQ writes, injected failures, exact rollback, absent properties, explicit backup overrides, and reset scope. Saved profiles survive device resets. The GUI and its new controls build but were not launched under the no-focus-change constraint.

## SOFA interpolation

Time-aligned interpolation aligns neighboring direct arrivals using a 65-tap Kaiser-windowed sinc fractional-delay filter, then restores the weighted per-ear arrival time. Both ears retain shared barycentric directional weights; full response tails and one common causal padding offset are preserved. Ambiguous arrival spreads over 2 ms fall back to linear. Linear is available explicitly and retains byte parity with the previous converter.

Nine existing converter tests and synthetic timing, exact-node, silent-response, tail, and fallback checks passed. The full 710-direction reference converted to 22 directional nodes plus the origin carrier, 4,162 samples/ear at 48 kHz, with no fallback. The half-sample delay filter's maximum magnitude deviation below 16 kHz was 0.000103 dB. These tests establish numerical behavior, not improved perceived clarity. This is delay-aligned interpolation, not the full magnitude-corrected method described by [Arend et al.](https://arxiv.org/abs/2303.09966) or [SUpDEq](https://github.com/AudioGroupCologne/SUpDEq).

## Sonic system boundary

The owned-process full-SOFA+EQ test matched independent convolution plus SOS filtering within 1.20e-8; native Sonic plus EQ matched within 1.17e-10. The component convolver processes full FIR tails during explicit zero input and made no heap allocations during measured callbacks.

The current token cannot open the test endpoint's FX registration key for writing. No UAC prompt or registration change was attempted. Actual audio-host admission, initialization before Sonic caches its DSP, endpoint scope, engine recreation, source reuse, and all-null input handling remain unverified. WASAPI stereo loopback does not supply other applications' decoded spatial objects. The app therefore exposes no misleading Sonic enable or launch-out control. Prototype binaries, vendor libraries, raw recordings, endpoint identifiers, and private fixtures are excluded from this candidate.
