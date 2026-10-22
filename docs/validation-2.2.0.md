# 2.2.0 validation

Built from the preserved beta.1 source in an isolated `release/2.2.0` worktree.

- 60 compatibility/review, 37 feature, 41 EQ, 18 Atmos EQ, and 34 stock-room assertions passed.
- 81 real WinUI checks passed on a separate desktop: four tabs, light/dark themes, 560×720 and 480×420 layouts, room-control visibility, file-picker cancellation, EQ import errors, duplicate import, response drawing, deleting the last profile, and busy-state completion.
- Seven stock tuning candidates passed the installed native Dolby decoder in an owned process, with scratch canaries intact. Default expansion matches the original decoded tuning; Off zeros room output taps; feedback/custom and experimental directional scaling preserve the expected native fields.
- Production assembly version is 2.2.0.0 and excludes private QA code. The retained beta.1 converter runtime is packaged unchanged.
- Application ZIP and portable extraction are checked against the complete hash/size manifest, with cache reuse, locked-cache failure, archive CRC and private-path/identifier scans.

No live endpoint mutation or new physical-headphone listening was performed. Inactive-desktop render-target images omit some native control composition, so they are private diagnostic artifacts. Prior beta.1 renderer measurements remain historical. See [regression requirements](../regression.md) for integration and test commands.
