# 2.2.0 regression requirements

Base: preserved 2.1.0-beta.1 source archive, SHA-256 `0fb342ac21d651bc40d227f3301a2d2c8257d5a2a4a18ba44b1efd62146601b5`. This release is developed on `release/2.2.0`, independently of Sonic research. Compare or cherry-pick the fixes; do not replace ongoing research files wholesale with this branch.

## Dolby room without personalization

`DolbyRoomService.cs` and `DolbyLegacyRoom.cs` must support three cases: implicit built-in tuning, explicit generic tuning, and the existing personalized `rp` table. An active Atmos device does not imply that a personalized table exists.

- For implicit tuning, read the installed Dolby Access default headphone resource as data only. Validate its tagged fields and checksum. Write the expanded table to the generic initialization field, with personalization still absent.
- Legacy fields 19 and 21 contain feedback and room output taps. Fields 34/35 contain directional data. Derive their positions and sizes from tags; do not use the personalized table's fixed offsets or assume 64 bands. The verified stock resource has 77 bands.
- On, Off, HalfFeedback, Custom, DoubleRoom and QuadRoom must work from the stock baseline. Off zeros room taps; normal room changes preserve directional data. Experimental modes attenuate directional fields once from a saved full-gain baseline.
- Store both the exact original implicit initialization and a hash-checked expanded default template. Restore returns the exact original bytes, and saved restoration must not depend on the current Dolby Access resource.
- Preserve all flags and fields outside the permitted coefficient lanes. Implicit initialization has no coefficient offsets: its preservation check requires exact byte equality.
- Keep version/configuration warnings and **Allow anyway** behavior. An override may bypass compatibility warnings; it must not bypass malformed framing, invalid checksums, or coefficient overflow.
- Keep uploaded Atmos EQ composed with room gain correction. PHRTF Off must not be mistaken for a corrupt profile or be silently turned On by a room edit.

Run `dotnet run --project Tests/Tests.csproj -c Release -- --legacy-room`. An optional final argument exports candidates using the installed Dolby Access resource for private native-decoder verification. It does not change audio settings. Also run `--review`, `--features`, `--eq`, and `--atmos-eq`.

## UI behavior

- `FeatureWindow.cs`: permit resizing/maximizing, fit initial dimensions to the nearest display's work area, and cap minimum tracking dimensions to that work area. Do not restore fixed width or an unconditional 720-pixel minimum height.
- Page titles wrap, each page scrolls, and controls remain reachable at 480×420 and 560×720 in light and dark themes.
- `HeadphoneEqPage`: disable Browse and Import while an operation runs, along with the other input controls. Keep `EqBrowse`, `EqImport`, and `EqDelete` automation identifiers.
- Read and write source/documentation explicitly as UTF-8; preserve ellipses, middle dots and dash characters in labels without double encoding.
- Empty EQ state disables Apply/Delete; import errors leave the UI usable; repeated imports have distinct names; deleting the final profile restores the empty state.
- Preserve both SOFA interpolation options, all room selections, experimental confirmation/gain correction, generic force controls, independent Dolby/DTS reset scope, and automatic backups.

Build private UI tests with `dotnet publish AtmosProfile.csproj -c Release -p:Platform=x64 -p:UiQaBuild=true -o release/ui-qa-app`, then use `Tests/RunUiQa.ps1`. It launches on a separate desktop without switching to it. QA uses isolated app-data/EQ folders and does not invoke audio-setting actions. Production builds use `UiQaBuild=false`; QA code is compiled out.

## Validation boundary

Real WinUI interaction/layout tests and native decoding of stock candidates passed locally. Native decoder checks cover default parity, zero decoded room taps for Off, feedback/output scaling, preserved directional data, experimental directional scaling, and scratch-buffer canaries. These checks do not establish new physical-headphone listening or live endpoint behavior. Prior beta.1 measurements remain historical evidence. Inactive-desktop bitmap capture omits some native control composition and is not a visual-signoff screenshot.

No Sonic renderer, worker, registration or system-installation change belongs to this release. Keep those ongoing changes separate when integrating these fixes.
