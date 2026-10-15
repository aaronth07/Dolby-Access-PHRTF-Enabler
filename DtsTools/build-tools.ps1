param([string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$work = Join-Path $project 'release\dts-build'
New-Item -ItemType Directory -Path $work -Force | Out-Null
& $Python -m venv (Join-Path $work 'python')
if ($LASTEXITCODE) { throw 'Python environment creation failed.' }
$runtime = Join-Path $work 'python\Scripts\python.exe'
& $runtime -m pip install -r (Join-Path $PSScriptRoot 'requirements.txt')
if ($LASTEXITCODE) { throw 'Converter dependency installation failed.' }
& $runtime -m PyInstaller --noconfirm --clean --onedir --console --name DtsSofaConverter --distpath (Join-Path $work 'dist') --workpath (Join-Path $work 'build') --specpath (Join-Path $work 'spec') (Join-Path $PSScriptRoot 'convert_sofa_dts.py')
if ($LASTEXITCODE) { throw 'Converter build failed.' }
$output = Join-Path $PSScriptRoot 'bin'
if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($output)) -ne [IO.Path]::GetFullPath($PSScriptRoot)) { throw 'Unexpected converter output directory.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Path $output -Force | Out-Null
Copy-Item (Join-Path $work 'dist\DtsSofaConverter\*') $output -Recurse -Force
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $installation = if (Test-Path $vswhere) { & $vswhere -latest -prerelease -products '*' -property installationPath }
    if ($installation) {
        $version = (Get-Content (Join-Path $installation 'VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
        $compiler = Join-Path $installation "VC\Tools\MSVC\$version"
    } else {
        $compiler = Get-ChildItem (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\*\*\VC\Tools\MSVC\*') -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.FullName 'bin\Hostx64\x64\cl.exe') } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (!$compiler) { throw 'Install the MSVC x64 C++ toolchain.' }
    $sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
    $sdkVersion = Get-ChildItem (Join-Path $sdk 'Include') -Directory | Where-Object Name -match '^10\.' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
    $env:Path = "$compiler\bin\Hostx64\x64;" + $env:Path
    $env:INCLUDE = "$compiler\include;$sdk\Include\$sdkVersion\ucrt;$sdk\Include\$sdkVersion\shared;$sdk\Include\$sdkVersion\um"
    $env:LIB = "$compiler\lib\x64;$sdk\Lib\$sdkVersion\ucrt\x64;$sdk\Lib\$sdkVersion\um\x64"
}
& cl.exe /nologo /std:c++17 /W4 /WX /EHsc /MT /O2 (Join-Path $PSScriptRoot 'validate_dts_native.cpp') "/Fo:$work\validator.obj" "/Fe:$output\validate_dts_native.exe" /link bcrypt.lib
if ($LASTEXITCODE) { throw 'Native DTS validator build failed.' }
Copy-Item (Join-Path $PSScriptRoot 'THIRD_PARTY_NOTICES.md') $output
$pythonBase = & $runtime -c 'import sys; print(sys.base_prefix)'
if (Test-Path (Join-Path $pythonBase 'LICENSE.txt')) { Copy-Item (Join-Path $pythonBase 'LICENSE.txt') (Join-Path $output 'PYTHON_LICENSE.txt') }
