# Version 2.2.0 development

This release is based on the preserved 2.1.0-beta.1 source and is isolated on `release/2.2.0`. [Regression requirements](../regression.md) describe the stock-room and UI fixes, test commands and integration boundaries.

The release build must use `-p:UiQaBuild=false`. The private `UiQaBuild=true` executable requires `--ui-qa <output directory>`; `Tests/RunUiQa.ps1` launches it on a separate desktop without switching focus. It isolates both profile data and saved EQ and never invokes audio-setting actions. QA code is absent from production builds.

# Build and test

The application is an unpackaged, self-contained WinUI 3 Windows x64 app. Use .NET SDK 10, the Windows SDK, and MSVC x64 tools. Python is needed only to rebuild the converter or plots. The distributed converter is standalone.

```powershell
dotnet publish AtmosProfile.csproj -c Release -r win-x64 -p:Platform=x64 -o release/DolbyAccessPhrtfEnabler
```

The publish target includes WinUI PRI/XBF resources. Do not overwrite a running build; publish to a new staging directory or close that instance first.

To rebuild the SOFA converter and native validator:

```powershell
./DtsTools/build-tools.ps1
```

The script creates a project-local Python environment and installs pinned dependencies. The native validator resolves only the supported hash-pinned installed encoder. Runtime assets are copied from DtsTools/bin into the publish output.

Run backend checks:

```powershell
dotnet run --project Tests/Tests.csproj -- --review
dotnet run --project Tests/Tests.csproj -- --features
dotnet run --project Tests/Tests.csproj -- --eq
dotnet run --project Tests/Tests.csproj -- --atmos-eq
dotnet run --project Tests/Tests.csproj -- <profile> <original-payload> <expected-payload>
```

The review, features and EQ commands require no private fixtures. For live feature tests, provide the repo root, SOFA path, endpoint ID, and profile path:

```powershell
dotnet run --project Tests/Tests.csproj -- --features <repo-root> <sofa> <endpoint-id> --live --profile <profile>
```

Live tests change the selected endpoint temporarily. They save the starting property contexts and provider, use isolated app state for reset tests, and restore the exact starting values in finally blocks. Close vendor apps and avoid concurrent changes to that endpoint. Private fixture paths are arguments; fixtures and captured state are not part of the source archive.

For native Atmos transaction tests, use `--atmos-eq-probe <endpoint-id> <repo-root> <new-output-folder> - <initialization-payload>`; the `-` skips recordings. A reviewed private capture engine path enables recordings and requires its matching grid. All outputs are private.

For native DTS EQ transaction tests, use `--eq-native <endpoint-id> <repo-root> <new-output-folder>`. This temporarily changes the endpoint and restores its starting DTS context/provider. A fifth argument accepts the private matched spatial-object capture engine; `--spacious` adds a room-tail capture condition. Keep all endpoint writes sequential. See [native renderer and EQ status](native-renderer-eq-status.md) and `SonicPrototype/README.md` in the source archive for the tested scope and outstanding system integration.

Tests/UiIntegration.ps1 accepts an owned app process ID, endpoint ID, profile path, and SOFA path. It closes only that test process before restoring native state and app metadata. It uses WinApp CLI and UI Automation. Its evidence remains under release, excluded from public source.

To prepare the portable launcher, ZIP the publish output under DolbyAccessPhrtfEnabler/, then run PortableLauncher/build-portable.ps1 -PayloadZip <zip>. The build generates a size/SHA-256 manifest. Keep only the reviewed source, docs, redistributable runtime components, and app assets in the candidate. Omit personal profiles, raw captures, backups, registry exports, vendor DLLs/presets, and diagnostic logs.

## Local site and GitHub Pages

The site is static HTML/CSS with local images. Preview it from the repository root:

```powershell
python -m http.server 8768 --bind 127.0.0.1 --directory docs
```

Open http://127.0.0.1:8768. The docs folder contains .nojekyll and uses relative links, so it can be served from a repository subpath. GitHub Pages can serve the docs folder from the default branch.

## Graphs

The Off/On images were supplied by the existing matched-capture analysis. The room comparison is reproducible with docs/plot_room_comparison.py and its explicit input paths, rate, direction index, and label. It requires NumPy and Matplotlib. Raw recordings and response arrays remain private; docs/graph-provenance.json records source hashes and processing without embedding them.
