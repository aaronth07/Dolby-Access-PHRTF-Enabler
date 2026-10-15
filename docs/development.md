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
dotnet run --project Tests/Tests.csproj -- <profile> <original-payload> <expected-payload>
```

The first two commands require no private fixtures. For live feature tests, provide the repo root, SOFA path, endpoint ID, and profile path:

```powershell
dotnet run --project Tests/Tests.csproj -- --features <repo-root> <sofa> <endpoint-id> --live --profile <profile>
```

Live tests change the selected endpoint temporarily. They save the starting property contexts and provider, use isolated app state for reset tests, and restore the exact starting values in finally blocks. Close vendor apps and avoid concurrent changes to that endpoint. Private fixture paths are arguments; fixtures and captured state are not part of the source archive.

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
